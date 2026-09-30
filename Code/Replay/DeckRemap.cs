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
//
// Some cards that share a key are NOT interchangeable, though, and the key alone cannot say so.
// Two Genetic Algorithms differ by accumulated block, two Scythes by accumulated damage, and
// none of that is in (id, up, enchantment, amount). That was two separate faults. It made those
// copies unmappable, which is merely lost lineage, and it made the position fast path below
// willing to cross them, which is a wrong lineage: swap two copies' order across a reload and
// the keys still match position for position. So an entry now carries a second, optional
// discriminator (see Tag), used both to break the fast path when it disagrees and to split a
// key group the fast path did not get to.
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

        // (card id, up, enchantment, amount), pre-joined by the caller so this stays a pure
        // string comparison and both sides are built the same way.
        public string Key { get; }

        // An extra per-instance discriminator, or null for "we have none". Today the floor the
        // card entered the deck on (CardModel.FloorAddedToDeck), which the game writes once on
        // the add and round-trips through the save, so both sides of a reload agree on it.
        //
        // Deliberately NOT the per-instance saved-property values, even though those are the
        // state the key is actually missing. Those change as the card is played, and the stored
        // side of the comparison is whatever the last `deck` listing said, so a card played after
        // that listing would carry a stale value and disagree with itself. One such disagreement
        // abandons the whole position alignment, which today maps identical Defends exactly. A
        // mutable value in an identity key subtracts more than it adds; the values themselves are
        // recorded on the listing instead.
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
        //
        // The tag is checked here too, and that is the point of it: matching keys are what make
        // position look proven, and two copies of one card can match on key while being different
        // physical cards. A disagreeing tag is proof that this position is NOT a correspondence,
        // so it abandons the fast path for the whole listing rather than mapping the pair.
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
                if (kv.Value[0].C != mine[0].C) pairs.Add(new Pair(kv.Value[0].C, mine[0].C));
                continue;
            }
            ambiguous += SplitByTag(kv.Value, mine, pairs);
        }
        return new Result(pairs, ambiguous, false);
    }

    // Second pass for a key more than one card holds. The tag is exactly what tells those copies
    // apart, so try it before giving up: a tag with one holder on each side is a mapping on the
    // same footing as a unique key. Anything else stays refused: an untagged copy, a tag held
    // twice, a tag with no counterpart. Returns the count it refused, which is what `ambiguous`
    // reports.
    //
    // No elimination: when all but one copy in a group map by tag, the leftover pair is tempting
    // but not proven, because the group's membership may itself have changed across the boundary.
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

    // Two tags conflict only when both are present and differ. A null tag means "we do not know",
    // never "different": a listing written before the tag existed carries none at all, and reading
    // its silence as a mismatch would throw away alignments that work today.
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
