using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

// The remap row asserts "pre-reload card N continued as card M". A wrong pair here is the
// worst output this mod can produce: it does not look like missing data, it looks like
// lineage, and it would put an enchantment or an upgrade on a card that never had one.
//
// So these tests are mostly about what it must REFUSE to say.
public sealed class DeckRemapTests
{
    private static DeckRemap.Entry E(int c, string key, string? tag = null) => new(c, key, tag);

    private static List<DeckRemap.Entry> Deck(params (int C, string Key)[] rows)
        => rows.Select(r => E(r.C, r.Key)).ToList();

    private static List<DeckRemap.Entry> Tagged(params (int C, string Key, string? Tag)[] rows)
        => rows.Select(r => E(r.C, r.Key, r.Tag)).ToList();

    private const string Strike = "STRIKE|0||0";
    private const string Defend = "DEFEND|0||0";
    private const string ZapAdroit = "ZAP|1|ADROIT|1";
    private const string Genetic = "GENETIC_ALGORITHM|0||0";

    [Fact]
    public void SameKeysInTheSameOrderMapEveryCopyIncludingIdenticalOnes()
    {
        // The self-verifying path: we have both listings in hand and they agree position for
        // position, so position IS the correspondence — no assumption about deck ordering.
        var before = Deck((1, Strike), (2, Strike), (3, Defend), (4, ZapAdroit));
        var after = Deck((51, Strike), (52, Strike), (53, Defend), (54, ZapAdroit));

        var r = DeckRemap.Align(before, after);

        Assert.True(r.Exact);
        Assert.Equal(0, r.Ambiguous);
        Assert.Equal(new[] { (1, 51), (2, 52), (3, 53), (4, 54) },
                     r.Pairs.Select(p => (p.From, p.To)).ToArray());
    }

    [Fact]
    public void ReorderedIdenticalCopiesAreRefusedRatherThanGuessed()
    {
        // The deck changed shape, so the ordered path is off. Two Strikes are then
        // indistinguishable and MUST NOT be paired: pairing them by position here would be a
        // coin flip presented as a fact. The Zap is unique and still maps.
        var before = Deck((1, Strike), (2, Strike), (3, ZapAdroit));
        var after = Deck((51, ZapAdroit), (52, Strike), (53, Strike), (54, Defend));

        var r = DeckRemap.Align(before, after);

        Assert.False(r.Exact);
        Assert.Equal(new[] { (3, 51) }, r.Pairs.Select(p => (p.From, p.To)).ToArray());
        Assert.Equal(2, r.Ambiguous); // both Strikes, reported not guessed
    }

    [Fact]
    public void AnUpgradeAcrossTheBoundaryIsNotMistakenForTheSameCard()
    {
        // A save-scummed upgrade: the card exists on both sides under DIFFERENT keys. Claiming
        // 1 -> 51 would assert lineage through a state change we cannot actually observe here.
        var before = Deck((1, Strike), (2, Defend));
        var after = Deck((51, "STRIKE|1||0"), (52, Defend));

        var r = DeckRemap.Align(before, after);

        Assert.False(r.Exact);
        Assert.Equal(new[] { (2, 52) }, r.Pairs.Select(p => (p.From, p.To)).ToArray());
        Assert.Equal(1, r.Ambiguous);
    }

    [Fact]
    public void AnUnchangedIdIsNotEmittedAsAPair()
    {
        // Ids that did not move carry no information and would bloat every remap row.
        var before = Deck((1, Strike), (2, ZapAdroit));
        var after = Deck((1, Strike), (99, ZapAdroit));

        var r = DeckRemap.Align(before, after);

        Assert.True(r.Exact);
        Assert.Equal(new[] { (2, 99) }, r.Pairs.Select(p => (p.From, p.To)).ToArray());
    }

    [Fact]
    public void CopiesSharingAKeyMapByTagInsteadOfBeingRefused()
    {
        // Two Genetic Algorithms are the case the key cannot see: same id, same upgrade, same
        // enchantment, and completely different accumulated block. The floor each entered the
        // deck on tells them apart, so both map where before both were refused.
        var before = Tagged((1, Genetic, "f4"), (2, Genetic, "f19"), (3, Strike, null));
        var after = Tagged((51, Genetic, "f19"), (52, Genetic, "f4"), (53, Defend, null));

        var r = DeckRemap.Align(before, after);

        Assert.False(r.Exact);
        Assert.Equal(new[] { (1, 52), (2, 51) }, r.Pairs.OrderBy(p => p.From).Select(p => (p.From, p.To)).ToArray());
        Assert.Equal(1, r.Ambiguous); // the Strike, which has no counterpart at all
    }

    [Fact]
    public void ADisagreeingTagBreaksThePositionPathRatherThanCrossingTwoCopies()
    {
        // The dangerous case. Both listings hold the same keys in the same order, so position
        // looks proven, but the two Genetic Algorithms swapped places, and mapping 1 -> 51 would
        // hand one copy the other's accumulated block. The tag is the only thing that knows.
        var before = Tagged((1, Genetic, "f4"), (2, Genetic, "f19"));
        var after = Tagged((51, Genetic, "f19"), (52, Genetic, "f4"));

        var r = DeckRemap.Align(before, after);

        Assert.False(r.Exact);
        Assert.Equal(new[] { (1, 52), (2, 51) }, r.Pairs.OrderBy(p => p.From).Select(p => (p.From, p.To)).ToArray());
        Assert.Equal(0, r.Ambiguous);
    }

    [Fact]
    public void AnUntaggedSideStillAlignsExactlyByPosition()
    {
        // Backward compatibility, and the reason a null tag means "unknown" and never
        // "different": a listing written before `added_floor` existed carries no tags, and
        // treating its silence as a mismatch would cost it the position alignment it has today.
        var before = Deck((1, Strike), (2, Strike), (3, Genetic));
        var after = Tagged((51, Strike, "f1"), (52, Strike, "f1"), (53, Genetic, "f4"));

        var r = DeckRemap.Align(before, after);

        Assert.True(r.Exact);
        Assert.Equal(0, r.Ambiguous);
        Assert.Equal(3, r.Pairs.Count);
    }

    [Fact]
    public void AnUntaggedCopyInAnAmbiguousGroupIsStillRefused()
    {
        // Half a tag is not a mapping. The f4 copy maps; the untagged one cannot be deduced from
        // it even though only one candidate is left, because the group's membership may itself
        // have changed across the boundary.
        //
        // The order differs so the position path is off and this reaches the grouping path. When
        // position DOES hold, an untagged copy still maps by position, exactly as an untagged
        // identical Defend always has: the tag only ever adds a refusal the key could not make,
        // it never takes an alignment away.
        var before = Tagged((1, Genetic, "f4"), (2, Genetic, null), (3, Strike, null));
        var after = Tagged((51, Strike, null), (52, Genetic, "f4"), (53, Genetic, "f19"));

        var r = DeckRemap.Align(before, after);

        Assert.False(r.Exact);
        Assert.Equal(new[] { (1, 52), (3, 51) },
                     r.Pairs.OrderBy(p => p.From).Select(p => (p.From, p.To)).ToArray());
        Assert.Equal(1, r.Ambiguous);
    }

    [Fact]
    public void AddedFloorRoundTripsIntoTheTagAndIsOptional()
    {
        // The stored side has to spell the tag the way the live side does, and a listing without
        // the field has to stay readable rather than being refused the way a missing `up` is.
        var withFloor = """{"t":"deck","cards":[{"c":1,"id":"GENETIC_ALGORITHM","up":0,"added_floor":4}]}""";
        var withoutFloor = """{"t":"deck","cards":[{"c":1,"id":"GENETIC_ALGORITHM","up":0}]}""";

        Assert.Equal(ReplayJournalScan.Tag(4), ReplayJournalScan.DeckEntries(withFloor)[0].Tag);
        Assert.Single(ReplayJournalScan.DeckEntries(withoutFloor));
        Assert.Null(ReplayJournalScan.DeckEntries(withoutFloor)[0].Tag);
        Assert.Null(ReplayJournalScan.Tag(null));
    }

    [Fact]
    public void ALegacyListingWithoutUpIsRefusedWholesale()
    {
        // A pre-`up` capture keys on different fields than the live side, so aligning the two
        // would mis-pair rather than fail. Half a listing is worse than none.
        var withUp = """{"t":"deck","cards":[{"c":1,"id":"STRIKE","up":0}]}""";
        var withoutUp = """{"t":"deck","cards":[{"c":1,"id":"STRIKE"}]}""";

        Assert.Single(ReplayJournalScan.DeckEntries(withUp));
        Assert.Empty(ReplayJournalScan.DeckEntries(withoutUp));
    }

    [Fact]
    public void AHeaderStartingDeckReadsAsADeckListingToo()
    {
        // A reload right after run start has no `deck` row yet, only the header's.
        var header = """{"t":"header","starting_deck":[{"c":1,"id":"STRIKE","up":0},{"c":2,"id":"ZAP","up":1,"enchantment":"ADROIT","amount":1}]}""";

        var rows = ReplayJournalScan.DeckEntries(header);

        Assert.Equal(2, rows.Count);
        Assert.Equal(ReplayJournalScan.Key("ZAP", 1, "ADROIT", 1), rows[1].Key);
    }

    [Fact]
    public void GarbageAndAbsenceYieldNothingRatherThanThrowing()
    {
        Assert.Empty(ReplayJournalScan.DeckEntries(null));
        Assert.Empty(ReplayJournalScan.DeckEntries(""));
        Assert.Empty(ReplayJournalScan.DeckEntries("{not json"));
        Assert.Empty(ReplayJournalScan.DeckEntries("""{"t":"turn","n":3}"""));
    }

    [Fact]
    public void AShopShelfIsNotADeck()
    {
        // The shop row carries its stock under `cards`, the same key a deck listing uses. Read
        // as a deck it would remap real instance ids onto merchandise.
        var shop = """{"t":"shop","cards":[{"c":9,"id":"STRIKE","up":0}]}""";

        Assert.Empty(ReplayJournalScan.DeckEntries(shop));
    }

    [Fact]
    public void TheLiveAndStoredSidesMustSpellTheKeyTheSameWay()
    {
        // Two spellings of one key align nothing, silently. This pins the format both sides
        // call into.
        Assert.Equal(ReplayJournalScan.Key("ZAP", 1, "ADROIT", 1),
                     ReplayJournalScan.DeckEntries(
                         """{"t":"deck","cards":[{"c":7,"id":"ZAP","up":1,"enchantment":"ADROIT","amount":1}]}""")[0].Key);
    }
}
