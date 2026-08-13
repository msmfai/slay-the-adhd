using System;
using System.Linq;

namespace PokaYokeSpire.Spatial;

/// <summary>
/// Faithful pure port of the game's default (non-scene) enemy placement — MegaCrit.Sts2.Core.Nodes.
/// Rooms.NCombatRoom.PositionEnemies + the _enemyContainer centring/aspect-fit in
/// AdjustCreatureScaleForAspectRatio. Lets the occlusion sweep compute real enemy rectangles for
/// every non-scene encounter (CLAUDE.md § "Spatial placement").
///
/// COORDINATE SPACE: the game's design resolution, 1920×1080 (NGame.devResolution). Enemies occupy
/// the RIGHT half: their feet-centre local x ∈ [150, 960] is placed inside _enemyContainer, which
/// sits at (960, 540) — so screen x = 960 + scale·localX (always right of centre; the 150px
/// "_centerSafeZone" is what keeps them clear of a centred HUD element).
///
/// ASSUMPTIONS (documented because they're NOT in the DLL — see the sweep's report): each enemy's
/// visual Bounds.Size lives in packed spine data, so the sweep passes a NOMINAL footprint; camera
/// scaling and SceneContainer.Size are taken as 1.0 and 1920×1080 (the common case).
/// </summary>
public static class EnemyFormation
{
    public const float CenterSafeZone = 150f;   // NCombatRoom._centerSafeZone
    public const float DefaultPadding = 70f;    // NCombatRoom._defaultPadding
    public const float BaselineY = 200f;        // NCombatRoom._yPos (feet anchor, container-local)
    public const float DevW = 1920f, DevH = 1080f;
    private static readonly (float, float) Container = (DevW * 0.5f, DevH * 0.5f); // (960,540)

    /// Faithful port of PositionEnemies: feet-centre LOCAL positions (inside _enemyContainer).
    public static (float x, float y)[] LocalPositions(float[] boundsX, float scaling = 1f)
    {
        int n = boundsX.Length;
        var outp = new (float, float)[Math.Max(n, 0)];
        if (n == 0) return outp;

        float num = 960f / scaling;
        float pad = DefaultPadding;
        float totalW = boundsX.Sum();
        float span = totalW + (n - 1) * pad;
        float val = MathF.Max((num - span) * 0.5f, 150f);
        float stagger = 0f;
        if (val + span > num && n > 1)
        {
            pad = MathF.Max((num - 150f - totalW) / (n - 1), 5f);
            span = totalW + (n - 1) * pad;
            val = (num - span) * 0.5f;
            if (pad < 30f) stagger = 60f + (40f - 60f) * ((pad - 5f) / 25f); // Lerp(60,40,(pad-5)/25)
        }
        for (int i = 0; i < n; i++)
        {
            outp[i] = (val + boundsX[i] * 0.5f, BaselineY - ((i % 2 != 0) ? stagger : 0f));
            val += boundsX[i] + pad;
        }
        return outp;
    }

    /// Enemy screen rectangles (feet at the anchor, sprite extending UP by boundsY), after applying
    /// the _enemyContainer centring + aspect-fit shrink. baseWidth is the room width the fit clamps
    /// to (NCombatRoom.Size.X ≈ DevW).
    public static UiRect[] ScreenRects(float[] boundsX, float[] boundsY, float scaling = 1f, float baseWidth = DevW)
    {
        var local = LocalPositions(boundsX, scaling);
        int n = local.Length;
        var rects = new UiRect[n];
        if (n == 0) return rects;

        // AdjustCreatureScaleForAspectRatio: shrink+shift the container if the rightmost creature
        // (at scale 1, container x = 960) would exceed the room width.
        float rightmost = 0f;
        for (int i = 0; i < n; i++)
            rightmost = MathF.Max(rightmost, Container.Item1 + local[i].x + boundsX[i] * 0.5f);
        float s = 1f, containerX = Container.Item1;
        if (rightmost > baseWidth)
        {
            s = baseWidth / rightmost;
            containerX = Container.Item1 - (rightmost - baseWidth) * s;
        }

        for (int i = 0; i < n; i++)
        {
            float feetX = containerX + s * local[i].x;
            float feetY = Container.Item2 + s * local[i].y;
            float w = s * boundsX[i], h = s * boundsY[i];
            rects[i] = new UiRect($"enemy{i}", feetX - w / 2f, feetY - h, w, h);
        }
        return rects;
    }
}
