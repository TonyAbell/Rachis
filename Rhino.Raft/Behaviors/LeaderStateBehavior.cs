﻿// -----------------------------------------------------------------------
//  <copyright file="AbstractRaftStateBehavior.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rhino.Raft.Commands;
using Rhino.Raft.Messages;
using Rhino.Raft.Storage;

namespace Rhino.Raft.Behaviors
{
	public class LeaderStateBehavior : AbstractRaftStateBehavior
	{
		private readonly ConcurrentDictionary<string, long> _matchIndexes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, long> _nextIndexes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, Task> _snapshotsPendingInstallation = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);

		private readonly ConcurrentQueue<Command> _pendingCommands = new ConcurrentQueue<Command>();
		private readonly Task _heartbeatTask;

		private readonly CancellationTokenSource _disposedCancellationTokenSource = new CancellationTokenSource();
		private readonly CancellationTokenSource _stopHeartbeatCancellationTokenSource;

		public event Action HeartbeatSent;

		public LeaderStateBehavior(RaftEngine engine)
			: base(engine)
		{
			Timeout = engine.MessageTimeout;

			var lastLogEntry = Engine.PersistentState.LastLogEntry();

			foreach (var peer in Engine.AllVotingNodes)
			{
				_nextIndexes[peer] = lastLogEntry.Index + 1;
				_matchIndexes[peer] = 0;
			}

			AppendCommand(new NopCommand());
			_stopHeartbeatCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(Engine.CancellationToken, _disposedCancellationTokenSource.Token);

			_heartbeatTask = Task.Run(() => Heartbeat(), _stopHeartbeatCancellationTokenSource.Token);
		}

		private void Heartbeat()
		{
			while (_stopHeartbeatCancellationTokenSource.IsCancellationRequested == false)
			{
				Engine.DebugLog.Write("Starting sending Leader heartbeats to: {0}", string.Join(", ", Engine.CurrentTopology.AllVotingNodes));
				SendEntriesToAllPeers();

				OnHeartbeatSent();
				Thread.Sleep(Math.Min(Engine.MessageTimeout / 6, 250));
			}
		}

		internal void SendEntriesToAllPeers()
		{
			var peers = Engine.AllVotingNodes;
			foreach (var peer in peers)
			{
				if (peer.Equals(Engine.Name, StringComparison.OrdinalIgnoreCase))
					continue;// we don't need to send to ourselves

				_stopHeartbeatCancellationTokenSource.Token.ThrowIfCancellationRequested();
				SendEntriesToPeer(peer);
			}
		}

		private void SendEntriesToPeer(string peer)
		{
			LogEntry prevLogEntry;
			LogEntry[] entries;

			var nextIndex = _nextIndexes.GetOrAdd(peer, 0); //new peer's index starts at 0

			if (Engine.StateMachine.SupportSnapshots)
			{
				var snapshotIndex = Engine.PersistentState.GetLastSnapshotIndex();

				if (snapshotIndex != null && nextIndex < snapshotIndex)
				{
					if (_snapshotsPendingInstallation.ContainsKey(peer))
						return;

					using (var snapshotWriter = Engine.StateMachine.GetSnapshotWriter())
					{
						Engine.Transport.Send(peer, new CanInstallSnapshotRequest
						{
							From = Engine.Name,
							Index = snapshotWriter.Index,
							Term = snapshotWriter.Term,
							LeaderId = Engine.Name
						});
					}
					return;
				}
			}

			try
			{
				entries = Engine.PersistentState.LogEntriesAfter(nextIndex)
					.Take(Engine.MaxEntriesPerRequest)
					.ToArray();

				prevLogEntry = entries.Length == 0
					? Engine.PersistentState.LastLogEntry()
					: Engine.PersistentState.GetLogEntry(entries[0].Index - 1);
			}
			catch (Exception e)
			{
				Engine.DebugLog.Write("Error while fetching entries from persistent state. Reason : {0}", e);
				throw;
			}

			prevLogEntry = prevLogEntry ?? new LogEntry();
			
			if (entries.Length> 0)
				Engine.DebugLog.Write("Sending {0:#,#;;0} entries to {1} (PrevLogEntry: {3}). Entry indexes: {2}", entries.Length, peer, String.Join(",", entries.Select(x => x.Index)),
					prevLogEntry.Index.ToString(CultureInfo.InvariantCulture));
			else
				Engine.DebugLog.Write("Sending empty heartbeat to {0} (PrevLogEntry: {1})", peer,
					prevLogEntry.Index.ToString(CultureInfo.InvariantCulture));

			var aer = new AppendEntriesRequest
			{
				Entries = entries,
				LeaderCommit = Engine.CommitIndex,
				LeaderId = Engine.Name,
				PrevLogIndex = prevLogEntry.Index,
				PrevLogTerm = prevLogEntry.Term,
				Term = Engine.PersistentState.CurrentTerm,
				From = Engine.Name
			};

			Engine.Transport.Send(peer, aer);

			Engine.OnEntriesAppended(entries);
		}

		private void SendSnapshotToPeer(string peer)
		{
			try
			{
				var sp = Stopwatch.StartNew();
				using (var snapshotWriter = Engine.StateMachine.GetSnapshotWriter())
				{
					Engine.DebugLog.Write("Streaming snapshot to {0} - term {1}, index {2}", peer,
						snapshotWriter.Term,
						snapshotWriter.Index);

					Engine.Transport.Stream(peer, new InstallSnapshotRequest
					{
						Term = Engine.PersistentState.CurrentTerm,
						LastIncludedIndex = snapshotWriter.Index,
						LastIncludedTerm = snapshotWriter.Term,
						From = Engine.Name,
						LeaderId = Engine.Name,
					}, stream => snapshotWriter.WriteSnapshot(stream));

					Engine.DebugLog.Write("Finished snapshot streaming -> to {0} - term {1}, index {2} in {3}", peer, snapshotWriter.Index,
						snapshotWriter.Term, sp.Elapsed);
				}

			}
			catch (Exception e)
			{
				Engine.DebugLog.Write("Failed to send snapshot to {0} because:\r\n{1}", peer, e);
			}
		}

		public override RaftEngineState State
		{
			get { return RaftEngineState.Leader; }
		}

		public override void HandleTimeout()
		{
			// we set this value purely because we want to wait
			// for messages from the network. And we use the last heartbeat time 
			// to change the timeout we have
			LastHeartbeatTime = DateTime.UtcNow; 
		}

		public override void Handle(string destination, CanInstallSnapshotResponse resp)
		{
			Task snapshotInstallationTask;
			if (resp.Success == false)
			{
				Engine.DebugLog.Write("Received CanInstallSnapshotResponse(Success=false) from {0}, Term = {1}, Index = {2}, updating and will try again", 
					resp.From,
					resp.Term,
					resp.Index);
				_matchIndexes[resp.From] = resp.Index;
				_nextIndexes[resp.From] = resp.Index + 1;
				_snapshotsPendingInstallation.TryRemove(resp.From, out snapshotInstallationTask);
				return;
			}
			if (resp.IsCurrentlyInstalling)
			{
				Engine.DebugLog.Write("Received CanInstallSnapshotResponse(IsCurrentlyInstalling=false) from {0}, Term = {1}, Index = {2}, will retry when it is done",
					resp.From,
					resp.Term,
					resp.Index);
				
				_snapshotsPendingInstallation.TryRemove(resp.From, out snapshotInstallationTask);
				return;
			}
			Engine.DebugLog.Write("Received CanInstallSnapshotResponse from {0}, starting snapshot streaming task", resp.From);


			// problem, we can't just send the log entries, we have to send
			// the full snapshot to this destination, this can take a very long 
			// time for large data sets. Because of that, we are doing that in a 
			// background thread, and while we are doing that, we aren't going to be
			// doing any communication with this peer. Note that while the peer
			// is accepting the snapshot, it isn't counting the heartbeat timer, or 
			// can move to become a candidate.
			// During normal operations, we will never be using this, since we leave a buffer
			// in place (by default roughly 4K entries) to make sure that small disconnects will
			// not cause us to be forced to send a snapshot over the wire.

			if (_snapshotsPendingInstallation.ContainsKey(resp.From))
				return; // already sending


			var task = new Task(() => SendSnapshotToPeer(resp.From));
			task.ContinueWith(_ => _snapshotsPendingInstallation.TryRemove(resp.From, out _));

			if (_snapshotsPendingInstallation.TryAdd(resp.From, task))
				task.Start();
		}

		public override void Handle(string destination, AppendEntriesResponse resp)
		{
			Engine.DebugLog.Write("Handling AppendEntriesResponse from {0}", resp.From);

			// there is a new leader in town, time to step down
			if (resp.CurrentTerm > Engine.PersistentState.CurrentTerm)
			{
				Engine.UpdateCurrentTerm(resp.CurrentTerm, resp.LeaderId);
				return;
			}

			Debug.Assert(resp.From != null);
			_nextIndexes[resp.From] = resp.LastLogIndex + 1;
			_matchIndexes[resp.From] = resp.LastLogIndex;
			Engine.DebugLog.Write("Follower ({0}) has LastLogIndex = {1}", resp.From, resp.LastLogIndex);

			if (resp.Success == false)
			{
				Engine.DebugLog.Write("Received Success = false in AppendEntriesResponse from {1}. Now _nextIndexes[{1}] = {0}. Reason: {2}",
					_nextIndexes[resp.From], resp.From, resp.Message);
				return;
			}

			var maxIndexOnCurrentQuorom = GetMaxIndexOnQuorom();
			if (maxIndexOnCurrentQuorom == -1)
			{
				Engine.DebugLog.Write("Not enough followers committed, not applying commits yet");
				return;
			}

			if (maxIndexOnCurrentQuorom <= Engine.CommitIndex)
			{
				Engine.DebugLog.Write("maxIndexOnQuorom = {0} <= Engine.CommitIndex = {1} --> no need to apply commits",
					maxIndexOnCurrentQuorom, Engine.CommitIndex);
				return;
			}

			Engine.DebugLog.Write(
				"AppendEntriesResponse => applying commits, maxIndexOnQuorom = {0}, Engine.CommitIndex = {1}", maxIndexOnCurrentQuorom,
				Engine.CommitIndex);
			Engine.ApplyCommits(Engine.CommitIndex + 1, maxIndexOnCurrentQuorom);

			Command result;
			while (_pendingCommands.TryPeek(out result) && result.AssignedIndex <= maxIndexOnCurrentQuorom)
			{
				if (_pendingCommands.TryDequeue(out result) == false)
					break; // should never happen

				result.Completion.TrySetResult(null);
			}
		}

		/// <summary>
		/// This method works on the match indexes, assume that we have three nodes
		/// A, B and C, and they have the following index values:
		/// 
		/// { A = 4, B = 3, C = 2 }
		/// 
		/// 
		/// In this case, the quorom agrees on 3 as the committed index.
		/// 
		/// Why? Because A has 4 (which implies that it has 3) and B has 3 as well.
		/// So we have 2 nodes that have 3, so that is the quorom.
		/// </summary>
		private long GetMaxIndexOnQuorom()
		{
			var topology = Engine.CurrentTopology;
			var dic = new Dictionary<long, int>();
			foreach (var index in _matchIndexes)
			{
				if (topology.AllVotingNodes.Contains(index.Key, StringComparer.OrdinalIgnoreCase) == false)
					continue;

				int count;
				dic.TryGetValue(index.Value, out count);

				dic[index.Value] = count + 1;
			}
			var boost = 0;
			foreach (var source in dic.OrderByDescending(x => x.Key))
			{
				var confirmationsForThisIndex = source.Value + boost;
				boost += source.Value;
				if (confirmationsForThisIndex >= topology.QuoromSize)
					return source.Key;
			}

			return -1;
		}

		public void AppendCommand(Command command)
		{
			var index = Engine.PersistentState.AppendToLeaderLog(command);
			_matchIndexes[Engine.Name] = index;
			_nextIndexes[Engine.Name] = index + 1;

			if (Engine.CurrentTopology.QuoromSize == 1)
			{
				CommitEntries(null, index, index);
				if (command.Completion != null)
					command.Completion.SetResult(null);

				return;
			}

			if (command.Completion != null)
				_pendingCommands.Enqueue(command);
		}

		public override void Dispose()
		{
			_disposedCancellationTokenSource.Cancel();
			try
			{
				_heartbeatTask.Wait(Timeout * 2);
			}
			catch (OperationCanceledException)
			{
				//expected
			}
			catch (AggregateException e)
			{
				if (e.InnerException is OperationCanceledException == false)
					throw;
			}
		}

		protected virtual void OnHeartbeatSent()
		{
			var handler = HeartbeatSent;
			if (handler != null) handler();
		}
	}
}