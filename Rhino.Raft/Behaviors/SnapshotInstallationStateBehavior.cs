﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Rhino.Raft.Messages;

namespace Rhino.Raft.Behaviors
{
	public class SnapshotInstallationStateBehavior : AbstractRaftStateBehavior
	{
		private readonly Random _random;

		private Task _installingSnapshot;

		public SnapshotInstallationStateBehavior(RaftEngine engine) : base(engine)
		{
			_random = new Random((int)(engine.Name.GetHashCode() + DateTime.UtcNow.Ticks));
			Timeout = _random.Next(engine.MessageTimeout / 2, engine.MessageTimeout);
		}

		public override RaftEngineState State
		{
			get { return RaftEngineState.SnapshotInstallation; }
		}

		public override void Handle(string destination, CanInstallSnapshotRequest req)
		{
			if (_installingSnapshot == null)
			{
				base.Handle(destination, req);
				return;
			}
			Engine.Transport.Send(req.From, new CanInstallSnapshotResponse
			{
				From = Engine.Name,
				IsCurrentlyInstalling = true,
				Message = "The node is in the process of installing a snapshot",
				Success = false
			});			
		}

		public override void Handle(string destination, InstallSnapshotRequest req, Stream stream)
		{
			if (_installingSnapshot != null)
			{
				base.Handle(destination, req, stream);
				return;
			}

			var lastLogEntry = Engine.PersistentState.LastLogEntry();
			if (req.Term < lastLogEntry.Term)
			{
				stream.Dispose();

				Engine.Transport.Send(req.From, new InstallSnapshotResponse
				{
					From = Engine.Name,
					CurrentTerm = lastLogEntry.Term,
					LastLogIndex = lastLogEntry.Index,
					Message = "Term " + req.Term + " is older than last term in the log " + lastLogEntry.Term + " so the snapshot was rejected",
					Success = false
				});
				return;
			}
				
			Engine.DebugLog.Write("Received InstallSnapshotRequest from {0} until term {1} / {2}", req.From, req.LastIncludedTerm, req.LastIncludedIndex);

			Engine.OnSnapshotInstallationStarted();
			
			// this can be a long running task
			_installingSnapshot = Task.Run(() =>
			{
				try
				{
					Engine.StateMachine.ApplySnapshot(req.LastIncludedTerm, req.LastIncludedIndex, stream);
					Engine.PersistentState.MarkSnapshotFor(req.LastIncludedIndex, req.LastIncludedTerm, int.MaxValue);
				}
				catch (Exception e)
				{
					Engine.DebugLog.Write("Failed to install snapshot because {0}", e);
					Engine.Transport.Execute(Engine.Name, () =>
					{
						_installingSnapshot = null;
					});
				}

				// we are doing it this way to ensure that we are single threaded
				Engine.Transport.Execute(Engine.Name, () =>
				{
					Engine.UpdateCurrentTerm(req.Term, req.LeaderId);
					Engine.OnSnapshotInstallationEnded(req.Term);

					Engine.Transport.Send(req.From, new InstallSnapshotResponse
					{
						From = Engine.Name,
						CurrentTerm = lastLogEntry.Term,
						LastLogIndex = lastLogEntry.Index,
						Success = true
					});
				});
			});
		}

		public override void Handle(string destination, AppendEntriesRequest req)
		{
			var lastLogEntry = Engine.PersistentState.LastLogEntry();
			Engine.Transport.Send(req.From,
				new AppendEntriesResponse
				{
					From = Engine.Name,
					CurrentTerm = Engine.PersistentState.CurrentTerm,
					LastLogIndex = lastLogEntry.Index,
					LeaderId = Engine.CurrentLeader,
					Message = "I am in the process of receiving a snapshot, so I cannot accept new entries at the moment",
					Source = destination,
					Success = false
				});
		}


		public override void HandleTimeout()
		{
			Timeout = _random.Next(Engine.MessageTimeout / 2, Engine.MessageTimeout);
			LastHeartbeatTime = DateTime.UtcNow;// avoid busy loop while waiting for the snapshot
			Engine.DebugLog.Write("Received timeout during installation of a snapshot. Doing nothing, since the node should finish receiving snapshot before it could change into candidate");
			//do nothing during timeout --> this behavior will go on until the snapshot installation is finished
		}

		public override void Dispose()
		{
			base.Dispose();
		}
	}
}
