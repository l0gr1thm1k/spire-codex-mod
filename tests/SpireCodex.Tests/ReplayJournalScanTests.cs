using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

// The high-water scan decides which ids a reloaded session may mint. Get it wrong and the
// second session reissues ids the first one already used, which reads downstream as one card
// moving rather than two cards existing — a WRONG value, not a missing one, and invisible in
// the file because nothing marks the session seam.
//
// The failure mode these guard is the needle: `"c":` must not also match `"deck_c":`, and
// `"s":` must not match `"ms":` or `"stars_paid":`. Both near-misses appear on ordinary lines
// thousands of times per run, so a careless match reads high (harmless) or a careless
// anchor reads low (reissues ids) with nothing to notice it.
public sealed class ReplayJournalScanTests
{
    private static string WriteJournal(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scan-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void RecoversTheHighestIdOfEachKind()
    {
        var path = WriteJournal(
            """{"t":"header","s":0,"starting_deck":[{"c":1,"id":"STRIKE"},{"c":2,"id":"DEFEND"}]}""",
            """{"t":"draw","s":1,"c":9,"deck_c":2}""",
            """{"t":"decision","s":2,"decision_id":4}""",
            """{"t":"transform","s":3,"from_c":9,"to_c":31}""",
            """{"t":"pick","s":4,"instance_id":27,"decision_id":3}""");

        var hw = ReplayJournalScan.HighWaterOf(path);
        File.Delete(path);

        Assert.Equal(4, hw.Seq);
        Assert.Equal(31, hw.Card);     // to_c, not the last c seen
        Assert.Equal(4, hw.Decision);  // the max, not the last
    }

    [Fact]
    public void DoesNotConfuseAKeyWithOneThatEndsInIt()
    {
        // deck_c is far larger than any real c, and stars_paid/ms far larger than any s. If the
        // needle ignored the opening quote these would be read as c and s.
        var path = WriteJournal(
            """{"t":"play","s":7,"ms":999999,"c":3,"deck_c":800,"stars_paid":500,"cost_paid":2}""");

        var hw = ReplayJournalScan.HighWaterOf(path);
        File.Delete(path);

        Assert.Equal(7, hw.Seq);
        Assert.Equal(800, hw.Card); // deck_c IS a card id and counts; ms and stars_paid are not
    }

    [Fact]
    public void ReadsTheWholeFileBecauseTheMaximaAreNotAtTheTail()
    {
        // The real shape: ids are minted in ascending order, then the run's last thousand lines
        // replay low-numbered starters. A tail-only read reports 4 and the next session reissues
        // everything above it.
        var lines = new List<string> { """{"t":"acquire","s":0,"c":742,"decision_id":88}""" };
        for (var i = 1; i < 4000; i++)
            lines.Add($$"""{"t":"play","s":{{i}},"c":4,"deck_c":2}""");

        var path = WriteJournal(lines.ToArray());
        var hw = ReplayJournalScan.HighWaterOf(path);
        File.Delete(path);

        Assert.Equal(742, hw.Card);
        Assert.Equal(88, hw.Decision);
    }

    [Fact]
    public void ANewFileYieldsNoMarksSoAFreshRunStartsAtOne()
    {
        var hw = ReplayJournalScan.HighWaterOf(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.jsonl"));

        Assert.Equal(-1, hw.Seq);   // matches LastSequence's "new or unreadable"
        Assert.Equal(0, hw.Card);   // ResumeFrom(0) is a no-op, so ids still begin at 1
        Assert.Equal(0, hw.Decision);
    }

    [Fact]
    public void SequenceStillMatchesTheTailReadItReplaced()
    {
        var path = WriteJournal(
            """{"t":"header","s":0}""",
            """{"t":"draw","s":1,"c":5}""",
            """{"t":"end","s":2}""");

        var hw = ReplayJournalScan.HighWaterOf(path);
        var tail = ReplayJournalScan.LastSequence(path);
        File.Delete(path);

        Assert.Equal(tail, hw.Seq);
    }
}
