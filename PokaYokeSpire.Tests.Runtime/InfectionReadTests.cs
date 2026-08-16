using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using MegaCrit.Sts2.Core.Models;          // ModelDb
using MegaCrit.Sts2.Core.Models.Cards;    // Infection, Burn

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// Diagnostic: what does the reader's end-of-turn logic actually SEE for Infection (and Burn) against the
/// real dll? The reader relies on `HasTurnEndInHandEffect` + a DamageVar/HpLossVar in `DynamicVars.Values`.
/// If either is missing on the real card, the defense number silently under-counts end-of-turn damage.
/// </summary>
public class InfectionReadTests
{
    private readonly ITestOutputHelper _o;
    public InfectionReadTests(ITestOutputHelper o) => _o = o;

    private void Probe<T>(string name) where T : CardModel
    {
        var card = ModelDb.Card<T>();
        _o.WriteLine($"{name}: HasTurnEndInHandEffect={card.HasTurnEndInHandEffect} type={card.Type} keywords=[{string.Join(",", card.Keywords)}]");
        var vars = new List<string>();
        try { foreach (var v in card.DynamicVars.Values) vars.Add($"{v.GetType().Name}={v.BaseValue}"); }
        catch (System.Exception e) { vars.Add($"THREW: {e.GetType().Name}"); }
        _o.WriteLine($"{name}: DynamicVars.Values = [{string.Join(", ", vars)}]");
    }

    [Fact]
    public void Probe_Infection_And_Burn()
    {
        GameEncounters.EnsureModelDb();
        Probe<Infection>("Infection");
        Probe<Burn>("Burn");
    }

    /// Regression: the reader's end-of-turn logic relies on these cards exposing HasTurnEndInHandEffect and
    /// a DamageVar in DynamicVars. If a game update changed either, the defense number would silently
    /// under-count end-of-turn damage — this pins it against the real dll.
    private static void Check<T>(int expected) where T : CardModel
    {
        GameEncounters.EnsureModelDb();
        var card = ModelDb.Card<T>();
        Assert.True(card.HasTurnEndInHandEffect, $"{typeof(T).Name}.HasTurnEndInHandEffect");
        var dmg = card.DynamicVars.Values.FirstOrDefault(v => v.GetType().Name == "DamageVar");
        Assert.NotNull(dmg);
        Assert.Equal(expected, (int)dmg!.BaseValue);
    }

    [Fact] public void Infection_ExposesTurnEndDamage() => Check<Infection>(3);
    [Fact] public void Burn_ExposesTurnEndDamage() => Check<Burn>(2);
}
