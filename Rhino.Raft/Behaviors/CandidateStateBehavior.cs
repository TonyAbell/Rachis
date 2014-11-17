﻿// -----------------------------------------------------------------------
//  <copyright file="CandidateStateBehavior.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Raft.Messages;

namespace Rhino.Raft.Behaviors
{
	public class CandidateStateBehavior : AbstractRaftStateBehavior
    {
		private readonly HashSet<string> _votesForMyLeadership = new HashSet<string>();
		private readonly Random _random;

		public CandidateStateBehavior(RaftEngine engine) : base(engine)
		{
			_random = new Random((int) (engine.Name.GetHashCode() + DateTime.UtcNow.Ticks));
			Timeout = _random.Next(engine.MessageTimeout / 2, engine.MessageTimeout);
			VoteForSelf();
	    }

		private void VoteForSelf()
		{
			Engine.DebugLog.Write("Voting for myself in term {0}", Engine.PersistentState.CurrentTerm);
			Handle(Engine.Name,
				new RequestVoteResponse
				{
					Term = Engine.PersistentState.CurrentTerm,
					VoteGranted = true,
					Message = String.Format("{0} -> Voting for myself", Engine.Name),
					From = Engine.Name
				});
		}

		public override void HandleTimeout()
	    {
			Engine.DebugLog.Write("Timeout ({1:#,#;;0} ms) for elections in term {0}", Engine.PersistentState.CurrentTerm,
				  Timeout);

			LastHeartbeatTime = DateTime.UtcNow;
			Timeout = _random.Next(Engine.MessageTimeout / 2, Engine.MessageTimeout); 
			_votesForMyLeadership.Clear();
			Engine.AnnounceCandidacy();
			VoteForSelf();
	    }

		public override RaftEngineState State
		{
			get { return RaftEngineState.Candidate; }
		}

		public override void Handle(string source,RequestVoteResponse resp)
		{
			if (resp.Term > Engine.PersistentState.CurrentTerm)
			{
				Engine.DebugLog.Write("CandidateStateBehavior -> UpdateCurrentTerm called");
				Engine.UpdateCurrentTerm(resp.Term, null);
				return;
			}

			if (resp.VoteGranted == false)
			{
				Engine.DebugLog.Write("Vote rejected from {0}", resp.From);
				return;
			}

			if(Engine.ContainedInAllVotingNodes(resp.From) == false) //precaution
			{
				Engine.DebugLog.Write("Vote acepted from {0}, which isn't in our topology", resp.From);
				return;
			}

			_votesForMyLeadership.Add(resp.From);
			Engine.DebugLog.Write("Adding to my votes: {0} (current votes: {1})", resp.From, string.Join(", ", _votesForMyLeadership));

			if (Engine.CurrentTopology.HasQuorum(_votesForMyLeadership) == false)
			{
				Engine.DebugLog.Write("Not enough votes for leadership, votes = {0}", _votesForMyLeadership.Any() ? string.Join(", ", _votesForMyLeadership) : "empty");
				return;
			}
			
			Engine.SetState(RaftEngineState.Leader);
			Engine.DebugLog.Write("Selected as leader, term = {0}", resp.Term);
		}

    }
}