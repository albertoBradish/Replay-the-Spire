using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using SpireReplay;
using SpireReplay.Playback;

namespace ReplayHarness;

internal static class CourierAcceptance
{
    private const string Seed = "CRYSTALREPLAY";
    private static string Receipt => Path.Combine(System.Environment.GetEnvironmentVariable("SPIRE_REPLAY_TEST")!, "courier-result.txt");
    private static string Potions(Player player) => string.Join('|', player.PotionSlots.Select(p => p?.Id.ToString() ?? "-"));

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
        await CrystalSphereAcceptance.ReachActTwoEventAsync(run, player, ModelDb.Event<PotionCourier>());
        var model = ((EventRoom)run.CurrentRoom!).LocalMutableEvent;
        var choice = model.CurrentOptions.Select((value, index) => (value,index)).Single(o => o.value.TextKey.EndsWith("GRAB_POTIONS"));
        NEventRoom.Instance!.OptionButtonClicked(choice.value, choice.index);
        await JourneyAcceptance.Wait(() => NOverlayStack.Instance?.Peek() is NRewardsScreen, "courier reward list");
        var screen = (NRewardsScreen)NOverlayStack.Instance!.Peek()!;
        var buttons = screen.FindChildren("*", "", true, false).OfType<NRewardButton>().Where(b => b.Reward is PotionReward).ToArray();
        if (buttons.Length != 3) throw new InvalidDataException("Expected the three courier potions.");
        var before = Potions(player);
        await (Task)AccessTools.Method(typeof(NRewardButton), "GetReward").Invoke(buttons[1], null)!;
        screen.Call(NRewardsScreen.MethodName.OnProceedButtonPressed, screen.GetNode<NProceedButton>("ProceedButton"));
        await JourneyAcceptance.Wait(() => model.IsFinished, "leave remaining potions");
        var after = Potions(player);
        if (after == before) throw new InvalidDataException("Taking the potion did not update the inventory.");
        await game.ReturnToMainMenu();
        ReplayStore.Flush();
        var info = ReplayStore.ListRuns().First(r => r.Seed == Seed && r.Rooms.Any(x => ReplayStore.LoadEvents(r.Id,x.Index).Any(e => e.ActionKey == choice.value.TextKey)));
        var visit = info.Rooms.Single(x => ReplayStore.LoadEvents(info.Id,x.Index).Any(e => e.ActionKey == choice.value.TextKey));
        File.WriteAllLines(Receipt,[info.Id,visit.Index.ToString(),after]);
    }

    internal static async Task WatchAsync()
    {
        var receipt = File.ReadAllLines(Receipt);
        var data = ReplayStore.LoadRoom(receipt[0],int.Parse(receipt[1]));
        await ReplayPlayer.PlayRoomAsync(data.Checkpoint!,data.Events);
        ReplayPlayer.SetSpeed(4);
        await JourneyAcceptance.Wait(() => ReplayPlayer.IsFinished, "courier choices complete", 40);
        var run = RunManager.Instance.DebugOnlyGetState()!;
        if (ReplayPlayer.HasError || Potions(run.Players.Single()) != receipt[2] || run.CurrentRoom is not EventRoom room || !room.LocalMutableEvent.IsFinished)
            throw new InvalidDataException("Courier reward or event result diverged: " + ReplayPlayer.Status);
        await ReplayPlayer.StopAsync();
    }
}
