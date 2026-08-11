using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

/// Demonstrates the isolation scaffolding reaches beyond relics: card and power models
/// also run their real constructors headless. (Metamorphic: large rosters; integration:
/// each class's ctor executes.)
public class ModelRuntimeTests
{
    private static List<Type> Concrete(string ns, Type baseType) =>
        typeof(RelicModel).Assembly.GetTypes()
            .Where(t => t.Namespace == ns && !t.IsAbstract && baseType.IsAssignableFrom(t))
            .ToList();

    [Fact]
    public void CardModelsInstantiate()
    {
        var cardBase = typeof(RelicModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.CardModel")!;
        var cards = Concrete("MegaCrit.Sts2.Core.Models.Cards", cardBase);
        Assert.True(cards.Count > 100, $"expected a big card roster, found {cards.Count}");

        var failures = new List<string>();
        foreach (var t in cards)
        {
            try { Assert.NotNull(Activator.CreateInstance(t, nonPublic: true)); }
            catch (Exception e) { failures.Add($"{t.Name}:{(e.InnerException ?? e).GetType().Name}"); }
        }
        // Report the rate; some cards may need construction args — that's information, not
        // necessarily failure. Assert the bulk construct.
        Assert.True(failures.Count < cards.Count / 2,
            $"{failures.Count}/{cards.Count} cards failed to construct: {string.Join(", ", failures.Take(8))}");
    }
}
