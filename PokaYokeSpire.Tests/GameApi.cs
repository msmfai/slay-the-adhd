using System.Reflection;

namespace PokaYokeSpire.Tests;

/// <summary>
/// Loads the installed game's sts2.dll as METADATA (no Godot runtime needed) so tests
/// can assert the exact methods/types/properties the mod depends on still exist with the
/// shape the mod assumes. If a game update renames or reshapes any of these, the matching
/// test fails BEFORE the mod is shipped/relaunched — catching the "my assumption was
/// wrong" bugs that otherwise only surface in-game.
/// </summary>
public sealed class GameApi : IDisposable
{
    public const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private readonly MetadataLoadContext _mlc;
    public Assembly Sts2 { get; }

    public GameApi()
    {
        string gameDir =
            Environment.GetEnvironmentVariable("STS2_GAME_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library/Application Support/Steam/steamapps/common/Slay the Spire 2/"
                    + "SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64");

        if (!Directory.Exists(gameDir))
            throw new DirectoryNotFoundException(
                $"Game managed dir not found: {gameDir}. Set STS2_GAME_DIR to override.");

        var resolver = new PathAssemblyResolver(Directory.GetFiles(gameDir, "*.dll"));
        _mlc = new MetadataLoadContext(resolver);
        Sts2 = _mlc.LoadFromAssemblyPath(Path.Combine(gameDir, "sts2.dll"));
    }

    public Type Type(string fullName) =>
        Sts2.GetType(fullName) ?? throw new Xunit.Sdk.XunitException($"Type not found: {fullName}");

    public void AssertMethod(string typeName, string method) =>
        // GetMethods (not GetMethod) so overloaded methods don't throw AmbiguousMatch.
        Xunit.Assert.True(Type(typeName).GetMethods(Any).Any(m => m.Name == method),
            $"Missing method {typeName}.{method}");

    public void AssertProperty(string typeName, string prop) =>
        Xunit.Assert.True(Type(typeName).GetProperty(prop, Any) is not null,
            $"Missing property {typeName}.{prop}");

    public void AssertField(string typeName, string field) =>
        Xunit.Assert.True(Type(typeName).GetField(field, Any) is not null,
            $"Missing field {typeName}.{field}");

    public void Dispose() => _mlc.Dispose();
}
