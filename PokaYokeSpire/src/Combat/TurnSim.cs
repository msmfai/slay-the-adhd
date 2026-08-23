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
    public enum Dyn { None, BodySlam, SecondWind, Entrench, Bully }   // bespoke, state-dependent cards

    public struct Enemy
    {
        public int Hp, Block, Vulnerable, Weak, Strength;
        public int IntentDamage, IntentHits;   // this enemy's queued attack (0 if it isn't attacking)
        public bool Capped;                     // Hardened Shell: caps HP damage taken this turn…
        public int CapRemaining;                // …at this many more points (only when Capped)
        public int Doom;                        // Doom stacks: dies at end of turn if Hp <= Doom (an execute)
        public int PerHitCap;                   // Hard to Kill / Slippery / Intangible: caps EACH hit taken to this (0 = none)
        public int DamageTakenPct;              // Soar/Flutter/Guarded/Colossus: % of damage this enemy takes (0 = 100%)
        public int BlockOnFirstHit;             // Curl Up: gains this block right after first taking damage
        public bool CurlUpArmed;                // Curl Up still armed (not yet triggered)
        public int BufferHits;                  // enemy Buffer: negate this many of YOUR hits (deal 0)
        public bool Alive => Hp > 0;
    }

    public struct Player
    {
        public int Energy, Strength, Dexterity, Block;
        public int Weak, Frail, Vulnerable, Shrink;   // debuffs (presence, not magnitude)
        public bool Intangible;                       // caps EACH hit you take to 1
        public int EndTurnSelfDamage;                 // UNBLOCKABLE end-of-turn (BadLuck/Beckon HpLossVar, Regret)
        public int EndTurnSelfDamageBlockable;        // BLOCKABLE end-of-turn (Burn/Decay/Toxic DamageVar) — your block absorbs it
        public int SelfDamageThisTurn;                // HP-cost cards played (Hemokinesis/Offering/Bloodletting): unblockable
        public int Vigor;                             // flat bonus to your NEXT card attack, then consumed
        public int BlockPerAttack;                    // Rage: gain this much (Unpowered) block per Attack you play
        public int BlockPerCardPlayed;                // Afterimage: gain this block on EVERY card you play
        public int BlockPerExhaust;                   // Feel No Pain: gain this block per card exhausted
        public int FirstAttackBonusPct;               // Lethality: your FIRST attack each turn does +this %
        public bool AttackedThisTurn;                 // has any attack been played yet (for Lethality)
        public int IncomingMultPct;                   // Tank: incoming enemy-attack damage ×this % (0 = 100%)
        public int ReactiveBlock;                     // Sneaky etc.: block gained during the enemy turn (mitigates their attacks only)
        public bool HalveVulnerableEnemyDamage;       // Colossus: you take half damage from a Vulnerable attacker
        public int WeakTargetMult;                    // Tracking: your attacks vs Weak enemies ×this (0/1 = none)
        public int VulnBonusPct;                      // Cruelty: Vulnerable multiplier +this % (on top of 1.5)
        public int DamageOnBlockGain;                 // Juggernaut: deal this to an enemy whenever a card gives you block
        public int DamagePerCard;                     // Serpent Form: deal this to an enemy on every card you play
        public int StrOnHpLoss;                       // Rupture: gain this Strength when a card costs you HP
        public int BlockOnExpensiveCard;              // Danse Macabre: gain this block per card costing >= 2
        public int BufferHits;                        // Buffer: negate this many incoming hits (the largest)
        public int EnemyStrDownOnHit;                 // Monarch's Gaze: an enemy you hit loses this much Strength
        public int PanacheDmg;                        // Panache: every 5 cards played, deal this to all enemies
        public int CardsPlayedThisTurn;               // counter for Panache
        public int DamageOnDebuff;                    // Sleight of Flesh: deal this when you apply a debuff to an enemy
        public int EchoCards;                         // Echo Form: this many of your next cards deal/block twice
        public int DefendBlockBonus;                  // Fasten: +this block on Defend-tagged block cards
        public int ShivDamageBonus;                   // Accuracy: +this damage to each Shiv attack
        public int FirstShivBonus;                    // Phantom Blades: +this to the FIRST Shiv each turn
        public bool FirstShivUsed;                    // whether the first Shiv has been played
        public int StrOnColorless;                    // Arsenal: +this Strength when you play a colorless card
        public int BlockOnEthereal;                   // Spirit of Ash: +this block per Ethereal card played
        public int BlockOnDoomApplied;                // Shroud: +this block when you apply Doom
        public bool ExhaustedThisTurn;                // has any card been exhausted this turn (Evil Eye condition)
        public int DoubleNextBlockCards;              // Unmovable: this many of your next block-gaining cards are doubled
        public int HpLossReductionPerHit;             // Tungsten Rod: each hit you take loses this many points
        public int MaxHpLossThisTurn;                 // Beating Remnant: HP lost this turn is capped here (0 = uncapped)
    }

    /// A hand card reduced to its modeled effects. Damage &gt; 0 ⇒ it's an attack (a NonAttack otherwise,
    /// which is what Second Wind counts/exhausts).
    public sealed class Card
    {
        public string Name = "";
        public int Cost;
        public int Damage, Hits = 1;
        public Tgt AttackTarget = Tgt.None;
        public int Block;       // block gained now (Dex/Frail-adjusted)
        public int FlatBlock;   // Unpowered block that triggers this turn — Plating/Metallicize applied by a card
        public int StrengthGain;
        public int DexterityGain;   // Footwork/Prowess: +Dex, boosts your LATER block cards this turn
        public int VigorGain;       // Patter/PrepTime: +Vigor, spent on your next card attack
        public int GrantBlockPerAttack;   // Rage: sets BlockPerAttack for the rest of this turn
        public int EnemyStrengthLoss; public Tgt EStrTarget = Tgt.None;   // Piercing Wail etc.: softens enemy attacks
        public int ApplyVulnerable; public Tgt VulnTarget = Tgt.None;
        public int ApplyWeak; public Tgt WeakTarget = Tgt.None;
        public int EnergyGain;
        public bool Exhausts;
        public bool DoubleBlockIfExhausted;   // Evil Eye: block ×2 if a card was exhausted this turn
        public int SelfDamageOnPlay;          // Hemokinesis/Offering/Bloodletting: unblockable HP cost when played
        public bool GrantIntangible;          // Apparition/Wraith Form: become Intangible (each hit capped to 1)
        public bool RemoveEnemyBlock;         // Expose: set the target enemy's Block to 0
        public bool DoubleHitsIfTargetVulnerable;   // Dismantle: hits ×2 if the target is Vulnerable
        public bool DoubleTargetVulnerable;         // Molten Fist: after hitting, applies Vulnerable = target's current (doubles it)
        public bool IsDefend, IsShiv, IsColorless, IsEthereal;   // tags for Fasten/Accuracy/PhantomBlades/Arsenal/SpiritOfAsh
        public int ApplyDoom; public Tgt DoomTarget = Tgt.None;   // Oblivion/End of Days: end-of-turn execute
        public bool XCost;     // X-cost card (Whirlwind): spends ALL energy, hits X = energy times
        public Dyn Dynamic = Dyn.None;
        public int DynParam;   // e.g. Second Wind's block-per-exhausted-card
        public bool IsAttack => Damage > 0 || Dynamic == Dyn.BodySlam || Dynamic == Dyn.Bully;
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
    internal static int Atk(int baseDmg, int strength, bool weak, bool shrink, bool vuln, int vulnBonusPct = 0)
    {
        decimal d = baseDmg + strength;          // additive: Strength (Vigor/etc. fold in here too)
        if (d < 0m) d = 0m;
        decimal m = 1m;                           // multiplicative, applied together then floored once
        if (weak) m *= 0.75m;                     // Weak −25%
        if (shrink) m *= 0.70m;                   // Shrink −30% (flat, not a Strength cut)
        if (vuln) m *= (150 + vulnBonusPct) / 100m;   // Vulnerable +50% (+Cruelty bonus)
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
        int incoming;
        if (p.BufferHits > 0)
        {
            // Buffer negates whole instances of HP loss — model it as removing the largest incoming hits.
            var hitList = new List<int>();
            foreach (var e in enemies)
            {
                if (e.IntentDamage <= 0) continue;
                int perHit = PerHitIncoming(e, p);
                int hc = e.IntentHits < 1 ? 1 : e.IntentHits;
                for (int h = 0; h < hc; h++) hitList.Add(perHit);
            }
            hitList.Sort();   // ascending
            for (int i = 0; i < p.BufferHits && i < hitList.Count; i++) hitList[hitList.Count - 1 - i] = 0;   // negate largest
            incoming = 0; foreach (var x in hitList) incoming += x;
        }
        else
        {
            incoming = 0;
            foreach (var e in enemies)
            {
                if (e.IntentDamage <= 0) continue;   // NB: no !e.Alive check — killing isn't defense
                incoming += PerHitIncoming(e, p) * (e.IntentHits < 1 ? 1 : e.IntentHits);
            }
        }
        // Blockable end-of-turn damage (Burn/Decay/Toxic) ticks at YOUR turn end → your block absorbs it
        // FIRST; the enemy attacks then hit whatever block is left (plus reactive block gained on their turn).
        int blockLeft = p.Block - p.EndTurnSelfDamageBlockable;
        int burnNet = blockLeft < 0 ? -blockLeft : 0;
        if (blockLeft < 0) blockLeft = 0;
        int enemyNet = incoming - blockLeft - p.ReactiveBlock;   // ReactiveBlock (Sneaky): gained during the enemy turn
        if (enemyNet < 0) enemyNet = 0;
        int net = burnNet + enemyNet + p.EndTurnSelfDamage + p.SelfDamageThisTurn;   // + unblockable (HpLossVar/Regret/HP-cost)
        if (p.MaxHpLossThisTurn > 0 && net > p.MaxHpLossThisTurn) net = p.MaxHpLossThisTurn;   // Beating Remnant cap
        return net;
    }

    /// One enemy hit's HP damage to the player: Weak/Vulnerable, then Tank (×), Intangible (cap 1), Tungsten (−N).
    private static int PerHitIncoming(in Enemy e, in Player p)
    {
        int perHit = Atk(e.IntentDamage, e.Strength, e.Weak > 0, false, p.Vulnerable > 0);
        if (p.HalveVulnerableEnemyDamage && e.Vulnerable > 0) perHit /= 2;      // Colossus: half from a Vulnerable attacker
        if (p.IncomingMultPct > 0) perHit = perHit * p.IncomingMultPct / 100;   // Tank: incoming ×2
        if (p.Intangible && perHit > 1) perHit = 1;                              // Intangible caps EACH hit to 1
        perHit -= p.HpLossReductionPerHit;                                       // Tungsten Rod: −N per instance
        return perHit < 0 ? 0 : perHit;
    }

    /// Public entry: a lean STATIC analyzer picks the cheapest CORRECT method for this hand. A hand with no
    /// order-dependence against a single un-capped enemy resolves in closed form (two small knapsacks — no
    /// search); everything else uses the exhaustive pruned DFS with exactly the prunes that stay valid.
    ///
    /// DEMONIC HEURISTIC: anything uncertain or random is resolved to the player's WORST outcome. This is
    /// realised by EXCLUSION in the reader — a card whose result can't be known (random target/selection,
    /// draw, unmodeled effect) contributes 0 to offense and 0 mitigation to defense, i.e. the worst case —
    /// so the solver only ever sees a deterministic, conservative hand. (Superseded the earlier best-case
    /// rule for random in-hand effects.)
    public static Result Solve(Player player, Enemy[] enemies, IReadOnlyList<Card> hand, int nodeCap = 200000)
    {
        if (IsTrivialSingleEnemy(player, enemies, hand)) return FastSolveTrivial(player, enemies, hand);
        return SolveDfs(player, enemies, hand, nodeCap);
    }

    /// The exhaustive pruned depth-first solve — correct for ALL hands; the fast path above is only taken
    /// when the analyzer proves it equivalent (see TrivialFastPath_MatchesDfs).
    internal static Result SolveDfs(Player player, Enemy[] enemies, IReadOnlyList<Card> hand, int nodeCap = 200000)
    {
        int n = enemies.Length;
        var initialHp = new int[n];
        for (int i = 0; i < n; i++) initialHp[i] = enemies[i].Hp;

        int[] cardClass = ClassifyCards(hand);   // identical cards → same class (played in one canonical order)

        var best = new Result { MaxDamage = 0, MaxPerEnemy = new int[n], MinHpLost = HpLost(player, enemies) };
        var visited = new HashSet<string>();
        int nodes = 0;
        ulong fullMask = hand.Count >= 64 ? ulong.MaxValue : (1UL << hand.Count) - 1;
        // The T3 setup-before-payload prune assumes non-attacks are best played first. Cards whose value
        // depends on turn HISTORY (Evil Eye: block doubled if you exhausted — the exhaust source may be an
        // attack that must come first) break that assumption, so relax the prune when one is in hand.
        bool relaxOrderPrune = false;
        foreach (var c in hand) if (c.DoubleBlockIfExhausted) { relaxOrderPrune = true; break; }
        Recurse(player, enemies, hand, cardClass, fullMask, initialHp, ref best, visited, ref nodes, nodeCap, payloadPlayed: false, relaxOrderPrune);
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

    // ── lean analyzer + closed-form fast path ─────────────────────────────────────────────────────
    /// "Trivial" ⇒ nothing is order-dependent: exactly one un-capped enemy, no pending player buffs, and
    /// every card is a PURE attack or PURE block (no buff, scaling, conditional, status, X-cost, energy-gain
    /// or self-effect). Offense and defense then each reduce to a single 0/1 knapsack over energy — no DFS,
    /// no visited-set. Proven equal to the DFS by the TrivialFastPath_MatchesDfs cross-check test.
    private static bool IsTrivialSingleEnemy(in Player p, Enemy[] enemies, IReadOnlyList<Card> hand)
    {
        if (enemies.Length != 1 || enemies[0].Capped || enemies[0].PerHitCap > 0 || enemies[0].DamageTakenPct > 0) return false;
        if (enemies[0].BlockOnFirstHit > 0 || enemies[0].BufferHits > 0) return false;
        if (p.Vigor != 0 || p.BlockPerAttack != 0 || p.DoubleNextBlockCards != 0) return false;
        if (p.BlockPerCardPlayed != 0 || p.BlockPerExhaust != 0 || p.FirstAttackBonusPct != 0 || p.IncomingMultPct != 0) return false;
        if (p.WeakTargetMult != 0 || p.VulnBonusPct != 0 || p.DamageOnBlockGain != 0 || p.DamagePerCard != 0
            || p.StrOnHpLoss != 0 || p.BlockOnExpensiveCard != 0 || p.EnemyStrDownOnHit != 0) return false;
        if (p.PanacheDmg != 0 || p.DamageOnDebuff != 0 || p.EchoCards != 0 || p.DefendBlockBonus != 0
            || p.ShivDamageBonus != 0 || p.FirstShivBonus != 0 || p.StrOnColorless != 0 || p.BlockOnEthereal != 0 || p.BlockOnDoomApplied != 0) return false;
        foreach (var c in hand) if (!IsPureAttack(c) && !IsPureBlock(c)) return false;
        return true;
    }

    private static bool NoSideEffects(Card c) =>
        c.FlatBlock == 0 && c.Dynamic == Dyn.None && !c.XCost && c.EnergyGain == 0
        && c.StrengthGain == 0 && c.DexterityGain == 0 && c.VigorGain == 0 && c.GrantBlockPerAttack == 0
        && c.EnemyStrengthLoss == 0 && c.ApplyVulnerable == 0 && c.ApplyWeak == 0 && c.ApplyDoom == 0
        && c.SelfDamageOnPlay == 0 && !c.GrantIntangible && !c.RemoveEnemyBlock
        && !c.DoubleBlockIfExhausted && !c.DoubleHitsIfTargetVulnerable && !c.DoubleTargetVulnerable;

    private static bool IsPureAttack(Card c) => c.Damage > 0 && c.AttackTarget != Tgt.None && c.Block == 0 && NoSideEffects(c);
    private static bool IsPureBlock(Card c) => c.Block > 0 && c.Damage == 0 && NoSideEffects(c);

    private static Result FastSolveTrivial(Player p, Enemy[] enemies, IReadOnlyList<Card> hand)
    {
        var e = enemies[0];
        int energy = p.Energy < 0 ? 0 : p.Energy;

        // offense: knapsack the most raw attack damage affordable; the enemy's block absorbs the total
        // (per-hit vs total is equivalent with no per-hit cap), and HP damage can't exceed its HP.
        int nAtk = 0; foreach (var c in hand) if (c.Damage > 0) nAtk++;
        var aCost = new int[nAtk]; var aVal = new int[nAtk]; int ai = 0;
        foreach (var c in hand) if (c.Damage > 0)
        {
            int per = Atk(c.Damage, p.Strength, p.Weak > 0, p.Shrink > 0, e.Vulnerable > 0);
            aCost[ai] = c.Cost < 0 ? 0 : c.Cost; aVal[ai] = per * (c.Hits < 1 ? 1 : c.Hits); ai++;
        }
        int rawDmg = Knapsack(energy, aCost, aVal);
        int hpDmg = rawDmg - e.Block; if (hpDmg < 0) hpDmg = 0; if (hpDmg > e.Hp) hpDmg = e.Hp;
        bool kill = rawDmg - e.Block >= e.Hp;

        // defense: knapsack the most block affordable; more block only ever helps, so max block = min HP lost.
        int nBlk = 0; foreach (var c in hand) if (c.Block > 0) nBlk++;
        var bCost = new int[nBlk]; var bVal = new int[nBlk]; int bi = 0;
        foreach (var c in hand) if (c.Block > 0)
        {
            bCost[bi] = c.Cost < 0 ? 0 : c.Cost; bVal[bi] = Blk(c.Block, p.Dexterity, p.Frail > 0); bi++;
        }
        var pd = p; pd.Block += Knapsack(energy, bCost, bVal);

        return new Result { MaxDamage = hpDmg, MaxPerEnemy = new[] { hpDmg }, MinHpLost = HpLost(pd, enemies), CanKillAll = kill, Nodes = 0, Truncated = false };
    }

    /// Standard 0/1 knapsack: max total value with total cost ≤ cap. cap and item counts are tiny (energy
    /// ≤ ~6, hand ≤ 10), so this is microseconds and allocation-light.
    private static int Knapsack(int cap, int[] cost, int[] val)
    {
        var dp = new int[cap + 1];
        for (int i = 0; i < cost.Length; i++)
            for (int w = cap; w >= cost[i]; w--)
                if (dp[w - cost[i]] + val[i] > dp[w]) dp[w] = dp[w - cost[i]] + val[i];
        return dp[cap];
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
        && a.FlatBlock == b.FlatBlock && a.StrengthGain == b.StrengthGain && a.DexterityGain == b.DexterityGain
        && a.VigorGain == b.VigorGain && a.GrantBlockPerAttack == b.GrantBlockPerAttack
        && a.EnemyStrengthLoss == b.EnemyStrengthLoss && a.EStrTarget == b.EStrTarget
        && a.ApplyVulnerable == b.ApplyVulnerable && a.VulnTarget == b.VulnTarget
        && a.ApplyWeak == b.ApplyWeak && a.WeakTarget == b.WeakTarget && a.EnergyGain == b.EnergyGain && a.XCost == b.XCost
        && a.Exhausts == b.Exhausts && a.DoubleBlockIfExhausted == b.DoubleBlockIfExhausted
        && a.SelfDamageOnPlay == b.SelfDamageOnPlay && a.GrantIntangible == b.GrantIntangible
        && a.RemoveEnemyBlock == b.RemoveEnemyBlock && a.DoubleHitsIfTargetVulnerable == b.DoubleHitsIfTargetVulnerable
        && a.DoubleTargetVulnerable == b.DoubleTargetVulnerable
        && a.ApplyDoom == b.ApplyDoom && a.DoomTarget == b.DoomTarget
        && a.IsDefend == b.IsDefend && a.IsShiv == b.IsShiv && a.IsColorless == b.IsColorless && a.IsEthereal == b.IsEthereal
        && a.Dynamic == b.Dynamic && a.DynParam == b.DynParam;

    private static bool SameSig(in Enemy a, in Enemy b) =>
        a.Hp == b.Hp && a.Block == b.Block && a.Vulnerable == b.Vulnerable && a.Weak == b.Weak && a.Strength == b.Strength
        && a.IntentDamage == b.IntentDamage && a.IntentHits == b.IntentHits && a.Doom == b.Doom
        && a.PerHitCap == b.PerHitCap && a.DamageTakenPct == b.DamageTakenPct
        && a.BlockOnFirstHit == b.BlockOnFirstHit && a.CurlUpArmed == b.CurlUpArmed && a.BufferHits == b.BufferHits
        && a.Capped == b.Capped && (!a.Capped || a.CapRemaining == b.CapRemaining);

    private static void Recurse(Player p, Enemy[] enemies, IReadOnlyList<Card> hand, int[] cardClass, ulong remaining,
                                int[] initialHp, ref Result best, HashSet<string> visited, ref int nodes, int cap, bool payloadPlayed,
                                bool relaxOrderPrune)
    {
        // evaluate THIS reachable state (you can stop playing at any point)
        int totalDealt = 0;
        bool allDead = true;
        for (int i = 0; i < enemies.Length; i++)
        {
            int effHp = enemies[i].Hp < 0 ? 0 : enemies[i].Hp;
            if (enemies[i].Doom > 0 && effHp <= enemies[i].Doom) effHp = 0;   // Doom executes at end of turn → counts as killed
            int dealt = initialHp[i] - effHp;
            if (dealt < 0) dealt = 0;
            if (dealt > best.MaxPerEnemy[i]) best.MaxPerEnemy[i] = dealt;
            totalDealt += dealt;
            if (effHp > 0) allDead = false;
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
            if (!relaxOrderPrune && payloadPlayed && !c.IsAttack) continue;

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
                    Recurse(np, ne, hand, cardClass, nr, initialHp, ref best, visited, ref nodes, cap, payloadPlayed: true, relaxOrderPrune);
                    if (nodes >= cap) return;
                }
            }
            else
            {
                nodes++;
                var (np, ne, nr) = Play(p, enemies, hand, remaining, i, -1);
                Recurse(np, ne, hand, cardClass, nr, initialHp, ref best, visited, ref nodes, cap, payloadPlayed || c.IsAttack, relaxOrderPrune);
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
        if (c.Dynamic == Dyn.Bully)                      // Bully: base + ExtraDamage × the TARGET's Vulnerable
        {
            int bt = target >= 0 ? target : FirstAlive(e);
            int bvuln = (bt >= 0 && bt < e.Length) ? e[bt].Vulnerable : 0;
            dmg = c.Damage + c.DynParam * bvuln;         // the 1.5× Vulnerable multiplier is applied by HitEnemy on top
        }
        if (c.IsShiv && dmg > 0)                         // Accuracy (+all Shivs) / Phantom Blades (+first Shiv)
        {
            dmg += p.ShivDamageBonus;
            if (!p.FirstShivUsed && p.FirstShivBonus > 0) dmg += p.FirstShivBonus;
        }

        int echoTimes = 1;                              // Echo Form: the first N cards play twice
        if (p.EchoCards > 0) { echoTimes = 2; p.EchoCards--; }

        if (dmg > 0)
        {
            int vigor = p.Vigor;                        // Vigor: flat bonus to THIS (your next) card attack…
            int outMult = (!p.AttackedThisTurn && p.FirstAttackBonusPct > 0) ? 100 + p.FirstAttackBonusPct : 100;   // Lethality: FIRST attack only
            int hits = c.XCost ? xHits : (c.Hits < 1 ? 1 : c.Hits);
            if (c.DoubleHitsIfTargetVulnerable && target >= 0 && target < e.Length && e[target].Vulnerable > 0) hits *= 2;   // Dismantle
            for (int rep = 0; rep < echoTimes; rep++)
            {
                if (c.AttackTarget == Tgt.AllEnemies)
                {
                    for (int t = 0; t < e.Length; t++) if (e[t].Alive) HitEnemy(ref e[t], dmg, hits, p, vigor, outMult);
                }
                else
                {
                    int t = target >= 0 ? target : FirstAlive(e);
                    if (t >= 0) HitEnemy(ref e[t], dmg, hits, p, vigor, outMult);
                }
                outMult = 100;                          // Lethality applies only to the first swing
            }
            p.Vigor = 0;                                // …then consumed, whatever the target
            p.AttackedThisTurn = true;                  // the first attack (and its Lethality bonus) is now spent
            if (c.IsShiv) p.FirstShivUsed = true;
            if (p.EnemyStrDownOnHit > 0)                // Monarch's Gaze: enemies you hit lose Strength
            {
                if (c.AttackTarget == Tgt.AllEnemies) { for (int t = 0; t < e.Length; t++) if (e[t].Alive) e[t].Strength -= p.EnemyStrDownOnHit; }
                else { int mt = target >= 0 ? target : FirstAlive(e); if (mt >= 0) e[mt].Strength -= p.EnemyStrDownOnHit; }
            }
        }

        if (c.Block > 0)
        {
            int b = Blk(c.Block + (c.IsDefend ? p.DefendBlockBonus : 0), p.Dexterity, p.Frail > 0);   // Fasten
            if (c.DoubleBlockIfExhausted && p.ExhaustedThisTurn) b *= 2;   // Evil Eye: doubled if you exhausted a card
            if (p.DoubleNextBlockCards > 0) { b *= 2; p.DoubleNextBlockCards--; }   // Unmovable: first N block cards doubled
            b *= echoTimes;                             // Echo Form: play the block twice
            p.Block += b;
            if (p.DamageOnBlockGain > 0) DealFlat(ref e, p.DamageOnBlockGain, target);   // Juggernaut
        }
        if (c.Cost >= 2 && p.BlockOnExpensiveCard > 0) p.Block += p.BlockOnExpensiveCard;   // Danse Macabre
        if (c.FlatBlock != 0) p.Block += c.FlatBlock;   // Plating gained this turn: Unpowered, no Dex/Frail
        if (c.IsAttack && p.BlockPerAttack > 0) p.Block += p.BlockPerAttack;   // Rage: block after each attack (Unpowered)
        if (c.Dynamic == Dyn.Entrench) p.Block *= 2;    // Entrench: double current block

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
            if (exhausted > 0) { p.ExhaustedThisTurn = true; p.Block += p.BlockPerExhaust * exhausted; }   // Evil Eye + Feel No Pain
        }

        if (c.StrengthGain != 0) p.Strength += c.StrengthGain;
        if (c.DexterityGain != 0) p.Dexterity += c.DexterityGain;   // boosts your LATER block cards
        if (c.VigorGain != 0) p.Vigor += c.VigorGain;
        if (c.GrantBlockPerAttack != 0) p.BlockPerAttack += c.GrantBlockPerAttack;   // Rage: arm block-per-attack
        if (c.IsColorless && p.StrOnColorless > 0) p.Strength += p.StrOnColorless;   // Arsenal
        if (c.IsEthereal && p.BlockOnEthereal > 0) p.Block += p.BlockOnEthereal;     // Spirit of Ash
        ApplyStrengthLoss(ref e, c.EnemyStrengthLoss, c.EStrTarget, target);   // Piercing Wail etc. (signed)
        ApplyStatus(ref e, c.ApplyVulnerable, c.VulnTarget, target, isVuln: true);
        if (c.DoubleTargetVulnerable)   // Molten Fist: adds Vulnerable equal to the target's current (doubling it)
        {
            int mt = target >= 0 ? target : FirstAlive(e);
            if (mt >= 0 && mt < e.Length && e[mt].Alive && e[mt].Vulnerable > 0) e[mt].Vulnerable += e[mt].Vulnerable;
        }
        ApplyStatus(ref e, c.ApplyWeak, c.WeakTarget, target, isVuln: false);
        ApplyDoomTo(ref e, c.ApplyDoom, c.DoomTarget, target);   // Oblivion/End of Days: end-of-turn execute
        if (c.ApplyDoom > 0 && p.BlockOnDoomApplied > 0) p.Block += p.BlockOnDoomApplied;   // Shroud
        if (p.DamageOnDebuff > 0 && (c.ApplyVulnerable > 0 || c.ApplyWeak > 0 || c.EnemyStrengthLoss > 0))
            DealFlat(ref e, p.DamageOnDebuff, target);   // Sleight of Flesh

        if (c.SelfDamageOnPlay != 0) { p.SelfDamageThisTurn += c.SelfDamageOnPlay; if (p.StrOnHpLoss > 0) p.Strength += p.StrOnHpLoss; }   // Hemokinesis/Offering + Rupture
        if (c.GrantIntangible) p.Intangible = true;                                // Apparition/Wraith Form
        if (c.RemoveEnemyBlock) { int rt = target >= 0 ? target : FirstAlive(e); if (rt >= 0) e[rt].Block = 0; }   // Expose

        if (c.Exhausts) { p.ExhaustedThisTurn = true; p.Block += p.BlockPerExhaust; }   // Evil Eye condition + Feel No Pain
        if (p.BlockPerCardPlayed > 0) p.Block += p.BlockPerCardPlayed;                   // Afterimage: block on every card played
        if (p.DamagePerCard > 0) DealFlat(ref e, p.DamagePerCard, -1);                    // Serpent Form: damage on every card played
        if (p.PanacheDmg > 0)                                                            // Panache: every 5 cards, hit all
        {
            p.CardsPlayedThisTurn++;
            if (p.CardsPlayedThisTurn % 5 == 0)
                for (int t = 0; t < e.Length; t++) if (e[t].Alive) DealFlat(ref e, p.PanacheDmg, t);
        }

        remaining &= ~(1UL << cardIndex);
        return (p, e, remaining);
    }

    private static void HitEnemy(ref Enemy e, int baseDmg, int hits, in Player p, int flatBonus = 0, int outMultPct = 100)
    {
        for (int h = 0; h < hits; h++)
        {
            if (e.BufferHits > 0) { e.BufferHits--; continue; }   // enemy Buffer negates this whole hit
            int dmg = Atk(baseDmg, p.Strength + flatBonus, p.Weak > 0, p.Shrink > 0, e.Vulnerable > 0, p.VulnBonusPct);
            if (e.Weak > 0 && p.WeakTargetMult > 1) dmg *= p.WeakTargetMult;   // Tracking: ×vs Weak enemies
            if (outMultPct != 100) dmg = dmg * outMultPct / 100;           // Lethality: first-attack multiplier
            if (e.DamageTakenPct > 0) dmg = dmg * e.DamageTakenPct / 100;   // Soar/Flutter/Guarded/Colossus: reduce damage taken
            if (e.PerHitCap > 0 && dmg > e.PerHitCap) dmg = e.PerHitCap;   // Hard to Kill/Slippery: cap EACH hit (per-hit, no depletion)
            if (e.Capped && dmg > e.CapRemaining) dmg = e.CapRemaining;   // Hardened Shell: cap this turn (per-turn total)
            int afterBlock = dmg - e.Block;
            if (afterBlock <= 0) { e.Block -= dmg; if (e.Block < 0) e.Block = 0; continue; }
            e.Block = 0;
            e.Hp -= afterBlock;
            if (e.Capped) e.CapRemaining -= afterBlock;   // consumed part of the per-turn allowance
            if (e.CurlUpArmed) { e.Block += e.BlockOnFirstHit; e.CurlUpArmed = false; }   // Curl Up: block after first hit
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

    /// Enemy Strength loss (Piercing Wail, Dark Shackles, …): lowers each targeted enemy's attack. Their
    /// Strength floors at −(their base intent) implicitly via Atk's own clamp, so we just subtract here.
    private static void ApplyStrengthLoss(ref Enemy[] e, int amount, Tgt tgt, int target)
    {
        if (amount == 0 || tgt == Tgt.None) return;   // signed: +N lowers the enemy's attack, −N (Fight Me) raises it
        if (tgt == Tgt.AllEnemies)
            for (int t = 0; t < e.Length; t++) { if (e[t].Alive) e[t].Strength -= amount; }
        else
        {
            int t = target >= 0 ? target : FirstAlive(e);
            if (t >= 0) e[t].Strength -= amount;
        }
    }

    /// Flat, Unpowered damage to one enemy (Juggernaut / Serpent Form / Panache): no Strength/Weak/Vuln,
    /// but still absorbed by block and clamped by the enemy's damage-reduction / per-hit / turn caps.
    private static void DealFlat(ref Enemy[] e, int amount, int target)
    {
        if (amount <= 0) return;
        int t = target >= 0 ? target : FirstAlive(e);
        if (t < 0) return;
        int dmg = amount;
        if (e[t].DamageTakenPct > 0) dmg = dmg * e[t].DamageTakenPct / 100;
        if (e[t].PerHitCap > 0 && dmg > e[t].PerHitCap) dmg = e[t].PerHitCap;
        if (e[t].Capped && dmg > e[t].CapRemaining) dmg = e[t].CapRemaining;
        int afterBlock = dmg - e[t].Block;
        if (afterBlock <= 0) { e[t].Block -= dmg; if (e[t].Block < 0) e[t].Block = 0; return; }
        e[t].Block = 0; e[t].Hp -= afterBlock;
        if (e[t].Capped) e[t].CapRemaining -= afterBlock;
    }

    private static void ApplyDoomTo(ref Enemy[] e, int amount, Tgt tgt, int target)
    {
        if (amount <= 0 || tgt == Tgt.None) return;
        if (tgt == Tgt.AllEnemies)
            for (int t = 0; t < e.Length; t++) { if (e[t].Alive) e[t].Doom += amount; }
        else
        {
            int t = target >= 0 ? target : FirstAlive(e);
            if (t >= 0) e[t].Doom += amount;
        }
    }

    private static int FirstAlive(Enemy[] e) { for (int i = 0; i < e.Length; i++) if (e[i].Alive) return i; return -1; }

    private static string StateKey(in Player p, Enemy[] e, ulong remaining)
    {
        var sb = new StringBuilder(64);
        sb.Append(remaining).Append('|').Append(p.Energy).Append(',').Append(p.Strength).Append(',')
          .Append(p.Dexterity).Append(',').Append(p.Weak).Append(',').Append(p.Frail).Append(',')
          .Append(p.Block).Append(',').Append(p.Vulnerable).Append(',').Append(p.Vigor).Append(',').Append(p.BlockPerAttack).Append(',').Append(p.ExhaustedThisTurn ? 1 : 0)
          .Append(',').Append(p.SelfDamageThisTurn).Append(',').Append(p.Intangible ? 1 : 0).Append(',').Append(p.DoubleNextBlockCards).Append(',').Append(p.AttackedThisTurn ? 1 : 0)
          .Append(',').Append(p.CardsPlayedThisTurn).Append(',').Append(p.EchoCards).Append(',').Append(p.FirstShivUsed ? 1 : 0).Append('|');
        foreach (var x in e) sb.Append(x.Hp).Append(':').Append(x.Block).Append(':').Append(x.Vulnerable).Append(':').Append(x.Weak).Append(':').Append(x.Strength).Append(':').Append(x.Doom).Append(':').Append(x.CurlUpArmed ? x.BufferHits + 1000 : x.BufferHits).Append(':').Append(x.Capped ? x.CapRemaining : -1).Append(';');
        return sb.ToString();
    }
}
