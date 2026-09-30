using System.Runtime.CompilerServices;
using System;
using System.Reflection;
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

    // Who actually answered a selection screen, resolved once at install.
    //
    // CardSelectCmd.Selector is non-null whenever something other than a human is choosing.
    // Whispering Earring pushes a VakuuCardSelector and autoplays up to 13 cards; AutoSlay
    // (the game's own smoke-test harness) pushes an AutoSlayCardSelector for a whole run. Both
    // resolve through Selector.GetSelectedCards and then fall into the same LogChoice we
    // prefix, so without this the journal presents a machine's pick as the player's own.
    //
    // A co-op partner's screens are separated by the mod's existing LocalPlayer helper. Both
    // clients process both players' selections: when the partner picks, CardSelectCmd here
    // takes the WaitForRemoteChoice branch and still calls LogChoice, so their pick lands in
    // our journal looking like ours.
    //
    // Held as reflection handles rather than read through Reflect.GetStatic because null is a
    // MEANINGFUL value here -- Selector reads null exactly when the human chose -- and a
    // renamed member must be distinguishable from that. Unresolved means the field is omitted.
    private static PropertyInfo? _selectorProp;

    // Whether CardModel.Keywords still exists, resolved once at install for the same reason.
    //
    // Keywords is EMPTY for most cards, so "this card has no keywords" and "the member was
    // renamed" would both reach the play row as an absent field, and a consumer would read the
    // absence as "not Ethereal". Knowing the member is still there is what makes the absence mean
    // the first one. A bool rather than a handle: the read itself goes through Reflect so the
    // combat-modified set is resolved per card, not cached.
    private static bool _keywordsResolved;

    // What OpenSelectOffer composes for the enchantment screen, "deck_select" + "enchant".
    // Named because CardEnchanted has to tell an enchant offer from any other open select
    // before it joins to one.
    private const string EnchantSelectType = "deck_select_enchant";

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
        // Combat-lifecycle latches. The recorder has already been bitten by a latch that was
        // never cleared: quit to menu then Continue in the same process silently dropped every
        // remaining line of the run. So both of these are cleared on every journal open as well
        // as at combat_start and when combat_end is written.
        _combatWon = false;
        _extraTurnPending = null;
        _selectByInstance = null;
        _selected = null;
        _selectDecision = 0;
        _selectDecisionType = null;
        // Event page state. A second run in the same process would otherwise start with the last
        // run's event id latched and its page numbering continuing, and BeginEvent only clears
        // them once the new run reaches its first event.
        _eventDecision = 0;
        _eventPageIndex = -1;
        _eventId = null;
        ResetAttacks(); // attacks
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
        var owner = _selectDecision;
        var ownerType = _selectDecisionType;
        _selected = null;
        _selectByInstance = null;
        _selectDecision = 0;
        _selectDecisionType = null;
        if (picked == null || owner <= 0) return;
        ReplayRecorder.Line("outcome")
            ?.Set("decision_id", owner)
            .Set("decision_type", ownerType)
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

        // --- card flow ---
        // End of turn. FlushPlayerHand moves the hand with CardPileCmd.Add, which never reaches
        // AfterCardDiscarded, so the flush and every retain were invisible. AfterFlush hands over
        // both lists already split. Prefix: it is async and the lists are final before it runs.
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterFlush", me, nameof(HandFlushed));
        // The fight's opening draw order. A second prefix on BeforeCombatStart, registered after
        // CombatStart's, so the row lands right behind combat_start at the same instant rng_state
        // is stamped. Harmony runs same-priority prefixes in registration order.
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeCombatStart", me, nameof(OpeningDrawOrder));
        // Afflictions have no notification hook (ShouldAfflict is a veto). The non-generic
        // Afflict is synchronous (Task.FromResult) and Afflict<T> delegates to it, so one postfix
        // sees every applied affliction and its result. ClearAffliction is a plain void.
        var cardCmd = HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardCmd");
        attempted++; n += HookPatcher.PatchOn(harmony, cardCmd, "Afflict", me, nameof(CardAfflicted), 3,
                                 firstParamType: "AfflictionModel", postfix: true);
        attempted++; n += HookPatcher.PatchOn(harmony, cardCmd, "ClearAffliction", me, nameof(AfflictionCleared), 1,
                                 firstParamType: "CardModel");
        // Hook.ShouldPlay refusals on the autoplay path. Postfix so the out preventer is set.
        attempted++; n += HookPatcher.Patch(harmony, hook, "ShouldPlay", me, nameof(PlayRefused), postfix: true);
        _autoPlayType = HookPatcher.FindType("MegaCrit.Sts2.Core.Entities.Cards.AutoPlayType");

        // --- block loss --------------------------------------------------------------
        // Block only ever went UP in the journal: 34531 gain rows and not one row anywhere that
        // removed any of it, so a consumer summing them got a level that never reset and "how
        // much block did I have when that hit landed" had no answer. These five cover every way
        // block leaves a creature except the block damage eats, which the hit row already carries
        // as `blocked`.
        //
        // Prefix on the private Creature.ClearBlock(), which is only reached from
        // Creature.AfterTurnStart. Hook.AfterBlockCleared is the game's own signal for the
        // turn-start wipe but does not carry the amount and fires after the block is already
        // gone, so the amount has to be latched before the body runs. Prefix and not postfix
        // because ClearBlock is async Task: a postfix fires at its first await, not at
        // completion, and in the prevented branch that await is the hook we also patch below.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Entities.Creatures.Creature"),
            "ClearBlock", me, nameof(BlockClearing), 0);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterBlockCleared", me, nameof(BlockCleared));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPreventingBlockClear", me, nameof(BlockClearPrevented));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterBlockBroken", me, nameof(BlockBroken));
        // Creature.LoseBlockInternal(amount) rather than CreatureCmd.LoseBlock, which is what
        // has no hook and what Expose calls. LoseBlock is async Task and keeps its guards
        // INSIDE the method (combat over or ending, dead creature, non-positive amount), so a
        // prefix there emits rows for strips that never happen, and the amount it is handed is
        // the amount REQUESTED: LoseBlockInternal clamps at zero, so a strip for more block than
        // the creature holds would record more block removed than existed. The internal runs
        // only when the removal actually happens and still sees the pre-clamp Block, so
        // min(amount, Block) is the exact figure.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Entities.Creatures.Creature"),
            "LoseBlockInternal", me, nameof(BlockLost), 1);
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

        // --- max hp and direct hp sets -----------------------------------------------
        //
        // Neither has a first-party hook, so both patch CreatureCmd by name and degrade to
        // "that line kind stops appearing" if a game patch renames them.
        //
        // CreatureCmd.SetMaxHp is the single funnel for every max HP move: GainMaxHp and
        // LoseMaxHp both route through it, and so do SetMaxAndCurrentHp, OstyCmd and
        // TestSubject's HP scaling. Without it a max HP change is not merely missing, it is
        // MISLABELLED. GainMaxHp finishes by calling Heal for the delta, so eating a Mango
        // emits hp src="heal" and cannot be told from a campfire rest; LoseMaxHp damages the
        // overflow away first, so an event penalty emits a hit. Anything adding HP per floor
        // counts both of those twice today.
        //
        // PREFIX on both, for two reasons. They are `async Task`, so a postfix fires at the
        // first await rather than at completion (SetMaxHp's is inside its MaxHp <= 0 Kill
        // branch) and SetMaxHp's __result is the Task, not the decimal delta it returns. And
        // the BEFORE values are the whole point: a prefix is the last moment the old cap and
        // the old current HP still exist, and the delta is named from those.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CreatureCmd"),
            "SetMaxHp", me, nameof(MaxHpSet), 2);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CreatureCmd"),
            "SetCurrentHp", me, nameof(CurrentHpSet), 2);
        // Not a Harmony patch, so deliberately outside the n/attempted tally. CreatureCmd.Heal
        // returns without doing anything when combat is ending and the target is not a player,
        // and now that Healed records monsters too it needs the same guard or it writes a heal
        // that never landed. An unresolved type reads as "not ending", which writes the row: a
        // spare row for a discarded monster heal is a smaller lie than dropping every heal.
        _combatManagerType = HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.CombatManager");
        if (_combatManagerType == null)
            MainFile.Logger.Info("replay-hooks: CombatManager not found; a monster heal during "
                                 + "combat end may emit a spare hp row, and power_lost rows lose post_combat");

        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterGoldGained", me, nameof(GoldGained));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterOrbChanneled", me, nameof(OrbChanneled));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterOrbEvoked", me, nameof(OrbEvoked));

        // --- powers ------------------------------------------------------------------
        // A power LEAVING the board, which no first-party hook reports at all.
        // PowerModel.RemoveInternal is the one method every departure funnels through:
        // PowerCmd.Remove calls it, and so does Creature.RemoveAllPowersInternalExcept, which is
        // what CreatureCmd.Escape, the after-death sweep (RemoveAllPowersAfterDeath) and combat
        // teardown (Player.AfterCombatEnd, CombatManager.Reset) all go through. Patching
        // PowerCmd.Remove instead would cover its 71 explicit call sites and miss every one of
        // those. Concrete and non-virtual on the abstract PowerModel, so one patch covers every
        // power in the game.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Models.PowerModel"),
            "RemoveInternal", me, nameof(PowerRemoved), 0);
        // The amount a change ASKED for, before the modifiers ran. Stashes, never emits.
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforePowerAmountChanged", me, nameof(PowerChanging));
        // The two places an application that landed nothing is still visible. Both emit only in
        // the zero case, so neither adds a line where the journal already has one.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Models.PowerModel"),
            "ApplyInternal", me, nameof(PowerApplied), 3);
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.History.CombatHistory"),
            "PowerReceived", me, nameof(PowerReceived), 4);

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

        // --- transforms ---
        //
        // CardCmd.Transform(IEnumerable<CardTransformation>, Rng?, style) is the one funnel every
        // transform in the game reaches: Transform(original, replacement) and TransformToRandom
        // both wrap their card in a one-element sequence and call it. It used to be patched from
        // the outside on the (CardModel, CardModel) overload instead, which reaches 11 of the
        // game's 22 transform call sites and misses the other 11 -- Claws, Leafy Poultice and
        // Pandora's Box go to the bulk overload directly, and the eight TransformToRandom callers
        // (New Leaf, Entropy, Morphic Grove, Symbiote, Trial, Whispering Hollow, Aroma of Chaos,
        // Endless Conveyor) do not match its name. Measured across the 953 journals: 420 cards turn up in a deck listing with no row
        // anywhere saying how they got there, over 191 deck-to-deck transitions in 47 files, and
        // every single one sits beside an unexplained departure. One Claws visit turning six cards
        // into six MAULs accounts for twelve of them and emitted nothing at all.
        //
        // The bulk overload itself is not patchable from either end. A prefix cannot see the
        // replacements: the random ones do not exist yet (GetReplacement runs inside the loop),
        // and walking the argument to find the rest would be actively destructive -- Pandora's
        // Box passes a lazy Select whose selector calls CardFactory.CreateRandomCardForTransform
        // off Rng.Niche, so enumerating it here would advance the run's RNG and hand the game a
        // different deck than the one we recorded. A postfix is the async trap documented on
        // FromDeckGeneric above: the method is `async Task<IEnumerable<CardPileAddResult>>`, so
        // __result is the Task and the pairs are a local array we never see.
        //
        // So patch the pair of notifications the loop itself calls per transformation:
        // `original.AfterTransformedFrom(); replacement.AfterTransformedTo();`, back to back with
        // no await between them. That lands AFTER Hook.ModifyCardBeingAddedToDeck has had its say
        // and after the replacement is in its pile, which makes it a better row than the old site
        // on two counts beyond coverage. It only fires for a transform that actually happened
        // (the old prefix also emitted for the ones CombatManager.IsEnding silently drops), and
        // to_c names the object that really entered the deck -- Frozen, Molten and Toxic Egg and
        // Fresnel Lens all answer ModifyCardBeingAddedToDeck with RunState.CloneCard(card), so
        // with one of those in play the replacement the old site named was thrown away and its
        // id never appeared again.
        //
        // These two replace the old CardCmd.Transform prefix rather than joining it: that
        // overload delegates here, so keeping both would emit every single-card transform twice.
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Models.CardModel"),
                                 "AfterTransformedFrom", me, nameof(TransformedFrom), 0);
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Models.CardModel"),
                                 "AfterTransformedTo", me, nameof(TransformedTo), 0);

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
        // Departures. Postfix, so the row is written against a removal that already happened:
        // Remove() does its work before its first await, so by the time the postfix runs the
        // relic is out of the player's list.
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.RelicCmd"),
                                 "Remove", me, nameof(RelicRemoved), 1, postfix: true);
        // Replace() calls Remove() then Obtain(), so it needs no emitter of its own -- only a
        // marker, so the pair it produces is readable as one swap rather than two coincidences.
        attempted++; n += HookPatcher.PatchOn(harmony, HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.RelicCmd"),
                                 "Replace", me, nameof(RelicReplacing), 2);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPotionUsed", me, nameof(PotionUsed));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPotionProcured", me, nameof(PotionProcured));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPotionDiscarded", me, nameof(PotionDiscarded));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterRestSiteHeal", me, nameof(RestHeal));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterRestSiteSmith", me, nameof(RestSmith));

        // --- events ------------------------------------------------------------------
        //
        // Event pages come from the game's own funnels, not from the live snapshot. The snapshot
        // is a 10 Hz sample and an event page is frequently set and answered between two ticks:
        // 706 of the 1009 event decisions in the 953-journal corpus carried an EMPTY option list,
        // 407 outcomes named an option their own decision never listed, and every Neow opening --
        // the highest-leverage choice in a run -- was in the broken set.
        //
        // EventModel.SetEventState is the single page-transition funnel. SetInitialEventState,
        // SetEventFinished and all 30-odd per-event page bodies route through it, and nothing in
        // the assembly overrides it, so one patch sees every page of every event. POSTFIX: it is
        // a plain `void`, not `async Task`, so a postfix runs with the page fully built rather
        // than at a first await, and CurrentOptions is populated by then.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Models.EventModel"),
            "SetEventState", me, nameof(EventPageShown), 2, postfix: true);
        // A visit restarts page numbering. BeginEvent, not SetInitialEventState: AncientEventModel
        // OVERRIDES the latter, so a base patch would miss every Ancient and Neow itself.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Models.EventModel"),
            "BeginEvent", me, nameof(EventBegun), 2);
        // The execution half. ChooseOptionForEvent is where an index becomes the option that
        // runs, for the local click and for a peer's, so it is the one place that carries both
        // the Player and the slot the option occupied. PREFIX: it calls EventOption.Chosen(),
        // which runs synchronously to its first await, and option bodies routinely set the next
        // page before anything else -- a postfix would file the outcome against the page that
        // replaced the one clicked.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer"),
            "ChooseOptionForEvent", me, nameof(EventOptionChosen), 2, firstParamType: "Player");
        // Proceed never reaches the synchronizer: NEventRoom.OptionButtonClicked short-circuits
        // IsProceed straight into option.Chosen(). Prefix, same async reason as above.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom"),
            "OptionButtonClicked", me, nameof(EventProceedClicked), 2, firstParamType: "EventOption");

        // Not Harmony patches, so outside the n/attempted tally, but resolved here to keep the
        // event capture in one block. Held as handles rather than read through Reflect.GetBool
        // because `false` is a real answer for both: a renamed IsProceed would turn every page
        // dismissal into a silent no-op, and a renamed IsLocked would report every greyed-out
        // option as a rejected alternative. Unresolved means the field is omitted.
        var eventOption = HookPatcher.FindType("MegaCrit.Sts2.Core.Events.EventOption");
        _optLockedProp = eventOption?.GetProperty("IsLocked",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        _optProceedProp = eventOption?.GetProperty("IsProceed",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (_optLockedProp == null || _optProceedProp == null)
            MainFile.Logger.Info("replay-hooks: EventOption.IsLocked/IsProceed not found; "
                                 + "event option selectability and proceed rows are omitted");
        // DynamicVarSet.AddTo(LocString), the prepare half of reading an option's text. Resolved
        // rather than called by name because OptionText must be able to refuse: formatting an
        // unprepared LocString sends a Sentry exception from the GAME's project, so no handle
        // means no label and no desc rather than a best effort.
        _addVarsMethod = HookPatcher
            .FindType("MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVarSet")
            ?.GetMethod("AddTo", BindingFlags.Public | BindingFlags.Instance);
        if (_addVarsMethod == null)
            MainFile.Logger.Info("replay-hooks: DynamicVarSet.AddTo not found; "
                                 + "event option label/desc are omitted");

        // --- attacks -----------------------------------------------------------------
        // Groups the hits of one attack under a shared `atk` id, and puts the models that
        // changed a number (Strength, Vulnerable, Frail...) on the hit or block row they
        // explain. All prefixes: each state change has to land before the hook's listeners
        // run, since a listener can start a nested attack or damage call of its own.
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeAttack", me, nameof(AttackOpened));
        attempted++; n += HookPatcher.Patch(harmony, hook, "ModifyAttackHitCount", me, nameof(AttackHitCount), postfix: true);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterAttack", me, nameof(AttackClosed));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterModifyingDamageAmount", me, nameof(DamageModified));
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeDamageReceived", me, nameof(DamageLanding));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterModifyingHpLostBeforeOsty", me, nameof(HpLossModified));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterModifyingHpLostAfterOsty", me, nameof(HpLossModified));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterModifyingBlockAmount", me, nameof(BlockModified));

        // --- combat lifecycle --------------------------------------------------------
        // An extra turn shares its ROUND with the turn it extends: CombatManager.SwitchSides
        // increments _state.RoundNumber in only one of its two branches while calling
        // PlayerCombatState.IncrementTurnNumber() in both. Switching turn.n to the player turn
        // number stops the collision; this hook is what still names the extra turn as one.
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterTakingExtraTurn", me, nameof(ExtraTurnTaken));
        // Deaths, including the ones no damage event can see (Doom, a direct CreatureCmd.Kill, a
        // self-destruct move) and the ones that were prevented. The only hook carrying
        // wasRemovalPrevented.
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterDeath", me, nameof(CreatureDied));
        // Not a Harmony patch, so deliberately outside the tally: a subscription to the game's own
        // CombatEnded / CombatWon events, which are the only end-of-combat notification that fires
        // on the loss path as well as the win. See BindCombatEndEvents.
        BindCombatEndEvents();

        // --- creatures ---------------------------------------------------------------
        // Mid-fight arrivals. CombatManager.AfterCreatureAdded rather than
        // Hook.AfterCreatureAddedToCombat: the hook fires after Creature.AfterAddedToRoom, where
        // monsters apply their entry powers (Tough Egg's Hatch), so the power row would beat the
        // roster row. Osty and relic pets arrive here too, through PlayerCmd.AddPet.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.CombatManager"),
            "AfterCreatureAdded", me, nameof(CreatureAdded), 1);
        // Escape fires no hook. CreatureCmd.Escape has early returns, CreatureEscaped is only
        // reached once the body really leaves.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.CombatState"),
            "CreatureEscaped", me, nameof(CreatureEscaped), 1);
        // Doom kills already write death rows (DoomKill calls CreatureCmd.Kill). These two
        // bracket the kill so CreatureDied can name the cause.
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Models.Powers.DoomPower"),
            "DoomKill", me, nameof(DoomKillStarting), 1);
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterDiedToDoom", me, nameof(DoomKillDone));
        // --- intents ---
        // What each enemy telegraphed, with numbers, at the moments the player saw it. Rolls
        // happen in StartTurn before the hand draw, so the telegraph is written once the turn
        // is actually handed over (AfterPlayerTurnStart) and again at BeforeTurnEnd only where
        // the shown numbers moved. See the intents section for the rest.
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeSideTurnStart", me, nameof(IntentTurnStarting));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterPlayerTurnStart", me, nameof(IntentsShown));
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeTurnEnd", me, nameof(IntentsCommitted));
        attempted++; n += HookPatcher.PatchOn(harmony,
            HookPatcher.FindType("MegaCrit.Sts2.Core.Models.MonsterModel"),
            "SetMoveImmediate", me, nameof(MoveSetImmediate), 2);

        // Not Harmony patches, so deliberately outside the n/attempted tally: these are plain
        // member lookups whose absence costs two fields on one row, not a whole line kind.
        // The Player argument for the overload match comes from the first pick, so IsMe
        // resolves lazily in SelectionReturned; only the property can be resolved here.
        _selectorProp = Reflect.StaticProperty(
            HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.CardSelectCmd"), "Selector");
        if (_selectorProp == null)
            MainFile.Logger.Info("replay-hooks: CardSelectCmd.Selector not found; "
                                 + "pick rows will omit `selector`");
        _keywordsResolved = HookPatcher.FindType("MegaCrit.Sts2.Core.Models.CardModel")
            ?.GetProperty("Keywords", BindingFlags.Instance | BindingFlags.Public) != null;
        if (!_keywordsResolved)
            MainFile.Logger.Info("replay-hooks: CardModel.Keywords not found; "
                                 + "play rows will omit `keywords`");

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
                // Which generation of this act's map this line describes. Golden Compass calls
                // RunManager.GenerateMap() a second time for the same act, so one act emits two
                // map lines with entirely different node graphs and joining a room.coord to the
                // graph actually in force was an ordering guess.
                .Set("gen", MapGeneration())
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

    // The game's own count of how many times a map has been generated this run.
    //
    // MapSelectionSynchronizer.MapGenerationCount is incremented in BeforeMapGenerated, which
    // RunManager.GenerateMap calls ahead of Hook.AfterMapGenerated, so by the time we read it the
    // number already describes THIS generation. Run-scoped and 1-based, not per-act.
    //
    // Omitted rather than defaulted on a failed read: the counter is at 1 or more whenever this
    // hook fires, so a 0 would be a value the game cannot produce presented as data.
    private static int? MapGeneration()
    {
        try
        {
            var mgr = Reflect.GetStatic(
                HookPatcher.FindType("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
            return Reflect.GetMember(
                Reflect.GetMember(mgr, "MapSelectionSynchronizer"), "MapGenerationCount") as int?;
        }
        catch { return null; }
    }

    private static void RoomEntered(object __0, object __1)
    {
        try
        {
            // A new room ends the previous decision context; a stale decision_id leaking onto
            // the next room's resolutions would silently mis-attribute picks.
            DemoteDecision();
            // An abandoned purchase never reaches AfterItemPurchased, so it would leave the
            // pending ware set for the rest of the run. RelicObtained reads that to tell a
            // relic off a shelf from one out of a decision, and a stale value would make it
            // drop a decision_id it should have kept.
            _pendingBuyId = null;
            _pendingBuyKind = null;

            var kind = __1.GetType().Name.Replace("Room", "").ToLowerInvariant();
            // The RESOLVED room type, which is the only thing separating a monster, an elite and a
            // boss: all three are CombatRoom, so the type name reads "combat" for every one of
            // them and the corpus has no elite or boss at all. AbstractRoom.RoomType is abstract
            // and CombatRoom overrides it as Encounter.RoomType, which is where Monster/Elite/Boss
            // comes from.
            //
            // A NEW field beside `kind` rather than a correction to it, because consumers read
            // those values today. Elite/boss is currently recoverable from the _ELITE/_BOSS suffix
            // on vanilla encounter ids, but a convention is not a field, and it says nothing about
            // a "?" node: MapPointType.Unknown resolves to a RoomType, and an event whose option
            // starts a fight changes the room type without the map point changing.
            var roomType = Reflect.GetMember(__1, "RoomType")?.ToString()?.ToLowerInvariant();
            // Floor from the live run state, not the centrally-stamped snapshot value: the
            // snapshot is up to one 10 Hz tick behind, and the first real journal filed a
            // treasure room on floor 9 whose very next line was floor 10.
            var floor = Reflect.GetInt(__0, "TotalFloor", -1);
            // Latch it before building the line, so every line until the next room (combat_start
            // above all, which fires immediately after this) carries the right floor.
            ReplayRecorder.NoteFloor(floor, Reflect.GetInt(__0, "CurrentActIndex", 0) + 1);
            ReplayRecorder.Line("room")
                ?.Set("kind", kind)
                .Set("room_type", roomType)
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
            // A roll after the turn-start telegraph (a mid-turn spawn) is the only time the
            // player sees this intent before it can change, so it gets its own row.
            if (IntentsShownFor(Reflect.GetMember(__1, "CombatState")))
                EmitIntent(__1, __result, "roll");
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
            var intents = IntentTypes(__instance);
            ReplayRecorder.Line("move")
                ?.Set("src", CreatureRef(owner))
                // Which enemy is about to act. In a fight with two of the same monster the two
                // intent lines were indistinguishable, so the move could not be attributed to
                // the body that then dealt the damage.
                .Set("src_cid", CreatureSlots.Maybe(owner))
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

    // A numeric member read as int?, null when the member is absent rather than 0.
    //
    // Reflect.GetInt exists and is not used for these: its fallback reports a real 0 for a
    // renamed property, which is how DamageResult.HpLost (a name that does not exist on the
    // type) shipped a journal in which every hit dealt 0 damage. Everything this reads is
    // declared `int` on the game type, so `is int` both converts and proves the read.
    private static int? IntOf(object? target, string name)
        => Reflect.GetMember(target, name) is int i ? i : (int?)null;

    // A [Flags] ValueProp as a list of lowercase flag names, the same shape move.intents uses.
    //
    // The game's own doc on the enum calls it "Aka damage type": Unblockable is HP loss like
    // Poison, Unpowered is relics/potions/powers, Move is attack cards and enemy attacks,
    // SkipHurtAnim is presentation. A flags value stringifies as "Move" or "Move, SkipHurtAnim",
    // so it is split rather than written through; a value with no flags set stringifies as "0",
    // which is not a prop name, so that reads as "no props" and the field is dropped instead of
    // writing a bogus one.
    private static List<string>? DamageProps(object? props)
    {
        var raw = props?.ToString();
        if (string.IsNullOrEmpty(raw) || raw == "0") return null;
        var names = raw.Split(',').Select(x => x.Trim().ToLowerInvariant())
                       .Where(x => x.Length > 0).ToList();
        return names.Count > 0 ? names : null;
    }

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

    // The decision that offer belongs to, captured when it OPENS.
    //
    // The outcome used to be written against whatever `_decision` held at flush time, which is
    // only the same decision when nothing has opened in between. Selections resolve
    // asynchronously, so a pick can land after the next decision is already open, and the
    // outcome then reported a deck select's `selected_option_indices` under an unrelated
    // `event` -- the one class of mis-join this record exists to prevent, and the reason
    // `_prevDecision` exists for resolutions. Holding the owner makes flush order irrelevant.
    private static int _selectDecision;
    private static string? _selectDecisionType;

    // The one full board keyframe per fight. Everything after it is deltas.
    private static void CombatStart(object __1)
    {
        try
        {
            // Cleared BEFORE the early return below, so a fight this session never recorded
            // (recording toggled off mid-run) cannot leave the previous fight's victory flag or
            // an unconsumed extra-turn mark behind for a later one to read.
            _combatWon = false;
            _extraTurnPending = null;
            _doomed.Clear(); // creatures: a DoomKill that never reached AfterDiedToDoom

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
                // `i` keeps the meaning it has always had: this enemy's POSITION in the list,
                // 0..n-1, left to right. Unchanged, because released readers already parse it.
                enemies.Add(CreatureEntry(new ReplayLine("e").Set("i", i++), e));

            // Non-player allies present at the start, in their own key so `enemies` reads as
            // before. Usually empty: Osty and relic pets are added by BeforeCombatStart
            // listeners, after this prefix, and get a spawn row. Omitted only if unreadable.
            List<ReplayLine>? allies = null;
            if (Reflect.GetMember(__1, "Allies") is IEnumerable allySeq)
            {
                allies = new List<ReplayLine>();
                foreach (var a in Enumerate(allySeq))
                    if (Reflect.GetMember(a, "IsPlayer") is false)
                        allies.Add(CreatureEntry(new ReplayLine("a"), a));
            }
            line.Set("combat_id", _combatId)
                .Set("attempt_id", ReplayRecorder.AttemptId)
                .Set("encounter", encounter)
                // The run's position in every random stream, at a precisely defined instant.
                // This is a Harmony PREFIX on Hook.BeforeCombatStart, which CombatManager calls
                // after the encounter's creatures are added and before StartTurn -- so the
                // counters are stamped before turn 1's shuffle and deal, and before any relic's
                // own BeforeCombatStart (Snecko Eye, Byrdpip) has drawn. A fight can therefore
                // be graded from its stated position rather than from a position inferred by
                // replaying every fight before it.
                .Set("rng_state", RngState.Read())
                .Set("enemies", enemies)
                .Set("allies", allies)
                .Emit();
        }
        catch { }
    }

    // Hook.AfterCombatEnd(IRunState runState, ICombatState? combatState, CombatRoom room) --
    // the VICTORY half. __1 is the combat state.
    //
    // Still emitted from the hook rather than only from the CombatEnded subscription below,
    // because of WHERE in the file the row lands. This hook runs early inside EndCombatInternal,
    // before the victory rewards and before the end-of-combat relic effects (Burning Blood's
    // heal, Meat on the Bone), while CombatEnded fires at the very last statement of that same
    // method. Moving the win row to the event would pull those heals INSIDE the fight's row
    // range and break "HP that moved between combat_start and combat_end is HP the fight cost".
    // The loss path never reaches this hook, and on the win path the subscription finds the
    // fight already closed.
    //
    // (The comment that used to live here named "AfterCombatVictoryEarly" as one of the game's
    // combat-end hooks. It is not one: it is AbstractModel.AfterCombatVictoryEarly, dispatched
    // inside Hook.AfterCombatVictory. That static does exist and is not patched.)
    private static void CombatEnd(object __1) => EmitCombatEnd(__1, won: true);

    // Hook.AfterPlayerTurnStart(ICombatState combatState, PlayerChoiceContext choiceContext,
    // Player player). Fires once per player, from SetupPlayerTurn after that player's hand draw,
    // so __2 is the player whose turn this row is about.
    private static void PlayerTurnStart(object __0, object __2)
    {
        try
        {
            var extra = TakeExtraTurnMark(__2);
            ReplayRecorder.Line("turn")
                // attempt_id too, so a turn is self-describing. combat_id alone pools the turns
                // of a fight that was reloaded and retried under one id, and only file position
                // separated them.
                ?.Set("combat_id", _combatId)
                .Set("attempt_id", _combatId == null ? (int?)null : ReplayRecorder.AttemptId)
                // PlayerCombatState.TurnNumber, NOT ICombatState.RoundNumber.
                //
                // CombatManager.SwitchSides increments _state.RoundNumber in only one of its two
                // branches while calling PlayerCombatState.IncrementTurnNumber() in both, so an
                // extra turn reused the round its parent turn already had: 217 player turn rows
                // in the corpus duplicate an existing (combat_id, attempt_id, n) and are
                // indistinguishable from a row emitted twice. RoundNumber carries the game's own
                // warning, "BE CAREFUL! You usually want PlayerCombatState.TurnNumber instead of
                // this", and EndCombatInternal uses TurnNumber for its own turnsTaken.
                .Set("n", TurnNumberOf(__2))
                // The round the turn belongs to, kept as its own field rather than discarded.
                // Two player turns sharing one round is now readable instead of a collision.
                .Set("round", RoundOf(__0))
                // Written only when the game said so, via Hook.AfterTakingExtraTurn. Absent is an
                // ordinary turn. If that hook is ever renamed the marker goes quiet but `n` stays
                // correct, and n running ahead of round still shows an extra turn was taken.
                .Set("extra", extra ? (bool?)true : null)
                .Set("side", "player")
                .Emit();
        }
        catch { }
    }

    // Hook.AfterSideTurnStart(ICombatState combatState, CombatSide side,
    // IReadOnlyList<Creature> participants).
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
                // Same meaning as on a player turn row, so `n` means one thing on every turn and
                // end_turn row: the local player's turn counter. An enemy turn is not handed a
                // Player, so it is read off the combat state's player list instead.
                .Set("n", LocalTurnNumber(__0))
                .Set("round", RoundOf(__0))
                .Set("side", side)
                .Emit();
        }
        catch { }
    }

    // Hook.AfterTurnEnd(ICombatState combatState, CombatSide side,
    // IEnumerable<Creature> participants).
    private static void TurnEnd(object __0, object __1)
    {
        try
        {
            ReplayRecorder.Line("end_turn")
                ?.Set("n", LocalTurnNumber(__0))
                .Set("round", RoundOf(__0))
                .Set("side", __1?.ToString()?.ToLowerInvariant())
                .Emit();
        }
        catch { }
    }

    // --- combat lifecycle ------------------------------------------------------------
    //
    // Everything below closes the two holes the fight boundary had: a loss produced no end row
    // at all, and a death was only ever visible as a flag on a damage row.

    // Set by the CombatWon subscription, read by EmitCombatEnd. Decides the LABEL only; it is not
    // the idempotence latch (see EmitCombatEnd). Cleared at combat_start and on journal open.
    private static bool _combatWon;

    // Players handed an extra turn by Hook.AfterTakingExtraTurn and not yet named on a turn row.
    //
    // A list of references rather than a bool, because in co-op two players can both be granted an
    // extra turn in one SwitchFromPlayerToEnemySide and Hook.AfterPlayerTurnStart then fires once
    // per player. Matched by reference identity, so nothing rests on whatever equality Player
    // defines.
    //
    // Cleared at combat_start, when combat_end is written, and on every journal open. StartTurn
    // returns early when combat is no longer in progress, so a granted extra turn whose turn row
    // never arrives would otherwise mark the first turn of a LATER fight.
    private static List<object>? _extraTurnPending;

    // The delegates this mod has installed on CombatManager's events, kept so a re-bind can remove
    // them first. Null until BindCombatEndEvents succeeds.
    private static Delegate? _onCombatEnded;
    private static Delegate? _onCombatWon;

    // Hook.AfterTakingExtraTurn(ICombatState combatState, Player player). __1 is the player.
    //
    // Fired from SwitchFromPlayerToEnemySide AFTER SwitchSides() has already incremented this
    // player's TurnNumber and BEFORE StartTurn, so the next turn row for this player is the extra
    // one and it already carries the right number.
    //
    // PREFIX. Hook.AfterTakingExtraTurn is `async Task`, so a postfix would run at its first
    // await rather than at completion, and nothing here depends on the hook body having run.
    private static void ExtraTurnTaken(object __1)
    {
        try
        {
            if (__1 == null) return;
            (_extraTurnPending ??= new List<object>()).Add(__1);
        }
        catch { }
    }

    // Hook.AfterDeath(IRunState runState, ICombatState? combatState, Creature creature,
    // bool wasRemovalPrevented, float deathAnimLength). __2 is the creature, __3 the flag.
    //
    // The first witness a death has of its own. Until now the only record was `killed: true` on a
    // hit row, and DamageResult.WasTargetKilled is documented as true "even if the creature was
    // resurrected afterwards (Fairy in a Bottle, etc.)", so a prevented death was recorded as a
    // death with no correcting row. Deaths with no damage event at all -- Doom, a direct
    // CreatureCmd.Kill, a self-destruct move -- produced nothing whatsoever.
    //
    // PREFIX, not postfix: Hook.AfterDeath is `async Task` (and its body awaits every listener),
    // so a postfix fires at the first await. Nothing read here is produced by the hook body --
    // CreatureCmd.KillWithoutCheckingWinCondition has already resolved the outcome and passes it
    // in -- so the prefix sees the final values.
    //
    // Deliberately NOT deduplicated per creature. KillWithoutCheckingWinCondition fires this with
    // wasRemovalPrevented: true when Hook.ShouldDie is vetoed, then recurses and fires it again
    // with false if the creature is still dead, so one killing blow can legitimately produce a
    // prevented row followed by a real one. Collapsing them would throw away the distinction the
    // row exists to make.
    //
    // A NEW row rather than a field on `hit`: the two are not the same event. A death can have no
    // damage event behind it, and a damage event can have two deaths behind it.
    private static void CreatureDied(object __2, bool __3)
    {
        try
        {
            ReplayRecorder.Line("death")
                // Same convention as the power row's target: "player", a bare monster id, or
                // "effect" / "unknown" when the body cannot be named.
                ?.Set("tgt", CreatureRef(__2))
                // Which body. Two of the same monster share one `tgt`, so without this the kill
                // order in a multi-enemy fight still is not recoverable.
                .Set("tgt_cid", CreatureSlots.Maybe(__2))
                // SetFlag, not Set: false is the entire point of the row. `true` is a death that
                // was prevented, which is the correcting row for a `killed: true` hit whose target
                // is still standing. `false` is a body that really left. A dropped false would
                // read as "really died", which is the wrong half to guess.
                .SetFlag("removal_prevented", __3)
                // creatures: only ever "doom" today. Absent means no known special cause.
                .Set("cause", _doomed.Contains(__2) ? "doom" : null)
                .Emit();
        }
        catch { }
    }

    // CombatManager.CombatWon(CombatRoom). Fires from EndCombatInternal a few statements ahead of
    // CombatEnded with no await between them, so it is always observed first.
    //
    // This is what stops `result` being a hardcoded constant. On the paths the game takes today
    // the win row is written by the AfterCombatEnd hook and this flag is never read, but if a game
    // patch renames that hook the event below becomes the only emitter, and then this is the
    // difference between labelling every victory a loss and labelling it correctly.
    private static void CombatWonEvent(object room) => _combatWon = true;

    // CombatManager.CombatEnded(CombatRoom) -- the LOSS half, and the fact that combat ended.
    //
    // CombatManager.LoseCombat() only sets _pendingLoss. CheckWinCondition() tests it FIRST and
    // routes it to the private ProcessPendingLoss(), which sets IsInProgress = false and fires
    // this event but never calls EndCombatInternal(), so Hook.AfterCombatEnd never runs on a loss
    // and the game has no loss hook anywhere. 878 of the 2822 fights in the corpus had no end row
    // of any kind for that reason, with losses, reloads, mid-fight quits and truncated files all
    // collapsed into the same indistinguishable shape.
    //
    // The event's argument is the CombatRoom, so the combat state comes off it rather than being
    // looked up: CombatRoom.CombatState is set in the constructor and is still live here on both
    // paths (ProcessPendingLoss does not clear it, and EndCombatInternal fires this event under an
    // explicit `_state != null` guard).
    private static void CombatEndedEvent(object room)
    {
        try
        {
            EmitCombatEnd(Reflect.GetMember(room, "CombatState"), _combatWon);
        }
        catch { }
    }

    // The single writer of combat_end, reached from the hook on a win and from the event on a loss.
    //
    // _combatId is BOTH the open-fight guard and the idempotence latch. Whichever emitter arrives
    // first clears it, so the second writes nothing: exactly one row per fight even if
    // CheckWinCondition is re-entered, or if a loss is queued during EndCombatInternal's awaits
    // and processed after the win row was already written. The same check refuses an event that
    // arrives with no fight open. (On the teardown question specifically: CombatEnded is invoked
    // from exactly two places in the v0.111.0 assembly, ProcessPendingLoss and the last statement
    // of EndCombatInternal. CombatManager.Reset(bool), which is what a run abandon and an exit
    // call, does not fire it.)
    private static void EmitCombatEnd(object? combatState, bool won)
    {
        try
        {
            if (_combatId == null) return;
            ReplayRecorder.Line("combat_end")
                // A recorded outcome, not a constant: "victory" is the game reaching
                // EndCombatInternal, "loss" is it reaching ProcessPendingLoss instead.
                ?.Set("result", won ? "victory" : "loss")
                .Set("combat_id", _combatId)
                .Set("attempt_id", ReplayRecorder.AttemptId)
                // The local player's own turn count, which is the number EndCombatInternal itself
                // stores as turnsTaken. This used to be ICombatState.RoundNumber, which does not
                // move for an extra turn and so under-reported every fight that had one.
                .Set("turns", LocalTurnNumber(combatState))
                // Kept beside it rather than dropped: it is the value released journals carry
                // under `turns`, and it is still the right count of enemy turns.
                .Set("rounds", RoundOf(combatState))
                // Omitted, never 0, when the fight was already under way before this journal
                // session opened. A fight with no end line at all keeps no total either, which
                // is what makes "interrupted" readable rather than looking like a clean 0.
                .Set("hp_lost_total", _hpLostInCombat)
                .Emit();
            _combatId = null;
            _hpLostInCombat = null;
            _extraTurnPending = null;
        }
        catch { }
    }

    // Subscribe to CombatManager's two end-of-combat events.
    //
    // Subscribed ONCE, from Apply, rather than per run. CombatManager.Instance is a process-wide
    // singleton built by its own static initializer, so it outlives every run: there is no run
    // boundary at which to re-subscribe and therefore no way for a second run in the same process
    // to double-subscribe. Rebind also removes the delegate it installed last before adding a new
    // one, so calling this twice still leaves exactly one subscription. The handlers keep no
    // subscription-scoped state of their own; they read _combatId, which ResetRun clears on every
    // journal open.
    private static void BindCombatEndEvents()
    {
        var type = HookPatcher.FindType("MegaCrit.Sts2.Core.Combat.CombatManager");
        var instance = Reflect.GetStatic(type, "Instance");
        if (type == null || instance == null)
        {
            MainFile.Logger.Info("replay-hooks: CombatManager.Instance not found; "
                                 + "combat_end will not fire on a loss");
            return;
        }
        _onCombatEnded = Rebind(type, instance, "CombatEnded", nameof(CombatEndedEvent), _onCombatEnded);
        _onCombatWon = Rebind(type, instance, "CombatWon", nameof(CombatWonEvent), _onCombatWon);
    }

    // Add one of our handlers to a game event, removing whatever this method installed on a
    // previous call so the subscription can never stack.
    //
    // A C# event subscription is a different mechanism from every other capture point in this
    // file, and it needs one thing Harmony does not: a delegate of the game's own type. The
    // handler is declared `void H(object room)` while the event is Action<CombatRoom>, a type the
    // mod cannot name. Delegate.CreateDelegate's relaxed binding accepts that, because a reference
    // conversion on a parameter is allowed and CombatRoom converts to object. Verified against
    // net10 before this was written; a failure throws here and is logged rather than degrading
    // into a wrong value.
    private static Delegate? Rebind(Type type, object instance, string eventName,
                                    string handlerName, Delegate? installed)
    {
        try
        {
            var evt = type.GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance);
            if (evt?.EventHandlerType == null)
            {
                MainFile.Logger.Info($"replay-hooks: CombatManager.{eventName} not found");
                return null;
            }
            if (installed != null) evt.RemoveEventHandler(instance, installed);
            var handler = typeof(ReplayHooks).GetMethod(
                handlerName, BindingFlags.NonPublic | BindingFlags.Static);
            if (handler == null) return null;
            var d = Delegate.CreateDelegate(evt.EventHandlerType, handler);
            evt.AddEventHandler(instance, d);
            return d;
        }
        catch (Exception e)
        {
            MainFile.Logger.Info(
                $"replay-hooks: subscribing to CombatManager.{eventName} failed: {e.Message}");
            return null;
        }
    }

    // ICombatState.RoundNumber, or null when it cannot be read.
    //
    // The game documents it as starting at 1, so a 0 is a failed read and the field is omitted
    // rather than written as a plausible "round zero". That is the whole silent-null trap: a
    // fallback number here would be indistinguishable from a real count.
    private static int? RoundOf(object? combatState)
    {
        var n = Reflect.GetInt(combatState, "RoundNumber", 0);
        return n > 0 ? n : (int?)null;
    }

    // PlayerCombatState.TurnNumber for one player, or null when it cannot be read. Also documented
    // as starting at 1, so 0 is a failed read and is omitted for the same reason.
    private static int? TurnNumberOf(object? player)
    {
        var pcs = Reflect.GetMember(player, "PlayerCombatState");
        if (pcs == null) return null;
        var n = Reflect.GetInt(pcs, "TurnNumber", 0);
        return n > 0 ? n : (int?)null;
    }

    // The local player's turn number, for the rows the game does not hand a Player.
    //
    // Falls back to the sole player when no net id matches, the same way the rest of the mod
    // treats single-player: RunManager sets LocalContext.NetId from NetService at run start, so it
    // is not reliably null off a lobby, and a strict match would drop `n` in single-player.
    private static int? LocalTurnNumber(object? combatState)
    {
        var players = Enumerate(Reflect.GetMember(combatState, "Players")).ToList();
        if (players.Count == 0) return null;
        var me = players.FirstOrDefault(p => LocalPlayer.IsLocalPlayer(p))
                 ?? (players.Count == 1 ? players[0] : null);
        return TurnNumberOf(me);
    }

    // Consume the extra-turn mark for one player, if one is waiting for it.
    private static bool TakeExtraTurnMark(object? player)
    {
        var pending = _extraTurnPending;
        if (pending == null || player == null) return false;
        for (var i = 0; i < pending.Count; i++)
        {
            if (!ReferenceEquals(pending[i], player)) continue;
            pending.RemoveAt(i);
            return true;
        }
        return false;
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
            // The X an X-cost card was played for: the CAPTURED value, not ResolveEnergyXValue().
            // The resolver runs Hook.ModifyXValue over every listener in the combat, and calling a
            // game hook by reflection from inside another hook turns a capture point into a game
            // bug. CapturedXValue is the same number already banked -- CardModel.SpendEnergy sets
            // it during SpendResources, ahead of OnPlayWrapper, and CardCmd.AutoPlay sets it before
            // BeforeCardPlayed -- so it is populated by the time this runs. The one thing it misses
            // is a Chemical X style modifier, which is what the resolver adds.
            //
            // Gated on CostsX because the property THROWS for a non-X card ("Only X-cost cards have
            // a captured value"), and a throw reads as null through Reflect, which would be
            // indistinguishable from the member having been renamed.
            var energyCost = Reflect.GetMember(card, "EnergyCost");
            var xValue = Reflect.GetBool(energyCost, "CostsX")
                ? Reflect.GetMember(energyCost, "CapturedXValue") as int?
                : null;
            // The keywords in force for THIS play, not the printed ones. The canonical set is
            // already derivable from `id`; what is not is a keyword granted during the fight, which
            // CardModel.Keywords resolves through Hook.ModifyKeywordsInCombat (HexPower's Ethereal
            // is its only implementor in the shipped assembly) on top of the card's local set.
            // Reading it re-runs that listener walk, which is a pure query the game itself makes
            // constantly on this same property, including inside CardCmd.AutoPlay.
            var keywords = _keywordsResolved
                ? Enumerate(Reflect.GetMember(card, "Keywords"))
                      .Select(k => k.ToString()!.ToLowerInvariant()).ToList()
                : null;
            ReplayRecorder.Line("play")
                ?.Set("c", CardInstances.Of(card))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(card, "Id")))
                .Set("up", Reflect.GetInt(card, "CurrentUpgradeLevel", 0))
                // Model id AND slot. The id alone names a species, so against two Corpse Slugs
                // every targeted play read CORPSE_SLUG and which one took it was unrecoverable.
                // The old objection here -- that minting a CardInstances id for a Creature would
                // consume ids from the card sequence and corrupt card_instances downstream --
                // is answered by CreatureSlots keeping a sequence of its own.
                .Set("target", target == null ? null : Ids.Bare(Reflect.GetString(target, "ModelId")))
                .Set("target_cid", CreatureSlots.Maybe(target))
                .Set("cost_paid", Reflect.GetInt(resources, "EnergySpent", -1))
                // What the card COST, against what was actually spent. ResourceInfo has four
                // fields and this used to read only the two Spent ones; its own doc says "if you
                // auto-play a 3-energy-cost card, this will be 3, while EnergySpent will be 0", so
                // every auto-played card recorded cost_paid 0 and was indistinguishable from a
                // genuinely free one. CardCmd.AutoPlay builds exactly that ResourceInfo
                // (EnergySpent 0, EnergyValue GetAmountToSpend()); PlayCardAction sets the two
                // equal, so on a manual play these agree and the pair is only informative on the
                // autoplay path. That is the path energy accounting could not audit.
                //
                // Read as a nullable rather than through GetInt: a 0 fallback is precisely the
                // plausible wrong number this record keeps getting bitten by, and ResourceInfo is
                // already on that list (the first build read "Energy", which does not exist).
                .Set("cost_value", Reflect.GetMember(resources, "EnergyValue") as int?)
                .Set("stars_paid", Reflect.GetInt(resources, "StarsSpent", 0))
                .Set("stars_value", Reflect.GetMember(resources, "StarValue") as int?)
                .Set("x_value", xValue)
                // Where the card is headed once the play finishes, and the only place
                // exhaust-on-play and retain-on-play are stated. Decided, not predicted: the game
                // resolves it through Hook.ModifyCardPlayResultPileTypeAndPosition before it builds
                // this CardPlay, so an exhaust here is the outcome rather than a keyword something
                // later might override.
                .Set("result_pile",
                     Reflect.GetMember(__1, "ResultPile")?.ToString()?.ToLowerInvariant())
                // Omitted when empty, which is most cards. The install-time lookup behind
                // _keywordsResolved is what keeps that absence meaning "no keywords" rather than
                // "the member moved".
                .Set("keywords", keywords is { Count: > 0 } ? keywords : null)
                .SetFlag("auto", Reflect.GetBool(__1, "IsAutoPlay"))
                .Set("play_index", Reflect.GetInt(__1, "PlayIndex", 0))
                .Set("play_count", Reflect.GetInt(__1, "PlayCount", 1))
                .Set("turn", Reflect.GetInt(__0, "RoundNumber", 0))
                .Emit();
        }
        catch { }
    }

    // AfterCardDrawn(ICombatState, PlayerChoiceContext, CardModel card, bool fromHandDraw).
    //
    // fromHandDraw is the game's own name for "part of the initial card draws at the start of your
    // turn"; CombatManager.SetupPlayerTurn is the only caller that passes it true, everything else
    // defaults it to false. So it separates the turn-start deal from a draw a card, power or relic
    // caused, and `draw` is the largest line kind in the corpus by a wide margin and was one
    // undifferentiated stream without it.
    private static void CardDrawn(object __2, bool __3) => CardMove("draw", __2, "turn_start", __3);
    private static void CardDiscarded(object __2) => CardMove("discard", __2);

    // AfterCardExhausted(ICombatState, PlayerChoiceContext, CardModel card, bool causedByEthereal).
    // A card exhausting itself at end of turn because it is Ethereal is not the player spending it,
    // and both were landing as the same plain exhaust.
    private static void CardExhausted(object __2, bool __3)
        => CardMove("exhaust", __2, "ethereal", __3);

    // `reason` names the one bool its hook hands over to say WHY the move happened, so the three
    // callers keep sharing one emitter rather than growing a copy each. Written with SetFlag, so
    // the false case is explicit: "drawn, not at turn start" is a fact, not a missing read, and
    // AfterCardDiscarded carries no such flag so discard passes none.
    private static void CardMove(string kind, object card, string? reason = null, bool value = false)
    {
        try
        {
            // deck_c is the DECK instance this combat copy came from; absent for cards with no
            // deck ancestor (Slimed, Wound, Dazed). `c` alone is combat-scoped and never joins
            // to card_instances.
            var origin = CardInstances.DeckIdOf(card);
            var line = ReplayRecorder.Line(kind);
            if (line == null) return;
            line.Set("c", CardInstances.Of(card))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(card, "Id")));
            if (reason != null) line.SetFlag(reason, value);
            line.Emit();
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

    // AfterShuffle(ICombatState, PlayerChoiceContext, Player shuffler). Fired at the very end of
    // CardPileCmd.Shuffle, so by now the discard pile is empty and the draw pile holds everything
    // that went into the shuffle.
    private static void Shuffled(object __2)
    {
        try
        {
            // The shuffler, for the same reason the relic rows carry an owner: Shuffle is
            // per-player, and a co-op partner reshuffling their own pile used to land in this
            // journal as a bare row indistinguishable from ours. Null outside co-op, where every
            // shuffle is necessarily ours.
            //
            // n_draw is the draw pile AFTER the shuffle, which is the count that was shuffled
            // together. It is what makes the row worth reading at all: a shuffle resets draw order,
            // and the corpus holds thousands of these saying only that one happened.
            var cards = Reflect.GetMember(
                Reflect.GetMember(Reflect.GetMember(__2, "PlayerCombatState"), "DrawPile"),
                "Cards");
            ReplayRecorder.Line("shuffle")
                ?.Set("mine", Mine(__2))
                // Omitted, not zeroed, on a failed read: a shuffle cannot leave an empty draw pile,
                // so a 0 here would be a number the game never produces presented as data.
                .Set("n_draw", cards == null ? (int?)null : Enumerate(cards).Count())
                // card flow (D4): the order the shuffle produced, top first. See PileOrder.
                .Set("order_c", PileOrder(cards))
                .Emit();
        }
        catch { }
    }

    // --- card flow -------------------------------------------------------------------

    // Resolved at install; names the autoplay kind on play_blocked. Null leaves auto_type off.
    private static Type? _autoPlayType;

    // Instance ids of a card sequence in its own order, or null when it could not be read.
    // For a draw pile index 0 is the top: CardPileCmd.Draw takes Cards.FirstOrDefault().
    //
    // Shuffle order comes from the pile itself rather than a Hook.ModifyShuffleOrder postfix.
    // That hook is sync and edits a list CardPileCmd.Shuffle then re-adds to the emptied draw
    // pile one by one in that order, so by AfterShuffle the pile IS that list, plus the debug
    // forced top card the list never shows.
    private static List<int>? PileOrder(object? cards)
        => cards is IEnumerable ? Enumerate(cards).Select(CardInstances.Of).ToList() : null;

    // Hook.AfterFlush(ICombatState, Player, PlayerChoiceContext, flushedCards, retainedCards).
    // Written even when both are empty: an empty list is an answer, a missing field is not.
    private static void HandFlushed(object __1, object __3, object __4)
    {
        try
        {
            ReplayRecorder.Line("flush")
                ?.Set("mine", Mine(__1))
                .Set("flushed_c", PileOrder(__3))
                .Set("retained_c", PileOrder(__4))
                .Emit();
        }
        catch { }
    }

    // Hook.BeforeCombatStart(IRunState, ICombatState?). The initial shuffle ran back in
    // SetUpCombat (Player.PopulateCombatState -> RandomizeOrderInternal -> ModifyShuffleOrder),
    // so the draw pile here is its output. Turn 1 then moves Innate cards to the top and
    // bottom-start enchantments down, which the deal's draw rows show.
    //
    // order_deck_c pairs each position with its deck card: undrawn cards otherwise never
    // name their deck origin anywhere. Dropped whole if any entry has none, so it never
    // carries a 0 posing as an id.
    private static void OpeningDrawOrder(object __1)
    {
        try
        {
            var players = Enumerate(Reflect.GetMember(__1, "Players")).ToList();
            var me = players.FirstOrDefault(p => LocalPlayer.IsLocalPlayer(p))
                     ?? (players.Count == 1 ? players[0] : null);
            if (me == null) return;
            var cards = Reflect.GetMember(
                Reflect.GetMember(Reflect.GetMember(me, "PlayerCombatState"), "DrawPile"), "Cards");
            var order = PileOrder(cards);
            if (order == null) return;
            var deck = Enumerate(cards).Select(CardInstances.DeckIdOf).ToList();
            ReplayRecorder.Line("draw_order")
                ?.Set("order_c", order)
                .Set("order_deck_c", deck.Contains(0) ? null : deck)
                .Emit();
        }
        catch { }
    }

    // CardCmd.Afflict(AfflictionModel affliction, CardModel card, decimal amount), postfix.
    // A null result is a refusal (ShouldAfflict, CanAfflict, combat ending) and writes nothing.
    // On a stack the returned model is the card's existing one, so amount_total is the sum.
    private static void CardAfflicted(object __1, decimal __2, object? __result)
    {
        try
        {
            if (__result is not System.Threading.Tasks.Task { IsCompletedSuccessfully: true } done) return;
            var applied = Reflect.GetMember(done, "Result");
            if (applied == null || __1 == null) return;
            var origin = CardInstances.DeckIdOf(__1);
            ReplayRecorder.Line("afflict")
                ?.Set("c", CardInstances.Of(__1))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Set("affliction", Ids.Bare(Reflect.GetString(applied, "Id")))
                // The game truncates the same way when it stores it.
                .Set("amount", (int)__2)
                .Set("amount_total", Reflect.GetMember(applied, "Amount") as int?)
                .Set("mine", Mine(Reflect.GetMember(__1, "Owner")))
                .Emit();
        }
        catch { }
    }

    // CardCmd.ClearAffliction(CardModel card), prefix, so the affliction is still readable.
    // Callers often clear without checking, and that no-op writes nothing.
    private static void AfflictionCleared(object __0)
    {
        try
        {
            var affliction = Reflect.GetMember(__0, "Affliction");
            if (affliction == null) return;
            var origin = CardInstances.DeckIdOf(__0);
            ReplayRecorder.Line("unafflict")
                ?.Set("c", CardInstances.Of(__0))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Set("affliction", Ids.Bare(Reflect.GetString(affliction, "Id")))
                .Set("amount", Reflect.GetMember(affliction, "Amount") as int?)
                .Set("mine", Mine(Reflect.GetMember(__0, "Owner")))
                .Emit();
        }
        catch { }
    }

    // Hook.ShouldPlay(ICombatState, CardModel card, out AbstractModel? preventer, AutoPlayType).
    //
    // CardModel.CanPlay calls this every frame for UI glow, always with AutoPlayType.None
    // (enum value 0), and CardCmd.AutoPlay is the only caller passing anything else. So a
    // refusal with a non-None type is an autoplay the game then routes to
    // MoveToResultPileWithoutPlaying with no play row. `int __3` binds the enum without boxing
    // and `object __2` reads the out value (Harmony derefs a by-ref arg for a by-value param).
    //
    // The manual path is not covered: PlayCardAction re-checks CanPlay after an await and
    // cancels, which leaves the card in hand with nothing spent, and CanPlay itself is too hot
    // to tell that check apart from UI polling.
    private static void PlayRefused(bool __result, object __1, object? __2, int __3)
    {
        if (__result || __3 == 0) return;
        try
        {
            var origin = CardInstances.DeckIdOf(__1);
            var kind = _autoPlayType == null ? null : Enum.GetName(_autoPlayType, __3);
            ReplayRecorder.Line("play_blocked")
                ?.Set("c", CardInstances.Of(__1))
                .Set("deck_c", origin > 0 ? origin : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Set("preventer", Ids.Bare(Reflect.GetString(__2, "Id")))
                .Set("auto_type", kind?.ToLowerInvariant())
                .Set("mine", Mine(Reflect.GetMember(__1, "Owner")))
                .Emit();
        }
        catch { }
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
    //
    // `dmg` is UnblockedDamage and still means exactly that: HP actually removed. It is NOT the
    // damage the blow dealt. The game computes that as `item.UnblockedDamage +
    // item.OverkillDamage`, so on a lethal hit UnblockedDamage stops at the target's remaining
    // HP and the swing is truncated by the overkill, on precisely the hits worth grading. Rather
    // than redefine a field 35k rows already use, the missing half is added as `overkill`:
    // damage dealt = dmg + (overkill ?? 0). DamageResult.TotalDamage is deliberately NOT
    // recorded; it is a derived property, BlockedDamage + UnblockedDamage, both of which are
    // already on this row, and it is truncated on a lethal hit for the same reason dmg is.
    private static void DamageGiven(object __1, object __2, object __3, object __4, object __5, object __6)
    {
        try
        {
            // attacks: taken before anything else so the latch is released even when no row is written.
            var why = TakeDamageFrame(__1, __5, __2, __6);
            var dealerIsPlayer = Reflect.GetMember(__2, "IsPlayer") is true;
            var targetIsPlayer = Reflect.GetMember(__5, "IsPlayer") is true;
            if (targetIsPlayer && Reflect.GetBool(__3, "WasTargetKilled")) PlayerDied = true;
            // Actual HP reduction only: unblocked damage, and only while a fight this journal
            // session actually saw start. Blocked damage is excluded and healing never subtracts,
            // so this is "HP lost in combat", not "net HP change".
            if (targetIsPlayer && _hpLostInCombat is { } lost)
                _hpLostInCombat = lost + Reflect.GetInt(__3, "UnblockedDamage", 0);
            var hit = ReplayRecorder.Line("hit");
            hit
                // A null dealer is an effect (poison, thorns, disintegration), not a missing
                // read. Naming it keeps that distinguishable from a failed reflection.
                ?.Set("src", dealerIsPlayer ? "player"
                        : __2 == null ? "effect" : Ids.Bare(Reflect.GetString(__2, "ModelId")))
                .Set("src_cid", CreatureSlots.Maybe(__2))
                .Set("dst", targetIsPlayer ? "player" : Ids.Bare(Reflect.GetString(__5, "ModelId")))
                // Which body took it. Without this, two of the same enemy share one `dst` and
                // the kill order in a multi-enemy fight is not recoverable from the journal.
                .Set("dst_cid", CreatureSlots.Maybe(__5))
                .Set("dmg", Reflect.GetInt(__3, "UnblockedDamage", 0))
                .Set("blocked", Reflect.GetInt(__3, "BlockedDamage", 0))
                // Damage past 0 HP. Written only when it is nonzero, which is almost never, so
                // its absence means "no overkill" on every hit that is not a kill. Pair it with
                // `dmg` to get what the blow actually dealt.
                .Set("overkill", IntOf(__3, "OverkillDamage") is int over && over > 0 ? over : (int?)null)
                // dmg:0 blocked:9 cannot tell "fully blocked" from "reduced to nothing", which is
                // the difference between a Defend that worked and a Defend that was irrelevant.
                // Written only when true, same as overkill.
                .Set("full_block", Reflect.GetMember(__3, "WasFullyBlocked") is true ? true : (bool?)null)
                .SetFlag("killed", Reflect.GetBool(__3, "WasTargetKilled"))
                // What KIND of damage this was, from the hook's own ValueProp. Poison, thorns and
                // relic pings all arrive with card null and no dealer worth naming, so they were
                // indistinguishable from an unattributed attack: FlameBarrierPower calls
                // CreatureCmd.Damage with the owner as dealer and ValueProp.Unpowered, which read
                // as the player attacking the enemy in the middle of the enemy's turn.
                .Set("dmg_type", DamageProps(__4))
                .Set("card", __6 == null ? null : Ids.Bare(Reflect.GetString(__6, "Id")))
                // attacks: see the attacks section.
                .Set("atk", hit == null ? null : AttackIdFor(__1, __2, __6, __4))
                .Set("mods", why?.Mods)
                .Set("hp_mods", why?.HpMods)
                .Emit();
        }
        catch { }
    }

    // AfterDamageReceived(choiceContext, runState, combatState, target, DamageResult, props,
    // dealer, cardSource). In combat this is the same blow DamageGiven already recorded, so it
    // only emits OUT of combat (combatState null): event damage, which DamageGiven never sees.
    private static void DamageReceived(object __2, object __3, object __4, object __5)
    {
        try
        {
            if (__2 != null) return; // in combat: DamageGiven owns it
            if (Reflect.GetMember(__3, "IsPlayer") is not true) return;
            ReplayRecorder.Line("hp_loss")
                ?.Set("dmg", Reflect.GetInt(__4, "UnblockedDamage", 0))
                .Set("blocked", Reflect.GetInt(__4, "BlockedDamage", 0))
                // Same two additions as the hit row, and for the same reason: out of combat the
                // damage that matters is an event cost or a curse tick, and dmg alone truncates a
                // lethal one. full_block is left off here, there is no block outside a fight.
                .Set("overkill", IntOf(__4, "OverkillDamage") is int over && over > 0 ? over : (int?)null)
                .Set("dmg_type", DamageProps(__5))
                .Emit();
        }
        catch { }
    }

    // AfterPowerAmountChanged(combatState, choiceContext, PowerModel, amount, applier, cardSource)
    private static void PowerChanged(object __2, decimal __3, object __4)
    {
        try
        {
            var owner = Reflect.GetMember(__2, "Owner");
            var line = ReplayRecorder.Line("power");
            if (line == null) return;
            var src = CreatureRef(__4);
            var landed = (int)__3;
            // Who applied it. Without this a relic or thorns ticking on the enemy turn looked
            // identical to a monster buffing itself, because tgt was the only clue. Same
            // convention as hit.src: a null applier is an effect, not a failed read.
            line.Set("src", src)
                .Set("src_cid", CreatureSlots.Maybe(__4))
                .Set("id", Ids.Bare(Reflect.GetString(__2, "Id")))
                .Set("n", landed)
                // The RESULTING TOTAL, not a second delta. `n` is the change alone
                // (modifiedOffset in PowerCmd.ModifyAmount, modifiedAmount in Apply), so a
                // consumer summing deltas drifts permanently the moment one row is missed --
                // and a row IS missed every time an application is modified to zero. Amount is
                // the number the game itself carries on the power, so every row restates the
                // board and the drift cannot accumulate.
                .Set("amount", AmountOf(__2))
                // What was asked for before the modifiers, written only when it differs from
                // what landed. Unsettling Lamp doubles a debuff and Ruined Helmet doubles the
                // first Strength of a fight, and today the journal shows only the doubled
                // figure. Equal amounts are the overwhelming majority and write nothing, which
                // is why the before hook does not get a row of its own.
                .Set("n_intended", IntendedAmount(__2, src) is { } asked && asked != landed
                        ? asked : (int?)null)
                // Deliberately ModelId, so the player reads as IRONCLAD rather than "player".
                // Unchanged because released readers parse it, and power_lost / power_negated
                // below follow it for the same reason.
                .Set("tgt", Ids.Bare(Reflect.GetString(owner, "ModelId")))
                // Which body carries the debuff. `tgt` alone said CORPSE_SLUG for a Weak that
                // only one of the two slugs actually had.
                .Set("tgt_cid", CreatureSlots.Maybe(owner));
            StampInstance(line, __2);
            line.Emit();
        }
        catch { }
    }

    // BeforePowerAmountChanged(combatState, PowerModel, amount, target, applier, cardSource).
    //
    // Emits NOTHING by itself. It fires on every amount change in the game, 64237 rows' worth in
    // the recorded corpus, so a row here would double the largest combat line kind in the file to
    // restate what the after row already says. What it is for is the number that exists ONLY
    // here: the amount before Hook.ModifyPowerAmountGiven / ModifyPowerAmountReceived touched it.
    // Two rows need it -- a power row whose landed amount was changed on the way in, and the
    // negation rows below, where PowerCmd guards the after hook behind a non-zero check and the
    // whole event otherwise vanishes.
    //
    // PREFIX on an async Task hook, deliberately: the body awaits every listener's own
    // BeforePowerAmountChanged, and the intent has to be stashed before any of them can modify
    // the power it is about.
    private static void PowerChanging(object __1, decimal __2, object __4, object __5)
    {
        try
        {
            if (__1 == null) return;
            Intents.Remove(__1);
            Intents.Add(__1, new Intent
            {
                Amount = (int)__2,
                Src = CreatureRef(__4),
                SrcCid = CreatureSlots.Maybe(__4),
                Card = __5 == null ? null : Ids.Bare(Reflect.GetString(__5, "Id")),
            });
        }
        catch { }
    }

    // PowerModel.ApplyInternal(Creature owner, decimal amount, bool silent).
    //
    // Half of "I applied Weak and it did not land". PowerCmd.Apply calls this unconditionally
    // once target.CanReceivePowers, while the row-producing Hook.AfterPowerAmountChanged sits
    // behind `if (modifiedAmount != 0m)`, and ApplyInternal itself no-ops on a zero amount. So a
    // zero here is exactly a fresh power negated on the way in (Artifact eating a debuff), and
    // nothing else in the game reports it: today that reads identically to never applying it.
    //
    // Only the zero case emits, so a normal application still has exactly one row.
    private static void PowerApplied(object __instance, object __0, decimal __1)
    {
        try
        {
            if (__1 != 0m) return;
            // Not on the board and never will be: PowerCmd.Apply built this instance for an
            // application that landed nothing, so it gets no pid (an id minted here would name a
            // power that never existed) and no amount. The applier is read off the power because
            // Apply sets power.Applier one line before the before hook runs.
            EmitNegated(__instance, __0, Reflect.GetMember(__instance, "Applier"), onBoard: false);
        }
        catch { }
    }

    // CombatHistory.PowerReceived(ICombatState, PowerModel, decimal amount, Creature? applier).
    //
    // The other half. When the target ALREADY carries the power, PowerCmd.Apply hands off to
    // ModifyAmount, which guards Hook.AfterPowerAmountChanged behind `(int)modifiedOffset != 0`
    // while calling History.PowerReceived one line earlier with no guard at all. That unguarded
    // call is the only witness to an offset the modifiers ate.
    //
    // The int cast here is the game's own test for "no after row will follow", used so the two
    // can never disagree. PowerCmd.Apply also calls PowerReceived, but only when its amount is
    // non-zero, so the fresh-application path never reaches the emit. A caller that genuinely
    // asks for an offset of 0 lands here too and says so: n_intended is then 0.
    private static void PowerReceived(object __1, decimal __2, object __3)
    {
        try
        {
            if ((int)__2 != 0) return;
            EmitNegated(__1, Reflect.GetMember(__1, "Owner"), __3, onBoard: true);
        }
        catch { }
    }

    // One shape for both negation paths: an application that reached its target and changed
    // nothing. "I applied Weak and Artifact ate it" and "I never applied Weak" are the same
    // journal today, and grading a debuff plan needs them apart.
    //
    // `n_intended` is the point of the row, so it comes from the intent stashed at
    // BeforePowerAmountChanged rather than from anything re-derived here. There is no `n`: a 0
    // delta would be indistinguishable from a row that simply did not record one.
    private static void EmitNegated(object? power, object? target, object? applier, bool onBoard)
    {
        var line = ReplayRecorder.Line("power_negated");
        if (line == null) return;
        Intent? intent = null;
        if (power != null) Intents.TryGetValue(power, out intent);
        line.Set("src", intent?.Src ?? CreatureRef(applier))
            .Set("src_cid", intent?.SrcCid ?? CreatureSlots.Maybe(applier))
            .Set("id", Ids.Bare(Reflect.GetString(power, "Id")))
            .Set("n_intended", intent?.Amount)
            .Set("card", intent?.Card)
            .Set("tgt", Ids.Bare(Reflect.GetString(target, "ModelId")))
            .Set("tgt_cid", CreatureSlots.Maybe(target));
        // The target already carried this instance, so there is a real board position to point
        // at and to restate the total of. A negated FRESH application has neither.
        if (onBoard)
        {
            line.Set("amount", AmountOf(power));
            StampInstance(line, power);
        }
        line.Emit();
    }

    // PowerModel.RemoveInternal(). The board LOSING a power.
    //
    // Nothing reported this, so a consumer replaying power deltas accumulated a board that only
    // ever grew: "did the enemy still have Vulnerable when I hit it" is the question these rows
    // exist for and it was unanswerable. 64237 power rows depend on it.
    //
    // PREFIX, because RemoveInternal's last act is Owner.RemovePowerInternal(this) and a postfix
    // would be reading the owner off a power the game has already unhooked. It happens to still
    // read today, since RemovePowerInternal does not clear _owner, and that is an implementation
    // detail rather than a contract.
    private static void PowerRemoved(object __instance)
    {
        try
        {
            // The duration tick is ALREADY recorded and must not be recorded twice.
            // PowerCmd.ModifyAmount fires AfterPowerAmountChanged with the negative offset and
            // only then calls Remove, so Vulnerable reaching 0 already has a power row whose
            // amount is 0. ShouldRemoveDueToAmount is the game's own predicate for that branch,
            // so asking it is asking "did the row that already went out explain this removal".
            // A failed read falls through to emitting: the duplicate costs a line whose n is 0,
            // a missing row costs the answer.
            if (Reflect.Call(__instance, "ShouldRemoveDueToAmount") is true) return;
            var owner = Reflect.GetMember(__instance, "Owner");
            var line = ReplayRecorder.Line("power_lost");
            if (line == null) return;
            var held = AmountOf(__instance);
            line.Set("id", Ids.Bare(Reflect.GetString(__instance, "Id")))
                // Same two fields as a power row and the same meanings: `n` is the delta, and
                // `amount` is the total it lands on. The instance is gone, so the total is 0
                // whatever the amount read did, and `n` is omitted rather than guessed when the
                // amount could not be read.
                .Set("n", held is { } a ? -a : (int?)null)
                .Set("amount", 0)
                .Set("tgt", Ids.Bare(Reflect.GetString(owner, "ModelId")))
                .Set("tgt_cid", CreatureSlots.Maybe(owner))
                // Why the board changed, where it is readable off the two objects already in
                // hand. A death sweep and a combat teardown are bulk wipes a consumer will want
                // to treat differently from Expose stripping one Artifact, and neither is
                // inferable from a removal row alone. Both are omitted rather than defaulted:
                // untagged means "could not tell", never "no". There is no `src` at all -- the
                // funnel takes no applier, so who removed it is genuinely not in scope here.
                .Set("dead", Reflect.GetMember(owner, "IsAlive") is false ? (object)true : null)
                .Set("post_combat", PostCombat());
            StampInstance(line, __instance);
            line.Emit();
        }
        catch { }
    }

    // PowerModel.Amount, or null when it cannot be read. Never 0 as a stand-in: `amount` is read
    // as the board's current total, where 0 says the power is gone.
    private static int? AmountOf(object? power)
        => Reflect.GetMember(power, "Amount") is int a ? a : (int?)null;

    // Which INSTANCE of a power a row is about, for the powers that can have more than one.
    //
    // PowerCmd.FindExistingInstanceForStacking switches on PowerModel.InstanceType: `Instanced`
    // never stacks and always adds a second instance (The Bomb, Sandpit, Monologue), and
    // `InstancedPerApplier` keeps one per applier (Strangle, Oblivion). One creature therefore
    // carries several live powers with the same id, and id + tgt_cid silently fuses them into one
    // power whose amount jumps around. 883 rows of the recorded corpus are these powers.
    //
    // Written for those two only. For PowerInstanceType.None the game guarantees uniqueness
    // itself -- Creature.ApplyPowerInternal throws on a second non-instanced instance of the same
    // type -- so id + tgt_cid is already a key there and a pid would be 64k rows of restatement.
    // An unreadable InstanceType writes nothing, losing the discriminator rather than inventing
    // one, and keeps the game's own spelling of the value the way reward_kind keeps RelicReward.
    private static void StampInstance(ReplayLine line, object? power)
    {
        if (power == null) return;
        var instancing = Reflect.GetMember(power, "InstanceType")?.ToString();
        if (instancing == null || instancing == "None") return;
        line.Set("inst", instancing).Set("pid", PowerInstances.Of(power));
    }

    // The last thing asked of a power before the modifiers ran, keyed on the PowerModel the
    // request was about. The applier is stored as the two already-resolved row fields rather than
    // as the Creature, so a stashed intent never keeps a body alive for the sake of a field.
    private sealed class Intent
    {
        public int Amount;
        public string Src = "effect";
        public int? SrcCid;
        public string? Card;
    }

    private static readonly ConditionalWeakTable<object, Intent> Intents = new();

    // The stashed intent for this power, but only when it belongs to the change being recorded.
    //
    // `src` is matched because nesting on one power instance is possible -- a listener of the
    // before hook is free to modify the same power -- and a stale intent written onto a later row
    // would be exactly the plausible wrong number this file keeps warning about. A mismatch, or a
    // missing stash, reads as unknown.
    private static int? IntendedAmount(object? power, string src)
    {
        if (power == null) return null;
        return Intents.TryGetValue(power, out var intent) && intent.Src == src
            ? intent.Amount : (int?)null;
    }

    // CombatManager.Instance.IsInProgress, as a tag on the teardown wipes.
    //
    // EndCombatInternal sets IsInProgress = false before it does anything else, and both
    // Player.AfterCombatEnd and CombatManager.Reset wipe every remaining power well after that,
    // so a false here reads "the fight was already over". A run abandoned mid-fight is the
    // exception: that wipe runs from RunManager's cleanup with the fight still flagged in
    // progress, so those rows go out untagged.
    //
    // Returns null and never false, so a renamed IsInProgress cannot read as "during the fight".
    private static Type? _combatManagerType;

    private static object? PostCombat()
    {
        var mgr = Reflect.GetStatic(_combatManagerType, "Instance");
        if (mgr == null) return null;
        return Reflect.GetMember(mgr, "IsInProgress") is false ? (object)true : null;
    }

    // Current or max HP read STRICTLY: an int or nothing.
    //
    // Reflect.GetInt's fallback cannot be used for these. Its default is 0, and 0 HP is a real
    // value that means dead, so a renamed property would ship "this creature is at 0" on every
    // row instead of saying nothing. Every caller below bails rather than guess.
    private static int? HpOf(object? creature, string name)
        => Reflect.GetMember(creature, name) is int v ? v : (int?)null;

    // Whose HP moved, in co-op only.
    //
    // Written only for a player's creature. A monster belongs to neither player, so mine:false
    // on a monster row would read as "the other player's", which is worse than leaving it off.
    // Null in single-player, where every player creature is ours and the field would be a
    // constant, exactly as Mine() already behaves for relic rows.
    private static bool? MineCreature(object? creature)
        => Reflect.GetMember(creature, "IsPlayer") is true
            ? Mine(Reflect.GetMember(creature, "Player"))
            : (bool?)null;

    // CreatureCmd.Heal(creature, amount, playAnim). Patched directly because the game's
    // AfterCurrentHpChanged did not produce an hp line for a rest-site heal, so four rest
    // floors in a real journal recorded the option taken and never the amount. Prefix, so the
    // amount is the one being applied rather than one re-derived after the fact.
    //
    // NOT gated on IsPlayer any more. CreatureCmd.Heal is the only caller of
    // Creature.HealInternal in the whole assembly, so this one prefix is every heal in the
    // game, and the gate was throwing away all of them that landed on a body other than the
    // local player: Knowledge Demon's 30-per-player self-heal, Waterfall Giant's Siphon, Test
    // Subject, Regen and Reattach on a monster, Osty, and the pets Spur heals. Without those a
    // consumer subtracting hit.dmg from combat_start.hp has the wrong number for that body for
    // the rest of the fight.
    //
    // `mine` is what fixes Mend. MendRestSiteOption.OnSelect heals ANOTHER player -- its own
    // targeting filter refuses the caster -- so a teammate's Mend used to be recorded as the
    // local player's HP going up, with nothing on the row to say otherwise. Round Tea Party
    // heals a chosen player the same way.
    //
    // `d` is the HP the creature ACTUALLY gains, not the amount asked for. Creature.HealInternal
    // clamps at the cap, so the old row claimed a 7-point heal at 78/80 and an hp of 85, a value
    // the game will never hold. `overheal` carries what was thrown away, so a Regen tick at full
    // HP is d:0 overheal:3 (a real fact) rather than a silent lie, and the old field is
    // recoverable as d + overheal.
    private static void Healed(object __0, decimal __1)
    {
        try
        {
            // Heal's own first guard: it returns before touching HP when combat is ending and
            // the target is not a player. Read the same way the game reads it.
            if (Reflect.GetMember(__0, "IsPlayer") is not true
                && Reflect.GetMember(
                       Reflect.GetStatic(_combatManagerType, "Instance"), "IsEnding") is true)
                return;
            if (__1 <= 0m) return; // no HP moves and no hook fires for a non-positive amount
            if (HpOf(__0, "CurrentHp") is not int oldHp) return;
            if (HpOf(__0, "MaxHp") is not int max) return;
            // Mirrors Creature.SetCurrentHpInternal, which is what HealInternal calls:
            // CurrentHp = (int)Math.Min(CurrentHp + amount, MaxHp).
            var newHp = (int)Math.Min(oldHp + __1, max);
            var d = newHp - oldHp;
            // Capped before the cast for the same reason MaxHpSet caps: the game's own ceiling
            // is 999999999 and an unbounded (int) on a decimal throws rather than saturating.
            var wasted = (int)Math.Min(__1, 999999999m) - d;
            ReplayRecorder.Line("hp")
                ?.Set("dst", CreatureRef(__0))
                .Set("dst_cid", CreatureSlots.Maybe(__0))
                .Set("d", d)
                .Set("hp", newHp)
                .Set("src", "heal")
                .Set("overheal", wasted > 0 ? wasted : (int?)null)
                .Set("mine", MineCreature(__0))
                .Emit();
        }
        catch { }
    }

    // --- attacks ---------------------------------------------------------------------
    //
    // Two things the hit and block rows could not say on their own.
    //
    // `atk` on a hit: which attack it belonged to. A monster multi-attack has no card, so its
    // hits were N unrelated rows and "how many times was I attacked" returned the hit count.
    // Hits of one attack share an id; damage that is not an attack (poison, thorns, relic
    // pings) carries none. An `attack` row is written at AfterAttack only when it says
    // something the hits don't: more than one hit, a random target, or fewer hits than planned.
    //
    // `mods` / `hp_mods` on a hit, `mods` on a block gain: the models that changed the number
    // (Strength, Vulnerable, Intangible, Frail, Tungsten Rod...). Card enchantments are applied
    // in the same Modify calls but the game never adds them to the list, so they can't appear.
    //
    // The modifier hooks carry no creature, so they are paired with the row by call order in
    // CreatureCmd.Damage / GainBlock. Every latch is checked against its combat state and
    // dropped on a new fight or run, so a leak from a throw can't reach a later fight.

    private sealed class AttackFrame
    {
        public object? Command, Attacker, Card, Combat;
        public long? Id;
        public int? Planned;
    }

    private sealed class DamageFrame
    {
        public object? Target, Dealer, Card, Combat;
        public List<string>? Mods, HpMods;
    }

    private sealed class BlockFrame
    {
        public object? Combat, Card;
        public decimal Amount;
        public List<string>? Mods;
    }

    private static readonly List<AttackFrame> OpenAttacks = new();
    private static readonly List<DamageFrame> OpenDamage = new();
    private static readonly List<BlockFrame> OpenBlock = new();
    // Damage modifiers between AfterModifyingDamageAmount and the BeforeDamageReceived that
    // names their target. A stack, not a slot: a listener of the first can deal damage itself.
    private static readonly Stack<List<string>?> PendingDamageMods = new();

    // Next attack id, or 0 when this journal session has not minted one yet.
    //
    // Seeded from the journal's last `s` + 1 and minted only for a hit row being written, so a
    // session never mints more ids than it writes lines. A reload resumes `s` above everything
    // in the file, so seeding from it can't reissue an id the earlier session used, with no
    // extra scan field. The ids are run-unique, not sequence numbers.
    private static long _atkNext;
    private static long _atkHigh;

    private static void ResetAttacks()
    {
        OpenAttacks.Clear();
        OpenDamage.Clear();
        OpenBlock.Clear();
        PendingDamageMods.Clear();
        _atkNext = 0;
    }

    private static long? NextAttackId()
    {
        if (_atkNext <= 0)
        {
            var path = ReplayRecorder.CurrentPath;
            if (path == null) return null;
            _atkNext = Math.Max(Math.Max(ReplayJournal.LastSequence(path) + 1, _atkHigh + 1), 1);
        }
        var id = _atkNext++;
        _atkHigh = Math.Max(_atkHigh, id);
        return id;
    }

    // Model ids of a modifier list, null when it is empty. An unreadable id is written as
    // "unknown" rather than dropped: dropping it could turn "one modifier" into "none".
    private static List<string>? ModelIds(object? models)
    {
        var ids = Enumerate(models)
            .Select(m => Ids.Bare(Reflect.GetString(m, "Id")) ?? "unknown")
            .Distinct().ToList();
        return ids.Count > 0 ? ids : null;
    }

    // BeforeAttack(combatState, command). Fires from AttackCommand.Execute and from
    // AttackContext.CreateAsync (Echoing Slash, Omnislice), which both pair it with AfterAttack.
    private static void AttackOpened(object __0, object __1)
    {
        try
        {
            if (__1 == null) return;
            OpenAttacks.RemoveAll(f => !ReferenceEquals(f.Combat, __0));
            if (OpenAttacks.Count > 16) OpenAttacks.Clear();
            OpenAttacks.Add(new AttackFrame
            {
                Command = __1,
                Attacker = Reflect.GetMember(__1, "Attacker"),
                // Execute passes `ModelSource as CardModel` as the Damage cardSource, and only
                // FromCard / FromOsty ever set ModelSource, so it is the card or null.
                Card = Reflect.GetMember(__1, "ModelSource"),
                Combat = __0,
            });
        }
        catch { }
    }

    // ModifyAttackHitCount(combatState, command, originalHitCount), postfix for the result.
    // Only AttackCommand.Execute calls it, right after BeforeAttack. The loop runs while
    // i < count, so a fractional count rounds up.
    private static void AttackHitCount(object __1, decimal __result)
    {
        try
        {
            var f = OpenAttacks.LastOrDefault(x => ReferenceEquals(x.Command, __1));
            if (f != null) f.Planned = (int)Math.Ceiling(__result);
        }
        catch { }
    }

    // The attack a hit belongs to, or null. Only the innermost open attack is considered, and
    // only when the dealer, the card and the combat are the attack's own and the damage is a
    // Move: that keeps thorns, relic pings and power ticks that fire inside an attack from
    // being stamped as part of it. A nested attack opens its own frame on top.
    private static long? AttackIdFor(object combat, object? dealer, object? card, object? props)
    {
        if (OpenAttacks.Count == 0) return null;
        var f = OpenAttacks[^1];
        if (!ReferenceEquals(f.Combat, combat) || !ReferenceEquals(f.Attacker, dealer)
            || !ReferenceEquals(f.Card, card)) return null;
        if (DamageProps(props) is not { } flags || !flags.Contains("move")) return null;
        return f.Id ??= NextAttackId();
    }

    // AfterAttack(combatState, choiceContext, command). Prefix, so the frame is closed before
    // any listener's own follow-up damage runs.
    private static void AttackClosed(object __2)
    {
        try
        {
            var at = OpenAttacks.FindLastIndex(x => ReferenceEquals(x.Command, __2));
            if (at < 0) return;
            var f = OpenAttacks[at];
            // Anything above it never saw its AfterAttack (a throw), so it goes too.
            OpenAttacks.RemoveRange(at, OpenAttacks.Count - at);

            // One inner list per hit, per the game's own doc on Results.
            var hits = Enumerate(Reflect.GetMember(__2, "Results")).Count();
            if (hits == 0) return;
            var random = Reflect.GetMember(__2, "IsRandomlyTargeted") is true;
            var shortOfPlan = f.Planned is int planned && planned != hits;
            if (hits == 1 && !random && !shortOfPlan) return; // the hit row already says it all

            ReplayRecorder.Line("attack")
                ?.Set("atk", f.Id)
                .Set("src", CreatureRef(f.Attacker))
                .Set("src_cid", CreatureSlots.Maybe(f.Attacker))
                .Set("card", f.Card == null ? null : Ids.Bare(Reflect.GetString(f.Card, "Id")))
                .Set("hits", hits)
                .Set("hits_planned", shortOfPlan ? f.Planned : null)
                .Set("random", random ? true : (bool?)null)
                .Emit();
        }
        catch { }
    }

    // AfterModifyingDamageAmount(runState, combatState, cardSource, modifiers). Only caller is
    // CreatureCmd.Damage, once per target, immediately before BeforeDamageReceived for that
    // target. Pushed even when empty so the two stay paired.
    private static void DamageModified(object __3)
    {
        try
        {
            if (PendingDamageMods.Count > 16) PendingDamageMods.Clear();
            PendingDamageMods.Push(ModelIds(__3));
        }
        catch { }
    }

    // BeforeDamageReceived(choiceContext, runState, combatState, target, amount, props, dealer,
    // cardSource). Binds the pending modifiers to the target. The frame is released by that
    // target's AfterDamageGiven, which Damage fires in a second loop after every target has
    // been through here, hence frames per target rather than one latch.
    private static void DamageLanding(object __2, object __3, object __6, object __7)
    {
        try
        {
            var mods = PendingDamageMods.Count > 0 ? PendingDamageMods.Pop() : null;
            // Out of combat Damage never fires AfterDamageGiven, so nothing would release it.
            if (__2 == null || __3 == null) return;
            OpenDamage.RemoveAll(f => !ReferenceEquals(f.Combat, __2));
            if (OpenDamage.Count > 32) OpenDamage.Clear();
            OpenDamage.Add(new DamageFrame
            {
                Target = __3, Dealer = __6, Card = __7, Combat = __2, Mods = mods,
            });
        }
        catch { }
    }

    // AfterModifyingHpLostBeforeOsty / AfterOsty(runState, combatState, modifiers). Both fire
    // inside the per-target loop after BeforeDamageReceived, so they belong to the most recently
    // opened frame (a nested call inside that window has already released its own).
    private static void HpLossModified(object __1, object __2)
    {
        try
        {
            if (__1 == null || OpenDamage.Count == 0) return;
            var f = OpenDamage[^1];
            if (!ReferenceEquals(f.Combat, __1)) return;
            if (ModelIds(__2) is not { } ids) return;
            f.HpMods = f.HpMods == null ? ids : f.HpMods.Union(ids).ToList();
        }
        catch { }
    }

    // Release the frame for this blow: the newest one for this target, when its dealer, card
    // and combat match. When Osty soaks a hit the frame is the original target's, so the Osty
    // row gets no modifiers and the owner's row gets them.
    private static DamageFrame? TakeDamageFrame(object combat, object target, object? dealer, object? card)
    {
        var at = OpenDamage.FindLastIndex(x => ReferenceEquals(x.Target, target));
        if (at < 0) return null;
        var f = OpenDamage[at];
        if (!ReferenceEquals(f.Combat, combat) || !ReferenceEquals(f.Dealer, dealer)
            || !ReferenceEquals(f.Card, card)) return null;
        OpenDamage.RemoveAt(at);
        return f;
    }

    // AfterModifyingBlockAmount(combatState, modifiedBlock, cardSource, cardPlay, modifiers).
    // Only caller is CreatureCmd.GainBlock, which fires AfterBlockGained with the same amount
    // and card after it, strictly nested around any gain a listener starts.
    private static void BlockModified(object __0, decimal __1, object __2, object __4)
    {
        try
        {
            OpenBlock.RemoveAll(f => !ReferenceEquals(f.Combat, __0));
            if (OpenBlock.Count > 16) OpenBlock.Clear();
            OpenBlock.Add(new BlockFrame { Combat = __0, Card = __2, Amount = __1, Mods = ModelIds(__4) });
        }
        catch { }
    }

    private static List<string>? TakeBlockMods(object combat, decimal amount, object? card)
    {
        var at = OpenBlock.FindLastIndex(x => ReferenceEquals(x.Combat, combat)
                                              && x.Amount == amount && ReferenceEquals(x.Card, card));
        if (at < 0) return null;
        var mods = OpenBlock[at].Mods;
        OpenBlock.RemoveRange(at, OpenBlock.Count - at);
        return mods;
    }

    // --- block -----------------------------------------------------------------------
    //
    // One row kind for the whole of block, gains and losses together, because the only question
    // block rows exist to answer is "how much did this body have at that moment" and that has to
    // be one series. The shape:
    //
    //   n       signed delta. Positive gained, negative removed. Absent when nothing moved
    //           (`prevented`, `broken`). A consumer that only sums n gets the level, without
    //           having to know which reasons subtract.
    //   left    the absolute Block on that body immediately after the event, so every row
    //           re-anchors the sum instead of letting it drift. Omitted, never 0, when the read
    //           fails.
    //   reason  gained | cleared | prevented | broken | lost.
    //
    // A `reason` field beats separate row kinds here for two reasons. A consumer that does not
    // know a reason still gets the arithmetic right from signed n, where an unknown row kind
    // would simply be skipped and the level would be wrong. And the reasons are not
    // independent: `broken` describes the same moment a `lost` or a hit row already accounts
    // for, so they have to be readable as one ordered series, which separate kinds fight.
    //
    // ONE PATH DELIBERATELY EMITS NO ROW: block eaten by damage. CreatureCmd.Damage takes it
    // through Creature.DamageBlockInternal, which nothing else calls and which has no hook, and
    // the amount is already on the hit row as `blocked`. Emitting it here as well would put the
    // same number in two places and invite a double count, and it would add a row to every
    // damaging hit in the file. So the rule for a consumer is: level = sum of block.n minus the
    // hit.blocked of every hit on that body since, re-anchored by `left` on the next block row.
    // `broken` exists to make that self-checking: it asserts the level is 0 at that point, which
    // is exactly what a partial consumption between rows would otherwise hide.

    // Block latched immediately before the turn-start wipe, keyed by the creature it is about to
    // come off. CombatManager.StartTurn runs every creature's AfterTurnStart, which is what calls
    // ClearBlock, in one loop and only then fires Hook.AfterBlockCleared for every creature in a
    // second loop, so a single field would be overwritten by the next creature before the hook
    // that needs it fires. Weak keys, same as MoveOwners: a creature the game drops is collected.
    private static readonly ConditionalWeakTable<object, object> BlockBeforeClear = new();

    // AfterBlockGained(combatState, creature, amount, props, cardSource)
    private static void BlockGained(object __0, object __1, decimal __2, object __4)
    {
        try
        {
            var mods = TakeBlockMods(__0, __2, __4); // attacks: see the attacks section

            // Monster block is recorded too. This used to return early for anything but the
            // player, so a monster that spent its turn gaining Block left nothing in the
            // journal at all and the turn read as "nothing recorded".
            BlockRow(__1, (int)__2, "gained")
                ?.Set("card", __4 == null ? null : Ids.Bare(Reflect.GetString(__4, "Id")))
                .Set("mods", mods)
                .Emit();
        }
        catch { }
    }

    // Creature.ClearBlock(), private, prefix. Latch only, no row: at this point it is not yet
    // known whether the wipe will happen at all, because the body asks Hook.ShouldClearBlock and
    // Barricade or Burrowed can refuse it. The row is written by whichever of the two hooks below
    // then fires.
    private static void BlockClearing(object __instance)
    {
        try
        {
            if (__instance == null) return;
            BlockBeforeClear.Remove(__instance);
            if (IntOf(__instance, "Block") is int block && block > 0)
                BlockBeforeClear.Add(__instance, block);
        }
        catch { }
    }

    // AfterBlockCleared(combatState, creature). The turn-start wipe.
    //
    // This hook fires UNCONDITIONALLY for every creature starting the turn, which is three
    // things it does not mean. It fires for a creature that had no block. It fires for the player
    // on turn 1, whose AfterTurnStart returns before ClearBlock is ever called. And it fires for a
    // creature whose wipe was prevented. So the row is written only when the latch says this body
    // actually held block and the body now reads exactly 0, the one combination that means the
    // block is gone. Everything else emits nothing rather than a zeroing row that is wrong.
    //
    // The consequence of that strictness: if the private ClearBlock is ever renamed, the latch is
    // always empty and clears stop appearing entirely rather than appearing without an amount.
    // That is the intended trade, and the "n/m patched" count is what surfaces it.
    private static void BlockCleared(object __1)
    {
        try
        {
            if (__1 == null) return;
            if (!BlockBeforeClear.TryGetValue(__1, out var latched)) return;
            BlockBeforeClear.Remove(__1);
            if (latched is not int had || had <= 0) return;
            if (IntOf(__1, "Block") != 0) return; // prevented, or something already put block back
            BlockRow(__1, -had, "cleared").Emit();
        }
        catch { }
    }

    // AfterPreventingBlockClear(combatState, preventer, creature). Barricade and Burrowed.
    //
    // Without this row a consumer that zeroes block at every turn start is wrong for exactly the
    // two cases where carried block is the whole point of the build, and it has nothing to tell
    // it so: the `cleared` row it would be pairing with simply never arrives. No `n`, nothing
    // moved. `left` states the level that survived, which makes the row an assertion rather than
    // a delta. `by` is the model that refused, so "why did this block survive" is answered on the
    // row instead of inferred from the relic and power list.
    private static void BlockClearPrevented(object __1, object __2)
    {
        try
        {
            if (__2 == null) return;
            // Drop the latch: the wipe did not happen, so the AfterBlockCleared that fires for
            // this same creature in StartTurn's next loop must not write a `cleared` row.
            BlockBeforeClear.Remove(__2);
            BlockRow(__2, null, "prevented")
                ?.Set("by", Ids.Bare(Reflect.GetString(__1, "Id")))
                .Emit();
        }
        catch { }
    }

    // AfterBlockBroken(combatState, creature). Fired from CreatureCmd.LoseBlock when a strip takes
    // the last of it, and from the WasBlockBroken sweep in CreatureCmd.Damage immediately before
    // the AfterDamageGiven that produces the hit row for the same blow.
    //
    // No `n` on purpose. The amount is already accounted for both times this fires: a strip writes
    // its own `lost` row a moment earlier, and block eaten by damage is on the hit row as
    // `blocked`. A delta here would double count either one. What the row adds is the zero
    // assertion described above, which is the only checkpoint a consumer gets for the damage path.
    private static void BlockBroken(object __1)
    {
        try
        {
            if (__1 == null) return;
            BlockRow(__1, null, "broken").Emit();
        }
        catch { }
    }

    // Creature.LoseBlockInternal(amount), prefix. Every block removal that is neither damage nor
    // the turn-start wipe funnels through here: CreatureCmd.LoseBlock, which is the Expose-style
    // strip and has no hook of its own, and Player.AfterCombatEnd's teardown wipe, which has no
    // witness anywhere else. Nothing else in the assembly calls it.
    //
    // A prefix, so Block is still the pre-clamp level and min(amount, Block) is the exact figure
    // LoseBlockInternal is about to remove. `left` is computed rather than read for the same
    // reason: the removal has not happened yet.
    //
    // `reason` stays the generic "lost" for both callers. The prefix cannot see which one called
    // it, and naming the cause wrong is worse than not naming it; the number is exact either way.
    // A consumer that needs to tell them apart has position: the teardown lands at combat end.
    private static void BlockLost(object __instance, decimal __0)
    {
        try
        {
            if (__instance == null || __0 <= 0m) return;
            if (IntOf(__instance, "Block") is not int block || block <= 0) return;
            var removed = (int)Math.Min(__0, block);
            if (removed <= 0) return;
            BlockRow(__instance, -removed, "lost", block - removed).Emit();
        }
        catch { }
    }

    // The one shape every block row is built from. `left` defaults to reading the creature, which
    // is right for the three hooks that fire after the fact; BlockLost passes it explicitly
    // because it runs before the removal. Omitted, never 0, when Block cannot be read.
    private static ReplayLine? BlockRow(object creature, int? n, string reason, int? left = null)
        => ReplayRecorder.Line("block")
            ?.Set("src", CreatureRef(creature))
            .Set("src_cid", CreatureSlots.Maybe(creature))
            .Set("n", n)
            .Set("left", left ?? IntOf(creature, "Block"))
            .Set("reason", reason);

    // AfterCurrentHpChanged(runState, combatState, creature, delta). Fires for EVERY creature,
    // and the game has exactly four callers: CreatureCmd.Damage, Kill, Heal and SetCurrentHp.
    //
    // This used to return for anything but the player, which is why no monster HP movement was
    // ever recorded. But ungating it and emitting everything is worse than the gate, because the
    // Damage caller fires one of these for every unblocked hit on either side, immediately
    // before the AfterDamageGiven that the `hit` row is built from. That would put tens of
    // thousands of rows in the corpus restating damage the journal already states.
    //
    // So a row is written only when nothing else already carries the number:
    //
    //   delta > 0              DROPPED. Only Heal and SetCurrentHp can raise HP, and both are
    //                          prefixed above, so each already writes its own row with a src and
    //                          a dst for every creature.
    //   delta < 0, in combat   DROPPED. Damage hands this hook the same `combatState` local it
    //                          gates the AfterDamageGiven call on, so combatState != null is
    //                          PROOF that a hit row carries this exact number for this exact
    //                          body. The Kill path's own -currentHp fire goes with it, and it is
    //                          the one thing this rule gives up: a damage-less death in combat
    //                          (Doom, The Gambit, a self-destruct, a cap driven to 0) used to
    //                          leave an hp row for the player and now leaves none. It is not
    //                          separable from a lethal hit here -- both fire -(HP remaining) and
    //                          both end at 0 -- and a death is Hook.AfterDeath's row to write,
    //                          not an hp row's. In combat the player's remaining HP is already
    //                          combat_start.hp minus the hits, and the death itself is on the
    //                          terminal line.
    //   delta < 0, no combat   KEPT. Out of combat AfterDamageGiven never fires, so no hit row
    //                          exists at all, and AfterDamageReceived is skipped when the blow
    //                          was fatal, so hp_loss is not always there either. A fatal event
    //                          hit has no other witness anywhere in the file.
    //
    // __1 is the combat state and __2 the creature. Confirmed against the hook's own signature
    // rather than counted: (IRunState runState, ICombatState? combatState, Creature creature,
    // decimal delta). One index out and the row would name the run state as the body.
    private static void HpChanged(object __1, object __2, decimal __3)
    {
        try
        {
            var d = (int)__3;
            if (d >= 0) return;
            if (__1 != null) return;
            ReplayRecorder.Line("hp")
                ?.Set("dst", CreatureRef(__2))
                .Set("dst_cid", CreatureSlots.Maybe(__2))
                .Set("d", d)
                // LoseHpInternal has already run by the time the hook fires, so this is the
                // post-hit value and the row is a resync point, not a delta to be accumulated.
                .Set("hp", HpOf(__2, "CurrentHp"))
                // Named rather than left blank. "loss" is HP gone with this row as its only
                // witness; heal and set are the two prefixes above. An hp row with no src at all
                // predates this.
                .Set("src", "loss")
                .Set("mine", MineCreature(__2))
                .Emit();
        }
        catch { }
    }

    // CreatureCmd.SetMaxHp(creature, amount). Every max HP move in the game funnels through
    // here: GainMaxHp calls it and then heals the delta, LoseMaxHp damages the overflow away and
    // then calls it, and SetMaxAndCurrentHp, OstyCmd and TestSubject call it directly. So this
    // one row separates Cook's +9, a Mango and Stone Humidifier from a campfire rest, and
    // Brightest Flame, Tablet of Truth and Night Terrors from combat damage.
    //
    // A prefix, so the delta is recomputed the way Creature.SetMaxHpInternal computes it --
    // Math.Min((int)Math.Max(0, amount), 999999999) against the cap that is still in place --
    // rather than read back afterwards. There is no afterwards to read from: SetMaxHp is
    // `async Task<decimal>` and returns the delta, so a postfix would be handed the Task.
    //
    // `hp` is here because SetMaxHpInternal assigns CurrentHp = Math.Min(CurrentHp, MaxHp)
    // DIRECTLY, firing no hook of its own, so a cap dropping below current HP moves current HP
    // with no witness anywhere. LoseMaxHp normally damages the overflow away first and leaves
    // nothing to clamp, but SetMaxAndCurrentHp lowering a monster's cap (Tough Egg hatching, a
    // Decimillipede segment) does not: SetMaxHp clamps it here, so the SetCurrentHp that follows
    // sees no change and fires nothing. This row is the only place that shows up.
    private static void MaxHpSet(object __0, decimal __1)
    {
        try
        {
            if (HpOf(__0, "MaxHp") is not int oldMax) return;
            // Clamped BEFORE the int cast, not after like the game does it. Same answer for
            // every value the game passes and no OverflowException on a larger one.
            var newMax = (int)Math.Min(Math.Max(0m, __1), 999999999m);
            // Test Subject rescaling to the cap it already has, and the second
            // SetMaxAndCurrentHp on an already-invincible Waterfall Giant. Nothing moved.
            if (newMax == oldMax) return;
            if (ReplayRecorder.Line("max_hp") is not { } line) return;
            line.Set("dst", CreatureRef(__0))
                .Set("dst_cid", CreatureSlots.Maybe(__0))
                .Set("d", newMax - oldMax)
                .Set("max_hp", newMax);
            if (HpOf(__0, "CurrentHp") is int cur) line.Set("hp", Math.Min(cur, newMax));
            line.Set("mine", MineCreature(__0)).Emit();
        }
        catch { }
    }

    // CreatureCmd.SetCurrentHp(creature, amount). A direct assignment, neither damage nor a
    // heal: Fur Coat marking an enemy down to 1 HP, and the phase transitions that arrive
    // through SetMaxAndCurrentHp.
    //
    // Patched even though SetCurrentHp fires AfterCurrentHpChanged itself, because HpChanged now
    // drops in-combat losses as restatements of a hit row and a set is not a hit. Prefix for the
    // same reason as SetMaxHp: async, and the old value only exists before the body runs.
    private static void CurrentHpSet(object __0, decimal __1)
    {
        try
        {
            if (HpOf(__0, "CurrentHp") is not int oldHp) return;
            if (HpOf(__0, "MaxHp") is not int max) return;
            // Mirrors Creature.SetCurrentHpInternal: CurrentHp = (int)Math.Min(amount, MaxHp).
            var newHp = (int)Math.Min(__1, max);
            if (newHp == oldHp) return;
            ReplayRecorder.Line("hp")
                ?.Set("dst", CreatureRef(__0))
                .Set("dst_cid", CreatureSlots.Maybe(__0))
                .Set("d", newHp - oldHp)
                .Set("hp", newHp)
                .Set("src", "set")
                .Set("mine", MineCreature(__0))
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
                ?.Set("decision_id", _decision > 0 ? _decision : (int?)null)
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
        _selectDecision = _decision;
        _selectDecisionType = _decisionType;

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
    private static void SelectionReturned(object __0, object __1)
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
                // The OFFER's decision, not whatever is open now. `joined` is proof the card
                // was in _selectByInstance, so the offer that map belongs to is definitionally
                // the owner -- and the `outcome` row for this same selection is written against
                // the same field, so using `_decision` here could have the pick and the outcome
                // name two different decisions for one screen.
                ?.Set("decision_id", joined ? _selectDecision : (int?)null)
                .Set("decision_type", joined ? _selectDecisionType : null)
                .Set("n_picked", picked.Count)
                // WHO answered. Both are omitted rather than defaulted when the member behind
                // them is gone, so their absence dates a capture and never asserts "a human
                // chose" -- see the _selectorProp comment.
                .Set("selector", SelectorName())
                .Set("mine", Mine(__0))
                .Set("cards", picked)
                .Emit();
        }
        catch { }
    }

    // "human" when the game says no selector is installed, the selector's type name when one
    // is, and null when we could not resolve the property at all. Spelling the human case
    // explicitly rather than leaving the field off is what makes an absent `selector` mean
    // "this capture predates the field" instead of being indistinguishable from it.
    private static string? SelectorName()
    {
        if (_selectorProp == null) return null;
        var selector = Reflect.Read(_selectorProp);
        return selector == null ? "human" : selector.GetType().Name;
    }

    // Whether our own player made this selection; false is a co-op partner's pick.
    //
    // Emitted ONLY in co-op. In single-player every pick is ours, so the field would be a
    // constant true on every row and say nothing. `selector` is the one that dates a capture,
    // because it is always written.
    private static bool? Mine(object? player)
        => LocalPlayer.IsCoop ? LocalPlayer.IsLocalPlayer(player) : (bool?)null;

    private static void CardRemoved(object __1)
    {
        try
        {
            ReplayRecorder.Line("remove")
                ?.Set("decision_id", _decision > 0 ? _decision : (int?)null)
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
                // Only the removal service IS the open decision's paid half. Shop stock is not
                // a decision in this schema -- the shop line lists it and `slot` below names
                // which entry was taken -- so a card, relic or potion purchase has no decision
                // to belong to. Attaching whatever happened to be open was worse than attaching
                // nothing: a removal screen stays open for the rest of the visit, so every later
                // purchase claimed it, and a relic that opens a selection screen gets billed to
                // the screen it caused, because its own purchase lands after that screen
                // resolves.
                ?.Set("decision_id",
                    kind == "removal_service" && _decision > 0 ? _decision : (int?)null)
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
                // "shop" joins the same way "granted" does -- not at all. Shop stock is not a
                // decision in this schema; the buy line's `slot` names which entry was taken.
                // It was inheriting the open decision, and a card-removal select stays open for
                // the rest of the visit, so a purchased card read as one of its picks.
                // A select that only TAKES cards can never be the decision that gave one.
                // WELLSPRING opens a removal screen and then grants a curse, so GUILTY arrived
                // pointing at the screen that had just removed a Strike -- reading as though a
                // removal offer had handed the player a curse. The event is the real grantor
                // and `source` already says so; attributing it to _prevDecision instead would
                // be a guess, and an unproven join is what this field refuses to make.
                ?.Set("decision_id", offered ? decisionForCard
                        : source == "granted" || source == "shop" ? (int?)null
                        : _decisionType == "deck_select_remove"
                          || _decisionType == EnchantSelectType ? (int?)null
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
            // The deck listing must re-read: this changes card state without
            // changing membership, which the deck signature cannot see.
            ReplayRecorder.MarkDeckChanged();
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

            // The deck listing must re-read: this changes card state without
            // changing membership, which the deck signature cannot see.
            ReplayRecorder.MarkDeckChanged();
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

    // The original of the transformation the game is applying right now.
    //
    // Safe as a single slot because CardCmd.Transform's loop calls AfterTransformedFrom on the
    // original and AfterTransformedTo on the replacement adjacently, with no await and no other
    // call between them, so neither another transformation in the same batch nor another batch
    // can interleave. Consumed and cleared by TransformedTo, which is what makes a missed From
    // cost a from_c instead of mis-pairing one: the only shipped override,
    // SovereignBlade.AfterTransformedFrom, does not call base, so a forged blade transformed away
    // emits the row with its `from` half absent rather than pointing at the previous card.
    private static object? _transformFrom;

    private static void TransformedFrom(object __instance)
    {
        _transformFrom = __instance;
    }

    // Transform genuinely swaps objects, so this is the one place the lineage thread would
    // break. Written from the replacement's side because that is where both halves are final.
    private static void TransformedTo(object __instance)
    {
        var from = _transformFrom;
        _transformFrom = null;
        try
        {
            ReplayRecorder.Line("transform")
                ?.Set("decision_id", _decision > 0 ? _decision : (int?)null)
                // The option index belongs to the card that was CHOSEN, which is the one that
                // was on offer; the replacement was never in the deck when the select opened.
                .Set("option_index", SelectIndexOf(from))
                // Explicitly null rather than CardInstances.Of(null), which is the 0 sentinel and
                // would read downstream as a real instance id.
                .Set("from_c", from == null ? (int?)null : CardInstances.Of(from))
                .Set("from_id", Ids.Bare(Reflect.GetString(from, "Id")))
                .Set("to_c", CardInstances.Of(__instance))
                .Set("to_id", Ids.Bare(Reflect.GetString(__instance, "Id")))
                .Emit();
        }
        catch { }
    }

    private static void RelicObtained(object __0)
    {
        try
        {
            // A relic arriving mid-purchase came off a shelf, not out of the open decision.
            // _pendingBuyKind is set by PurchaseAttempt and cleared by ItemPurchased, so it is
            // non-null exactly for the window this hook fires in during a buy. Without the
            // gate a shop relic claimed whatever screen was open -- a card-removal select
            // stays open for the whole visit, so the relic rendered as one of its options.
            var fromShelf = _pendingBuyKind != null;
            // A relic keeps its decision only when the open decision is one that can GRANT a
            // relic. An allow-list, not a deny-list: the shelf gate above only knows about
            // purchases, and a relic that is simply granted while any card screen is open fell
            // straight through it. Neow's "remove two cards" select is open as the run's
            // starting relics arrive, and a combat's card reward is open as its relic drops --
            // so LARGE_CAPSULE and friends rendered as resolutions of a card-removal offer, and
            // 95 more relics as resolutions of card rewards, across 28 version-5 journals.
            //
            // `event` is the only captured decision type that grants relics today. A relic
            // reward screen would be another, but nothing records its offer yet (STS-98), so
            // there is no decision for such a relic to belong to and omitting is correct.
            // Deciding on the open decision's TYPE asks the right question: not how the relic
            // arrived, but whether the thing waiting for an answer could produce one at all.
            var grantable = _decisionType == "event";
            ReplayRecorder.Line("relic")
                ?.Set("decision_id",
                      !fromShelf && grantable && _decision > 0 ? _decision : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__0, "Id")))
                .Emit();
        }
        catch { }
    }

    // RelicCmd.Replace(original, replace) prefix. Replace is Remove-then-Obtain, so the only
    // thing the nested Remove cannot work out for itself is WHY it was called. Same idiom as
    // _pendingBuyKind, which exists so a relic arriving mid-purchase knows it came off a shelf.
    private static bool _replacingRelic;

    private static void RelicReplacing() => _replacingRelic = true;

    // RelicCmd.Remove(RelicModel relic) postfix. The journal recorded every relic gained and
    // none lost, so a run that handed one to Ranwid the Elder read as still holding it for the
    // rest of the run -- and a relic that changes what every fight does (Red Mask putting Weak
    // on every enemy at the top of every turn) went on doing it in the model for 28 more floors.
    //
    // The only witness before this was the event outcome's display LABEL ("Give Red Mask"),
    // which is localized, and which in 2 of 6 real cases was the unsubstituted template
    // "Give {Relic}" -- unreadable in any language.
    //
    // Owner survives this point: RemoveRelicInternal -> RelicModel.RemoveInternal() only sets
    // HasBeenRemovedFromState, so the co-op owner check below still resolves.
    //
    // The HasBeenRemovedFromState guard is doing real work, not belt-and-braces. Remove() is an
    // `async Task`, so a throw inside it is captured on the returned Task instead of
    // propagating, and a postfix therefore runs even when the removal FAILED --
    // RemoveRelicInternal throws when the player does not hold the relic. The flag is set by the
    // removal itself, so it is the one thing on hand that tells the two apart. Without it this
    // hook would happily record relics that never left.
    //
    // Not covered, deliberately: RelicCmd.Melt (ToyBox). A melted relic STAYS in the inventory
    // and stops working, which is a state change and not a departure; filing it under a "lost"
    // row would tell a consumer the wrong thing. It needs its own answer.
    private static void RelicRemoved(object __0)
    {
        try
        {
            var replacing = _replacingRelic;
            _replacingRelic = false;
            if (Reflect.GetMember(__0, "HasBeenRemovedFromState") is not true) return;
            ReplayRecorder.Line("relic_lost")
                // Only an event may own a removal. Measured against the game's callers:
                // RanwidTheElder and RelicTrader go through an event decision, while
                // SwordOfStone and TouchOfOrobas reach Remove via Replace with no decision open
                // at all, and the dev console has none either. Without the gate those three
                // would inherit whichever screen happened to be open, which is the same
                // mis-join a shop relic used to make.
                ?.Set("decision_id",
                      !replacing && _decisionType == "event" && _decision > 0
                          ? _decision : (int?)null)
                .Set("id", Ids.Bare(Reflect.GetString(__0, "Id")))
                // "replaced" pairs this with the `relic` row that follows it from the same
                // swap. Stated rather than left to be inferred from adjacency, which is only
                // ever a guess about ordering.
                .Set("reason", replacing ? "replaced" : "removed")
                .Set("mine", Mine(Reflect.GetMember(__0, "Owner")))
                .Emit();
        }
        catch { }
    }

    // AfterPotionUsed(IRunState, ICombatState?, PotionModel potion, Creature? target). The target
    // was being discarded, so a Fire Potion thrown at one of three Corpse Slugs said which potion
    // and not which body, and the `hit` rows it causes name no potion to join back on.
    // AfterPotionProcured and AfterPotionDiscarded have no target argument at all.
    private static void PotionUsed(object __2, object __3) => Potion("potion_used", __2, __3);
    private static void PotionProcured(object __2) => Potion("potion_got", __2);
    private static void PotionDiscarded(object __2) => Potion("potion_dropped", __2);

    private static void Potion(string kind, object potion, object? target = null)
    {
        try
        {
            ReplayRecorder.Line(kind)
                ?.Set("id", Ids.Bare(Reflect.GetString(potion, "Id")))
                // Model id AND slot, the same pair and the same field names as the play row: the id
                // names a species, so against two of the same enemy it cannot say which one drank
                // the debuff. Both null for an untargeted potion, which is most of them.
                .Set("target",
                     target == null ? null : Ids.Bare(Reflect.GetString(target, "ModelId")))
                .Set("target_cid", CreatureSlots.Maybe(target))
                .Emit();
        }
        catch { }
    }

    // AfterRestSiteHeal(IRunState, Player, bool isMimicked). isMimicked is true when the heal came
    // from somewhere that is not a campfire: PlayerCmd.MimicRestSiteHeal calls
    // HealRestSiteOption.ExecuteRestSiteHeal(player, isMimicked: true), and DenseVegetation's Rest
    // option is its only caller in the shipped assembly. The flag was discarded, so the row said
    // "campfire heal" for an event.
    //
    // The flag is ADDED rather than the row suppressed. Verified in PLAY0034-1790606094 at s=382: a
    // rest option=heal filed on floor 9 while the open room was the DENSE_VEGETATION event, so
    // anything counting campfire visits over-counts by one. Deleting the row would fix that count
    // and destroy the only witness that the option was taken -- the `hp` row the heal produces says
    // a heal happened, not that an event offered a rest -- and silence is the failure this record
    // exists to avoid. A consumer counting campfires filters on the flag instead.
    private static void RestHeal(bool __2)
    {
        try
        {
            ReplayRecorder.Line("rest")
                ?.Set("option", "heal")
                // SetFlag, so the false case is written too: a heal with no flag at all would be a
                // journal from before this existed, and that is worth being able to tell apart.
                .SetFlag("mimicked", __2)
                .Emit();
        }
        catch { }
    }

    private static void RestSmith()
    {
        try { ReplayRecorder.Line("rest")?.Set("option", "smith").Emit(); } catch { }
    }

    // --- events ----------------------------------------------------------------------

    // The decision id of the event page currently on screen, or 0 when the open page offers no
    // real choice at all (the finished page, whose only button is the UI's synthesized Proceed).
    //
    // Deliberately separate from _decision. The two move at different moments: a page's offer is
    // known the instant the page is built, while _decision only becomes this page when an option
    // on it is actually taken. Collapsing them would file a grant made by page 1's option under
    // page 2, because an option body usually sets its next page before it grants anything.
    private static int _eventDecision;

    // Index of the page currently open, within the current event VISIT. -1 before the first page.
    // Neow's boon page and its done page are two pages of one visit, not one decision with two
    // winners, and Orobas is up to four.
    private static int _eventPageIndex = -1;

    // The event whose page is open, latched from EventModel.Id so the proceed row does not have
    // to reach into NEventRoom's private _event field to name its own event.
    private static string? _eventId;

    // EventOption.IsLocked / IsProceed and DynamicVarSet.AddTo, resolved once at install. See the
    // install block for why all three are handles rather than read or called by name.
    private static PropertyInfo? _optLockedProp;
    private static PropertyInfo? _optProceedProp;
    private static MethodInfo? _addVarsMethod;

    // EventModel.BeginEvent(Player player, bool isPreFinished) prefix. One visit, one page
    // numbering.
    //
    // Owner is assigned inside BeginEvent, so the local check reads the Player argument instead.
    // In co-op the synchronizer clones one EventModel per player and begins each of them, so
    // without the check a two-player run would reset (and later double-record) every page.
    private static void EventBegun(object __0)
    {
        try
        {
            if (LocalPlayer.IsCoop && !LocalPlayer.IsLocalPlayer(__0)) return;
            _eventPageIndex = -1;
            _eventDecision = 0;
            _eventId = null;
        }
        catch { }
    }

    // EventModel.SetEventState(LocString description, IEnumerable<EventOption> options) postfix.
    // One row per page, carrying the option set that was on screen.
    //
    // The offer is read off CurrentOptions rather than off the `options` argument. Not paranoia:
    // CurrentOptions is the list NEventRoom.SetOptions renders and the list
    // EventSynchronizer.ChooseOptionForEvent indexes into, so recording it is what makes this
    // row's option_index the same number the outcome reports. The argument is an arbitrary
    // IEnumerable from thirty-odd call sites and has already been copied by the time we run.
    //
    // A page with no real option gets NO decision row. Two cases reach it. The common one is the
    // finished page: SetEventFinished calls through here with an empty list, which is also what
    // flips IsFinished, and NEventRoom then mints a Proceed button that exists only in the UI.
    // The rare one is an Ancient blocked by Hook.ShouldAllowAncient, whose CurrentOptions is a
    // single real option with IsProceed set. Neither is a choice, and minting decisions for them
    // is what produced 706 zero-option decision rows in the corpus.
    private static void EventPageShown(object __instance)
    {
        try
        {
            var owner = Reflect.GetMember(__instance, "Owner");
            if (LocalPlayer.IsCoop && !LocalPlayer.IsLocalPlayer(owner)) return;

            // Whatever was open belonged to the page being replaced.
            _eventDecision = 0;
            var pageIndex = ++_eventPageIndex;
            _eventId = Ids.Bare(Reflect.GetString(__instance, "Id"));

            var options = new List<ReplayLine>();
            var index = 0;
            var real = 0;
            var selectable = 0;
            var lockedKnown = true;
            string? pageKey = null;
            var pageKeyAgrees = true;

            foreach (var opt in Enumerate(Reflect.GetMember(__instance, "CurrentOptions")))
            {
                var key = Reflect.GetString(opt, "TextKey");
                var proceed = OptFlag(_optProceedProp, opt);
                var locked = OptFlag(_optLockedProp, opt);
                if (proceed is not true) real++;
                if (locked == null) lockedKnown = false;
                else if (locked == false) selectable++;

                // Proceed options stay in the list so option_index keeps matching the game's own
                // indexing, even though a page made only of them emits no decision at all.
                var row = new ReplayLine("o")
                    .Set("option_index", index++)
                    .Set("option_kind", proceed is true ? "proceed" : "event_option")
                    .Set("option_id", key)
                    .Set("label", OptionText(__instance, opt, "Title"))
                    .Set("desc", OptionText(__instance, opt, "Description"))
                    .Set("grants_card", RoomExport.OptionCard(opt))
                    .Set("grants_relic", Ids.Bare(Reflect.GetString(Reflect.GetMember(opt, "Relic"), "Id")))
                    .SetFlag("presented", true);
                if (locked != null)
                {
                    row.SetFlag("selectable", locked == false);
                    if (locked == true) row.Set("selectable_reason", "locked");
                }
                options.Add(row);

                if (PageKeyOf(key) is not { } p) continue;
                if (pageKey == null) pageKey = p;
                else if (pageKey != p) pageKeyAgrees = false;
            }

            if (real == 0) return;

            // Mint only when something will read it, the same contract OpenSelectOffer keeps:
            // advancing decision state for a run nobody is recording leaves the next real
            // decision with a gap in its id.
            if (ReplayRecorder.Line("decision") is not { } line) return;
            _eventDecision = ReplayRecorder.NextDecisionId();
            line.Set("decision_id", _eventDecision)
                .Set("decision_type", "event")
                .Set("source", "event")
                .Set("event_id", _eventId)
                .Set("page_index", pageIndex)
                .Set("page_key", pageKeyAgrees ? pageKey : null)
                .Set("n_presented", options.Count)
                .Set("n_selectable", lockedKnown ? selectable : (int?)null)
                .Set("options", options);
            StampEventFloor(line, owner);
            line.Emit();
        }
        catch { }
    }

    // EventSynchronizer.ChooseOptionForEvent(Player player, int optionIndex) prefix. The option
    // that actually ran, and the slot it occupied on the page the decision row listed.
    //
    // option_index is the join that survives a duplicate option id, and duplicates are real:
    // War Historian Repy reuses its INITIAL loc keys on a later page, and every one of Neow's
    // per-modifier pages keys on the modifier's own id.
    //
    // Not patched at EventOption.Chosen(), which is where this used to sit. Chosen() holds no
    // reference back to its EventModel and no index, so it could name neither the page nor the
    // slot, and in co-op it fires once per player's clone with no way to tell them apart.
    private static void EventOptionChosen(object __instance, object __0, int __1)
    {
        try
        {
            if (LocalPlayer.IsCoop && !LocalPlayer.IsLocalPlayer(__0)) return;

            var model = Reflect.CallWith(__instance, "GetEventForPlayer", __0);
            var opt = ElementAt(Reflect.GetMember(model, "CurrentOptions"), __1);
            var chosenKey = Reflect.GetString(opt, "TextKey");
            var label = OptionText(model, opt, "Title") ?? chosenKey;

            // The clicked page becomes the open decision HERE, not when the page appeared, so a
            // relic granted or removed by this option joins to the page that offered it.
            // RelicObtained and RelicRemoved both gate on _decisionType == "event".
            DemoteDecision();
            if (_eventDecision > 0)
            {
                _decision = _eventDecision;
                _decisionType = "event";
            }

            ReplayRecorder.Line("outcome")
                ?.Set("decision_id", _eventDecision > 0 ? _eventDecision : (int?)null)
                .Set("decision_type", "event")
                .Set("outcome", "chosen")
                .Set("option_index", __1)
                .Set("option_id", chosenKey)
                .Set("label", label)
                .Emit();
        }
        catch { }
    }

    // NEventRoom.OptionButtonClicked(EventOption option, int index) prefix. The Proceed button,
    // and nothing else.
    //
    // Proceed gets a row of its own rather than a decision and an outcome. It is not a decision:
    // NEventRoom.SetOptions synthesizes it whenever EventModel.IsFinished, it exists in no
    // CurrentOptions on that path, and it has no alternatives, so there is nothing rejected and
    // nothing revealed. Recording it as one is what put 707 phantom outcomes and 599 zero-option
    // decisions in the corpus. It is not nothing either: "the player read the last page and went
    // back to the map" is the only thing separating a finished event from a run that quit on it.
    //
    // Real choices are deliberately NOT recorded here. This is the LOCAL CLICK, which in a shared
    // co-op event is a vote that can lose; ChooseOptionForEvent above is the option that ran.
    private static void EventProceedClicked(object __0)
    {
        try
        {
            if (OptFlag(_optLockedProp, __0) is true) return;      // the game ignores the click
            if (OptFlag(_optProceedProp, __0) is not true) return; // a real choice, recorded above
            _eventDecision = 0;
            ReplayRecorder.Line("event_proceed")
                ?.Set("event_id", _eventId)
                .Set("page_index", _eventPageIndex >= 0 ? _eventPageIndex : (int?)null)
                .Emit();
        }
        catch { }
    }

    // A bool off a resolved handle. Null means the member is gone, which is not the same answer
    // as false and must not be written as one.
    private static bool? OptFlag(PropertyInfo? prop, object? opt)
    {
        if (prop == null || opt == null) return null;
        try { return prop.GetValue(opt) as bool?; }
        catch { return null; }
    }

    // An option's localized title or description, resolved exactly the way the game's own button
    // resolves it.
    //
    // The AddTo call is not decoration and it is not a mutation of game state: a LocString keeps
    // its own variable bag purely so it can be formatted, NEventOptionButton._Ready calls this
    // same pair on these same LocStrings a moment later, and EventModel.GameInfoOptions does it
    // to throwaway LocStrings for the same reason.
    //
    // Skipping it is the trap. The FIRST page of an event is set inside BeginEvent, before
    // NEventRoom exists and therefore before any button has added the event's DynamicVars, so
    // formatting it unprepared hits a missing variable -- and LocManager.SmartFormat answers a
    // missing variable by logging an error, CAPTURING A SENTRY EXCEPTION and returning the raw
    // template. That would both record "{Cards}" as a description and file a report in MegaCrit's
    // own Sentry project for every option of every event of every run.
    //
    // Which is why an unresolved AddTo means no text at all. A missing label is a missing field;
    // formatting anyway would be a wrong value AND telemetry noise in someone else's project.
    private static string? OptionText(object? model, object? opt, string member)
    {
        if (_addVarsMethod == null) return null;
        // GetOptionTitle/GetOptionDescription use LocString.GetIfExists, so a key the table does
        // not carry reads as a null LocString rather than an empty one.
        var loc = Reflect.GetMember(opt, member);
        if (loc == null) return null;
        var vars = Reflect.GetMember(model, "DynamicVars");
        if (vars == null) return null;
        try { _addVarsMethod.Invoke(vars, new[] { loc }); }
        catch { return null; }
        var text = Reflect.CallString(loc, "GetFormattedText");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // "NEOW.pages.INITIAL.options.LARGE_CAPSULE" -> "INITIAL", so a multi-page event is readable
    // without joining every row back together. Null for a key that does not follow the
    // convention, which is a real case rather than a defensive one: Neow's modifier pages key on
    // the modifier id and The Architect builds its own keys. A guessed page name is worse than
    // none, and page_index is always there.
    private static string? PageKeyOf(string? textKey)
    {
        if (textKey == null) return null;
        const string pages = ".pages.";
        const string opts = ".options.";
        var start = textKey.IndexOf(pages, StringComparison.Ordinal);
        if (start < 0) return null;
        start += pages.Length;
        var end = textKey.IndexOf(opts, start, StringComparison.Ordinal);
        return end > start ? textKey.Substring(start, end - start) : null;
    }

    // Stamp an event row with the event's own floor and act.
    //
    // EventRoom.EnterInternal begins the event BEFORE it awaits Hook.AfterRoomEntered, so the
    // recorder's floor latch still holds the PREVIOUS room's floor when an event's first page is
    // built. RunManager.EnterMapPointInternal has already appended the new map point by then, so
    // the owner's run state is the correct source and it is read straight off it.
    //
    // Both values or neither: a corrected floor paired with a stale act is worse than the pair
    // Line() already wrote.
    private static void StampEventFloor(ReplayLine line, object? owner)
    {
        var runState = Reflect.GetMember(owner, "RunState");
        if (runState == null) return;
        var floor = Reflect.GetInt(runState, "TotalFloor", -1);
        var actIndex = Reflect.GetInt(runState, "CurrentActIndex", -1);
        if (floor < 0 || actIndex < 0) return;
        line.Set("floor", floor).Set("act", actIndex + 1);
    }

    // --- creatures -------------------------------------------------------------------

    // One creature's roster fields, shared by combat_start (enemies, allies) and spawn so the
    // shapes cannot drift. Strict reads: an unreadable hp is omitted, never 0.
    private static ReplayLine CreatureEntry(ReplayLine row, object c)
        // `cid` is the body's identity, what every `*_cid` elsewhere refers to. Not the same as
        // `i`: position is per fight and shifts, identity is per run and never moves.
        => row.Set("cid", CreatureSlots.Maybe(c))
            .Set("id", Ids.Bare(Reflect.GetString(c, "ModelId")))
            // The game's own name for the position, where the encounter defines one (19 of 98).
            .Set("slot", Reflect.GetString(c, "SlotName"))
            .Set("hp", HpOf(c, "CurrentHp"))
            .Set("max_hp", HpOf(c, "MaxHp"));

    // CombatManager.AfterCreatureAdded(Creature). Starting creatures pass through here too,
    // from StartCombatInternal before IsInProgress flips, and combat_start declares those.
    // CreatureCmd.Add throws unless IsInProgress, so true here means a mid-fight arrival.
    // PREFIX: the method is async and the entry powers are applied inside it.
    private static void CreatureAdded(object __instance, object __0)
    {
        try
        {
            if (Reflect.GetMember(__instance, "IsInProgress") is not true) return;
            if (ReplayRecorder.Line("spawn") is not { } line) return;
            // hp is as created. Osty is added at its model default and then set by SetMaxHp
            // and Heal, which write their own max_hp and hp rows right after this one.
            CreatureEntry(line, __0)
                .Set("side", Reflect.GetMember(__0, "Side")?.ToString() switch
                {
                    "Enemy" => "enemy",
                    "Player" => "ally",
                    _ => null,
                })
                .Emit();
        }
        catch { }
    }

    // CombatState.CreatureEscaped(Creature). Only reached from CreatureCmd.Escape once its
    // guards pass (Fat Gremlin, Thieving Hopper, Battleworn Dummy). The power_lost rows from
    // Escape's power sweep land just before this row.
    private static void CreatureEscaped(object __0)
    {
        try
        {
            ReplayRecorder.Line("escape")
                ?.Set("tgt", CreatureRef(__0))
                .Set("tgt_cid", CreatureSlots.Maybe(__0))
                .Emit();
        }
        catch { }
    }

    // Bodies inside a DoomKill, by reference. Set in its prefix (before any Kill runs) and
    // cleared by Hook.AfterDiedToDoom, which DoomKill awaits after the last Kill, so a doomed
    // body that was revived and dies later to a Strike is not blamed on Doom.
    private static readonly HashSet<object> _doomed = new(ReferenceEqualityComparer.Instance);

    // DoomPower.DoomKill(IReadOnlyList<Creature>), static. PREFIX: async, and the deaths
    // happen inside it.
    private static void DoomKillStarting(object __0)
    {
        try
        {
            foreach (var c in Enumerate(__0)) _doomed.Add(c);
        }
        catch { }
    }

    // Hook.AfterDiedToDoom(ICombatState, IReadOnlyList<Creature>). PREFIX so the marks are gone
    // before any listener can kill again.
    private static void DoomKillDone(object __1)
    {
        try
        {
            foreach (var c in Enumerate(__1)) _doomed.Remove(c);
        }
        catch { }
    }

    // --- intents ---------------------------------------------------------------------
    //
    // `intent` rows: what an enemy telegraphed and when. `at` says which moment:
    //   turn_start  every enemy, once the player's hand is dealt and they can act
    //   roll        a move rolled after that (mid-turn spawn)
    //   set         SetMoveImmediate replaced the shown move (stun, enrage, revive, phase)
    //   turn_end    BeforeTurnEnd, only for enemies whose shown move or numbers changed
    // `move` rows still mark when the move is performed.

    // The combat whose turn-start telegraph has been written, cleared at every side's turn
    // start. Rolls before it are the StartTurn batch the turn_start rows already cover.
    // Keyed to the combat so a new fight's setup rolls never read the last fight's latch.
    private static WeakReference<object>? _intentsShownFor;

    // The last (id, dmg, hits) written per creature, so turn_end only writes what moved.
    private static readonly ConditionalWeakTable<object, string> LastIntent = new();

    // AttackIntent.GetSingleDamage(targets, owner), per concrete intent type. Null when the
    // type has none, which is every non-attack intent.
    private static readonly Dictionary<Type, MethodInfo?> SingleDamageMethods = new();

    private static bool IntentsShownFor(object? combat)
        => combat != null && _intentsShownFor != null
           && _intentsShownFor.TryGetTarget(out var shown) && ReferenceEquals(shown, combat);

    // Hook.BeforeSideTurnStart(ICombatState combatState, CombatSide side, participants).
    // Runs before CombatManager.StartTurn's PrepareForNextTurn rolls.
    private static void IntentTurnStarting()
    {
        _intentsShownFor = null;
    }

    // Hook.AfterPlayerTurnStart(ICombatState combatState, PlayerChoiceContext, Player player).
    // Fires once per player in co-op; the latch keeps it to one telegraph per turn.
    private static void IntentsShown(object __0)
    {
        try
        {
            if (__0 == null || IntentsShownFor(__0)) return;
            _intentsShownFor = new WeakReference<object>(__0);
            foreach (var enemy in Enumerate(Reflect.GetMember(__0, "Enemies")))
            {
                var move = Reflect.GetMember(Reflect.GetMember(enemy, "Monster"), "NextMove");
                if (move != null) EmitIntent(enemy, move, "turn_start");
            }
        }
        catch { }
    }

    // Hook.BeforeTurnEnd(ICombatState combatState, CombatSide side, participants). The
    // player's side only: what they ended the turn against, where it differs from the last
    // row for that enemy (Weak applied, Strength gained, a move swapped).
    private static void IntentsCommitted(object __0, object __1)
    {
        try
        {
            if (__1?.ToString() != "Player") return;
            foreach (var enemy in Enumerate(Reflect.GetMember(__0, "Enemies")))
            {
                var move = Reflect.GetMember(Reflect.GetMember(enemy, "Monster"), "NextMove");
                if (move == null) continue;
                var (dmg, hits) = AttackNumbers(move, enemy);
                var sig = IntentSig(Reflect.GetString(move, "StateId"), dmg, hits);
                if (LastIntent.TryGetValue(enemy, out var last) && last == sig) continue;
                EmitIntent(enemy, move, "turn_end");
            }
        }
        catch { }
    }

    // MonsterModel.SetMoveImmediate(MoveState state, bool forceTransition). Prefix, so the
    // outgoing NextMove is still readable. Mirrors the method's own guard: when the current
    // move can't transition and the swap isn't forced, nothing changes and nothing is written.
    // Also latches the owner: these states never pass through RollMove, so without it a
    // stun from a card (Whistle) or a revive performed with no `move` row at all.
    private static void MoveSetImmediate(object __instance, object __0, object __1)
    {
        try
        {
            if (__0 == null || __1 is not bool force) return;
            var owner = Reflect.GetMember(__instance, "Creature");
            if (owner == null) return;
            // The bestiary calls this on display monsters outside any fight.
            if (Reflect.Call(Reflect.GetMember(owner, "CombatState"), "IsLiveCombat") is not true) return;
            var from = Reflect.GetMember(__instance, "NextMove");
            if (!force && Reflect.GetMember(from, "CanTransitionAway") is not true) return;
            if (ReferenceEquals(from, __0)) return;
            MoveOwners.Remove(__0);
            MoveOwners.Add(__0, owner);
            EmitIntent(owner, __0, "set", Reflect.GetString(from, "StateId"));
        }
        catch { }
    }

    private static void EmitIntent(object owner, object move, string at, string? from = null)
    {
        var id = Reflect.GetString(move, "StateId");
        var (dmg, hits) = AttackNumbers(move, owner);
        LastIntent.AddOrUpdate(owner, IntentSig(id, dmg, hits));
        var intents = IntentTypes(move);
        ReplayRecorder.Line("intent")
            ?.Set("src", CreatureRef(owner))
            .Set("src_cid", CreatureSlots.Maybe(owner))
            .Set("id", id)
            .Set("at", at)
            .Set("from", from)
            .Set("intents", intents.Count > 0 ? intents : null)
            .Set("dmg", dmg)
            .Set("hits", hits)
            .Emit();
    }

    private static string IntentSig(string? id, int? dmg, int? hits) => $"{id}|{dmg}|{hits}";

    // Lowercased IntentType names, the shape move.intents has always had.
    private static List<string> IntentTypes(object move)
        => Enumerate(Reflect.GetMember(move, "Intents"))
            .Select(i => Reflect.GetMember(i, "IntentType")?.ToString()?.ToLowerInvariant())
            .Where(x => x != null).Select(x => x!).ToList();

    // The per-hit damage the player's intent label shows, and the hit count. Omitted unless
    // the move has exactly one attack intent (every move in v0.107.1 has at most one).
    //
    // GetSingleDamage runs DamageCalc() and Hook.ModifyDamage against the local player. Both
    // are pure reads in v0.107.1 (no RNG, no writes; modifiers are returned, not applied), and
    // the game calls it on every intent refresh. It ignores `targets`, so null is passed.
    private static (int? dmg, int? hits) AttackNumbers(object move, object owner)
    {
        object? attack = null;
        MethodInfo? method = null;
        var count = 0;
        foreach (var intent in Enumerate(Reflect.GetMember(move, "Intents")))
        {
            var m = SingleDamageMethod(intent.GetType());
            if (m == null) continue;
            attack = intent;
            method = m;
            count++;
        }
        if (count != 1 || attack == null || method == null) return (null, null);
        int? dmg = null;
        try
        {
            if (method.Invoke(attack, new object?[] { null, owner }) is int d) dmg = d;
        }
        catch { }
        var hits = Reflect.GetMember(attack, "Repeats") is int r ? r : (int?)null;
        return (dmg, hits);
    }

    private static MethodInfo? SingleDamageMethod(Type type)
    {
        if (SingleDamageMethods.TryGetValue(type, out var cached)) return cached;
        MethodInfo? found = null;
        try
        {
            found = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetSingleDamage" && m.ReturnType == typeof(int)
                                     && m.GetParameters().Length == 2);
        }
        catch { }
        SingleDamageMethods[type] = found;
        return found;
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
