using System.Runtime.CompilerServices;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SpireCodex.Core;

namespace SpireCodex.Replay;

// Every capture point for the replay journal, patched onto the game's own hook surface.
//
// Capture only. Each handler swallows its own exceptions: a game patch that renames a method
// makes that one line kind stop appearing, which the "n/m patched" log line surfaces, rather
// than throwing into a game hook.
//
// STATE RECONSTRUCTION, NOT RE-SIMULATION. We do not attempt deterministic replay from
// inputs; that would mean reimplementing and then preserving every game version's RNG and
// engine semantics forever. Instead we record resolved effects with actor and target instance
// ids. The consequence for consumers: a full board keyframe is written at combat_start, and
// each turn's exact state is reached by applying the draw/play/discard/exhaust/hit lines from
// there. Seeking to a turn means replaying that combat from its start (a few hundred lines),
// never the whole run.
internal static class ReplayHooks
{
    // The decision a resolution belongs to. Set when an offer is recorded, read by the
    // resolution handlers so their lines carry decision_id explicitly. Adjacency is not
    // enough: a resolution can arrive well after the selection, and a late one still has to
    // join to the right decision.
    private static int _decision;
    private static string? _decisionType;

    // What OpenSelectOffer composes for the enchantment screen, "deck_select" + "enchant".
    // Named because CardEnchanted has to tell an enchant offer from any other open select
    // before it joins to one.
    private const string EnchantSelectType = "deck_select_enchant";

    // Signature of the event page a decision was opened for. Multi-page events (Neow offers a
    // boon and then a separate Proceed) are two distinct choice sets, and keying only on
    // "am I already in an event" would collapse them into one decision with two winners.
    private static string? _eventPage;

    // Bumped by CardReward.Reroll and consumed by the Populate that follows it, so a rerolled
    // offer keeps its decision and increments offer_generation. The earlier version minted a
    // fresh decision with generation 0 every time, contradicting its own contract and losing
    // the link between the rejected alternatives and the offer that replaced them.
    private static int _pendingReroll;

    // Set when a killing blow lands on the player. The producer ticks at 10 Hz and the run
    // leaves InRun before a tick observes IsGameOver, so the last in-run snapshot still says
    // alive: the first real full journal ended a death as terminal_reason "left_run",
    // is_game_over false, hp 5. This latch is the authoritative signal.
    public static bool PlayerDied { get; private set; }

    // What the merchant is about to sell, captured BEFORE the sale. MerchantEntry clears its
    // Model / CreationResult in ClearAfterPurchase(), which runs before Hook.AfterItemPurchased,
    // so reading the item at that hook always found null: every buy line in the first full
    // journal recorded a cost with no item.
    private static string? _pendingBuyId;
    private static string? _pendingBuyKind;

    // Close whatever decision is open. Called by the recorder before the terminal line so a run
    // that ends with a select still on screen writes its outcome: an absent
    // selected_option_indices now means "no select was open", and leaving one unflushed would
    // make an unfinished select indistinguishable from that.
    public static void CloseOpenDecision() => DemoteDecision();

    // A reload can drop the player back into a fight ALREADY IN PROGRESS, with the turn counter
    // continuing. Measured, not theorised: a real journal resumed VANTOM_BOSS at turn 8 straight
    // after turn 7, with no combat_start in the new session. BeforeCombatStart does not fire on
    // that path and neither does the room hook, so without this every line for the rest of that
    // fight carried no combat_id at all.
    //
    // The id is rebuilt from the same three recorded facts combat_start uses (act, floor,
    // encounter), read off the live combat state. That is a read, not an inference.
    //
    // _hpLostInCombat deliberately stays NULL. We did not see this fight start, so its total is
    // unknowable and combat_end will omit hp_lost_total rather than report a partial as a total.
    public static string? ResumeCombatIfInFight()
    {
        try
        {
            var room = Reflect.GetMember(Core.Sts2Access.LiveRunState, "CurrentRoom");
            var combat = Reflect.GetMember(room, "CombatState");
            if (combat == null) return null;
            var encounter = Ids.Bare(Reflect.GetString(Reflect.GetMember(combat, "Encounter"), "Id"));
            if (encounter == null) return null;

            var floorKey = ReplayRecorder.FloorKey;
            _combatFloorKey = floorKey;
            // Deliberately NOT pre-counting this encounter. Seeding it as "seen once" made a
            // RESTARTED fight's combat_start mint "1.2:SLUDGE_SPINNER_WEAK#1" while the resume
            // line named "1.2:SLUDGE_SPINNER_WEAK", so the same fight got two different ids
            // across the reload, which is the one thing this id exists to prevent. Leaving the
            // map empty means a following start re-mints the identical bare id, whether the
            // fight resumed or restarted.
            _floorEncounters = new Dictionary<string, int>();
            _combatId = $"{floorKey}:{encounter}";
            _hpLostInCombat = null;
            return _combatId;
        }
        catch { return null; }
    }

    public static void ResetRun()
    {
        PlayerDied = false;
        _pendingBuyId = null;
        _pendingBuyKind = null;
        // Combat ids are position-derived, so a new run on the same floor numbers would collide
        // with the last one's if the per-floor counter carried over.
        _combatId = null;
        _combatFloorKey = null;
        _floorEncounters = null;
        _hpLostInCombat = null;
        _selectByInstance = null;
        _selected = null;
    }

    // MerchantEntry.OnTryPurchaseWrapper(inventory, ignoreCost) — fires on the ATTEMPT, so the
    // ware is still attached. Only remembered here; the buy line is emitted from
    // AfterItemPurchased, which fires only on success, so an abandoned purchase writes nothing.
    private static void PurchaseAttempt(object __instance)
    {
        try
        {
            var type = __instance.GetType().Name;
            _pendingBuyKind = type.Contains("Relic") ? "relic"
                : type.Contains("Potion") ? "potion"
                : type.Contains("CardRemoval") ? "removal_service"
                : type.Contains("Card") ? "card" : "other";
            _pendingBuyId = Ids.Bare(Reflect.GetString(Reflect.GetMember(__instance, "Model"), "Id"))
                ?? Ids.Bare(Reflect.GetString(
                       Reflect.GetMember(Reflect.GetMember(__instance, "CreationResult"), "Card"), "Id"));
        }
        catch { }
    }

    // Card id -> option_index for the decision currently open. Needed because a reward offers
    // CANONICAL CardModels and the card that lands in the deck is a MUTABLE clone of one
    // (AbstractModel.ToMutable MemberwiseClones when the source is canonical), so the offered
    // instance id and the acquired instance id are different objects and never join. Matching
    // on card id within the open decision is exact in practice: a reward does not offer the
    // same card twice.
    private static Dictionary<string, int>? _offerIndex;

    // One-deep history of the decision that was open before the current one.
    //
    // Two things make a single "current decision" wrong. Resolutions are ASYNC: a card picked
    // as the player clicks to leave lands after AfterRoomEntered has already cleared the
    // context, and would be filed as an unchosen grant — a real draft pick recorded as if the
    // game handed it over. And decisions NEST: an event that opens a card select, or a campfire
    // whose Toke opens a deck select, replaces the outer decision while it is still unresolved.
    // Keeping the previous one lets a late resolution still find its offer.
    private static int _prevDecision;
    private static Dictionary<string, int>? _prevOfferIndex;

    // Card ids offered more than once in the same decision. Their option_index is ambiguous, so
    // it is omitted rather than guessed: attributing a pick to the wrong option is worse for the
    // solver than attributing it to none.
    private static HashSet<string>? _ambiguousOffers;

    // Make the open decision the previous one. Called when a decision opens and when a room
    // changes, so context is demoted rather than destroyed.
    private static void DemoteDecision()
    {
        FlushSelectOutcome();
        if (_decision > 0)
        {
            _prevDecision = _decision;
            _prevOfferIndex = _offerIndex;
        }
        _decision = 0;
        _decisionType = null;
        _offerIndex = null;
        _ambiguousOffers = null;
    }

    // Close out an open deck select with the indices it actually resolved.
    //
    // The array is ALWAYS written for a deck select, empty included: an empty array is "the
    // player declined", and the field being absent is "no select was open". Collapsing those
    // two into a missing field is the whole class of bug this record exists to avoid, so
    // SetFlag-style unconditional writing is deliberate here rather than Set's null-dropping.
    private static void FlushSelectOutcome()
    {
        var picked = _selected;
        _selected = null;
        _selectByInstance = null;
        if (picked == null || _decision <= 0) return;
        ReplayRecorder.Line("outcome")
            ?.Set("decision_id", _decision)
            .Set("decision_type", _decisionType)
            .Set("outcome", picked.Count > 0 ? "select" : "decline")
            .Set("selected_option_indices", picked)
            .Set("n_selected", picked.Count)
            .Emit();
    }

    // The option_index a resolved card occupies in the open deck select, by instance identity.
    // Records the pick as well, so the outcome line's array is built from the same facts the
    // individual resolution lines carry rather than recounted separately.
    private static int? SelectIndexOf(object? card)
    {
        if (_selectByInstance == null || card == null) return null;
        var instance = CardInstances.Of(card);
        if (instance == 0 || !_selectByInstance.TryGetValue(instance, out var idx)) return null;
        // Registered once even when two handlers see the same pick: the `pick` row names every
        // card LogChoice returned, and the per-consequence handlers (remove, upgrade,
        // transform) then name the same card again. One option row is one physical card, so it
        // cannot honestly be selected twice, and appending twice would have made the outcome
        // line's n_selected count handlers instead of choices.
        var picks = _selected ??= new List<int>();
        if (!picks.Contains(idx)) picks.Add(idx);
        return idx;
    }

    public static void Apply(Harmony harmony)
    {
        var hook = HookPatcher.FindType("MegaCrit.Sts2.Core.Hooks.Hook");
        if (hook == null)
        {
            MainFile.Logger.Info("replay-hooks: Hook type not found; replay disabled");
            return;
        }

        var me = typeof(ReplayHooks);
        var n = 0;
        var attempted = 0;

        // --- structure ---------------------------------------------------------------
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterActEntered", me, nameof(ActEntered));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterMapGenerated", me, nameof(MapGenerated));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterRoomEntered", me, nameof(RoomEntered));

        // --- combat ------------------------------------------------------------------
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeCombatStart", me, nameof(CombatStart));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCombatEnd", me, nameof(CombatEnd));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPlayerTurnStart", me, nameof(PlayerTurnStart));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterSideTurnStart", me, nameof(SideTurnStart));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterTurnEnd|BeforeTurnEnd|AfterSideTurnEnd", me, nameof(TurnEnd));
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeCardPlayed", me, nameof(CardPlayed));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCardDrawn", me, nameof(CardDrawn));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCardDiscarded", me, nameof(CardDiscarded));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCardExhausted", me, nameof(CardExhausted));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterShuffle", me, nameof(Shuffled));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterDamageGiven", me, nameof(DamageGiven));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterDamageReceived", me, nameof(DamageReceived));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPowerAmountChanged", me, nameof(PowerChanged));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterBlockGained", me, nameof(BlockGained));
        // No first-party hook exists for either of these, so they patch game internals by name
        // and degrade to "that line stops appearing" if a patch renames them.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterMoveStateMachine"),
            "RollMove", me, nameof(MoveRolled), 3, postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState"),
            "PerformMove", me, nameof(MovePerformed), 1);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CreatureCmd"),
            "Heal", me, nameof(Healed), 3);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCurrentHpChanged", me, nameof(HpChanged));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterGoldGained", me, nameof(GoldGained));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterOrbChanneled", me, nameof(OrbChanneled));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterOrbEvoked", me, nameof(OrbEvoked));

        // --- decisions ---------------------------------------------------------------
        // The offer itself, with one option row per card and its selectability. Postfix on
        // Populate: the cards exist only after it runs.
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Rewards.CardReward"),
                                 "Populate", me, nameof(CardRewardPopulated), 0, postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Rewards.CardReward"),
                                 "OnSkipped", me, nameof(CardRewardSkipped), 0);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Rewards.CardReward"),
                                 "Reroll", me, nameof(CardRewardRerolled), 0);

        // The one funnel that applies IsRemovable AND each caller's own filter, so this is
        // the real eligible set rather than a reconstruction from the deck. Critical for
        // Removal Elo: ineligible cards are not rejected alternatives.
        // PREFIX, not postfix. FromDeckGeneric is `async Task<IEnumerable<CardModel>>`, so a
        // postfix's __result is the Task, not the cards — enumerating it would yield nothing
        // and mark every card ineligible, which is worse than not capturing at all because it
        // looks like data. The prefix reads the filter argument and applies it itself.
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"),
                                 "FromDeckGeneric", me, nameof(DeckSelectOffered), 4);

        // The result half, from the one private funnel all eleven entry points call. Registered
        // beside the offer above because the two are read together: the offer names the
        // alternatives, this names the choice. It fires for screens whose offer is not patched
        // yet, and a `pick` row with no decision_id is still the pick.
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"),
                                 "LogChoice", me, nameof(SelectionReturned), 2);

        // The enchantment screen. FromDeckForEnchantment does NOT delegate to FromDeckGeneric:
        // it filters the deck on enchantment.CanEnchant itself and shows
        // NDeckEnchantSelectScreen, a SIBLING of NDeckCardSelectScreen under
        // NCardGridSelectionScreen rather than a subclass, so nothing above catches it. This is
        // the terminal overload of three; the other two delegate to it, and it takes the
        // presented list directly so the offer needs no reconstruction from the deck.
        //
        // firstParamType disambiguates it from the (Player, EnchantmentModel, int,
        // CardSelectorPrefs) overload, which has the same arity.
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"),
                                 "FromDeckForEnchantment", me, nameof(EnchantSelectOffered), 4,
                                 firstParamType: "IReadOnlyList`1");

        // Resolutions. Each carries decision_id so `applied` is derived from the effect
        // actually landing rather than from an async return value.
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeCardRemoved", me, nameof(CardRemoved));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterItemPurchased", me, nameof(ItemPurchased));
        // The ware is only readable BEFORE the sale: MerchantEntry.ClearAfterPurchase() nulls it
        // ahead of AfterItemPurchased, which is why every buy line used to carry a cost and no item.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Entities.Merchant.MerchantEntry"),
            "OnTryPurchaseWrapper", me, nameof(PurchaseAttempt), 2);
        // Cards conjured straight into hand mid-combat (Prepared chains, potions, relics) never
        // appear in a draw line, so a consumer replaying the hand sees them played from nowhere.
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCardGeneratedForCombat", me, nameof(CardGenerated));
        // CombatState.CloneCard(mutableCard) is where a fight gets its own copy of a deck card.
        // Postfix, so __result is the combat copy and __0 the deck original; without this pair
        // the two id spaces never join and per-instance play analysis is impossible.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.CombatState"),
            "CloneCard", me, nameof(CardCloned), 1, postfix: true);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterRewardTaken", me, nameof(RewardTaken));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterCardChangedPiles", me, nameof(CardChangedPiles));

        // Deck mutations that instance lineage depends on.
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardCmd"),
                                 "Upgrade", me, nameof(CardUpgraded), 2, firstParamType: "CardModel");
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardCmd"),
                                 "Transform", me, nameof(CardTransformed), 3, firstParamType: "CardModel");
        // Enchantment has no hook at all: Hook.cs names Enchantment only to read damage and
        // block modifiers. CardCmd.Enchant is the patch point instead, and it is the right one
        // on three counts. It is SYNCHRONOUS, returning EnchantmentModel? rather than a Task,
        // so a postfix reads the applied result directly instead of hitting the async trap
        // documented on FromDeckGeneric above. It is UNIVERSAL: the line inside it that appends
        // to PlayerMapPointHistoryEntry.CardsEnchanted is the game's sole writer of that list,
        // and a run whose only enchantment came from an event still has the card in its
        // map-point history, so the event paths provably land here -- one patch covers all the
        // relics, every event, and any future source. And it sees what no screen shows: a relic
        // that sweeps the whole deck opens no selector, and neither does the enchant screen when
        // the eligible set is no larger than MinSelect.
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardCmd"),
                                 "Enchant", me, nameof(CardEnchanted), 3,
                                 firstParamType: "EnchantmentModel", postfix: true);

        // Relics, potions, events, rest.
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.RelicCmd"),
                                 "Obtain", me, nameof(RelicObtained), 3);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPotionUsed", me, nameof(PotionUsed));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPotionProcured", me, nameof(PotionProcured));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPotionDiscarded", me, nameof(PotionDiscarded));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterRestSiteHeal", me, nameof(RestHeal));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterRestSiteSmith", me, nameof(RestSmith));
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Events.EventOption"),
                                 "Chosen", me, nameof(EventOptionChosen), 0);

        MainFile.Logger.Info($"replay-hooks: {n}/{attempted} patched");
    }

    // --- structure -------------------------------------------------------------------

    private static void ActEntered(object __0)
    {
        try
        {
            // The act NUMBER is already stamped on every line by ReplayRecorder.Line; only
            // the act's identity is added here. CurrentAct/Id came back null in the first real
            // journal, so try the room's act model as well before giving up.
            ReplayRecorder.Line("act")
                ?.Set("name", Ids.Bare(Reflect.GetString(Reflect.GetMember(__0, "CurrentAct"), "Id"))
                              ?? Ids.Bare(Reflect.GetString(Reflect.GetMember(__0, "Act"), "Id"))
                              ?? Ids.Bare(Reflect.GetString(__0, "CurrentActId")))
                .Emit();
        }
        catch { }
    }

    private static ReplayLine MapNode(object node)
        => new ReplayLine("n")
            .Set("coord", Coord(Reflect.GetMember(node, "coord")))
            .Set("kind", Reflect.GetMember(node, "PointType")?.ToString()?.ToLowerInvariant())
            .Set("children", Enumerate(Reflect.GetMember(node, "Children"))
                .Select(c => Coord(Reflect.GetMember(c, "coord")) ?? "")
                .ToList());

    // AfterMapGenerated(IRunState, ActMap map, int actIndex) — the whole act map, so a replay
    // can draw the graph and the path taken through it. One line per act.
    private static void MapGenerated(object __0, object __1, int __2)
    {
        try
        {
            var line = ReplayRecorder.Line("map");
            if (line == null) return;
            // ActMap exposes GetAllMapPoints(), not a Points/Nodes property, and MapPoint
            // carries a `coord` field plus a Children set rather than x/y ints. Reading the
            // wrong names here returned an empty node list, which looked like a map with no
            // nodes instead of a failed read.
            var nodes = new List<ReplayLine>();
            foreach (var node in Enumerate(Reflect.Call(__1, "GetAllMapPoints")))
                nodes.Add(MapNode(node));

            // GetAllMapPoints() walks ActMap.Grid, and neither the Ancient nor the boss is IN
            // the grid: StandardActMap builds StartingMapPoint as `new MapPoint(cols / 2, 0)`
            // and only assigns it PointType.Ancient, and ActMap.IsInMap special-cases both.
            // So both are real rooms, at real coords, that the node list silently omitted —
            // the Ancient always at row 0, which is why every recorded map appeared to start at
            // row 1 with the player's first room floating off the graph.
            //
            // Their Children ARE populated (StartingMapPoint.AddChildPoint wires it to row 1),
            // so the edges are recorded rather than reconstructed.
            foreach (var extra in new[] { "StartingMapPoint", "BossMapPoint", "SecondBossMapPoint" })
                if (Reflect.GetMember(__1, extra) is { } point)
                {
                    var coord = Coord(Reflect.GetMember(point, "coord"));
                    if (coord == null || nodes.Any(n => (n.Fields.TryGetValue("coord", out var c)
                                                        ? c as string : null) == coord)) continue;
                    nodes.Add(MapNode(point));
                }
            // The boss sits outside the walkable graph, so the viewer can only label it if the
            // map line names it. A10+ runs a double boss, hence the second coord.
            // Identities come from the act this map WAS GENERATED FOR, addressed by the index
            // the hook hands us: IRunState.Acts is 0-based, so Acts[actIndex] is exact.
            //
            // They used to come from LiveStateProducer.Latest.Route, and that was wrong in the
            // way none of this is allowed to be wrong: the producer samples at 10 Hz and this
            // hook runs synchronously during generation, so on entering a new act the snapshot
            // still described the PREVIOUS one. A real act 3 map recorded act 2's boss and act
            // 2's ancient beside act 3's correct coordinates. Acts 1 and 2 happened to agree,
            // so nothing looked broken until a third act existed to disagree.
            var actModel = ElementAt(Reflect.GetMember(__0, "Acts"), __2);
            line.Set("act", __2 + 1)
                .Set("boss", Ids.Bare(Reflect.GetString(
                    Reflect.GetMember(actModel, "BossEncounter"), "Id")))
                // A10+ runs a second boss. Its coord was already recorded; without this the
                // identity was simply missing, so a double boss had two nodes and one name.
                .Set("boss2", Ids.Bare(Reflect.GetString(
                    Reflect.GetMember(actModel, "SecondBossEncounter"), "Id")))
                .Set("ancient", Ids.Bare(Reflect.GetString(
                    Reflect.GetMember(actModel, "Ancient"), "Id")))
                .Set("ancient_coord", Coord(Reflect.GetMember(
                    Reflect.GetMember(__1, "StartingMapPoint"), "coord")))
                .Set("boss_coord", Coord(Reflect.GetMember(
                    Reflect.GetMember(__1, "BossMapPoint"), "coord")))
                .Set("boss2_coord", Coord(Reflect.GetMember(
                    Reflect.GetMember(__1, "SecondBossMapPoint"), "coord")))
                .Set("nodes", nodes)
                .Emit();
        }
        catch { }
    }

    private static void RoomEntered(object __0, object __1)
    {
        try
        {
            // A new room ends the previous decision context; a stale decision_id leaking onto
            // the next room's resolutions would silently mis-attribute picks.
            DemoteDecision();
            _eventPage = null;

            var kind = __1.GetType().Name.Replace("Room", "").ToLowerInvariant();
            // Floor from the live run state, not the centrally-stamped snapshot value: the
            // snapshot is up to one 10 Hz tick behind, and the first real journal filed a
            // treasure room on floor 9 whose very next line was floor 10.
            var floor = Reflect.GetInt(__0, "TotalFloor", -1);
            // Latch it before building the line, so every line until the next room (combat_start
            // above all, which fires immediately after this) carries the right floor.
            ReplayRecorder.NoteFloor(floor, Reflect.GetInt(__0, "CurrentActIndex", 0) + 1);
            ReplayRecorder.Line("room")
                ?.Set("kind", kind)
                .Set("floor", floor >= 0 ? floor : (int?)null)
                // Which map node this room is, in the same coord space as the map line. Without
                // it the route is ambiguous wherever a row holds two nodes of the same kind,
                // which is most of act 1. Null for rooms outside the graph (boss, Neow).
                .Set("coord", Coord(Reflect.GetMember(__0, "CurrentMapCoord")))
                .Set("id", Ids.Bare(Reflect.GetString(Reflect.GetMember(__1, "CanonicalEvent"), "Id"))
                           ?? Ids.Bare(Reflect.GetString(__1, "ModelId")))
                .Emit();
        }
        catch { }
    }

    // A rolled move, keyed to the monster that rolled it.
    //
    // The game has NO move or intent hook, and neither MoveState nor MonsterMoveStateMachine
    // carries an owner: only RollMove(targets, owner, rng) ever sees both. So the owner is
    // latched at roll time and read back when the move actually performs. Weak keys, so a
    // move the game drops is collected normally.
    private static readonly ConditionalWeakTable<object, object> MoveOwners = new();

    // MonsterMoveStateMachine.RollMove(targets, owner, rng) -> MoveState. Postfix: the only
    // point where a move and its monster are both in scope.
    private static void MoveRolled(object __result, object __1)
    {
        try
        {
            if (__result == null || __1 == null) return;
            MoveOwners.Remove(__result);
            MoveOwners.Add(__result, __1);
        }
        catch { }
    }

    // MoveState.PerformMove(targets). Emitted BEFORE the hits and powers it causes, so a
    // consumer reads "monster did X" then the consequences.
    private static void MovePerformed(object __instance)
    {
        try
        {
            if (!MoveOwners.TryGetValue(__instance, out var owner)) return;
            var intents = Enumerate(Reflect.GetMember(__instance, "Intents"))
                .Select(i => Reflect.GetMember(i, "IntentType")?.ToString()?.ToLowerInvariant())
                .Where(x => x != null).ToList();
            ReplayRecorder.Line("move")
                ?.Set("src", CreatureRef(owner))
                .Set("id", Reflect.GetString(__instance, "StateId"))
                .Set("intents", intents.Count > 0 ? intents : null)
                .Emit();
        }
        catch { }
    }

    // "player", a bare monster id, or "effect" for a null creature. Shared by hit, power,
    // block and move so one convention covers every actor in the journal.
    private static string CreatureRef(object? creature)
        => creature == null ? "effect"
            : Reflect.GetMember(creature, "IsPlayer") is true ? "player"
            : Ids.Bare(Reflect.GetString(creature, "ModelId")) ?? "unknown";

    // --- combat ----------------------------------------------------------------------

    // The open fight's id, carried on combat_start, combat_end and every turn line so a turn
    // recorded after an interrupted fight is attributable rather than filed against whichever
    // combat happens to be open.
    //
    // It is derived from what the run itself says, never from a counter: "{act}.{floor}:{ENCOUNTER}".
    // A pure counter restarts at 0 in a fresh process, which would both make a resumed fight look
    // brand new AND collide: a floor with two fights, reloaded during the second, would renumber
    // that second fight #0 and hand it the first fight's id. Act, floor and encounter are all
    // stable across a reload, so the resumed fight keeps its id and only the attempt changes.
    //
    // "#n" is appended only for the remaining case, the SAME encounter twice on one floor, and it
    // is the one part that can still renumber across a reload. That is documented rather than
    // hidden; it needs a floor that repeats an encounter and a reload inside the repeat.
    private static string? _combatId;
    private static Dictionary<string, int>? _floorEncounters; // encounter -> times seen this floor
    private static string? _combatFloorKey;                   // the floor that map belongs to

    // Player HP actually lost in the open fight: the sum of unblocked damage where the player
    // was the target. Null outside a fight AND when a journal opens mid-combat, because a total
    // that started counting halfway through is worse than no total. Never offset by healing and
    // never includes blocked damage.
    private static int? _hpLostInCombat;

    // The deck-select offer currently open: instance id -> option_index, plus the indices
    // resolved so far. Instance identity is exact, so a resolution maps to its option without
    // any id matching and without the ambiguity that forces _ambiguousOffers to refuse.
    private static Dictionary<int, int>? _selectByInstance;
    private static List<int>? _selected;

    // The one full board keyframe per fight. Everything after it is deltas.
    private static void CombatStart(object __1)
    {
        try
        {
            var line = ReplayRecorder.Line("combat_start");
            if (line == null) return;

            var encounter = Ids.Bare(Reflect.GetString(Reflect.GetMember(__1, "Encounter"), "Id"));
            var floorKey = ReplayRecorder.FloorKey;
            if (floorKey != _combatFloorKey)
            {
                _combatFloorKey = floorKey;
                _floorEncounters = new Dictionary<string, int>();
            }
            var key = encounter ?? "unknown";
            var seen = (_floorEncounters ??= new Dictionary<string, int>())
                .TryGetValue(key, out var prior) ? prior : 0;
            _floorEncounters[key] = seen + 1;
            _combatId = seen == 0 ? $"{floorKey}:{key}" : $"{floorKey}:{key}#{seen}";

            // Zero, not carried over: the game does not save mid-combat. CombatRoom rebuilds its
            // creatures from the encounter model and calls SetUpCombat before firing this hook, so
            // a reload RESTARTS the fight rather than resuming it. HP lost in an abandoned attempt
            // was rolled back with it and must not be added to the one that stuck.
            _hpLostInCombat = 0;

            var enemies = new List<ReplayLine>();
            var i = 0;
            foreach (var e in Enumerate(Reflect.GetMember(__1, "Enemies")))
            {
                enemies.Add(new ReplayLine("e")
                    .Set("i", i++)
                    .Set("id", Ids.Bare(Reflect.GetString(e, "ModelId")))
                    .Set("hp", Reflect.GetInt(e, "CurrentHp", 0))
                    .Set("max_hp", Reflect.GetInt(e, "MaxHp", 0)));
            }
            line.Set("combat_id", _combatId)
                .Set("attempt_id", ReplayRecorder.AttemptId)
                .Set("encounter", encounter)
                .Set("enemies", enemies)
                .Emit();
        }
        catch { }
    }

    private static void CombatEnd(object __1)
    {
        try
        {
            ReplayRecorder.Line("combat_end")
                // "victory" is a RECORDED fact here, not a default.
                //
                // CombatManager.CheckWinCondition tests the pending loss FIRST and routes it to
                // ProcessPendingLoss(), which never calls EndCombatInternal() and so never fires
                // this hook. Only the ending path reaches EndCombatInternal, which fires
                // AfterCombatEnd and then AfterCombatVictory. There is no loss hook at all:
                // AfterCombatEnd / AfterCombatVictory / AfterCombatVictoryEarly are the only
                // combat-end hooks the game has.
                //
                // So this line existing IS the victory, and a combat_start with no combat_end
                // is a loss or an interruption. That is why the value is constant and still
                // honest: the absence carries the other half of the information.
                ?.Set("result", "victory")
                .Set("combat_id", _combatId)
                .Set("attempt_id", ReplayRecorder.AttemptId)
                .Set("turns", Reflect.GetInt(__1, "RoundNumber", 0))
                // Omitted, never 0, when the fight was already under way before this journal
                // session opened. A fight with no end line at all keeps no total either, which
                // is what makes "interrupted" readable rather than looking like a clean 0.
                .Set("hp_lost_total", _hpLostInCombat)
                .Emit();
            _combatId = null;
            _hpLostInCombat = null;
        }
        catch { }
    }

    private static void PlayerTurnStart(object __0)
    {
        try
        {
            ReplayRecorder.Line("turn")
                // attempt_id too, so a turn is self-describing. combat_id alone pools the turns
                // of a fight that was reloaded and retried under one id, and only file position
                // separated them.
                ?.Set("combat_id", _combatId)
                .Set("attempt_id", _combatId == null ? (int?)null : ReplayRecorder.AttemptId)
                .Set("n", Reflect.GetInt(__0, "RoundNumber", 0))
                .Set("side", "player")
                .Emit();
        }
        catch { }
    }

    private static void SideTurnStart(object __0, object __1)
    {
        try
        {
            var side = __1?.ToString()?.ToLowerInvariant();
            if (side == "player") return; // already emitted by PlayerTurnStart
            ReplayRecorder.Line("turn")
                // attempt_id too, so a turn is self-describing. combat_id alone pools the turns
                // of a fight that was reloaded and retried under one id, and only file position
                // separated them.
                ?.Set("combat_id", _combatId)
                .Set("attempt_id", _combatId == null ? (int?)null : ReplayRecorder.AttemptId)
                .Set("n", Reflect.GetInt(__0, "RoundNumber", 0))
                .Set("side", side)
                .Emit();
        }
        catch { }
    }

    private static void TurnEnd(object __0, object __1)
    {
        try
        {
            ReplayRecorder.Line("end_turn")
                ?.Set("n", Reflect.GetInt(__0, "RoundNumber", 0))
                .Set("side", __1?.ToString()?.ToLowerInvariant())
                .Emit();
        }
        catch { }
    }

    // BeforeCardPlayed(ICombatState combatState, CardPlay cardPlay).
    //
    // Deliberately the BEFORE hook. AfterCardPlayed fires once the card's effects have already
    // resolved, so the first real journal had every `hit` line ordered ahead of the `play` that
    // caused it — a replay stepping forward showed damage landing before the card was played.
    // CardPlay's ResourceInfo is `required init`, so it is fully populated at construction and
    // the cost is available here too.
    //
    // ResourceInfo's field is EnergySpent. The first build read "Energy", which does not exist,
    // so every play recorded cost_paid -1.
    private static void CardPlayed(object __0, object __1)
    {
        try
        {
            var card = Reflect.GetMember(__1, "Card");
            var target = Reflect.GetMember(__1, "Target");
            var resources = Reflect.GetMember(__1, "Resources");
            var origin = CardInstances.DeckIdOf(card);
            ReplayRecorder.Line("play")
                ?.Set("c", CardInstances.Of(card))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(card, "Id")))
                .Set("up", Reflect.GetInt(card, "CurrentUpgradeLevel", 0))
                // Model id only. Minting a CardInstances id for a Creature would consume ids
                // from the card sequence and corrupt card_instances downstream, so two
                // identical enemies are deliberately not distinguished here.
                .Set("target", target == null ? null : Ids.Bare(Reflect.GetString(target, "ModelId")))
                .Set("cost_paid", Reflect.GetInt(resources, "EnergySpent", -1))
                .Set("stars_paid", Reflect.GetInt(resources, "StarsSpent", 0))
                .SetFlag("auto", Reflect.GetBool(__1, "IsAutoPlay"))
                .Set("play_index", Reflect.GetInt(__1, "PlayIndex", 0))
                .Set("play_count", Reflect.GetInt(__1, "PlayCount", 1))
                .Set("turn", Reflect.GetInt(__0, "RoundNumber", 0))
                .Emit();
        }
        catch { }
    }

    private static void CardDrawn(object __2) => CardMove("draw", __2);
    private static void CardDiscarded(object __2) => CardMove("discard", __2);
    private static void CardExhausted(object __2) => CardMove("exhaust", __2);

    private static void CardMove(string kind, object card)
    {
        try
        {
            // deck_c is the DECK instance this combat copy came from; absent for cards with no
            // deck ancestor (Slimed, Wound, Dazed). `c` alone is combat-scoped and never joins
            // to card_instances.
            var origin = CardInstances.DeckIdOf(card);
            ReplayRecorder.Line(kind)
                ?.Set("c", CardInstances.Of(card))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(card, "Id")))
                .Emit();
        }
        catch { }
    }

    // CombatState.CloneCard(CardModel mutableCard) -> CardModel. Synchronous, so a postfix sees
    // the finished copy.
    private static void CardCloned(object __0, object __result)
    {
        try { CardInstances.LinkClone(__0, __result); } catch { }
    }

    // AfterCardGeneratedForCombat(ICombatState, CardModel card, Player? creator)
    private static void CardGenerated(object __1)
    {
        try
        {
            var origin = CardInstances.DeckIdOf(__1);
            ReplayRecorder.Line("generate")
                ?.Set("c", CardInstances.Of(__1))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Emit();
        }
        catch { }
    }

    private static void Shuffled()
    {
        try { ReplayRecorder.Line("shuffle")?.Emit(); } catch { }
    }

    // AfterDamageGiven(choiceContext, combatState, dealer, DamageResult, props, target, cardSource).
    //
    // Fires for EVERY dealer, enemies included, so the dealer is read rather than assumed. The
    // first build hardcoded src="player" and produced lines claiming the player hit themselves
    // for 0 while AfterDamageReceived logged the same blow again from the enemy: one event,
    // recorded twice, both wrong.
    //
    // DamageResult's fields are UnblockedDamage / BlockedDamage. The first build read HpLost /
    // BlockLost, which do not exist on the type, so every hit in the first real journal
    // recorded 0 damage. Reflection misses are silent by design here, which is exactly why a
    // wrong field name produces plausible-looking zeros instead of an error.
    private static void DamageGiven(object __2, object __3, object __5, object __6)
    {
        try
        {
            var dealerIsPlayer = Reflect.GetMember(__2, "IsPlayer") is true;
            var targetIsPlayer = Reflect.GetMember(__5, "IsPlayer") is true;
            if (targetIsPlayer && Reflect.GetBool(__3, "WasTargetKilled")) PlayerDied = true;
            // Actual HP reduction only: unblocked damage, and only while a fight this journal
            // session actually saw start. Blocked damage is excluded and healing never subtracts,
            // so this is "HP lost in combat", not "net HP change".
            if (targetIsPlayer && _hpLostInCombat is { } lost)
                _hpLostInCombat = lost + Reflect.GetInt(__3, "UnblockedDamage", 0);
            ReplayRecorder.Line("hit")
                // A null dealer is an effect (poison, thorns, disintegration), not a missing
                // read. Naming it keeps that distinguishable from a failed reflection.
                ?.Set("src", dealerIsPlayer ? "player"
                        : __2 == null ? "effect" : Ids.Bare(Reflect.GetString(__2, "ModelId")))
                .Set("dst", targetIsPlayer ? "player" : Ids.Bare(Reflect.GetString(__5, "ModelId")))
                .Set("dmg", Reflect.GetInt(__3, "UnblockedDamage", 0))
                .Set("blocked", Reflect.GetInt(__3, "BlockedDamage", 0))
                .SetFlag("killed", Reflect.GetBool(__3, "WasTargetKilled"))
                .Set("card", __6 == null ? null : Ids.Bare(Reflect.GetString(__6, "Id")))
                .Emit();
        }
        catch { }
    }

    // AfterDamageReceived(choiceContext, runState, combatState, target, DamageResult, props,
    // dealer, cardSource). In combat this is the same blow DamageGiven already recorded, so it
    // only emits OUT of combat (combatState null): event damage, which DamageGiven never sees.
    private static void DamageReceived(object __2, object __3, object __4)
    {
        try
        {
            if (__2 != null) return; // in combat: DamageGiven owns it
            if (Reflect.GetMember(__3, "IsPlayer") is not true) return;
            ReplayRecorder.Line("hp_loss")
                ?.Set("dmg", Reflect.GetInt(__4, "UnblockedDamage", 0))
                .Set("blocked", Reflect.GetInt(__4, "BlockedDamage", 0))
                .Emit();
        }
        catch { }
    }

    // AfterPowerAmountChanged(combatState, choiceContext, PowerModel, amount, applier, cardSource)
    private static void PowerChanged(object __2, decimal __3, object __4)
    {
        try
        {
            ReplayRecorder.Line("power")
                // Who applied it. Without this a relic or thorns ticking on the enemy turn
                // looked identical to a monster buffing itself, because tgt was the only clue.
                // Same convention as hit.src: a null applier is an effect, not a failed read.
                ?.Set("src", CreatureRef(__4))
                .Set("id", Ids.Bare(Reflect.GetString(__2, "Id")))
                .Set("n", (int)__3)
                .Set("tgt", Ids.Bare(Reflect.GetString(Reflect.GetMember(__2, "Owner"), "ModelId")))
                .Emit();
        }
        catch { }
    }

    // CreatureCmd.Heal(creature, amount, playAnim). Patched directly because the game's
    // AfterCurrentHpChanged did not produce an hp line for a rest-site heal, so four rest
    // floors in a real journal recorded the option taken and never the amount. Prefix, so the
    // amount is the one being applied rather than one re-derived after the fact.
    private static void Healed(object __0, decimal __1)
    {
        try
        {
            if (Reflect.GetMember(__0, "IsPlayer") is not true) return;
            var amount = (int)__1;
            if (amount == 0) return;
            ReplayRecorder.Line("hp")
                ?.Set("d", amount)
                .Set("hp", Reflect.GetInt(__0, "CurrentHp", 0) + amount)
                .Set("src", "heal")
                .Emit();
        }
        catch { }
    }

    // AfterBlockGained(combatState, creature, amount, props, cardSource)
    private static void BlockGained(object __1, decimal __2, object __4)
    {
        try
        {
            // Monster block is recorded too. This used to return early for anything but the
            // player, so a monster that spent its turn gaining Block left nothing in the
            // journal at all and the turn read as "nothing recorded".
            ReplayRecorder.Line("block")
                ?.Set("src", CreatureRef(__1))
                .Set("n", (int)__2)
                .Set("card", __4 == null ? null : Ids.Bare(Reflect.GetString(__4, "Id")))
                .Emit();
        }
        catch { }
    }

    // AfterCurrentHpChanged(runState, combatState, creature, delta) — every HP delta, not just
    // combat hits, so event and campfire HP movement is in the record too.
    private static void HpChanged(object __2, decimal __3)
    {
        try
        {
            if (Reflect.GetMember(__2, "IsPlayer") is not true) return;
            ReplayRecorder.Line("hp")
                ?.Set("d", (int)__3)
                .Set("hp", Reflect.GetInt(__2, "CurrentHp", 0))
                .Emit();
        }
        catch { }
    }

    private static void GoldGained(object __1)
    {
        try
        {
            ReplayRecorder.Line("gold")
                ?.Set("gold", Reflect.GetInt(__1, "Gold", 0))
                .Emit();
        }
        catch { }
    }

    private static void OrbChanneled(object __3) => Orb("orb_channel", __3);
    private static void OrbEvoked(object __2) => Orb("orb_evoke", __2);

    private static void Orb(string kind, object orb)
    {
        try
        {
            ReplayRecorder.Line(kind)?.Set("id", Ids.Bare(Reflect.GetString(orb, "Id"))).Emit();
        }
        catch { }
    }

    // --- decisions -------------------------------------------------------------------

    // The card-reward offer. One `decision` line plus one `option` row per card, carrying
    // presented/selectable and the reasons, so the solver can tell a rejected alternative
    // from one that was never available.
    private static void CardRewardPopulated(object __instance)
    {
        try
        {
            if (ReplayRecorder.Line("decision") is not { } line) return;
            var generation = 0;
            if (_pendingReroll > 0 && _decision > 0)
            {
                generation = _pendingReroll; // same decision, next generation of its offer
            }
            else
            {
                DemoteDecision();
                _decision = ReplayRecorder.NextDecisionId();
            }
            _pendingReroll = 0;
            _decisionType = "card_reward";
            _offerIndex = new Dictionary<string, int>();
            _ambiguousOffers = new HashSet<string>();

            var options = new List<ReplayLine>();
            var i = 0;
            foreach (var card in Enumerate(Reflect.GetMember(__instance, "Cards")))
            {
                var offerId = Ids.Bare(Reflect.GetString(card, "Id")) ?? "";
                // A reward can offer the same card twice (colorless picks, an upgraded copy
                // beside a plain one). Record the collision instead of letting the second
                // overwrite the first and mis-attribute the pick.
                if (!(_offerIndex ??= new Dictionary<string, int>()).TryAdd(offerId, i))
                    (_ambiguousOffers ??= new HashSet<string>()).Add(offerId);
                options.Add(new ReplayLine("o")
                    .Set("option_index", i++)
                    .Set("option_kind", "card")
                    .Set("option_id", Ids.Bare(Reflect.GetString(card, "Id")))
                    .Set("instance_id", CardInstances.Of(card))
                    .Set("up", Reflect.GetInt(card, "CurrentUpgradeLevel", 0))
                    .SetFlag("presented", true)
                    .SetFlag("selectable", true));
            }

            line.Set("decision_id", _decision)
                .Set("decision_type", _decisionType)
                .Set("source", "reward")
                .Set("offer_generation", generation)
                .Set("n_presented", options.Count)
                .Set("n_selectable", options.Count)
                .SetFlag("decline_available", Reflect.GetBool(__instance, "CanSkip", true))
                .SetFlag("can_reroll", Reflect.GetBool(__instance, "CanReroll"))
                .Set("options", options)
                .Emit();
        }
        catch { }
    }

    private static void CardRewardSkipped()
    {
        try
        {
            ReplayRecorder.Line("outcome")
                ?.Set("decision_id", _decision)
                .Set("decision_type", _decisionType)
                .Set("outcome", "skip")
                .Emit();
            DemoteDecision(); // resolved: its offers must not claim a later grant
        }
        catch { }
    }

    // A reroll rejects everything currently shown. Populate fires again afterwards, so the
    // next offer is a new generation of the SAME decision rather than a new decision.
    private static void CardRewardRerolled()
    {
        try
        {
            _pendingReroll++;
            ReplayRecorder.Line("outcome")
                ?.Set("decision_id", _decision)
                .Set("decision_type", _decisionType)
                .Set("outcome", "reroll")
                .Set("offer_generation", _pendingReroll - 1)
                .Emit();
        }
        catch { }
    }

    // Map CardSelectorPrefs.Prompt's loc key to the actual intent. Every selection screen
    // carries prefs, so this reads the same for all of them; the earlier version labelled
    // every option "remove", which would have fed campfire upgrades and event transforms
    // straight into Removal Elo as if they were removal alternatives.
    private static string SelectKind(object? prefs)
    {
        // LocString exposes LocEntryKey (and LocTable); there is no "Key". Reading the wrong
        // name made every deck selection land as "unknown", which is the safe direction but
        // still loses the removal/upgrade/transform distinction Removal Elo depends on.
        var key = Reflect.GetString(Reflect.GetMember(prefs, "Prompt"), "LocEntryKey") ?? "";
        if (key.Contains("REMOVE")) return "remove";
        if (key.Contains("UPGRADE")) return "upgrade";
        if (key.Contains("TRANSFORM")) return "transform";
        if (key.Contains("EXHAUST")) return "exhaust";
        if (key.Contains("ENCHANT")) return "enchant";
        if (key.Contains("DISCARD")) return "discard";
        return "unknown"; // never silently "remove": a miss must not look like a removal offer
    }

    // CardSelectCmd.FromDeckGeneric(player, prefs, filter, sort) — the deck funnel behind
    // removal and the event picks that route through it. Its prefix is where the genuinely
    // eligible set is known, filter applied.
    private static void DeckSelectOffered(object __0, object __1, object __2)
    {
        try
        {
            var deck = Reflect.GetMember(Reflect.GetMember(__0, "Deck"), "Cards");
            OpenSelectOffer("deck_select", SelectKind(__1), __0, __1, Enumerate(deck), __2)?.Emit();
        }
        catch { }
    }

    // CardSelectCmd.FromDeckForEnchantment(cards, enchantment, amount, prefs) — the terminal
    // overload, so this fires once whichever of the three a relic or event called.
    //
    // The presented set arrives already filtered by CanEnchant (and by the caller's own
    // additionalFilter, on the overload that takes one), so it IS the eligible set and there is
    // no filter left to apply: every option row is selectable by construction.
    //
    // SelectKind needs no new case. CardSelectorPrefs.EnchantSelectionPrompt is
    // LocString("card_selection", "TO_ENCHANT") and SelectKind already matches on
    // Contains("ENCHANT") — that branch has simply been unreachable, because the only patched
    // entry point never carried an enchant prompt. decision_type has been deck_select_remove
    // and nothing else for the life of the replay.
    private static void EnchantSelectOffered(object __0, object __1, int __2, object __3)
    {
        try
        {
            var cards = new List<object>(Enumerate(__0));
            // The game shows no screen for an empty set and does not log a choice for one
            // either (its LogChoice call is guarded on cards.Count > 0). Minting a decision
            // nothing can resolve would flush an outcome of "decline" at the next offer, so an
            // offer of nothing records nothing. The enchant row still fires if the deck changes.
            if (cards.Count == 0) return;

            // Recorded even when the eligible set is no larger than MinSelect and the game
            // auto-selects without a screen: n_presented against min_select is how a consumer
            // sees the player was never asked, and the pick still needs an offer to join to.
            OpenSelectOffer("deck_select", SelectKind(__3), Reflect.GetMember(cards[0], "Owner"),
                            __3, cards, filter: null)
                ?.Set("enchantment", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Set("amount", __2)
                .Emit();
        }
        catch { }
    }

    // The shared body behind a card-selection offer: one nested option row per presented card,
    // carrying the instance id that joins it to the rest of the run and whether this screen
    // would let it be chosen.
    //
    // Split out of DeckSelectOffered because FromDeckGeneric is one of ELEVEN public entry
    // points on CardSelectCmd, and the enchantment screen below is a second. The nine still
    // unpatched — hand, combat pile, upgrade, transform, the two grids, bundles — present their
    // own sets and do not delegate here, so each needs its own prefix. They differ only in where
    // the presented set and the player come from, so whatever is the same now lives here.
    //
    // Returns the decision line UNEMITTED so a caller can add the fields only it knows before
    // emitting. Null means no run is being recorded, and on that path no decision id is minted
    // and no outcome flushed: keeping the journal check first stops an unrecorded session from
    // advancing decision state nothing will ever read.
    private static ReplayLine? OpenSelectOffer(string source, string kind, object? player,
                                               object? prefs, IEnumerable<object> cards,
                                               object? filter)
    {
        if (ReplayRecorder.Line("decision") is not { } line) return null;
        FlushSelectOutcome(); // a previous select closes here, still under ITS decision id
        _decision = ReplayRecorder.NextDecisionId();
        _decisionType = source + "_" + kind;

        var selectableCount = 0;
        // Empty, not null: from here on a select IS open, so its outcome line is owed even
        // if the player picks nothing.
        _selectByInstance = new Dictionary<int, int>();
        _selected = new List<int>();

        var options = new List<ReplayLine>();
        var i = 0;
        foreach (var card in cards)
        {
            var id = CardInstances.Of(card);
            // filter is the caller's Func<CardModel, bool>, already composed with the screen's
            // own eligibility rule by the entry point (IsRemovable for removal, CanEnchant for
            // enchantment). Card-selection predicates are pure, so invoking one here observes
            // eligibility without changing anything. A null filter means the presented set is
            // already the eligible set.
            var selectable = filter == null
                || Reflect.CallWith(filter, "Invoke", card) is true;
            if (selectable) selectableCount++;
            if (id != 0) _selectByInstance[id] = i;
            var row = new ReplayLine("o")
                .Set("option_index", i++)
                .Set("option_kind", kind)
                .Set("option_id", Ids.Bare(Reflect.GetString(card, "Id")))
                .Set("instance_id", id)
                .Set("up", Reflect.GetInt(card, "CurrentUpgradeLevel", 0))
                .SetFlag("presented", true)
                .SetFlag("selectable", selectable);
            if (!selectable)
                row.Set("selectable_reason",
                    Reflect.GetBool(card, "IsRemovable", true) ? "filtered" : "eternal");
            options.Add(row);
        }

        return line.Set("decision_id", _decision)
            .Set("decision_type", _decisionType)
            .Set("source", source)
            .Set("select_kind", kind)
            .Set("min_select", Reflect.GetInt(prefs, "MinSelect", 1))
            .Set("max_select", Reflect.GetInt(prefs, "MaxSelect", 1))
            .SetFlag("decline_available", Reflect.GetBool(prefs, "Cancelable"))
            .Set("n_presented", options.Count)
            .Set("n_selectable", selectableCount)
            .Set("gold_on_hand", Reflect.GetInt(player, "Gold", 0))
            .Set("options", options);
    }

    // CardSelectCmd.LogChoice(Player, IEnumerable<CardModel?>) — the private funnel ALL eleven
    // entry points converge on, where the game takes the cards the screen returned, formats
    // them as English and puts them in a text log. It is the one place "what did the player
    // actually pick" exists for every selection screen at once.
    //
    // Until now a pick was only ever read back from its consequence: removal from
    // BeforeCardRemoved, upgrade and transform from their own hooks. Screens whose consequence
    // has no hook resolved to nothing, and an offer that resolved to nothing flushed an outcome
    // of "decline" — not an absence of data but a wrong answer.
    //
    // PREFIX, not postfix. LogChoice enumerates the sequence itself, so reading it first is
    // guaranteed to see the full set. Re-enumerating is safe by construction because the game
    // already does it twice: every entry point ends `LogChoice(player, enumerable); return
    // enumerable;` and its own caller then enumerates what came back.
    private static void SelectionReturned(object __1)
    {
        try
        {
            var picked = new List<ReplayLine>();
            // Whether a returned card was actually in the offer we recorded. CardInstances.Of
            // mints on first sight, so a null index means "not in this offer" rather than "not
            // seen before", which makes it a proof of provenance: one card found in
            // _selectByInstance is one card this screen took from a set we wrote down.
            var joined = false;
            foreach (var card in Enumerate(__1))
            {
                // Registers the pick against the open offer, which is what makes the outcome
                // line's selected_option_indices a record rather than a guess.
                var optionIndex = SelectIndexOf(card);
                if (optionIndex != null) joined = true;
                picked.Add(new ReplayLine("p")
                    .Set("option_index", optionIndex)
                    .Set("c", CardInstances.Of(card))
                    .Set("id", Ids.Bare(Reflect.GetString(card, "Id")))
                    .Set("up", Reflect.GetInt(card, "CurrentUpgradeLevel", 0)));
            }

            ReplayRecorder.Line("pick")
                // decision_id ONLY on a proven join. This patch turns the result on for all
                // eleven screens at once while only one of them records its offer, so ten of
                // them would otherwise inherit whatever decision was last open — a card reward
                // three rooms back claiming to be the Retain pick. An unjoined pick names its
                // cards and stays silent about the offer; `outcome` remains the authority on
                // whether a recorded offer was declined.
                ?.Set("decision_id", joined ? _decision : (int?)null)
                .Set("decision_type", joined ? _decisionType : null)
                .Set("n_picked", picked.Count)
                .Set("cards", picked)
                .Emit();
        }
        catch { }
    }

    private static void CardRemoved(object __1)
    {
        try
        {
            ReplayRecorder.Line("remove")
                ?.Set("decision_id", _decision)
                .Set("option_index", SelectIndexOf(__1))
                .Set("c", CardInstances.Of(__1))
                .Set("id", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Emit();
        }
        catch { }
    }

    // AfterItemPurchased(runState, player, MerchantEntry, goldSpent). The ware is read from
    // what PurchaseAttempt captured, because the entry has already been cleared by now.
    private static void ItemPurchased(object __1, object __2, int __3)
    {
        try
        {
            var kind = _pendingBuyKind
                ?? (__2.GetType().Name.Contains("CardRemoval") ? "removal_service" : "other");
            var id = _pendingBuyId;
            _pendingBuyId = null;
            _pendingBuyKind = null;

            ReplayRecorder.Line("buy")
                ?.Set("decision_id", _decision > 0 ? _decision : (int?)null)
                .Set("kind", kind)
                // Which stock entry this was, indexed the same way the shop line lists them, so
                // a purchase still matches when the same card is on the shelf twice.
                .Set("slot", ShopSlot(__1, __2, kind))
                .Set("id", id)
                .Set("cost_current", __3)
                .Set("cost_resource", "gold")
                .Set("gold_on_hand", Reflect.GetInt(__1, "Gold", 0))
                .Emit();
        }
        catch { }
    }

    // The purchased entry's index within its kind, matching the shop line's ordering exactly:
    // cards are character entries then colorless (the order RoomExport.ReadShop concatenates
    // them in), relics and potions in their own lists. "removal" has no index. Null when the
    // inventory can't be resolved, rather than a guessed 0.
    private static int? ShopSlot(object player, object entry, string kind)
    {
        try
        {
            if (kind == "removal_service") return null;
            var room = Reflect.GetMember(Reflect.GetMember(player, "RunState"), "CurrentRoom");
            var inv = Reflect.Call(room, "GetLocalInventory");
            if (inv == null) return null;

            var lists = kind switch
            {
                "card" => new[] { "CharacterCardEntries", "ColorlessCardEntries" },
                "relic" => new[] { "RelicEntries" },
                "potion" => new[] { "PotionEntries" },
                _ => Array.Empty<string>(),
            };
            var i = 0;
            foreach (var listName in lists)
            {
                foreach (var e in Enumerate(Reflect.GetMember(inv, listName)))
                {
                    if (ReferenceEquals(e, entry)) return i;
                    i++;
                }
            }
        }
        catch { }
        return null;
    }

    // AfterRewardTaken(runState, player, Reward) — the resolution half of a reward decision.
    private static void RewardTaken(object __2)
    {
        try
        {
            var kind = __2.GetType().Name;
            var line = ReplayRecorder.Line("resolve");
            if (line == null) return;
            line.Set("decision_id", _decision > 0 ? _decision : (int?)null)
                .Set("reward_kind", kind);
            switch (kind)
            {
                case "PotionReward":
                    line.Set("id", Ids.Bare(Reflect.GetString(Reflect.GetMember(__2, "Potion"), "Id")));
                    break;
                case "GoldReward":
                    line.Set("gold", Reflect.GetInt(__2, "Amount", 0));
                    break;
            }
            line.Emit();
        }
        catch { }
    }

    // AfterCardChangedPiles(runState, combatState, card, oldPile, clonedBy). Out of combat with
    // oldPile None, this is a card entering the run.
    //
    // Not every card that enters a deck is a PICK. Curses from events, cards added by relics or
    // run modifiers, and anything else granted without a choice must never be counted as
    // revealed preference. So decision_id is written only when a decision is actually open, and
    // `source` says where the card came from; a null decision_id with source "granted" is the
    // shape the solver has to exclude.
    private static void CardChangedPiles(object __0, object __1, object __2, object __3)
    {
        try
        {
            if (__1 != null) return;
            if (__3?.ToString() != "None") return;
            var room = Reflect.GetMember(__0, "CurrentRoom");
            if (room == null) return; // run-start deck setup, already in header.starting_deck

            var acquiredId = Ids.Bare(Reflect.GetString(__2, "Id"));
            // Current decision first, then the one just demoted: a pick that resolves after the
            // room changed still belongs to the offer it came from.
            int? optionIndex = null;
            var decisionForCard = 0;
            if (acquiredId != null && _ambiguousOffers?.Contains(acquiredId) != true)
            {
                if (_offerIndex != null && _offerIndex.TryGetValue(acquiredId, out var oi))
                { optionIndex = oi; decisionForCard = _decision; }
                else if (_prevOfferIndex != null && _prevOfferIndex.TryGetValue(acquiredId, out var po))
                { optionIndex = po; decisionForCard = _prevDecision; }
            }
            var offered = optionIndex != null;
            var roomName = room.GetType().Name;
            var source = offered ? "reward"
                : roomName == "MerchantRoom" ? "shop"
                : roomName == "EventRoom" ? "event"
                : "granted";

            ReplayRecorder.Line("acquire")
                // Only a real decision, never the 0 sentinel: a card granted outside a choice
                // has no choice set and must not be joined to whatever decision came last.
                // "granted" means no choice was made, so it never carries a decision — a card
                // stolen and returned mid-combat was inheriting whatever reward happened to be
                // open and would have read as a pick.
                ?.Set("decision_id", offered ? decisionForCard
                        : source == "granted" ? (int?)null
                        : _decision > 0 ? _decision : (int?)null)
                .Set("source", source)
                .Set("c", CardInstances.Of(__2))
                .Set("id", acquiredId)
                // Which offered option this resolves, so `chosen` is a fact rather than an
                // inference from "a resolution happened near this decision".
                .Set("option_index", optionIndex)
                .Emit();
            // A reward resolves once. Retiring the offer here stops a card of the same type
            // granted later (an event curse, a relic's gift) from inheriting "reward" and an
            // option_index it never earned.
            if (offered && decisionForCard == _decision) DemoteDecision();
        }
        catch { }
    }

    // Upgrades mutate the CardModel in place, so the instance id is unchanged and lineage
    // survives. This line records that it happened, not a new identity.
    private static void CardUpgraded(object __0)
    {
        try
        {
            ReplayRecorder.Line("upgrade")
                ?.Set("decision_id", _decision > 0 ? _decision : (int?)null)
                .Set("option_index", SelectIndexOf(__0))
                .Set("c", CardInstances.Of(__0))
                .Set("id", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Emit();
        }
        catch { }
    }

    // CardCmd.Enchant(enchantment, card, amount) -> EnchantmentModel?. The only witness
    // enchantment has: the deck mutation with no hook, no resolution row and, until the offer
    // patch above, no decision row either.
    //
    // Enchant ADDS to an existing enchantment of the same type and throws on a different one,
    // so a card carries at most one and a second row for the same card is a stack, not a
    // replacement. `amount` is what this call applied; `amount_total` is the enchantment's
    // amount afterwards, read off the returned model, so a consumer grading against the game's
    // own save (players[].deck[].enchantment.amount) compares a value rather than folding rows.
    private static void CardEnchanted(object __0, object __1, decimal __2, object? __result)
    {
        try
        {
            // The game reports the enchantment that landed. A null return means none did, and
            // claiming an enchantment the deck did not take is the one failure mode here that
            // would look like data.
            if (__result == null) return;

            // Join ONLY to an enchantment offer. A relic that sweeps the deck can fire while an
            // unrelated removal select is still open, and the deck cards it touches are in that
            // offer's instance map too — so an ungated lookup would hand the sweep a removal's
            // option_index and register a pick against a decision the player never made.
            // Omitting both fields is also how a consumer tells a choice from a sweep.
            var offered = _decisionType == EnchantSelectType;
            var optionIndex = offered ? SelectIndexOf(__1) : null;

            ReplayRecorder.Line("enchant")
                ?.Set("decision_id", optionIndex != null ? _decision : (int?)null)
                .Set("option_index", optionIndex)
                .Set("c", CardInstances.Of(__1))
                .Set("id", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Set("enchantment", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Set("amount", __2)
                .Set("amount_total", Reflect.GetInt(__result, "Amount", 0))
                // Where the card was when the enchantment landed — the same gate the game
                // itself uses, since Enchant adds to CardsEnchanted only when the pile is the
                // Deck. Silken Tress is what makes this load-bearing: it clones each card
                // reward option and enchants the clone BEFORE it belongs to any pile, so three
                // cards are enchanted and at most one reaches the deck. Measured on a real run,
                // the game's own cards_enchanted was EMPTY and all three landed under
                // card_choices instead. So `pile: "deck"` is a deck mutation on its own, and
                // anything else means "counts only if an acquire row for the same c follows".
                .Set("pile", PileName(__1))
                .Emit();
        }
        catch { }
    }

    // The lowercased PileType of a card's current pile, or "none" when it is in no pile at
    // all, which is a freshly cloned reward option. CardChangedPiles already compares this
    // enum by ToString(), so this reads it the same way.
    private static string PileName(object? card)
    {
        var pile = Reflect.GetMember(card, "Pile");
        var type = pile == null ? "None" : Reflect.GetString(pile, "Type");
        return (type ?? "unknown").ToLowerInvariant();
    }

    // Transform genuinely swaps objects, so this is the one place the lineage thread would
    // break. Both are in scope here, so the link is explicit.
    private static void CardTransformed(object __0, object __1)
    {
        try
        {
            ReplayRecorder.Line("transform")
                ?.Set("decision_id", _decision)
                // The option index belongs to the card that was CHOSEN, which is the one that
                // was on offer; the replacement was never in the deck when the select opened.
                .Set("option_index", SelectIndexOf(__0))
                .Set("from_c", CardInstances.Of(__0))
                .Set("from_id", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Set("to_c", CardInstances.Of(__1))
                .Set("to_id", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Emit();
        }
        catch { }
    }

    private static void RelicObtained(object __0)
    {
        try
        {
            ReplayRecorder.Line("relic")
                ?.Set("decision_id", _decision > 0 ? _decision : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Emit();
        }
        catch { }
    }

    private static void PotionUsed(object __2) => Potion("potion_used", __2);
    private static void PotionProcured(object __2) => Potion("potion_got", __2);
    private static void PotionDiscarded(object __2) => Potion("potion_dropped", __2);

    private static void Potion(string kind, object potion)
    {
        try
        {
            ReplayRecorder.Line(kind)?.Set("id", Ids.Bare(Reflect.GetString(potion, "Id"))).Emit();
        }
        catch { }
    }

    private static void RestHeal()
    {
        try { ReplayRecorder.Line("rest")?.Set("option", "heal").Emit(); } catch { }
    }

    private static void RestSmith()
    {
        try { ReplayRecorder.Line("rest")?.Set("option", "smith").Emit(); } catch { }
    }

    // EventOption.Chosen(). The game gives us the option that WAS taken and no hook for the
    // page being shown, so the first real journal recorded event outcomes against decision_id
    // 0 — an outcome with no choice set, which is precisely the "rejection is indistinguishable
    // from missing data" failure the schema review warned about.
    //
    // The offer is recovered from the live snapshot, which already parses the event page's
    // full option list (Sts2Access builds EventInfo, including each option's Locked flag).
    // It is at most one producer tick stale, and an event page cannot change in the 100ms
    // before the player clicks it.
    private static void EventOptionChosen(object __instance)
    {
        try
        {
            var chosenKey = Reflect.GetString(__instance, "TextKey");
            var label = Reflect.CallString(Reflect.GetMember(__instance, "Title"), "GetFormattedText");
            if (string.IsNullOrWhiteSpace(label)) label = chosenKey;

            // Open the decision for this page the first time an option on it is taken.
            var page = Producer.LiveStateProducer.Latest?.Event is { } e0
                ? $"{e0.Id}|{e0.Options.Count}|{(e0.Options.Count > 0 ? e0.Options[0].Key : "")}"
                : null;
            if (Producer.LiveStateProducer.Latest?.Event is { } ev && page != _eventPage)
            {
                _eventPage = page;
                _decision = ReplayRecorder.NextDecisionId();
                _decisionType = "event";
                var options = new List<ReplayLine>();
                var i = 0;
                foreach (var o in ev.Options)
                {
                    var row = new ReplayLine("o")
                        .Set("option_index", i++)
                        .Set("option_kind", "event_option")
                        .Set("option_id", o.Key)
                        .Set("label", o.Text)
                        .Set("desc", o.Desc)
                        .Set("grants_card", o.Card)
                        .Set("grants_relic", o.Relic)
                        .SetFlag("presented", true)
                        .SetFlag("selectable", !o.Locked);
                    if (o.Locked) row.Set("selectable_reason", "locked");
                    options.Add(row);
                }
                ReplayRecorder.Line("decision")
                    ?.Set("decision_id", _decision)
                    .Set("decision_type", "event")
                    .Set("source", "event")
                    .Set("event_id", ev.Id)
                    .Set("n_presented", options.Count)
                    .Set("n_selectable", options.Count(o => o.Fields["selectable"] is true))
                    .Set("options", options)
                    .Emit();
            }

            ReplayRecorder.Line("outcome")
                ?.Set("decision_id", _decision)
                .Set("decision_type", "event")
                .Set("outcome", "chosen")
                .Set("option_id", chosenKey)
                .Set("label", label)
                .Emit();
        }
        catch { }
    }

    // --- helpers ---------------------------------------------------------------------

    // MapCoord.ToString() renders as "MapCoord (0, 1)". Strip it to "0,1": the prefix repeats
    // on every node and every child edge, and a bare pair is what a consumer wants to join on.
    private static string? Coord(object? coord)
    {
        var raw = coord?.ToString();
        if (raw == null) return null;
        var open = raw.IndexOf('(');
        var close = raw.IndexOf(')');
        if (open < 0 || close <= open) return raw;
        return raw.Substring(open + 1, close - open - 1).Replace(" ", "");
    }

    // Game collections come back as opaque objects; enumerate defensively so a shape change
    // yields an empty list rather than an exception inside a hook.
    // Acts[i] without assuming the collection is an IList: IReadOnlyList<ActModel> is, but we
    // compile against none of these types and an indexer read that silently misses is exactly
    // the failure this whole feature keeps having.
    private static object? ElementAt(object? source, int index)
    {
        if (index < 0) return null;
        var i = 0;
        foreach (var item in Enumerate(source))
            if (i++ == index) return item;
        return null;
    }

    private static IEnumerable<object> Enumerate(object? source)
    {
        if (source is not IEnumerable seq) yield break;
        foreach (var item in seq)
            if (item != null) yield return item;
    }
}
