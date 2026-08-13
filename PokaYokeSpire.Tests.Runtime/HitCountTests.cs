using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using PokaYokeSpire.Combat;
using Xunit;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// Guards the multi-hit hit-count read (<see cref="HitCountRegistry"/>) — the logic behind the
/// "lethal gem undercounts Twin Strike / dynamic Strike-X cards" bug. Hit count lives inside a card's
/// OnPlay with no readable field, so the registry uses a known-card table, then description parsing,
/// then a CONSERVATIVE default of 1 (never over-count into a false lethal). These run against the real
/// game card models.
/// </summary>
public class HitCountTests
{
    private static CardModel? Canonical(System.Func<string, bool> nameMatch)
    {
        GameEncounters.EnsureModelDb();
        var f = typeof(ModelDb).GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
        var dict = f?.GetValue(null) as IDictionary;
        if (dict == null) return null;
        foreach (var v in dict.Values)
            if (v is CardModel c && nameMatch(c.GetType().Name)) return c;
        return null;
    }

    private static List<CardModel> AllCards()
    {
        GameEncounters.EnsureModelDb();
        var f = typeof(ModelDb).GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
        var dict = f?.GetValue(null) as IDictionary;
        var list = new List<CardModel>();
        if (dict != null)
            foreach (var v in dict.Values)
                if (v is CardModel c) list.Add(c);
        return list;
    }

    [Fact]
    public void KnownMultiHit_TwinStrike_CountsTwo()
    {
        var twin = Canonical(n => n == "TwinStrike");
        Assert.True(twin != null, "expected canonical TwinStrike in ModelDb");
        Assert.Equal(2, HitCountRegistry.HitsOf(twin!));
    }

    [Fact]
    public void SingleHit_Strike_CountsOne()
    {
        var strike = Canonical(n => n == "Strike" || n.StartsWith("Strike"));
        Assert.True(strike != null, "expected canonical Strike in ModelDb");
        Assert.Equal(1, HitCountRegistry.HitsOf(strike!));
    }

    [Fact]
    public void WholeRoster_HitsOf_NeverThrows_AndIsAtLeastOne()
    {
        // conservative-by-construction: an unknown/odd card must never crash the solver and must never
        // report < 1 hit (which would zero out its damage) — this is the "errs toward silence" contract.
        var cards = AllCards();
        Assert.NotEmpty(cards);
        foreach (var c in cards)
        {
            int hits = HitCountRegistry.HitsOf(c);   // must not throw for any card in the game
            Assert.True(hits >= 1, $"{c.GetType().Name} reported {hits} hits (< 1)");
            Assert.True(hits < 20, $"{c.GetType().Name} reported implausible {hits} hits");
        }
    }
}
