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
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class CharactersAcceptance
{
    // The same seed exercises an ordinary first-floor encounter for every starting deck.
    private const string Seed = "REPLAYPERF";
    private sealed record HeroProof(string RunId, int Room, int Turns, string Played,
        int EffectBefore, int EffectAfter, string[] NativeChecksums, JsonElement State);
    private static CharacterModel[] Heroes() => [ModelDb.Character<Ironclad>(), ModelDb.Character<Silent>(),
        ModelDb.Character<Defect>(), ModelDb.Character<Regent>(), ModelDb.Character<Necrobinder>()];
    private static string PathFor(CharacterModel hero) => Path.Combine(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!,
        $"characters-{hero.GetType().Name.ToLowerInvariant()}.json");
    private static Task Wait(Func<bool> predicate, string name) => JourneyAcceptance.Wait(predicate, name, 55);

    internal static async Task RecordAsync(NGame game)
    {
        SaveManager.Instance.Progress.EnableFtues = false;
        foreach (var hero in Heroes())
        {
            var existing = ReplayStore.ListRuns().Select(run => run.Id).ToHashSet();
#if BETA
            var participant = Player.CreateForNewRun(hero, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1);
            var run = await game.StartNewSingleplayerRun(participant, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard, false);
#else
            var run = await game.StartNewSingleplayerRun(hero, true, ActModel.GetDefaultList(), [], Seed, GameMode.Standard);
#endif
            var player = run.Players.Single();
            if (!hero.StartingDeck.Select(x => x.Id.ToString()).Order().SequenceEqual(player.Deck.Cards.Select(x => x.Id.ToString()).Order()))
                throw new InvalidDataException($"{hero.Id} entered with a different deck.");
            var map = run.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
                .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
            await RunManager.Instance.EnterMapCoord(map.coord);
            string played = "";
            int effectBefore = 0, effectAfter = 0, turns = 0;
            while (turns < 3)
            {
                await Wait(() => player.PlayerCombatState?.Phase == PlayerTurnPhase.Play &&
                    player.PlayerCombatState.Hand.Cards.Count > 0 && !RunManager.Instance.ActionExecutor.IsPaused, $"draw {hero.Id}");
                var combat = player.Creature.CombatState!;
                turns = combat.RoundNumber;
                var target = combat.HittableEnemies.OrderByDescending(enemy => enemy.CurrentHp).First();
                var hand = player.PlayerCombatState!.Hand;
                var signature = hand.Cards.FirstOrDefault(card => IsSignatureCard(hero, card) && card.CanPlay());
                if (played == "" && signature != null)
                {
                    effectBefore = Mechanic(player, target);
                    await Play(signature, target);
                    effectAfter = Mechanic(player, target);
                    if (effectAfter <= effectBefore) throw new InvalidDataException($"{signature.Id} did not change {hero.Id}'s expected mechanic.");
                    played = signature.Id.ToString();
                }
                var defend = hand.Cards.FirstOrDefault(card => card.Id.Entry.StartsWith("DEFEND_", StringComparison.Ordinal) && card.CanPlay());
                if (defend != null) await Play(defend, target);
                if ((turns >= 2 && played != "") || turns == 3) break;
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turns));
                await Wait(() => combat.RoundNumber > turns && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play, $"next turn {hero.Id}");
            }
            if (played == "") throw new InvalidDataException($"{hero.Id} did not draw its signature starter within three turns.");
            await Wait(() => !RunManager.Instance.ActionExecutor.IsRunning, $"actions settled {hero.Id}");
            var state = Signature(player);
            await game.ReturnToMainMenu();
            ReplayStore.Flush();
            var saved = ReplayStore.ListRuns().Single(r => !existing.Contains(r.Id) && r.Seed == Seed);
            var battle = saved.Rooms.Last(r => r.HasCombat);
            var room = ReplayStore.LoadRoom(saved.Id, battle.Index);
            var checksums = Checksums(room.Combat!);
            if (checksums.Length < turns || !room.Events.Any(e => e.Kind == "play" && e.Card == played))
                throw new InvalidDataException($"{hero.Id} archive lacks native checksums or its signature action.");
            var proof = new HeroProof(saved.Id, battle.Index, turns, played, effectBefore, effectAfter, checksums, state);
            File.WriteAllText(PathFor(hero), JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
            GD.Print($"[ReplayHarness] {hero.Id} {played}: {effectBefore}->{effectAfter}, {turns} turns, {checksums.Length} native checksums.");
        }
        GD.Print("REPLAY_CHARACTERS_RECORD_PASS");
    }

    internal static async Task WatchAsync()
    {
        double previousSpeed = ReplayPlayer.Speed;
        try
        {
            ReplayPlayer.SetSpeed(4);
            foreach (var hero in Heroes())
            {
                var proof = JsonSerializer.Deserialize<HeroProof>(File.ReadAllText(PathFor(hero)))!;
                var room = ReplayStore.LoadRoom(proof.RunId, proof.Room);
                if (!Checksums(room.Combat!).SequenceEqual(proof.NativeChecksums)) throw new InvalidDataException("Recorded checksums changed.");
                try
                {
                    await ReplayPlayer.PlayAsync(room.Combat!, 0, room.Events);
                    await Wait(() => ReplayPlayer.IsFinished || ReplayPlayer.HasError, $"play {hero.Id}");
                    if (!ReplayPlayer.IsVerified || ReplayPlayer.HasError) throw new InvalidDataException(ReplayPlayer.Status);
                    var player = RunManager.Instance.DebugOnlyGetState()!.Players.Single();
                    var actual = Signature(player);
                    if (!JsonElement.DeepEquals(proof.State, actual))
                        throw new InvalidDataException($"{hero.Id} state mismatch. Recorded {proof.State}; replayed {actual}.");
                    GD.Print($"[ReplayHarness] {hero.Id} {ReplayPlayer.Status}, complete state matched.");
                }
                finally { if (ReplayPlayer.IsActive) await ReplayPlayer.StopAsync(); }
            }
        }
        finally { ReplayPlayer.SetSpeed(previousSpeed); }
        GD.Print("REPLAY_CHARACTERS_WATCH_PASS");
    }

    private static Task Play(CardModel card, Creature enemy)
    {
        var action = new PlayCardAction(card, card.TargetType == TargetType.AnyEnemy ? enemy : null);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
        return Wait(() => action.CompletionTask.IsCompleted, card.Id.ToString());
    }

    private static bool IsSignatureCard(CharacterModel hero, CardModel card) => (hero, card) switch
    {
        (Ironclad, Bash) or (Silent, Neutralize) or (Defect, Zap) or (Regent, Venerate) or (Necrobinder, Bodyguard) => true,
        _ => false
    };

    private static int Mechanic(Player player, Creature enemy) => player.Character switch
    {
        Ironclad => enemy.GetPowerAmount<VulnerablePower>(),
        Silent => enemy.GetPowerAmount<WeakPower>(),
        Defect => player.PlayerCombatState!.OrbQueue.Orbs.Count,
        Regent => player.PlayerCombatState!.Stars,
        Necrobinder => player.Osty?.MaxHp ?? 0,
        _ => throw new InvalidOperationException(player.Character.Id.ToString())
    };

    private static string[] Checksums(byte[] bytes)
    {
        var reader = new PacketReader();
        reader.Reset(bytes);
        return reader.Read<CombatReplay>().checksumData.Select(c => $"{c.checksumData.id}:{c.checksumData.checksum:X8}").ToArray();
    }

    private static JsonElement Signature(Player player)
    {
        var combat = player.Creature.CombatState!;
        var state = player.PlayerCombatState!;
        static object CreatureState(Creature c) => new { id = c.Monster?.Id.ToString(), hp = c.CurrentHp, maxHp = c.MaxHp, c.Block,
            powers = c.Powers.Select(power => $"{power.Id}:{power.Amount}").ToArray() };
        static string CardState(CardModel card) => $"{card.Id}/{card.CurrentUpgradeLevel}@{NetCombatCard.FromModel(card).CombatCardIndex}";
        return JsonSerializer.SerializeToElement(new {
            hero = player.Character.Id.ToString(), turn = combat.RoundNumber, player = CreatureState(player.Creature),
            deck = player.Deck.Cards.Select(c => $"{c.Id}/{c.CurrentUpgradeLevel}"), player.Gold,
            relics = player.Relics.Select(r => r.Id.ToString()), potions = player.Potions.Select(p => p.Id.ToString()),
            state.Energy, state.Stars, orbs = state.OrbQueue.Orbs.Select(o => o.Id.ToString()),
            pets = state.Pets.Select(CreatureState), enemies = combat.Enemies.Select(CreatureState),
            hand = state.Hand.Cards.Select(CardState), draw = PileType.Draw.GetPile(player).Cards.Select(CardState),
            discard = PileType.Discard.GetPile(player).Cards.Select(CardState), exhaust = PileType.Exhaust.GetPile(player).Cards.Select(CardState)
        });
    }
}
