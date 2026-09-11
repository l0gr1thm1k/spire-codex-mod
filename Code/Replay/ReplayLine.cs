using System.Collections.Generic;

namespace SpireCodex.Replay;

// One line of the replay journal. Built on the game thread by a hook, serialized on the
// writer thread (see ReplayJournal), so nothing here touches JSON or disk.
//
// Fields is a plain dictionary rather than a typed record per line kind because the line
// kinds are heterogeneous and still moving; a dictionary keeps the hooks readable and the
// writer generic. The allocation cost is irrelevant at this rate: a full run emits ~3,300
// lines over ~an hour, and even a busy AoE turn is a few dozen. The thing that actually
// hitched the game in this codebase was synchronous serialization and disk I/O on the main
// thread (see SnapshotWriter), and both of those happen on the writer thread here.
//
// Seq and Ms are stamped by the journal at enqueue time, not by the caller, so ordering is
// decided at one place and can't drift between hooks.
public sealed class ReplayLine
{
    public string Kind { get; }
    public Dictionary<string, object?> Fields { get; }

    public long Seq { get; set; }
    public long Ms { get; set; }

    public ReplayLine(string kind, int capacity = 8)
    {
        Kind = kind;
        Fields = new Dictionary<string, object?>(capacity);
    }

    // Fluent so a hook reads as one expression. Null values are dropped rather than written
    // as JSON null: the consumer treats a missing key and an explicit null the same, and
    // dropping them is a meaningful slice of the file at this line count.
    public ReplayLine Set(string key, object? value)
    {
        if (value != null) Fields[key] = value;
        return this;
    }

    // Explicit false/0 matter for the analytics columns (presented, selectable, chosen,
    // applied), so those go through Set too — bool and int box, but never to null.
    public ReplayLine SetFlag(string key, bool value)
    {
        Fields[key] = value;
        return this;
    }
}
