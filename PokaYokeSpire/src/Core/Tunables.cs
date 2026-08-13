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

    /// Bumped on every successful (re)load; features compare it to know when to rebuild.
    public static int Revision { get; private set; }

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
