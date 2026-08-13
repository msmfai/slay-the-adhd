using System.Collections.Generic;
using Godot;

namespace PokaYokeSpire.Core;

/// <summary>
/// In-game tuning panel — a draggable, sliders-for-everything overlay on TOP of the game, gated behind
/// Config.LiveTuning (debug). Toggle with F9. Drag any slider and the change applies LIVE (each Set
/// bumps Tunables.Revision → features rebuild); "Save to source" writes the current values back to
/// tunables.json in the repo, which is also what the next build bakes in.
///
/// This deliberately captures its own input (it IS an interactive tool), so it is NOT routed through
/// Overlay/UiSafety — it lives in Core, gated entirely behind LiveTuning, and only covers its own rect
/// so gameplay input elsewhere is untouched. Everything is fail-open: any error is logged, never thrown.
/// </summary>
public partial class TuningPanel : CanvasLayer
{
    private Control _window = null!;
    private Label _status = null!;
    private bool _dragging;
    private Vector2 _dragOff;
    private readonly List<(Tunables.Knob k, HSlider s, Label v)> _rows = new();

    public override void _Ready()
    {
        try { Layer = 128; BuildUi(); _window.Visible = false; }
        catch (System.Exception e) { DebugLog.Error("TuningPanel._Ready", e); }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        try
        {
            if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F9 })
            {
                _window.Visible = !_window.Visible;
                if (_window.Visible) RefreshValues();
                GetViewport().SetInputAsHandled();
            }
        }
        catch (System.Exception ex) { DebugLog.Error("TuningPanel.key", ex); }
    }

    private void BuildUi()
    {
        var panel = new PanelContainer { Name = "Window", Position = new Vector2(40, 40) };
        panel.CustomMinimumSize = new Vector2(380, 0);
        _window = panel;
        AddChild(panel);

        var vb = new VBoxContainer();
        panel.AddChild(vb);

        // ── title bar (drag handle) ──
        var title = new HBoxContainer { Name = "TitleBar" };
        title.AddChild(new Label { Text = "Poka-Yoke Tuning  (F9)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var close = new Button { Text = "×" };
        close.Pressed += () => _window.Visible = false;
        title.AddChild(close);
        title.GuiInput += OnTitleInput;
        vb.AddChild(title);
        vb.AddChild(new HSeparator());

        // ── one slider row per knob ──
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 440), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var knobs = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(knobs);
        vb.AddChild(scroll);
        foreach (var k in Tunables.Knobs) knobs.AddChild(BuildKnobRow(k));

        // ── footer: save / randomize / reset + status ──
        vb.AddChild(new HSeparator());
        var footer = new HBoxContainer();
        var save = new Button { Text = "Save to source" };
        save.Pressed += OnSave;
        footer.AddChild(save);
        var rand = new Button { Text = "Randomize" };
        rand.Pressed += OnRandomize;
        footer.AddChild(rand);
        var reset = new Button { Text = "Reset" };
        reset.Pressed += OnReset;
        footer.AddChild(reset);
        _status = new Label { Text = "", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        footer.AddChild(_status);
        vb.AddChild(footer);
    }

    private Control BuildKnobRow(Tunables.Knob k)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = k.Path, CustomMinimumSize = new Vector2(200, 0) });

        var slider = new HSlider
        {
            MinValue = k.Min, MaxValue = k.Max, Step = k.Step, Value = k.Get(),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(120, 0),
        };
        var val = new Label { Text = k.Get().ToString("0.###"), CustomMinimumSize = new Vector2(48, 0) };
        slider.ValueChanged += d =>
        {
            try { k.Set((float)d); val.Text = ((float)d).ToString("0.###"); LiveTuning.RaiseReloaded(); }
            catch (System.Exception e) { DebugLog.Error($"knob {k.Path}", e); }
        };
        row.AddChild(slider);
        row.AddChild(val);
        _rows.Add((k, slider, val));
        return row;
    }

    private void RefreshValues()
    {
        foreach (var (k, s, v) in _rows) { s.SetValueNoSignal(k.Get()); v.Text = k.Get().ToString("0.###"); }
    }

    private void OnSave()
    {
        bool ok = Tunables.WriteTo(Tunables.DiskPath());
        _status.Text = ok ? "saved to source ✓" : "save failed (see log)";
    }

    /// Scramble every knob to a random value in its range — a quick visual check that each knob is
    /// actually wired to something. Does NOT save; use Reset to undo back to the saved source.
    private void OnRandomize()
    {
        foreach (var k in Tunables.Knobs)
        {
            float t = (float)GD.RandRange(0.0, 1.0);
            float v = k.Min + t * (k.Max - k.Min);
            if (k.Step > 0f) v = k.Min + Mathf.Round((v - k.Min) / k.Step) * k.Step;   // snap to step
            k.Set(v);
        }
        RefreshValues();
        LiveTuning.RaiseReloaded();
        _status.Text = "randomized (Reset to undo)";
    }

    /// Undo: restore every value to the saved source (tunables.json / baked-in).
    private void OnReset()
    {
        Tunables.ResetToSaved();
        RefreshValues();
        LiveTuning.RaiseReloaded();
        _status.Text = "reset to saved ✓";
    }

    private void OnTitleInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            _dragging = mb.Pressed;
            if (mb.Pressed) _dragOff = mb.GlobalPosition - _window.Position;
        }
        else if (e is InputEventMouseMotion mm && _dragging)
        {
            _window.Position = mm.GlobalPosition - _dragOff;
        }
    }
}
