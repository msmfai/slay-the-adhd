using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// Reflects every concrete <see cref="EncounterModel"/> in the game and extracts, for each, the
/// facts the occlusion sweep needs: how many enemies, which slots they use, and whether the enemy
/// positions live in a CUSTOM packed scene (HasScene / named Slots) — in which case the on-screen
/// coordinates are NOT recoverable from C# and the encounter can only be reported, not geometrically
/// checked. This is deliberately defensive: instantiating game models can throw, so every read is
/// guarded and the failure is recorded rather than aborting the sweep.
/// </summary>
public static class GameEncounters
{
    public sealed record Info(
        string Name,
        int EnemyCount,
        IReadOnlyList<string> Slots,
        bool HasCustomScene,
        bool FullyCenterPlayers,
        string CountSource,   // how EnemyCount was determined (for honesty about confidence)
        string? Error);

    private static bool _dbReady;

    /// Populate the game's ModelDb headlessly so monster lookups (ModelDb.Monster&lt;T&gt;, used by every
    /// encounter's GenerateMonsters/AllPossibleMonsters) resolve. ModelDb.Init/AllAbstractModelSubtypes
    /// can't be used here — they call into the ModManager, which isn't initialized under `dotnet test`.
    /// So we enumerate the concrete AbstractModel subtypes straight from the assembly and Inject each,
    /// in several passes: a model whose ctor references another model fails the first pass and succeeds
    /// once its dependency is registered. Every Inject is guarded so an engine-only ctor just gets left
    /// out (it's reported as an undetermined count, never a crash).
    public static void EnsureModelDb()
    {
        if (_dbReady) return;
        _dbReady = true;
        try
        {
            var abstractModel = typeof(EncounterModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.AbstractModel")!;
            var concrete = typeof(EncounterModel).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && abstractModel.IsAssignableFrom(t)
                            && t.GetConstructor(Type.EmptyTypes) != null)
                .ToList();

            for (int pass = 0; pass < 4; pass++)
            {
                int before = RegisteredCount();
                foreach (var t in concrete)
                {
                    if (ModelDb.Contains(t)) continue;
                    try { ModelDb.Inject(t); } catch { /* dep not yet present, or engine-only ctor */ }
                }
                if (RegisteredCount() == before) break;   // no further progress
            }
            try { ModelDb.InitIds(); } catch { }
        }
        catch { }
    }

    private static int RegisteredCount()
    {
        try
        {
            var f = typeof(ModelDb).GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
            return (f?.GetValue(null) as System.Collections.ICollection)?.Count ?? 0;
        }
        catch { return 0; }
    }

    /// The canonical encounter instances registered in ModelDb. Do NOT construct encounters yourself:
    /// once the canonical instance exists, the ctor throws DuplicateModelException. Filters the
    /// deprecated/debug placeholders.
    public static IReadOnlyList<EncounterModel> CanonicalEncounters()
    {
        EnsureModelDb();
        var f = typeof(ModelDb).GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
        var dict = f?.GetValue(null) as IDictionary;
        var result = new List<EncounterModel>();
        if (dict != null)
            foreach (var v in dict.Values)
                if (v is EncounterModel enc && enc.GetType().Name != "DeprecatedEncounter" && !enc.GetType().Name.StartsWith("Debug"))
                    result.Add(enc);
        return result.OrderBy(e => e.GetType().Name).ToList();
    }

    public static IReadOnlyList<Info> All()
    {
        var list = new List<Info>();
        foreach (var enc in CanonicalEncounters())
        {
            try { list.Add(Describe(enc)); }
            catch (Exception e) { list.Add(new Info(enc.GetType().Name, 0, Array.Empty<string>(), false, false, "error", (e.InnerException ?? e).Message)); }
        }
        return list;
    }

    private static Info Describe(EncounterModel enc)
    {
        IReadOnlyList<string> slots = SafeSlots(enc);
        bool hasScene = SafeBool(enc, "HasScene");
        bool centered = SafeBool(enc, "FullyCenterPlayers");

        var (count, source) = EnemyCount(enc, slots);
        // "Custom scene" == coordinates only in the packed scene: HasScene, or named (non-empty) slots.
        bool customScene = hasScene || slots.Count > 0;
        return new Info(enc.GetType().Name, count, slots, customScene, centered, source, null);
    }

    private static (int, string) EnemyCount(EncounterModel enc, IReadOnlyList<string> slots)
    {
        // 1) best: the real generated monster list (works when GenerateMonsters ignores run state)
        var gen = enc.GetType().GetMethod("GenerateMonsters", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (gen != null)
        {
            try
            {
                if (gen.Invoke(enc, null) is IEnumerable list)
                {
                    int c = list.Cast<object>().Count();
                    if (c > 0) return (c, "GenerateMonsters");
                }
            }
            catch { /* needs run state / rng — fall through */ }
        }
        // 2) named slots pin the enemy positions one-to-one
        if (slots.Count > 0) return (slots.Count, "Slots");
        // 3) last resort: the possible-monster roster (may over-count random-subset encounters)
        try
        {
            var all = (IEnumerable?)enc.GetType().GetProperty("AllPossibleMonsters")?.GetValue(enc);
            if (all != null) { int c = all.Cast<object>().Count(); if (c > 0) return (c, "AllPossibleMonsters~"); }
        }
        catch { }
        return (0, "unknown");
    }

    private static IReadOnlyList<string> SafeSlots(EncounterModel enc)
    {
        try
        {
            var v = enc.GetType().GetProperty("Slots")?.GetValue(enc) as IEnumerable;
            return v == null ? Array.Empty<string>() : v.Cast<object>().Select(o => o?.ToString() ?? "").ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static bool SafeBool(EncounterModel enc, string prop)
    {
        try { return enc.GetType().GetProperty(prop)?.GetValue(enc) is bool b && b; }
        catch { return false; }
    }
}
