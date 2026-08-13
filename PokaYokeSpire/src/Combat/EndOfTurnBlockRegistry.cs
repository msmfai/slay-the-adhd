using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models; // PowerModel, RelicModel

namespace PokaYokeSpire.Combat;

/// <summary>
/// Extensible registry of END-OF-TURN BLOCK sources. The damage side of the end-turn
/// prediction is exact (it runs the game's own damage hook); the only thing we compute
/// ourselves is how much block you'll have WHEN the enemies attack — current block plus
/// whatever triggers add at end of turn. Those are listed here, keyed by class name so we
/// don't hard-depend on specific types, and it's trivial to add more.
///
/// Add a source: EndOfTurnBlockRegistry.Powers["FooPower"] = (p, block) => (int)p.Amount;
/// </summary>
public static class EndOfTurnBlockRegistry
{
    /// Power class name -> block it grants at end of turn, given (power, currentBlock).
    public static readonly Dictionary<string, Func<PowerModel, int, int>> Powers = new()
    {
        ["PlatingPower"]     = (p, _) => Amt(p),   // Plating: gain block = Amount
        ["MetallicizePower"] = (p, _) => Amt(p),   // Metallicize: gain block = Amount
    };

    /// Relic class name -> block it grants at end of turn, given (relic, currentBlock).
    public static readonly Dictionary<string, Func<RelicModel, int, int>> Relics = new()
    {
        // Orichalcum: gain 6 block if you end the turn with none.
        ["Orichalcum"] = (_, block) => block == 0 ? 6 : 0,
    };

    private static int Amt(PowerModel p)
    {
        try { return Convert.ToInt32(p.Amount); } catch { return 0; }
    }

    /// Best-effort total block added at end of turn by the local player's powers + relics.
    public static int Predict(Creature me, int currentBlock)
    {
        int gain = 0;
        foreach (var pw in me.Powers)
            if (Powers.TryGetValue(pw.GetType().Name, out var f))
                try { gain += f(pw, currentBlock); } catch { }

        var player = me.Player;
        if (player != null)
            foreach (var relic in player.Relics)
                if (Relics.TryGetValue(relic.GetType().Name, out var f))
                    try { gain += f(relic, currentBlock); } catch { }

        return gain;
    }
}
