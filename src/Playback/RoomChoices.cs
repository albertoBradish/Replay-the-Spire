using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireReplay.Playback;

// Room checkpoints restore the original native presentation. Apply a decision
// only when both the recorded index and the durable option ID match that room.
internal sealed class RoomChoices(RunState run, IReadOnlyList<ReplayEvent> timeline, bool inEventBattle,
    Func<int, Action, string, Task> presentGrid, Func<Func<bool>, Task> wait, Func<double, Task> present)
{
    private readonly RewardChoices rewards = new();
    private readonly HashSet<int> appliedMarkers = [];
    private CrystalSpherePlayback? crystal;
    private Task? eventWithRewards;

    internal Task<bool> ApplyAsync(int index)
    {
        var entry = timeline[index];
        if (run.CurrentRoom is EventRoom { CanonicalEvent: CrystalSphere } sphere &&
            entry.Kind is "event" or "crystal" or "crystal-board" or "reward")
        {
            crystal ??= new CrystalSpherePlayback(sphere, timeline, rewards, wait, present);
            return crystal.ApplyAsync(index);
        }
        if (appliedMarkers.Contains(index))
            return Task.FromResult(true);
        if (entry.Kind == "direct-event")
            return Task.FromResult(run.CurrentRoom is EventRoom { CanonicalEvent: TheArchitect });
        if (entry.Kind == "event-resume")
            return ResumeAsync(entry.ActionKey);
        if (entry.Kind == "reward")
            return ApplyRewardAsync(entry, index + 1 < timeline.Count ? timeline[index + 1] : null);
        if (entry.Kind == "map" && NMapScreen.Instance is { } map)
        {
            string[] coordinate = entry.ActionKey.Split(',');
            if (coordinate.Length == 2 && int.TryParse(coordinate[0], out int column) &&
                int.TryParse(coordinate[1], out int row))
            {
                map.Open();
                map.SetTravelEnabled(false);
                var point = new MapCoord(column, row);
                Ping(map, point, run.Players[0]);
                return Task.FromResult(true);
            }
        }
        if (entry.Kind == "treasure" && run.CurrentRoom is TreasureRoom && NRun.Instance?.TreasureRoom is { } chest &&
            entry.ActionIndex is int relic && relic >= -1 && !timeline.Any(e => e.Kind == "reward"))
            return OpenTreasureAsync(chest, relic, entry.ActionKey);
        if (entry.Kind is "camp-open" or "shop-open")
        {
            int completed = index + 1;
            while (completed < timeline.Count && timeline[completed].Kind == "choice") completed++;
            if (completed >= timeline.Count || timeline[completed].Kind + "-open" != entry.Kind ||
                timeline[completed].ActionKey != entry.ActionKey || timeline[completed].ActionIndex != entry.ActionIndex)
                return Task.FromResult(false);
            return ApplyWithChildrenAsync(completed, index + 1, completed, markCompleted: true);
        }
        if (entry.Kind is not ("event" or "camp" or "shop"))
            return Task.FromResult(false);
        int start = index, end = index;
        if (entry.Kind == "event")
        {
            start = end = index + 1;
            while (end < timeline.Count && timeline[end].Kind == "choice") end++;
        }
        else
            while (start > 0 && timeline[start - 1].Kind == "choice") start--;
        return ApplyWithChildrenAsync(index, start, end, markCompleted: false);
    }

    internal static void Ping(NMapScreen map, MapCoord point, Player player)
    {
        var ping = AccessTools.Method(typeof(NMapScreen), "PingMapCoord", [typeof(MapCoord), typeof(Player)]);
        if (ping != null)
            ping.Invoke(map, [point, player]);
        else
            AccessTools.Method(typeof(NMapScreen), "PingMapCoord", [typeof(Player), typeof(MapCoord)])
                ?.Invoke(map, [player, point]);
    }

    private async Task<bool> ApplyRewardAsync(ReplayEvent entry, ReplayEvent? next)
    {
        if (eventWithRewards != null && entry.ActionKey is "skip-set" or "leave-set" &&
            NOverlayStack.Instance?.Peek() is NRewardsScreen screen)
        {
            screen.Call(NRewardsScreen.MethodName.OnProceedButtonPressed, screen.GetNode<NProceedButton>("ProceedButton"));
            var pending = eventWithRewards;
            await wait(() => pending.IsCompleted || RewardChoices.Visible && NOverlayStack.Instance?.Peek() != screen);
            if (pending.IsCompleted)
            {
                await pending;
                eventWithRewards = null;
            }
            return true;
        }
        return await rewards.ApplyAsync(entry, next);
    }

    private async Task<bool> OpenTreasureAsync(NTreasureRoom chest, int index, string key)
    {
        var picking = RunManager.Instance.TreasureRoomRelicSynchronizer;
        if (picking.CurrentRelics is not { } relics || index >= relics.Count ||
            (index == -1 ? key != "skip" : relics[index].Id.ToString() != key))
            return false;
        var open = typeof(NTreasureRoom).GetMethod("OpenChest",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (open?.Invoke(chest, null) is not Task animation)
            return false;
        var active = AccessTools.Field(typeof(NTreasureRoom), "_isRelicCollectionOpen");
        var tree = chest.GetTree();
        while (GodotObject.IsInstanceValid(chest) && chest.IsInsideTree() && active?.GetValue(chest) is not true)
        {
            if (animation.IsCompleted)
            {
                await animation;
                return false;
            }
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        if (!GodotObject.IsInstanceValid(chest) || !chest.IsInsideTree())
            return false;
        if (index == -1)
        {
            // The native skip button has a delayed enable task tied to this room.
            // Allow it to finish before the next room can release the button.
            var proceed = chest.GetNode<NProceedButton>("%ProceedButton");
            await wait(() => proceed.IsEnabled && proceed.IsSkip);
            picking.OnPicked(run.Players[0], null);
            _ = animation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return true;
        }
        picking.OnPicked(run.Players[0], index);
        await animation;
        return true;
    }

    private async Task<bool> ApplyOptionAsync(ReplayEvent entry)
    {
        if (entry.ActionIndex is not int option || option < 0 || string.IsNullOrEmpty(entry.ActionKey))
            return false;
        switch (entry.Kind, run.CurrentRoom)
        {
            case ("event", EventRoom room):
                var model = room.LocalMutableEvent;
                if (option >= model.CurrentOptions.Count ||
                    model.CanonicalEncounter != null && !inEventBattle && model is not TheArchitect)
                    return false;
                var eventOption = model.CurrentOptions[option];
                if (eventOption.TextKey != entry.ActionKey || eventOption.IsLocked || eventOption.IsProceed && model is not TheArchitect ||
                    NEventRoom.Instance == null)
                    return false;
                NEventRoom.Instance.OptionButtonClicked(eventOption, option);
                var pending = RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks();
                await wait(() => pending.IsCompleted || RewardChoices.Visible);
                if (pending.IsCompleted) await pending;
                else
                {
                    eventWithRewards = pending;
                    _ = pending.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }
                return true;
            case ("camp", RestSiteRoom room):
                if (option >= room.Options.Count || room.Options[option].OptionId != entry.ActionKey ||
                    !room.Options[option].IsEnabled)
                    return false;
                var selected = room.Options[option];
                var button = NRestSiteRoom.Instance?.GetButtonForOption(selected);
                if (button == null || AccessTools.Method(button.GetType(), "SelectOption")?.Invoke(button, [selected]) is not Task applying)
                    return false;
                await applying;
                return true;
            case ("shop", MerchantRoom):
            case ("shop", EventRoom { LocalMutableEvent: FakeMerchant }):
                MerchantInventory? inventory;
                if (run.CurrentRoom is MerchantRoom shop)
                {
                    NMerchantRoom.Instance?.OpenInventory();
                    object owner = AccessTools.Property(typeof(RunManager), "MerchantSynchronizer")
                        ?.GetValue(RunManager.Instance) ?? shop;
                    inventory = AccessTools.Method(owner.GetType(), "GetLocalInventory")?.Invoke(owner, null) as MerchantInventory;
                }
                else
                {
                    var vendor = NEventRoom.Instance?.FindChildren("*", "", true, false).OfType<NFakeMerchant>().FirstOrDefault();
                    if (vendor == null) return false;
                    vendor.Call(NFakeMerchant.MethodName.OpenInventory);
                    inventory = vendor.Inventory.Inventory;
                }
                if (inventory == null)
                    return false;
                var item = inventory.AllEntries.ElementAtOrDefault(option);
                string? itemId = item switch
                {
                    MerchantCardEntry card => card.CreationResult?.Card.Id.ToString(),
                    MerchantRelicEntry relic => relic.Model?.Id.ToString(),
                    MerchantPotionEntry potion => potion.Model?.Id.ToString(),
                    MerchantCardRemovalEntry => "remove",
                    _ => null
                };
                if (item == null || itemId != entry.ActionKey || !item.IsStocked || !item.EnoughGold)
                    return false;
                var purchase = typeof(MerchantEntry).GetMethod("OnTryPurchaseUnsynchronized")
                    ?? typeof(MerchantEntry).GetMethod("OnTryPurchaseWrapper");
                return purchase?.Invoke(item, [inventory, false]) is Task<bool> completed && await completed;
            default:
                return false;
        }
    }

    private async Task<bool> ResumeAsync(string key)
    {
        if (run.CurrentRoom is CombatRoom { IsPreFinished: true, ShouldResumeParentEventAfterCombat: true } combat &&
            combat.ParentEventId?.ToString() == key)
        {
            var proceed = typeof(RunManager).GetMethod("ProceedFromTerminalRewardsScreenAsync") ??
                typeof(RunManager).GetMethod(nameof(RunManager.ProceedFromTerminalRewardsScreen));
            if (proceed?.Invoke(RunManager.Instance, null) is Task transition)
                await transition;
        }
        if (run.CurrentRoom is not EventRoom room || room.ModelId.ToString() != key)
            throw new InvalidDataException("战斗结束后的事件与记录不同。");
        return true;
    }

    private async Task<bool> ApplyWithChildrenAsync(int index, int start, int end, bool markCompleted)
    {
        var answers = new List<RoomChoiceAnswers.Entry>(end - start);
        for (int child = start; child < end; child++) answers.Add(new(child, timeline[child].ActionKey));
        bool applied = await RoomChoiceAnswers.ApplyAsync(answers, presentGrid, () => ApplyOptionAsync(timeline[index]));
        if (applied)
        {
            for (int child = start; child < end; child++) appliedMarkers.Add(child);
            if (markCompleted) appliedMarkers.Add(index);
        }
        return applied;
    }
}
