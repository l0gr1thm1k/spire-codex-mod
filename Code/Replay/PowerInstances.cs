using System.Runtime.CompilerServices;
using System.Threading;

namespace SpireCodex.Replay;

// WHICH power instance a row is about, when the power's id and the body carrying it cannot say.
//
// Most powers need none of this. PowerCmd.FindExistingInstanceForStacking looks up the existing
// instance and stacks onto it, and Creature.ApplyPowerInternal throws outright if a second one is
// ever added, so `id` plus `tgt_cid` is already a key. Two of the three PowerInstanceType values
// break that. `Instanced` never stacks -- a second The Bomb is a second power ticking down from 3
// -- and `InstancedPerApplier` keeps one per applier, so two monsters Strangling the player are
// two separate powers. For those, id plus tgt_cid names a GROUP, and every row in the group reads
// as one power whose amount jumps around.
//
// The game has no per-instance id of its own to use instead. PowerModel carries a ModelId (the
// power's TYPE) and nothing else identifying, and powers are not serialized at all: there is no
// SerializablePower and no combat state in a run save, so there is nothing to re-key on after a
// reload either. A reload restarts the fight rather than resuming it.
//
// So the id is minted here, keyed by REFERENCE identity through a ConditionalWeakTable, the same
// way CardInstances and CreatureSlots mint theirs. The table holds its keys weakly, so a fight's
// powers are collected along with the fight.
//
// The ATTEMPT PREFIX is the part that matters. The counter is in-process and a reload starts a
// fresh process, while a resumed run APPENDS to the journal it already wrote: a bare counter would
// hand "17" to one power in the first session and a different power in the second, inside one
// file, which is the exact fusion this class exists to prevent. ReplayRecorder.AttemptId is the
// game's own reload count, so "2.17" cannot collide with "3.17". The card and creature counters
// solve the same problem by scanning the file for a high-water mark; a prefix needs no scan, and
// it also says out loud which session minted the id.
//
// A pid is NOT claimed to follow one power across a reload. Nothing can: the powers themselves do
// not survive it.
internal static class PowerInstances
{
    private static readonly ConditionalWeakTable<object, string> Ids = new();

    // Never reset between runs, deliberately. A journal is per run, so an id only has to be unique
    // within one file, and a counter that climbs for the life of the process cannot collide with
    // itself when a second run starts inside it.
    private static int _next;

    public static string Of(object power)
        => Ids.GetValue(power, _ => $"{ReplayRecorder.AttemptId}.{Interlocked.Increment(ref _next)}");
}
