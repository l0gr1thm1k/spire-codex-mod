using System.Collections.Generic;

namespace SpireCodex.Replay;

// Bridging card-instance ids across a reload.
//
// A reload rebuilds every CardModel from the save, so CardInstances' table — keyed on object
// identity — is gone, and the game has no per-instance id of its own to re-key on (verified
// against the shipped assembly: AbstractModel.Id is a ModelId, i.e. the card TYPE;
// CanonicalInstance points at the prototype; the sorting ids are catalog keys). So a resumed
// session renumbers the deck, and without a bridge the upgraded card you were holding is one
// id before the reload and a different id after, with nothing saying they are the same card.
//
// What IS available is two listings of the same deck: the last `deck` row the previous session
// wrote, and the deck as it stands at the resumed header. Aligning those two recovers the
// mapping — for the cards it can distinguish.
//
// The distinguishing key is (card id, upgrade level, enchantment, amount). Four un-upgraded
// Defends share a key and are genuinely interchangeable; an Adroit Zap does not, and that is
// the case worth recovering.
public static class DeckRemap
{
    public readonly struct Entry
    {
        public Entry(int c, string key)
        {
            C = c;
            Key = key;
        }

        public int C { get; }

        // (card id, up, enchantment, amount), pre-joined by the caller so this stays a pure
        // string comparison and both sides are built the same way.
        public string Key { get; }
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

        // Cards whose key is shared by more than one copy on either side, so no mapping can be
        // asserted for them. Reported rather than guessed: a wrong lineage is worse than a
        // missing one, and a consumer that sees `ambiguous: 4` knows not to trust a name match
        // it might otherwise make for itself.
        public int Ambiguous { get; }

        // True when the two listings held the same keys in the same order, which makes position
        // a PROVEN correspondence for this pair of listings rather than an assumption about
        // deck ordering in general.
        public bool Exact { get; }
    }

    public static Result Align(List<Entry> before, List<Entry> after)
    {
        var pairs = new List<Pair>();

        // The self-verifying fast path. Whether the game preserves deck order across a save
        // load is not something we can establish in the abstract — so instead of assuming it,
        // check it here against the two listings actually in hand. Same length, same keys, same
        // order means each position describes the same card in both, and every copy maps
        // exactly, identical Defends included. When it does not hold we fall through rather
        // than trusting position anyway.
        if (before.Count == after.Count && before.Count > 0)
        {
            var aligned = true;
            for (var i = 0; i < before.Count; i++)
            {
                if (before[i].Key == after[i].Key) continue;
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

        // Otherwise map only what is unambiguous: a key held by exactly one card on each side.
        // A deck that changed across the boundary (a save-scummed reward, a rolled-back
        // removal) lands here, and so does any deck whose order moved.
        var lhs = Group(before);
        var rhs = Group(after);
        var ambiguous = 0;
        foreach (var kv in lhs)
        {
            var mine = rhs.TryGetValue(kv.Key, out var found) ? found : null;
            if (kv.Value.Count == 1 && mine != null && mine.Count == 1)
            {
                if (kv.Value[0] != mine[0]) pairs.Add(new Pair(kv.Value[0], mine[0]));
                continue;
            }
            ambiguous += kv.Value.Count;
        }
        return new Result(pairs, ambiguous, false);
    }

    private static Dictionary<string, List<int>> Group(List<Entry> rows)
    {
        var by = new Dictionary<string, List<int>>();
        foreach (var row in rows)
        {
            if (!by.TryGetValue(row.Key, out var list)) by[row.Key] = list = new List<int>();
            list.Add(row.C);
        }
        return by;
    }
}
