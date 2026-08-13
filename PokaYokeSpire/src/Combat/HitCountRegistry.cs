using System.Collections.Generic;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Entities.Cards; // PileType
using MegaCrit.Sts2.Core.Models;         // CardModel

namespace PokaYokeSpire.Combat;

/// <summary>
/// How many times a card hits — needed so multi-hit cards (Twin Strike ×2, Dagger Spray ×2)
/// aren't undercounted in the lethal check. Hit count lives inside a card's OnPlay
/// (.WithHitCount(n)) with no readable field, so: (1) a small registry of known cards, then
/// (2) parse the card's own resolved description ("twice", "N times"), else (3) default 1.
/// Defaulting to 1 is deliberately conservative — an unknown multi-hit card under-counts, so
/// the lethal glow errs toward silence, never a false lethal.
/// </summary>
public static class HitCountRegistry
{
    /// Known multi-hit cards, class name -> hits. Extend freely.
    public static readonly Dictionary<string, int> Known = new()
    {
        ["TwinStrike"] = 2,
        ["DaggerSpray"] = 2,
    };

    private static readonly Dictionary<string, int> _cache = new();

    public static int HitsOf(CardModel card)
    {
        string name = card.GetType().Name;
        if (Known.TryGetValue(name, out var h)) return h;
        if (_cache.TryGetValue(name, out var c)) return c;
        int hits = ParseFromDescription(card);
        _cache[name] = hits;
        return hits;
    }

    private static int ParseFromDescription(CardModel card)
    {
        try
        {
            string d = (card.GetDescriptionForPile(PileType.Hand, null) ?? "").ToLowerInvariant();
            if (d.Contains("twice")) return 2;
            if (d.Contains("thrice")) return 3;
            var m = Regex.Match(d, @"(\d+)\s*times");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0 && n < 20) return n;
        }
        catch { }
        return 1;
    }
}
