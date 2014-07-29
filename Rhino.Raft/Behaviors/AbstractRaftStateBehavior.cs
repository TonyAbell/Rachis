using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Rhino.Raft.Interfaces;
using Rhino.Raft.Messages;
using Voron;


namespace Rhino.Raft.Behaviors
{
	public abstract class AbstractRaftStateBehavior : IDisposable
	{
		protected readonly RaftEngine Engine;

		public string Name
		{
			get { return Engine.Name; }
		}

		public int Timeout { get; set; }

		public virtual void HandleMessage(MessageEnvelope envelope)
		{
			RequestVoteRequest requestVoteRequest;
			RequestVoteResponse requestVoteResponse;
			AppendEntriesResponse appendEntriesResponse;
			AppendEntriesRequest appendEntriesRequest;

			if (TryCastMessage(envelope.Message, out requestVoteRequest))
				Handle(envelope.Destination, requestVoteRequest);
			else if (TryCastMessage(envelope.Message, out appendEntriesResponse))
				Handle(envelope.Destination, appendEntriesResponse);
			else if (TryCastMessage(envelope.Message, out appendEntriesRequest))
				Handle(envelope.Destination, appendEntriesRequest);
			else if (TryCastMessage(envelope.Message, out requestVoteResponse))
				Handle(envelope.Destination, requestVoteResponse);
		}

		public virtual void Handle(string destination, RequestVoteResponse resp)
		{
		}

		protected static bool TryCastMessage<T>(object abstractMessage, out T typedMessage)
			where T : class
		{
			typedMessage = abstractMessage as T;
			return typedMessage != null;
		}

		public abstract void HandleTimeout();

		protected AbstractRaftStateBehavior(RaftEngine engine)
		{
			Engine = engine;
		}

		public void Handle(string destination, RequestVoteRequest req)
		{
			Engine.DebugLog.Write("Received RequestVoteRequest, req.CandidateId = {0}, term = {1}", req.CandidateId, req.Term);
			if (req.Term < Engine.PersistentState.CurrentTerm)
			{
				var msg = string.Format("Rejecting request vote because term {0} is lower than current term {1}",
					req.Term, Engine.PersistentState.CurrentTerm);
				Engine.DebugLog.Write(msg);
				Engine.Transport.Send(destination, new RequestVoteResponse
				{
					VoteGranted = false,
					Term = Engine.PersistentState.CurrentTerm,
					Message = msg
				});
				return;
			}

			if (req.Term > Engine.PersistentState.CurrentTerm)
			{
				Engine.DebugLog.Write("{0} -> UpdateCurrentTerm() is called from Abstract Behavior", GetType().Name);
				Engine.UpdateCurrentTerm(req.Term, null);
			}

			if (Engine.PersistentState.VotedFor != null && Engine.PersistentState.VotedFor != req.CandidateId)
			{
				var msg = string.Format("Rejecting request vote because already voted for {0} in term {1}",
					Engine.PersistentState.VotedFor, req.Term);

				Engine.DebugLog.Write(msg);
				Engine.Transport.Send(destination, new RequestVoteResponse
				{
					VoteGranted = false,
					Term = Engine.PersistentState.CurrentTerm,
					Message = msg
				});
				return;
			}

			if (Engine.LogIsUpToDate(req.LastLogTerm, req.LastLogIndex) == false)
			{
				var msg = string.Format("Rejecting request vote because remote log for {0} in not up to date.", req.CandidateId);
				Engine.DebugLog.Write(msg);
				Engine.Transport.Send(destination, new RequestVoteResponse
				{
					VoteGranted = false,
					Term = Engine.PersistentState.CurrentTerm,
					Message = msg
				});
				return;
			}

			Engine.DebugLog.Write("Recording vote for candidate = {0}",req.CandidateId);
			Engine.PersistentState.RecordVoteFor(req.CandidateId);

			Engine.Transport.Send(req.CandidateId, new RequestVoteResponse
			{
				VoteGranted = true,
				Term = Engine.PersistentState.CurrentTerm,
				Message = "Vote granted"
			});
		}

		public virtual void Handle(string destination, AppendEntriesResponse resp)
		{
			// not a leader, no idea what to do with this. Probably an old
			// message from when we were a leader, ignoring.			
		}
	
		public virtual void Handle(string destination, AppendEntriesRequest req)
		{
			if (req.Term < Engine.PersistentState.CurrentTerm)
			{
				var msg = string.Format("Rejecting append entries because msg term {0} is lower than current term {1}",
					req.Term, Engine.PersistentState.CurrentTerm);
				Engine.DebugLog.Write(msg);
				Engine.Transport.Send(destination, new AppendEntriesResponse
				{
					Success = false,
					CurrentTerm = Engine.PersistentState.CurrentTerm,
					Message = msg
				});
				return;
			}
			if (req.Term > Engine.PersistentState.CurrentTerm)
			{
				Engine.UpdateCurrentTerm(req.Term, req.LeaderId);
			}

			Engine.CurrentLeader = req.LeaderId;
			Engine.DebugLog.Write("Set current leader to {0}", req.LeaderId);

			var prevTerm = Engine.PersistentState.TermFor(req.PrevLogIndex) ?? 0;
			if (prevTerm != req.PrevLogTerm)
			{
				string msg = string.Format(
					"Rejecting append entries because msg previous term {0} is not the same as the persisted current term {1} at log index {2}", req.PrevLogTerm, prevTerm, req.PrevLogIndex);
				Engine.DebugLog.Write(msg);
				Engine.Transport.Send(destination, new AppendEntriesResponse
				{
					Success = false,
					CurrentTerm = Engine.PersistentState.CurrentTerm,
					Message = msg,
					LeaderId = req.LeaderId
				});
				return;
			}

			if (req.Entries.Length > 0)
			{
				Engine.DebugLog.Write("Appending log, entries count: {0} (node state = {1})", req.Entries.Length, Engine.State);
				Engine.PersistentState.AppendToLog(req.Entries, removeAllAfter: req.PrevLogIndex);
			}

			string message;
			long lastIndex;

			if (req.Entries.Length > 0)
			{
				lastIndex = req.Entries[req.Entries.Length - 1].Index;

				if (req.LeaderCommit > Engine.CommitIndex)
				{
					long oldCommitIndex = Engine.CommitIndex;

					Engine.CommitIndex = Math.Min(req.LeaderCommit, lastIndex);

					Engine.ApplyCommits(oldCommitIndex, Engine.CommitIndex);
				}
				message = "Applied commits";
			}
			else
			{
				message = "No commits to apply";
				lastIndex = Engine.CommitIndex;
			}

			Engine.Transport.Send(destination, new AppendEntriesResponse
			{
				Success = true,
				CurrentTerm = Engine.PersistentState.CurrentTerm,
				LastLogIndex = lastIndex,
				Message = message 
			});
		}

		public virtual void Dispose()
		{
			
		}
	}
}