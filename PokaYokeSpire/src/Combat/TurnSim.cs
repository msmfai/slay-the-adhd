using System.Collections.Generic;
using System.Text;

namespace PokaYokeSpire.Combat;

/// <summary>
/// A faithful (if partial) forward simulator of ONE of your turns, shared by both gems. It plays out
/// every reachable sequence of your hand against a snapshot of the board (player + enemies + powers),
/// threading state so path-dependent effects resolve correctly — Bash's Vulnerable boosts a later
/// Strike, Second Wind sees fewer cards after a Defend, Body Slam scales with block you've gained, your
/// Weak/Shrink lower your own damage, etc. Over all reachable states it reports:
///   • MaxDamage / MaxPerEnemy — the most HP damage you can deal (offense gem, and per-enemy for hover);
///   • MinHpLost — the least HP you can lose to the enemies' queued attacks (defense gem), by blocking,
///     Weakening attackers, and killing them.
///
/// PURE + deterministic (no Godot/game types) so it is exhaustively unit-tested; a separate reader
/// snapshots the live game into these structs on the game thread. Efficiency comes from path-dependence:
/// branches that reach an identical state are memoized, and the node budget bounds pathological hands
/// (when hit, the result is a conservative lower bound on damage / upper bound on HP lost).
///
/// Coverage is the "encoded so far" set; the reader logs anything it can't map so the gap closes from
/// real play. Damage math models the STS pipeline: (base + Strength) → ×¾ if attacker Weak → ×3⁄2 if
/// defender Vulnerable, flooring at each step; block is (base + Dexterity) → ×¾ if Frail.
/// </summary>
public static class TurnSim
{
    public enum Tgt { None, OneEnemy, AllEnemies }
    public enum Dyn { None, BodySlam, SecondWind }   // bespoke, state-dependent cards

    public struct Enemy
    {
        public int Hp, Block, Vulnerable, Weak, Strength;
        public int IntentDamage, IntentHits;   // this enemy's queued attack (0 if it isn't attacking)
        public bool Capped;                     // Hardened Shell: caps HP damage taken this turn…
        public int CapRemaining;                // …at this many more points (only when Capped)
        public bool Alive => Hp > 0;
    }

    public struct Player
    {
        public int Energy, Strength, Dexterity, Block;
        public int Weak, Frail, Vulnerable, Shrink;   // debuffs (presence, not magnitude)
        public bool Intangible;                       // caps EACH hit you take to 1
        public int EndTurnSelfDamage;                 // Burn/Toxic etc. in hand: HP lost at end of turn
    }

    /// A hand card reduced to its modeled effects. Damage &gt; 0 ⇒ it's an attack (a NonAttack otherwise,
    /// which is what Second Wind counts/exhausts).
    public sealed class Card
    {
        public string Name = "";
        public int Cost;
        public int Damage, Hits = 1;
        public Tgt AttackTarget = Tgt.None;
        public int Block;
        public int StrengthGain;
        public int ApplyVulnerable; public Tgt VulnTarget = Tgt.None;
        public int ApplyWeak; public Tgt WeakTarget = Tgt.None;
        public int EnergyGain;
        public bool Exhausts;
        public bool XCost;     // X-cost card (Whirlwind): spends ALL energy, hits X = energy times
        public Dyn Dynamic = Dyn.None;
        public int DynParam;   // e.g. Second Wind's block-per-exhausted-card
        public bool IsAttack => Damage > 0 || Dynamic == Dyn.BodySlam;
    }

    public struct Result
    {
        public int MaxDamage;
        public int[] MaxPerEnemy;
        public int MinHpLost;
        public bool CanKillAll;   // is there ONE play sequence that leaves every enemy dead this turn?
        public int Nodes;         // states expanded (for diagnostics); == cap ⇒ truncated (conservative)
        public bool Truncated;    // true if the node budget was hit before the search finished
    }

    // ── damage / block math (STS pipeline: (base + Σadditive) × Πmultiplicative, floored ONCE) ──
    internal static int Atk(int baseDmg, int strength, bool weak, bool shrink, bool vuln)
    {
        decimal d = baseDmg + strength;          // additive: Strength (Vigor/etc. fold in here too)
        if (d < 0m) d = 0m;
        decimal m = 1m;                           // multiplicative, applied together then floored once
        if (weak) m *= 0.75m;                     // Weak −25%
        if (shrink) m *= 0.70m;                   // Shrink −30% (flat, not a Strength cut)
        if (vuln) m *= 1.5m;                      // Vulnerable +50%
        return (int)decimal.Floor(d * m);
    }

    internal static int Blk(int baseBlock, int dexterity, bool frail)
    {
        int b = baseBlock + dexterity;
        if (b < 0) b = 0;
        if (frail) b = b * 3 / 4;                // Frail: −25%, floored
        return b;
    }

    /// HP you'd lose to the enemies' queued attacks — the DEFENSE score. This is pure MITIGATION: block
    /// and Weaken/Vulnerable reduce it, but KILLING an attacker does NOT (that's offense — it competes for
    /// the same energy and belongs to the offense gem / green glow). So every attacker's intent counts
    /// regardless of whether the sim's attacks could kill it; only block and its (possibly Weakened)
    /// damage change. Keeps a pure Strike from ever reading as "defense".
    internal static int HpLost(in Player p, Enemy[] enemies)
    {
        int incoming = 0;
        foreach (var e in enemies)
        {
            if (e.IntentDamage <= 0) continue;   // NB: no !e.Alive check — killing isn't defense
            int perHit = Atk(e.IntentDamage, e.Strength, e.Weak > 0, false, p.Vulnerable > 0);
            if (p.Intangible && perHit > 1) perHit = 1;   // Intangible caps EACH hit to 1
            incoming += perHit * (e.IntentHits < 1 ? 1 : e.IntentHits);
        }
        int net = incoming - p.Block;
        if (net < 0) net = 0;
        return net + p.EndTurnSelfDamage;   // Burn/Toxic/Infection etc. — unblockable end-of-turn HP loss
    }

    public static Result Solve(Player player, Enemy[] enemies, IReadOnlyList<Card> hand, int nodeCap = 200000)
    {
        int n = enemies.Length;
        var initialHp = new int[n];
        for (int i = 0; i < n; i++) initialHp[i] = enemies[i].Hp;

        int[] cardClass = ClassifyCards(hand);   // identical cards → same class (played in one canonical order)

        var best = new Result { MaxDamage = 0, MaxPerEnemy = new int[n], MinHpLost = HpLost(player, enemies) };
        var visited = new HashSet<string>();
        int nodes = 0;
        ulong fullMask = hand.Count >= 64 ? ulong.MaxValue : (1UL << hand.Count) - 1;
        Recurse(player, enemies, hand, cardClass, fullMask, initialHp, ref best, visited, ref nodes, nodeCap, payloadPlayed: false);
        best.Nodes = nodes;
        best.Truncated = nodes >= nodeCap;

        // Target-symmetry only focused one representative of each ROOT-identical enemy group; enemies
        // identical at the root share the same focusable max, so copy the group max to every peer. Lossless.
        var groupMax = new int[n];
        for (int i = 0; i < n; i++)
        {
            int m = best.MaxPerEnemy[i];
            for (int j = 0; j < n; j++) if (SameSig(enemies[i], enemies[j]) && best.MaxPerEnemy[j] > m) m = best.MaxPerEnemy[j];
            groupMax[i] = m;
        }
        for (int i = 0; i < n; i++) best.MaxPerEnemy[i] = groupMax[i];
        return best;
    }

    // ── lossless pruning helpers ──
    private static int[] ClassifyCards(IReadOnlyList<Card> hand)
    {
        var cls = new int[hand.Count];
        int next = 0;
        for (int i = 0; i < hand.Count; i++)
        {
            int found = -1;
            for (int j = 0; j < i; j++) if (SameCard(hand[j], hand[i])) { found = cls[j]; break; }
            cls[i] = found >= 0 ? found : next++;
        }
        return cls;
    }

    private static bool SameCard(Card a, Card b) =>
        a.Cost == b.Cost && a.Damage == b.Damage && a.Hits == b.Hits && a.AttackTarget == b.AttackTarget && a.Block == b.Block
        && a.StrengthGain == b.StrengthGain && a.ApplyVulnerable == b.ApplyVulnerable && a.VulnTarget == b.VulnTarget
        && a.ApplyWeak == b.ApplyWeak && a.WeakTarget == b.WeakTarget && a.EnergyGain == b.EnergyGain
        && a.Exhausts == b.Exhausts && a.Dynamic == b.Dynamic && a.DynParam == b.DynParam;

    private static bool SameSig(in Enemy a, in Enemy b) =>
        a.Hp == b.Hp && a.Block == b.Block && a.Vulnerable == b.Vulnerable && a.Weak == b.Weak && a.Strength == b.Strength
        && a.IntentDamage == b.IntentDamage && a.IntentHits == b.IntentHits
        && a.Capped == b.Capped && (!a.Capped || a.CapRemaining == b.CapRemaining);

    private static void Recurse(Player p, Enemy[] enemies, IReadOnlyList<Card> hand, int[] cardClass, ulong remaining,
                                int[] initialHp, ref Result best, HashSet<string> visited, ref int nodes, int cap, bool payloadPlayed)
    {
        // evaluate THIS reachable state (you can stop playing at any point)
        int totalDealt = 0;
        bool allDead = true;
        for (int i = 0; i < enemies.Length; i++)
        {
            int dealt = initialHp[i] - (enemies[i].Hp < 0 ? 0 : enemies[i].Hp);
            if (dealt < 0) dealt = 0;
            if (dealt > best.MaxPerEnemy[i]) best.MaxPerEnemy[i] = dealt;
            totalDealt += dealt;
            if (enemies[i].Alive) allDead = false;
        }
        if (totalDealt > best.MaxDamage) best.MaxDamage = totalDealt;
        if (allDead) best.CanKillAll = true;   // this single sequence clears the room
        int hpLost = HpLost(p, enemies);
        if (hpLost < best.MinHpLost) best.MinHpLost = hpLost;

        if (nodes >= cap) return;
        string key = StateKey(p, enemies, remaining);
        if (!visited.Add(key)) return;   // path-dependence: an identical state has already been expanded

        for (int i = 0; i < hand.Count; i++)
        {
            ulong bit = 1UL << i;
            if ((remaining & bit) == 0) continue;
            var c = hand[i];
            if (c.Cost > p.Energy) continue;
            if (c.XCost && p.Energy <= 0) continue;   // X-cost with no energy does nothing

            // T3 (setup-before-payload dominance): once an attack/Body Slam has been played, never play a
            // pure non-attack card — playing every setup/block BEFORE the attacks is always ≥ as good
            // (buffs help more attacks, block feeds Body Slam), so interleavings are dominated. Lossless.
            if (payloadPlayed && !c.IsAttack) continue;

            // T1: among identical cards, only play them in index order (skip if a duplicate at a lower
            // index is still unplayed) — collapses the factorial of interchangeable-card permutations.
            bool dupLower = false;
            for (int j = 0; j < i; j++) if ((remaining & (1UL << j)) != 0 && cardClass[j] == cardClass[i]) { dupLower = true; break; }
            if (dupLower) continue;

            // a single-target attack branches over each live enemy target; everything else has one branch
            bool singleTarget = c.AttackTarget == Tgt.OneEnemy && c.Damage > 0;
            if (singleTarget)
            {
                for (int t = 0; t < enemies.Length; t++)
                {
                    if (!enemies[t].Alive) continue;
                    // T2: among CURRENTLY-identical live enemies, target only the first — the others are
                    // symmetric (root-identical peers get their per-enemy max copied back in Solve).
                    bool dupTarget = false;
                    for (int u = 0; u < t; u++) if (enemies[u].Alive && SameSig(enemies[u], enemies[t])) { dupTarget = true; break; }
                    if (dupTarget) continue;

                    nodes++;
                    var (np, ne, nr) = Play(p, enemies, hand, remaining, i, t);
                    Recurse(np, ne, hand, cardClass, nr, initialHp, ref best, visited, ref nodes, cap, payloadPlayed: true);
                    if (nodes >= cap) return;
                }
            }
            else
            {
                nodes++;
                var (np, ne, nr) = Play(p, enemies, hand, remaining, i, -1);
                Recurse(np, ne, hand, cardClass, nr, initialHp, ref best, visited, ref nodes, cap, payloadPlayed || c.IsAttack);
                if (nodes >= cap) return;
            }
        }
    }

    /// Apply one card, returning the new (player, enemies, remaining-mask). Damage lands FIRST, then the
    /// card's own status/buffs (so a card never benefits from the Vulnerable it applies).
    private static (Player, Enemy[], ulong) Play(Player p, Enemy[] src, IReadOnlyList<Card> hand,
                                                 ulong remaining, int cardIndex, int target)
    {
        var c = hand[cardIndex];
        var e = (Enemy[])src.Clone();

        // X-cost (Whirlwind): spends ALL energy, and hits X = that much
        int xHits = 0;
        if (c.XCost) { xHits = p.Energy; p.Energy = 0; }
        else p.Energy -= c.Cost;
        p.Energy += c.EnergyGain;

        int dmg = c.Damage;
        if (c.Dynamic == Dyn.BodySlam) dmg = p.Block;   // Body Slam: damage == current block

        if (dmg > 0)
        {
            int hits = c.XCost ? xHits : (c.Hits < 1 ? 1 : c.Hits);
            if (c.AttackTarget == Tgt.AllEnemies)
            {
                for (int t = 0; t < e.Length; t++) if (e[t].Alive) HitEnemy(ref e[t], dmg, hits, p);
            }
            else
            {
                int t = target >= 0 ? target : FirstAlive(e);
                if (t >= 0) HitEnemy(ref e[t], dmg, hits, p);
            }
        }

        if (c.Block > 0) p.Block += Blk(c.Block, p.Dexterity, p.Frail > 0);

        if (c.Dynamic == Dyn.SecondWind)
        {
            // exhaust all OTHER non-attack cards; gain DynParam block for each (Dex/Frail-adjusted)
            int exhausted = 0;
            ulong toExhaust = 0;
            for (int j = 0; j < hand.Count; j++)
            {
                if (j == cardIndex) continue;
                if ((remaining & (1UL << j)) == 0) continue;
                if (!hand[j].IsAttack) { toExhaust |= 1UL << j; exhausted++; }
            }
            p.Block += Blk(c.DynParam, p.Dexterity, p.Frail > 0) * exhausted;
            remaining &= ~toExhaust;
        }

        if (c.StrengthGain != 0) p.Strength += c.StrengthGain;
        ApplyStatus(ref e, c.ApplyVulnerable, c.VulnTarget, target, isVuln: true);
        ApplyStatus(ref e, c.ApplyWeak, c.WeakTarget, target, isVuln: false);

        remaining &= ~(1UL << cardIndex);
        return (p, e, remaining);
    }

    private static void HitEnemy(ref Enemy e, int baseDmg, int hits, in Player p)
    {
        for (int h = 0; h < hits; h++)
        {
            int dmg = Atk(baseDmg, p.Strength, p.Weak > 0, p.Shrink > 0, e.Vulnerable > 0);
            if (e.Capped && dmg > e.CapRemaining) dmg = e.CapRemaining;   // Hardened Shell: cap this turn
            int afterBlock = dmg - e.Block;
            if (afterBlock <= 0) { e.Block -= dmg; if (e.Block < 0) e.Block = 0; continue; }
            e.Block = 0;
            e.Hp -= afterBlock;
            if (e.Capped) e.CapRemaining -= afterBlock;   // consumed part of the per-turn allowance
        }
    }

    private static void ApplyStatus(ref Enemy[] e, int amount, Tgt tgt, int target, bool isVuln)
    {
        if (amount <= 0 || tgt == Tgt.None) return;
        if (tgt == Tgt.AllEnemies)
            for (int t = 0; t < e.Length; t++) { if (!e[t].Alive) continue; if (isVuln) e[t].Vulnerable += amount; else e[t].Weak += amount; }
        else
        {
            int t = target >= 0 ? target : FirstAlive(e);
            if (t >= 0) { if (isVuln) e[t].Vulnerable += amount; else e[t].Weak += amount; }
        }
    }

    private static int FirstAlive(Enemy[] e) { for (int i = 0; i < e.Length; i++) if (e[i].Alive) return i; return -1; }

    private static string StateKey(in Player p, Enemy[] e, ulong remaining)
    {
        var sb = new StringBuilder(64);
        sb.Append(remaining).Append('|').Append(p.Energy).Append(',').Append(p.Strength).Append(',')
          .Append(p.Dexterity).Append(',').Append(p.Weak).Append(',').Append(p.Frail).Append(',')
          .Append(p.Block).Append(',').Append(p.Vulnerable).Append('|');
        foreach (var x in e) sb.Append(x.Hp).Append(':').Append(x.Block).Append(':').Append(x.Vulnerable).Append(':').Append(x.Weak).Append(':').Append(x.Capped ? x.CapRemaining : -1).Append(';');
        return sb.ToString();
    }
}
