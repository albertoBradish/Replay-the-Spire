using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireReplay.Playback;

// Room checkpoints restore the original native presentation. Apply a decision
// only when both the recorded index and the durable option ID match that room.
internal sealed class RoomChoices(RunState run, IReadOnlyList<ReplayEvent> timeline)
{
    internal Task<bool> ApplyAsync(int index)
    {
        var entry = timeline[index];
        if (entry.Kind == "map" && NMapScreen.Instance is { } map)
        {
            string[] coordinate = entry.ActionKey.Split(',');
            if (coordinate.Length == 2 && int.TryParse(coordinate[0], out int column) &&
                int.TryParse(coordinate[1], out int row))
            {
                map.Open();
                map.SetTravelEnabled(false);
                var point = new MapCoord(column, row);
                var ping = AccessTools.Method(typeof(NMapScreen), "PingMapCoord", [typeof(MapCoord), typeof(Player)]);
                if (ping != null)
                    ping.Invoke(map, [point, run.Players[0]]);
                else
                    AccessTools.Method(typeof(NMapScreen), "PingMapCoord", [typeof(Player), typeof(MapCoord)])
                        ?.Invoke(map, [run.Players[0], point]);
                return Task.FromResult(true);
            }
        }
        if (entry.Kind == "treasure" && run.CurrentRoom is TreasureRoom && NRun.Instance?.TreasureRoom is { } chest &&
            entry.ActionIndex is int relic && relic >= 0 && !timeline.Any(e => e.Kind == "reward"))
            return OpenTreasureAsync(chest, relic, entry.ActionKey);
        if (entry.Kind is not ("event" or "camp" or "shop"))
            return Task.FromResult(false);
        var children = ChildSelections(index);
        return RoomChoiceAnswers.ApplyAsync(children, () => ApplyOptionAsync(entry));
    }

    private async Task<bool> OpenTreasureAsync(NTreasureRoom chest, int index, string key)
    {
        var picking = RunManager.Instance.TreasureRoomRelicSynchronizer;
        if (picking.CurrentRelics is not { } relics || index >= relics.Count || relics[index].Id.ToString() != key)
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
                if (option >= model.CurrentOptions.Count || model.CanonicalEncounter != null)
                    return false;
                var eventOption = model.CurrentOptions[option];
                if (eventOption.TextKey != entry.ActionKey || eventOption.IsLocked || eventOption.IsProceed ||
                    NEventRoom.Instance == null)
                    return false;
                NEventRoom.Instance.OptionButtonClicked(eventOption, option);
                await RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks();
                return true;
            case ("camp", RestSiteRoom room):
                if (option >= room.Options.Count || room.Options[option].OptionId != entry.ActionKey ||
                    !room.Options[option].IsEnabled)
                    return false;
                return await RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(option);
            case ("shop", MerchantRoom room):
                NMerchantRoom.Instance?.OpenInventory();
                // The beta moved the local inventory to MerchantSynchronizer.
                object owner = AccessTools.Property(typeof(RunManager), "MerchantSynchronizer")
                    ?.GetValue(RunManager.Instance) ?? room;
                var inventory = AccessTools.Method(owner.GetType(), "GetLocalInventory")
                    ?.Invoke(owner, null) as MerchantInventory;
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

    private List<string> ChildSelections(int index)
    {
        var answers = new List<string>();
        if (timeline[index].Kind == "event")
        {
            for (int i = index + 1; i < timeline.Count && timeline[i].Kind == "choice"; i++)
                answers.Add(timeline[i].ActionKey);
        }
        else
        {
            for (int i = index - 1; i >= 0 && timeline[i].Kind == "choice"; i--)
                answers.Add(timeline[i].ActionKey);
            answers.Reverse();
        }
        return answers;
    }
}
