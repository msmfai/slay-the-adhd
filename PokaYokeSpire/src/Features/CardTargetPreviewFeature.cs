using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;        // MegaRichTextLabel (renders [gold] etc.)
using MegaCrit.Sts2.Core.Entities.Creatures; // Creature
using MegaCrit.Sts2.Core.Models;             // CardModel
using MegaCrit.Sts2.Core.Nodes.Combat;       // NCardPlay, NCreature, NTargetManager
using PokaYokeSpire.Combat;

namespace PokaYokeSpire.Features;

/// <summary>
/// CARD TARGET PREVIEW — while targeting an enemy with a card, a cursor-following panel shows
/// the card's actual effect on THAT enemy (game's exact effect text incl. [gold] markup) plus
/// how much HP gets through its block.
///
/// Visibility is CORRECT BY CONSTRUCTION: OnCreatureHover/OnCreatureUnhover only set/clear the
/// (card, target); the panel's visibility is re-derived every frame from the live state
/// (NTargetManager.IsInSelection AND a target is set), so it can never get stuck if an unhover
/// event is missed — when targeting ends, IsInSelection goes false and it hides next frame.
/// </summary>
[HarmonyPatch(typeof(NCardPlay), "OnCreatureHover")]
internal static class CardTargetPreviewFeature
{
    private static void Postfix(NCardPlay __instance, NCreature creature)
    {
        try
        {
            if (!Config.ShowCardTargetPreview) { CardPreviewOverlay.SetTarget(null, null); return; }
            var card = Traverse.Create(__instance).Property("Card").GetValue<CardModel>();
            CardPreviewOverlay.SetTarget(card, creature?.Entity);
        }
        catch { CardPreviewOverlay.SetTarget(null, null); }
    }
}

[HarmonyPatch(typeof(NCardPlay), "OnCreatureUnhover")]
internal static class CardTargetPreviewHideFeature
{
    private static void Postfix() { try { CardPreviewOverlay.SetTarget(null, null); } catch { } }
}

internal static class CardPreviewOverlay
{
    private static CardModel? _card;
    private static Creature? _target;

    private static CanvasLayer? _layer;
    private static PanelContainer? _panel;
    private static MegaRichTextLabel? _effect;
    private static Label? _hp;
    private static bool _logged;

    /// Set (or clear) the current hovered target; recomputes + updates content when set.
    internal static void SetTarget(CardModel? card, Creature? target)
    {
        _card = card;
        _target = target;
        if (card == null || target == null) return;

        EnsureBuilt();
        if (_panel == null) return;
        var p = CardTargetPreview.Compute(card, target);
        _effect!.Text = p.EffectText ?? "";
        if (p.Damage <= 0) { _hp!.Text = ""; _hp.Visible = false; }
        else
        {
            _hp!.Visible = true;
            _hp.Text = p.FullyBlocked ? "→ blocked (0 HP)" : $"→ {p.HpThroughBlock} HP through block";
            _hp.AddThemeColorOverride("font_color", p.FullyBlocked ? new Color(0.5f, 0.85f, 1f) : new Color(1f, 0.45f, 0.45f));
        }
        if (!_logged) { _logged = true; MegaCrit.Sts2.Core.Logging.Log.Info($"[Poka-Yoke] feature8 CARD-PREVIEW: dmg={p.Damage} block={p.TargetBlock} hpThrough={p.HpThroughBlock}"); }
    }

    /// Called every frame by the follower: visibility is derived purely from live state.
    internal static void Tick()
    {
        if (_panel == null || !GodotObject.IsInstanceValid(_panel)) return;

        bool active = _card != null && _target != null && Config.ShowCardTargetPreview && IsTargeting();
        _panel.Visible = active;
        if (!active) return;

        // follow the cursor, flipping off screen edges
        Vector2 mouse = _panel.GetViewport().GetMousePosition();
        Vector2 size = _panel.Size;
        Vector2 vp = _panel.GetViewportRect().Size;
        float x = mouse.X + 24f, y = mouse.Y - size.Y - 16f;
        if (x + size.X > vp.X) x = mouse.X - size.X - 24f;
        if (y < 0) y = mouse.Y + 24f;
        _panel.Position = new Vector2(Mathf.Max(0, x), Mathf.Max(0, y));
    }

    private static bool IsTargeting()
    {
        try { return NTargetManager.Instance?.IsInSelection ?? false; } catch { return false; }
    }

    private static void EnsureBuilt()
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel)) return;

        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        _layer = new CanvasLayer { Layer = 128 };
        root.AddChild(_layer);

        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat { BgColor = new Color(0.05f, 0.05f, 0.08f, 0.93f) };
        style.SetContentMarginAll(10); style.SetCornerRadiusAll(8);
        style.SetBorderWidthAll(2); style.BorderColor = new Color(0.4f, 0.4f, 0.5f);
        _panel.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        vbox.AddThemeConstantOverride("separation", 4);

        // The game's rich-text label renders its custom tags ([gold], keywords, ...). It
        // asserts a "normal_font" override exists before _Ready, so set one first.
        _effect = new MegaRichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off, MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(220, 0),
        };
        // CRITICAL: MegaRichTextLabel auto-fits its font to its box every time Text is set;
        // in a FitContent container that box starts tiny and grows, so the text starts
        // comically small and creeps up each open. Turn auto-size off -> fixed 20px.
        _effect.AutoSizeEnabled = false;
        _effect.AddThemeFontOverride("normal_font", ThemeDB.Singleton.FallbackFont);
        _effect.AddThemeFontSizeOverride("normal_font_size", 20);

        _hp = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hp.AddThemeFontSizeOverride("font_size", 22);
        _hp.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0));
        _hp.AddThemeConstantOverride("outline_size", 5);

        vbox.AddChild(_effect);
        vbox.AddChild(_hp);
        _panel.AddChild(vbox);
        _panel.Visible = false;
        _layer.AddChild(_panel);

        _layer.AddChild(new PreviewFollower());
    }
}

/// Drives CardPreviewOverlay.Tick() every frame (visibility + follow).
internal partial class PreviewFollower : Node
{
    public override void _Process(double delta) { try { CardPreviewOverlay.Tick(); } catch { } }
}
