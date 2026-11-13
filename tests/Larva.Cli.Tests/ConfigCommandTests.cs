using System.Text.Json.Nodes;

namespace Larva.Cli.Tests;

/// <summary><c>larva config</c> and its settings file: other keys kept, a broken file reported and never written over.</summary>
public sealed class ConfigCommandTests
{
    private static LarvaProcess Larva(out string config)
    {
        string root = TestEnvironment.TempFolder("config");
        config = Path.Combine(root, "defaults.json");
        return new LarvaProcess { WorkingDirectory = root, Config = config, UserLanguages = Path.Combine(root, "user-languages") };
    }

    [Fact]
    public void Keys_larva_does_not_know_survive_a_save()
    {
        var larva = Larva(out string config);
        File.WriteAllText(config, """{ "borrowcheck": "warn", "future.key": 42, "nested": { "a": [1, 2] } }""");
        Assert.Equal("cpu: native", larva.Ok("config", "cpu", "native").Trim());
        var written = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        Assert.Equal("native", (string?)written["cpu"]);
        Assert.Equal("warn", (string?)written["borrowcheck"]);
        Assert.Equal(42, (int?)written["future.key"]);
        Assert.Equal(2, written["nested"]!["a"]!.AsArray().Count);
        larva.Ok("config", "--unset", "borrowcheck");
        written = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        Assert.Null(written["borrowcheck"]);
        Assert.Equal(42, (int?)written["future.key"]);
    }

    [Fact]
    public void A_broken_file_is_reported_and_not_written_over_without_reset()
    {
        var larva = Larva(out string config);
        const string broken = "{ \"borrowcheck\": \"warn\", bad json\n";
        File.WriteAllText(config, broken);

        var (code, output) = larva.Run("config", "cpu", "native");
        Assert.Equal(1, code);
        Assert.Contains($"larva: {config}: ", output);
        Assert.Contains("not written over", output);
        Assert.Equal(broken, File.ReadAllText(config));

        (code, output) = larva.Run("config");
        Assert.Equal(1, code);
        Assert.Contains($"larva: {config}: ", output);
        Assert.Equal(broken, File.ReadAllText(config));

        // Any other command goes on with the built-in defaults, saying so once (a project read: its BUILD defaults).
        File.WriteAllText(Path.Combine(larva.WorkingDirectory, "x.queen"), "queen 1.0;\n\nchunk PROJ:\n   name: x\n");
        (code, output) = larva.Run("hive", "install", "x.queen");
        Assert.Contains($"larva: {config}: ", output);
        Assert.Equal(1, output.Split("larva: " + config).Length - 1);

        // --reset starts a new file.
        Assert.Equal("cpu: native", larva.Ok("config", "--reset", "cpu", "native").Trim());
        var written = JsonNode.Parse(File.ReadAllText(config))!.AsObject();
        Assert.Equal("native", (string?)written["cpu"]);
        Assert.Single(written);
        Assert.Contains("cpu: native", larva.Ok("config"));
    }
}
