using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using SpireReplay.Playback;
using static SpireReplay.Interface.ReplayStyle;

namespace SpireReplay.Interface;

public static class ReplayUi
{
    internal static ReplayBrowser? Browser { get; private set; }

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(NMainMenu), nameof(NMainMenu._Ready)),
            postfix: new HarmonyMethod(typeof(ReplayUi), nameof(AttachEntry)));
    }

    private static void AttachEntry(NMainMenu __instance)
    {
        if (__instance.HasNode("ReplayTheSpireEntry")) return;
        var menu = __instance;
        var button = Action(T("回放尖塔", "Replay the Spire"), () => Open(menu.GetTree()), true);
        Typeface(button, 22);
        button.Name = "ReplayTheSpireEntry";
        menu.AddChild(button);
        button.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        button.OffsetLeft = -112;
        button.OffsetRight = 112;
        button.OffsetTop = -66;
        button.OffsetBottom = -26;
        var display = menu.GetTree().Root;
        void SizeEntry()
        {
            button.Scale = Vector2.One * ReadabilityScale(display);
            button.PivotOffset = new Vector2(button.Size.X / 2, button.Size.Y);
        }
        display.SizeChanged += SizeEntry;
        button.TreeExiting += () => display.SizeChanged -= SizeEntry;
        SizeEntry();
        var nativeButtons = menu.GetNodeOrNull<Control>("MainMenuTextButtons");
        if (nativeButtons != null)
        {
            void Sync() => button.Visible = nativeButtons.Visible;
            nativeButtons.VisibilityChanged += Sync;
            Sync();
        }
    }

    internal static void Open(SceneTree tree, string? runId = null, int? roomIndex = null)
    {
        if (Browser != null && GodotObject.IsInstanceValid(Browser.Window))
        {
            Browser.Window.GrabFocus();
            return;
        }
        var next = new ReplayBrowser(tree, runId, roomIndex);
        Browser = next;
        next.Window.TreeExited += () => { if (Browser == next) Browser = null; };
        next.Show();
    }
}
