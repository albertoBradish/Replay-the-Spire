using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class FacilityAcceptance
{
    private const string Seed = "CAMPSTORECHEST";
    private sealed record Stop(int Room, string Kind, string State);
    private static string Expected => Path.Combine(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, "facility-states.json");

    internal static async Task RecordAsync(NGame game)
    {
        double speed = Engine.TimeScale;
        try
        {
            Engine.TimeScale = 3;
            SaveManager.Instance.Progress.EnableFtues = false;
            var hero = ModelDb.Character<Ironclad>();
#if BETA
            var avatar = Player.CreateForNewRun(hero, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
            var run = await game.StartNewSingleplayerRun(avatar, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard, false);
#else
            var run = await game.StartNewSingleplayerRun(hero, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard);
#endif
            var player = run.Players.Single();
            await CreatureCmd.SetCurrentHp(player.Creature, 24);
            await PlayerCmd.GainGold(350, player);
            var results = new List<Stop>();
            foreach (var type in new[] { MapPointType.RestSite, MapPointType.Shop, MapPointType.Treasure })
            {
                var point = run.Map.GetAllMapPoints().Where(p => p.PointType == type)
                    .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
                await RunManager.Instance.EnterMapCoord(point.coord);
                var kind = type == MapPointType.RestSite ? "camp" : type == MapPointType.Shop ? "shop" : "treasure";
                string before = Signature(player);
                if (run.CurrentRoom is RestSiteRoom camp)
                {
                    int option = camp.Options.ToList().FindIndex(x => x.OptionId == "HEAL" && x.IsEnabled);
                    if (option < 0 || !await RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(option))
                        throw new InvalidDataException("Could not rest at the campfire.");
                }
                else if (run.CurrentRoom is MerchantRoom merchant)
                {
                    NMerchantRoom.Instance!.OpenInventory();
                    var owner = AccessTools.Property(typeof(RunManager), "MerchantSynchronizer")?.GetValue(RunManager.Instance) ?? merchant;
                    var stock = (MerchantInventory)AccessTools.Method(owner.GetType(), "GetLocalInventory").Invoke(owner, null)!;
                    var offer = stock.AllEntries.OfType<MerchantCardEntry>().First(o => o.IsStocked && o.EnoughGold);
                    var buy = AccessTools.Method(typeof(MerchantEntry), "OnTryPurchaseUnsynchronized")
                        ?? AccessTools.Method(typeof(MerchantEntry), "OnTryPurchaseWrapper");
                    if (!await (Task<bool>)buy.Invoke(offer, [stock, false])!) throw new InvalidDataException("Shop purchase failed.");
                }
                else if (run.CurrentRoom is TreasureRoom)
                {
                    var chest = NRun.Instance!.TreasureRoom!;
                    var open = (Task)AccessTools.Method(typeof(NTreasureRoom), "OpenChest").Invoke(chest, null)!;
                    var picking = RunManager.Instance.TreasureRoomRelicSynchronizer;
                    var visible = AccessTools.Field(typeof(NTreasureRoom), "_isRelicCollectionOpen");
                    await Wait(() => (bool)visible.GetValue(chest)! && picking.CurrentRelics?.Count > 0, "open relic chest");
                    picking.PickRelicLocally(0);
                    await open;
                }
                else throw new InvalidDataException("Unexpected room " + run.CurrentRoom?.GetType().Name);
                var saved = ReplayStore.ListRuns().First(r => r.Seed == Seed);
                var room = saved.Rooms.Last();
                var events = ReplayStore.LoadEvents(saved.Id, room.Index);
                if (Signature(player) == before || !room.HasCheckpoint || !events.Any(e => e.Kind == kind))
                    throw new InvalidDataException("Missing recorded state or decision for " + kind);
                results.Add(new Stop(room.Index, kind, Signature(player)));
            }
            await game.ReturnToMainMenu();
            File.WriteAllText(Expected, JsonSerializer.Serialize(results));
            GD.Print("REPLAY_FACILITIES_RECORD_PASS");
        }
        finally { Engine.TimeScale = speed; }
    }

    internal static async Task WatchAsync()
    {
        var info = ReplayStore.ListRuns().First(r => r.Seed == Seed);
        foreach (var expected in JsonSerializer.Deserialize<Stop[]>(File.ReadAllText(Expected))!)
        {
            var data = ReplayStore.LoadRoom(info.Id, expected.Room);
            await ReplayPlayer.PlayRoomAsync(data.Checkpoint!, data.Events);
            ReplayPlayer.SetSpeed(4);
            await Wait(() => ReplayPlayer.IsFinished, expected.Kind);
            string actual = Signature(RunManager.Instance.DebugOnlyGetState()!.Players.Single());
            if (ReplayPlayer.HasError || actual != expected.State)
                throw new InvalidDataException(expected.Kind + " expected " + expected.State + " observed " + actual + " / " + ReplayPlayer.Status);
            await ReplayPlayer.StopAsync();
        }
        GD.Print("REPLAY_FACILITIES_WATCH_PASS");
    }

    private static string Signature(Player p) => string.Join("|", p.Creature.CurrentHp, p.Creature.MaxHp, p.Gold,
        string.Join(",", p.Deck.Cards.Select(c => c.Id + ":" + c.CurrentUpgradeLevel)),
        string.Join(",", p.Relics.Select(r => r.Id.ToString())), string.Join(",", p.Potions.Select(i => i.Id.ToString())));

    private static async Task Wait(Func<bool> ready, string name)
    {
        var tree = NGame.Instance!.GetTree();
        ulong start = Time.GetTicksMsec();
        while (!ready())
        {
            if (Time.GetTicksMsec() - start > 35000) throw new TimeoutException(name + " " + ReplayPlayer.Status);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }
}
