using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace PokaYokeSpire.Tests.Runtime;

/// <summary>
/// Registered before test discovery touches any game type: resolves sts2.dll's and
/// GodotSharp's dependencies from the installed game directory, so the real assemblies
/// load headlessly under `dotnet test`.
/// </summary>
internal static class Bootstrap
{
    internal static readonly string GameDir =
        Environment.GetEnvironmentVariable("STS2_GAME_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library/Application Support/Steam/steamapps/common/Slay the Spire 2/"
                + "SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64");

    [ModuleInitializer]
    internal static void Init()
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var path = Path.Combine(GameDir, name.Name + ".dll");
            return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
        };
    }
}
