using System.Runtime.CompilerServices;
using System.Threading;

namespace SpireCodex.Replay;

// Which BODY a row is talking about, when the model id cannot say.
//
// "Corpse Slug" names a species, not a creature. A hallway against two of them produced a
// journal in which every play, hit and power read `CORPSE_SLUG`, so nothing downstream could
// tell which slug a Strike hit, which one a Weak landed on, or which one died first.
//
// The game cannot answer it either. `Creature.SlotName` is the game's own notion of position --
// Exoskeleton branches on it, NCombatRoom places sprites by it -- but it is filled from
// `EncounterModel.Slots`, which defaults to `Array.Empty<string>()` and is overridden by just 19
// of 98 encounters in the shipped assembly. `CorpseSlugsNormal` and `CorpseSlugsWeak` are not
// among them, so both slugs carry a null SlotName. It is recorded where it exists (as `slot` on
// the combat_start enemy rows) because for those 19 it ties this id to what the player sees, but
// it cannot be the identity.
//
// So the id is minted here, keyed by REFERENCE identity: two slugs are two objects however equal
// their models are.
//
// RUN-SCOPED AND NEVER RESET, which the first version of this got wrong and a real journal
// caught. Per-fight numbering looks obviously right and is not: CombatManager.StartCombatInternal
// runs AfterCreatureAdded for every creature BEFORE it calls Hook.BeforeCombatStart, so an
// encounter whose monsters apply a power on entry -- Phantasmal Gardeners' Skittish, Corpse Slugs'
// Ravenous -- emits power rows before the combat_start that would do the resetting. Those rows
// minted into the PREVIOUS fight's numbering and the combat_start then renumbered the very same
// creatures, so one gardener was `2` in its own Skittish row and `0` in the enemy list eight
// rows later. A body must have exactly one id for its whole existence or the field is worse than
// useless, and the only numbering with that property is one that never restarts.
//
// A separate sequence from CardInstances, deliberately. The play row recorded the model id alone
// for exactly this reason -- minting a card-instance id for a Creature would consume ids from the
// card sequence and corrupt card_instances downstream -- and a counter of its own removes that
// objection rather than working around it.
internal static class CreatureSlots
{
    // Weak keys: a creature stays in the table only as long as the game itself holds it, so a run
    // of hundreds of monsters retains nothing after each fight is torn down. The same reason
    // ReplayHooks uses a ConditionalWeakTable for MoveOwners.
    //
    // Reference identity is what a ConditionalWeakTable keys on by definition, which is also
    // exactly what is wanted: whatever equality a Creature defines, two distinct bodies must never
    // collapse onto one id.
    private static readonly ConditionalWeakTable<object, object> Ids = new();

    // Starts at 1, not 0. The id is omitted when it cannot be determined, so 0 would never be
    // written by this code -- but a consumer writing `if (row.target_cid)` in TypeScript would
    // silently drop a real creature 0, and no id is worth that footgun.
    private static int _next;

    // Pick the counter up where the previous session of this run left it.
    //
    // "Per run" is not the same as "per process", and a reload is where the two part company.
    // The journal filename is derived from seed + start_time, so a reload APPENDS to a file that
    // already contains cids 1..N, and a counter restarting at 1 hands those same numbers to
    // different bodies inside one journal. Observed on run 9WWFYZ7FT7L2: the seven fights before
    // the reload used cids 1..10, and the fights after it started again at 1, so a Seapunk and a
    // Soul Fysh both answered to cid 1.
    //
    // This is the same defect the card-instance and decision counters had, fixed the same way
    // and from the same single file scan (see ReplayJournalScan.HighWaterOf). A wrong id is
    // worse than a missing one here for exactly the reason a card id is: it silently fuses two
    // bodies into one lineage.
    public static void ResumeFrom(int highWater)
    {
        if (highWater > _next) _next = highWater;
    }

    public static int Of(object creature)
        => (int)Ids.GetValue(creature, _ => (object)Interlocked.Increment(ref _next));

    // Null, never a number, for the player and for a null read.
    //
    // Null must mean "no id" and must never be a number. A fallback would name some creature in
    // every row whose read failed, which is the one outcome worse than saying nothing:
    // `ReplayLine.Set` drops a null so a missing key costs nothing, while a confident wrong id
    // would put the player's own Strike on a body that never took it.
    //
    // The player is excluded rather than given an id because `src`/`dst`/`tgt` already carry the
    // literal "player" for it. (Note `power.tgt` is the exception in the existing schema: it
    // names the player by character id, e.g. IRONCLAD, not by "player". Unchanged here.) In co-op
    // this still does not distinguish the two players; that is unchanged either way.
    //
    // An unseen creature is MINTED rather than refused, so a body whose first appearance is a
    // power applied on entry, or a mid-fight summon (Infested's wrigglers, Stock's axebots),
    // carries the same id from that first row onwards.
    public static int? Maybe(object? creature)
    {
        if (creature == null) return null;
        if (Core.Reflect.GetMember(creature, "IsPlayer") is true) return null;
        return Of(creature);
    }
}
