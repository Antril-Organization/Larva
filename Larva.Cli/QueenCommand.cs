using System.Text.Json;
using System.Text.Json.Nodes;
using Larva.Build;
using Larva.Build.Profiles;

/// <summary>
/// <c>larva queen read &lt;file.queen&gt;</c>: a queen file checked against its profile (the one its name chooses, as its
/// place sees it: its project's tools' profiles too), written as JSON for a tool or an API that uses queen files:
/// <code>
/// { "file": "…/nest.queen", "profile": "nest",
///   "chunks": { "NEST": { "model": "Shop.Data.ShopDb", "migrations": "migrations" } },
///   "diagnostics": [ { "code": "QN124", "message": "…", "line": 3, "column": 15, "error": true } ] }
/// </code>
/// Every key the profile declares is there, with the file's value or its default (a key without either is left out); a
/// <c>many</c> key's values are a list, a list value is a list; other values are text as written. Keys an open chunk
/// allows, and chunks an open profile allows, are there as written. The exit code is 1 when the file has errors (or
/// no profile has its name, or it cannot be read), 0 otherwise.
/// </summary>
internal static class QueenCommand
{
    private const string Usage = "usage: larva queen read <file.queen>   the file checked against its profile, as JSON";

    public static int Run(string[] args)
    {
        if (args is not ["read", var file])
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var (json, ok) = Read(file);
        Console.WriteLine(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return ok ? 0 : 1;
    }

    /// <summary>The file as JSON, and whether it is free of errors.</summary>
    internal static (JsonObject Json, bool Ok) Read(string file)
    {
        string path = Path.GetFullPath(file);
        var diagnostics = new List<QueenDiagnostic>();
        var json = new JsonObject { ["file"] = path };
        var profile = ProfileCatalog.ForFile(path);
        json["profile"] = profile?.Name;
        var chunks = new JsonObject();
        json["chunks"] = chunks;
        if (profile is null)
            diagnostics.Add(new QueenDiagnostic("QN140", $"no profile is named `{ProfileCatalog.NameOf(path) ?? Path.GetFileName(path)}` (a file's name chooses its profile: `project.queen`, or a tool's)", path, 1, 1));
        else if (!File.Exists(path))
            diagnostics.Add(new QueenDiagnostic("QN101", $"`{Path.GetFileName(path)}` is not there", path, 1, 1));
        else
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new QueenDiagnostic("QN101", $"cannot read it: {e.Message}", path, 1, 1));
                text = "";
            }
            if (text.Length > 0 && QueenParser.Parse(text, path, diagnostics) is { } document)
            {
                QueenValidator.Validate(document, profile, diagnostics);
                Fill(chunks, document, profile, path);
            }
        }
        json["diagnostics"] = new JsonArray([.. diagnostics.Select(d => (JsonNode)new JsonObject
        {
            ["code"] = d.Code,
            ["message"] = d.Message,
            ["line"] = d.Line,
            ["column"] = d.Column,
            ["error"] = d.IsError,
        })]);
        return (json, !diagnostics.Any(d => d.IsError));
    }

    private static void Fill(JsonObject chunks, QueenDocument document, QueenProfile profile, string path)
    {
        string folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        // The profile's chunks (each with every key it declares that has a value or a default), then the file's others.
        foreach (var spec in profile.Chunks)
        {
            var entries = document.Named(spec.Name).SelectMany(c => c.Entries).ToList();
            var chunk = new JsonObject();
            foreach (var key in spec.Keys)
            {
                var given = entries.Where(e => e.Key == key.Name).Select(e => e.Value).ToList();
                if (given.Count > 0)
                    chunk[key.Name] = key.Many ? new JsonArray([.. given.Select(Value)]) : Value(given[^1]);
                else if (key.Default is { } fallback)
                    chunk[key.Name] = key.Many ? new JsonArray(JsonValue.Create(QueenValidator.Fill(fallback, folder, path))) : JsonValue.Create(QueenValidator.Fill(fallback, folder, path));
            }
            foreach (var other in entries.Where(e => spec.Key(e.Key) is null).GroupBy(e => e.Key))
                chunk[other.Key] = other.Count() > 1 ? new JsonArray([.. other.Select(e => Value(e.Value))]) : Value(other.Last().Value);
            if (chunk.Count > 0 || document.Named(spec.Name).Any())
                chunks[spec.Name] = chunk;
        }
        foreach (var chunk in document.Chunks.Where(c => profile.Chunk(c.Name) is null))
        {
            var written = new JsonObject();
            foreach (var entry in chunk.Entries.GroupBy(e => e.Key))
                written[entry.Key] = entry.Count() > 1 ? new JsonArray([.. entry.Select(e => Value(e.Value))]) : Value(entry.Last().Value);
            chunks[chunk.Name] = written;
        }
    }

    /// <summary>A value as JSON: a list as a list, anything else as its text.</summary>
    private static JsonNode? Value(QueenValue value) =>
        value is QueenList list ? new JsonArray([.. list.Items.Select(Value)]) : JsonValue.Create(value.Display);
}
