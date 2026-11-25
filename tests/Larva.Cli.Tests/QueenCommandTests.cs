using System.Text.Json.Nodes;

namespace Larva.Cli.Tests;

/// <summary><c>larva queen read</c>: a queen file checked against its profile, written as JSON (what tools read).</summary>
public sealed class QueenCommandTests
{
    [Fact]
    public void A_queen_file_is_read_with_its_profile_as_json()
    {
        string root = TestEnvironment.TempFolder("queen-read");
        // A repository with a profile of its own (.larva/profiles), and a file of that profile.
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        string profile = Path.Combine(root, ".larva", "profiles", "shop");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "template.tqueen"), """
            tqueen 1.0;
            profile shop: "A shop's settings"
            chunk SHOP:
               name: TEXT = $folder
               size: INT(1..10) = 3
               tags: IDENTIFIER  many
            """);
        var larva = new LarvaProcess { WorkingDirectory = root, Config = Path.Combine(root, "defaults.json"), UserLanguages = Path.Combine(root, "user-languages") };

        File.WriteAllText(Path.Combine(root, "shop.queen"), "queen 1.0;\n\nchunk SHOP:\n   tags: a,\n   tags: b\n\nchunk NOTES:\n   seen: true\n");
        var (code, output) = larva.Run("queen", "read", "shop.queen");
        Assert.True(code == 0, output);
        var json = JsonNode.Parse(output[output.IndexOf('{')..])!;
        Assert.Equal("shop", (string?)json["profile"]);
        var shop = json["chunks"]!["SHOP"]!;
        Assert.Equal(Path.GetFileName(root), (string?)shop["name"]);
        Assert.Equal("3", (string?)shop["size"]);
        Assert.Equal(["a", "b"], shop["tags"]!.AsArray().Select(t => (string?)t));
        Assert.Equal("true", (string?)json["chunks"]!["NOTES"]!["seen"]);
        Assert.Contains(json["diagnostics"]!.AsArray(), d => (string?)d!["code"] == "QN141");

        File.WriteAllText(Path.Combine(root, "shop.queen"), "queen 1.0;\n\nchunk SHOP:\n   size: 40\n");
        (code, output) = larva.Run("queen", "read", "shop.queen");
        Assert.True(code == 1 && output.Contains("QN124"), output);

        File.WriteAllText(Path.Combine(root, "nothing.queen"), "queen 1.0;\n");
        (code, output) = larva.Run("queen", "read", "nothing.queen");
        Assert.True(code == 1 && output.Contains("QN140"), output);

        (code, output) = larva.Run("queen", "read");
        Assert.Equal(2, code);
    }
}
