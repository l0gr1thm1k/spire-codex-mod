using System.Runtime.CompilerServices;

namespace SpireCodex.Replay;

// Run-scoped identity for a physical card.
//
// The game gives us no per-instance id: AbstractModel carries a ModelId (the card's TYPE,
// e.g. STRIKE_IRONCLAD) and nothing distinguishing one copy from another. That is enough to
// replay a fight and not enough for deck lineage, which is what pick-to-win conversion and
// Removal Elo actually read. You start with five Strikes; one is removed on floor 4, one
// upgraded on floor 12, one transformed on floor 22, and "which drafted copy survived to the
// end" is unanswerable from the type id alone.
//
// So we mint our own, keyed on object identity via ConditionalWeakTable. Two properties of
// the game make this work:
//
//  - Upgrades MUTATE IN PLACE. CardCmd.Upgrade calls card.UpgradeInternal() then
//    FinalizeUpgradeInternal() on the same CardModel, so the object survives and the id
//    tracks the card across the upgrade. Verified against the decompile.
//  - Transforms DO NOT. CardCmd.Transform(original, replacement) genuinely swaps objects, so
//    a transform has to emit an explicit old -> new link. Both objects are in scope at that
//    call, so ReplayHooks records the pair rather than silently losing the thread.
//
// The table holds weak references to the keys, so cards the game drops are collected
// normally and this never keeps a run's garbage alive.
internal static class CardInstances
{
    private sealed class Box { public int Id; }

    // Replaced wholesale on a new run rather than cleared: ConditionalWeakTable has no Clear,
    // and a fresh table is both cheaper and guarantees no id leaks across runs.
    private static ConditionalWeakTable<object, Box> _ids = new();
    // Combat copy -> the deck instance id it was cloned from.
    //
    // A fight does not use the deck's CardModel objects: CombatState.CloneCard runs
    // mutableCard.ClonePreservingMutability(), so every card in a combat is a fresh object and
    // gets a fresh id here. Measured on a real 994-line journal: 123 combat handles, zero of
    // them overlapping the 21 deck ids. Without this map, "which physical Strike did I play"
    // is unanswerable, and that is not something a consumer can reconstruct later.
    private static ConditionalWeakTable<object, Box> _deckOrigin = new();
    private static int _next;
    private static readonly object Gate = new();

    // New run: ids restart at 1. Called from ReplayRecorder when the seed changes, the same
    // run-boundary signal DamageTracker already uses.
    public static void Reset()
    {
        lock (Gate)
        {
            _ids = new ConditionalWeakTable<object, Box>();
            _deckOrigin = new ConditionalWeakTable<object, Box>();
            _next = 0;
        }
    }

    // The run-scoped id for this card object, minting one on first sight. Returns 0 for null
    // so a failed reflection read degrades to "unknown instance" instead of throwing into a
    // game hook.
    public static int Of(object? card)
    {
        if (card == null) return 0;
        lock (Gate) return OfLocked(card);
    }

    // Caller must hold Gate. Monitor is re-entrant so nesting would work, but keeping the
    // locked core explicit stops a future edit from adding a second lock acquisition here.
    private static int OfLocked(object card)
    {
        var table = _ids;
        if (table.TryGetValue(card, out var box)) return box.Id;
        var fresh = new Box { Id = ++_next };
        table.Add(card, fresh);
        return fresh.Id;
    }

    // Record that `clone` is a combat copy of `source`. Chases the chain so a copy of a copy
    // (Dual Wield on an already-cloned card) still resolves to the original deck card.
    public static void LinkClone(object? source, object? clone)
    {
        if (source == null || clone == null) return;
        lock (Gate)
        {
            var origin = _deckOrigin.TryGetValue(source, out var known) ? known.Id : OfLocked(source);
            if (origin != 0) _deckOrigin.Add(clone, new Box { Id = origin });
        }
    }

    // The deck instance this card descends from, or 0 when it is a deck card itself (or was
    // generated in combat and has no deck ancestor, like Slimed or Wound).
    public static int DeckIdOf(object? card)
    {
        if (card == null) return 0;
        lock (Gate) return _deckOrigin.TryGetValue(card, out var box) ? box.Id : 0;
    }

    // Whether we have already seen this card object. Lets a hook tell "this card just entered
    // the run" from "this card moved piles again" without minting an id as a side effect.
    public static bool Known(object? card)
    {
        if (card == null) return false;
        lock (Gate) return _ids.TryGetValue(card, out _);
    }
}
