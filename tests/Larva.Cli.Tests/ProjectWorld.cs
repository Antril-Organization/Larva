namespace Larva.Cli.Tests;

/// <summary>
/// A <see cref="LanguageWorld"/> with Antril installed from its folder registry (once for every test that builds a
/// project: installing a language takes seconds), and projects made in it from text.
/// </summary>
internal sealed class ProjectWorld
{
    private static readonly Lazy<ProjectWorld> shared = new(() => new ProjectWorld());

    /// <summary>The one world the project tests share (they run one at a time).</summary>
    public static ProjectWorld Shared => shared.Value;

    public LanguageWorld World { get; }
    public LarvaProcess Larva => World.Larva;

    private ProjectWorld()
    {
        World = new LanguageWorld("projects", trustRegistry: false);
        World.Approve("0.8.0");
        Larva.Ok("config", "languages.trust", World.RegistryPublicKey);
        Larva.Ok("config", "languages.provider", "file:" + World.Registry);
        Larva.Ok("languages", "install", "antril@0.8.0");
    }

    /// <summary>A project folder under the world: its <c>.queen</c> and its sources, as given; the <c>.queen</c>'s path.</summary>
    public string Project(string name, string queen, params (string File, string Text)[] sources)
    {
        string folder = Path.Combine(World.Root, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".queen");
        File.WriteAllText(path, queen);
        foreach (var (file, text) in sources)
            File.WriteAllText(Path.Combine(folder, file), text);
        return path;
    }

    /// <summary>Runs larva in <paramref name="folder"/> (the world's own settings and languages).</summary>
    public LarvaProcess In(string folder) => new() { WorkingDirectory = folder, Config = Larva.Config, UserLanguages = Larva.UserLanguages, Cache = Path.Combine(World.Root, "hive-cache") };

    public static string Program(string text = "Console.WriteLine(\"hello\");") =>
        $"import System;\n\ninto namespace App\n{{\n\tMain()\n\t{{\n\t\t{text}\n\t}}\n}}\n";
}
