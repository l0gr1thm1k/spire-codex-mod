using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

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
        var before = Deck((1, Strike), (2, Strike), (3, ZapAdroit));
        var after = Deck((51, ZapAdroit), (52, Strike), (53, Strike), (54, Defend));

        var r = DeckRemap.Align(before, after);

        Assert.False(r.Exact);
        Assert.Equal(new[] { (3, 51) }, r.Pairs.Select(p => (p.From, p.To)).ToArray());
        Assert.Equal(2, r.Ambiguous);
    }

    [Fact]
    public void AnUpgradeAcrossTheBoundaryIsNotMistakenForTheSameCard()
    {
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
        var before = Deck((1, Strike), (2, ZapAdroit));
        var after = Deck((1, Strike), (99, ZapAdroit));

        var r = DeckRemap.Align(before, after);

        Assert.True(r.Exact);
        Assert.Equal(new[] { (2, 99) }, r.Pairs.Select(p => (p.From, p.To)).ToArray());
    }

    [Fact]
    public void CopiesSharingAKeyMapByTagInsteadOfBeingRefused()
    {
        var before = Tagged((1, Genetic, "f4"), (2, Genetic, "f19"), (3, Strike, null));
        var after = Tagged((51, Genetic, "f19"), (52, Genetic, "f4"), (53, Defend, null));

        var r = DeckRemap.Align(before, after);

        Assert.False(r.Exact);
        Assert.Equal(new[] { (1, 52), (2, 51) }, r.Pairs.OrderBy(p => p.From).Select(p => (p.From, p.To)).ToArray());
        Assert.Equal(1, r.Ambiguous);
    }

    [Fact]
    public void ADisagreeingTagBreaksThePositionPathRatherThanCrossingTwoCopies()
    {
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
        var withUp = """{"t":"deck","cards":[{"c":1,"id":"STRIKE","up":0}]}""";
        var withoutUp = """{"t":"deck","cards":[{"c":1,"id":"STRIKE"}]}""";

        Assert.Single(ReplayJournalScan.DeckEntries(withUp));
        Assert.Empty(ReplayJournalScan.DeckEntries(withoutUp));
    }

    [Fact]
    public void AHeaderStartingDeckReadsAsADeckListingToo()
    {
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
        var shop = """{"t":"shop","cards":[{"c":9,"id":"STRIKE","up":0}]}""";

        Assert.Empty(ReplayJournalScan.DeckEntries(shop));
    }

    [Fact]
    public void TheLiveAndStoredSidesMustSpellTheKeyTheSameWay()
    {
        Assert.Equal(ReplayJournalScan.Key("ZAP", 1, "ADROIT", 1),
                     ReplayJournalScan.DeckEntries(
                         """{"t":"deck","cards":[{"c":7,"id":"ZAP","up":1,"enchantment":"ADROIT","amount":1}]}""")[0].Key);
    }
}
