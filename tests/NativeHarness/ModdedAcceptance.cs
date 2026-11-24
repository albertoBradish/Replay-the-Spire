using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class ModdedAcceptance
{
    private const string Seed = "CUSTOMREPLAY";
    private sealed record Proof(string Run, int Room, string Character, string Assembly, string ModdedCard,
        string NextCard, int NextTurn, string[] Checksums, JsonElement State);
    private static string Evidence => Path.Combine(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, "modded-character.json");

    private static CharacterModel Hero()
    {
        string key = System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST_CHARACTER")?.Trim()
            ?? throw new InvalidOperationException("Set SPIRE_REPLAY_TEST_CHARACTER to an installed character model ID.");
        var hero = ModelDb.AllCharacters.FirstOrDefault(c => string.Equals(c.Id.ToString(), key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Id.Entry, key, StringComparison.OrdinalIgnoreCase));
        hero ??= ModelDb.GetByIdOrNull<CharacterModel>(new ModelId("CHARACTER", key.Split('.', 2)[^1]));
        if (hero == null || hero.GetType().Assembly == typeof(CharacterModel).Assembly)
            throw new InvalidDataException($"External character {key} unavailable. Loaded characters: " + string.Join(", ", ModelDb.AllCharacters.Select(c => c.Id)));
        return hero;
    }

    internal static async Task RecordAsync(NGame game)
    {
        var hero = Hero();
        var previous = ReplayStore.ListRuns().Select(r => r.Id).ToHashSet();
        SaveManager.Instance.Progress.EnableFtues = false;
#if BETA
        var participant = Player.CreateForNewRun(hero, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
        var run = await game.StartNewSingleplayerRun(participant, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard, false);
#else
        var run = await game.StartNewSingleplayerRun(hero, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard);
#endif
        var player = run.Players.Single();
        if (!player.Deck.Cards.Select(c => c.Id).Order().SequenceEqual(hero.StartingDeck.Select(c => c.Id).Order()))
            throw new InvalidDataException("The character did not enter with its built-in starter deck.");
        var point = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
            .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
        await RunManager.Instance.EnterMapCoord(point.coord);
        await JourneyAcceptance.Wait(() => player.Creature.CombatState != null, "custom character combat entry");
        var combat = player.Creature.CombatState!;
        int previousTurn = 0, turn = 0;
        string? moddedCard = null, nextCard = null;
        while (nextCard == null && turn < 5)
        {
            await JourneyAcceptance.Wait(() => combat.RoundNumber > previousTurn &&
                player.PlayerCombatState is { Phase: PlayerTurnPhase.Play } state && state.Hand.Cards.Count > 0 &&
                !RunManager.Instance.ActionExecutor.IsPaused && !RunManager.Instance.ActionExecutor.IsRunning, "custom character player turn");
            turn = combat.RoundNumber;
            foreach (var card in player.PlayerCombatState!.Hand.Cards
                .OrderByDescending(c => c.GetType().Assembly != typeof(CardModel).Assembly)
                .ThenByDescending(c => moddedCard == null ? c.TargetType == TargetType.AnyEnemy : c.Id.ToString() != moddedCard))
            {
                if (moddedCard == null && card.GetType().Assembly == typeof(CardModel).Assembly) continue;
                Creature? target = card.TargetType == TargetType.AnyEnemy ? combat.HittableEnemies.FirstOrDefault() : null;
                if (!card.CanPlayTargeting(target)) continue;
                string id = card.Id.ToString();
                var play = new PlayCardAction(card, target);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(play);
                await JourneyAcceptance.Wait(() => play.CompletionTask.IsCompleted, "custom card " + id);
                await play.CompletionTask;
                if (moddedCard == null) moddedCard = id;
                else nextCard = id;
                break;
            }
            if (nextCard == null)
            {
                previousTurn = turn;
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            }
        }
        if (moddedCard == null || nextCard == null || turn <= 1)
            throw new InvalidDataException($"No legal external starter followed by a second input after an enemy turn: {hero.Id}.");
        await JourneyAcceptance.Wait(() => !RunManager.Instance.ActionExecutor.IsRunning, "custom card effects settle");
        var stateAtEnd = Signature(player);
        await game.ReturnToMainMenu();
        ReplayStore.Flush();
        var saved = ReplayStore.ListRuns().Single(r => r.Seed == Seed && !previous.Contains(r.Id));
        var room = saved.Rooms.Last(r => r.HasCombat);
        var data = ReplayStore.LoadRoom(saved.Id, room.Index);
        string[] checksums = Checksums(data.Combat!);
        if (checksums.Length < 2 || !data.Events.Any(e => e.Kind == "play" && e.Card == moddedCard && e.Turn < turn) ||
            !data.Events.Any(e => e.Kind == "play" && e.Card == nextCard && e.Turn == turn))
            throw new InvalidDataException("Missing external card, second-turn decision or native checksums.");
        var proof = new Proof(saved.Id, room.Index, hero.Id.ToString(), hero.GetType().Assembly.GetName().Name!,
            moddedCard, nextCard, turn, checksums, stateAtEnd);
        await File.WriteAllTextAsync(Evidence, JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"REPLAY_MODDED_RECORD_PASS {proof.Character} {moddedCard} -> T{turn} {nextCard} checksums={checksums.Length}");
    }

    internal static async Task WatchAsync()
    {
        var hero = Hero();
        var proof = JsonSerializer.Deserialize<Proof>(await File.ReadAllTextAsync(Evidence))!;
        var record = ReplayStore.ListRuns().Single(r => r.Id == proof.Run);
        if (hero.Id.ToString() != proof.Character || hero.GetType().Assembly.GetName().Name != proof.Assembly ||
            ReplayStore.CheckCompatibility(record) is not null)
            throw new InvalidDataException("The installed replay character or mod list changed.");
        var data = ReplayStore.LoadRoom(proof.Run, proof.Room);
        if (!Checksums(data.Combat!).SequenceEqual(proof.Checksums) ||
            !data.Events.Any(e => e.Kind == "play" && e.Card == proof.ModdedCard && e.Turn < proof.NextTurn) ||
            !data.Events.Any(e => e.Kind == "play" && e.Card == proof.NextCard && e.Turn == proof.NextTurn))
            throw new InvalidDataException("The journal or original engine checksums changed on reload.");
        double speed = ReplayPlayer.Speed;
        try
        {
            ReplayPlayer.SetSpeed(3);
            await ReplayPlayer.PlayAsync(data.Combat!, 0, data.Events);
            await JourneyAcceptance.Wait(() => ReplayPlayer.IsFinished, "custom character replay", 90);
            if (ReplayPlayer.HasError || !ReplayPlayer.IsVerified) throw new InvalidDataException(ReplayPlayer.Status);
            var actual = Signature(RunManager.Instance.DebugOnlyGetState()!.Players.Single());
            if (!JsonElement.DeepEquals(proof.State, actual))
                throw new InvalidDataException($"Custom character state mismatch. Expected {proof.State}, got {actual}.");
            GD.Print($"REPLAY_MODDED_WATCH_PASS {proof.Character} {proof.ModdedCard} checksums={proof.Checksums.Length} {ReplayPlayer.Status}");
        }
        finally
        {
            try { if (ReplayPlayer.IsActive) await ReplayPlayer.StopAsync(); }
            finally { ReplayPlayer.SetSpeed(speed); }
        }
    }

    private static string[] Checksums(byte[] combat)
    {
        var reader = new PacketReader();
        reader.Reset(combat);
        return reader.Read<CombatReplay>().checksumData.Select(c => $"{c.checksumData.id}:{c.checksumData.checksum:X8}").ToArray();
    }

    private static JsonElement Signature(Player player)
    {
        var state = player.PlayerCombatState!;
        static object CreatureState(Creature c) => new { c.CurrentHp, c.MaxHp, c.Block, model = c.Monster?.Id.ToString(),
            powers = c.Powers.Select(p => $"{p.Id}/{p.Amount}") };
        static string Card(CardModel c) => $"{c.Id}/{c.CurrentUpgradeLevel}@{NetCombatCard.FromModel(c).CombatCardIndex}";
        return JsonSerializer.SerializeToElement(new { character = player.Character.Id.ToString(), turn = player.Creature.CombatState!.RoundNumber,
            player = CreatureState(player.Creature), enemies = player.Creature.CombatState.Enemies.Select(CreatureState),
            player.Gold, state.Energy, relics = player.Relics.Select(r => r.Id.ToString()), orbs = state.OrbQueue.Orbs.Select(o => o.Id.ToString()),
            hand = state.Hand.Cards.Select(Card), discard = PileType.Discard.GetPile(player).Cards.Select(Card),
            draw = PileType.Draw.GetPile(player).Cards.Select(Card), exhaust = PileType.Exhaust.GetPile(player).Cards.Select(Card) });
    }
}
