using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpireCodex.Core;
using SpireCodex.Producer;

namespace SpireCodex.Replay;

// Run lifecycle for the replay journal: opens one per run, stamps the header, hands out
// decision ids, and closes it with a terminal line. The hooks in ReplayHooks call Line() to
// emit; this class owns everything about which file is open and when.
//
// Run boundaries come from the producer tick reporting the live seed, the same signal
// DamageTracker.NoteRun already uses, so there is no need to reach into game types for a
// "run started" event that may not exist.
//
// Phase 1 records to disk only. Upload lands in phase 3 and reads the same files.
public static class ReplayRecorder
{
    private static ReplayJournal? _journal;
    private static string? _seed;
    // Last snapshot seen while still in the run. By the time the seed clears the run is
    // already gone, so the outcome has to be remembered rather than read at close time.
    private static Snapshot? _lastInRun;
    // Deck instance ids for the terminal line, captured while the run is still live (the live
    // player is gone by the time it ends).
    //
    // Refreshed on a MEMBERSHIP signature, not on deck size. Size was wrong: CardCmd.Transform
    // swaps one card for another without changing the count, so a transformed deck kept the
    // stale list and final_deck claimed the transformed-away instance survived while omitting
    // its replacement. A removal plus an acquisition between two ticks had the same problem.
    private static List<ReplayLine>? _deckSnapshot;
    private static int _deckCount = -1;
    private static int _deckSig;

    // Signature of the merchant stock last written, so a shop line is emitted when the screen
    // opens and again whenever the stock actually changes (a purchase, a restock, a price
    // move) rather than on every 10 Hz tick. Cleared on leaving the shop so revisiting emits.
    private static string? _shopSig;

    // Authoritative floor, latched by the room hook from the live run state. The snapshot's
    // floor lags by up to one 10 Hz tick, which is invisible mid-floor but wrong for the first
    // lines after entering a room: combat_start fires immediately after AfterRoomEntered and
    // was being stamped with the PREVIOUS floor, so a fight filed one floor too early.
    private static int _floor = -1;
    private static int _act = -1;

    public static void NoteFloor(int floor, int act)
    {
        if (floor >= 0) _floor = floor;
        if (act > 0) _act = act;
    }
    private static int _decisionId;
    private static readonly object Gate = new();

    // %APPDATA%/SpireCodex/replays/. Deliberately NOT inside the game's save tree: the
    // game's cloud sync deletes local-only files it doesn't recognise, which already bit us
    // on the modded/vanilla save split. This sits next to the upload ledger and the backfill
    // marker, which live here for the same reason.
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpireCodex", "replays");

    public static bool Active => _journal != null;

    // Path of the journal being written right now, so the upload sweep skips it.
    public static string? CurrentPath => _journal?.Path;

    // Called once at mod init. Recovers journals abandoned by a crash before any new run can
    // open one.
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            RecoverAbandoned();
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay: start failed: {e.Message}");
        }
    }

    // Producer tick. Opens a journal when a new run begins and closes one when the run ends.
    // No-op unless the seed changed, so this is cheap to call at 10 Hz.
    public static void NoteRun(Snapshot snapshot)
    {
        try
        {
            if (!SpireCodexConfig.RecordReplays) { StopIfRunning("disabled"); return; }

            var seed = snapshot.InRun ? snapshot.Seed : null;
            if (seed == _seed)
            {
                // Same run: keep the outcome state fresh for the eventual terminal line.
                if (snapshot.InRun)
                {
                    _lastInRun = snapshot;
                    RefreshDeckIfChanged(snapshot);
                    // This call was missing entirely. RecordShopIfChanged existed, was correct,
                    // and was never invoked, so not one `shop` line was written in any journal
                    // ever recorded while 44 `buy` lines were. C# does not warn on an unused
                    // private static method, and the deck refresh right above it made the block
                    // look complete. Second time this exact shape has bitten this feature.
                    RecordShopIfChanged(snapshot);
                }
                return;
            }

            // The run changed. Close the OLD one with the last state seen IN IT, before any
            // run-scoped cache is touched. The earlier version assigned _lastInRun from the new
            // snapshot before this comparison, so a direct run-A-to-run-B transition (no
            // out-of-run tick between) closed A carrying B's hp, floors, runtime and relics.
            if (_journal != null)
            {
                var prior = _lastInRun;
                if (prior != null)
                    Finish(prior, ReplayHooks.PlayerDied ? "death"
                        : prior.IsGameOver ? "game_over" : "left_run");
                else StopIfRunning("run_changed");
            }

            _seed = seed;
            _lastInRun = null;
            _deckSnapshot = null;
            _deckCount = -1;
            if (string.IsNullOrEmpty(seed)) return;
            _lastInRun = snapshot;

            CardInstances.Reset();
            ReplayHooks.ResetRun();
            _floor = -1;
            _act = -1;
            Interlocked.Exchange(ref _decisionId, 0);
            _deckSnapshot = null;
            _deckCount = -1;

            // Identity must match the .run EXACTLY. The upload endpoint 409s on a header whose
            // seed / start_time / character disagree with the run doc, and both were wrong:
            //   seed       — Snapshot.Seed is Rng.Seed, the NUMERIC rng seed
            //                (15804765326433090030); the .run stores Rng.StringSeed, the
            //                display seed (JLUJ75JXPV9F). Completely different values.
            //   start_time — was DateTimeOffset.UtcNow at journal open, which ran a second
            //                after the run actually started (1788584350 vs 1788584349).
            // Every replay upload would have been rejected.
            var runState = Core.Sts2Access.LiveRunState;
            var runSeed = Reflect.GetString(Reflect.GetMember(runState, "Rng"), "StringSeed");
            var startTime = RunStartTime();
            if (runSeed == null || startTime == 0)
            {
                // Loud, not silent. A wrong identity here does not break the recording, it
                // makes every upload 409 header_mismatch forever, and the first version of
                // this failed exactly that way while looking fine on disk.
                MainFile.Logger.Info(
                    $"replay: RUN IDENTITY INCOMPLETE (seed={runSeed ?? "?"}, start_time={startTime}); "
                    + "uploads will be rejected until this is fixed");
                runSeed ??= seed!;
                if (startTime == 0) startTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            var journal = ReplayJournal.Open(Dir, runSeed, startTime);
            if (journal == null) return;

            lock (Gate) _journal = journal;
            WriteHeader(snapshot, runSeed, startTime);

            // A run loaded from a save continues the SAME journal (seed and start_time are
            // restored, so the filename matches and the writer appends). That continuity is
            // what makes the reload invisible otherwise: the run clock does not jump, and a
            // replayed turn just looks like more play. The game counts reloads itself —
            // SaveManager.IncrementNumReloads fires on load — so this is measured, not inferred.
            var reloads = Reloads();
            AttemptId = reloads;
            // Latch the position from the snapshot we were HANDED rather than leaving Line() to
            // fall back to LiveStateProducer.Latest, which is the same producer that is mid-call
            // into us and may not have published yet. The resume line's floor is what tells a
            // consumer which fight the reload rolled back, so it must not be a null that looks
            // like "no floor".
            NoteFloor(snapshot.TotalFloor, snapshot.Act);
            // If the reload landed us inside a fight, re-latch that fight's id BEFORE the resume
            // line, so the resume itself names the combat it resumed into and every turn, play
            // and hp line after it is attributable.
            var resumedCombat = ReplayHooks.ResumeCombatIfInFight();
            // journal.Resumed, not reloads: the game's counter does not move on an in-process
            // quit-to-menu and Continue, and that boundary still needs marking.
            if (journal.Resumed || reloads > 0)
                Line("resume")
                    ?.Set("reloads", reloads)
                    // Present only when the reload dropped straight back into an unfinished
                    // fight. Absent means the reload landed out of combat, which is the case
                    // where the earlier attempt really was rolled back.
                    .Set("combat_id", resumedCombat)
                    .Set("wall_clock", DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    .Set("run_time", snapshot.RunTime)
                    .Set("hp", snapshot.CurrentHp)
                    .Set("gold", snapshot.Gold)
                    .Set("deck_size", snapshot.DeckSize)
                    .Emit();
            RefreshDeckIfChanged(snapshot); // so an early finish still has instance-level deck
            MainFile.Logger.Info($"replay: recording {Path.GetFileName(journal.Path)}");
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay: NoteRun failed: {e.Message}");
        }
    }

    // Emit a line. Returns null when nothing is recording, so hooks read as:
    //     ReplayRecorder.Line("play")?.Set("id", id).Emit();
    // and cost nothing outside a recorded run.
    //
    // floor and act are stamped centrally from the live snapshot rather than re-derived by
    // each hook: the producer already tracks them, and the analytics tables need them on
    // every row so month partitions prune without joining back to the run.
    public static ReplayLine? Line(string kind)
    {
        if (_journal == null) return null;
        var line = new ReplayLine(kind);
        // Prefer the floor the room hook latched; fall back to the snapshot before the first
        // room of a run has been entered.
        if (_floor >= 0) line.Set("floor", _floor).Set("act", _act > 0 ? _act : 1);
        else if (LiveStateProducer.Latest is { InRun: true } s)
            line.Set("floor", s.TotalFloor).Set("act", s.Act);
        return line;
    }

    // Companion to Line(): pushes the built line into the open journal.
    public static void Emit(this ReplayLine? line)
    {
        if (line == null) return;
        _journal?.Write(line);
    }

    // A fresh decision id. One per choice-set observation (a card reward, a shop visit, a
    // removal, an event page), carried on the offer, on every option row, and on the
    // resolution lines, so a late-arriving resolution still joins to its decision instead of
    // being inferred from adjacency.
    public static int NextDecisionId() => Interlocked.Increment(ref _decisionId);

    // Which play session of this run is writing. A resumed run appends to the same journal, so
    // "same combat_id, different attempt_id" is how a consumer tells a fight that was restarted
    // by a reload from one that simply continued.
    //
    // It reads the GAME's reload counter rather than counting journal opens ourselves. Counting
    // opens would restart at 0 whenever a journal is recreated, and the whole point of the field
    // is that it stays meaningful across exactly the event that clears our in-memory state.
    public static int AttemptId { get; private set; }

    // "{act}.{floor}" from the latched position, the same values Line() stamps. Used to build a
    // combat id that survives a reload, so it must come from the run's own coordinates and not
    // from any counter this process keeps.
    public static string FloorKey => $"{(_act > 0 ? _act : 1)}.{(_floor >= 0 ? _floor : 0)}";

    private static void WriteHeader(Snapshot s, string seed, long startTime)
    {
        Line("header")
            // 1  the original schema.
            // 2  adds combat_id + attempt_id on combat lines, combat_end.hp_lost_total, and
            //    selected_option_indices on a deck-select outcome. Also the first version in
            //    which room.coord is reliably populated: it existed in 1 and was always null,
            //    so a consumer must branch on THIS number rather than on whether a coord
            //    happens to be present. "this mod version did not record map positions" and
            //    "this floor's position was not recorded" are different sentences.
            // 4  adds starting_max_hp and starting_hp to the header, so the ascension HP
            //    penalty is readable without inferring it from the run-start heal.
            // 3  adds the move line (what an enemy actually did), src on power and block, block
            //    for monsters as well as the player, and an hp line on heals so a rest site
            //    records the amount rather than only the option taken.
            ?.Set("replay_version", 4)
            .Set("run_schema_version", 9)
            .Set("seed", seed)
            // The .run records the bare version ("v0.111.0"); Sts2Version.Current carries the
            // commit too ("v0.111.0+41cef1ea"). The run corpus partitions on build_id, so the
            // header has to use the same form or replays never join their runs by build. The
            // precise value is kept alongside rather than thrown away.
            .Set("build_id", Core.Sts2Version.Current.Split('+')[0])
            .Set("build_id_full", Core.Sts2Version.Current)
            .Set("mod_version", Api.ModVersion.Current)
            .Set("character", s.Character)
            .Set("ascension", s.Ascension)
            // Max HP before anything in the run moves it, which is the denominator for the
            // Ascension 2 effect. "Weary Traveler: Ancients only heal 80% of your missing HP",
            // and a run begins at 0 HP, so the Neow heal lands at 80% of max from A2 up and
            // 100% below it. Measured across real runs: A0 and A1 heal to full, A3, A5 and A10
            // all heal to exactly 80%, on both Ironclad (64 of 80) and Regent (60 of 75). The
            // penalty does not deepen past A2.
            //
            // end.max_hp cannot serve: by then Cook, Stone Humidifier and anything else that
            // raises the cap have moved it. This is the value before any of that.
            .Set("starting_max_hp", s.MaxHp > 0 ? s.MaxHp : (int?)null)
            // No starting_hp field on purpose. The snapshot reports the character's DEFAULT at
            // header time, before the run's real state exists, so it came back 80 on a run that
            // actually began at 0 and was healed to 64. A confidently wrong number is worse than
            // none. Real starting HP is the first hp line, src "heal", at floor 0.
            .Set("game_mode", s.GameMode?.ToLowerInvariant())
            .Set("modifiers", s.Modifiers)
            .Set("start_time", startTime)
            .Set("platform_type", "steam")
            .Set("player_count", s.PlayerCount)
            .Set("reloads", Reloads())
            // Real instance ids for the starting deck, so lineage is exact instead of inferred.
            // Without this a consumer has to guess that "any id first seen outside an acquire is
            // a starter", which misfires on cards generated mid-combat (Slimed, Guilty, and
            // every Ascender's Bane) — all of which first appear in a draw line.
            .Set("starting_deck", StartingDeck())
            .Set("starting_relics", s.Relics.Select(r => r.Id).ToList())
            .Emit();
    }

    // How many times this run has been loaded from a save. The game maintains the counter
    // (RunManager._numReloads, restored from the save and incremented on every load), so a
    // quit-and-reload is detectable exactly rather than guessed from timing.
    private static int Reloads()
    {
        try
        {
            var state = Core.Sts2Access.LiveRunState;
            var mgr = Reflect.GetStatic(
                state?.GetType().Assembly.GetType("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
            if (Reflect.GetMember(mgr, "NumReloads") is int pub) return pub;
            if (Reflect.GetMember(mgr, "_numReloads") is int priv) return priv;
        }
        catch { }
        return 0;
    }

    // What the merchant had on offer. Emitted from the snapshot rather than a game hook: the
    // live feed already parses the whole inventory (RoomExport.ReadShop), including sold-out
    // slots, sale flags and the removal price, and the stock is only knowable once the screen
    // has populated. Without this a merchant floor records only what was bought, so everything
    // the player passed over — the actual alternatives — is unrecoverable.
    private static void RecordShopIfChanged(Snapshot s)
    {
        if (_journal == null) return;
        if (s.Shop is not { } shop)
        {
            _shopSig = null; // left the merchant; a later visit is a fresh stock
            return;
        }

        var sig = string.Join(",",
            shop.Cards.Select(c => $"c{c.Id}:{c.Cost}:{c.Stocked}:{c.OnSale}")
                .Concat(shop.Relics.Select(r => $"r{r.Id}:{r.Cost}:{r.Stocked}"))
                .Concat(shop.Potions.Select(p => $"p{p.Id}:{p.Cost}:{p.Stocked}")))
            + $"|{shop.Removal?.Cost}:{shop.Removal?.Stocked}";
        if (sig == _shopSig) return;
        _shopSig = sig;

        static List<ReplayLine> Items(List<ShopItemInfo> src) =>
            src.Select((it, i) => new ReplayLine("i")
                .Set("slot", i)
                .Set("id", it.Id)
                .Set("cost", it.Cost)
                .SetFlag("stocked", it.Stocked)
                .SetFlag("sale", it.OnSale)
                .Set("pool", it.Slot))   // "character" / "colorless" for cards
            .ToList();

        Line("shop")
            // Stamp position from the snapshot that DETECTED the shop, overriding the latch.
            // Line() prefers the floor the room hook latched, and this runs off the producer
            // tick, which beats the room hook by one beat on entry. So the first shop line of a
            // visit, the one carrying the full pre-purchase stock and the only one that is an
            // offer set, was stamped with the PREVIOUS floor: 3 while the merchant was floor 4.
            // Every later line for that same visit had the right floor, so a consumer grouping
            // by floor lost exactly the line worth having.
            ?.Set("floor", s.TotalFloor)
            .Set("act", s.Act)
            .Set("gold", s.Gold)
            .Set("removal_cost", shop.Removal?.Cost)
            .Set("removal_stocked", shop.Removal?.Stocked)
            .Set("cards", Items(shop.Cards))
            .Set("relics", Items(shop.Relics))
            .Set("potions", Items(shop.Potions))
            .Emit();
    }

    // The run's own start time, which the .run serialises as start_time and the upload endpoint
    // matches on. It lives on RunManager as the private _startTime, not on RunState: reading
    // "StartTime" off the run state returned null and silently fell back to the wall clock,
    // which is how the header ended up one second later than the .run.
    private static long RunStartTime()
    {
        try
        {
            var state = Core.Sts2Access.LiveRunState;
            if (Reflect.GetMember(state, "StartTime") is long direct && direct > 0) return direct;
            var mgr = Reflect.GetStatic(
                state?.GetType().Assembly.GetType("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
            if (Reflect.GetMember(mgr, "StartTime") is long pub && pub > 0) return pub;
            if (Reflect.GetMember(mgr, "_startTime") is long priv && priv > 0) return priv;
        }
        catch { }
        return 0;
    }

    // Re-capture the deck when its membership changes. The signature folds every card id in
    // order, so a transform (same count, different card) is caught as well as an add or remove.
    private static void RefreshDeckIfChanged(Snapshot s)
    {
        if (_journal == null) return;
        var sig = 17;
        foreach (var d in s.Deck) sig = unchecked(sig * 31 + (d.Id?.GetHashCode() ?? 0));
        if (sig == _deckSig && s.Deck.Count == _deckCount) return;
        _deckSig = sig;
        _deckCount = s.Deck.Count;
        _deckSnapshot = LiveDeck();
    }

    // Mint instance ids for the deck as it exists at run start. These are the same CardModel
    // objects the combat piles later use, so the ids match every later line for those cards.
    //
    // Deck cards are MUTABLE models and keep their identity for the whole run. Canonical models
    // (the templates a reward offers) do not: AbstractModel.ToMutable() returns `this` only when
    // already mutable, otherwise MemberwiseClone()s a new object. That is why a card offered in
    // a reward and the card that lands in the deck are two different objects with two different
    // instance ids.
    private static List<ReplayLine> StartingDeck() => LiveDeck();

    private static List<ReplayLine> LiveDeck()
    {
        var rows = new List<ReplayLine>();
        try
        {
            var deck = Reflect.GetMember(Core.Sts2Access.LivePlayer, "Deck");
            if (Reflect.GetMember(deck, "Cards") is not System.Collections.IEnumerable cards)
                return rows;
            foreach (var card in cards)
            {
                if (card == null) continue;
                rows.Add(new ReplayLine("c")
                    .Set("c", CardInstances.Of(card))
                    .Set("id", Core.Ids.Bare(Reflect.GetString(card, "Id"))));
            }
        }
        catch { }
        return rows;
    }

    // Close the open journal, if any. Runs the close off the caller's thread so a producer
    // tick never waits on disk.
    private static void StopIfRunning(string reason)
    {
        ReplayJournal? journal;
        lock (Gate)
        {
            journal = _journal;
            _journal = null;
        }
        _seed = null; // see Finish: a closed run must be re-openable
        if (journal == null) return;

        // Before the terminal line, while the journal is still ours to write to.
        _journal = journal;
        ReplayHooks.CloseOpenDecision();
        _journal = null;

        var terminal = new ReplayLine("end").Set("terminal_reason", reason);
        _ = Task.Run(async () =>
        {
            await journal.CloseAsync(terminal).ConfigureAwait(false);
            journal.Dispose();
        });
    }

    // Close with the real run outcome. Called when the run ends in a win, a death, or an
    // abandon, before the seed flips.
    public static void Finish(Snapshot s, string reason)
    {
        ReplayJournal? journal;
        lock (Gate)
        {
            journal = _journal;
            _journal = null;
        }
        // Clear the seed latch, or the run can never be recorded again.
        //
        // Save-and-quit-to-menu ends the run and closes the journal, but _seed kept the seed, so
        // hitting Continue in the SAME process hit the `seed == _seed` early return: no journal
        // was reopened and every line for the rest of that run was silently dropped. It looked
        // fine because the file on disk was complete up to the quit. It only ever worked when
        // the game process restarted, which is why every reload tested until now happened to
        // pass.
        _seed = null;
        if (journal == null) return;

        var terminal = new ReplayLine("end")
            .Set("terminal_reason", reason)
            .Set("run_time", s.RunTime)
            .Set("floors", s.TotalFloor)
            // The last in-run snapshot predates the killing blow (the run leaves InRun before a
            // 10 Hz tick can observe IsGameOver), so the death latch decides both of these.
            .SetFlag("is_game_over", s.IsGameOver || ReplayHooks.PlayerDied)
            .Set("hp", ReplayHooks.PlayerDied ? 0 : s.CurrentHp)
            .Set("max_hp", s.MaxHp)
            // [{c,id}] when we captured the deck while still in the run, so every instance's
            // in_final_deck is exact; bare ids as the fallback.
            .Set("final_deck", (object?)_deckSnapshot ?? s.Deck.Select(d => d.Id).ToList())
            .Set("final_relics", s.Relics.Select(r => r.Id).ToList());

        _ = Task.Run(async () =>
        {
            await journal.CloseAsync(terminal).ConfigureAwait(false);
            journal.Dispose();
        });
    }

    // A journal with no terminal line was abandoned by a crash or a kill. Append one saying
    // so, rather than leaving a file that looks like a player who simply stopped choosing.
    //
    // The distinction matters downstream: an interrupted capture must be excluded from any
    // statistic with a denominator, while an abandoned run is a real player decision and
    // belongs in the data. Guessing between them from a missing terminal line is exactly the
    // silent contamination this field exists to prevent.
    private static void RecoverAbandoned()
    {
        string[] files;
        try { files = Directory.GetFiles(Dir, "*.jsonl"); }
        catch { return; }

        foreach (var file in files)
        {
            try
            {
                if (HasTerminal(file)) continue;
                // A crash leaves at most one torn trailing record. Appending straight onto it
                // fuses the fragment and the marker into one invalid line, losing both — the
                // recovery marker included. Terminate the fragment first when the file does not
                // already end on a newline.
                if (!EndsWithNewline(file)) File.AppendAllText(file, "\n");
                // Carry a sequence number. Every other line in every journal has one, and this
                // was the single exception, because the scan appends from outside the writer
                // that owns the counter. A consumer keying on (run, s) hit a missing field on
                // exactly the line that says the capture is broken, and a contiguity check saw
                // a hole at the end of any recovered file. Read the tail for the highest s the
                // same way reopening a journal does.
                //
                // No ms, floor or act, deliberately: there is no clock and no position out here,
                // and inventing them is worse than their absence.
                var seq = ReplayJournal.LastSequence(file) + 1;
                File.AppendAllText(file,
                    "{\"t\":\"end\",\"s\":" + seq + ",\"terminal_reason\":\"interrupted\"," +
                    "\"capture_status\":\"truncated\"}\n");
                MainFile.Logger.Info($"replay: recovered {Path.GetFileName(file)} (interrupted)");
            }
            catch { /* one unreadable journal must not stop the others */ }
        }
    }

    // Whether the file's final byte is a newline, i.e. its last record is complete.
    private static bool EndsWithNewline(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return true;
            fs.Seek(-1, SeekOrigin.End);
            return fs.ReadByte() == '\n';
        }
        catch { return true; }
    }

    // Read the tail rather than the whole file: journals reach a few hundred KB and this runs
    // at mod init, on the game's startup path.
    private static bool HasTerminal(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(fs.Length, 4096);
            if (take == 0) return false;
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            var tail = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            // Only a COMPLETE terminal record counts. A crash mid-write can leave the opening
            // {"t":"end" of a line that never finished, and treating that as a finished run
            // would keep the recovery marker off a journal that genuinely needs one.
            var at = tail.LastIndexOf("\"t\":\"end\"", StringComparison.Ordinal);
            return at >= 0 && tail.IndexOf('\n', at) >= 0;
        }
        catch { return true; } // unreadable: leave it alone rather than append to it
    }
}
