using System.Collections.Generic;
using System.Globalization;

namespace PokaYokeSpire.Combat;

/// <summary>
/// Aggregate deck metrics for the card-reward screen. Pure (no Godot), so the math is unit-tested.
///
/// The basis is chosen to be as ORTHOGONAL and high-level-informative as possible: composition
/// (size + attack/skill/power mix), economy (cost, self-draw, self-energy), and output (damage,
/// block) each on their own axis, plus the two per-energy EFFICIENCY ratios that good players
/// actually judge cards by (damage- and block-per-energy). Values are read structurally from each
/// card's canonical vars (the same "no new information" boundary the lethal solver uses): a card
/// contributes only the numbers printed on it, so conditional/scaling effects are under-counted,
/// never invented.
/// </summary>
public static class DeckStats
{
    public enum Kind { Attack, Skill, Power, Other }

    /// One card's structurally-read contribution. Draw = its "Cards" var (mostly card draw),
    /// EnergyGain = its "Energy" var. CostsX cards are excluded from the cost/efficiency averages.
    public readonly record struct CardFact(int Cost, bool CostsX, int Damage, int Block, int Draw, int EnergyGain, Kind Kind);

    public readonly record struct Result(
        int Count,
        double AvgCost, int XCostCount,
        double AvgDamage, double AvgBlock,
        double AvgDraw, double AvgEnergyGain,
        double DamagePerEnergy, double BlockPerEnergy,
        int Attacks, int Skills, int Powers, int Others);

    public static Result Compute(IReadOnlyList<CardFact> cards)
    {
        int n = cards.Count;
        if (n == 0) return default;

        long dmg = 0, blk = 0, draw = 0, egain = 0, costSum = 0;
        int costable = 0, xcost = 0, atk = 0, skl = 0, pow = 0, oth = 0;
        foreach (var c in cards)
        {
            dmg += c.Damage; blk += c.Block; draw += c.Draw; egain += c.EnergyGain;
            if (c.CostsX) xcost++;
            else { costSum += c.Cost; costable++; }
            switch (c.Kind)
            {
                case Kind.Attack: atk++; break;
                case Kind.Skill: skl++; break;
                case Kind.Power: pow++; break;
                default: oth++; break;
            }
        }

        // Per-energy efficiency is the deck-wide yield per energy spent (total output / total cost),
        // with a floor of 1 so a deck of all-0-cost cards doesn't divide by zero.
        double totalCostF = costSum <= 0 ? 1 : costSum;
        return new Result(
            Count: n,
            AvgCost: costable == 0 ? 0 : (double)costSum / costable, XCostCount: xcost,
            AvgDamage: (double)dmg / n, AvgBlock: (double)blk / n,
            AvgDraw: (double)draw / n, AvgEnergyGain: (double)egain / n,
            DamagePerEnergy: dmg / totalCostF, BlockPerEnergy: blk / totalCostF,
            Attacks: atk, Skills: skl, Powers: pow, Others: oth);
    }

    // ── formatting: (label, value) rows, pure + testable, so the feature just renders them ──

    public static IReadOnlyList<(string label, string value)> LeftRows(Result r)
    {
        string mix = $"{Pct(r.Attacks, r.Count)}/{Pct(r.Skills, r.Count)}/{Pct(r.Powers, r.Count)}";
        return new (string, string)[]
        {
            ("Cards", r.Count.ToString(CultureInfo.InvariantCulture)),
            ("Avg cost", r.XCostCount > 0 ? $"{F1(r.AvgCost)} (+{r.XCostCount}×X)" : F1(r.AvgCost)),
            ("Draw / card", F2(r.AvgDraw)),
            ("Energy / card", F2(r.AvgEnergyGain)),
            ("Atk/Skl/Pwr", mix),
        };
    }

    public static IReadOnlyList<(string label, string value)> RightRows(Result r) => new (string, string)[]
    {
        ("Avg damage", F1(r.AvgDamage)),
        ("Avg block", F1(r.AvgBlock)),
        ("Dmg / energy", F1(r.DamagePerEnergy)),
        ("Block / energy", F1(r.BlockPerEnergy)),
    };

    private static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    private static string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Pct(int part, int whole) => whole == 0 ? "0" : ((int)System.Math.Round(100.0 * part / whole)).ToString(CultureInfo.InvariantCulture);
}
