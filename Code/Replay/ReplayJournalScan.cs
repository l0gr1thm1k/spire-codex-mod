using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SpireCodex.Replay;

// Reading a journal back far enough to append to it safely.
//
// Separated from ReplayJournal purely so it is testable: everything here is file and string
// handling with no Godot or game dependency, which lets SpireCodex.Tests compile this one
// file instead of an assembly that cannot load outside the game.
public static class ReplayJournalScan
{
    // Highest `s` already in the file, or -1 when it is new or unreadable. Reads the tail only;
    // journals reach hundreds of KB and this runs on the run-start path.
    // Also used by the crash-recovery scan, which appends its marker from outside the writer
    // and so has no sequence state of its own.
    public static long LastSequence(string path)
    {
        try
        {
            if (!File.Exists(path)) return -1;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(fs.Length, 8192);
            if (take == 0) return -1;
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            var tail = System.Text.Encoding.UTF8.GetString(buf, 0, read);

            var best = -1L;
            foreach (var line in tail.Split('\n'))
                best = MaxField(line, "s", best);
            return best;
        }
        catch { return -1; }
    }

    // The counters a resumed session has to pick up from, recovered from the file it is about
    // to append to.
    //
    // `s` has been resumed since the crash-recovery work, because the exploder keys events on
    // (run_hash, s) and a second s=0,1,2... silently produced duplicate keys on exactly the
    // save-scum runs that work existed to study. The card-instance id and the decision id have
    // the same property and never got the same treatment: ReplayRecorder restarts both at 0 on
    // every session, so after a reload `c: 7` and `decision_id: 3` name one thing in the first
    // session and a DIFFERENT thing in the second, inside a single file, with nothing in the
    // stream marking the change.
    //
    // Measured over 313 journals: 36% carry more than one session, and of those 96% re-mint a
    // card id and 82% re-mint a decision id (worst case, 548 collided card ids in one run). A
    // wrong instance id is worse than a missing one -- it silently fuses two physical cards
    // into one lineage -- so the counters continue rather than restart.
    //
    // Unlike LastSequence this reads the WHOLE file, because these maxima are not near the
    // tail. Ids are minted in ascending order, but the last lines of a run reference whichever
    // cards happen to be in play, which are typically low-numbered starters; a tail read would
    // report a high-water mark far below the true one and reissue ids anyway. It runs once per
    // journal open, on the run-start path, against a file that reaches ~1.5 MB at the extreme.
    public readonly struct HighWater
    {
        public HighWater(long seq, int card, int decision, string? deckLine)
        {
            Seq = seq;
            Card = card;
            Decision = decision;
            DeckLine = deckLine;
        }

        // -1 when the file is new or unreadable, matching LastSequence.
        public long Seq { get; }
        public int Card { get; }
        public int Decision { get; }

        // The raw text of the last line in the file that listed the whole deck, or null when
        // there is none. Kept as text and parsed only if it is needed, so the common case (a
        // new run) parses nothing at all.
        public string? DeckLine { get; }
    }

    public static HighWater HighWaterOf(string path)
    {
        var seq = -1L;
        var card = 0L;
        var decision = 0L;
        string? deckLine = null;
        try
        {
            if (!File.Exists(path)) return new HighWater(-1, 0, 0, null);
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                seq = MaxField(line, "s", seq);
                // Every key that carries a minted card-instance id. `c` also appears inside the
                // header's starting_deck rows, which scanning the raw line catches for free.
                card = MaxField(line, "c", card);
                card = MaxField(line, "deck_c", card);
                card = MaxField(line, "from_c", card);
                card = MaxField(line, "to_c", card);
                card = MaxField(line, "instance_id", card);
                decision = MaxField(line, "decision_id", decision);
                // The newest full deck listing wins, whichever kind wrote it: a `deck` row from
                // mid-session, or the `starting_deck` on a header. Cheap substring tests, so the
                // scan stays one pass and allocates nothing per line.
                if (line.Contains("\"t\":\"deck\"", StringComparison.Ordinal)
                    || line.Contains("\"starting_deck\":", StringComparison.Ordinal))
                    deckLine = line;
            }
        }
        catch
        {
            // A truncated or unreadable tail still leaves everything read so far usable, and a
            // high-water mark that is too LOW is the pre-existing behaviour, not a regression.
        }
        return new HighWater(seq, (int)card, (int)decision, deckLine);
    }

    // Highest value of "<key>":<integer> anywhere in the line, or `best` when the key is absent.
    // The leading quote is part of the needle, so "c" does not also match "deck_c" and "s" does
    // not match "ms" or "stars_paid".
    private static long MaxField(string line, string key, long best)
    {
        var needle = "\"" + key + "\":";
        var at = line.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            var start = at + needle.Length;
            // Tolerate whitespace after the colon. The writer never emits it, so this costs
            // nothing in practice, but without it any journal not produced by this writer
            // silently reads as "no sequence" and the caller restarts from 0. That is how a
            // hand-written test fixture fooled me into thinking the recovery fix had failed.
            while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
            var end = start;
            while (end < line.Length && char.IsDigit(line[end])) end++;
            if (end > start && long.TryParse(line.Substring(start, end - start), out var v) && v > best)
                best = v;
            at = line.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return best;
    }

    // The deck a stored listing describes, as remap entries. Returns an empty list for
    // anything it cannot read: a malformed line, or a listing written before deck rows carried
    // `up`, in which case the key would be built from different fields on the two sides and
    // would mis-align rather than fail. A listing we cannot key is not a listing we can trust.
    public static List<DeckRemap.Entry> DeckEntries(string? line)
    {
        var rows = new List<DeckRemap.Entry>();
        if (string.IsNullOrEmpty(line)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(line);
            // `cards` is NOT unique to a deck listing -- the `shop` row carries the shelf under
            // the same key. Gate on the row type so a shop's stock can never be read as a deck;
            // the missing-`up` refusal below would catch it today, but only by luck.
            var kind = Text(doc.RootElement, "t");
            if (kind != "deck" && kind != "header") return rows;
            if (!doc.RootElement.TryGetProperty("cards", out var cards)
                && !doc.RootElement.TryGetProperty("starting_deck", out cards))
                return rows;
            if (cards.ValueKind != JsonValueKind.Array) return rows;
            foreach (var card in cards.EnumerateArray())
            {
                if (card.ValueKind != JsonValueKind.Object) return Empty(rows);
                if (!card.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.Number)
                    return Empty(rows);
                // `up` absent means the capture predates it. Without it the key is not
                // comparable against a live deck that has it, so refuse the whole listing.
                if (!card.TryGetProperty("up", out var up) || up.ValueKind != JsonValueKind.Number)
                    return Empty(rows);
                rows.Add(new DeckRemap.Entry(c.GetInt32(), Key(
                    Text(card, "id"), up.GetInt32(), Text(card, "enchantment"),
                    card.TryGetProperty("amount", out var amt)
                        && amt.ValueKind == JsonValueKind.Number ? amt.GetInt32() : 0)));
            }
        }
        catch { return Empty(rows); }
        return rows;
    }

    // One place that builds the key, called for the stored side here and for the live side by
    // the recorder, because two spellings of the same key align nothing.
    public static string Key(string? id, int up, string? enchantment, int amount)
        => $"{id}|{up}|{enchantment}|{amount}";

    private static List<DeckRemap.Entry> Empty(List<DeckRemap.Entry> rows)
    {
        rows.Clear();
        return rows;
    }

    private static string? Text(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
