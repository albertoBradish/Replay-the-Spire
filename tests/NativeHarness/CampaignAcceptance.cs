using System.IO.Compression;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class CampaignAcceptance
{
    private const string Seed = "CAMPAIGNREPLAY";
    private const string Setup = "Ironclad, 1,000 maximum/current HP, six The Scythe at 999 damage (986 permanent growth), six Clash. Set once before the first map choice.";
    private static string Report(string name) => Path.Combine(
        System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, "campaign-" + name + ".json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task RecordAsync(NGame game)
    {
        double speed = Engine.TimeScale;
        var visits = new List<Visit>();
        try
        {
            Engine.TimeScale = 6;
            SaveManager.Instance.Progress.EnableFtues = false;
            var hero = ModelDb.Character<Ironclad>();
#if BETA
            var avatar = Player.CreateForNewRun(hero, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
            var run = await game.StartNewSingleplayerRun(avatar, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard, false);
#else
            var run = await game.StartNewSingleplayerRun(hero, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard);
#endif
            var player = run.Players.Single();
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
            var driver = new CampaignDriver(run, player);
            AbstractRoom? previous = null;
            RunInfo? victory = null;
            int moves = 0;
            for (; moves < 650 && victory == null; moves++)
            {
                await JourneyAcceptance.Wait(() => run.CurrentRoom != null, "campaign room entry");
                var room = run.CurrentRoom!;
                if (!ReferenceEquals(room, previous))
                {
                    visits.Add(new Visit(run.CurrentActIndex + 1, run.TotalFloor, room.RoomType.ToString(), room.ModelId?.ToString()));
                    GD.Print($"[ReplayHarness] campaign act {run.CurrentActIndex + 1}, floor {run.TotalFloor}, {room.RoomType} {room.ModelId}");
                    previous = room;
                }
                if (await driver.VisitAsync(room))
                {
                    await JourneyAcceptance.Wait(() => (victory = ReplayStore.ListRuns().FirstOrDefault(r =>
                        r.Seed == Seed && r.Result == "胜利")) != null, "canonical victory recorded");
                }
                else if (player.Creature.IsDead)
                    throw new InvalidDataException("Defeated before the final boss.");
            }
            if (victory == null)
                throw new InvalidDataException($"No victory after {moves} UI actions; act {run.CurrentActIndex + 1}, floor {run.TotalFloor}, {run.CurrentRoom?.ModelId}, overlay {NOverlayStack.Instance?.Peek()?.GetType().Name}.");
            if (visits.Select(v => v.Act).Distinct().Count() != run.Acts.Count)
                throw new InvalidDataException($"Native victory reached with only {visits.Select(v => v.Act).Distinct().Count()}/{run.Acts.Count} acts.");
            int encounterRooms = visits.Count(visit => visit.Type is "Monster" or "Elite" or "Boss");
            if (victory.Rooms.Count(room => room.HasCombat) < encounterRooms)
                throw new InvalidDataException("The native recording omitted an encounter from the original route.");
            string archive = Path.Combine(ReplayStore.ExportsDirectory, victory.Id + ".spireplay");
            await JourneyAcceptance.Wait(() => File.Exists(archive), "campaign portable archive");
            using var zip = ZipFile.OpenRead(archive);
            var witness = new Campaign(victory.Id, victory.Result, Setup, visits, victory.Rooms.Count,
                zip.Entries.Sum(e => e.Length), new FileInfo(archive).Length, zip.Entries.Count,
                victory.Rooms.Count(r => r.HasCombat), victory.Rooms.Count(r => r.HasCheckpoint));
            File.WriteAllText(Report("witness"), JsonSerializer.Serialize(witness, Json));
            GD.Print("REPLAY_CAMPAIGN_RECORD_PASS");
        }
        catch
        {
            File.WriteAllText(Report("partial-route"), JsonSerializer.Serialize(visits, Json));
            throw;
        }
        finally { Engine.TimeScale = speed; }
    }

    internal sealed class CampaignDriver(RunState run, Player player, bool ordinary = false)
    {
        private bool keptCard;
        private bool keptTreasure;

        internal async Task<bool> VisitAsync(AbstractRoom room)
        {
            var map = NMapScreen.Instance!;
            if (map.IsOpen && map.IsTravelEnabled)
                await TravelAsync(map, room);
            else if (NOverlayStack.Instance?.Peek() is NCrystalSphereScreen sphere)
                await AdvanceSphereAsync(sphere);
            else if (NOverlayStack.Instance?.Peek() is NRewardsScreen rewards)
                await CollectAsync(rewards, room);
            else if (NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen grid)
                await SelectGridAsync(grid);
            else if (room is CombatRoom)
            {
                if (CombatManager.Instance.IsInProgress)
                {
                    if (ordinary) await JourneyAcceptance.FightAsync(player, hand => OrdinaryAcceptance.ChoosePlay(player, hand), 300);
                    else await JourneyAcceptance.WinFightAsync(player, CombatCandidates);
                }
                if (player.Creature.IsDead) return false;
                await JourneyAcceptance.Wait(() => player.Creature.IsDead || NOverlayStack.Instance?.Peek() is NRewardsScreen ||
                    NMapScreen.Instance!.IsOpen || run.CurrentRoom != room || CombatManager.Instance.IsInProgress,
                    "post-combat reward or map");
            }
            else if (room is EventRoom ev)
                return await ChooseEventAsync(ev);
            else if (room is RestSiteRoom camp)
            {
                int heal = camp.Options.ToList().FindIndex(o => o.IsEnabled && o.OptionId == "HEAL");
                if (heal >= 0)
                {
                    var rest = camp.Options[heal];
                    var native = NRestSiteRoom.Instance!;
                    await JourneyAcceptance.Wait(() => native.GetButtonForOption(rest) != null, "camp buttons");
                    var button = native.GetButtonForOption(rest)!;
                    await (Task)AccessTools.Method(button.GetType(), "SelectOption").Invoke(button, [rest])!;
                }
                await OpenMapAsync();
            }
            else if (room is TreasureRoom)
            {
                if (!map.IsTravelEnabled)
                {
                    await JourneyAcceptance.Wait(() => NRun.Instance?.TreasureRoom is { } view && view.IsNodeReady(),
                        "native treasure room ready");
                    var chest = NRun.Instance!.TreasureRoom!;
                    var open = (Task)AccessTools.Method(typeof(NTreasureRoom), "OpenChest").Invoke(chest, null)!;
                    var picking = RunManager.Instance.TreasureRoomRelicSynchronizer;
                    await JourneyAcceptance.Wait(() => picking.CurrentRelics is {Count: > 0} &&
                        AccessTools.Field(typeof(NTreasureRoom), "_isRelicCollectionOpen").GetValue(chest) is true,
                        "campaign treasure");
                    if (ordinary || !keptTreasure)
                    {
                        var selected = picking.CurrentRelics![0].Id;
                        picking.PickRelicLocally(0);
                        while (!open.IsCompleted)
                        {
                            await JourneyAcceptance.Wait(() => open.IsCompleted || NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen,
                                "native treasure relic pickup");
                            if (NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen select) await SelectGridAsync(select);
                        }
                        await open;
                        if (!player.Relics.Any(r => r.Id == selected))
                            throw new InvalidDataException("Treasure pickup did not award " + selected);
                        keptTreasure = true;
                    }
                    else
                    {
                        var proceed = chest.GetNode<NProceedButton>("%ProceedButton");
                        await JourneyAcceptance.Wait(() => proceed.IsEnabled && proceed.IsSkip, "native treasure skip button");
                        proceed.EmitSignal(NClickableControl.SignalName.Released, proceed);
                        _ = open.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    }
                }
                if (!map.IsOpen && NOverlayStack.Instance?.Peek() is not NRewardsScreen) await OpenMapAsync();
            }
            else if (room is MerchantRoom merchant)
            {
                if (ordinary) await OrdinaryAcceptance.ShopAsync(merchant);
                await OpenMapAsync();
            }
            else if (room is MapRoom)
                await OpenMapAsync();
            else throw new InvalidDataException("Unhandled native room " + room.GetType().Name);
            return false;
        }

        private static async Task AdvanceSphereAsync(NCrystalSphereScreen sphere)
        {
            var entity = (CrystalSphereMinigame)AccessTools.Field(typeof(NCrystalSphereScreen), "_entity").GetValue(sphere)!;
            if (entity.DivinationCount > 0)
            {
                var cells = sphere.FindChildren("*", "", true, false).OfType<NCrystalSphereCell>().Where(c => c.Entity.IsHidden);
                var cell = cells.OrderBy(c => Math.Abs(c.Entity.X - entity.GridSize.X / 2) + Math.Abs(c.Entity.Y - entity.GridSize.Y / 2))
                    .ThenBy(c => c.Entity.Y).ThenBy(c => c.Entity.X).First();
                await (Task)AccessTools.Method(typeof(NCrystalSphereScreen), "OnCellClicked").Invoke(sphere, [cell])!;
                return;
            }
            var proceed = (NProceedButton)AccessTools.Field(typeof(NCrystalSphereScreen), "_proceedButton").GetValue(sphere)!;
            await JourneyAcceptance.Wait(() => NOverlayStack.Instance?.Peek() != sphere || proceed.IsVisibleInTree() && proceed.IsEnabled,
                "crystal sphere reward settlement");
            if (NOverlayStack.Instance?.Peek() != sphere) return;
            AccessTools.Method(typeof(NCrystalSphereScreen), "OnProceedButtonPressed").Invoke(sphere, [proceed]);
            await JourneyAcceptance.Wait(() => NMapScreen.Instance!.IsOpen || NOverlayStack.Instance?.Peek() != sphere, "leave crystal sphere");
        }

        private static IEnumerable<CardModel> CombatCandidates(IEnumerable<CardModel> hand) =>
            hand.Where(c => c is TheScythe or Clash or PommelStrike or Discovery)
                .OrderBy(c => c switch { TheScythe => 0, Clash => 1, _ => 2 });

        private static async Task OpenMapAsync()
        {
            var map = NMapScreen.Instance!;
            await JourneyAcceptance.Wait(() => map.IsTravelEnabled, "native map travel enabled");
            map.Open();
        }

        private async Task TravelAsync(NMapScreen map, AbstractRoom from)
        {
            NMapPoint[] choices = [];
            await JourneyAcceptance.Wait(() => !map.IsTraveling && (choices = map.FindChildren("*", "", true, false)
                .OfType<NMapPoint>().Where(n => n.State == MapPointState.Travelable).ToArray()).Length > 0, "connected map nodes");
            var node = choices.OrderBy(n => n.Point.PointType switch
            {
                MapPointType.Monster => 0,
                MapPointType.RestSite => 1,
                MapPointType.Shop => 2,
                MapPointType.Elite => 3,
                MapPointType.Unknown => 5,
                _ => 4
            }).ThenBy(n => n.Point.coord.col).First();
            map.OnMapPointSelectedLocally(node);
            await JourneyAcceptance.Wait(() => run.CurrentRoom != null && run.CurrentRoom != from && !map.IsTraveling,
                "native map transition");
        }

        private async Task CollectAsync(NRewardsScreen screen, AbstractRoom from)
        {
            var options = screen.FindChildren("*", "", true, false).OfType<NRewardButton>()
                .Where(b => b.IsEnabled && b.Reward is {SuccessfullySelected: false}).ToList();
            var gold = options.FirstOrDefault(b => b.Reward is GoldReward);
            if (gold != null) await PressReward(gold);
            var potion = options.FirstOrDefault(b => b.Reward is PotionReward);
            if (ordinary && player.HasOpenPotionSlots && potion != null) await PressReward(potion);
            var card = options.FirstOrDefault(b => b.Reward is CardReward);
            if ((ordinary || !keptCard) && card?.Reward is CardReward offer)
            {
                var offerPick = ordinary ? OrdinaryAcceptance.ChooseReward(player, offer.Cards) : null;
                if (offerPick != null || !ordinary)
                {
                    var selecting = PressReward(card);
                    await JourneyAcceptance.Wait(() => NOverlayStack.Instance!.Peek() is NCardRewardSelectionScreen, "campaign card reward");
                    var picker = (NCardRewardSelectionScreen)NOverlayStack.Instance!.Peek()!;
                    var pick = offerPick ?? offer.Cards.FirstOrDefault(c => c.Type == CardType.Attack) ?? offer.Cards.First();
                    AccessTools.Method(picker.GetType(), "SelectCard").Invoke(picker, [picker.GetCardHolder(pick)]);
                    await selecting;
                    keptCard = true;
                }
            }
            AccessTools.Method(screen.GetType(), "OnProceedButtonPressed").Invoke(screen, [null]);
            await JourneyAcceptance.Wait(() => NMapScreen.Instance!.IsOpen || run.CurrentRoom != from ||
                NOverlayStack.Instance!.Peek() != screen, "campaign rewards proceed");
        }

        private static Task PressReward(NRewardButton button) =>
            (Task)AccessTools.Method(typeof(NRewardButton), "GetReward").Invoke(button, null)!;

        private async Task<bool> ChooseEventAsync(EventRoom room)
        {
            // Act changes push the Architect model before its assets and event
            // scene are initialized. Wait for the same event that a player sees.
            await JourneyAcceptance.Wait(() => NEventRoom.Instance is { } view &&
                AccessTools.Field(typeof(NEventRoom), "_event").GetValue(view) is EventModel showing &&
                showing.Id == room.ModelId, "native event scene " + room.ModelId);
            var model = room.LocalMutableEvent;
            if (model.IsFinished && model is not TheArchitect)
            {
                await NEventRoom.Proceed();
                return false;
            }
            await JourneyAcceptance.Wait(() => model.CurrentOptions.Any(o => !o.IsLocked), "clickable event choice " + model.Id);
            var choices = model.CurrentOptions;
            int selected = Enumerable.Range(0, choices.Count).Where(i => !choices[i].IsLocked)
                .OrderBy(i => choices[i].IsProceed || choices[i].TextKey.Contains("LEAVE", StringComparison.OrdinalIgnoreCase) ? 0 :
                    choices[i].Relic != null ? 1 : 2).First();
            var chosen = choices[selected];
            bool ending = model is TheArchitect && chosen.TextKey == "PROCEED";
            NEventRoom.Instance!.OptionButtonClicked(chosen, selected);
            if (ending) return true;
            await JourneyAcceptance.Wait(() => player.Creature.IsDead || run.CurrentRoom != room || model.IsFinished || !model.CurrentOptions.Contains(chosen) ||
                NOverlayStack.Instance?.Peek() != null || NMapScreen.Instance!.IsOpen, "event response " + chosen.TextKey);
            return false;
        }

        private static async Task SelectGridAsync(NCardGridSelectionScreen grid)
        {
            var type = grid.GetType();
            if (AccessTools.Field(typeof(NCardGridSelectionScreen), "_cards")?.GetValue(grid) is not IReadOnlyList<CardModel> { Count: > 0 } options ||
                AccessTools.Field(type, "_prefs")?.GetValue(grid) is not CardSelectorPrefs prefs)
                throw new InvalidDataException("Missing card options or selection limits on " + type.Name);
            var click = AccessTools.Method(type, "OnCardClicked", [typeof(CardModel)]) ??
                throw new MissingMethodException(type.Name, "OnCardClicked");
            var completion = grid.CardsSelected();
            int count = prefs.RequireManualConfirmation ? Math.Max(1, prefs.MinSelect) : prefs.MaxSelect;
            var picks = options.OrderBy(c => c is TheScythe).Take(count).ToArray();
            foreach (var card in picks) click.Invoke(grid, [card]);

            // Simple single-choice grids close on the card click. Deck selection screens may
            // first ask to preview the choices, then enable a second confirmation button.
            var pressed = new HashSet<NConfirmButton>();
            while (!completion.IsCompleted && pressed.Count < 2)
            {
                NConfirmButton? button = null;
                await JourneyAcceptance.Wait(() => completion.IsCompleted ||
                    (button = grid.FindChildren("*", "", true, false).OfType<NConfirmButton>()
                        .FirstOrDefault(b => !pressed.Contains(b) && b.IsVisibleInTree() && b.IsEnabled)) != null,
                    "confirmation button on " + type.Name);
                if (completion.IsCompleted) break;
                pressed.Add(button!);
                button!.EmitSignal(NClickableControl.SignalName.Released, button);
            }
            await JourneyAcceptance.Wait(() => completion.IsCompleted && NOverlayStack.Instance?.Peek() != grid, "selection closed on " + type.Name);
            if (!(await completion).ToHashSet().SetEquals(picks))
                throw new InvalidDataException(type.Name + " returned different cards than the ones selected.");
            GD.Print($"[ReplayHarness] {type.Name} confirmed {string.Join(", ", picks.Select(c => c.Id))}");
        }
    }

    internal static async Task WatchAsync()
    {
        var witness = JsonSerializer.Deserialize<Campaign>(File.ReadAllText(Report("witness")))!;
        var record = ReplayStore.ListRuns().Single(r => r.Id == witness.Run);
        if (record.Result != witness.Result || record.Rooms.Count != witness.Rooms)
            throw new InvalidDataException("Campaign index differs from the completed run.");
        await WatchRoomsAsync(record, witness.Combats, Report("coverage"));
        GD.Print("REPLAY_CAMPAIGN_WATCH_PASS");
    }

    internal static async Task WatchRoomsAsync(RunInfo record, int expectedCombats, string coveragePath)
    {
        var results = new List<Coverage>();
        foreach (var room in record.Rooms)
        {
            var data = ReplayStore.LoadRoom(record.Id, room.Index);
            string mode = data.Combat != null ? "combat" : data.Checkpoint != null ? "room" : "journal";
            bool finale = data.Events.Any(e => e.Kind == "direct-event");
            try
            {
                if (data.Combat != null)
                    await ReplayPlayer.PlayAsync(data.Combat, 0, data.Events);
                else if (data.Checkpoint != null)
                    await ReplayPlayer.PlayRoomAsync(data.Checkpoint, data.Events);
                if (mode != "journal")
                {
                    ReplayPlayer.SetSpeed(8);
                    await JourneyAcceptance.Wait(() => ReplayPlayer.IsFinished, "campaign room playback " + room.Index, 105);
                }
                bool passed = mode != "journal" && !ReplayPlayer.HasError && (mode != "combat" || ReplayPlayer.IsVerified) &&
                    (!finale || ReplayPlayer.NativeVictory);
                results.Add(new Coverage(room.Index, room.Floor, room.Name, mode, data.Events.Count, passed, DateTimeOffset.UtcNow,
                    ReplayPlayer.NativeChoiceCount, ReplayPlayer.CaptionChoiceCount, ReplayPlayer.NativeVictory,
                    mode == "journal" ? "No native room checkpoint" : ReplayPlayer.Status));
            }
            catch (Exception error)
            {
                results.Add(new Coverage(room.Index, room.Floor, room.Name, mode, data.Events.Count, false, DateTimeOffset.UtcNow,
                    ReplayPlayer.NativeChoiceCount, ReplayPlayer.CaptionChoiceCount, ReplayPlayer.NativeVictory, error.Message));
            }
            finally
            {
                if (ReplayPlayer.IsActive) await ReplayPlayer.StopAsync();
                File.WriteAllText(coveragePath, JsonSerializer.Serialize(results, Json));
            }
        }
        if (results.Any(r => !r.Passed) || results.Count(r => r.Mode == "combat") != expectedCombats)
            throw new InvalidDataException("See " + coveragePath + " for divergent rooms.");
    }

    private sealed record Visit(int Act, int Floor, string Type, string? Model);
    private sealed record Campaign(string Run, string Result, string Setup, List<Visit> Visits, int Rooms,
        long RawBytes, long ArchiveBytes, int Entries, int Combats, int Checkpoints);
    private sealed record Coverage(int Index, int Floor, string Name, string Mode, int Events, bool Passed, DateTimeOffset CompletedUtc,
        int NativeChoices, int CaptionOnlyChoices, bool NativeVictory, string Detail);
}
