using System.Collections.Generic;

namespace SpireCodex.Replay;

public static class DeckRemap
{
    public readonly struct Entry
    {
        public Entry(int c, string key, string? tag = null)
        {
            C = c;
            Key = key;
            Tag = tag;
        }

        public int C { get; }

        public string Key { get; }

        public string? Tag { get; }
    }

    public readonly struct Pair
    {
        public Pair(int from, int to)
        {
            From = from;
            To = to;
        }

        public int From { get; }
        public int To { get; }
    }

    public readonly struct Result
    {
        public Result(List<Pair> pairs, int ambiguous, bool exact)
        {
            Pairs = pairs;
            Ambiguous = ambiguous;
            Exact = exact;
        }

        public List<Pair> Pairs { get; }

        public int Ambiguous { get; }

        public bool Exact { get; }
    }

    public static Result Align(List<Entry> before, List<Entry> after)
    {
        var pairs = new List<Pair>();

        if (before.Count == after.Count && before.Count > 0)
        {
            var aligned = true;
            for (var i = 0; i < before.Count; i++)
            {
                if (before[i].Key == after[i].Key && TagsAgree(before[i].Tag, after[i].Tag)) continue;
                aligned = false;
                break;
            }
            if (aligned)
            {
                for (var i = 0; i < before.Count; i++)
                    if (before[i].C != after[i].C)
                        pairs.Add(new Pair(before[i].C, after[i].C));
                return new Result(pairs, 0, true);
            }
        }

        var lhs = Group(before);
        var rhs = Group(after);
        var ambiguous = 0;
        foreach (var kv in lhs)
        {
            var mine = rhs.TryGetValue(kv.Key, out var found) ? found : null;
            if (kv.Value.Count == 1 && mine != null && mine.Count == 1)
            {
                if (kv.Value[0].C != mine[0].C) pairs.Add(new Pair(kv.Value[0].C, mine[0].C));
                continue;
            }
            ambiguous += SplitByTag(kv.Value, mine, pairs);
        }
        return new Result(pairs, ambiguous, false);
    }

    private static int SplitByTag(List<Entry> lhs, List<Entry>? rhs, List<Pair> pairs)
    {
        if (rhs == null || rhs.Count == 0) return lhs.Count;
        var theirs = new Dictionary<string, List<int>>();
        foreach (var row in rhs)
        {
            if (row.Tag == null) continue;
            if (!theirs.TryGetValue(row.Tag, out var list)) theirs[row.Tag] = list = new List<int>();
            list.Add(row.C);
        }
        var held = new Dictionary<string, int>();
        foreach (var row in lhs)
            if (row.Tag != null)
                held[row.Tag] = held.TryGetValue(row.Tag, out var n) ? n + 1 : 1;

        var refused = 0;
        foreach (var row in lhs)
        {
            if (row.Tag != null && held[row.Tag] == 1
                && theirs.TryGetValue(row.Tag, out var match) && match.Count == 1)
            {
                if (row.C != match[0]) pairs.Add(new Pair(row.C, match[0]));
                continue;
            }
            refused++;
        }
        return refused;
    }

    private static bool TagsAgree(string? a, string? b) => a == null || b == null || a == b;

    private static Dictionary<string, List<Entry>> Group(List<Entry> rows)
    {
        var by = new Dictionary<string, List<Entry>>();
        foreach (var row in rows)
        {
            if (!by.TryGetValue(row.Key, out var list)) by[row.Key] = list = new List<Entry>();
            list.Add(row);
        }
        return by;
    }
}
