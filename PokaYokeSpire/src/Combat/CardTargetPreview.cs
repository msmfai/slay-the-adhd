using System;
using MegaCrit.Sts2.Core.Entities.Cards;      // PileType
using MegaCrit.Sts2.Core.Entities.Creatures;  // Creature
using MegaCrit.Sts2.Core.Models;              // CardModel, CardPreviewMode

namespace PokaYokeSpire.Combat;

/// <summary>
/// Computes what a card will actually do to a specific target, for the cursor readout while
/// targeting. Damage + statuses come from the GAME'S OWN per-target resolution so they're
/// exact (Vulnerable/Strength/relics all baked in):
///   damage  = card.DynamicVars.Damage resolved for the target via UpdateCardPreview (which
///             runs Hook.ModifyDamage) — 0 for non-attack (pure-debuff) cards;
///   effect  = card.GetDescriptionForPile(pile, target) — the game's resolved effect text;
///   hpThroughBlock = max(0, damage - targetBlock) (0 / "blocked" if it won't break block).
/// </summary>
public static class CardTargetPreview
{
    public readonly record struct Preview(int Damage, int TargetBlock, int HpThroughBlock, bool FullyBlocked, string EffectText, bool Valid);

    /// Pure arithmetic — unit-testable.
    public static int HpThroughBlock(int damage, int targetBlock) => Math.Max(0, damage - targetBlock);

    public static Preview Compute(CardModel card, Creature target)
    {
        if (card == null || target == null) return default;

        int damage = 0;
        try
        {
            var dv = card.DynamicVars.Damage;                                   // throws if no Damage var
            dv.UpdateCardPreview(card, CardPreviewMode.Normal, target, true);   // runs the game's damage hook
            damage = (int)dv.PreviewValue;                                      // target-adjusted (Vulnerable etc.)
        }
        catch { damage = 0; } // non-attack card -> no damage, just statuses

        int block = target.Block;
        int hp = HpThroughBlock(damage, block);

        string effect = "";
        try { effect = card.GetDescriptionForPile(card.Pile?.Type ?? PileType.Hand, target) ?? ""; } catch { }

        return new Preview(damage, block, hp, damage > 0 && hp == 0, effect, true);
    }
}
