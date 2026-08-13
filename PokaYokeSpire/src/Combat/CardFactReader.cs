using MegaCrit.Sts2.Core.Entities.Cards;   // CardType, CostModifiers
using MegaCrit.Sts2.Core.Models;           // CardModel

namespace PokaYokeSpire.Combat;

/// <summary>
/// Reads one <see cref="CardModel"/> into a <see cref="DeckStats.CardFact"/> — purely from the card's
/// canonical DynamicVars, cost and type (no Godot, no combat state), so the "no new information"
/// boundary is explicit and the read is unit-testable against real game cards.
/// </summary>
public static class CardFactReader
{
    public static DeckStats.CardFact Read(CardModel c)
    {
        int Read(params string[] keys)
        {
            foreach (var k in keys)
                if (c.DynamicVars.TryGetValue(k, out var v)) return v.IntValue;
            return 0;
        }

        int dmg = Read("Damage", "CalculatedDamage");
        int blk = Read("Block", "CalculatedBlock");
        int draw = Read("Cards");
        int energy = Read("Energy");

        bool costsX = false; int cost = 0;
        try { costsX = c.EnergyCost.CostsX; cost = costsX ? 0 : c.EnergyCost.GetWithModifiers(CostModifiers.All); } catch { }

        var kind = c.Type switch
        {
            CardType.Attack => DeckStats.Kind.Attack,
            CardType.Skill => DeckStats.Kind.Skill,
            CardType.Power => DeckStats.Kind.Power,
            _ => DeckStats.Kind.Other,
        };
        return new DeckStats.CardFact(cost, costsX, dmg, blk, draw, energy, kind);
    }
}
