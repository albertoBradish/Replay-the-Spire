using Godot;
using static SpireReplay.Interface.ReplayStyle;

namespace SpireReplay.Playback;

internal sealed class DecisionCaption
{
    private readonly CanvasLayer layer = new() { Name = "ReplayDecisionCaption", Layer = 90 };
    private readonly Label heading = Text("", 16, true);
    private readonly Label choice = Text("", 25);

    internal DecisionCaption(SceneTree tree)
    {
        var panel = Panel(new Color("172029ed"), 14);
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        var rows = Column(2);
        rows.MouseFilter = Control.MouseFilterEnum.Ignore;
        rows.AddChild(heading);
        rows.AddChild(choice);
        panel.AddChild(rows);
        layer.AddChild(panel);
        tree.Root.AddChild(layer);
        panel.SetAnchorAndOffset(Side.Left, 0.04f, 0);
        panel.SetAnchorAndOffset(Side.Right, 0.47f, 0);
        panel.SetAnchorAndOffset(Side.Top, 0.77f, 0);
        panel.SetAnchorAndOffset(Side.Bottom, 0.77f, 0);
        choice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        choice.MaxLinesVisible = 3;
    }

    internal void Show(ReplayEvent entry, bool applied)
    {
        string title = entry.Kind switch
        {
            "map" => T("地图路线", "Map route"),
            "reward" when entry.ActionKey.StartsWith("pick:", StringComparison.Ordinal) => T("选择卡牌", "Card picked"),
            "reward" when entry.ActionKey is "skip-set" or "leave-set" => T("结束奖励选择", "Leave rewards"),
            "reward" => T("奖励选择", "Reward choice"),
            "shop" or "shop-open" => T("商店", "Merchant"),
            "camp" or "camp-open" => T("营地", "Rest site"),
            "event" => T("事件选择", "Event choice"),
            "event-resume" => T("返回事件", "Back to event"),
            "direct-event" => T("终局", "Finale"),
            "treasure" => T("宝箱", "Treasure"),
            _ => T("选择", "Decision")
        };
        heading.Text = applied ? title : title + T(" · 操作记录", " · Recorded choice");
        if (entry.DecisionSeconds is double seconds && double.IsFinite(seconds))
            heading.Text += $"    {seconds:0.#} s";
        choice.Text = entry.Label;
    }

    internal void Close()
    {
        layer.Hide();
        layer.QueueFree();
    }
}
