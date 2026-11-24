using System.IO.Compression;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;

namespace ReplayHarness;

internal static class OrdinaryAcceptance
{
    private const string Seed = "ORDINARYREPLAY";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string Report(string name) => Path.Combine(
        System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, $"ordinary-{name}.json");

    internal static async Task RecordAsync(NGame game)
    {
        double speed = Engine.TimeScale;
        var route = new List<Visit>();
        try
        {
            Engine.TimeScale = 6;
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
            var startingDeck = player.Deck.Cards.Select(c => c.Id.ToString()).ToArray();
            int startingHp = player.Creature.CurrentHp, startingGold = player.Gold;
            if (startingHp != 80 || player.Creature.MaxHp != 80 || startingGold != hero.StartingGold ||
                !startingDeck.Order().SequenceEqual(hero.StartingDeck.Select(c => c.Id.ToString()).Order()) ||
                player.Deck.Cards.Any(c => c.CurrentUpgradeLevel != 0) ||
                !player.Relics.Select(r => r.Id).SequenceEqual(hero.StartingRelics.Select(r => r.Id)))
                throw new InvalidDataException("The ironclad did not begin with the standard loadout.");

            var driver = new CampaignAcceptance.CampaignDriver(run, player, ordinary: true);
            AbstractRoom? previous = null;
            RunInfo? result = null;
            for (int i = 0; i < 650 && result == null; i++)
            {
                await JourneyAcceptance.Wait(() => run.CurrentRoom != null, "ordinary room entry");
                var room = run.CurrentRoom!;
                if (previous != room)
                {
                    route.Add(new Visit(run.CurrentActIndex + 1, run.TotalFloor, room.RoomType.ToString(), room.ModelId?.ToString()));
                    GD.Print($"[ReplayHarness] ordinary act {run.CurrentActIndex + 1} floor {run.TotalFloor} {room.ModelId}, {player.Creature.CurrentHp} HP");
                    previous = room;
                }
                bool victory = await driver.VisitAsync(room);
                if (!victory && !player.Creature.IsDead) continue;
                var expected = victory ? "胜利" : "战败";
                await JourneyAcceptance.Wait(() => (result = ReplayStore.ListRuns().SingleOrDefault(r =>
                    !existing.Contains(r.Id) && r.Seed == Seed && r.Result == expected)) != null, "native " + expected);
            }
            if (result == null) throw new InvalidDataException("Ordinary run did not reach a natural outcome.");
            string archive = Path.Combine(ReplayStore.ExportsDirectory, result.Id + ".spireplay");
            await JourneyAcceptance.Wait(() => File.Exists(archive), "natural outcome archive");
            var rooms = result.Rooms.Select(r => MeasureRoom(result, r)).ToArray();
            var library = new DirectoryInfo(Path.Combine(ReplayStore.LibraryDirectory, result.Id));
            using var zip = ZipFile.OpenRead(archive);
            var witness = new Witness(result.Id, result.Build, result.Seed, result.Result, startingHp, startingGold,
                startingDeck, route, route[^1].Act, route[^1].Floor, rooms, result.Rooms.Count(r => r.HasCombat), rooms.Sum(r => r.PlayerTurns),
                rooms.Sum(r => r.CombatCommands), rooms.Sum(r => r.CardsPlayed), rooms.Sum(r => r.Events),
                rooms.Sum(r => r.Checksums.Length), library.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length),
                zip.Entries.Sum(e => e.Length), new FileInfo(archive).Length);
            File.WriteAllText(Report("witness"), JsonSerializer.Serialize(witness, Json));
            GD.Print("REPLAY_ORDINARY_RECORD_PASS");
        }
        catch
        {
            File.WriteAllText(Report("partial-route"), JsonSerializer.Serialize(route, Json));
            throw;
        }
        finally { Engine.TimeScale = speed; }
    }

    // Use only directly resolving cards. Reward and shop offers still come from the native pools.
    private static int Rank(CardModel card) => card switch
    {
        Uppercut or IronWave => 0,
        PommelStrike or TwinStrike or PerfectedStrike or Rampage or Anger or ShrugItOff or FlameBarrier => 1,
        Bash => 2,
        StrikeIronclad or DefendIronclad => 3,
        _ => int.MaxValue
    };

    internal static IEnumerable<CardModel> ChoosePlay(Player player, IEnumerable<CardModel> hand)
    {
        var combat = player.Creature.CombatState!;
        var enemies = combat.HittableEnemies.OrderBy(e => e.CurrentHp).ToArray();
        var playable = hand.Where(c => Rank(c) != int.MaxValue && c.CanPlay()).OrderBy(Rank).ToArray();
        var attack = enemies.Length == 0 ? null : playable.FirstOrDefault(c => c.TargetType == TargetType.AnyEnemy);
        var defend = playable.FirstOrDefault(c => c.Type == CardType.Skill && c.TargetType == TargetType.Self);
        int incoming = enemies.Sum(e => e.Monster!.NextMove.Intents.OfType<AttackIntent>()
            .Sum(i => i.GetTotalDamage([player.Creature], e)));
        bool guard = enemies.Length > 0 && enemies[0].CurrentHp > 6 &&
            incoming > player.Creature.Block && player.Creature.Block < 10;
        var selected = guard ? defend ?? attack : attack ?? defend;
        return selected == null ? [] : [selected];
    }

    internal static CardModel? ChooseReward(Player player, IEnumerable<CardModel> cards) =>
        cards.Where(c => Rank(c) < 2 && player.Deck.Cards.Count(d => d.Id == c.Id) < 2).OrderBy(Rank).FirstOrDefault();

    internal static async Task ShopAsync(MerchantRoom room)
    {
        await JourneyAcceptance.Wait(() => NMerchantRoom.Instance != null && NMapScreen.Instance!.IsTravelEnabled, "ordinary merchant ready");
        var view = NMerchantRoom.Instance!;
        view.OpenInventory();
        var owner = AccessTools.Property(typeof(RunManager), "MerchantSynchronizer")?.GetValue(RunManager.Instance) ?? room;
        var stock = (MerchantInventory)AccessTools.Method(owner.GetType(), "GetLocalInventory").Invoke(owner, null)!;
        var offer = stock.AllEntries.OfType<MerchantCardEntry>().Where(e => e.IsStocked && e.EnoughGold &&
            e.CreationResult is { } result && Rank(result.Card) < 2).OrderBy(e => Rank(e.CreationResult!.Card)).FirstOrDefault();
        if (offer != null)
        {
            var buy = AccessTools.Method(typeof(MerchantEntry), "OnTryPurchaseUnsynchronized")
                ?? AccessTools.Method(typeof(MerchantEntry), "OnTryPurchaseWrapper");
            if (!await (Task<bool>)buy.Invoke(offer, [stock, false])!) throw new InvalidDataException("Ordinary shop purchase failed.");
        }
        AccessTools.Method(view.Inventory.GetType(), "Close").Invoke(view.Inventory, null);
    }

    internal static async Task WatchAsync()
    {
        var witness = JsonSerializer.Deserialize<Witness>(File.ReadAllText(Report("witness")))!;
        var run = ReplayStore.ListRuns().Single(r => r.Id == witness.Run);
        if (run.Result != witness.Result || run.Seed != witness.Seed || run.Rooms.Count != witness.Rooms.Length)
            throw new InvalidDataException("Ordinary run identity or result differs from the original.");
        foreach (var room in run.Rooms)
        {
            var original = witness.Rooms[room.Index];
            var stored = MeasureRoom(run, room);
            if (stored.Events != original.Events || !stored.Checksums.SequenceEqual(original.Checksums))
                throw new InvalidDataException("Original room witness changed at index " + room.Index);
        }
        int fights = run.Rooms.Count(r => r.HasCombat);
        if (fights < 2) throw new InvalidDataException("Ordinary sample did not reach two fights.");
        await CampaignAcceptance.WatchRoomsAsync(run, fights, Report("coverage"));
        GD.Print("REPLAY_ORDINARY_WATCH_PASS");
    }

    private static Room MeasureRoom(RunInfo run, RoomInfo room)
    {
        var data = ReplayStore.LoadRoom(run.Id, room.Index);
        string[] sums = [];
        if (data.Combat != null)
        {
            var reader = new PacketReader();
            reader.Reset(data.Combat);
            sums = reader.Read<CombatReplay>().checksumData.Select(c => $"{c.checksumData.id}:{c.checksumData.checksum:X8}").ToArray();
        }
        return new Room(room.Index, room.Floor, room.Name, data.Events.Count, data.Events.Count(e => e.Kind == "turn"),
            data.Events.Count(e => e.Kind == "choice" && e.ActionKey == "combat"), data.Events.Count(e => e.Kind == "play"), sums);
    }

    private sealed record Visit(int Act, int Floor, string Type, string? Model);
    private sealed record Room(int Index, int Floor, string Name, int Events, int PlayerTurns, int CombatCommands,
        int CardsPlayed, string[] Checksums);
    private sealed record Witness(string Run, string Build, string Seed, string Result, int StartingHp, int StartingGold,
        string[] StartingDeck, List<Visit> Route, int FinalAct, int FinalFloor, Room[] Rooms, int CombatRooms, int PlayerTurns, int CombatCommands, int CardsPlayed,
        int Events, int Checksums, long RawBytes, long ArchiveUncompressedBytes, long ArchiveBytes);
}
