﻿// -----------------------------------------------------------------------
//  <copyright file="HttpTransportSender.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using Rhino.Raft.Messages;

namespace Rhino.Raft.Transport
{
	/// <summary>
	/// All requests are fire & forget, with the reply coming in (if at all)
	/// from the resulting thread.
	/// </summary>
	public class HttpTransportSender  : IDisposable
	{
		private readonly HttpTransportBus _bus;

		private readonly ConcurrentDictionary<string, NodeConnectionInfo> _nodeConnectionInfos =
			new ConcurrentDictionary<string, NodeConnectionInfo>();

		private readonly ConcurrentDictionary<string, ConcurrentQueue<HttpClient>> _httpClientsCache = new ConcurrentDictionary<string, ConcurrentQueue<HttpClient>>();
		private readonly Logger _log;
		public HttpTransportSender(string name, HttpTransportBus bus)
		{
			_bus = bus;
			_log = LogManager.GetLogger(GetType().Name + "." + name);
		}

		public void Register(NodeConnectionInfo connectionInfo)
		{
			_nodeConnectionInfos.AddOrUpdate(connectionInfo.Name, connectionInfo, (s, info) => connectionInfo);
		}

		public void Stream(string dest, InstallSnapshotRequest req, Action<Stream> streamWriter)
		{
			HttpClient client;
			using (GetConnection(dest, out client))
			{
				LogStatus("install snapshot to " + dest, async () =>
				{
					var requestUri =
						string.Format("raft/installSnapshot?term={0}&=lastIncludedIndex={1}&lastIncludedTerm={2}&from={3}&topology={4}",
							req.Term, req.LastIncludedIndex, req.LastIncludedTerm, req.From, Uri.EscapeDataString(JsonConvert.SerializeObject(req.Topology)));
					var httpResponseMessage = await client.PostAsync(requestUri, new SnapshotContent(streamWriter));
					httpResponseMessage.EnsureSuccessStatusCode();
					var reply = await httpResponseMessage.Content.ReadAsStringAsync();
					var installSnapshotResponse = JsonConvert.DeserializeObject<InstallSnapshotResponse>(reply);
					SendToSelf(installSnapshotResponse);
				});
			}
		}

		public class SnapshotContent : HttpContent
		{
			private readonly Action<Stream> _streamWriter;

			public SnapshotContent(Action<Stream> streamWriter)
			{
				_streamWriter = streamWriter;
			}

			protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
			{
				_streamWriter(stream);

				return Task.FromResult(1);
			}

			protected override bool TryComputeLength(out long length)
			{
				length = -1;
				return false;
			}
		}

		public void Send(string dest, AppendEntriesRequest req)
		{
			HttpClient client;
			using (GetConnection(dest, out client))
			{
				LogStatus("append entries to " + dest, async () =>
				{
					var requestUri = string.Format("raft/appendEntries?term={0}&leaderCommit={1}&prevLogTerm={2}&prevLogIndex={3}&entriesCount={4}&from={5}",
						req.Term, req.LeaderCommit, req.PrevLogTerm, req.PrevLogIndex, req.EntriesCount, req.From);
					var httpResponseMessage = await client.PostAsync(requestUri,new EntriesContent(req.Entries));
					var reply = await httpResponseMessage.Content.ReadAsStringAsync();
					httpResponseMessage.EnsureSuccessStatusCode();
					var appendEntriesResponse = JsonConvert.DeserializeObject<AppendEntriesResponse>(reply);
					SendToSelf(appendEntriesResponse);
				});
			}
		}

		private class EntriesContent : HttpContent
		{
			private readonly LogEntry[] _entries;

			public EntriesContent(LogEntry[] entries)
			{
				_entries = entries;
			}

			protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
			{
				foreach (var logEntry in _entries)
				{
					Write7BitEncodedInt64(stream, logEntry.Index);
					Write7BitEncodedInt64(stream, logEntry.Term);
					stream.WriteByte(logEntry.IsTopologyChange == true ? (byte)1 : (byte)0);
					Write7BitEncodedInt64(stream, logEntry.Data.Length);
					stream.Write(logEntry.Data, 0, logEntry.Data.Length);
				}
				return Task.FromResult(1);
			}

			private void Write7BitEncodedInt64(Stream stream, long value)
			{
				var v = (ulong)value;
				while (v >= 128)
				{
					stream.WriteByte((byte)(v | 128));
					v >>= 7;
				}
				stream.WriteByte((byte)(v));
			}

			private int SizeOf7BitEncodedInt64(long value)
			{
				var size = 1;
				var v = (ulong)value;
				while (v >= 128)
				{
					size ++;
					v >>= 7;
				}
				return size;
			}

			protected override bool TryComputeLength(out long length)
			{
				length = 0;
				foreach (var logEntry in _entries)
				{
					length += SizeOf7BitEncodedInt64(logEntry.Index) +
					          SizeOf7BitEncodedInt64(logEntry.Term) +
					          1 /*topology*/+
							  SizeOf7BitEncodedInt64(logEntry.Data.Length) +
					          logEntry.Data.Length;
				}
				return true;
			}
		}

		public void Send(string dest, CanInstallSnapshotRequest req)
		{
			HttpClient client;
			using (GetConnection(dest, out client))
			{
				LogStatus("can install snapshot to " + dest, async () =>
				{
					var requestUri = string.Format("raft/canInstallSnapshot?term={0}&=index{1}&from={2}", req.Term, req.Index,
						req.From);
					var httpResponseMessage = await client.GetAsync(requestUri);
					httpResponseMessage.EnsureSuccessStatusCode();
					var reply = await httpResponseMessage.Content.ReadAsStringAsync();
					var canInstallSnapshotResponse = JsonConvert.DeserializeObject<CanInstallSnapshotResponse>(reply);
					SendToSelf(canInstallSnapshotResponse);
				});
			}
		}
	
		public void Send(string dest, RequestVoteRequest req)
		{
			HttpClient client;
			using (GetConnection(dest, out client))
			{
				LogStatus("request vote from " + dest, async () =>
				{
					var requestUri = string.Format("raft/requestVote?term={0}&=lastLogIndex{1}&lastLogTerm={2}&trialOnly={3}&forcedElection={4}&from={5}", 
						req.Term, req.LastLogIndex, req.LastLogTerm, req.TrialOnly, req.ForcedElection, req.From);
					var httpResponseMessage = await client.GetAsync(requestUri);
					httpResponseMessage.EnsureSuccessStatusCode();
					var reply = await httpResponseMessage.Content.ReadAsStringAsync();
					var requestVoteResponse = JsonConvert.DeserializeObject<RequestVoteResponse>(reply);
					SendToSelf(requestVoteResponse);
				});
			}
		}

		private void SendToSelf(object o)
		{
			_bus.Publish(o, source: null);
		}

		public void Send(string dest, TimeoutNowRequest req)
		{
			HttpClient client;
			using (GetConnection(dest, out client))
			{
				LogStatus("timeout to " + dest, async () =>
				{
					var message = await client.GetAsync(string.Format("raft/timeoutNow?term={0}&from={1}", req.Term, req.From));
					message.EnsureSuccessStatusCode();
					SendToSelf(new NothingToDo());
				});
			}
		}

		private ConcurrentDictionary<Task, object> _runningOps = new ConcurrentDictionary<Task, object>();

		private void LogStatus(string details, Func<Task> operation)
		{
			var op = operation();
			_runningOps.TryAdd(op, op);
			op
				.ContinueWith(task =>
				{
					object value;
					_runningOps.TryRemove(op, out value);
					if (task.Exception != null)
					{
						_log.Warn("Failed to send  " + details, task.Exception);
						return;
					}
					_log.Info("Sent {0}", details);
				});
		}


		public void Dispose()
		{
			foreach (var q in _httpClientsCache.Select(x=>x.Value))
			{
				HttpClient result;
				while (q.TryDequeue(out result))
				{
					result.Dispose();
				}
			}
			_httpClientsCache.Clear();
			var array = _runningOps.Keys.ToArray();
			_runningOps.Clear();
			try
			{
				Task.WaitAll(array);
			}
			catch (OperationCanceledException e)
			{
				// nothing to do here
			}
			catch (AggregateException e)
			{
				if (e.InnerException is OperationCanceledException == false)
					throw;
				// nothing to do here
			}
		}


		private ReturnToQueue GetConnection(string dest, out HttpClient result)
		{
			NodeConnectionInfo info;
			if (_nodeConnectionInfos.TryGetValue(dest, out info) == false)
				throw new InvalidOperationException("Don't know how to connect to " + dest);

			var connectionQueue = _httpClientsCache.GetOrAdd(dest, _ => new ConcurrentQueue<HttpClient>());

			if (connectionQueue.TryDequeue(out result) == false)
			{
				result = new HttpClient
				{
					BaseAddress = info.Url
				};
			}

			return new ReturnToQueue(result, connectionQueue);
		}

		private struct ReturnToQueue : IDisposable
		{
			private readonly HttpClient client;
			private readonly ConcurrentQueue<HttpClient> queue;

			public ReturnToQueue(HttpClient client, ConcurrentQueue<HttpClient> queue)
			{
				this.client = client;
				this.queue = queue;
			}

			public void Dispose()
			{
				queue.Enqueue(client);
			}
		}

	}
}