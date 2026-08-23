using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MegaCrit.Sts2.Core.Combat;             // CombatState, CombatManager, CombatSide
using MegaCrit.Sts2.Core.Context;            // LocalContext
using MegaCrit.Sts2.Core.Entities.Cards;     // CardType
using MegaCrit.Sts2.Core.Entities.Creatures; // Creature
using MegaCrit.Sts2.Core.Entities.Players;   // Player, PlayerCombatState
using MegaCrit.Sts2.Core.Models;             // CardModel
using PokaYokeSpire.Combat;                   // TurnSim, TurnSimReader
using PokaYokeSpire.Core;                     // Config, DebugLog

namespace PokaYokeSpire.Features;

/// <summary>
/// SELF-AUDIT — the play-and-fix loop. Each turn this freezes the gems' prediction from the true turn-START
/// hand (once the draw finishes and you may act), measures what ACTUALLY happened, and flags any turn where
/// reality broke a bound the solver guarantees — writing a full-state report to hand back for a fix.
///
/// ROBUST TO NEW INFORMATION (the whole point). Drawing/creating a card mid-turn gives you options the frozen
/// hand didn't have, which could legitimately beat the frozen prediction — so instead of blanket-suppressing
/// those turns, we RE-SOLVE at finalize over the UNION of everything revealed (frozen hand ∪ every card that
/// entered your hand this turn) and check the actual result against THAT. A draw only excuses a violation if
/// the drawn cards actually account for it:
///
///   • OFFENSE — you can't deal more than the max over the revealed union. <c>actualDealt[i] &gt; omniMax[i]</c>
///     ⇒ even knowing every card you drew, the sim can't produce that damage ⇒ a real under-model (unmodelled
///     enchant/power/relic). If you dealt 47 but the card you drew was a Defend, the re-solve still says ~20
///     and it flags — correctly. Measured at end-of-turn, before enemies act.
///   • DEFENSE — you can't lose fewer HP than the min over the revealed union. <c>actualHpLost &lt; omniMin</c>
///     ⇒ even with every drawn defence option, the sim thought more damage was unavoidable ⇒ a real
///     over-model. Measured across the enemy turn, at the next turn start.
///
/// The re-solve over-approximates capability (all revealed cards at once, on the frozen energy), which only
/// LOOSENS both bounds — so it strictly reduces false positives while preserving every real-bug catch. Two
/// confounds it can't fold in as cards remain suppressors: a POTION (off-hand damage/block/draw the sim never
/// models) suppresses both; HEALING (raises net HP, confounding the loss measurement) suppresses defense.
///
/// Opt-in (<see cref="Config.TurnAudit"/>), log-only: reads state the driver already snapshots, writes to
/// modding/logs/ (audit-summary.log one line/turn; mismatch-{offense,defense}-*.txt on a real violation).
/// </summary>
internal static class TurnAudit
{
    private sealed class Turn
    {
        public TurnSim.Result Pred;                   // frozen turn-start prediction (for the report's before/after)
        public TurnSimReader.Snapshot Snap = null!;   // the FROZEN turn-start snapshot
        public int PlayerHpStart;
        public int[] EnemyBaselineHp = Array.Empty<int>();   // index-aligned with Snap.EnemyRefs
        public bool Corruption;                        // CorruptionPower at freeze ⇒ revealed skills cost 0 + exhaust

        // Union of everything revealed this turn, and the models we've already folded in (by instance).
        public readonly List<TurnSim.Card> OmniCards = new();
        public readonly HashSet<CardModel> SeenModels = new();   // reference identity — ALL raw cards seen (parsed or not)
        public TurnSim.Result OmniPred;               // lazily solved over OmniCards (see EnsureSolved)
        public bool OmniSolved;

        // ── confound annotations ── Drew/Created are informational (the re-solve handles them); Potion/Healed
        // are true suppressors (non-card effects the re-solve can't account for).
        public int HandStartCount, DrawPileStart, LastDrawCount, TotalCardsStart, PotionCountStart, BaseDiscExh;
        public bool Drew, Created, UsedPotion, Healed;

        public bool OffenseDone;
        public string OffenseSummary = "offense=?";
        public bool OffenseOverSuppressed;   // an over-omni-max hit was seen but a potion suppressed the flag
    }

    private static bool _subscribed;
    private static int _lastArmedRound = int.MinValue;   // arm exactly once per RoundNumber (no mid-turn re-arm)
    private static bool _armed;   // re-capturing the pre-play hand; locks on your first play of the turn
    private static Turn? _cur;

    // ── entry point, called from TurnSimDriverFeature.Submit on every combat-state change ──
    public static void Observe(TurnSimReader.Snapshot? snap, CombatState state)
    {
        if (!Config.TurnAudit) { _cur = null; _lastArmedRound = int.MinValue; _armed = false; return; }
        try
        {
            EnsureSubscribed();
            var side = state.CurrentSide;
            if (side != CombatSide.Player) return;   // only the player's own turn is audited

            // Arm ONCE per round (RoundNumber is stable within a turn, so CurrentSide flickering during card
            // resolution can't re-arm us mid-turn — the bug that was re-freezing a played-down hand).
            int round = RoundNumber(state);
            if (round != _lastArmedRound)
            {
                FinalizeDefenseAndSummary(state);   // close out the turn that just ended
                _cur = null;
                _armed = true;
                _lastArmedRound = round;
            }

            // While armed, RE-CAPTURE the hand each observation (it grows as the turn-start draw lands) and
            // LOCK the moment you first play a card — so the frozen hand is the full pre-play hand, never a
            // partial mid-draw one nor a played-down one. (Observes are event-driven; the fully-drawn hand may
            // emit no "stable" event of its own, so we track growth instead of waiting for stability.)
            if (_armed && snap != null)
            {
                var c = ReadCounts(state);
                if (_cur != null && Played(c, _cur)) _armed = false;   // first play → lock the last pre-play freeze
                else _cur = Freeze(snap, state, c);                    // re-capture the (still-growing) hand
            }

            // After the freeze is locked, fold genuinely mid-turn-revealed cards into the union.
            if (_cur != null && !_armed)
                Accumulate(_cur, state);
        }
        catch (Exception e) { DebugLog.Error("TurnAudit.Observe", e); }
    }

    private static void EnsureSubscribed()
    {
        if (_subscribed) return;
        var cm = CombatManager.Instance;
        if (cm == null) return;
        cm.PlayerEndedTurn += OnPlayerEndedTurn;
        // New combat re-uses RoundNumber 1, so reset the arming gate at each combat start.
        cm.CombatSetUp += _ => { _lastArmedRound = int.MinValue; _cur = null; _armed = false; };
        _subscribed = true;
    }

    private static int RoundNumber(CombatState state)
    {
        try { return state.RoundNumber; } catch { return int.MinValue; }
    }

    // Has a card left your hand since the freeze? (a play sends it to discard/exhaust, or removes a power, or
    // just shrinks the hand). Sensitive on purpose: locking a hair early only makes omni fold the rest back
    // in via Accumulate, whereas missing a play would drop a played card from the union.
    private static bool Played(in Counts c, Turn t)
        => c.Hand < t.HandStartCount || (c.Disc + c.Exh) > t.BaseDiscExh || c.Total < t.TotalCardsStart;

    // Fires the instant the player commits End Turn — enemy HP now reflects your plays, before enemies act.
    private static void OnPlayerEndedTurn(Player _, bool __)
    {
        if (!Config.TurnAudit) return;
        try { FinalizeOffense(); }
        catch (Exception e) { DebugLog.Error("TurnAudit.OnPlayerEndedTurn", e); }
    }

    // Cheap: just snapshot the pre-play state (re-run each observation until the first play locks it). The
    // predictions are solved lazily once at finalize (EnsureSolved), so re-capturing costs no solver time.
    private static Turn Freeze(TurnSimReader.Snapshot snap, CombatState state, in Counts c)
    {
        var t = new Turn
        {
            Snap = snap,
            PlayerHpStart = PlayerHp(state),
            EnemyBaselineHp = new int[snap.EnemyRefs.Count],
            Corruption = HasPower(state, "CorruptionPower"),
            HandStartCount = c.Hand,
            DrawPileStart = c.Draw,
            LastDrawCount = c.Draw,
            TotalCardsStart = c.Total,
            PotionCountStart = c.Potions,
            BaseDiscExh = c.Disc + c.Exh,
        };
        for (int i = 0; i < snap.EnemyRefs.Count; i++)
            t.EnemyBaselineHp[i] = SafeHp(snap.EnemyRefs[i]);

        // Seed the union with the frozen hand: mark its models seen, copy its interpreted cards.
        var hand = RawHand(state);
        if (hand != null) foreach (var cm in hand) t.SeenModels.Add(cm);
        t.OmniCards.AddRange(snap.Hand);
        return t;
    }

    // Fold any card now in hand that we haven't seen this turn into the union (a draw or a created card), and
    // update the confound annotations. Reading each new card ONCE, while it's in hand and readable.
    private static void Accumulate(Turn t, CombatState state)
    {
        if (PlayerHp(state) > t.PlayerHpStart) t.Healed = true;

        var c = ReadCounts(state);
        if (c.Draw < t.LastDrawCount || c.Draw < t.DrawPileStart) t.Drew = true;
        t.LastDrawCount = c.Draw;
        if (c.Total > t.TotalCardsStart) t.Created = true;   // cards created into combat (Shivs, …)
        if (c.Potions < t.PotionCountStart) t.UsedPotion = true;

        var hand = RawHand(state);
        if (hand == null) return;
        foreach (var cm in hand)
        {
            if (!t.SeenModels.Add(cm)) continue;   // already folded in
            t.Drew = true;
            var card = TurnSimReader.ReadCard(cm);
            if (card == null) continue;
            if (t.Corruption && SafeType(cm) == CardType.Skill) { card.Cost = 0; card.Exhausts = true; }
            t.OmniCards.Add(card);
        }
    }

    // Solve both predictions lazily, once (offense finalises first, defense reuses them): the frozen hand (for
    // the report's before/after) and the revealed union (the authoritative bound the checks use).
    private static void EnsureSolved(Turn t)
    {
        if (t.OmniSolved) return;
        t.OmniSolved = true;
        try { t.Pred = TurnSim.Solve(t.Snap.Player, t.Snap.Enemies, t.Snap.Hand, nodeCap: 200000); }
        catch (Exception e) { DebugLog.Error("TurnAudit.EnsureSolved/frozen", e); t.Pred = default; }
        try { t.OmniPred = TurnSim.Solve(t.Snap.Player, t.Snap.Enemies, t.OmniCards, nodeCap: 200000); }
        catch (Exception e) { DebugLog.Error("TurnAudit.EnsureSolved/omni", e); t.OmniPred = t.Pred; }
    }

    private static void FinalizeOffense()
    {
        var t = _cur;
        if (t == null || t.OffenseDone) return;
        t.OffenseDone = true;
        EnsureSolved(t);

        var refs = t.Snap.EnemyRefs;
        var fmax = t.Pred.MaxPerEnemy ?? Array.Empty<int>();
        var omax = t.OmniPred.MaxPerEnemy ?? fmax;
        var dealt = new int[refs.Count];
        var over = new List<int>();
        for (int i = 0; i < refs.Count; i++)
        {
            int d = t.EnemyBaselineHp[i] - SafeHp(refs[i]);
            if (d < 0) d = 0;
            dealt[i] = d;
            int cap = i < omax.Length ? omax[i] : 0;
            if (d > cap) over.Add(i);   // beat the re-solve's max — the draw doesn't explain it
        }
        t.OffenseSummary = $"dealt=[{string.Join(",", dealt)}] vs max=[{string.Join(",", fmax)}]→omni[{string.Join(",", omax)}]";

        if (over.Count == 0) return;
        if (t.UsedPotion) { t.OffenseOverSuppressed = true; return; }   // potion damage the sim never modelled

        var sb = new StringBuilder();
        sb.AppendLine("VIOLATION: OFFENSE — actual damage dealt exceeded the sim's max even after re-solving over");
        sb.AppendLine("every card you drew this turn — so the extra damage is NOT explained by new information.");
        foreach (int i in over)
            sb.AppendLine($"    enemy[{i}]: dealt {dealt[i]} > omni-max {(i < omax.Length ? omax[i] : 0)} (frozen-max {(i < fmax.Length ? fmax[i] : 0)})");
        sb.AppendLine("  ⇒ the sim UNDER-modelled your offense (an unmodelled enchant/power/relic added damage).");
        WriteMismatch("offense", sb.ToString(), t);
    }

    private static void FinalizeDefenseAndSummary(CombatState state)
    {
        var t = _cur;
        if (t == null) return;
        EnsureSolved(t);

        int lost = t.PlayerHpStart - PlayerHp(state);
        if (lost < 0) lost = 0;
        int fmin = t.Pred.MinHpLost;
        int omin = t.OmniPred.MinHpLost;

        // Lost fewer HP than achievable even with every drawn defence option — and not via a potion or heal.
        bool violation = lost < omin && !t.UsedPotion && !t.Healed;
        if (violation)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"VIOLATION: DEFENSE — you lost {lost} HP but the sim's min (re-solved over every card you");
            sb.AppendLine($"drew) was {omin} (frozen min {fmin}) — a smaller loss the sim thought was impossible.");
            sb.AppendLine("  ⇒ the sim OVER-modelled the danger (over-counted incoming, or under-counted mitigation).");
            WriteMismatch("defense", sb.ToString(), t);
        }

        string flags = (t.Drew ? " drew" : "") + (t.Created ? " created" : "") + (t.UsedPotion ? " potion" : "")
                     + (t.Healed ? " healed" : "") + (t.OffenseOverSuppressed ? " offense-over(potion-suppressed)" : "");
        string verdict = violation ? "DEFENSE-MISMATCH" : "OK";
        AppendSummary($"turn done: offense {t.OffenseSummary} | hpLost={lost} vs min={fmin}→omni{omin} | killAll={t.OmniPred.CanKillAll}{(flags.Length > 0 ? " |" + flags : "")} => {verdict}");
    }

    // ── report writers ──
    private static void WriteMismatch(string kind, string verdict, Turn t)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"==================== AUDIT MISMATCH ({kind.ToUpperInvariant()}) ====================");
            sb.AppendLine(verdict);
            sb.AppendLine($"confounds: drew={t.Drew} created={t.Created} usedPotion={t.UsedPotion} healed={t.Healed}");
            sb.AppendLine();
            sb.AppendLine("── FROZEN TURN-START STATE (what produced the prediction) ──");
            RenderFrozen(sb, t);
            sb.AppendLine("===============================================================");

            string report = sb.ToString();
            DebugLog.Warn(report);
            var path = Path.Combine(LogDir(), $"mismatch-{kind}-{Stamp()}.txt");
            File.WriteAllText(path, report);
            DebugLog.Warn($"AUDIT: {kind} mismatch written to {path}");
            AppendSummary($"!! {kind.ToUpperInvariant()} MISMATCH -> {Path.GetFileName(path)}");
        }
        catch (Exception e) { DebugLog.Error("TurnAudit.WriteMismatch", e); }
    }

    private static void RenderFrozen(StringBuilder sb, Turn t)
    {
        var p = t.Snap.Player;
        sb.AppendLine($"player: hpStart={t.PlayerHpStart} energy={p.Energy} str={p.Strength} dex={p.Dexterity} weak={p.Weak} frail={p.Frail} vuln={p.Vulnerable} shrink={p.Shrink} intangible={p.Intangible} block={p.Block} vigor={p.Vigor} reactiveBlk={p.ReactiveBlock} endTurnBlockable={p.EndTurnSelfDamageBlockable} endTurnUnblockable={p.EndTurnSelfDamage} corruption={t.Corruption}");
        sb.AppendLine($"piles at start: hand={t.HandStartCount} draw={t.DrawPileStart} totalCards={t.TotalCardsStart} potions={t.PotionCountStart}");
        sb.AppendLine($"frozen hand ({t.Snap.Hand.Count}):");
        foreach (var c in t.Snap.Hand) sb.AppendLine("    " + RenderCard(c));
        int revealed = t.OmniCards.Count - t.Snap.Hand.Count;
        sb.AppendLine($"REVEALED union hand ({t.OmniCards.Count}, +{(revealed < 0 ? 0 : revealed)} drawn/created — the re-solve saw ALL of these):");
        foreach (var c in t.OmniCards) sb.AppendLine("    " + RenderCard(c));
        // Every RAW card seen this turn (parsed or not). A card here that's absent from the union above is one
        // the reader DROPPED (ReadCard returned null) — its effect is missing from the sim, a prime suspect.
        sb.AppendLine($"RAW cards seen ({t.SeenModels.Count}) — game DynamicVars (a card missing from the union above was DROPPED by the reader):");
        foreach (var cm in t.SeenModels)
        {
            var line = new StringBuilder($"    {SafeName(cm)} type={SafeType(cm)} vars={{");
            try { foreach (var v in cm.DynamicVars.Values) line.Append($"{v.Name}={v.BaseValue},"); } catch { }
            line.Append("} keywords={");
            try { foreach (var k in cm.Keywords) line.Append(k).Append(','); } catch { }
            line.Append("} tags={");
            try { foreach (var tg in cm.Tags) line.Append(tg).Append(','); } catch { }
            line.Append('}');
            sb.AppendLine(line.ToString());
        }
        sb.AppendLine($"enemies ({t.Snap.Enemies.Length}):");
        for (int i = 0; i < t.Snap.Enemies.Length; i++)
        {
            var e = t.Snap.Enemies[i];
            int baseHp = i < t.EnemyBaselineHp.Length ? t.EnemyBaselineHp[i] : -1;
            sb.AppendLine($"    [{i}] hpStart={baseHp} block={e.Block} vuln={e.Vulnerable} weak={e.Weak} str={e.Strength} intent={e.IntentDamage}x{e.IntentHits} perHitCap={e.PerHitCap} dmgTakenPct={e.DamageTakenPct} doom={e.Doom}");
        }
        sb.AppendLine($"frozen prediction: maxDmg={t.Pred.MaxDamage} perEnemy=[{string.Join(",", t.Pred.MaxPerEnemy ?? Array.Empty<int>())}] minHp={t.Pred.MinHpLost} killAll={t.Pred.CanKillAll} nodes={t.Pred.Nodes} truncated={t.Pred.Truncated}");
        sb.AppendLine($"re-solved (omni): maxDmg={t.OmniPred.MaxDamage} perEnemy=[{string.Join(",", t.OmniPred.MaxPerEnemy ?? Array.Empty<int>())}] minHp={t.OmniPred.MinHpLost} killAll={t.OmniPred.CanKillAll} nodes={t.OmniPred.Nodes} truncated={t.OmniPred.Truncated}");
    }

    private static string RenderCard(TurnSim.Card c)
        => $"{c.Name}: cost={c.Cost} dmg={c.Damage}x{c.Hits} tgt={c.AttackTarget} block={c.Block} flatBlk={c.FlatBlock} applyVuln={c.ApplyVulnerable} applyWeak={c.ApplyWeak} strGain={c.StrengthGain} xcost={c.XCost} exhaust={c.Exhausts} dyn={c.Dynamic}";

    private static void AppendSummary(string line)
    {
        try { File.AppendAllText(Path.Combine(LogDir(), "audit-summary.log"), $"[{Stamp()}] {line}{Environment.NewLine}"); }
        catch { }
    }

    // ── helpers ──
    private static int PlayerHp(CombatState state)
    {
        try { return LocalContext.GetMe((IEnumerable<Creature>)state.Creatures)?.CurrentHp ?? 0; }
        catch { return 0; }
    }

    private static bool HasPower(CombatState state, string powerTypeName)
    {
        try
        {
            var meC = LocalContext.GetMe((IEnumerable<Creature>)state.Creatures);
            if (meC != null) foreach (var pw in meC.Powers) if (pw.GetType().Name == powerTypeName) return true;
        }
        catch { }
        return false;
    }

    private static IReadOnlyList<CardModel>? RawHand(CombatState state)
    {
        try { return LocalContext.GetMe(state)?.PlayerCombatState?.Hand?.Cards; }
        catch { return null; }
    }

    private static CardType SafeType(CardModel cm)
    {
        try { return cm.Type; } catch { return CardType.Attack; }
    }

    private static string SafeName(CardModel cm)
    {
        try { return cm.GetType().Name; } catch { return "?"; }
    }

    private static int SafeHp(Creature? c)
    {
        try { int hp = c?.CurrentHp ?? 0; return hp < 0 ? 0 : hp; }
        catch { return 0; }
    }

    private readonly struct Counts
    {
        public readonly int Hand, Draw, Disc, Exh, Potions;
        public Counts(int hand, int draw, int disc, int exh, int potions) { Hand = hand; Draw = draw; Disc = disc; Exh = exh; Potions = potions; }
        public int Total => Hand + Draw + Disc + Exh;   // every card in combat; grows only when cards are created
    }

    // Pile SIZES + filled potion slots, read together each observation.
    private static Counts ReadCounts(CombatState state)
    {
        int hand = 0, draw = 0, disc = 0, exh = 0, pot = 0;
        try
        {
            var me = LocalContext.GetMe(state);
            var pcs = me?.PlayerCombatState;
            if (pcs != null)
            {
                hand = pcs.Hand.Cards.Count;
                draw = pcs.DrawPile.Cards.Count;
                disc = pcs.DiscardPile.Cards.Count;
                exh = pcs.ExhaustPile.Cards.Count;
            }
            if (me?.Potions != null) foreach (var po in me.Potions) if (po != null) pot++;
        }
        catch { }
        return new Counts(hand, draw, disc, exh, pot);
    }

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

    private static string LogDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "SlayTheSpire2", "modding", "logs");
}
