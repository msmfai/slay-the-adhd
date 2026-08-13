using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace PokaYokeHarness;

/// <summary>
/// Test-only driver. Makes the game pilot itself, headless and SILENT, so the Poka-Yoke
/// patches actually execute in real combat and we can assert (from the log) which features
/// fire. Strategy, mirroring the RL bridge mod's proven seam:
///   1. TestMode.IsOn = true  -> every NAudioManager Play* call is gated on !TestMode.IsOn,
///      so this guarantees total silence ("can't hear it") regardless of volume.
///   2. Patch NGame.IsReleaseGame() -> false, which unlocks the built-in AutoSlay autoplayer
///      (dead code in the Steam build otherwise).
///   3. Speed patches so a full run finishes fast.
///   4. Wait for the main menu, then run the game's own AutoSlayer(seed) — it self-navigates
///      main menu -> character select -> the whole run.
/// The window is suppressed by --headless and the dock icon by LSUIElement (set by the
/// launch script), so it's also invisible ("can't see it").
/// </summary>
[ModInitializer(nameof(Init))]
public class Plugin
{
    public const string ModId = "PokaYokeHarness";

    public static void Init()
    {
        Log.Info("[Harness] === init ===");
        var harmony = new Harmony(ModId);
        harmony.PatchAll(typeof(Plugin).Assembly);

        // MUTE without TestMode. TestMode.IsOn silences audio BUT also flips
        // CombatStateTracker into an assertion mode that throws "Backend should not be
        // subscribing to CombatStateChanged!" the moment any other mod (e.g. RitsuLib)
        // subscribes during combat setup — a real incompatibility. So instead of TestMode,
        // skip the audio Play* methods directly (mutes FMOD too), leaving combat untouched.
        int muted = 0;
        foreach (var m in typeof(NAudioManager).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            if (m.Name is "PlayOneShot" or "PlayLoop" or "PlayMusic")
            {
                try { harmony.Patch(m, prefix: new HarmonyMethod(typeof(MuteAudioPatch).GetMethod(nameof(MuteAudioPatch.Prefix)))); muted++; } catch { }
            }
        Log.Info($"[Harness] muted {muted} audio methods (no TestMode; combat behaviour untouched)");
        Log.Info($"[Harness] patched: {string.Join(", ", System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(harmony.GetPatchedMethods(), m => m.DeclaringType?.Name + "." + m.Name)))}");

        TaskHelper.RunSafely(DriveAsync());
    }

    private static async Task DriveAsync()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        Log.Info("[Harness] waiting for NGame.Instance...");
        await WaitHelper.Until(() => NGame.Instance != null, cts.Token, TimeSpan.FromSeconds(60), "NGame.Instance not available");

        // Wait for the main menu to be VISIBLE — not to navigate it (that crashes headless),
        // but because it's the reliable signal that init finished: profile initialized and
        // the model/card database (ModelDb) fully loaded, both of which the direct start needs.
        Node root = ((SceneTree)Engine.GetMainLoop()).Root;
        await WaitHelper.Until(
            () => root.GetNodeOrNull<Control>("/root/Game/RootSceneContainer/MainMenu")?.IsVisibleInTree() ?? false,
            cts.Token, TimeSpan.FromSeconds(60), "Main menu not visible");
        await WaitHelper.Until(() => RunManager.Instance != null, cts.Token, TimeSpan.FromSeconds(30), "RunManager not available");

        // Clear the (user-authorised disposable) in-progress run so nothing conflicts.
        try
        {
            if (SaveManager.Instance.HasRunSave) { SaveManager.Instance.DeleteCurrentRun(); Log.Info("[Harness] deleted disposable in-progress run"); }
        }
        catch (Exception e) { Log.Info($"[Harness] delete-run skipped: {(e.InnerException ?? e).Message}"); }

        // Start a run DIRECTLY (bypassing the main menu, whose submenu navigation crashes
        // headless), replicating NSceneBootstrapper.StartNewRun with concrete values.
        string seed = System.Environment.GetEnvironmentVariable("STS2_SEED") ?? SeedHelper.GetRandomSeed();
        try { await DirectStartRunAsync(seed); }
        catch (Exception e) { Log.Info($"[Harness] direct-start FAILED: {(e.InnerException ?? e)}"); return; }

        TaskHelper.RunSafely(ExerciseFeaturesInLiveGameAsync(cts.Token));
    }

    /// Replicates the game's debug NSceneBootstrapper.StartNewRun to drop straight into a
    /// combat, no main menu involved.
    private static async Task DirectStartRunAsync(string seed)
    {
        // Models are canonical singletons — fetch from ModelDb, never construct them.
        var character = ModelDb.AllCharacters.First(c => c.GetType().Name == "Ironclad");
        Log.Info($"[Harness] step: character={character?.GetType().Name ?? "NULL"}");
        var unlock = SaveManager.Instance.GenerateUnlockStateFromProgress();
        Log.Info($"[Harness] step: unlockState={(unlock == null ? "NULL" : "ok")}");
        var player = Player.CreateForNewRun(character, unlock, 1uL);
        Log.Info($"[Harness] step: player={(player == null ? "NULL" : "ok")}");
        var defaults = ActModel.GetDefaultList();
        Log.Info($"[Harness] step: defaultActs={(defaults == null ? "NULL" : defaults.Count.ToString())}");
        var acts = defaults.Select(a => a.ToMutable()).ToList();
        var runState = RunState.CreateForNewRun(new[] { player }, acts, new List<ModifierModel>(), GameMode.Standard, 0, seed);
        Log.Info($"[Harness] step: runState={(runState == null ? "NULL" : "ok")}");

        RunManager.Instance.SetUpNewSingleplayer(runState, false);
        Log.Info("[Harness] step: SetUpNewSingleplayer ok");
        // Load the run's assets so the combat UI scene can actually instantiate headless
        // (skipping this left the UI nodes our features hook uncreated).
        await MegaCrit.Sts2.Core.Assets.PreloadManager.LoadRunAssets(new[] { character });
        Log.Info("[Harness] step: LoadRunAssets ok");
        RunManager.Instance.Launch();
        Log.Info("[Harness] step: Launch ok");
        var rsc = NGame.Instance.RootSceneContainer;
        Log.Info($"[Harness] step: RootSceneContainer={(rsc == null ? "NULL" : "ok")}");
        // NRun.Create casts run.tscn's root to NRun via PreloadManager.Cache; in the full mod
        // stack RitsuLib's reflection-cache clearing can unbind the C# script so the root
        // comes back as a bare Control. Load the scene FRESH to dodge the stale cache.
        NRun nrun;
        try { nrun = NRun.Create(runState); }
        catch (System.InvalidCastException)
        {
            var scene = GD.Load<PackedScene>("res://scenes/run.tscn");
            var node = scene.Instantiate(PackedScene.GenEditState.Disabled);
            nrun = node as NRun ?? throw new InvalidOperationException(
                $"run.tscn root is {node.GetType().Name}, not NRun — another mod unbound the scene's C# script (harness can't pilot combat in this stack).");
            Traverse.Create(nrun).Field("_state").SetValue(runState);
            Log.Info("[Harness] NRun rebound via fresh scene load");
        }
        rsc.SetCurrentScene(nrun);
        Log.Info($"[Harness] direct run started (seed={seed}); entering act 0");
        // Mirror NSceneBootstrapper.StartNewRun's setup that must precede room entry.
        await RunManager.Instance.SetActInternal(0);
        RunManager.Instance.RunLocationTargetedBuffer.OnLocationChanged(runState.RunLocation);
        RunManager.Instance.MapSelectionSynchronizer.OnLocationChanged(runState.MapLocation);
        // Enter a combat DIRECTLY via the debug room entry (bypasses map navigation, which
        // needs UI nodes not built headless). RoomType.Monster = a normal combat.
        // Pick an encounter whose enemy actually ATTACKS turn 1 (the first one, BygoneEffigy,
        // opens asleep). STS2_ENC overrides by 0-based index.
        var encList = MegaCrit.Sts2.Core.Models.ModelDb.AllEncounters.ToList();
        var encEnv = System.Environment.GetEnvironmentVariable("STS2_ENC");
        var encounter = (encEnv != null && int.TryParse(encEnv, out var ei) && ei < encList.Count)
            ? encList[ei]
            : (encList.FirstOrDefault(e => !e.GetType().Name.Contains("Effigy") && !e.GetType().Name.Contains("Sleep")) ?? encList.First());
        Log.Info($"[Harness] entering combat via EnterRoomDebug (encounter={encounter.GetType().Name})");
        await RunManager.Instance.EnterRoomDebug(
            MegaCrit.Sts2.Core.Rooms.RoomType.Elite,   // Elite so guard 2's room check can pass
            MegaCrit.Sts2.Core.Map.MapPointType.Unassigned,
            encounter.ToMutable());
        RunManager.Instance.ActionExecutor.Unpause();
        Log.Info("[Harness] entered act 0 — combat should be live");
    }

    /// The combat UI scene never auto-instantiates headless, but the Godot runtime IS live,
    /// so we construct the specific nodes each feature hooks, inject the real live combat
    /// state, and invoke the hooked methods directly — i.e. drive the actual patched code
    /// paths. Each step is isolated so one failure can't stop the others; every outcome is
    /// logged so the drive report can show exactly which features fired.
    private static System.Reflection.Assembly _mod =>
        System.Array.Find(System.AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "PokaYokeSpire")!;

    /// Invoke a private static Prefix/Postfix (or manager method) on a PokaYokeSpire patch
    /// class directly — i.e. run the actual patched code path with the args we supply.
    private static void InvokePatch(string typeName, string method, params object?[] args)
    {
        try
        {
            var m = _mod.GetType(typeName)?.GetMethod(method,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (m == null) { Log.Info($"[Harness] {typeName}.{method} not found"); return; }
            m.Invoke(null, args);
        }
        catch (Exception e) { Log.Info($"[Harness] {typeName}.{method} threw: {(e.InnerException ?? e).Message}"); }
    }

    private static T? MakeNode<T>() where T : Godot.Node
    {
        try { return (T)System.Activator.CreateInstance(typeof(T))!; }
        catch (Exception e) { Log.Info($"[Harness] new {typeof(T).Name} failed: {(e.InnerException ?? e).Message}"); return null; }
    }

    private static async Task ExerciseFeaturesInLiveGameAsync(CancellationToken ct)
    {
        await Task.Delay(2500, ct); // let the combat model settle
        var root = ((SceneTree)Engine.GetMainLoop()).Root;

        var combat = MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState();
        var player = MegaCrit.Sts2.Core.Context.LocalContext.GetMe((MegaCrit.Sts2.Core.Combat.ICombatState)combat);
        Log.Info($"[Harness] live combat: state={(combat == null ? "NULL" : "ok")} player={(player == null ? "NULL" : "ok")}");
        try
        {
            foreach (var en in combat.Enemies)
            {
                var mv = en.Monster?.NextMove;
                var kinds = mv == null ? "no-move" : string.Join("+", System.Linq.Enumerable.Select(mv.Intents, i => i.GetType().Name));
                Log.Info($"[Harness] enemy {en.Monster?.GetType().Name}: move={mv?.Id ?? "null"} intents=[{kinds}]");
            }
        }
        catch (Exception e) { Log.Info($"[Harness] enemy-intent probe failed: {(e.InnerException ?? e).Message}"); }

        // A real relic (from the live player) for features 5 & 6 — never construct models.
        var relic = player?.Relics.FirstOrDefault();

        // FEATURE 4 — invoke the energy-counter postfix directly on a constructed node.
        var ec = MakeNode<MegaCrit.Sts2.Core.Nodes.Combat.NEnergyCounter>();
        if (ec != null) { Traverse.Create(ec).Field("_combatState").SetValue(combat); root.AddChild(ec); InvokePatch("PokaYokeSpire.Features.EnergyCounterFeature", "Postfix", ec); }

        // FEATURES 5 & 6 — invoke the radial-relic prefix + the blue-counter manager toggle.
        if (relic != null)
        {
            InvokePatch("PokaYokeSpire.Features.RadialRelicsFeature", "Prefix", relic);
            InvokePatch("PokaYokeSpire.Features.BlueCounterManager", "Toggle", relic);
        }
        else Log.Info("[Harness] no relic on player — skip 5/6");

        // GUARDS 1 & 2 — invoke the guard prefix/postfix directly on a constructed button with
        // the real combat state injected (the original AnimIn/OnRelease NRE headless).
        var btn = MakeNode<MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton>();
        if (btn != null)
        {
            Traverse.Create(btn).Field("_combatState").SetValue(combat);
            root.AddChild(btn);
            // Guard 1 first, on the clean (potion-free) state it reads.
            InvokePatch("PokaYokeSpire.Guards.EndTurnEnergyGuard", "Prefix", btn);    // guard 1
            // Now give the player a potion so guard 2's condition is satisfiable.
            try
            {
                var slots = Traverse.Create(player).Field("_potionSlots").GetValue<System.Collections.IList>();
                if (slots != null && slots.Count > 0) { slots[0] = MegaCrit.Sts2.Core.Models.ModelDb.AllPotions.First(); Log.Info("[Harness] gave player a potion"); }
            }
            catch (Exception e) { Log.Info($"[Harness] potion inject skipped: {(e.InnerException ?? e).Message}"); }
            InvokePatch("PokaYokeSpire.Guards.ElitePotionGuard", "Postfix", btn);     // guard 2
            // FEATURE 7 — end-turn damage readout: invoke on the same button + real combat.
            InvokePatch("PokaYokeSpire.Features.EndTurnDamageFeature", "Postfix", btn, combat);
        }

        // GUARD 3 — invoke the deck-check prefix on a constructed reward screen (cardHolder
        // only used in the OK callback, so null is fine to trigger the nudge path).
        var screen = MakeNode<MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen>();
        if (screen != null)
        {
            root.AddChild(screen);
            InvokePatch("PokaYokeSpire.Guards.DeckCheck_SelectCard", "Prefix", screen, null);
        }

        // FEATURE 8 — card target preview: compute a real card's effect on a live enemy, then
        // drive the overlay's Show so the render path + marker run.
        try
        {
            // Undo the guard-2 potion injection — a raw canonical potion in a slot corrupts
            // the damage hook (this is a harness artifact, not a mod issue).
            try { var s = Traverse.Create(player).Field("_potionSlots").GetValue<System.Collections.IList>(); for (int si = 0; si < s.Count; si++) s[si] = null; } catch { }

            var enemy0 = combat.Enemies.FirstOrDefault();
            var hand = player?.PlayerCombatState?.Hand?.Cards?.ToList();
            if (hand != null && hand.Count > 0 && enemy0 != null)
            {
                var previewT = _mod.GetType("PokaYokeSpire.Combat.CardTargetPreview")!;
                var computeM = previewT.GetMethod("Compute")!;
                var dmgProp = previewT.GetNestedType("Preview")!.GetProperty("Damage")!;
                MegaCrit.Sts2.Core.Models.CardModel? attackCard = null;
                foreach (var c in hand)
                {
                    var pv = computeM.Invoke(null, new object[] { c, enemy0 });
                    int dmg = (int)dmgProp.GetValue(pv)!;
                    Log.Info($"[Harness] card-preview: {c.GetType().Name} vs {enemy0.Monster?.GetType().Name} -> dmg={dmg}");
                    if (dmg > 0 && attackCard == null) attackCard = c;
                }
                // exercise the render path (builds the MegaRichTextLabel overlay) via SetTarget
                var setTargetM = _mod.GetType("PokaYokeSpire.Features.CardPreviewOverlay")!
                    .GetMethod("SetTarget", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                setTargetM!.Invoke(null, new object[] { attackCard ?? hand[0], enemy0 });
            }
            else Log.Info("[Harness] card-preview: no hand card / enemy");
        }
        catch (Exception e) { Log.Info($"[Harness] card-preview FAILED: {(e.InnerException ?? e).Message}"); }

        // FEATURE 9 — lethal gem glow: opt-in + drop the enemy's HP so the starting hand is
        // lethal, cache state, invoke the _Process postfix; expect the feature9 LETHAL marker.
        try
        {
            _mod.GetType("PokaYokeSpire.Config")!.GetProperty("LethalGemGlow")!.SetValue(null, true);
            var enemyL = combat.Enemies.FirstOrDefault(e => e.CurrentHp > 0);
            if (enemyL != null && ec != null)
            {
                try { Traverse.Create(enemyL).Property("CurrentHp").SetValue(3); }
                catch { Traverse.Create(enemyL).Field("<CurrentHp>k__BackingField").SetValue(3); }
                Log.Info($"[Harness] lethal probe: enemy hp now {enemyL.CurrentHp}, side={combat.CurrentSide}");
                // Invoke the state hook — it snapshots + solves OFF-THREAD + logs feature9 async.
                InvokePatch("PokaYokeSpire.Features.LethalGemGlowState", "Postfix", combat);
                System.Threading.Thread.Sleep(400);   // let the background solve finish + log
            }
        }
        catch (Exception e) { Log.Info($"[Harness] lethal probe FAILED: {(e.InnerException ?? e).Message}"); }

        Log.Info("[Harness] feature exercise complete");
    }
}

/// Mute: skip audio playback (patched onto NAudioManager Play* — mutes FMOD too) without
/// using TestMode, so combat behaviour is untouched and other mods stay compatible.
public static class MuteAudioPatch
{
    public static bool Prefix() => false;
}

/// Unlocks AutoSlay + other dev features by forcing the release check false.
[HarmonyPatch(typeof(NGame), nameof(NGame.IsReleaseGame))]
public static class IsReleaseGamePatch
{
    private static bool Prefix(ref bool __result) { __result = false; return false; }
}

/// Cut all timed waits to 10% so a full run finishes fast.
[HarmonyPatch(typeof(Cmd), nameof(Cmd.CustomScaledWait))]
public static class WaitSpeedPatch
{
    private static void Prefix(ref float fastSeconds, ref float standardSeconds)
    {
        fastSeconds *= 0.1f; standardSeconds *= 0.1f;
    }
}

/// The map SCREEN drawing needs UI nodes that aren't built headless (NREs). Skip the draw
/// — the map DATA is still generated by GenerateMap, which is all EnterAct needs.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen), "SetMap")]
public static class SkipMapDrawPatch
{
    private static bool Prefix() => false;
}
