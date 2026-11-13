using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MultiplayerPeeking;

namespace MultiplayerPeekingTests;

// Loaded ONLY in the launcher's disposable app. All combat actions use the real
// game's ENet/action queues. Files coordinate test phases, never game state.
[ModInitializer(nameof(Init))]
public static class Probe
{
    private static SceneTree Tree = null!;
    private static string Role = "", Output = "";
    private static bool Started;
    private static readonly List<string> Checks = [];
    private static readonly Dictionary<string, object> Snapshots = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Init()
    {
        Role = Argument("peeking-test-role");
        Output = Argument("peeking-test-output");
        if (Role is not ("host" or "client") || string.IsNullOrEmpty(Output))
            throw new InvalidOperationException("Test probe requires the isolated run.py launcher.");
        Directory.CreateDirectory(Output);
        Tree = (SceneTree)Engine.GetMainLoop();
        var harmony = new Harmony("MultiplayerPeekingTests");
        harmony.Patch(AccessTools.Method(typeof(BootstrapSettingsUtil), "Get"),
            prefix: new HarmonyMethod(typeof(Probe), nameof(BootstrapPrefix)));
        harmony.Patch(AccessTools.Method(typeof(NMainMenu), "CheckCommandLineArgs"),
            prefix: new HarmonyMethod(typeof(Probe), nameof(FastMpPrefix)));
        // Mutation control: emulate the old combat-screen-only visibility gate.
        if (Argument("peeking-test-disable-peek-patch") == "true")
            harmony.Patch(AccessTools.Method(typeof(PeekHandPatch), "IsPeekingSelectionScreen"),
                prefix: new HarmonyMethod(typeof(Probe), nameof(DisablePeekPrefix)));
        Tree.ProcessFrame += OnFrame;
        GD.Print($"[PEEK-TEST] {Role}: probe loaded");
    }

    private static string Argument(string key) => OS.GetCmdlineArgs()
        .FirstOrDefault(a => a.StartsWith("--" + key + "="))?.Split('=', 2)[1] ?? "";
    private static bool BootstrapPrefix(ref Type? __result) { __result = typeof(Fixture); return false; }
    private static bool DisablePeekPrefix(ref bool __result) { __result = false; return false; }
    private static bool FastMpPrefix() => !Started;

    private static void OnFrame()
    {
        if (Started) return;
        try
        {
            if (ActiveScreenContext.Instance.GetCurrentScreen() is NCharacterSelectScreen screen
                && AccessTools.Field(typeof(NCharacterSelectScreen), "_lobby").GetValue(screen) is StartRunLobby lobby)
            {
                File.WriteAllText(Path.Combine(Output, Role + ".lobby"), "ready");
                if (lobby.Players.Count == 2 && !lobby.LocalPlayer.isReady)
                {
                    lobby.SetReady(true);
                }
            }
            if (NCombatRoom.Instance == null || !CombatManager.Instance.IsInProgress) return;
            var run = RunManager.Instance.DebugOnlyGetState();
            if (run == null || run.Players.Count != 2 || run.Players.Any(p => p.PlayerCombatState?.Hand.Cards.Count != 5
                || p.PlayerCombatState.Phase != PlayerTurnPhase.Play)) return;
            Started = true;
            Tree.ProcessFrame -= OnFrame;
            _ = RunTests(run);
        }
        catch (Exception e) { Started = true; Tree.ProcessFrame -= OnFrame; Finish(e); }
    }

    private static async Task RunTests(RunState run)
    {
        try
        {
            await Frames(10);
            var me = run.Players.Single(p => p.NetId == LocalContext.NetId);
            var remote = run.Players.Single(p => p != me);
            var states = NRun.Instance!.GlobalUi.MultiplayerPlayerContainer.GetChildren()
                .OfType<NMultiplayerPlayerState>().ToArray();
            var state = states.Single(s => s.Player == remote);
            Check("real ENet " + Role, RunManager.Instance.NetService.IsConnected
                && RunManager.Instance.NetService.GetType().Name == (Role == "host" ? "NetHostGameService" : "NetClientGameService"));
            PeekSettings.HideDelayMs = 150;
            PeekSettings.NotifyChanged();
            await Frames(4);
            Check("remote opening hand thumbnails", MatchesHand(Thumbnail(state), remote) && Thumbnail(state)!.IsVisibleInTree());
            Check("no local hand duplication", states.Where(s => s.Player == me).All(s => Thumbnail(s) == null));
            Check("warning card prismatic material", Count("_origHighlightMat") >= 3);
            Check("warning body tint", Count("_tintedBodies") == 1);
            Snapshot("opening", run);
            await Screenshot("01-thumbnails");

            state.OnCreatureHovered();
            await Frames(4);
            Check("hover opens matching hand", MatchesHand(Overlay(state), remote) && Overlay(state)!.IsVisibleInTree());
            Check("hover hides thumbnails", !Thumbnail(state)!.IsVisibleInTree());
            await Screenshot("02-hover");

            // Both real peers play a card. The remote UI must rebuild via ContentsChanged.
            await Barrier("play-defend");
            Check("enqueue local Defend", me.PlayerCombatState!.Hand.Cards.OfType<DefendIronclad>().Single().TryManualPlay(null));
            await Until(() => run.Players.All(p => p.PlayerCombatState!.Hand.Cards.Count == 4
                && p.PlayerCombatState.DiscardPile.Cards.OfType<DefendIronclad>().Any()), "both Defends resolved");
            await Frames(8);
            Check("live overlay tracks remote play", MatchesHand(Overlay(state), remote));
            Check("thumbnail tracks remote play", MatchesHand(Thumbnail(state), remote));
            Snapshot("after-play", run);
            await Screenshot("03-after-play");
            await Barrier("play-observed");
            state.OnCreatureUnhovered();
            await Delay(0.3);
            Check("hover exit restores thumbnail", Overlay(state) == null && Thumbnail(state)!.IsVisibleInTree());

            await SelectionScreens(state, me);
            await InputAndSettings(state);

            // Real enemy turn, damage/block, shuffle and next-turn draw on both machines.
            await Barrier("end-turn");
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, me.PlayerCombatState!.TurnNumber));
            await Until(() => run.Players.All(p => p.PlayerCombatState!.TurnNumber == 2
                && p.PlayerCombatState.Phase == PlayerTurnPhase.Play && p.PlayerCombatState.Hand.Cards.Count == 5), "second player turn", 35);
            await Frames(8);
            Check("next turn hand redraw", MatchesHand(Thumbnail(state), remote) && Thumbnail(state)!.IsVisibleInTree());
            Snapshot("turn-two", run);
            await Screenshot("04-turn-two");
            await Barrier("turn-two-observed");

            // Make a pending timer survive scene teardown; it must not touch freed nodes.
            // Longer than scene loading: prove the callback runs AFTER state destruction.
            PeekSettings.HideDelayMs = 5000;
            state.OnCreatureHovered();
            await Frames(2);
            state.OnCreatureUnhovered();
            Check("pending hide before teardown", Count("_hideTimers") == 1);
            var pendingTimer = (SceneTreeTimer)Dict("_hideTimers")[state]!;
            await Barrier("exit");
            await NGame.Instance!.ReturnToMainMenu();
            await Frames(2);
            Check("pending timer outlives removed state", pendingTimer.TimeLeft > 0
                && (!GodotObject.IsInstanceValid(state) || !state.IsInsideTree()));
            await Delay(Math.Max(0, pendingTimer.TimeLeft) + 0.2);
            foreach (var name in new[] { "_handHandlers", "_hideTimers", "_overlays", "_thumbnails",
                         "_origHighlightMat", "_tintedBodies", "_highlightedStates", "_overlayHovered" })
                Check("teardown clears " + name, Count(name) == 0);
            Finish(null);
        }
        catch (Exception e) { Finish(e); }
    }

    private static async Task SelectionScreens(NMultiplayerPlayerState state, Player me)
    {
        var hand = me.PlayerCombatState!.Hand;
        var grid = NCombatPileCardSelectScreen.Create(hand,
            new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 1) { Cancelable = true }, null);
        NOverlayStack.Instance!.Push(grid);
        await CheckSelection("grid", grid, state);
        var choice = NChooseACardSelectionScreen.ShowScreen(hand.Cards.Take(3).ToArray(), true)!;
        await CheckSelection("choose-card", choice, state);
        var bundles = NChooseABundleSelectionScreen.ShowScreen(new IReadOnlyList<CardModel>[]
            { hand.Cards.Take(2).ToArray(), hand.Cards.Skip(2).ToArray() }, true);
        await CheckSelection("choose-bundle", bundles, state);
    }

    private static async Task CheckSelection(string name, Control screen, NMultiplayerPlayerState state)
    {
        await Frames(6);
        Check(name + " hides thumbnails", !Thumbnail(state)!.IsVisibleInTree());
        var peek = screen.GetNode<NPeekButton>("%PeekButton");
        peek.SetPeeking(true);
        await Frames(4);
        Check(name + " peek restores thumbnails", Thumbnail(state)!.IsVisibleInTree());
        state.OnCreatureHovered();
        await Frames(4);
        Check(name + " peek permits hover", Overlay(state)?.IsVisibleInTree() == true);
        await Screenshot("peek-" + name);
        peek.SetPeeking(false);
        await Frames(4);
        Check(name + " closing peek hides overlay", Overlay(state) == null && !Thumbnail(state)!.IsVisibleInTree());
        state.OnCreatureUnhovered();
        NOverlayStack.Instance!.Remove((IOverlayScreen)screen);
        await Delay(0.3);
        Check(name + " close restores combat", Thumbnail(state)!.IsVisibleInTree());
    }

    private static async Task InputAndSettings(NMultiplayerPlayerState state)
    {
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true });
        await Frames(4);
        Check("middle hold hides thumbnails", !Thumbnail(state)!.IsVisibleInTree());
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = false });
        await Frames(4);
        Check("middle release restores thumbnails", Thumbnail(state)!.IsVisibleInTree());
        await Tap(Key.Minus);
        Check("minus toggles thumbnails off", !PeekSettings.ThumbnailsEnabled && Thumbnail(state) == null);
        await Tap(Key.Minus);
        Check("minus toggles thumbnails on", PeekSettings.ThumbnailsEnabled && Thumbnail(state)!.IsVisibleInTree());

        var materials = Dict("_origHighlightMat");
        var savedMaterials = materials.Keys.Cast<NCard>()
            .Select(card => (card, (Material)materials[card]!)).ToArray();
        PeekSettings.MasterEnabled = false;
        PeekSettings.NotifyChanged();
        await Frames(4);
        Check("master off removes previews", Count("_thumbnails") == 0 && Count("_overlays") == 0);
        Check("pooled card materials restored", Count("_origHighlightMat") == 0 && savedMaterials.All(pair =>
            !GodotObject.IsInstanceValid(pair.Item1) || pair.Item1.CardHighlight.Material == pair.Item2));
        Check("master off resets body tint", Count("_tintedBodies") == 0);
        PeekSettings.MasterEnabled = true;
        PeekSettings.NotifyChanged();
        await Frames(4);
        Check("master on rebuilds previews", Thumbnail(state)?.IsVisibleInTree() == true);

        PeekSettings.HideDelayMs = 300;
        state.OnCreatureHovered();
        state.OnCreatureUnhovered();
        await Delay(0.1);
        state.OnCreatureHovered();
        await Delay(0.35);
        Check("stale timer does not hide new hover", Overlay(state)?.IsVisibleInTree() == true);
        state.OnCreatureUnhovered();
        await Delay(0.4);
        Check("current timer hides overlay", Overlay(state) == null && Thumbnail(state)!.IsVisibleInTree());
    }

    private static async Task Tap(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, Pressed = true });
        await Frames(4);
        Input.ParseInputEvent(new InputEventKey { Keycode = key, Pressed = false });
        await Frames(4);
    }
    private static Control? Thumbnail(NMultiplayerPlayerState state) => Dict("_thumbnails")[state] as Control;
    private static Control? Overlay(NMultiplayerPlayerState state) => Dict("_overlays")[state] as Control;
    private static object Field(string name) => AccessTools.Field(typeof(PeekHandPatch), name).GetValue(null)!;
    private static IDictionary Dict(string name) => (IDictionary)Field(name);
    private static int Count(string name) => (int)Field(name).GetType().GetProperty("Count")!.GetValue(Field(name))!;
    private static bool MatchesHand(Control? root, Player player) => root != null &&
        root.GetChildren().OfType<NCard>().Select(c => c.Model).SequenceEqual(player.PlayerCombatState!.Hand.Cards);
    private static void Check(string name, bool passed)
    {
        if (!passed) throw new InvalidOperationException("Assertion failed: " + name);
        Checks.Add(name);
        GD.Print($"[PEEK-TEST] {Role} PASS: {name}");
    }
    private static async Task Frames(int count)
    {
        for (var i = 0; i < count; i++) await Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
    }
    private static async Task Delay(double seconds) => await Tree.ToSignal(Tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    private static async Task Until(Func<bool> condition, string reason, double timeout = 20)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > timeout) throw new TimeoutException(reason);
            await Frames(1);
        }
    }
    private static async Task Barrier(string name)
    {
        File.WriteAllText(Path.Combine(Output, Role + "." + name + ".ready"), "ready");
        var other = Role == "host" ? "client" : "host";
        await Until(() => File.Exists(Path.Combine(Output, other + "." + name + ".ready")), "peer barrier " + name, 40);
    }
    private static void Snapshot(string name, RunState run)
    {
        Snapshots[name] = run.Players.OrderBy(p => p.NetId).Select(p => new
        {
            player = p.NetId, hp = p.Creature.CurrentHp, block = p.Creature.Block,
            turn = p.PlayerCombatState!.TurnNumber, energy = p.PlayerCombatState.Energy,
            hand = p.PlayerCombatState.Hand.Cards.Select(c => c.Id.ToString()).ToArray(),
            discard = p.PlayerCombatState.DiscardPile.Cards.Select(c => c.Id.ToString()).ToArray()
        }).ToArray();
    }
    private static async Task Screenshot(string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        await Tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = Tree.Root.GetTexture().GetImage();
        if (image.SavePng(Path.Combine(Output, Role + "-" + name + ".png")) != Error.Ok)
            throw new IOException("Screenshot failed: " + name);
    }
    private static void Finish(Exception? error)
    {
        GD.Print($"[PEEK-TEST] {Role} {(error == null ? "PASS" : "FAIL")}: {error}");
        var path = Path.Combine(Output, Role + ".result.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
        { role = Role, passed = error == null, checks = Checks, snapshots = Snapshots, error = error?.ToString() }, JsonOptions));
        File.Move(path + ".tmp", path, overwrite: true);
        Tree.Quit(error == null ? 0 : 1);
    }
}

public sealed class Fixture : IBootstrapSettings
{
    public CharacterModel Character => ModelDb.Character<Ironclad>();
    public RoomType RoomType => RoomType.Monster;
    public EncounterModel Encounter => ModelDb.Encounter<SlimesWeak>();
    public EventModel Event => throw new NotSupportedException();
    public ActModel Act => ModelDb.Act<Overgrowth>();
    public int Ascension => 0;
    public bool SaveRunHistory => false;
    public string Seed => "PEEK113";
    public string Language => "eng";
    public bool DoPreloading => true;
    public bool BootstrapInMultiplayer => true;
    public List<ModifierModel> Modifiers => [];

    public Task Setup(Player player)
    {
        SaveManager.Instance.SetFtuesEnabled(false);
        var run = (RunState)player.RunState;
        foreach (var p in run.Players)
        {
            foreach (var card in p.Deck.Cards.ToArray()) { p.Deck.RemoveInternal(card); run.RemoveCard(card); }
            p.Deck.AddInternal(run.CreateCard<Bash>(p));
            p.Deck.AddInternal(run.CreateCard<Neutralize>(p));
            p.Deck.AddInternal(run.CreateCard<SharedFate>(p));
            p.Deck.AddInternal(run.CreateCard<DefendIronclad>(p));
            p.Deck.AddInternal(run.CreateCard<StrikeIronclad>(p));
        }
        return Task.CompletedTask;
    }
}
