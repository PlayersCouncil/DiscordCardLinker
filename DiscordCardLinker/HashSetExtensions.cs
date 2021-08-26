
using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordCardLinker
{
	public static class HashSetExtensions
	{
		public static bool AddRange<T>(this HashSet<T> set, IEnumerable<T> items)
		{
			bool allIn = true;
			foreach(var item in items)
			{
				allIn &= set.Add(item);
			}

			return allIn;
		}
	}
}
