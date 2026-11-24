using System.IO;
using System.Security.Cryptography;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;

namespace SpireReplay.Capture;

internal static class CrystalBoard
{
    internal static string Fingerprint(CrystalSphereMinigame grid)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        var size = grid.GridSize;
        writer.Write(size.X);
        writer.Write(size.Y);
        writer.Write(grid.DivinationCount);
        writer.Write((int)grid.CrystalSphereTool);
        var items = new Dictionary<CrystalSphereItem, int>();
        writer.Write(grid.Items.Count);
        foreach (var item in grid.Items)
        {
            items.Add(item, items.Count);
            var data = item.ToSerializable();
            writer.Write((int)data.type);
            writer.Write((int)data.cardRarity);
            writer.Write((int)data.potionRarity);
            writer.Write(data.isBigGold);
            writer.Write(item.Position.X);
            writer.Write(item.Position.Y);
            writer.Write(item.Size.X);
            writer.Write(item.Size.Y);
        }
        for (int y = 0; y < size.Y; y++)
        {
            for (int x = 0; x < size.X; x++)
            {
                var cell = grid.cells[x, y];
                writer.Write(cell.IsHidden);
                writer.Write(cell.Item == null ? -1 : items[cell.Item]);
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }
}

internal static partial class CaptureHooks
{
    private static CrystalSphereMinigame? crystalGrid;
    private static bool crystalCanProceed;

    private static void InstallCrystalSphere(Action<Type, string, string, bool> hook)
    {
        hook(typeof(NCrystalSphereScreen), nameof(NCrystalSphereScreen.ShowScreen), nameof(CrystalOpened), false);
        hook(typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.SetTool), nameof(CrystalTool), true);
        hook(typeof(CrystalSphereMinigame), nameof(CrystalSphereMinigame.CellClicked), nameof(CrystalCell), true);
        hook(typeof(NCrystalSphereScreen), "OnMinigameFinished", nameof(CrystalFinished), false);
        hook(typeof(NCrystalSphereScreen), "OnProceedButtonPressed", nameof(CrystalProceed), true);
    }

    private static void CrystalOpened(CrystalSphereMinigame grid)
    {
        if (!Recording) return;
        Observe(() =>
        {
            crystalGrid = grid;
            crystalCanProceed = false;
            ReplayStore.Add("crystal-board", "水晶球棋盘", amount: grid.DivinationCount,
                actionKey: "open:" + CrystalBoard.Fingerprint(grid));
            RestartClock();
        });
    }

    private static void CrystalTool(CrystalSphereMinigame __instance, CrystalSphereMinigame.CrystalSphereToolType tool)
    {
        if (!Recording || __instance != crystalGrid) return;
        Observe(() =>
        {
            bool big = tool == CrystalSphereMinigame.CrystalSphereToolType.Big;
            ReplayStore.Add("crystal", $"选择{CrystalToolLabel(big)}", decisionSeconds: TakeTime(),
                actionKey: big ? "tool:big" : "tool:small");
        });
    }

    private static void CrystalCell(CrystalSphereMinigame __instance, CrystalSphereCell clickedCell)
    {
        if (!Recording || __instance != crystalGrid || __instance.IsFinished) return;
        Observe(() =>
        {
            bool big = __instance.CrystalSphereTool == CrystalSphereMinigame.CrystalSphereToolType.Big;
            var label = $"{CrystalToolLabel(big)} · 第 {clickedCell.Y + 1} 行、第 {clickedCell.X + 1} 列";
            ReplayStore.Add("crystal", label, amount: __instance.DivinationCount, decisionSeconds: TakeTime(),
                actionKey: big ? "cell:big" : "cell:small", actionIndex: clickedCell.Y * __instance.GridSize.X + clickedCell.X);
        });
    }

    private static string CrystalToolLabel(bool big) => new LocString("events", big
        ? "CRYSTAL_SPHERE.button.DIVINATION_LABEL_BIG" : "CRYSTAL_SPHERE.button.DIVINATION_LABEL_SMALL").GetFormattedText();

    private static void CrystalFinished(CrystalSphereMinigame ____entity)
    {
        if (!Recording || ____entity != crystalGrid) return;
        Observe(() =>
        {
            crystalCanProceed = true;
            ReplayStore.Add("crystal-board", "占卜结束", amount: ____entity.DivinationCount,
                actionKey: "finish:" + CrystalBoard.Fingerprint(____entity));
            RestartClock();
        });
    }

    private static void CrystalProceed(CrystalSphereMinigame ____entity)
    {
        if (Recording && ____entity == crystalGrid)
            Observe(() => ReplayStore.Add("crystal", "离开水晶球", decisionSeconds: TakeTime(), actionKey: "proceed"));
    }
}
