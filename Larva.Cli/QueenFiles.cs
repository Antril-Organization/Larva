using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Larva.Build;
using Larva.Build.Profiles;

/// <summary>
/// Editor features of queen files, from the profile the file's name chooses (<c>project.queen</c>: the <c>project</c>
/// profile): completion of chunk names, of the keys of the chunk the cursor is in and of the values a key takes, and hovers
/// that explain chunks and keys; and of profiles' own <c>.tqueen</c> templates (their keywords). Answers are LSP JSON.
/// </summary>
internal static class QueenFiles
{
    private static readonly Regex ChunkLine = new(@"^\s*chunk\s+([A-Z][A-Z0-9_]*)\b", RegexOptions.Compiled);

    /// <summary>The chunk the line <paramref name="line"/> belongs to (the last `chunk NAME:` at or above it).</summary>
    private static string? ChunkAt(string[] lines, int line)
    {
        for (int l = Math.Min(line, lines.Length - 1); l >= 0; l--)
        {
            if (ChunkLine.Match(lines[l]) is { Success: true } match)
                return match.Groups[1].Value;
        }
        return null;
    }

    /// <summary>Completion in the queen file at <paramref name="path"/> (null when no profile has its name).</summary>
    public static JsonNode? Complete(string path, string text, int line, int character)
    {
        if (path.EndsWith(ProfileCatalog.TemplateExtension, StringComparison.OrdinalIgnoreCase))
            return CompleteTemplate(text, line, character);
        if (ProfileCatalog.ForFile(path) is not { } profile)
            return null;
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if (line >= lines.Length)
            return null;
        string before = lines[line][..Math.Min(character, lines[line].Length)];
        var items = new JsonArray();
        var chunks = profile.Chunks.ToList();
        // `chunk |`: the chunks.
        if (Regex.Match(before, @"^\s*chunk\s+([A-Z0-9_]*)$") is { Success: true })
        {
            foreach (var chunk in chunks)
                items.Add(Item(chunk.Name, chunk.Doc, 9, chunk.Name + ":"));
            return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        }
        // The entry being written: after the last comma of the line.
        string entry = before[(before.LastIndexOf(',') + 1)..];
        var spec = ChunkAt(lines, line) is { } name ? profile.Chunk(name) : null;
        // `key: |`: the values the key takes.
        if (Regex.Match(entry, @"^\s*([A-Za-z_][\w.]*)\s*:\s*([\w.^@>=<-]*)$") is { Success: true } valued)
        {
            if (spec?.Key(valued.Groups[1].Value) is not { } key)
                return null;
            foreach (string value in key.Type.Values)
                items.Add(Item(value, Explain(key), 12, value));
            if (key.Default is { } fallback && !key.Type.Values.Contains(fallback))
                items.Add(Item(QueenValidator.Fill(fallback, Path.GetFileName(Path.GetDirectoryName(path)) ?? "", path), $"The default. {Explain(key)}", 1, QueenValidator.Fill(fallback, Path.GetFileName(Path.GetDirectoryName(path)) ?? "", path)));
            if (!key.Type.Values.Any() && key.Example is { } example)
                items.Add(Item(example, $"Example. {Explain(key)}", 1, example));
            return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        }
        // A key (or `chunk` itself at the start of a line).
        if (Regex.IsMatch(entry, @"^\s*[A-Za-z_]?[\w.]*$"))
        {
            if (spec is not null)
            {
                foreach (var key in spec.Keys)
                    items.Add(Item(key.Name, Explain(key), 10, key.Name + ": "));
            }
            if (before.IndexOf(',') < 0)
                items.Add(Item("chunk", $"A section of the file: {string.Join(", ", chunks.Select(c => c.Name))}.", 14, "chunk "));
            return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        }
        return null;
    }

    /// <summary>Completion in a <c>.tqueen</c>: the types after a key's <c>:</c>, the modifiers after its type, the words at the start of a line.</summary>
    private static JsonNode? CompleteTemplate(string text, int line, int character)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if (line >= lines.Length)
            return null;
        string before = lines[line][..Math.Min(character, lines[line].Length)];
        var items = new JsonArray();
        bool keyLine = Regex.IsMatch(before, @"^\s*[A-Za-z_][\w.]*\s*:") && !Regex.IsMatch(before, @"^\s*(chunk|profile|rules)\b");
        if (keyLine && Regex.IsMatch(before, @":\s*[A-Z]*$"))
        {
            foreach (string keyword in ProfileParser.TypeKeywords)
                items.Add(Item(keyword, TypeDoc(keyword), 14, keyword));
        }
        else if (keyLine)
        {
            foreach (string modifier in new[] { "required", "many", "only when ", "required when ", "deprecated \"\"", "example \"\"", "= " })
                items.Add(Item(modifier.Trim(), "A modifier of the key.", 14, modifier));
        }
        else if (Regex.IsMatch(before, @"^\s*[a-z]*$"))
        {
            foreach (string word in new[] { "chunk ", "profile ", "rules:", "open chunks" })
                items.Add(Item(word.Trim(), "A declaration of the profile.", 14, word));
        }
        return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
    }

    private static string TypeDoc(string keyword) => keyword switch
    {
        "TEXT" => "Any text.",
        "IDENTIFIER" => "A name: letters, digits and `_`.",
        "NAME" => "A name with dots (`Namespace.Type`).",
        "INT" => "A whole number; `INT(0..3)` within a range.",
        "NUMBER" => "A number.",
        "BOOL" => "`true` or `false`.",
        "VERSION" => "A version: `v1.0`.",
        "PATH" => "A path, relative to the file's folder or absolute.",
        "RPATH" => "A relative path.",
        "APATH" => "An absolute path.",
        "FILE" => "A file: `FILE(.dll .lva)` with those extensions, `FILE(profile project)` of that profile.",
        "FOLDER" => "A folder.",
        "GLOB" => "A file, a folder or a pattern (`src/**`).",
        "URL" => "An address: `https://…`, `antril.org/hive`.",
        "REGEX" => "Text that matches a pattern: `REGEX(\"^[a-z]+$\")`.",
        "LIST" => "A list of a type, or a single value: `LIST(GLOB)`.",
        _ => "",
    };

    /// <summary>A hover in the queen file at <paramref name="path"/>: a chunk's or a key's explanation.</summary>
    public static JsonNode? Hover(string path, string text, int line, int character)
    {
        if (ProfileCatalog.ForFile(path) is not { } profile)
            return null;
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if (line >= lines.Length)
            return null;
        string current = lines[line];
        int start = Math.Min(character, current.Length), end = start;
        while (start > 0 && (char.IsLetterOrDigit(current[start - 1]) || current[start - 1] is '_' or '.'))
            start--;
        while (end < current.Length && (char.IsLetterOrDigit(current[end]) || current[end] is '_' or '.'))
            end++;
        if (start == end)
            return null;
        string word = current[start..end];
        string? markdown = null;
        if (profile.Chunk(word) is { } chunk && ChunkLine.IsMatch(current))
            markdown = $"**chunk {chunk.Name}**\n\n{chunk.Doc}\n\nKeys: {string.Join(", ", chunk.Keys.Select(k => $"`{k.Name}`"))}";
        else if (ChunkAt(lines, line) is { } name && profile.Chunk(name)?.Key(word) is { } key && Regex.IsMatch(current[end..], @"^\s*:"))
            markdown = $"**{name} {key.Name}**\n\n{Explain(key)}";
        if (markdown is null)
            return null;
        return new JsonObject
        {
            ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = markdown },
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = start },
                ["end"] = new JsonObject { ["line"] = line, ["character"] = end },
            },
        };
    }

    private static string Explain(KeySpec key) =>
        key.Doc
        + $"\n\nTakes {key.Type.Describe()}."
        + (key.Default is { } fallback ? $" Default: `{fallback}`." : "")
        + (key.Required ? " Required." : "")
        + (key.RequiredWhen is { } required ? $" Required when {required.Text}." : "")
        + (key.OnlyWhen is { } only ? $" Only when {only.Text}." : "")
        + (key.Many ? " Any number of times." : "")
        + (key.Deprecated is { } why ? $"\n\nDeprecated: {why}" : "")
        + (key.Example is { } example ? $"\n\nExample: `{key.Name}: {example}`" : "");

    private static JsonObject Item(string label, string documentation, int kind, string insert) => new()
    {
        ["label"] = label,
        ["kind"] = kind,
        ["insertText"] = insert,
        ["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation },
    };
}
