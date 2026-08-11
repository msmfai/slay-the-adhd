using System.Linq;
using System.Reflection;
using BaseLib.Config;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using PokaYokeSpire.Relics;

namespace PokaYokeSpire;

/// <summary>
/// Mod entry point. ModManager finds the [ModInitializer] type and invokes the named
/// method — which MUST be static (invoked via Invoke(null, null)). Registers the
/// BaseLib config, applies the Harmony guards, and logs exactly which methods were
/// patched so the log confirms every guard applied.
/// </summary>
[ModInitializer(nameof(Init))]
public class Plugin
{
    public const string ModId = "PokaYokeSpire";

    public static void Init()
    {
        ModConfigRegistry.Register(ModId, new Config());
        RelicCustomizations.Install(); // per-relic custom behaviour

        var harmony = new Harmony(ModId);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        // Global ctrl-drag handler at the scene-tree root.
        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        root.CallDeferred(Node.MethodName.AddChild, new DragHandler());

        string[] patched = harmony.GetPatchedMethods()
            .Select(m => m.DeclaringType?.Name + "." + m.Name)
            .ToArray();
        Log.Info($"[Poka-Yoke] patched {patched.Length} methods: {string.Join(", ", patched)}");
    }
}
