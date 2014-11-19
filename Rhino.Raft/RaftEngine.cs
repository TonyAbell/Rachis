﻿// -----------------------------------------------------------------------
//  <copyright file="RaftEngine.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using Rhino.Raft.Behaviors;
using Rhino.Raft.Commands;
using Rhino.Raft.Interfaces;
using Rhino.Raft.Messages;
using Rhino.Raft.Storage;
using Rhino.Raft.Utils;

namespace Rhino.Raft
{
	public class RaftEngine : IDisposable
	{
		private readonly RaftEngineOptions _raftEngineOptions;
		private readonly CancellationTokenSource _eventLoopCancellationTokenSource;
		private readonly ManualResetEventSlim _leaderSelectedEvent = new ManualResetEventSlim();
		private TaskCompletionSource<object> _steppingDownCompletionSource;

		private Topology _currentTopology;

		internal Logger _log;

		public IRaftStateMachine StateMachine { get { return _raftEngineOptions.StateMachine; } }

		public ITransport Transport { get { return _raftEngineOptions.Transport; } }

		public IEnumerable<string> AllVotingNodes
		{
			get
			{
				return _currentTopology.AllVotingNodes;
			}
		}

		public bool ContainedInAllVotingNodes(string node)
		{
			return _currentTopology.AllVotingNodes.Contains(node);
		}

		public string Name { get; set; }
		public PersistentState PersistentState { get; set; }

		public string CurrentLeader
		{
			get
			{
				return _currentLeader;
			}
			set
			{
				if (_currentLeader == value)
					return;

				_log.Debug("Setting CurrentLeader: {0}", value);
				_currentLeader = value;


				if (value == null)
					_leaderSelectedEvent.Reset();
				else
				{
					_leaderSelectedEvent.Set();
				}
			}
		}

		/// <summary>
		/// This is a thread safe operation, since this is being used by both the leader's message processing thread
		/// and the leader's heartbeat thread
		/// </summary>
		public long CommitIndex
		{
			get
			{
				return Thread.VolatileRead(ref _commitIndex);
			}
			set
			{
				Interlocked.Exchange(ref _commitIndex, value);
			}
		}

		public RaftEngineState State
		{
			get
			{
				var behavior = StateBehavior;
				if (behavior == null)
					return RaftEngineState.None;
				return behavior.State;
			}

		}

		private readonly Task _eventLoopTask;

		private long _commitIndex;
		private string _currentLeader;

		private Task _snapshottingTask;
		private Task _changingTopology;

		private AbstractRaftStateBehavior StateBehavior { get; set; }

		public CancellationToken CancellationToken { get { return _eventLoopCancellationTokenSource.Token; } }

		public event Action<RaftEngineState> StateChanged;
		public event Action<Command> CommitApplied;
		public event Action<long> NewTerm;
		public event Action ElectionStarted;
		public event Action StateTimeout;
		public event Action<LogEntry[]> EntriesAppended;
		public event Action<long, long> CommitIndexChanged;
		public event Action ElectedAsLeader;

		public event Action<TopologyChangeCommand> TopologyChanged;
		public event Action TopologyChanging;

		public event Action CreatingSnapshot;
		public event Action CreatedSnapshot;

		public event Action InstallingSnapshot;
		public event Action SnapshotInstalled;

		public event Action<Exception> SnapshotCreationError;

		/// <summary>
		/// will fire each time event loop of the node will process events and send response messages
		/// </summary>
		public event Action EventsProcessed;

		public RaftEngine(RaftEngineOptions raftEngineOptions)
		{
			_raftEngineOptions = raftEngineOptions;
			Debug.Assert(raftEngineOptions.Stopwatch != null);

			_log = LogManager.GetLogger(GetType().FullName + "." + raftEngineOptions.Name);

			_eventLoopCancellationTokenSource = new CancellationTokenSource();

			Name = raftEngineOptions.Name;
			PersistentState = new PersistentState(raftEngineOptions.Options, _eventLoopCancellationTokenSource.Token)
			{
				CommandSerializer = new JsonCommandSerializer()
			};

			_currentTopology = PersistentState.GetCurrentTopology();

			if (raftEngineOptions.ForceNewTopology ||
				(_currentTopology.AllVotingNodes.Count == 0))
			{
				_currentTopology = new Topology(raftEngineOptions.AllVotingNodes ?? new[] { Name });
				PersistentState.SetCurrentTopology(_currentTopology, 0);
			}

			//warm up to make sure that the serializer don't take too long and force election timeout
			PersistentState.CommandSerializer.Serialize(new NopCommand());

			var thereAreOthersInTheCluster = AllVotingNodes.Any(n => !n.Equals(Name, StringComparison.OrdinalIgnoreCase));
			if (thereAreOthersInTheCluster == false)
			{
				SetState(RaftEngineState.Leader);
				PersistentState.UpdateTermTo(this, PersistentState.CurrentTerm + 1);// restart means new term
			}
			else
			{
				SetState(RaftEngineState.Follower);
			}

			_eventLoopTask = Task.Run(() => EventLoop());
		}

		public RaftEngineOptions Options
		{
			get { return _raftEngineOptions; }
		}

		protected void EventLoop()
		{
			while (_eventLoopCancellationTokenSource.IsCancellationRequested == false)
			{
				try
				{
					MessageContext message;
					var behavior = StateBehavior;
					var lastHeartBeat = (int)(DateTime.UtcNow - behavior.LastHeartbeatTime).TotalMilliseconds;
					var timeout = behavior.Timeout - lastHeartBeat;
					var hasMessage = Transport.TryReceiveMessage(timeout, _eventLoopCancellationTokenSource.Token, out message);
					if (_eventLoopCancellationTokenSource.IsCancellationRequested)
						break;

					if (hasMessage == false)
					{
						if (State != RaftEngineState.Leader)
							_log.Debug("State {0} timeout ({1:#,#;;0} ms).", State, behavior.Timeout);
						behavior.HandleTimeout();
						OnStateTimeout();
						continue;
					}
					_log.Debug("{0}: {1} {2}", State, 
						message.Message.GetType().Name,
						message.Message is BaseMessage ? JsonConvert.SerializeObject(message.Message) : string.Empty
						);

					behavior.HandleMessage(message);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}

		internal void UpdateCurrentTerm(long term, string leader)
		{
			PersistentState.UpdateTermTo(this, term);
			SetState(RaftEngineState.Follower);
			_log.Debug("UpdateCurrentTerm() setting new leader : {0}", leader ?? "no leader currently");
			CurrentLeader = leader;
		}

		internal void SetState(RaftEngineState state)
		{
			if (state == State)
				return;

			if (State == RaftEngineState.Leader)
				_leaderSelectedEvent.Reset();

			_log.Debug("{0} ==> {1}", State, state);

			var oldState = StateBehavior;
			using (oldState)
			{
				switch (state)
				{
					case RaftEngineState.Follower:
					case RaftEngineState.FollowerAfterStepDown:
						StateBehavior = new FollowerStateBehavior(this, state == RaftEngineState.FollowerAfterStepDown);
						break;
					case RaftEngineState.CandidateByRequest:
					case RaftEngineState.Candidate:
						StateBehavior = new CandidateStateBehavior(this, state == RaftEngineState.CandidateByRequest);
						break;
					case RaftEngineState.SnapshotInstallation:
						StateBehavior = new SnapshotInstallationStateBehavior(this);
						break;
					case RaftEngineState.Leader:
						StateBehavior = new LeaderStateBehavior(this);
						CurrentLeader = Name;
						OnElectedAsLeader();
						break;
					case RaftEngineState.SteppingDown:
						StateBehavior = new SteppingDownStateBehavior(this);
						CurrentLeader = Name;
						break;
					case RaftEngineState.None:
						_eventLoopCancellationTokenSource.Cancel(); //stop event loop						
						break;
					default:
						throw new ArgumentOutOfRangeException(state.ToString());
				}

				Debug.Assert(StateBehavior != null, "StateBehavior != null");

				OnStateChanged(state);
			}
		}

		public Task StepDownAsync()
		{
			if (State != RaftEngineState.Leader)
				throw new InvalidOperationException("Not a leader, and only leaders can step down");

			if (CurrentTopology.QuoromSize == 1)
			{
				throw new InvalidOperationException("Cannot step down if I'm the only one in the cluster");
			}

			_steppingDownCompletionSource = new TaskCompletionSource<object>();
			_log.Debug("Got a request to step down from leadership position, personal reasons, you understand.");
			SetState(RaftEngineState.SteppingDown);

			return _steppingDownCompletionSource.Task;
		}

		public Task RemoveFromClusterAsync(string node)
		{
			if (_currentTopology.AllVotingNodes.Contains(node) == false)
				throw new InvalidOperationException("Node " + node + " was not found in the cluster");

			if (string.Equals(node, Name, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("You cannot remove the current node from the cluster, step down this node and then remove it from the new leader");

			var requestedTopology = _currentTopology.CloneAndRemove(node);
			if (_log.IsInfoEnabled)
			{
				_log.Info("RemoveFromClusterAsync, requestedTopology: {0}", requestedTopology.AllVotingNodes);
			}
			return ModifyTopology(requestedTopology);
		}

		public Task AddToClusterAsync(string node)
		{
			if (_currentTopology.AllVotingNodes.Contains(node))
				throw new InvalidOperationException("Node " + node + " is already in the cluster");

			var requestedTopology = _currentTopology.CloneAndAdd(node);
			if (_log.IsInfoEnabled)
			{
				_log.Info("AddToClusterClusterAsync, requestedTopology: {0}", requestedTopology.AllVotingNodes);
			} return ModifyTopology(requestedTopology);
		}

		private Task ModifyTopology(Topology requested)
		{
			if (State != RaftEngineState.Leader)
				throw new InvalidOperationException("Cannot modify topology from a non leader node, current leader is: " +
				                                    (CurrentLeader ?? "no leader"));

			var tcc = new TopologyChangeCommand
				{
					Completion = new TaskCompletionSource<object>(),
					Requested = requested,
					BufferCommand = false,
				};

			if (Interlocked.CompareExchange(ref _changingTopology, tcc.Completion.Task, null) != null)
				throw new InvalidOperationException("Cannot change the cluster topology while another topology change is in progress");

			try
			{
				_log.Debug("Topology change started on leader");
				TopologyChangeStarting(tcc);
				AppendCommand(tcc);
				return tcc.Completion.Task;
			}
			catch (Exception)
			{
				Interlocked.Exchange(ref _changingTopology, null);
				throw;
			}
		}

		public Topology CurrentTopology
		{
			get { return _currentTopology; }
		}

		internal bool LogIsUpToDate(long lastLogTerm, long lastLogIndex)
		{
			// Raft paper 5.4.1
			var lastLogEntry = PersistentState.LastLogEntry();

			if (lastLogEntry.Term < lastLogTerm)
				return true;
			return lastLogEntry.Index <= lastLogIndex;
		}

		public void WaitForLeader()
		{
			_leaderSelectedEvent.Wait(CancellationToken);
		}

		public void AppendCommand(Command command)
		{
			if (command == null) throw new ArgumentNullException("command");

			var leaderStateBehavior = StateBehavior as LeaderStateBehavior;
			if (leaderStateBehavior== null || leaderStateBehavior.State != RaftEngineState.Leader)
				throw new NotLeadingException("Command can be appended only on leader node. This node behavior type is " +
													StateBehavior.GetType().Name)
				{
					CurrentLeader = CurrentLeader 
				};

		
			leaderStateBehavior.AppendCommand(command);
		}

		public void ApplyCommits(long from, long to)
		{
			Debug.Assert(to >= from);
			foreach (var entry in PersistentState.LogEntriesAfter(from, to))
			{
				try
				{
					var oldCommitIndex = CommitIndex;
					var command = PersistentState.CommandSerializer.Deserialize(entry.Data);

					var sysCommand = command is NopCommand || command is TopologyChangeCommand;

					if(sysCommand == false)
						StateMachine.Apply(entry, command);

					CommitIndex = entry.Index;
					_log.Debug("ApplyCommits --> CommitIndex changed to {0}", entry.Index);

					var tcc = command as TopologyChangeCommand;
					if (tcc != null)
					{
						if (_log.IsInfoEnabled)
						{
							_log.Info("ApplyCommits for TopologyChangedCommand,tcc.Requested.AllVotingPeers = {0}, Name = {1}",
								String.Join(",", tcc.Requested.AllVotingNodes), Name);
						}
						CommitTopologyChange(tcc);
					}

					OnCommitIndexChanged(oldCommitIndex, CommitIndex);
					OnCommitApplied(command);
				}
				catch (Exception e)
				{
					_log.Error("Failed to apply commit", e);
					throw;
				}
			}

			if (StateMachine.SupportSnapshots == false)
				return;

			var commitedEntriesCount = PersistentState.GetCommitedEntriesCount(to);
			if (commitedEntriesCount >= Options.MaxLogLengthBeforeCompaction)
			{
				SnapshotAndTruncateLog(to);
			}
		}

		private void SnapshotAndTruncateLog(long to)
		{
			var task = new Task(() =>
			{
				OnSnapshotCreationStarted();
				try
				{
					var currentTerm = PersistentState.CurrentTerm;
					StateMachine.CreateSnapshot(to, currentTerm);
					PersistentState.MarkSnapshotFor(to, currentTerm,
						Options.MaxLogLengthBeforeCompaction - (Options.MaxLogLengthBeforeCompaction / 8));

					OnSnapshotCreationEnded();
				}
				catch (Exception e)
				{
					_log.Error("Failed to create snapshot", e);
					OnSnapshotCreationError(e);
				}
			});

			if (Interlocked.CompareExchange(ref _snapshottingTask, task, null) != null)
				return;
			task.Start();
		}


		private void CommitTopologyChange(TopologyChangeCommand tcc)
		{
			Interlocked.Exchange(ref _changingTopology, null);
			var shouldRemainInTopology = tcc.Requested.AllVotingNodes.Contains(Name);
			if (shouldRemainInTopology == false)
			{
				_log.Debug("This node is being removed from topology, emptying its AllVotingNodes list and settings its state to None (stopping event loop)");
				Interlocked.Exchange(ref _changingTopology, null);
				CurrentLeader = null;

				SetState(RaftEngineState.None);
			}
			else
			{
				if (_currentTopology.AllVotingNodes.Contains(CurrentLeader) == false)
				{
					CurrentLeader = null;
				}

				if (_log.IsInfoEnabled)
				{
					_log.Info("Finished applying new topology. New AllVotingNodes: {0}", string.Join(",", _currentTopology.AllVotingNodes));
				}
			}

			OnTopologyChanged(tcc);
		}

		public void Dispose()
		{
			_eventLoopCancellationTokenSource.Cancel();
			_eventLoopTask.Wait(500);

			PersistentState.Dispose();

		}

		internal virtual void OnCandidacyAnnounced()
		{
			var handler = ElectionStarted;
			if (handler != null)
			{
				try
				{
					handler();
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		protected virtual void OnStateChanged(RaftEngineState state)
		{
			var handler = StateChanged;
			if (handler != null)
			{
				try
				{
					handler(state);
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		protected virtual void OnStateTimeout()
		{
			var handler = StateTimeout;
			if (handler != null)
			{
				try
				{
					handler();
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		internal virtual void OnEntriesAppended(LogEntry[] logEntries)
		{
			var handler = EntriesAppended;
			if (handler != null)
			{
				try
				{
					handler(logEntries);
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		protected virtual void OnCommitIndexChanged(long oldCommitIndex, long newCommitIndex)
		{
			var handler = CommitIndexChanged;
			if (handler != null)
			{
				try
				{
					handler(oldCommitIndex, newCommitIndex);
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		protected virtual void OnElectedAsLeader()
		{
			var handler = ElectedAsLeader;
			if (handler != null)
			{
				try
				{
					handler();
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		protected virtual void OnTopologyChanged(TopologyChangeCommand cmd)
		{
			var handler = TopologyChanged;
			if (handler != null)
			{
				try
				{
					handler(cmd);
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		protected virtual void OnCommitApplied(Command cmd)
		{
			var handler = CommitApplied;
			if (handler != null)
			{
				try
				{
					handler(cmd);
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		internal virtual void OnTopologyChanging(TopologyChangeCommand tcc)
		{
			var handler = TopologyChanging;
			if (handler != null)
			{
				try
				{
					handler();
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		protected virtual void OnSnapshotCreationStarted()
		{
			var handler = CreatingSnapshot;
			if (handler != null)
			{
				try
				{
					handler();
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		protected virtual void OnSnapshotCreationEnded()
		{
			var handler = CreatedSnapshot;
			if (handler != null)
			{
				try
				{
					handler();
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		protected virtual void OnSnapshotCreationError(Exception e)
		{
			var handler = SnapshotCreationError;
			if (handler != null)
			{
				try
				{
					handler(e);
				}
				catch (Exception ex)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		internal virtual void OnEventsProcessed()
		{
			var handler = EventsProcessed;
			if (handler != null)
			{
				try
				{
					handler();
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		internal virtual void OnSnapshotInstallationStarted()
		{
			var handler = InstallingSnapshot;
			if (handler != null)
			{
				try
				{
					handler();
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}
		internal void OnNewTerm(long term)
		{
			var handler = NewTerm;
			if (handler != null)
			{
				try
				{
					handler(term);
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		internal virtual void OnSnapshotInstallationEnded(long snapshotTerm)
		{
			var handler = SnapshotInstalled;
			if (handler != null)
			{
				try
				{
					handler();
				}
				catch (Exception e)
				{
					_log.Error("Error on raising event", e);
				}
			}
		}

		public override string ToString()
		{
			return string.Format("Name: {0}", Name);
		}

		internal void TopologyChangeStarting(TopologyChangeCommand tcc)
		{
			Interlocked.Exchange(ref _currentTopology, tcc.Requested);
			Interlocked.Exchange(ref _changingTopology, new TaskCompletionSource<object>().Task);
			OnTopologyChanging(tcc);
		}

		internal void RevertTopologyTo(string[] previousPeers)
		{
			_log.Info("Reverting topology because the topology change command was reverted");
			Interlocked.Exchange(ref _changingTopology, null);
			Interlocked.Exchange(ref _currentTopology, new Topology(previousPeers));
			OnTopologyChanged(new TopologyChangeCommand
			{
				Requested = new Topology(previousPeers)
			});
		}

		internal void FinishSteppingDown()
		{
			var steppingDownCompletionSource = _steppingDownCompletionSource;
			if (steppingDownCompletionSource == null)
				return;
			_steppingDownCompletionSource = null;
			steppingDownCompletionSource.TrySetResult(null);
		}

	}
}