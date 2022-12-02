﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Linq;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json.Bson;
using System.Buffers.Binary;
using System.Net.Http;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Security.Cryptography;

namespace LesbianDB
{
	public interface IDatabaseEngine{
		public Task<IReadOnlyDictionary<string, string>> Execute(IEnumerable<string> reads, IReadOnlyDictionary<string, string> conditions, IReadOnlyDictionary<string, string> writes);
	}

	/// <summary>
	/// A high-performance LesbianDB storage engine
	/// </summary>
	public sealed class YuriDatabaseEngine : IDatabaseEngine
	{
		/// <summary>
		/// Restores a binlog stream into the given IAsyncDictionary
		/// </summary>
		public static async Task RestoreBinlog(Stream binlog, IAsyncDictionary asyncDictionary){
			byte[] buffer = null;
			try{
				buffer = Misc.arrayPool.Rent(256);
				Dictionary<string, string> delta = new Dictionary<string, string>();
				JsonSerializer jsonSerializer = new JsonSerializer();
				while(true){
					int read = await binlog.ReadAsync(buffer, 0, 4);
					if (read != 4)
					{
						binlog.SetLength(binlog.Seek(-read, SeekOrigin.Current));
						return;
					}
					int len = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(0, 4));
					if (buffer.Length < len)
					{
						try
						{
							
						}
						finally
						{
							Misc.arrayPool.Return(buffer);
							buffer = Misc.arrayPool.Rent(len);
						}
					}
					read = await binlog.ReadAsync(buffer, 0, len);
					if (read != len)
					{
						binlog.SetLength(binlog.Seek(-4 - read, SeekOrigin.Current));
						return;
					}
					using (Stream str = new DeflateStream(new MemoryStream(buffer, 0, len, false, false), CompressionMode.Decompress, false)){
						BsonDataReader bsonDataReader = new BsonDataReader(str);
						GC.SuppressFinalize(bsonDataReader);
						jsonSerializer.Populate(bsonDataReader, delta);
					}
					Queue<Task> tasks = new Queue<Task>();
					foreach(KeyValuePair<string, string> kvp in delta){
						tasks.Enqueue(asyncDictionary.Write(kvp.Key, kvp.Value));
					}
					delta.Clear();
					while(tasks.TryDequeue(out Task tsk)){
						await tsk;
					}
				}
			} finally{
				if(buffer is { }){
					Misc.arrayPool.Return(buffer);
				}
			}
		}
		private void InitLocks(){
			for(int i = 0; i < 65536; ){
				asyncReaderWriterLocks[i++] = new AsyncReaderWriterLock();
			}
		}
		private readonly IAsyncDictionary asyncDictionary;
		public YuriDatabaseEngine(IAsyncDictionary asyncDictionary){
			this.asyncDictionary = asyncDictionary ?? throw new ArgumentNullException(nameof(asyncDictionary));
			InitLocks();
		}
		public YuriDatabaseEngine(IAsyncDictionary asyncDictionary, Stream binlog)
		{
			this.asyncDictionary = asyncDictionary ?? throw new ArgumentNullException(nameof(asyncDictionary));
			this.binlog = binlog ?? throw new ArgumentNullException(nameof(binlog));
			binlogLock = new AsyncMutex();
			InitLocks();
		}
		private readonly AsyncMutex binlogLock;
		private readonly Stream binlog;
		private readonly AsyncReaderWriterLock[] asyncReaderWriterLocks = new AsyncReaderWriterLock[65536];
		private async Task WriteAndFlushBinlog(byte[] buffer, int len){
			await binlog.WriteAsync(buffer, 0, len);
			await binlog.FlushAsync();
		}
		public async Task<IReadOnlyDictionary<string, string>> Execute(IEnumerable<string> reads, IReadOnlyDictionary<string, string> conditions, IReadOnlyDictionary<string, string> writes)
		{
			//Lock checking
			Dictionary<ushort, bool> lockLevels = new Dictionary<ushort, bool>();
			bool write = writes.Count > 0;
			if(write){
				foreach (KeyValuePair<string, string> keyValuePair in writes)
				{
					lockLevels.Add((ushort)(keyValuePair.Key.GetHashCode() & 65535), true);
				}
			}
			foreach (string str in reads)
			{
				lockLevels.TryAdd((ushort)(str.GetHashCode() & 65535), false);
			}
			foreach (KeyValuePair<string, string> keyValuePair in conditions)
			{
				lockLevels.TryAdd((ushort)(keyValuePair.Key.GetHashCode() & 65535), false);
			}
			//Lock ordering
			List<ushort> locks = lockLevels.Keys.ToList();
			locks.Sort();

			//Pending reads
			Dictionary<string, Task<string>> pendingReads = new Dictionary<string, Task<string>>();
			Dictionary<string, string> readResults = new Dictionary<string, string>();
			Dictionary<string, Task<string>> conditionReads = new Dictionary<string, Task<string>>();

			//binlog stuff
			byte[] buffer = null;
			Task writeBinlog = null;
			bool binlocked = false;

			//Acquire locks
			foreach (ushort id in locks)
			{
				if (lockLevels[id])
				{
					await asyncReaderWriterLocks[id].AcquireWriterLock();
				}
				else
				{
					await asyncReaderWriterLocks[id].AcquireReaderLock();
				}
			}
			try
			{
				foreach (string read in reads)
				{
					if (!pendingReads.ContainsKey(read))
					{
						pendingReads.Add(read, asyncDictionary.Read(read));
					}
				}
				foreach (KeyValuePair<string, Task<string>> kvp in pendingReads)
				{
					readResults.Add(kvp.Key, await kvp.Value);
				}
				foreach (KeyValuePair<string, string> kvp in conditions)
				{
					string key = kvp.Key;
					if (!pendingReads.TryGetValue(key, out Task<string> tsk))
					{
						tsk = asyncDictionary.Read(key);
					}
					conditionReads.Add(kvp.Key, tsk);
				}
				if(!write){
					return readResults;
				}
				foreach (KeyValuePair<string, string> kvp in conditions)
				{
					if(kvp.Value != await conditionReads[kvp.Key]){
						return readResults;
					}
				}

				if (binlog is { })
				{
					int len;
					JsonSerializer jsonSerializer = new JsonSerializer();
					using (MemoryStream memoryStream = new MemoryStream())
					{
						using (Stream deflateStream = new DeflateStream(memoryStream, CompressionLevel.Optimal, true))
						{
							BsonDataWriter bsonDataWriter = new BsonDataWriter(deflateStream);
							jsonSerializer.Serialize(bsonDataWriter, writes);
						}
						len = (int)memoryStream.Position;
						memoryStream.Seek(0, SeekOrigin.Begin);
						buffer = Misc.arrayPool.Rent(len + 4);
						memoryStream.Read(buffer, 4, len);
					}
					BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), len);
					binlocked = true;
					await binlogLock.Enter();
					writeBinlog = WriteAndFlushBinlog(buffer, len + 4);
				}
				Queue<Task> writeTasks = new Queue<Task>();
				foreach (KeyValuePair<string, string> keyValuePair in writes)
				{
					writeTasks.Enqueue(asyncDictionary.Write(keyValuePair.Key, keyValuePair.Value));
				}
				foreach (Task tsk in writeTasks)
				{
					await tsk;
				}
			}
			finally
			{
				//Buffer is only allocated if we are binlogged
				if (buffer is { })
				{
					//Buffer will always be created before binlog locking
					if (binlocked)
					{
						try
						{
							//Binlog writing will always start after binlog locking
							if (writeBinlog is { })
							{
								await writeBinlog;
							}
						}
						finally
						{
							binlogLock.Exit();
						}
					}
					Misc.arrayPool.Return(buffer, false);
				}
				foreach (KeyValuePair<ushort, bool> keyValuePair in lockLevels)
				{
					if (keyValuePair.Value)
					{
						asyncReaderWriterLocks[keyValuePair.Key].ReleaseWriterLock();
					}
					else
					{
						asyncReaderWriterLocks[keyValuePair.Key].ReleaseReaderLock();
					}
				}
			}
			return readResults;
		}
	}

	public sealed class RemoteDatabaseEngine : IDatabaseEngine, IDisposable{
		[JsonObject(MemberSerialization.Fields)]
		private sealed class Packet{
			public readonly string id;
			public readonly IEnumerable<string> reads;
			public readonly IReadOnlyDictionary<string, string> conditions;
			public readonly IReadOnlyDictionary<string, string> writes;

			public Packet(IEnumerable<string> reads, IReadOnlyDictionary<string, string> conditions, IReadOnlyDictionary<string, string> writes)
			{
				this.reads = reads;
				this.conditions = conditions;
				this.writes = writes;
				Span<byte> bytes = stackalloc byte[32];
				id = Convert.ToBase64String(bytes, Base64FormattingOptions.None);
			}
		}
		[JsonObject(MemberSerialization.Fields)]

		private sealed class Reply{
			public string id;
			public IReadOnlyDictionary<string, string> result;
		}
		private readonly ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyDictionary<string, string>>> completionSources = new ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyDictionary<string, string>>>();

		private readonly ClientWebSocket clientWebSocket = new ClientWebSocket();
		private readonly AsyncMutex asyncMutex = new AsyncMutex();
		private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
		public static async Task<RemoteDatabaseEngine> Connect(Uri server, CancellationToken cancellationToken = default)
		{
			RemoteDatabaseEngine remoteDatabaseEngine = new RemoteDatabaseEngine();
			await remoteDatabaseEngine.clientWebSocket.ConnectAsync(server, cancellationToken);
			remoteDatabaseEngine.ReceiveEventLoop();
			return remoteDatabaseEngine;
		}
		private async void StartTimeout(string id){
			await Task.Delay(5000);
			if (completionSources.TryRemove(id, out TaskCompletionSource<IReadOnlyDictionary<string, string>> taskCompletionSource))
			{
				taskCompletionSource.SetException(new TimeoutException("The database transaction took too long!"));
			}
		}
		private async void SendImpl(string json, string id)
		{
			byte[] bytes = null;
			try
			{
				int len = Encoding.UTF8.GetByteCount(json);
				bytes = Misc.arrayPool.Rent(len);
				Encoding.UTF8.GetBytes(json, 0, json.Length, bytes, 0);

				await asyncMutex.Enter();
				try
				{
					await clientWebSocket.SendAsync(bytes.AsMemory(0, len), WebSocketMessageType.Text, true, default);
				}
				finally
				{
					asyncMutex.Exit();
				}
			}
			catch (Exception e)
			{
				if (completionSources.TryRemove(id, out TaskCompletionSource<IReadOnlyDictionary<string, string>> taskCompletionSource))
				{
					taskCompletionSource.SetException(e);
				}
			}
			finally
			{
				if (bytes is { })
				{
					Misc.arrayPool.Return(bytes, false);
				}
			}
		}
		public Task<IReadOnlyDictionary<string, string>> Execute(IEnumerable<string> reads, IReadOnlyDictionary<string, string> conditions, IReadOnlyDictionary<string, string> writes)
		{
			Packet packet = new Packet(reads, conditions, writes);
			string json = JsonConvert.SerializeObject(packet);
			
			TaskCompletionSource<IReadOnlyDictionary<string, string>> taskCompletionSource = new TaskCompletionSource<IReadOnlyDictionary<string, string>>();
			completionSources.TryAdd(packet.id, taskCompletionSource);

			//One of these might complete first
			StartTimeout(packet.id);
			SendImpl(json, packet.id);
			return taskCompletionSource.Task;
			
		}
		private async void ReceiveEventLoop(){
			try{
				Reply reply = new Reply();
				byte[] buffer = new byte[65536];
				JsonSerializer jsonSerializer = new JsonSerializer();
				jsonSerializer.MissingMemberHandling = MissingMemberHandling.Error;
				CancellationToken cancellationToken = cancellationTokenSource.Token;
				while(true){
					using(MemoryStream memoryStream = new MemoryStream()){
					read:
						WebSocketReceiveResult recv = await clientWebSocket.ReceiveAsync(buffer, cancellationToken);
						if (recv.MessageType.HasFlag(WebSocketMessageType.Close))
						{
							return;
						}
						int len = recv.Count;
						await memoryStream.WriteAsync(buffer, 0, len);
						if (!recv.EndOfMessage)
						{
							memoryStream.Capacity += len;
							goto read;
						}
						memoryStream.Seek(0, SeekOrigin.Begin);
						using(StreamReader streamReader = new StreamReader(memoryStream, Encoding.UTF8, false, -1, true)){
							jsonSerializer.Populate(streamReader, reply);
						}
					}
					if(completionSources.TryRemove(reply.id, out TaskCompletionSource<IReadOnlyDictionary<string, string>> taskCompletionSource)){
						taskCompletionSource.SetResult(reply.result);
					}
				}
			} catch{
				
			}
			finally
			{
				await asyncMutex.Enter();
				try{
					await clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Jessie Lesbian is cute!", default);
					clientWebSocket.Dispose();
					cancellationTokenSource.Dispose();
				} finally{
					asyncMutex.Exit();
				}
			}
		}
		private volatile int disposed;
		public void Dispose()
		{
			if(Interlocked.Exchange(ref disposed, 1) == 0){
				GC.SuppressFinalize(this);
				cancellationTokenSource.Cancel();
			}
		}

		private RemoteDatabaseEngine(){
			GC.SuppressFinalize(clientWebSocket);
			GC.SuppressFinalize(cancellationTokenSource);
			clientWebSocket.Options.AddSubProtocol("LesbianDB-v2.1");
		}


		~RemoteDatabaseEngine(){
			if (Interlocked.Exchange(ref disposed, 1) == 0)
			{
				cancellationTokenSource.Cancel();
			}
		}
		
		
	}
}
