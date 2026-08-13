using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;

namespace PokaYokeSpire.Core;

/// <summary>
/// The mod's little tuning ENGINE. All tunable layout/appearance values live in tunables.json, which is
/// embedded into the DLL at build — so it is authoritative at COMPILE TIME and, in release, parsed ONCE
/// at load into fixed values (no per-frame or disk IO: effectively hardcoded for the session).
///
/// With <see cref="Config.LiveTuning"/> ON (a debug switch), the on-disk tunables.json (the repo source)
/// is polled and any change is hot-applied, so appearance/layout can be tuned WITHOUT recompiling or
/// relaunching. <see cref="Revision"/> bumps on every (re)load so features know to rebuild.
///
/// Parsing is tolerant: a missing/!invalid key keeps the current value, and any parse error is logged
/// and swallowed — bad JSON can never break the mod.
/// </summary>
public static class Tunables
{
    // ── gem (combat orbs) ──
    public static float GemFontFrac { get; private set; } = 0.20f;
    public static float GemGap { get; private set; } = 14f;
    public static float GemScale { get; private set; } = 2f / 3f;
    public static Color GemBlueTint { get; private set; } = new(0.18f, 0.42f, 1.8f);
    public static Color GemRedTint { get; private set; } = new(1.9f, 0.5f, 0.28f);
    public static string IncomingTip { get; private set; } = "Incoming damage";
    public static string OffenseTip { get; private set; } = "Your damage";

    // ── hud raise ──
    public static float HandRaiseFrac { get; private set; } = 0.5f;
    public static float CounterRaiseFracOfHand { get; private set; } = 1f / 3f;

    // ── energy counter ──
    public static float EnergyHeightFrac { get; private set; } = 1.03f;   // fraction of screen height

    // ── radial relics ──
    public static float RadialRadius { get; private set; } = 140f;

    // ── reward screen + deck fan ──
    public static float RewardRaise { get; private set; } = 64f;            // reward choices lifted (px)
    public static float DeckRaiseFracOfReward { get; private set; } = 0.5f; // deck fan lifts by this × RewardRaise
    public static float DeckFanGap { get; private set; } = 14f;             // clear gap between fully-spread cards
    public static float DeckMinVisibleStep { get; private set; } = 26f;     // min sliver of an overlapped card

    // ── deck-stats side panels ──
    public static float SidePanelMarginFrac { get; private set; } = 0.02f;  // panels this far off each screen edge

    // ── card target preview ──
    public static float CardPreviewOffsetX { get; private set; } = 24f;
    public static float CardPreviewOffsetY { get; private set; } = 16f;

    /// Bumped on every successful (re)load OR live edit; features compare it to know when to rebuild.
    public static int Revision { get; private set; }
    private static void Bump() => Revision++;

    // ── knob registry: the numeric tunables as (path, range, get, set) so a UI can build sliders and
    //    save generically. Colours are exposed as three r/g/b knobs; text (tooltips) stays JSON-only. ──
    public sealed class Knob
    {
        public string Path = "";
        public float Min, Max, Step;
        public Func<float> Get = () => 0f;
        public Action<float> Set = _ => { };
    }

    public static readonly System.Collections.Generic.List<Knob> Knobs = new()
    {
        new() { Path = "gem.fontFrac", Min = 0.05f, Max = 0.60f, Step = 0.005f, Get = () => GemFontFrac, Set = v => { GemFontFrac = v; Bump(); } },
        new() { Path = "gem.gap",      Min = 0f,    Max = 60f,   Step = 0.5f,   Get = () => GemGap,      Set = v => { GemGap = v; Bump(); } },
        new() { Path = "gem.scale",    Min = 0.20f, Max = 1.20f, Step = 0.01f,  Get = () => GemScale,    Set = v => { GemScale = v; Bump(); } },
        new() { Path = "gem.blueTint.r", Min = 0f, Max = 2f, Step = 0.02f, Get = () => GemBlueTint.R, Set = v => { GemBlueTint = new Color(v, GemBlueTint.G, GemBlueTint.B); Bump(); } },
        new() { Path = "gem.blueTint.g", Min = 0f, Max = 2f, Step = 0.02f, Get = () => GemBlueTint.G, Set = v => { GemBlueTint = new Color(GemBlueTint.R, v, GemBlueTint.B); Bump(); } },
        new() { Path = "gem.blueTint.b", Min = 0f, Max = 2f, Step = 0.02f, Get = () => GemBlueTint.B, Set = v => { GemBlueTint = new Color(GemBlueTint.R, GemBlueTint.G, v); Bump(); } },
        new() { Path = "gem.redTint.r",  Min = 0f, Max = 2f, Step = 0.02f, Get = () => GemRedTint.R,  Set = v => { GemRedTint = new Color(v, GemRedTint.G, GemRedTint.B); Bump(); } },
        new() { Path = "gem.redTint.g",  Min = 0f, Max = 2f, Step = 0.02f, Get = () => GemRedTint.G,  Set = v => { GemRedTint = new Color(GemRedTint.R, v, GemRedTint.B); Bump(); } },
        new() { Path = "gem.redTint.b",  Min = 0f, Max = 2f, Step = 0.02f, Get = () => GemRedTint.B,  Set = v => { GemRedTint = new Color(GemRedTint.R, GemRedTint.G, v); Bump(); } },
        new() { Path = "hud.handRaiseFrac",           Min = 0f, Max = 2f, Step = 0.02f, Get = () => HandRaiseFrac,           Set = v => { HandRaiseFrac = v; Bump(); } },
        new() { Path = "hud.counterRaiseFracOfHand",  Min = 0f, Max = 1f, Step = 0.01f, Get = () => CounterRaiseFracOfHand,  Set = v => { CounterRaiseFracOfHand = v; Bump(); } },
        new() { Path = "energy.heightFrac",           Min = 0.40f, Max = 1.20f, Step = 0.01f, Get = () => EnergyHeightFrac,        Set = v => { EnergyHeightFrac = v; Bump(); } },
        new() { Path = "radial.radius",               Min = 60f,   Max = 320f,  Step = 5f,    Get = () => RadialRadius,            Set = v => { RadialRadius = v; Bump(); } },
        new() { Path = "reward.rewardRaise",          Min = 0f,    Max = 160f,  Step = 2f,    Get = () => RewardRaise,             Set = v => { RewardRaise = v; Bump(); } },
        new() { Path = "reward.deckRaiseFracOfReward",Min = 0f,    Max = 1f,    Step = 0.05f, Get = () => DeckRaiseFracOfReward,    Set = v => { DeckRaiseFracOfReward = v; Bump(); } },
        new() { Path = "deckFan.gap",                 Min = 0f,    Max = 40f,   Step = 1f,    Get = () => DeckFanGap,              Set = v => { DeckFanGap = v; Bump(); } },
        new() { Path = "deckFan.minVisibleStep",      Min = 8f,    Max = 60f,   Step = 1f,    Get = () => DeckMinVisibleStep,      Set = v => { DeckMinVisibleStep = v; Bump(); } },
        new() { Path = "sidePanel.marginFrac",        Min = 0f,    Max = 0.10f, Step = 0.005f,Get = () => SidePanelMarginFrac,     Set = v => { SidePanelMarginFrac = v; Bump(); } },
        new() { Path = "cardPreview.cursorOffsetX",   Min = -80f,  Max = 80f,   Step = 2f,    Get = () => CardPreviewOffsetX,      Set = v => { CardPreviewOffsetX = v; Bump(); } },
        new() { Path = "cardPreview.cursorOffsetY",   Min = -80f,  Max = 80f,   Step = 2f,    Get = () => CardPreviewOffsetY,      Set = v => { CardPreviewOffsetY = v; Bump(); } },
    };

    /// Serialize the CURRENT values back to the tunables.json schema (indented, so it stays readable in
    /// source). Text tooltips are included so a save never drops them.
    public static string ToJson()
    {
        var gem = new System.Collections.Generic.Dictionary<string, object>
        {
            ["fontFrac"] = Round(GemFontFrac), ["gap"] = Round(GemGap), ["scale"] = Round(GemScale),
            ["blueTint"] = new[] { Round(GemBlueTint.R), Round(GemBlueTint.G), Round(GemBlueTint.B) },
            ["redTint"] = new[] { Round(GemRedTint.R), Round(GemRedTint.G), Round(GemRedTint.B) },
            ["incomingTip"] = IncomingTip, ["offenseTip"] = OffenseTip,
        };
        var hud = new System.Collections.Generic.Dictionary<string, object>
        {
            ["handRaiseFrac"] = Round(HandRaiseFrac), ["counterRaiseFracOfHand"] = Round(CounterRaiseFracOfHand),
        };
        var root = new System.Collections.Generic.Dictionary<string, object>
        {
            ["gem"] = gem,
            ["hud"] = hud,
            ["energy"] = new System.Collections.Generic.Dictionary<string, object> { ["heightFrac"] = Round(EnergyHeightFrac) },
            ["radial"] = new System.Collections.Generic.Dictionary<string, object> { ["radius"] = Round(RadialRadius) },
            ["reward"] = new System.Collections.Generic.Dictionary<string, object>
            {
                ["rewardRaise"] = Round(RewardRaise), ["deckRaiseFracOfReward"] = Round(DeckRaiseFracOfReward),
            },
            ["deckFan"] = new System.Collections.Generic.Dictionary<string, object>
            {
                ["gap"] = Round(DeckFanGap), ["minVisibleStep"] = Round(DeckMinVisibleStep),
            },
            ["sidePanel"] = new System.Collections.Generic.Dictionary<string, object> { ["marginFrac"] = Round(SidePanelMarginFrac) },
            ["cardPreview"] = new System.Collections.Generic.Dictionary<string, object>
            {
                ["cursorOffsetX"] = Round(CardPreviewOffsetX), ["cursorOffsetY"] = Round(CardPreviewOffsetY),
            },
        };
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    private static double Round(float v) => System.Math.Round(v, 4);

    /// Write the current values to <paramref name="path"/> (the panel passes <see cref="DiskPath"/> so
    /// Save persists to the repo source). Never throws.
    public static bool WriteTo(string path)
    {
        try
        {
            File.WriteAllText(path, ToJson());
            // adopt our own write as the last-seen state so the disk poller doesn't immediately reload it
            _lastWrite = File.GetLastWriteTimeUtc(path);
            DebugLog.Info($"Tunables saved to {path}");
            return true;
        }
        catch (Exception e) { DebugLog.Error("Tunables.WriteTo", e); return false; }
    }

    static Tunables()
    {
        var baked = EmbeddedJson();
        if (baked != null) Apply(baked);   // compile-time-authoritative baked values
    }

    /// The on-disk file watched for live edits — the repo source, which is also what gets embedded, so
    /// editing it hot-updates the running game AND is what the next build bakes in. One file, no drift.
    public static string DiskPath()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "Application Support", "SlayTheSpire2", "modding",
                            "PokaYokeSpire", "src", "Core", "tunables.json");
    }

    private static DateTime _lastWrite;

    /// If the on-disk json changed since the last check, reload it and bump <see cref="Revision"/>.
    /// Returns true if a reload happened. Cheap enough to poll a few times a second.
    public static bool TryReloadFromDisk()
    {
        try
        {
            var p = DiskPath();
            if (!File.Exists(p)) return false;
            var w = File.GetLastWriteTimeUtc(p);
            if (w == _lastWrite) return false;
            _lastWrite = w;
            int before = Revision;
            Apply(File.ReadAllText(p));
            if (Revision != before) { DebugLog.Info($"Tunables reloaded from disk (rev {Revision})"); return true; }
            return false;
        }
        catch (Exception e) { DebugLog.Error("Tunables.TryReloadFromDisk", e); return false; }
    }

    /// Parse a JSON string into the tunables (test seam + the reload path). Tolerant + never throws.
    public static void Load(string json) => Apply(json);

    private static string? EmbeddedJson()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("tunables.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return null;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        catch { return null; }
    }

    private static void Apply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.TryGetProperty("gem", out var gem))
            {
                GemFontFrac = F(gem, "fontFrac", GemFontFrac);
                GemGap = F(gem, "gap", GemGap);
                GemScale = F(gem, "scale", GemScale);
                GemBlueTint = Col(gem, "blueTint", GemBlueTint);
                GemRedTint = Col(gem, "redTint", GemRedTint);
                IncomingTip = S(gem, "incomingTip", IncomingTip);
                OffenseTip = S(gem, "offenseTip", OffenseTip);
            }
            if (root.TryGetProperty("hud", out var hud))
            {
                HandRaiseFrac = F(hud, "handRaiseFrac", HandRaiseFrac);
                CounterRaiseFracOfHand = F(hud, "counterRaiseFracOfHand", CounterRaiseFracOfHand);
            }
            if (root.TryGetProperty("energy", out var en))
                EnergyHeightFrac = F(en, "heightFrac", EnergyHeightFrac);
            if (root.TryGetProperty("radial", out var ra))
                RadialRadius = F(ra, "radius", RadialRadius);
            if (root.TryGetProperty("reward", out var rw))
            {
                RewardRaise = F(rw, "rewardRaise", RewardRaise);
                DeckRaiseFracOfReward = F(rw, "deckRaiseFracOfReward", DeckRaiseFracOfReward);
            }
            if (root.TryGetProperty("deckFan", out var df))
            {
                DeckFanGap = F(df, "gap", DeckFanGap);
                DeckMinVisibleStep = F(df, "minVisibleStep", DeckMinVisibleStep);
            }
            if (root.TryGetProperty("sidePanel", out var sp))
                SidePanelMarginFrac = F(sp, "marginFrac", SidePanelMarginFrac);
            if (root.TryGetProperty("cardPreview", out var cp))
            {
                CardPreviewOffsetX = F(cp, "cursorOffsetX", CardPreviewOffsetX);
                CardPreviewOffsetY = F(cp, "cursorOffsetY", CardPreviewOffsetY);
            }
            Revision++;
        }
        catch (Exception e) { DebugLog.Error("Tunables.Apply", e); }
    }

    private static float F(JsonElement e, string k, float d) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : d;

    private static string S(JsonElement e, string k, string d) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? d) : d;

    private static Color Col(JsonElement e, string k, Color d)
    {
        if (e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() >= 3)
        {
            var a = v.EnumerateArray().ToArray();
            return new Color((float)a[0].GetDouble(), (float)a[1].GetDouble(), (float)a[2].GetDouble());
        }
        return d;
    }
}
