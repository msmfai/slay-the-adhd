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

    public RelicModel Create(Type t) => (RelicModel)Activator.CreateInstance(t, nonPublic: true)!;
    public RelicModel First() => Create(RelicTypes[0]);
}
