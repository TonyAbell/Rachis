﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Rhino.Raft.Interfaces;
using Rhino.Raft.Messages;
using Rhino.Raft.Storage;

namespace Rhino.Raft
{
	public class InMemoryTransport : ITransport
	{
		private readonly ConcurrentDictionary<string,BlockingCollection<MessageEnvelope>> _messageQueue = new ConcurrentDictionary<string, BlockingCollection<MessageEnvelope>>();

		private readonly HashSet<string> _disconnectedNodes = new HashSet<string>();

		public ConcurrentDictionary<string, BlockingCollection<MessageEnvelope>> MessageQueue
		{
			get { return _messageQueue; }
		}

		private void AddToQueue<T>(string dest, T message)
		{
			//if destination is considered disconnected --> drop the message so it never arrives
			if(_disconnectedNodes.Contains(dest))
				return;

			var newMessage = new MessageEnvelope
			{
				Destination = dest,
				Message = message
			};

			_messageQueue.AddOrUpdate(dest,new BlockingCollection<MessageEnvelope> { newMessage }, 
			(destination, envelopes) =>
			{
				envelopes.Add(newMessage);
				return envelopes;
			} );
		}

		public void DisconnectNode(string node)
		{
			_disconnectedNodes.Add(node);
		}

		public void ReconnectNode(string node)
		{
			_disconnectedNodes.RemoveWhere(n => n.Equals(node, StringComparison.InvariantCultureIgnoreCase));
		}

		public bool TryReceiveMessage(string dest, int timeout, CancellationToken cancellationToken, out MessageEnvelope messageEnvelope)
		{
			messageEnvelope = null;
			if (_disconnectedNodes.Contains(dest))
				return false;

			var messageQueue = _messageQueue.GetOrAdd(dest, s => new BlockingCollection<MessageEnvelope>());
			return messageQueue.TryTake(out messageEnvelope, timeout, cancellationToken);
		}

		public void Send(string dest, AppendEntriesRequest req)
		{
			if (_disconnectedNodes.Contains(req.LeaderId))
				return;
			AddToQueue(dest, req);
		}

		public void Send(string dest, RequestVoteRequest req)
		{
			if (_disconnectedNodes.Contains(req.CandidateId))
				return;
			AddToQueue(dest, req);
		}

		public void Send(string dest, AppendEntriesResponse resp)
		{
			AddToQueue(dest, resp);
		}

		public void Send(string dest, RequestVoteResponse resp)
		{
			AddToQueue(dest, resp);
		}

		public void Send(string dest, TopologyChanges req)
		{
			AddToQueue(dest, req);
		}
	}
}
