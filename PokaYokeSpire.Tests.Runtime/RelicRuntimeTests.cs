using MegaCrit.Sts2.Core.Models;
using PokaYokeSpire.Relics;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

/// Integration + metamorphic tests that EXECUTE real game relic code headless, and test
/// the mod's per-relic registry against real relic instances.
public class RelicRuntimeTests : IClassFixture<GameRelics>
{
    private readonly GameRelics _g;
    public RelicRuntimeTests(GameRelics g) => _g = g;

    [Fact]
    public void GameHasManyRelics()
    {
        // sanity: the game ships a large relic roster (per README stats ~290).
        Assert.True(_g.RelicTypes.Count > 100, $"only found {_g.RelicTypes.Count} relic types");
    }

    [Fact]
    public void EveryRelicInstantiates()
    {
        // INTEGRATION: run each relic class's real constructor in isolation.
        var failures = new List<string>();
        foreach (var t in _g.RelicTypes)
        {
            try { Assert.NotNull(_g.Create(t)); }
            catch (Exception e) { failures.Add($"{t.Name}: {(e.InnerException ?? e).GetType().Name}"); }
        }
        Assert.True(failures.Count == 0, "relics that failed to construct: " + string.Join(", ", failures.Take(10)));
    }

    [Fact]
    public void Registry_KeyIsClassName()
    {
        var relic = _g.First();
        Assert.Equal(relic.GetType().Name, RelicRegistry.KeyOf(relic));
    }

    [Fact]
    public void Registry_CustomBehaviourOverridesDefault()
    {
        var relic = _g.Create(_g.RelicTypes[1]);
        RelicRegistry.For(RelicRegistry.KeyOf(relic)).Counter = _ => new CounterDisplay(42, 99);
        var info = RelicRegistry.Counter(relic);
        Assert.Equal(42, info.Current);
        Assert.Equal(99, info.Max);
        Assert.Equal("42/99", info.Text);
    }

    [Fact]
    public void Registry_BadCustomFnFallsBackNotThrows()
    {
        // A throwing custom fn must never break the counter — it falls back to default.
        var relic = _g.Create(_g.RelicTypes[2]);
        RelicRegistry.For(RelicRegistry.KeyOf(relic)).Counter = _ => throw new InvalidOperationException("boom");
        var info = RelicRegistry.Counter(relic); // must not throw
        Assert.NotNull(info);
    }

    [Fact]
    public void Registry_DefaultIsDisplayAmount()
    {
        // METAMORPHIC/INTEGRATION: an unregistered relic's counter == its DisplayAmount.
        var relic = _g.Create(_g.RelicTypes[3]); // not registered by any test above
        var info = RelicRegistry.Counter(relic);
        Assert.Equal(relic.DisplayAmount, info.Current);
        Assert.Null(info.Max);
    }
}
