using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace SpireReplay.Interface;

internal static class ReplayStyle
{
    internal static readonly Color Ink = new("151c23");
    internal static readonly Color Slate = new("26333e");
    internal static readonly Color Raised = new("334450");
    internal static readonly Color Line = new("465664");
    internal static readonly Color Paper = new("f1eadc");
    internal static readonly Color Muted = new("bdc6ca");
    internal static readonly Color Gold = new("eac17b");
    private static readonly Font Font = new SystemFont
    {
        FontNames = ["Microsoft YaHei UI", "PingFang SC", "Noto Sans CJK SC", "DejaVu Sans"],
    };

    internal static string T(string zh, string en) => LocManager.Instance?.Language is "zhs" or "zht" ? zh : en;

    // Game controls are laid out in a 1920-wide canvas. Preserve text and target
    // sizes when it is displayed in a smaller window.
    internal static float ReadabilityScale(Window root)
    {
        var canvas = root.GetVisibleRect().Size;
        var display = root.Size;
        if (display.X <= 0 || display.Y <= 0) return 1;
        return Mathf.Clamp(Mathf.Min(canvas.X / display.X, canvas.Y / display.Y), 1, 3);
    }

    internal static StyleBoxFlat Box(Color fill, Color edge, int insetX = 12, int insetY = 6)
    {
        var box = new StyleBoxFlat { BgColor = fill, BorderColor = edge };
        box.SetCornerRadiusAll(6);
        box.SetBorderWidthAll(1);
        box.ContentMarginLeft = box.ContentMarginRight = insetX;
        box.ContentMarginTop = box.ContentMarginBottom = insetY;
        return box;
    }

    internal static void Typeface(Control control, int size = 16)
    {
        control.AddThemeFontOverride("font", Font);
        control.AddThemeFontSizeOverride("font_size", size);
        control.AddThemeColorOverride("font_color", Paper);
    }

    internal static void StyleButton(Button button, bool accent = false)
    {
        Typeface(button);
        button.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        button.AddThemeStyleboxOverride("normal", Box(accent ? new Color("4d4030") : Slate, accent ? Gold : Line));
        button.AddThemeStyleboxOverride("hover", Box(Raised, Gold));
        button.AddThemeStyleboxOverride("pressed", Box(Ink, Gold));
        button.AddThemeStyleboxOverride("disabled", Box(Ink, Line));
        var focus = Box(Colors.Transparent, Gold);
        focus.SetBorderWidthAll(2);
        button.AddThemeStyleboxOverride("focus", focus);
        button.AddThemeColorOverride("font_disabled_color", new Color("909ba1"));
        button.AddThemeColorOverride("font_hover_color", Paper);
        button.CustomMinimumSize = new Vector2(0, 34);
    }

    internal static Button Action(string text, Action action, bool accent = false)
    {
        var button = new Button { Text = text };
        StyleButton(button, accent);
        button.Pressed += action;
        return button;
    }

    internal static Label Text(string text, int size = 16, bool muted = false)
    {
        var label = new Label
        {
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        Typeface(label, size);
        label.AddThemeColorOverride("font_color", muted ? Muted : Paper);
        return label;
    }

    internal static VBoxContainer Column(int gap = 8)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", gap);
        return box;
    }

    internal static HBoxContainer Row(int gap = 8)
    {
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", gap);
        return box;
    }

    internal static PanelContainer Panel(Color fill, int inset = 12)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Box(fill, Line, inset, inset));
        return panel;
    }

    internal static void Expand(Control control)
    {
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        control.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
    }

    internal static void Clear(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }
}
