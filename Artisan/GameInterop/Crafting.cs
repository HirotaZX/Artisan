﻿using Artisan.CraftingLogic;
using Artisan.CraftingLogic.CraftData;
using Artisan.GameInterop.CSExt;
using Artisan.RawInformation.Character;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OtterGui;
using System;
using System.Linq;

namespace Artisan.GameInterop;

// state of the crafting process
// manages the 'inner loop' (executing actions to complete a single craft)
public static unsafe class Crafting
{
    public enum State
    {
        IdleNormal, // we're not crafting - the default state of the game
        IdleBetween, // we've finished a craft and have not yet started another, sitting in the menu
        WaitStart, // we're waiting for a new (quick) craft to start
        InProgress, // crafting is in progress, waiting for next action
        WaitAction, // we've executed an action and are waiting for results
        WaitFinish, // we're waiting for a craft to end (success / failure / cancel)
        QuickCraft, // we're inside quick craft loop
        InvalidState, // we're in a state we probably shouldn't be, such as reloading the plugin mid-craft
    }

    public static State CurState { get; private set; } = State.InvalidState;
    public static event Action<State>? StateChanged;

    public static Lumina.Excel.GeneratedSheets.Recipe? CurRecipe { get; private set; }
    public static CraftState? CurCraft { get; private set; }
    public static StepState? CurStep { get; private set; }
    public static bool IsTrial { get; private set; }

    public static (int Cur, int Max) QuickSynthState { get; private set; }
    public static bool QuickSynthCompleted => QuickSynthState.Cur == QuickSynthState.Max && QuickSynthState.Max > 0;

    public delegate void CraftStartedDelegate(Lumina.Excel.GeneratedSheets.Recipe recipe, CraftState craft, StepState initialStep, bool trial);
    public static event CraftStartedDelegate? CraftStarted;

    // note: step index increases for most actions (except final appraisal / careful observation / heart&soul)
    public delegate void CraftAdvancedDelegate(Lumina.Excel.GeneratedSheets.Recipe recipe, CraftState craft, StepState step);
    public static event CraftAdvancedDelegate? CraftAdvanced;

    // note: final action that completes/fails a craft does not advance step index
    public delegate void CraftFinishedDelegate(Lumina.Excel.GeneratedSheets.Recipe recipe, CraftState craft, StepState finalStep, bool cancelled);
    public static event CraftFinishedDelegate? CraftFinished;

    public delegate void QuickSynthProgressDelegate(int cur, int max);
    public static event QuickSynthProgressDelegate? QuickSynthProgress;

    private static StepState? _predictedNextStep; // set when receiving Advance*Action messages
    private static DateTime _predictionDeadline;

    private delegate void CraftingEventHandlerUpdateDelegate(CraftingEventHandler* self, nint a2, nint a3, CraftingEventHandler.OperationId* payload);
    private static Hook<CraftingEventHandlerUpdateDelegate> _craftingEventHandlerUpdateHook;

    static Crafting()
    {
        _craftingEventHandlerUpdateHook = Svc.Hook.HookFromSignature<CraftingEventHandlerUpdateDelegate>("48 89 5C 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 30 80 A1", CraftingEventHandlerUpdateDetour);
        _craftingEventHandlerUpdateHook.Enable();
    }

    public static void Dispose()
    {
        _craftingEventHandlerUpdateHook.Dispose();
    }

    // note: this uses current character stats & equipped gear
    public static CraftState BuildCraftStateForRecipe(CharacterStats stats, Job job, Lumina.Excel.GeneratedSheets.Recipe recipe)
    {
        var lt = recipe.RecipeLevelTable.Value;
        var res = new CraftState()
        {
            StatCraftsmanship = stats.Craftsmanship,
            StatControl = stats.Control,
            StatCP = stats.CP,
            StatLevel = CharacterInfo.JobLevel(job),
            UnlockedManipulation = CharacterInfo.IsManipulationUnlocked(job),
            Specialist = stats.Specialist,
            Splendorous = stats.Splendorous,
            CraftCollectible = recipe.ItemResult.Value?.IsCollectable ?? false,
            CraftExpert = recipe.IsExpert,
            CraftLevel = lt?.ClassJobLevel ?? 0,
            CraftDurability = Calculations.RecipeDurability(recipe),
            CraftProgress = Calculations.RecipeDifficulty(recipe),
            CraftProgressDivider = lt?.ProgressDivider ?? 180,
            CraftProgressModifier = lt?.ProgressModifier ?? 100,
            CraftQualityDivider = lt?.QualityDivider ?? 180,
            CraftQualityModifier = lt?.QualityModifier ?? 180,
            CraftQualityMax = Calculations.RecipeMaxQuality(recipe),
        };

        if (res.CraftCollectible)
        {
            // Check regular collectibles first
            var breakpoints = Svc.Data.GetExcelSheet<Lumina.Excel.GeneratedSheets.CollectablesShopItem>()?.FirstOrDefault(x => x.Item.Row == recipe.ItemResult.Row)?.CollectablesShopRefine.Value;
            if (breakpoints != null)
            {
                res.CraftQualityMin1 = breakpoints.LowCollectability * 10;
                res.CraftQualityMin2 = breakpoints.MidCollectability * 10;
                res.CraftQualityMin3 = breakpoints.HighCollectability * 10;
            }
            else // Then check custom delivery
            {
                var satisfaction = Svc.Data.GetExcelSheet<Lumina.Excel.GeneratedSheets.SatisfactionSupply>()?.FirstOrDefault(x => x.Item.Row == recipe.ItemResult.Row);
                if (satisfaction != null)
                {
                    res.CraftQualityMin1 = satisfaction.CollectabilityLow * 10;
                    res.CraftQualityMin2 = satisfaction.CollectabilityMid * 10;
                    res.CraftQualityMin3 = satisfaction.CollectabilityHigh * 10;
                }
                else // Finally, check Ishgard Restoration
                {
                    var hwdSheet = Svc.Data.GetExcelSheet<Lumina.Excel.GeneratedSheets.HWDCrafterSupply>()?.FirstOrDefault(x => x.ItemTradeIn.Any(y => y.Row == recipe.ItemResult.Row));
                    if (hwdSheet != null)
                    {
                        var index = hwdSheet.ItemTradeIn.IndexOf(x => x.Row == recipe.ItemResult.Row);
                        res.CraftQualityMin1 = hwdSheet.BaseCollectableRating[index] * 10;
                        res.CraftQualityMin2 = hwdSheet.MidCollectableRating[index] * 10;
                        res.CraftQualityMin3 = hwdSheet.HighCollectableRating[index] * 10;
                    }
                }
            }

            if (res.CraftQualityMin3 == 0)
            {
                res.CraftQualityMin3 = res.CraftQualityMin2;
                res.CraftQualityMin2 = res.CraftQualityMin1;
            }
        }
        else if (recipe.RequiredQuality > 0)
        {
            res.CraftQualityMin1 = res.CraftQualityMin2 = res.CraftQualityMin3 = res.CraftQualityMax = (int)recipe.RequiredQuality;
        }
        else if (recipe.CanHq)
        {
            res.CraftQualityMin3 = res.CraftQualityMax;
        }

        return res;
    }

    public static void Update()
    {
        // typical craft loop looks like this:
        // 1. starting from IdleNormal state (no condition flags) or IdleBetween state (Crafting + PreparingToCraft condition flags)
        // 2. user presses 'craft' button
        // 2a. craft-start animation starts - this is signified by Crafting40 condition flag, we transition to WaitStart state
        // 2b. quickly after that Crafting flag is set (if it was not already set before)
        // 2c. some time later, animation ends - at this point synth addon is updated and Crafting40 condition flag is cleared - at this point we transition to InProgress state
        // 3. user executes an action that doesn't complete a craft
        // 3a. client sets Crafting40 condition flag - we transition to WaitAction state
        // 3b. a bit later client receives a bunch of packets: ActorControl (to start animation), StatusEffectList (containing previous statuses and new cp) and UpdateClassInfo (irrelevant)
        // 3c. a few seconds later client receives another bunch of packets: some misc ones, EventPlay64 (contains new crafting state - progress/quality/condition/etc), StatusEffectList (contains new statuses and new cp) and UpdateClassInfo (irrelevant)
        // 3d. on the next frame after receiving EventPlay64, Crafting40 flag is cleared and player is unblocked
        // 3e. sometimes EventPlay64 and final StatusEffectList might end up in a different packet bundle and can get delayed for arbitrary time (and it won't be sent at all if there are no status updates) - we transition back to InProgress state only once statuses are updated
        // 4. user executes an action that completes a craft in any way (success or failure)
        // 4a-c - same as 3a-c
        // 4d. same as 3d, however Crafting40 flag remains set and crafting finish animation starts playing
        // 4e. as soon as we've got fully updated state, we transition to WaitFinish state
        // 4f. some time later, finish animation ends - Crafting40 condition flag is cleared, PreparingToCraft flag is set - at this point we transition to IdleBetween state
        // 5. user exits crafting mode - condition flags are cleared, we transition to IdleNormal state
        // 6. if at some point during craft user cancels it
        // 6a. client sets Crafting40 condition flag - we transition to WaitAction state
        // 6b. soon after, addon disappears - we detect that and transition to WaitFinish state
        // 6c. next EventPlay64 contains abort message - we ignore it for now
        // since an action can complete a craft only if it increases progress or reduces durability, we can use that to determine when to transition from WaitAction to WaitFinish
        var newState = CurState switch
        {
            State.IdleNormal => TransitionFromIdleNormal(),
            State.IdleBetween => TransitionFromIdleBetween(),
            State.WaitStart => TransitionFromWaitStart(),
            State.InProgress => TransitionFromInProgress(),
            State.WaitAction => TransitionFromWaitAction(),
            State.WaitFinish => TransitionFromWaitFinish(),
            State.QuickCraft => TransitionFromQuickCraft(),
            _ => TransitionFromInvalid()
        };
        if (newState != CurState)
        {
            Svc.Log.Debug($"Transition: {CurState} -> {newState}");
            CurState = newState;
            StateChanged?.Invoke(newState);
        }
    }

    private static State TransitionFromInvalid()
    {
        if (Svc.Condition[ConditionFlag.Crafting40] || Svc.Condition[ConditionFlag.Crafting] != Svc.Condition[ConditionFlag.PreparingToCraft])
            return State.InvalidState; // stay in this state until we get to one of the idle states

        // wrap up
        if (CurRecipe != null && CurCraft != null && CurStep != null)
            CraftFinished?.Invoke(CurRecipe, CurCraft, CurStep, true); // emulate cancel (TODO reconsider)
        return State.WaitFinish;
    }

    private static State TransitionFromIdleNormal()
    {
        if (Svc.Condition[ConditionFlag.Crafting40])
        {
            return State.WaitStart; // craft started, but we don't yet know details
        }

        if (Svc.Condition[ConditionFlag.PreparingToCraft])
        {
            Svc.Log.Error("Unexpected crafting state transition: from idle to preparing");
            return State.IdleBetween;
        }

        // stay in default state or exit crafting menu
        return State.IdleNormal;
    }

    private static State TransitionFromIdleBetween()
    {
        // note that Crafting40 remains set after exiting from quick-synth mode
        if (Svc.Condition[ConditionFlag.PreparingToCraft])
            return State.IdleBetween; // still in idle state

        if (Svc.Condition[ConditionFlag.Crafting40])
            return State.WaitStart; // craft started, but we don't yet know details

        // exit crafting menu
        return State.IdleNormal;
    }

    private static State TransitionFromWaitStart()
    {
        var quickSynth = GetQuickSynthAddon(); // TODO: consider updating quicksynth state to 0/max in CEH update hook and checking that here instead
        if (quickSynth != null)
            return State.QuickCraft; // we've actually started quick synth

        if (Svc.Condition[ConditionFlag.Crafting40])
            return State.WaitStart; // still waiting

        if (CurRecipe == null)
            return State.InvalidState; // failed to find recipe, bail out...

        // note: addon is normally available on the same frame transition ends
        var synthWindow = GetAddon();
        if (synthWindow == null)
        {
            Svc.Log.Error($"Unexpected addon state when craft should've been started");
            return State.WaitStart; // try again next frame
        }

        var canHQ = CurRecipe.CanHq;
        CurCraft = BuildCraftStateForRecipe(CharacterStats.GetCurrentStats(), CharacterInfo.JobID, CurRecipe);
        CurStep = BuildStepState(synthWindow, Skills.None, false);
        if (CurStep.Index != 1 || CurStep.Condition != Condition.Normal || CurStep.PrevComboAction != Skills.None)
            Svc.Log.Error($"Unexpected initial state: {CurStep}");

        IsTrial = synthWindow->AtkUnitBase.AtkValues[1] is { Type: FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool, Byte: 1 };
        CraftStarted?.Invoke(CurRecipe, CurCraft, CurStep, IsTrial);
        return State.InProgress;
    }

    private static State TransitionFromInProgress()
    {
        if (!Svc.Condition[ConditionFlag.Crafting40])
            return State.InProgress; // when either action is executed or craft is cancelled, this condition flag will be set
        _predictedNextStep = null; // just in case, ensure it's cleared
        _predictionDeadline = default;
        return State.WaitAction;
    }

    private static State TransitionFromWaitAction()
    {
        var synthWindow = GetAddon();
        if (synthWindow == null)
        {
            // craft was aborted
            CraftFinished?.Invoke(CurRecipe!, CurCraft!, CurStep!, true);
            return State.WaitFinish;
        }

        if (_predictedNextStep == null)
            return State.WaitAction; // continue waiting for transition

        if (_predictedNextStep.Progress >= CurCraft!.CraftProgress || _predictedNextStep.Durability <= 0)
        {
            // craft was finished, we won't get any status updates, so just wrap up
            CurStep = BuildStepState(synthWindow, _predictedNextStep.PrevComboAction, _predictedNextStep.PrevActionFailed);
            _predictedNextStep = null;
            _predictionDeadline = default;
            CraftFinished?.Invoke(CurRecipe!, CurCraft, CurStep, false);
            return State.WaitFinish;
        }
        else
        {
            // action was executed, but we might not have correct statuses yet
            var step = BuildStepState(synthWindow, _predictedNextStep.PrevComboAction, _predictedNextStep.PrevActionFailed);
            if (step != _predictedNextStep)
            {
                if (DateTime.Now <= _predictionDeadline)
                {
                    Svc.Log.Debug("Waiting for status update...");
                    return State.WaitAction; // wait for a bit...
                }
                // ok, we've been waiting too long - complain and consider current state to be correct
                Svc.Log.Error($"Unexpected status update - probably a simulator bug:\n     had {CurStep}\nexpected {_predictedNextStep}\n     got {step}");
            }
            CurStep = step;
            _predictedNextStep = null;
            _predictionDeadline = default;
            CraftAdvanced?.Invoke(CurRecipe!, CurCraft, CurStep);
            return State.InProgress;
        }
    }

    private static State TransitionFromWaitFinish()
    {
        if (Svc.Condition[ConditionFlag.Crafting40])
            return State.WaitFinish; // transition still in progress

        Svc.Log.Debug($"Resetting");
        _predictedNextStep = null;
        _predictionDeadline = default;
        CurRecipe = null;
        CurCraft = null;
        CurStep = null;
        IsTrial = false;
        return Svc.Condition[ConditionFlag.PreparingToCraft] ? State.IdleBetween : State.IdleNormal;
    }

    private static State TransitionFromQuickCraft()
    {
        if (Svc.Condition[ConditionFlag.PreparingToCraft])
        {
            UpdateQuickSynthState((0, 0));
            CurRecipe = null;
            return State.IdleBetween; // exit quick-craft menu
        }
        else
        {
            var quickSynth = GetQuickSynthAddon();
            UpdateQuickSynthState(quickSynth != null ? GetQuickSynthState(quickSynth) : (0, 0));
            return State.QuickCraft;
        }
    }

    private static AddonSynthesis* GetAddon()
    {
        var synthWindow = (AddonSynthesis*)Svc.GameGui.GetAddonByName("Synthesis");
        if (synthWindow == null)
            return null; // not ready

        if (synthWindow->AtkUnitBase.AtkValuesCount < 26)
        {
            Svc.Log.Error($"Unexpected addon state: 0x{(nint)synthWindow:X} {synthWindow->AtkUnitBase.AtkValuesCount} {synthWindow->AtkUnitBase.UldManager.NodeListCount})");
            return null;
        }

        return synthWindow;
    }

    private static AtkUnitBase* GetQuickSynthAddon()
    {
        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SynthesisSimple");
        if (addon == null)
            return null;

        if (addon->AtkValuesCount < 9)
        {
            Svc.Log.Error($"Unexpected quicksynth addon state: 0x{(nint)addon:X} {addon->AtkValuesCount} {addon->UldManager.NodeListCount})");
            return null;
        }

        return addon;
    }

    private static (int cur, int max) GetQuickSynthState(AtkUnitBase* quickSynthWindow)
    {
        var cur = quickSynthWindow->AtkValues[3].Int;
        var max = quickSynthWindow->AtkValues[4].Int;
        //var succeededNQ = quickSynthWindow->AtkValues[5].Int;
        //var succeededHQ = quickSynthWindow->AtkValues[8].Int;
        //var failed = quickSynthWindow->AtkValues[6].Int;
        //var itemId = quickSynthWindow->AtkValues[7].UInt;
        return (cur, max);
    }

    private static void UpdateQuickSynthState((int cur, int max) state)
    {
        if (QuickSynthState == state)
            return;
        QuickSynthState = state;
        Svc.Log.Debug($"Quick-synth progress update: {QuickSynthState}");
        QuickSynthProgress?.Invoke(QuickSynthState.Cur, QuickSynthState.Max);
    }

    private static int GetStepIndex(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[15].Int;
    private static int GetStepProgress(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[5].Int;
    private static int GetStepQuality(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[9].Int;
    private static int GetStepDurability(AddonSynthesis* synthWindow) => synthWindow->AtkUnitBase.AtkValues[7].Int;
    private static Condition GetStepCondition(AddonSynthesis* synthWindow) => (Condition)synthWindow->AtkUnitBase.AtkValues[12].Int;

    private static StepState BuildStepState(AddonSynthesis* synthWindow, Skills prevAction, bool prevActionFailed) => new ()
    {
        Index = GetStepIndex(synthWindow),
        Progress = GetStepProgress(synthWindow),
        Quality = GetStepQuality(synthWindow),
        Durability = GetStepDurability(synthWindow),
        RemainingCP = (int)CharacterInfo.CurrentCP,
        Condition = GetStepCondition(synthWindow),
        IQStacks = GetStatus(Buffs.InnerQuiet)?.Param ?? 0,
        WasteNotLeft = GetStatus(Buffs.WasteNot2)?.Param ?? GetStatus(Buffs.WasteNot)?.Param ?? 0,
        ManipulationLeft = GetStatus(Buffs.Manipulation)?.Param ?? 0,
        GreatStridesLeft = GetStatus(Buffs.GreatStrides)?.Param ?? 0,
        InnovationLeft = GetStatus(Buffs.Innovation)?.Param ?? 0,
        VenerationLeft = GetStatus(Buffs.Veneration)?.Param ?? 0,
        MuscleMemoryLeft = GetStatus(Buffs.MuscleMemory)?.Param ?? 0,
        FinalAppraisalLeft = GetStatus(Buffs.FinalAppraisal)?.Param ?? 0,
        CarefulObservationLeft = P.Config.UseSpecialist && ActionManagerEx.CanUseSkill(Skills.CarefulObservation) ? 1 : 0,
        HeartAndSoulActive = GetStatus(Buffs.HeartAndSoul) != null,
        HeartAndSoulAvailable = P.Config.UseSpecialist && ActionManagerEx.CanUseSkill(Skills.HeartAndSoul),
        PrevActionFailed = prevActionFailed,
        PrevComboAction = prevAction,
    };

    private static Dalamud.Game.ClientState.Statuses.Status? GetStatus(uint statusID) => Svc.ClientState.LocalPlayer?.StatusList.FirstOrDefault(s => s.StatusId == statusID);

    private static void CraftingEventHandlerUpdateDetour(CraftingEventHandler* self, nint a2, nint a3, CraftingEventHandler.OperationId* payload)
    {
        Svc.Log.Verbose($"CEH hook: {*payload}");
        switch (*payload)
        {
            case CraftingEventHandler.OperationId.StartPrepare:
                // this is sent immediately upon starting (quick) synth and does nothing interesting other than resetting the state
                // transition (Crafting40) is set slightly earlier by client when initiating the craft
                // the actual crafting states (setting Crafting and clearing PreparingToCraft) set in response to this message
                if (CurState is not State.WaitStart and not State.IdleBetween)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                break;
            case CraftingEventHandler.OperationId.StartInfo:
                // this is sent few 100s of ms after StartPrepare for normal synth and contains details of the recipe
                // client stores the information in payload in event handler, but we continue waiting
                if (CurState != State.WaitStart)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                var startPayload = (CraftingEventHandler.StartInfo*)payload;
                Svc.Log.Debug($"Starting craft: recipe #{startPayload->RecipeId}, initial quality {startPayload->StartingQuality}, u8={startPayload->u8}");
                if (CurRecipe != null)
                    Svc.Log.Error($"Unexpected non-null recipe when receiving {*payload} message");
                CurRecipe = Svc.Data.GetExcelSheet<Lumina.Excel.GeneratedSheets.Recipe>()?.GetRow(startPayload->RecipeId);
                if (CurRecipe == null)
                    Svc.Log.Error($"Failed to find recipe #{startPayload->RecipeId}");
                // note: we could build CurCraft and CurStep here
                break;
            case CraftingEventHandler.OperationId.StartReady:
                // this is sent few 100s of ms after StartInfo for normal synth and instructs client to start synth session - set up addon, etc
                // transition (Crafting40) will be cleared in a few frames
                if (CurState != State.WaitStart)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                break;
            case CraftingEventHandler.OperationId.Finish:
                // this is sent few seconds after last action that completed the craft or quick synth and instructs client to exit the finish transition
                // transition (Crafting40) is cleared in response to this message
                if (CurState is not State.WaitFinish and not State.IdleBetween)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                break;
            case CraftingEventHandler.OperationId.Abort:
                // this is sent immediately upon aborting synth
                // transition (Crafting40) is set slightly earlier by client when aborting the craft
                // actual craft state (Crafting) is cleared several seconds later
                // currently we rely on addon disappearing to detect aborts (for robustness), it can happen either before or after Abort message
                if (CurState is not State.WaitAction and not State.WaitFinish and not State.IdleBetween)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                if (_predictedNextStep != null)
                    Svc.Log.Error($"Unexpected non-null predicted-next when receiving {*payload} message");
                break;
            case CraftingEventHandler.OperationId.AdvanceCraftAction:
            case CraftingEventHandler.OperationId.AdvanceNormalAction:
                // this is sent a few seconds after using an action and contains action result
                // in response to this action, client updates the addon data, prints log message and clears consumed statuses (mume, gs, etc)
                // transition (Crafting40) will be cleared in a few frames, if this action did not complete the craft
                // if there are any status changes (e.g. remaining step updates) and if craft is not complete, these will be updated by the next StatusEffectList packet, which might arrive with a delay
                // because of that, we wait until statuses match prediction (or too much time passes) before transitioning to InProgress
                if (CurState != State.WaitAction)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                if (_predictedNextStep != null)
                {
                    Svc.Log.Error($"Unexpected non-null predicted-next when receiving {*payload} message");
                    _predictedNextStep = null;
                }
                var advancePayload = (CraftingEventHandler.AdvanceStep*)payload;
                bool complete = advancePayload->Flags.HasFlag(CraftingEventHandler.StepFlags.CompleteSuccess) || advancePayload->Flags.HasFlag(CraftingEventHandler.StepFlags.CompleteFail);
                _predictedNextStep = Simulator.Execute(CurCraft!, CurStep!, SkillActionMap.ActionToSkill(advancePayload->LastActionId), advancePayload->Flags.HasFlag(CraftingEventHandler.StepFlags.LastActionSucceeded) ? 0 : 1, 1).Item2;
                _predictedNextStep.Condition = (Condition)(advancePayload->ConditionPlus1 - 1);
                // fix up predicted state to match what game sends
                if (complete)
                    _predictedNextStep.Index = CurStep.Index; // step is not advanced for final actions
                _predictedNextStep.Progress = Math.Min(_predictedNextStep.Progress, CurCraft.CraftProgress);
                _predictedNextStep.Quality = Math.Min(_predictedNextStep.Quality, CurCraft.CraftQualityMax);
                _predictedNextStep.Durability = Math.Max(_predictedNextStep.Durability, 0);
                // validate sim predictions
                if (_predictedNextStep.Index != advancePayload->StepIndex)
                    Svc.Log.Error($"Prediction error: expected step #{advancePayload->StepIndex}, got {_predictedNextStep.Index}");
                if (_predictedNextStep.Progress != advancePayload->CurProgress)
                    Svc.Log.Error($"Prediction error: expected progress {advancePayload->CurProgress}, got {_predictedNextStep.Progress}");
                if (_predictedNextStep.Quality != advancePayload->CurQuality)
                    Svc.Log.Error($"Prediction error: expected quality {advancePayload->CurQuality}, got {_predictedNextStep.Quality}");
                if (_predictedNextStep.Durability != advancePayload->CurDurability)
                    Svc.Log.Error($"Prediction error: expected durability {advancePayload->CurDurability}, got {_predictedNextStep.Durability}");
                var predictedDeltaProgress = _predictedNextStep.PrevActionFailed ? 0 : Simulator.CalculateProgress(CurCraft!, CurStep!, _predictedNextStep.PrevComboAction);
                var predictedDeltaQuality = _predictedNextStep.PrevActionFailed ? 0 : Simulator.CalculateQuality(CurCraft!, CurStep!, _predictedNextStep.PrevComboAction);
                var predictedDeltaDurability = _predictedNextStep.PrevComboAction == Skills.MastersMend ? 30 : -Simulator.GetDurabilityCost(CurStep!, _predictedNextStep.PrevComboAction);
                if (predictedDeltaProgress != advancePayload->DeltaProgress)
                    Svc.Log.Error($"Prediction error: expected progress delta {advancePayload->DeltaProgress}, got {predictedDeltaProgress}");
                if (predictedDeltaQuality != advancePayload->DeltaQuality)
                    Svc.Log.Error($"Prediction error: expected quality delta {advancePayload->DeltaQuality}, got {predictedDeltaQuality}");
                if (predictedDeltaDurability != advancePayload->DeltaDurability)
                    Svc.Log.Error($"Prediction error: expected durability delta {advancePayload->DeltaDurability}, got {predictedDeltaDurability}");
                if ((_predictedNextStep.Progress >= CurCraft!.CraftProgress || _predictedNextStep.Durability <= 0) != complete)
                    Svc.Log.Error($"Prediction error: unexpected completion state diff (got {complete})");
                _predictionDeadline = DateTime.Now.AddSeconds(0.5f); // if we don't get status effect list quickly enough, bail out...
                break;
            case CraftingEventHandler.OperationId.QuickSynthStart:
                // this is sent a few seconds after StartPrepare for quick synth and contains details of the recipe
                // client stores the information in payload in event handler and opens the addon
                if (CurState != State.WaitStart)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                var quickSynthPayload = (CraftingEventHandler.QuickSynthStart*)payload;
                Svc.Log.Debug($"Starting quicksynth: recipe #{quickSynthPayload->RecipeId}, count {quickSynthPayload->MaxCount}");
                if (CurRecipe != null)
                    Svc.Log.Error($"Unexpected non-null recipe when receiving {*payload} message");
                CurRecipe = Svc.Data.GetExcelSheet<Lumina.Excel.GeneratedSheets.Recipe>()?.GetRow(quickSynthPayload->RecipeId);
                if (CurRecipe == null)
                    Svc.Log.Error($"Failed to find recipe #{quickSynthPayload->RecipeId}");
                break;
            case CraftingEventHandler.OperationId.QuickSynthProgress:
                // this is sent a ~second after ActorControl that contains the actual new counts
                if (CurState != State.QuickCraft)
                    Svc.Log.Error($"Unexpected state {CurState} when receiving {*payload} message");
                break;
        }
        _craftingEventHandlerUpdateHook.Original(self, a2, a3, payload);
        Svc.Log.Verbose("CEH hook exit");
    }
}
