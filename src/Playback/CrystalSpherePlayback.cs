using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpireReplay.Capture;
using Tool = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame.CrystalSphereToolType;

namespace SpireReplay.Playback;

// Selecting the event option starts an async native task which remains open through all
// the cell clicks and any rewards. Each recorded input has its own playback boundary.
internal sealed class CrystalSpherePlayback(EventRoom room, IReadOnlyList<ReplayEvent> timeline,
    RewardChoices rewards, Func<Func<bool>, Task> wait, Func<double, Task> present)
{
    private NCrystalSphereScreen? screen;
    private CrystalSphereMinigame? board;
    private Task? optionTask;
    private bool verifiedFinish;

    internal async Task<bool> ApplyAsync(int index)
    {
        var entry = timeline[index];
        if (entry.Kind == "event") return await OpenAsync(index, entry);
        if (screen == null || board == null || optionTask == null) return false;
        if (entry.Kind == "reward")
            return await RewardAsync(entry, index + 1 < timeline.Count ? timeline[index + 1] : null);
        if (entry.Kind == "crystal-board")
        {
            if (entry.ActionKey.StartsWith("open:", StringComparison.Ordinal))
                return VerifyBoard(entry, "open:");
            if (!entry.ActionKey.StartsWith("finish:", StringComparison.Ordinal)) return false;
            await wait(() => optionTask.IsCompleted);
            await optionTask;
            verifiedFinish = VerifyBoard(entry, "finish:");
            return verifiedFinish;
        }
        if (entry.ActionKey == "proceed")
        {
            if (!verifiedFinish) throw new InvalidDataException("水晶球结算记录不完整。");
            var proceed = screen.GetNode<NProceedButton>("%ProceedButton");
            await wait(() => NOverlayStack.Instance?.Peek() == screen && proceed.IsEnabled);
            screen.Call(NCrystalSphereScreen.MethodName.OnProceedButtonPressed, proceed);
            return true;
        }
        if (NOverlayStack.Instance?.Peek() != screen)
            throw new InvalidDataException("水晶球棋盘未处于可操作状态。");
        var tool = entry.ActionKey switch
        {
            "tool:small" or "cell:small" => Tool.Small,
            "tool:big" or "cell:big" => Tool.Big,
            _ => Tool.None
        };
        if (tool == Tool.None) return false;
        if (entry.ActionKey.StartsWith("tool:", StringComparison.Ordinal))
        {
            SetTool(tool);
            return true;
        }
        if (board.IsFinished || entry.Amount != board.DivinationCount || entry.ActionIndex is not int cell ||
            cell < 0 || cell >= board.GridSize.X * board.GridSize.Y)
            throw new InvalidDataException("水晶球的格子或剩余占卜次数与记录不同。");
        var entity = board.cells[cell % board.GridSize.X, cell / board.GridSize.X];
        var node = screen.GetNode<Control>("%Cells").GetChildren().OfType<NCrystalSphereCell>()
            .SingleOrDefault(n => ReferenceEquals(n.Entity, entity));
        if (node == null) throw new InvalidDataException("记录中的水晶球格子无法翻开。");
        if (board.CrystalSphereTool != tool) SetTool(tool);
        board.SetHoveredCell(entity);
        await present(0.65);
        var click = (Task)AccessTools.Method(typeof(NCrystalSphereScreen), "OnCellClicked").Invoke(screen, [node])!;
        await wait(() => click.IsCompleted);
        await click;
        board.UnsetHoveredCell();
        if (entity.IsHidden || board.DivinationCount != entry.Amount - 1)
            throw new InvalidDataException("水晶球翻格结果与记录不同。");
        return true;
    }

    private async Task<bool> OpenAsync(int index, ReplayEvent option)
    {
        var remaining = timeline.Skip(index + 1).ToArray();
        var header = remaining.FirstOrDefault(e => e.Kind == "crystal-board");
        if (header == null || !header.ActionKey.StartsWith("open:", StringComparison.Ordinal)) return false;
        var cells = remaining.Where(e => e.Kind == "crystal" && e.ActionKey.StartsWith("cell:", StringComparison.Ordinal)).ToArray();
        bool terminal = remaining.Any(e => e.Kind == "map" ||
            e.Kind == "crystal-board" && e.ActionKey.StartsWith("finish:", StringComparison.Ordinal) || e.Kind == "crystal" && e.ActionKey == "proceed");
        if (terminal && (cells.Length != header.Amount ||
            !remaining.Any(e => e.Kind == "crystal-board" && e.ActionKey.StartsWith("finish:", StringComparison.Ordinal)))) return false;
        var options = room.LocalMutableEvent.CurrentOptions;
        if (option.ActionIndex is not int selected || (uint)selected >= options.Count ||
            options[selected].TextKey != option.ActionKey || options[selected].IsLocked || optionTask != null) return false;
        NEventRoom.Instance!.OptionButtonClicked(options[selected], selected);
        optionTask = RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks();
        await wait(() => NOverlayStack.Instance?.Peek() is NCrystalSphereScreen || optionTask.IsCompleted);
        screen = NOverlayStack.Instance?.Peek() as NCrystalSphereScreen;
        if (screen == null)
        {
            await optionTask;
            throw new InvalidDataException("原生水晶球棋盘未打开。");
        }
        board = (CrystalSphereMinigame)AccessTools.Field(typeof(NCrystalSphereScreen), "_entity").GetValue(screen)!;
        await wait(() => screen.GetNode<Control>("%Items").IsVisibleInTree());
        return true;
    }

    private void SetTool(Tool tool)
    {
        var button = tool == Tool.Big ? "%BigDivinationButton" : "%SmallDivinationButton";
        var method = tool == Tool.Big ? NCrystalSphereScreen.MethodName.SetBigDivination : NCrystalSphereScreen.MethodName.SetSmallDivination;
        screen!.Call(method, screen.GetNode<Control>(button));
        if (board!.CrystalSphereTool != tool) throw new InvalidDataException("水晶球占卜工具与记录不同。");
    }

    private bool VerifyBoard(ReplayEvent entry, string prefix)
    {
        if (board!.DivinationCount != entry.Amount || entry.ActionKey != prefix + CrystalBoard.Fingerprint(board))
            throw new InvalidDataException("水晶球棋盘与原局的状态校验不同。");
        return true;
    }

    private async Task<bool> RewardAsync(ReplayEvent entry, ReplayEvent? next)
    {
        if (entry.ActionKey.StartsWith("open:", StringComparison.Ordinal) || entry.ActionKey is "skip-set" or "leave-set")
            await wait(() => RewardChoices.Visible || optionTask!.IsCompleted);
        if (NOverlayStack.Instance?.Peek() is NRewardsScreen picker && entry.ActionKey is "skip-set" or "leave-set")
        {
            // This is the sphere's nonterminal reward overlay. Its native proceed closes
            // the overlay as well as resolving the reward set.
            picker.Call(NRewardsScreen.MethodName.OnProceedButtonPressed, picker.GetNode<NProceedButton>("ProceedButton"));
            return true;
        }
        return await rewards.ApplyAsync(entry, next);
    }
}
