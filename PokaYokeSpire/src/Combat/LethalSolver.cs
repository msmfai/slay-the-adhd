using System.Collections.Generic;

namespace PokaYokeSpire.Combat;

/// <summary>
/// Deterministic lethal solver: does ANY sequence of card plays whose every outcome is known
/// from the CURRENT visible state finish the fight this turn? It searches over play orders and
/// target choices, chaining the effects it can read as data — damage (multi-hit, AOE), gaining
/// Strength (boosts later attacks), and applying Vulnerable (later attacks on that enemy do
/// +50%). It never uses hidden information (draw order, RNG) — cards with unmodelable/random
/// effects are simply excluded by the caller, which is exactly the "no new information" line.
///
/// Conservative by construction: it only reimplements the additive/×1.5 vanilla math on top of
/// each card's base damage, and under-counts anything it can't read — so it errs toward SILENCE
/// (a missed lethal), never a false "you have lethal".
/// </summary>
public static class LethalSolver
{
    public struct SimEnemy { public int Hp; public int Block; public bool Vulnerable; }

    /// A playable card's known effects. BaseDamage is intrinsic (no Strength/Vulnerable — those
    /// are applied by the sim). StrengthGain buffs later attacks; AppliesVulnerable marks the
    /// target (or all, if Aoe) Vulnerable for later attacks.
    public readonly record struct SimCard(
        int Cost, int BaseDamage, int Hits, bool Aoe, int StrengthGain, bool AppliesVulnerable);

    public static bool Solve(IReadOnlyList<SimCard> hand, int energy, int startStrength, IReadOnlyList<SimEnemy> enemies, bool playerWeak = false)
    {
        var e = new SimEnemy[enemies.Count];
        for (int i = 0; i < enemies.Count; i++) e[i] = enemies[i];
        int nodes = 0;
        return Search(new List<SimCard>(hand), energy, startStrength, e, playerWeak, ref nodes);
    }

    /// Weak makes the player's attacks deal ×0.75 (floored) — applied to each attack's per-hit damage.
    private static int ApplyWeak(int perHit, bool weak) => weak ? perHit * 3 / 4 : perHit;

    private static bool AllDead(SimEnemy[] e)
    {
        foreach (var x in e) if (x.Hp > 0) return false;
        return true;
    }

    private static bool Search(List<SimCard> hand, int energy, int strength, SimEnemy[] enemies, bool weak, ref int nodes)
    {
        if (AllDead(enemies)) return true;
        if (++nodes > 15000) return false;   // safety cap -> conservative (may miss, never lies)

        for (int i = 0; i < hand.Count; i++)
        {
            var c = hand[i];
            if (c.Cost > energy) continue;
            var rest = new List<SimCard>(hand); rest.RemoveAt(i);
            int nextEnergy = energy - c.Cost;
            int nextStrength = strength + c.StrengthGain;    // buff applies to LATER attacks

            bool isAttack = c.BaseDamage > 0 && c.Hits > 0;
            if (!isAttack)
            {
                // pure buff/utility: apply strength; apply Vulnerable ONLY if it's an AOE debuff
                // (a single-target debuff would need a target choice — skip it, conservative).
                var en = Clone(enemies);
                if (c.AppliesVulnerable && c.Aoe) for (int t = 0; t < en.Length; t++) if (en[t].Hp > 0) en[t].Vulnerable = true;
                if (Search(rest, nextEnergy, nextStrength, en, weak, ref nodes)) return true;
                continue;
            }

            int perHit = ApplyWeak(c.BaseDamage + strength, weak);   // current strength, then player Weak
            if (c.Aoe)
            {
                var en = Clone(enemies);
                for (int t = 0; t < en.Length; t++)
                {
                    if (en[t].Hp <= 0) continue;
                    ApplyDamage(ref en[t], perHit, c.Hits);
                    if (c.AppliesVulnerable) en[t].Vulnerable = true;
                }
                if (Search(rest, nextEnergy, nextStrength, en, weak, ref nodes)) return true;
            }
            else
            {
                for (int target = 0; target < enemies.Length; target++)
                {
                    if (enemies[target].Hp <= 0) continue;
                    var en = Clone(enemies);
                    ApplyDamage(ref en[target], perHit, c.Hits);
                    if (c.AppliesVulnerable) en[target].Vulnerable = true;
                    if (Search(rest, nextEnergy, nextStrength, en, weak, ref nodes)) return true;
                }
            }
        }
        return false;
    }

    /// Max HP damage removable this turn: <see cref="Total"/> = the best line maximizing the naive
    /// sum across all enemies; <see cref="PerEnemy"/>[i] = the best line maximizing damage to enemy i
    /// (each entry may come from a different line). Overkill is capped at each enemy's HP.
    public readonly record struct DamageResult(int Total, int[] PerEnemy);

    public static DamageResult MaxDamage(IReadOnlyList<SimCard> hand, int energy, int startStrength, IReadOnlyList<SimEnemy> enemies, bool playerWeak = false)
    {
        int n = enemies.Count;
        var e = new SimEnemy[n];
        var start = new int[n];
        for (int i = 0; i < n; i++) { e[i] = enemies[i]; start[i] = enemies[i].Hp; }
        int maxTotal = 0;
        var maxPer = new int[n];
        int nodes = 0;
        SearchDmg(new List<SimCard>(hand), energy, startStrength, e, start, playerWeak, ref maxTotal, maxPer, ref nodes);
        return new DamageResult(maxTotal, maxPer);
    }

    private static void SearchDmg(List<SimCard> hand, int energy, int strength, SimEnemy[] enemies, int[] start,
        bool weak, ref int maxTotal, int[] maxPer, ref int nodes)
    {
        // record HP removed so far (capped at each enemy's starting HP) — every prefix is a candidate.
        int total = 0;
        for (int i = 0; i < enemies.Length; i++)
        {
            int rem = start[i] - (enemies[i].Hp > 0 ? enemies[i].Hp : 0);
            if (rem < 0) rem = 0;
            total += rem;
            if (rem > maxPer[i]) maxPer[i] = rem;
        }
        if (total > maxTotal) maxTotal = total;
        if (++nodes > 15000) return;   // safety cap -> conservative (may under-count, never over)

        for (int i = 0; i < hand.Count; i++)
        {
            var c = hand[i];
            if (c.Cost > energy) continue;
            var rest = new List<SimCard>(hand); rest.RemoveAt(i);
            int nextEnergy = energy - c.Cost;
            int nextStrength = strength + c.StrengthGain;

            bool isAttack = c.BaseDamage > 0 && c.Hits > 0;
            if (!isAttack)
            {
                var en = Clone(enemies);
                if (c.AppliesVulnerable && c.Aoe) for (int t = 0; t < en.Length; t++) if (en[t].Hp > 0) en[t].Vulnerable = true;
                SearchDmg(rest, nextEnergy, nextStrength, en, start, weak, ref maxTotal, maxPer, ref nodes);
                continue;
            }

            int perHit = ApplyWeak(c.BaseDamage + strength, weak);
            if (c.Aoe)
            {
                var en = Clone(enemies);
                for (int t = 0; t < en.Length; t++)
                {
                    if (en[t].Hp <= 0) continue;
                    ApplyDamage(ref en[t], perHit, c.Hits);
                    if (c.AppliesVulnerable) en[t].Vulnerable = true;
                }
                SearchDmg(rest, nextEnergy, nextStrength, en, start, weak, ref maxTotal, maxPer, ref nodes);
            }
            else
            {
                for (int target = 0; target < enemies.Length; target++)
                {
                    if (enemies[target].Hp <= 0) continue;
                    var en = Clone(enemies);
                    ApplyDamage(ref en[target], perHit, c.Hits);
                    if (c.AppliesVulnerable) en[target].Vulnerable = true;
                    SearchDmg(rest, nextEnergy, nextStrength, en, start, weak, ref maxTotal, maxPer, ref nodes);
                }
            }
        }
    }

    private static void ApplyDamage(ref SimEnemy e, int perHit, int hits)
    {
        int dmgPerHit = e.Vulnerable ? perHit * 3 / 2 : perHit;   // vanilla +50%, floored
        if (dmgPerHit < 0) dmgPerHit = 0;
        int total = dmgPerHit * hits;
        int afterBlock = total - e.Block;
        if (afterBlock <= 0) { e.Block -= total; if (e.Block < 0) e.Block = 0; }
        else { e.Block = 0; e.Hp -= afterBlock; }
    }

    private static SimEnemy[] Clone(SimEnemy[] e)
    {
        var c = new SimEnemy[e.Length];
        System.Array.Copy(e, c, e.Length);
        return c;
    }
}
