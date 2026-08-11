using Godot;
using MegaCrit.Sts2.Core.Nodes.Combat; // NEnergyCounter
using PokaYokeSpire.Features;

namespace PokaYokeSpire;

/// <summary>
/// Ctrl(or Cmd)+click-drag ANY UI control to move it. Lives at the scene-tree root and
/// watches raw input. Hovering the energy counter (or any child of it) drags the whole
/// counter — so its overlays move with it. Consumes the ctrl-click so the underlying
/// control (relic, etc.) doesn't also react.
/// </summary>
public partial class DragHandler : Node
{
    internal static Control? Dragged;
    private Vector2 _grabOffset;

    public override void _Input(InputEvent e)
    {
        if (!Config.EnableUiDragging) { Dragged = null; return; }

        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            bool mod = Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Meta);
            if (mb.Pressed && mod)
            {
                Dragged = ResolveTarget(GetViewport().GuiGetHoveredControl());
                if (Dragged != null)
                {
                    _grabOffset = Dragged.GlobalPosition - mb.GlobalPosition;
                    GetViewport().SetInputAsHandled(); // don't also click the control
                }
            }
            else if (!mb.Pressed)
            {
                Dragged = null;
            }
        }
        else if (e is InputEventMouseMotion mm && Dragged != null && GodotObject.IsInstanceValid(Dragged))
        {
            Dragged.GlobalPosition = mm.GlobalPosition + _grabOffset;
            GetViewport().SetInputAsHandled();
        }
    }

    // Hovering the energy counter or any descendant -> drag the whole counter cluster.
    private static Control? ResolveTarget(Control? hovered)
    {
        if (hovered == null) return null;
        var energy = EnergyCounterFeature.Instance;
        if (energy != null && GodotObject.IsInstanceValid(energy))
            for (Node? n = hovered; n != null; n = n.GetParent())
                if (ReferenceEquals(n, energy)) return energy;
        return hovered;
    }
}
