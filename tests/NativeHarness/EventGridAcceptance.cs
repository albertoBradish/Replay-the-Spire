using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class EventGridAcceptance
{
    private const string Seed = "EVENTGRID";
    private static string Report(string name) => Path.Combine(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, $"event-grid-{name}.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Pick(int Room, string Kind, string Key, string[] Candidates, string Chosen,
        string[] DeckBefore, string[] DeckAfter);
    private sealed record Witness(string Run, Pick[] Picks);
    private sealed record Proof(int Room, string Kind, int Options, int Visible, string Highlighted, int DeckSize,
        int PauseMilliseconds, bool Reopened, int NativeChoices);
    private static string Id(CardModel c) => $"{c.Id}/{c.CurrentUpgradeLevel}";
    private static string[] Deck(Player player) => player.Deck.Cards.Select(Id).ToArray();
    private static NCardGridSelectionScreen? Screen => NOverlayStack.Instance?.Peek() as NCardGridSelectionScreen;
    private static NCardGrid Grid(NCardGridSelectionScreen screen) => (NCardGrid)AccessTools.Field(typeof(NCardGridSelectionScreen), "_grid").GetValue(screen)!;
    private static IReadOnlyList<CardModel> Options(NCardGridSelectionScreen screen) =>
        (IReadOnlyList<CardModel>)AccessTools.Field(typeof(NCardGridSelectionScreen), "_cards").GetValue(screen)!;

    internal static async Task RecordAsync(NGame game)
    {
        SaveManager.Instance.Progress.EnableFtues = false;
        var existing = ReplayStore.ListRuns().Select(r => r.Id).ToHashSet();
        var hero = ModelDb.Character<Ironclad>();
#if BETA
        var avatar = Player.CreateForNewRun(hero, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
        var run = await game.StartNewSingleplayerRun(avatar, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard, false);
#else
        var run = await game.StartNewSingleplayerRun(hero, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard);
#endif
        var player = run.Players.Single();
        var roomSet = (RoomSet)AccessTools.Field(typeof(ActModel), "_rooms").GetValue(run.Act)!;
        roomSet.events[roomSet.eventsVisited % roomSet.events.Count] = ModelDb.Event<BrainLeech>();
        var odds = run.Odds.UnknownMapPoint;
        odds.MonsterOdds = odds.TreasureOdds = odds.ShopOdds = 0;
        async Task Enter(MapPointType type)
        {
            var point = run.Map.GetAllMapPoints().Where(p => p.PointType == type).OrderBy(p => p.coord.row).First();
            await RunManager.Instance.EnterMapCoord(point.coord);
        }
        await Enter(MapPointType.Unknown);
        var ev = (EventRoom)run.CurrentRoom!;
        var option = ev.LocalMutableEvent.CurrentOptions[0];
        if (ev.CanonicalEvent is not BrainLeech || !option.TextKey.EndsWith("SHARE_KNOWLEDGE", StringComparison.Ordinal))
            throw new InvalidDataException("Brain Leech was not available.");
        NEventRoom.Instance!.OptionButtonClicked(option, 0);
        await JourneyAcceptance.Wait(() => Screen is NSimpleCardSelectScreen, "Brain Leech grid");
        var runId = ReplayStore.ListRuns().Single(r => !existing.Contains(r.Id) && r.Seed == Seed).Id;
        int CurrentIndex() => ReplayStore.ListRuns().Single(r => r.Id == runId).Rooms.Last().Index;
        var offered = Options(Screen!).ToArray();
        if (offered.Length != 5) throw new InvalidDataException("Expected five original offers.");
        var before = Deck(player);
        var chosen = offered[3];
        await ClickAsync(Screen!, chosen);
        await JourneyAcceptance.Wait(() => ev.LocalMutableEvent.IsFinished, "Brain Leech event completion");
        var after = Deck(player);
        if (!after.Order().SequenceEqual(before.Append(Id(chosen)).Order()))
            throw new InvalidDataException("Brain Leech did not add exactly the chosen card.");
        var reward = new Pick(CurrentIndex(), "reward-grid", "index:3", offered.Select(Id).ToArray(), Id(chosen), before, after);

        await Enter(MapPointType.Shop);
        var merchant = (MerchantRoom)run.CurrentRoom!;
        NMerchantRoom.Instance!.OpenInventory();
        var owner = AccessTools.Property(typeof(RunManager), "MerchantSynchronizer")?.GetValue(RunManager.Instance) ?? merchant;
        var inventory = (MerchantInventory)AccessTools.Method(owner.GetType(), "GetLocalInventory").Invoke(owner, null)!;
        var removal = inventory.AllEntries.OfType<MerchantCardRemovalEntry>().Single(o => o.EnoughGold);
        var method = AccessTools.Method(typeof(MerchantEntry), "OnTryPurchaseUnsynchronized") ??
            AccessTools.Method(typeof(MerchantEntry), "OnTryPurchaseWrapper");
        var paying = (Task<bool>)method.Invoke(removal, [inventory, false])!;
        await JourneyAcceptance.Wait(() => Screen is NDeckCardSelectScreen, "merchant removal grid");
        var deckOptions = Options(Screen!).ToArray();
        // Remove the new event card so the selected model and final deck visibly differ.
        var removed = deckOptions.Single(c => ReferenceEquals(c, chosen));
        int deckIndex = player.Deck.Cards.ToList().IndexOf(removed);
        before = Deck(player);
        await ClickAsync(Screen!, removed);
        if (!await paying || player.Deck.Cards.Contains(removed)) throw new InvalidDataException("Card removal failed.");
        after = Deck(player);
        if (!after.SequenceEqual(before.Where((_, i) => i != deckIndex)))
            throw new InvalidDataException("Merchant removed a different card.");
        var shop = new Pick(CurrentIndex(), "deck-removal", $"deck:{deckIndex}", deckOptions.Select(Id).ToArray(), Id(removed), before, after);
        await game.ReturnToMainMenu();
        ReplayStore.Flush();
        var witness = new Witness(runId, [reward, shop]);
        foreach (var pick in witness.Picks)
            if (!ReplayStore.LoadEvents(runId, pick.Room).Any(e => e.Kind == "choice" && e.ActionKey == pick.Key))
                throw new InvalidDataException("Missing recorded " + pick.Key);
        File.WriteAllText(Report("witness"), JsonSerializer.Serialize(witness, Json));
        GD.Print("REPLAY_EVENT_GRID_RECORD_PASS");
    }

    private static async Task ClickAsync(NCardGridSelectionScreen screen, CardModel card)
    {
        var result = screen.CardsSelected();
        var prefs = (CardSelectorPrefs)AccessTools.Field(screen.GetType(), "_prefs").GetValue(screen)!;
        AccessTools.Method(screen.GetType(), "OnCardClicked").Invoke(screen, [card]);
        if (screen is not NSimpleCardSelectScreen || prefs.RequireManualConfirmation)
        {
            NConfirmButton? confirm = null;
            await JourneyAcceptance.Wait(() => result.IsCompleted || Screen == screen &&
                (confirm = screen.FindChildren("*", "", true, false).OfType<NConfirmButton>()
                    .FirstOrDefault(b => b.IsVisibleInTree() && b.IsEnabled)) != null, "original grid confirmation button");
            if (!result.IsCompleted) confirm!.EmitSignal(NClickableControl.SignalName.Released, confirm);
        }
        await JourneyAcceptance.Wait(() => result.IsCompleted && Screen != screen, "original grid accepted selection");
        if (!(await result).SequenceEqual([card])) throw new InvalidDataException("Original selector returned a different card.");
    }

    internal static async Task WatchAsync()
    {
        var witness = JsonSerializer.Deserialize<Witness>(File.ReadAllText(Report("witness")))!;
        var results = new List<Proof>();
        foreach (var pick in witness.Picks)
        {
            var data = ReplayStore.LoadRoom(witness.Run, pick.Room);
            int target = data.Events.FindIndex(e => e.Kind == "choice" && e.ActionKey == pick.Key) + 1;
            if (target == 0) throw new InvalidDataException("Missing original choice.");
            await ReplayPlayer.PlayRoomAsync(data.Checkpoint!, data.Events, target);
            async Task<NCardGridSelectionScreen> WaitForGrid()
            {
                await JourneyAcceptance.Wait(() => ReplayPlayer.HasError || ReplayPlayer.IsFinished ||
                    ReplayPlayer.IsPaused && Screen != null, "seek inside " + pick.Kind);
                if (ReplayPlayer.HasError || ReplayPlayer.IsFinished || Screen == null)
                    throw new InvalidDataException("Missing native grid: " + ReplayPlayer.Status);
                return Screen;
            }
            var first = await WaitForGrid();
            var firstId = first.GetInstanceId();
            await ReplayPlayer.SeekDecisionAsync(target);
            var screen = await WaitForGrid();
            if (screen.GetInstanceId() == firstId) throw new InvalidDataException("Seek retained the previous prompt.");
            var options = Options(screen);
            var grid = Grid(screen);
            int visible = grid.CurrentlyDisplayedCardHolders.Count(h => h.CardModel != null && h.IsVisibleInTree());
            if (!options.Select(Id).SequenceEqual(pick.Candidates) || pick.Kind == "reward-grid" && visible != 5)
                throw new InvalidDataException($"Different candidates for {pick.Kind} ({visible} visible).");
            var player = RunManager.Instance.DebugOnlyGetState()!.Players.Single();
            var start = Time.GetTicksMsec();
            await JourneyAcceptance.Wait(() => Time.GetTicksMsec() - start >= 900, "paused offer");
            if (Screen != screen || !Deck(player).SequenceEqual(pick.DeckBefore) || !ReplayPlayer.IsPaused)
                throw new InvalidDataException("The paused offer was submitted.");
            int paused = (int)(Time.GetTicksMsec() - start);
            ReplayPlayer.SetSpeed(2);
            ReplayPlayer.SetPaused(false);
            var highlights = AccessTools.Field(typeof(NCardGrid), "_highlightedCards");
            CardModel? marked = null;
            await JourneyAcceptance.Wait(() => ReplayPlayer.IsFinished || GridHasHighlight(), "visible selected card");
            bool GridHasHighlight()
            {
                if (Screen != screen) return false;
                marked = ((IEnumerable<CardModel>)highlights.GetValue(grid)!).SingleOrDefault();
                return marked != null && grid.GetCardHolder(marked) is { } holder && holder.IsVisibleInTree();
            }
            if (marked == null || Id(marked) != pick.Chosen) throw new InvalidDataException("The original card was not highlighted.");
            await JourneyAcceptance.Wait(() => ReplayPlayer.IsFinished, "native event result");
            if (ReplayPlayer.HasError || !Deck(player).SequenceEqual(pick.DeckAfter))
                throw new InvalidDataException("Deck differs after the original choice: " + ReplayPlayer.Status);
            results.Add(new(pick.Room, pick.Kind, options.Count, visible, Id(marked), player.Deck.Cards.Count,
                paused, true, ReplayPlayer.NativeChoiceCount));
            File.WriteAllText(Report("coverage"), JsonSerializer.Serialize(results, Json));
            await ReplayPlayer.StopAsync();
        }
        GD.Print("REPLAY_EVENT_GRID_WATCH_PASS");
    }
}
