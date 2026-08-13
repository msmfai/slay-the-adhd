using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace PokaYokeSpire.Tests.Runtime;

/// Scaffolding: enumerates + instantiates real relic models from the loaded sts2.dll,
/// so tests can run the game's own relic code in isolation.
public sealed class GameRelics
{
    public IReadOnlyList<Type> RelicTypes { get; }

    public GameRelics()
    {
        RelicTypes = typeof(RelicModel).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(RelicModel).IsAssignableFrom(t))
            .OrderBy(t => t.Name)
            .ToList();
    }

    /// Construct the relic's real ctor. If another test already populated ModelDb (the models are
    /// global static state), the ctor throws DuplicateModelException — in that case the canonical
    /// instance is the same real relic, so return it. Works whether or not ModelDb is populated.
    public RelicModel Create(Type t)
    {
        try { return (RelicModel)Activator.CreateInstance(t, nonPublic: true)!; }
        catch (Exception e)
        {
            if ((e.InnerException ?? e).GetType().Name == "DuplicateModelException" && Canonical(t) is { } c) return c;
            throw;
        }
    }

    public RelicModel First() => Create(RelicTypes[0]);

    private static RelicModel? Canonical(Type t)
    {
        var f = typeof(ModelDb).GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
        if (f?.GetValue(null) is IDictionary dict)
            foreach (var v in dict.Values)
                if (v is RelicModel r && r.GetType() == t) return r;
        return null;
    }
}
