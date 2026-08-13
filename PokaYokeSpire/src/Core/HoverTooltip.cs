using System;
using Godot;

namespace PokaYokeSpire.Core;

/// <summary>
/// A custom hover tooltip we fully control — the game runs its own tooltip system, so Godot's built-in
/// Control.TooltipText doesn't surface for our overlay gems (hovering them falls through to the energy
/// counter's game tooltip). This shows our own little panel on mouse-enter, above the hovered node.
///
/// Bind a control with <see cref="Bind"/>; it sets the node to Pass (so it receives hover without eating
/// clicks) and shows/hides a shared panel on enter/exit. One panel node, added once on a high layer.
/// Fully fail-open.
/// </summary>
public static class HoverTooltip
{
    private static HoverTooltipLayer? _layer;

    private static void Ensure(Node any)
    {
        try
        {
            if (_layer != null && GodotObject.IsInstanceValid(_layer)) return;
            var root = any.GetTree()?.Root;
            if (root == null) return;
            if (root.GetNodeOrNull("PokaYokeTooltip") is HoverTooltipLayer existing) { _layer = existing; return; }
            _layer = new HoverTooltipLayer { Name = "PokaYokeTooltip" };
            root.AddChild(_layer);
        }
        catch (Exception e) { DebugLog.Error("HoverTooltip.Ensure", e); }
    }

    /// Make <paramref name="node"/> show <paramref name="text"/> on hover (text is re-evaluated each
    /// time, so it can be dynamic). Idempotent-ish: safe to call once per built node.
    public static void Bind(Control node, Func<string> text)
    {
        try
        {
            if (node == null || !GodotObject.IsInstanceValid(node)) return;
            node.MouseFilter = Control.MouseFilterEnum.Pass;   // receive hover, never consume a click
            node.MouseEntered += () =>
            {
                try
                {
                    Ensure(node);
                    _layer?.Show(text(), node.GetGlobalRect());
                    if (DebugLog.Enabled) DebugLog.Debug($"tooltip hover: {node.Name}");
                }
                catch (Exception e) { DebugLog.Error("HoverTooltip.enter", e); }
            };
            node.MouseExited += () => { try { _layer?.HideTip(); } catch { } };
        }
        catch (Exception e) { DebugLog.Error("HoverTooltip.Bind", e); }
    }
}

/// The shared panel node (a high CanvasLayer so it draws over everything), mouse-transparent.
public partial class HoverTooltipLayer : CanvasLayer
{
    private PanelContainer _panel = null!;
    private Label _label = null!;

    public override void _Ready()
    {
        Layer = 130;
        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        _label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Left };
        _label.AddThemeColorOverride("font_color", Colors.White);
        var bg = new StyleBoxFlat { BgColor = new Color(0.05f, 0.06f, 0.09f, 0.94f) };
        bg.SetContentMarginAll(0);
        bg.SetCornerRadiusAll(6);
        bg.BorderWidthBottom = bg.BorderWidthTop = bg.BorderWidthLeft = bg.BorderWidthRight = 1;
        bg.BorderColor = new Color(1f, 1f, 1f, 0.25f);
        _panel.AddThemeStyleboxOverride("panel", bg);
        margin.AddChild(_label);
        _panel.AddChild(margin);
        AddChild(_panel);
        _panel.Visible = false;
    }

    public void Show(string text, Rect2 anchorGlobalRect)
    {
        _label.Text = text;
        _panel.Visible = true;
        // size isn't known until laid out; position on the next frame, above the anchor, clamped on-screen
        Callable.From(() =>
        {
            try
            {
                if (!GodotObject.IsInstanceValid(_panel) || !_panel.Visible) return;
                Vector2 vp = _panel.GetViewportRect().Size;
                Vector2 size = _panel.Size;
                float x = anchorGlobalRect.Position.X + anchorGlobalRect.Size.X * 0.5f - size.X * 0.5f;   // centred over anchor
                float y = anchorGlobalRect.Position.Y - size.Y - 8f;                                      // above it
                if (y < 4f) y = anchorGlobalRect.Position.Y + anchorGlobalRect.Size.Y + 8f;               // flip below if clipped
                x = Mathf.Clamp(x, 4f, Mathf.Max(4f, vp.X - size.X - 4f));
                _panel.Position = new Vector2(x, y);
            }
            catch { }
        }).CallDeferred();
    }

    public void HideTip() { if (GodotObject.IsInstanceValid(_panel)) _panel.Visible = false; }
}
