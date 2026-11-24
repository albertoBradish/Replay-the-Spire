using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class CrystalSphereAcceptance
{
    private const string Seed = "CRYSTALREPLAY";
    private static string Report => Path.Combine(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, "crystal-state.json");

    internal static async Task RecordAsync(NGame game)
    {
        SaveManager.Instance.Progress.EnableFtues = false;
        var hero = ModelDb.Character<Ironclad>();
#if BETA
        var avatar = Player.CreateForNewRun(hero, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
        var run = await game.StartNewSingleplayerRun(avatar, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard, false);
#else
        var run = await game.StartNewSingleplayerRun(hero, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard);
#endif
        var player = run.Players.Single();
        await PlayerCmd.GainGold(150, player);
        await ReachActTwoEventAsync(run, player, ModelDb.Event<CrystalSphere>());
        var room = run.CurrentRoom as EventRoom;
        Require(room?.CanonicalEvent is CrystalSphere, "ordinary map roll must open Crystal Sphere");
        var option = room!.LocalMutableEvent.CurrentOptions[1];
        Require(option.TextKey.EndsWith("PAYMENT_PLAN", StringComparison.Ordinal), "six-divination option");
        int initialGold = player.Gold;
        using var probe = new Probe();
        NEventRoom.Instance!.OptionButtonClicked(option, 1);
        await JourneyAcceptance.Wait(() => NOverlayStack.Instance?.Peek() is NCrystalSphereScreen, "crystal sphere opens");
        var screen = (NCrystalSphereScreen)NOverlayStack.Instance!.Peek()!;
        var grid = Grid(screen);
        Require(grid.DivinationCount == 6, "initial native divination count");
        var cells = screen.FindChildren("*", "", true, false).OfType<NCrystalSphereCell>()
            .OrderBy(c => c.Entity.Y).ThenBy(c => c.Entity.X).ToArray();
        for (int i = 0; i < 6; i++)
        {
            bool big = i == 1;
            if (i < 3) AccessTools.Method(typeof(NCrystalSphereScreen), big ? "SetBigDivination" : "SetSmallDivination")
                .Invoke(screen, [null]);
            var cell = cells.First(c => c.Entity.IsHidden && (i switch
            {
                0 => c.Entity.Item is CrystalSphereGold { Size: var size } && size == Vector2I.One,
                1 => c.Entity.Item is CrystalSphereGold { Size: var size } && size.X == 2,
                _ => c.Entity.Item == null
            }));
            await (Task)AccessTools.Method(typeof(NCrystalSphereScreen), "OnCellClicked").Invoke(screen, [cell])!;
            Require(grid.DivinationCount == 5 - i, $"native cell {i + 1} consumed exactly one divination");
        }
        await JourneyAcceptance.Wait(() => NOverlayStack.Instance?.Peek() is NRewardsScreen, "crystal item rewards appear");
        var rewards = (NRewardsScreen)NOverlayStack.Instance!.Peek()!;
        var gold = rewards.FindChildren("*", "", true, false).OfType<NRewardButton>()
            .Where(b => b.Reward is GoldReward).ToArray();
        int amount = gold.Sum(b => ((GoldReward)b.Reward!).Amount);
        Require(amount >= 40, "the small and large revealed gold are both offered");
        foreach (var button in gold)
            await (Task)AccessTools.Method(typeof(NRewardButton), "GetReward").Invoke(button, null)!;
        if (NOverlayStack.Instance.Peek() == rewards)
            AccessTools.Method(typeof(NRewardsScreen), "OnProceedButtonPressed").Invoke(rewards, [null]);
        var proceed = (NProceedButton)AccessTools.Field(typeof(NCrystalSphereScreen), "_proceedButton").GetValue(screen)!;
        await JourneyAcceptance.Wait(() => NOverlayStack.Instance.Peek() == screen && proceed.IsVisibleInTree() && proceed.IsEnabled,
            "native rewards settled and crystal proceed enabled");
        Require(player.Gold == initialGold + amount && player.Deck.Cards.Any(c => c is Debt), "real reward payment and curse");
        AccessTools.Method(typeof(NCrystalSphereScreen), "OnProceedButtonPressed").Invoke(screen, [proceed]);
        await JourneyAcceptance.Wait(() => NMapScreen.Instance!.IsOpen && room.LocalMutableEvent.IsFinished, "crystal map exit");
        string outcome = PlayerState(player);
        await game.ReturnToMainMenu();
        ReplayStore.Flush();
        var info = ReplayStore.ListRuns().First(r => r.Seed == Seed);
        var visit = info.Rooms.Single(r => ReplayStore.LoadEvents(info.Id, r.Index).Any(e => e.Kind == "crystal"));
        var data = ReplayStore.LoadRoom(info.Id, visit.Index);
        Require(data.Checkpoint != null && data.Events.Any(e => e.Kind == "event" && e.ActionKey == option.TextKey),
            "restorable crystal event and parent option");
        var actions = data.Events.Where(e => e.Kind == "crystal").Select(ActionToken).ToArray();
        Require(actions.SequenceEqual(probe.Actions) && actions.Count(s => s.StartsWith("cell:")) == 6 &&
            actions.Contains("tool:small") && actions.Contains("tool:big") && actions.Last() == "proceed", "recorded native input order");
        var boards = data.Events.Where(e => e.Kind == "crystal-board").ToArray();
        Require(boards.Length == 2 && BoardMarker(boards[0], "open", 6) && BoardMarker(boards[1], "finish", 0), "opening and settled board fingerprints");
        Require(data.Events.Any(e => e.Kind == "reward" && e.ActionKey.Contains(nameof(GoldReward))),
            "native reward selection recorded alongside the board");
        Require(probe.Opened.Length > 0 && probe.Closed.Length > 0 && probe.Opened != probe.Closed, "observed a changed native board");
        File.WriteAllText(Report, JsonSerializer.Serialize(new Witness(info.Id, visit.Index, actions, probe.Opened, probe.Closed, outcome, amount)));
        GD.Print($"[ReplayHarness] crystal taps=6 gold={amount} board={probe.Closed}");
        GD.Print("REPLAY_CRYSTAL_RECORD_PASS");
    }

    internal static async Task ReachActTwoEventAsync(RunState run, Player player, EventModel target)
    {
        // The event is only eligible from act two. Win act one so the native
        // resume screen, event pool and replay all see the same complete history.
        double speed = Engine.TimeScale;
        try
        {
            Engine.TimeScale = 6;
            await CreatureCmd.SetMaxHp(player.Creature, 1000);
            await CreatureCmd.SetCurrentHp(player.Creature, 1000);
            await CardPileCmd.RemoveFromDeck(player.Deck.Cards.ToArray(), false);
            for (int i = 0; i < 6; i++)
            {
                var scythe = (TheScythe)run.CreateCard(ModelDb.Card<TheScythe>(), player);
                scythe.IncreasedDamage = 986;
                scythe.CurrentDamage = 999;
                await CardPileCmd.Add(scythe, PileType.Deck);
                await CardPileCmd.Add(run.CreateCard(ModelDb.Card<Clash>(), player), PileType.Deck);
            }
            var driver = new CampaignAcceptance.CampaignDriver(run, player);
            for (int i = 0; i < 240; i++)
            {
                await JourneyAcceptance.Wait(() => run.CurrentRoom != null, "connected crystal fixture room");
                if (run.CurrentRoom is EventRoom eventRoom && eventRoom.CanonicalEvent.Id == target.Id) return;
                var room = run.CurrentRoom!;
                var map = NMapScreen.Instance!;
                if (run.CurrentActIndex == 1 && map.IsOpen && map.IsTravelEnabled)
                {
                    NMapPoint[] nodes = [];
                    await JourneyAcceptance.Wait(() => !map.IsTraveling && (nodes = map.FindChildren("*", "", true, false)
                        .OfType<NMapPoint>().Where(n => n.State == MapPointState.Travelable).ToArray()).Length > 0,
                        "connected path to a second-act question mark");
                    var next = nodes.OrderBy(n => FloorsToEvent(n.Point)).ThenBy(n => n.Point.coord.col).First();
                    Require(FloorsToEvent(next.Point) < 100, "a reachable second-act question mark");
                    if (next.Point.PointType == MapPointType.Unknown)
                    {
                        var pool = (RoomSet)AccessTools.Field(typeof(ActModel), "_rooms").GetValue(run.Act)!;
                        pool.events[pool.eventsVisited % pool.events.Count] = target;
                        var odds = run.Odds.UnknownMapPoint;
                        odds.MonsterOdds = odds.TreasureOdds = odds.ShopOdds = 0;
                    }
                    map.OnMapPointSelectedLocally(next);
                    await JourneyAcceptance.Wait(() => run.CurrentRoom != null && run.CurrentRoom != room && !map.IsTraveling,
                        "map vote advances to the selected node");
                }
                else await driver.VisitAsync(room);
                Require(!player.Creature.IsDead && run.CurrentActIndex < 2, "alive on the crystal fixture route");
            }
            throw new TimeoutException("The connected route did not reach the second-act Crystal Sphere.");
        }
        finally { Engine.TimeScale = speed; }
    }

    private static int FloorsToEvent(MapPoint point)
    {
        if (point.PointType == MapPointType.Unknown) return 0;
        return point.Children.Count == 0 ? 100 : 1 + point.Children.Min(FloorsToEvent);
    }

    internal static async Task WatchAsync()
    {
        var expected = JsonSerializer.Deserialize<Witness>(File.ReadAllText(Report))!;
        var data = ReplayStore.LoadRoom(expected.Run, expected.Room);
        var protectedBefore = ProtectedFiles(expected.Run);
        Require(protectedBefore.Any(f => f.StartsWith("profile/")), "existing protected profile files");
        double speed = ReplayPlayer.Speed;
        using var probe = new Probe();
        try
        {
            ReplayPlayer.SetSpeed(4);
            int secondCell = data.Events.FindIndex(e => e.Kind == "crystal" && e.ActionKey.StartsWith("cell:") && e.Amount == 5);
            Require(secondCell >= 0, "second recorded cell");
            await ReplayPlayer.PlayRoomAsync(data.Checkpoint!, data.Events, secondCell + 1);
            await JourneyAcceptance.Wait(() => ReplayPlayer.IsPaused || ReplayPlayer.IsFinished, "seek to second crystal cell");
            var screen = NOverlayStack.Instance?.Peek() as NCrystalSphereScreen;
            Require(!ReplayPlayer.HasError && !ReplayPlayer.IsFinished && screen != null, "paused on the live crystal board");
            var board = Grid(screen!);
            string beforeStep = Probe.Fingerprint(board);
            Require(board.DivinationCount == 5 && probe.Actions.Count(a => a.StartsWith("cell:")) == 1, "first cell restored before second input");
            ulong start = Time.GetTicksMsec();
            await JourneyAcceptance.Wait(() => Time.GetTicksMsec() - start >= 600, "hold the board at a recorded decision");
            Require(ReplayPlayer.IsPaused && Probe.Fingerprint(board) == beforeStep, "paused fog and divinations stay unchanged");
            ReplayPlayer.Step();
            await JourneyAcceptance.Wait(() => ReplayPlayer.HasError || ReplayPlayer.IsPaused && board.DivinationCount == 4,
                "single-step the second crystal cell");
            Require(!ReplayPlayer.HasError && probe.Actions.Count(a => a.StartsWith("cell:")) == 2 &&
                Probe.Fingerprint(board) != beforeStep, "exactly one cell input applied after stepping");
            ReplayPlayer.SetPaused(false);
            await JourneyAcceptance.Wait(() => ReplayPlayer.IsFinished || ReplayPlayer.HasError, "native crystal replay", 85);
            Require(!ReplayPlayer.HasError && probe.Actions.SequenceEqual(expected.Actions), "replayed native tool/cell/proceed sequence");
            Require(probe.Opened == expected.Opened && probe.Closed == expected.Closed, "native item positions and final fog");
            var run = RunManager.Instance.DebugOnlyGetState()!;
            Require(run.CurrentRoom is EventRoom { LocalMutableEvent.IsFinished: true } &&
                PlayerState(run.Players.Single()) == expected.Player, $"replayed curse, inventory and {expected.Gold} gold");
            await ReplayPlayer.StopAsync();

            probe.Reset();
            var legacy = data.Events.Where(e => e.Kind is not ("crystal" or "crystal-board")).ToArray();
            await ReplayPlayer.PlayRoomAsync(data.Checkpoint!, legacy);
            await JourneyAcceptance.Wait(() => ReplayPlayer.IsFinished || ReplayPlayer.HasError, "legacy event choice finishes without a board", 45);
            Require(!ReplayPlayer.HasError && ReplayPlayer.CaptionChoiceCount > 0 && probe.Opened.Length == 0 &&
                probe.Actions.Count == 0 && NOverlayStack.Instance?.Peek() is not NCrystalSphereScreen,
                "legacy timeline displayed its recorded choice without claiming native tile actions");
        }
        finally
        {
            if (ReplayPlayer.IsActive) await ReplayPlayer.StopAsync();
            ReplayPlayer.SetSpeed(speed);
        }
        Require(!RunManager.Instance.IsInProgress && NGame.Instance?.MainMenu != null &&
            protectedBefore.SequenceEqual(ProtectedFiles(expected.Run)), "returned to menu with exact original save and replay bytes");
        GD.Print($"[ReplayHarness] crystal nativeActions={expected.Actions.Length} gold={expected.Gold} protected={protectedBefore.Length} legacy=caption");
        GD.Print("REPLAY_CRYSTAL_WATCH_PASS");
    }

    private static CrystalSphereMinigame Grid(NCrystalSphereScreen screen) =>
        (CrystalSphereMinigame)AccessTools.Field(typeof(NCrystalSphereScreen), "_entity").GetValue(screen)!;

    private static string PlayerState(Player player) => string.Join('|', player.Gold, player.Creature.CurrentHp,
        string.Join(',', player.Deck.Cards.Select(c => $"{c.Id}/{c.CurrentUpgradeLevel}")),
        string.Join(',', player.Relics.Select(r => r.Id)), string.Join(',', player.PotionSlots.Select(p => p?.Id.ToString() ?? "-")));

    private static bool BoardMarker(ReplayEvent e, string name, int count) =>
        e.Amount == count && e.ActionKey.StartsWith(name + ":", StringComparison.Ordinal) && e.ActionKey.Length == name.Length + 65 &&
        e.ActionKey[(name.Length + 1)..].All(char.IsAsciiHexDigit);

    private static string ActionToken(ReplayEvent e) =>
        e.ActionKey.StartsWith("cell:") ? $"{e.ActionKey}|{e.ActionIndex}|{e.Amount}" : e.ActionKey;

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidDataException(label + " — " + ReplayPlayer.Status);
    }

    private static string[] ProtectedFiles(string runId)
    {
        var profile = OS.GetUserDataDir();
        var replay = Path.Combine(ReplayStore.LibraryDirectory, runId);
        var files = Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories).Where(f =>
            Path.GetFileName(f) is not ("settings.save" or "settings.save.backup") &&
            (Path.GetExtension(f) is ".save" or ".mcr" or ".run" || f.EndsWith(".save.backup", StringComparison.Ordinal)));
        string Entry(string root, string path) => Path.GetRelativePath(root, path) + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return files.Select(f => "profile/" + Entry(profile, f)).Concat(Directory.EnumerateFiles(replay).Select(f => "replay/" + Entry(replay, f)))
            .Order(StringComparer.Ordinal).ToArray();
    }

    // Observe the game's own handlers during both sessions; reading the replay journal twice could hide skipped inputs.
    private sealed class Probe : IDisposable
    {
        private static Probe? current;
        private readonly Harmony harmony = new("replaythespire.test.crystal");
        private readonly List<(MethodInfo Original, MethodInfo Patch)> methods = [];
        internal List<string> Actions { get; } = [];
        internal string Opened { get; private set; } = "";
        internal string Closed { get; private set; } = "";

        internal Probe()
        {
            current = this;
            Patch(typeof(NCrystalSphereScreen), "ShowScreen", nameof(OnOpen));
            Patch(typeof(CrystalSphereMinigame), "SetTool", nameof(OnTool));
            Patch(typeof(CrystalSphereMinigame), "CellClicked", nameof(OnCell));
            Patch(typeof(NCrystalSphereScreen), "OnProceedButtonPressed", nameof(OnProceed));
        }
        internal void Reset() { Actions.Clear(); Opened = Closed = ""; }
        public void Dispose()
        {
            current = null;
            foreach (var (original, prefix) in methods) harmony.Unpatch(original, prefix);
        }
        private void Patch(Type type, string name, string prefix)
        {
            var target = AccessTools.Method(type, name);
            var patch = AccessTools.Method(typeof(Probe), prefix);
            harmony.Patch(target, prefix: new HarmonyMethod(patch));
            methods.Add((target, patch));
        }

        private static void OnOpen(CrystalSphereMinigame __0) => current!.Opened = Fingerprint(__0);
        private static void OnTool(CrystalSphereMinigame __instance, CrystalSphereMinigame.CrystalSphereToolType __0)
        {
            if (__instance.CrystalSphereTool != __0) current!.Actions.Add("tool:" + __0.ToString().ToLowerInvariant());
        }
        private static void OnCell(CrystalSphereMinigame __instance, CrystalSphereCell __0) => current!.Actions.Add(
            $"cell:{__instance.CrystalSphereTool.ToString().ToLowerInvariant()}|{__0.Y * __instance.GridSize.X + __0.X}|{__instance.DivinationCount}");
        private static void OnProceed(NCrystalSphereScreen __instance)
        {
            current!.Closed = Fingerprint(Grid(__instance));
            current.Actions.Add("proceed");
        }
        internal static string Fingerprint(CrystalSphereMinigame grid)
        {
            var items = grid.Items.Select(i =>
            {
                var item = i.ToSerializable();
                return $"{item.type}/{item.cardRarity}/{item.potionRarity}/{item.isBigGold}/{i.Position.X},{i.Position.Y}/{i.Size.X},{i.Size.Y}";
            });
            var fog = grid.cells.Cast<CrystalSphereCell>().Select(c => c.IsHidden ? '1' : '0').ToArray();
            string value = string.Join(';', items) + "|" + grid.DivinationCount + "|" + new string(fog);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }

    private sealed record Witness(string Run, int Room, string[] Actions, string Opened, string Closed, string Player, int Gold);
}
