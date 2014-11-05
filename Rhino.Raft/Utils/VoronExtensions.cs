﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Voron;
using Voron.Impl.Journal;
using Voron.Trees;

namespace Rhino.Raft.Utils
{
	public static class VoronExtensions
	{
		public static void Add<T>(this Tree tree, Slice key, T @object, ushort? version = null)			
			where T : class
		{
			tree.Add(key, JsonConvert.SerializeObject(@object),version);
		}

		public static T Read<T>(this Tree tree, Slice key)
			where T : class
		{
			var readResult = tree.Read(key);
			if (readResult == null || readResult.Version == 0)
				return null;

			var deserializeObject = (JToken)JsonConvert.DeserializeObject(readResult.Reader.ToStringValue());
			return deserializeObject.ToObject<T>();
		}
	}
}
