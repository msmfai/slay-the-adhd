using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;
using PokaYokeSpire.Combat;
using PokaYokeSpire.Spatial;
using Xunit;
using Xunit.Abstractions;
using Kind = PokaYokeSpire.Combat.DeckStats.Kind;
using Fact_ = PokaYokeSpire.Combat.DeckStats.CardFact;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// The pure deck-metric math + the structural card read against REAL game cards, plus the flanking
/// panels' placement (must never occlude the reward choices — CLAUDE.md § "Spatial placement").
/// </summary>
public class DeckStatsTests
{
    private readonly ITestOutputHelper _o;
    public DeckStatsTests(ITestOutputHelper o) => _o = o;

    // ─────────────────────────── pure aggregate math ───────────────────────────

    [Fact]
    public void Compute_AveragesAndMix()
    {
        var cards = new List<Fact_>
        {
            new(1, false, 6, 0, 0, 0, Kind.Attack),   // Strike
            new(1, false, 0, 5, 0, 0, Kind.Skill),    // Defend
            new(1, false, 0, 0, 2, 0, Kind.Skill),    // draws 2
            new(1, false, 0, 0, 0, 1, Kind.Power),    // +1 energy
        };
        var r = DeckStats.Compute(cards);
        Assert.Equal(4, r.Count);
        Assert.Equal(1.0, r.AvgCost, 3);
        Assert.Equal(6.0 / 4, r.AvgDamage, 3);
        Assert.Equal(5.0 / 4, r.AvgBlock, 3);
        Assert.Equal(2.0 / 4, r.AvgDraw, 3);
        Assert.Equal(1.0 / 4, r.AvgEnergyGain, 3);
        Assert.Equal(1, r.Attacks); Assert.Equal(2, r.Skills); Assert.Equal(1, r.Powers);
    }

    [Fact]
    public void Compute_PerEnergyEfficiency()
    {
        // 6 damage total over 2 energy total -> 3.0 dmg/energy.
        var cards = new List<Fact_> { new(2, false, 6, 4, 0, 0, Kind.Attack) };
        var r = DeckStats.Compute(cards);
        Assert.Equal(3.0, r.DamagePerEnergy, 3);
        Assert.Equal(2.0, r.BlockPerEnergy, 3);
    }

    [Fact]
    public void Compute_XCostExcludedFromCostAverage()
    {
        var cards = new List<Fact_> { new(1, false, 5, 0, 0, 0, Kind.Attack), new(0, true, 0, 0, 0, 0, Kind.Skill) };
        var r = DeckStats.Compute(cards);
        Assert.Equal(1.0, r.AvgCost, 3);   // only the non-X card counts toward avg cost
        Assert.Equal(1, r.XCostCount);
    }

    [Fact]
    public void Compute_EmptyDeckIsZero() => Assert.Equal(0, DeckStats.Compute(new List<Fact_>()).Count);

    [Fact]
    public void Rows_AreWellFormed()
    {
        var r = DeckStats.Compute(new List<Fact_> { new(1, false, 6, 0, 0, 0, Kind.Attack) });
        Assert.Contains(DeckStats.LeftRows(r), x => x.label == "Cards" && x.value == "1");
        Assert.Equal(4, DeckStats.RightRows(r).Count);
    }

    // ──────────────── structural read against REAL game cards ────────────────

    [Fact]
    public void Read_Strike_And_Defend_FromRealModels()
    {
        GameEncounters.EnsureModelDb();   // populates ModelDb so canonical cards exist

        // Strike/Defend are per-character (StrikeIronclad, DefendIronclad, …) — match by prefix.
        var strike = Canonical(n => n.StartsWith("Strike"));
        var defend = Canonical(n => n.StartsWith("Defend"));
        Assert.True(strike != null && defend != null, "expected a canonical Strike & Defend in ModelDb");

        var sf = CardFactReader.Read(strike!);
        _o.WriteLine($"Strike -> cost {sf.Cost}, dmg {sf.Damage}, kind {sf.Kind}");
        Assert.Equal(Kind.Attack, sf.Kind);
        Assert.Equal(6, sf.Damage);
        Assert.Equal(0, sf.Block);

        var df = CardFactReader.Read(defend!);
        _o.WriteLine($"Defend -> cost {df.Cost}, block {df.Block}, kind {df.Kind}");
        Assert.Equal(Kind.Skill, df.Kind);
        Assert.Equal(5, df.Block);
        Assert.Equal(0, df.Damage);
    }

    [Fact]
    public void Read_WholeCardRoster_DoesNotThrow_AndFindsSignal()
    {
        GameEncounters.EnsureModelDb();
        var cards = CanonicalCards();
        Assert.True(cards.Count > 50, $"expected a real card roster, found {cards.Count}");

        var facts = new List<Fact_>();
        foreach (var c in cards) { try { facts.Add(CardFactReader.Read(c)); } catch { } }
        var r = DeckStats.Compute(facts);
        _o.WriteLine($"Roster {r.Count}: avgCost {r.AvgCost:0.00}, avgDmg {r.AvgDamage:0.0}, avgBlk {r.AvgBlock:0.0}, dmg/E {r.DamagePerEnergy:0.0}");
        // sanity: the game's cards collectively deal damage and grant block.
        Assert.True(r.AvgDamage > 0, "roster should have some damage");
        Assert.True(r.AvgBlock > 0, "roster should have some block");
        Assert.True(r.Attacks > 0 && r.Skills > 0, "roster should have attacks and skills");
    }

    // ──────────────── dynamic hit-count read (lethal solver) ────────────────

    [Fact]
    public void TearAsunder_ExposesCalculatedHitsVar()
    {
        // The lethal gem resolves dynamic "hit X times" cards via a "CalculatedHits" CalculatedVar
        // (LethalGemGlowState.ReadHits). Guard against the game renaming it.
        GameEncounters.EnsureModelDb();
        var card = Canonical(n => n == "TearAsunder");
        Assert.True(card != null, "expected canonical TearAsunder in ModelDb");
        Assert.True(card!.DynamicVars.TryGetValue("CalculatedHits", out var v) && v.GetType().Name == "CalculatedVar",
            "Tear Asunder must expose a CalculatedHits CalculatedVar for the solver to read its hit count");
    }

    // ──────────────── flanking-panel placement (spatial) ────────────────

    [Theory]
    [InlineData(1920f, 1080f)][InlineData(2560f, 1440f)][InlineData(1280f, 720f)]
    public void Panels_DoNotOccludeRewardChoices(float vpW, float vpH)
    {
        float panelW = System.MathF.Max(240f, vpW * 0.15f);
        float panelH = 210f;
        var reward = new UiRect("reward", (vpW - vpW * 0.55f) / 2f, vpH * 0.10f, vpW * 0.55f, vpH * 0.55f);
        var (l, rr) = SidePanelLayout.Compute(vpW, vpH, panelW, panelH, vpH * 0.30f);

        Assert.False(SpatialGraph.Intersects(l, reward), "left stats panel overlaps the choices");
        Assert.False(SpatialGraph.Intersects(rr, reward), "right stats panel overlaps the choices");
        Assert.Empty(SpatialGraph.OutOfBounds(new[] { l, rr }, vpW, vpH));
    }

    // ── helpers: pull canonical models out of the populated ModelDb ──
    private static CardModel? Canonical(System.Func<string, bool> nameMatch) =>
        CanonicalCards().FirstOrDefault(c => nameMatch(c.GetType().Name));

    private static List<CardModel> CanonicalCards()
    {
        var f = typeof(ModelDb).GetField("_contentById", BindingFlags.NonPublic | BindingFlags.Static);
        var dict = f?.GetValue(null) as IDictionary;
        var list = new List<CardModel>();
        if (dict != null)
            foreach (var v in dict.Values)
                if (v is CardModel c) list.Add(c);
        return list;
    }
}
