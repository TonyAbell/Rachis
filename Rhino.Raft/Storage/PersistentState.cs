﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using Rhino.Raft.Commands;
using Rhino.Raft.Messages;
using Voron;
using Voron.Trees;
using Voron.Util.Conversion;
using Rhino.Raft.Utils;

namespace Rhino.Raft.Storage
{
	/// <summary>
	/// Uses Voron to store the persistent state / log of the raft state machine.
	/// Structure:
	/// 
	/// * $metadata tree - db id, version, current term, voted form, Topology info (like peer lists)
	/// * logs - the actual entry logs
	/// * entry-terms - the term for each entry id
	/// * peers - the data about the peers in the cluster
	/// </summary>
	public class PersistentState : IDisposable
	{
		private const string CurrentVersion = "1.0";
		private const string LogsTreeName = "logs";
		private const string EntryTermsTreeName = "entry-terms";
		private const string MetadataTreeName = "$metadata";

		public Guid DbId { get; private set; }
		public string VotedFor { get; private set; }
		public long CurrentTerm { get; private set; }

		private readonly StorageEnvironment _env;

		private readonly CancellationToken _cancellationToken;
		private bool _isDisposed = false;

		public ICommandSerializer CommandSerializer { get; set; }

		public event Action<Topology> ConfigurationChanged;

		public void SetCurrentTopology(Topology currentTopology, Topology changingTopology)
		{
			if (_isDisposed)
				return;
			using (var tx = _env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var metadata = tx.ReadTree(MetadataTreeName);

				metadata.Delete("changing-config-allvotingpeers");
				if (changingTopology != null)
				{
					metadata.Add("changing-config-allvotingpeers", changingTopology.AllVotingNodes);
				}

				metadata.Add("current-config-allvotingpeers", currentTopology.AllVotingNodes);

				tx.Commit();
			}

			OnConfigurationChanged(currentTopology);
		}

		public Topology GetCurrentConfiguration()
		{
			if (_isDisposed)
				return new Topology(new String[0]);

			return new Topology(ReadStringArray("current-config-allvotingpeers"));
		}

		public Topology GetChangingConfiguration()
		{
			if (_isDisposed)
				return null;

			var peers = ReadStringArray("changing-config-allvotingpeers");
			return peers.Any() ? new Topology(peers) : null;
		}

		private string[] ReadStringArray(string key)
		{
			using (var tx = _env.NewTransaction(TransactionFlags.Read))
			{
				var metadata = tx.ReadTree(MetadataTreeName);
				var allVotingPeers = metadata.Read<string[]>(key);
				var peers = allVotingPeers == null ?
					new string[0] :
					allVotingPeers.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray();

				return peers;
			}
		}


		public PersistentState(StorageEnvironmentOptions options, CancellationToken cancellationToken)
		{
			_cancellationToken = cancellationToken;
			_env = new StorageEnvironment(options);
			InitializeDatabase();
		}

		private void InitializeDatabase()
		{
			using (var tx = _env.NewTransaction(TransactionFlags.ReadWrite))
			{
				_env.CreateTree(tx, LogsTreeName);
				_env.CreateTree(tx, EntryTermsTreeName);

				var metadata = _env.CreateTree(tx, MetadataTreeName);
				var versionReadResult = metadata.Read("version");
				if (versionReadResult == null) // new db
				{
					metadata.Add("version", Encoding.UTF8.GetBytes(CurrentVersion));
					DbId = Guid.NewGuid();
					metadata.Add("db-id", DbId.ToByteArray());
					metadata.Add("current-term", BitConverter.GetBytes(0L));
					metadata.Add("voted-for", Encoding.UTF8.GetBytes(string.Empty));
				}
				else
				{
					var dbVersion = versionReadResult.Reader.ToStringValue();
					if (dbVersion != CurrentVersion)
						throw new InvalidOperationException("Cannot open db because its version is " + dbVersion +
															" but the library expects version " + CurrentVersion);

					int used;
					var bytes = metadata.Read("db-id").Reader.ReadBytes(16, out used).Take(16).ToArray();
					DbId = new Guid(bytes);

					CurrentTerm = metadata.Read("current-term").Reader.ReadLittleEndianInt64();
					var votedFor = metadata.Read("voted-for");
					VotedFor = votedFor.Reader.Length == 0 ? null : votedFor.Reader.ToStringValue();
				}

				tx.Commit();
			}
		}

		public long AppendToLeaderLog(Command command)
		{
			if (_isDisposed)
				return -1;

			using (var tx = _env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var logs = tx.ReadTree(LogsTreeName);
				var terms = tx.ReadTree(EntryTermsTreeName);

				var lastEntry = 0L;
				var lastKey = logs.LastKeyOrDefault();
				if (lastKey != null)
					lastEntry = lastKey.CreateReader().ReadBigEndianInt64();

				var nextEntryId = lastEntry + 1;
				var key = new Slice(EndianBitConverter.Big.GetBytes(nextEntryId));

				command.AssignedIndex = nextEntryId;
				var commandEntry = CommandSerializer.Serialize(command);
				logs.Add(key, commandEntry);
				terms.Add(key, BitConverter.GetBytes(CurrentTerm));

				if (command is TopologyChangeCommand)
				{
					var metadata = tx.ReadTree(MetadataTreeName);
					metadata.Add(key, BitConverter.GetBytes(true));
				}

				tx.Commit();

				return nextEntryId;
			}
		}


		public long? TermFor(long logIndex)
		{
			if (_isDisposed)
				return null;

			using (var tx = _env.NewTransaction(TransactionFlags.Read))
			{
				var terms = tx.ReadTree(EntryTermsTreeName);
				var key = new Slice(EndianBitConverter.Big.GetBytes(logIndex));
				var result = terms.Read(key);
				if (result == null)
					return null;
				var term = result.Reader.ReadLittleEndianInt64();

				tx.Commit();
				return term;
			}
		}

		public LogEntry LastLogEntry()
		{
			if (_isDisposed)
				return null;

			using (var tx = _env.NewTransaction(TransactionFlags.Read))
			{
				var terms = tx.ReadTree(EntryTermsTreeName);
				var logs = tx.ReadTree(LogsTreeName);
				var metadata = tx.ReadTree(MetadataTreeName);

				var lastKey = logs.LastKeyOrDefault();
				if (lastKey == null)
					return new LogEntry();

				var index = lastKey.CreateReader().ReadBigEndianInt64();

				var result = terms.Read(lastKey);

				var term = result.Reader.ReadLittleEndianInt64();
				var isTopologyChangeEntry = ReadIsTopologyChanged(metadata, lastKey);

				return new LogEntry
				{
					Term = term,
					Index = index,
					IsTopologyChange = isTopologyChangeEntry
				};
			}
		}

		public LogEntry GetLogEntry(long index)
		{
			if (_isDisposed)
				return null;

			using (var tx = _env.NewTransaction(TransactionFlags.Read))
			{
				var terms = tx.ReadTree(EntryTermsTreeName);
				var metadata = tx.ReadTree(MetadataTreeName);

				var key = new Slice(EndianBitConverter.Big.GetBytes(index));
				var result = terms.Read(key);
				if (result == null)
					return null;

				var term = result.Reader.ReadLittleEndianInt64();
				var isTopologyChangeEntry = ReadIsTopologyChanged(metadata, key);

				return new LogEntry
				{
					Term = term,
					Index = index,
					IsTopologyChange = isTopologyChangeEntry
				};
			}
		}

		public void RecordVoteFor(string candidateId)
		{
			if (_isDisposed)
				return;

			if (string.IsNullOrEmpty(candidateId))
				throw new ArgumentNullException("candidateId");

			using (var tx = _env.NewTransaction(TransactionFlags.ReadWrite))
			{
				VotedFor = candidateId;
				var metadata = tx.ReadTree(MetadataTreeName);
				metadata.Add("voted-for", Encoding.UTF8.GetBytes(candidateId));

				tx.Commit();
			}
		}

		public void IncrementTermAndVoteFor(string name)
		{
			if (_isDisposed)
				return;

			using (var tx = _env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var metadata = tx.ReadTree(MetadataTreeName);
				CurrentTerm++;
				VotedFor = name;
				metadata.Add("current-term", BitConverter.GetBytes(CurrentTerm));
				metadata.Add("voted-for", Encoding.UTF8.GetBytes(name));
				tx.Commit();
			}
		}

		public void UpdateTermTo(long term)
		{
			if (_isDisposed)
				return;

			using (var tx = _env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var metadata = tx.ReadTree(MetadataTreeName);

				metadata.Add("current-term", BitConverter.GetBytes(term));
				metadata.Add("voted-for", new byte[0]); // clearing who we voted for

				VotedFor = null;
				CurrentTerm = term;

				tx.Commit();
			}
		}

		public LogEntry LastTopologyChangeEntry()
		{
			if (_isDisposed)
				return null;

			using (var tx = _env.NewTransaction(TransactionFlags.Read))
			{
				var logs = tx.ReadTree(LogsTreeName);
				var terms = tx.ReadTree(EntryTermsTreeName);
				var metadata = tx.ReadTree(MetadataTreeName);
				using (var it = logs.Iterate())
				{
					if (it.Seek(Slice.AfterAllKeys) == false) //empty log --> nothing to return
						return null;

					while (_cancellationToken.IsCancellationRequested == false)
					{
						var isTopologyChanged = ReadIsTopologyChanged(metadata, it.CurrentKey);
						if (isTopologyChanged)
						{
							var entryIndex = it.CurrentKey.CreateReader().ReadBigEndianInt64();
							var term = terms.Read(it.CurrentKey).Reader.ReadLittleEndianInt64();

							var entryReader = it.CreateReaderForCurrent();
							var buffer = new byte[entryReader.Length];
							entryReader.Read(buffer, 0, buffer.Length);

							return new LogEntry
							{
								Term = term,
								Data = buffer,
								Index = entryIndex,
								IsTopologyChange = true
							};
						}

						if (it.MovePrev() == false)
							return null;
					}
				}
			}

			return null;
		}

		public IEnumerable<LogEntry> LogEntriesAfter(long index, long stopAfter = long.MaxValue)
		{
			Debug.Assert(index >= 0, "assert index >= 0, index actually is " + index);

			if (_isDisposed)
				yield return null;

			using (var tx = _env.NewTransaction(TransactionFlags.Read))
			{
				var logs = tx.ReadTree(LogsTreeName);
				var terms = tx.ReadTree(EntryTermsTreeName);
				var metadata = tx.ReadTree(MetadataTreeName);

				using (var it = logs.Iterate())
				{
					var key = new Slice(EndianBitConverter.Big.GetBytes(index));
					if (it.Seek(key) == false)
						yield break;

					while (_cancellationToken.IsCancellationRequested == false)
					{
						var entryIndex = it.CurrentKey.CreateReader().ReadBigEndianInt64();
						if (entryIndex > stopAfter)
							yield break;

						var term = terms.Read(it.CurrentKey).Reader.ReadLittleEndianInt64();

						var entryReader = it.CreateReaderForCurrent();
						var buffer = new byte[entryReader.Length];
						entryReader.Read(buffer, 0, buffer.Length);

						yield return new LogEntry
						{
							Term = term,
							Data = buffer,
							Index = entryIndex,
							IsTopologyChange = ReadIsTopologyChanged(metadata, it.CurrentKey)
						};

						if (it.MoveNext() == false)
							yield break;
					}
				}

				tx.Commit();
			}
		}

		public void Dispose()
		{
			if (_env != null && _isDisposed == false)
			{
				_env.Dispose();
				_isDisposed = true;
			}
		}

		public LogEntry GetLastSnapshot()
		{
			using (var tx = _env.NewTransaction(TransactionFlags.Read))
			{
				var metadata = tx.ReadTree(MetadataTreeName);
				return  metadata.Read<LogEntry>("last-snapshot");
			}
		}

		public void MarkSnapshotFor(long lastCommittedIndex, int maxNumberOfItemsToRemove)
		{
			using (var tx = _env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var logs = tx.ReadTree(LogsTreeName);
				var terms = tx.ReadTree(EntryTermsTreeName);
				var metadata = tx.ReadTree(MetadataTreeName);
				var termForLastCommittedIndex = terms.Read(new Slice(EndianBitConverter.Big.GetBytes(lastCommittedIndex)));
				metadata.Add("last-snapshot", new LogEntry
				{
					Index = lastCommittedIndex,
					Term = termForLastCommittedIndex.Reader.ReadLittleEndianInt64()
				});

				using (var it = logs.Iterate())
				{
					it.MaxKey = new Slice(EndianBitConverter.Big.GetBytes(lastCommittedIndex + 1));
					if (it.Seek(Slice.BeforeAllKeys) == false)
						return;
					do
					{
						terms.Delete(it.CurrentKey);
						maxNumberOfItemsToRemove--;
					} while (it.DeleteCurrentAndMoveNext() && maxNumberOfItemsToRemove >= 0);
				}
				tx.Commit();
			}
		}

		public void AppendToLog(IEnumerable<LogEntry> entries, long removeAllAfter)
		{
			if (_isDisposed)
				return;

			using (var tx = _env.NewTransaction(TransactionFlags.ReadWrite))
			{
				var logs = tx.ReadTree(LogsTreeName);
				var terms = tx.ReadTree(EntryTermsTreeName);

				using (var it = logs.Iterate())
				{
					if (it.Seek(new Slice(EndianBitConverter.Big.GetBytes(removeAllAfter))) &&
						it.MoveNext())
					{
						do
						{
							terms.Delete(it.CurrentKey);
						} while (it.DeleteCurrentAndMoveNext());
					}
				}

				foreach (var entry in entries)
				{
					var key = new Slice(EndianBitConverter.Big.GetBytes(entry.Index));
					logs.Add(key, entry.Data);
					terms.Add(key, new Slice(BitConverter.GetBytes(entry.Term)));
				}

				tx.Commit();
			}
		}
		protected virtual void OnConfigurationChanged(Topology newTopology)
		{
			var handler = ConfigurationChanged;
			if (handler != null) handler(newTopology);
		}

		private static bool ReadIsTopologyChanged(Tree metadata, Slice lastKey)
		{
			int used;
			var readResult = metadata.Read(lastKey);
			if (readResult == null)
				return false; //non-existing key, this means definitely not a topology change command

			var bytes = readResult.Reader.ReadBytes(sizeof(bool), out used);
			return BitConverter.ToBoolean(bytes, 0);
		}

		public long GetCommitedEntriesCount(long lastCommittedEntry)
		{
			using (var tx = _env.NewTransaction(TransactionFlags.Read))
			{
				var logs = tx.ReadTree(LogsTreeName);

				using (var it = logs.Iterate())
				{
					var lastEntryIndex = it.Seek(Slice.AfterAllKeys) == false ?
						0 : 
						it.CurrentKey.CreateReader().ReadBigEndianInt64();
					return logs.State.EntriesCount - Math.Max(0, (lastEntryIndex - lastCommittedEntry));
				}
			}
		}
	}
}
