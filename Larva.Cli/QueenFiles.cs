using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Larva.Build;

/// <summary>
/// Editor features of `.queen` project files, from their schema: completion of chunk names, of the keys of
/// the chunk the cursor is in and of the values a key takes, and hovers that explain chunks and keys. Answers are LSP JSON.
/// </summary>
internal static class QueenFiles
{
    private static readonly Regex ChunkLine = new(@"^\s*chunk\s+([A-Z][A-Z0-9_]*)\s*:", RegexOptions.Compiled);

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

    public static JsonNode? Complete(string text, int line, int character) => Complete(text, line, character, QueenSchema.Chunks);

    public static JsonNode? Complete(string text, int line, int character, QueenChunkSchema[] chunks)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        if (line >= lines.Length)
            return null;
        string before = lines[line][..Math.Min(character, lines[line].Length)];
        var items = new JsonArray();
        // `chunk |`: the chunks.
        if (Regex.Match(before, @"^\s*chunk\s+([A-Z0-9_]*)$") is { Success: true })
        {
            foreach (var chunk in chunks)
                items.Add(Item(chunk.Name, chunk.Description, 9, chunk.Name + ":"));
            return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        }
        // The entry being written: after the last comma of the line.
        string entry = before[(before.LastIndexOf(',') + 1)..];
        var schema = ChunkAt(lines, line) is { } name ? chunks.FirstOrDefault(c => c.Name == name) : null;
        // `key: |`: the values the key takes.
        if (Regex.Match(entry, @"^\s*([A-Za-z_][\w.]*)\s*:\s*([\w.^@>=<-]*)$") is { Success: true } valued)
        {
            if (schema?.Keys.FirstOrDefault(k => k.Name == valued.Groups[1].Value) is not { } key)
                return null;
            foreach (string value in key.Values)
                items.Add(Item(value, key.Description, 12, value));
            if (key.Values.Length == 0 && key.Example is { } example)
                items.Add(Item(example, $"Example. {key.Description}", 1, example));
            return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        }
        // A key (or `chunk` itself at the start of a line).
        if (Regex.IsMatch(entry, @"^\s*[A-Za-z_]?[\w.]*$"))
        {
            if (schema is not null)
            {
                foreach (var key in schema.Keys)
                    items.Add(Item(key.Name, Explain(key), 10, key.Name + ": "));
            }
            if (before.IndexOf(',') < 0)
                items.Add(Item("chunk", $"A section of the file: {string.Join(", ", chunks.Select(c => c.Name))}.", 14, "chunk "));
            return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        }
        return null;
    }

    public static JsonNode? Hover(string text, int line, int character) => Hover(text, line, character, QueenSchema.Chunks);

    public static JsonNode? Hover(string text, int line, int character, QueenChunkSchema[] chunks)
    {
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
        if (chunks.FirstOrDefault(c => c.Name == word) is { } chunk && ChunkLine.IsMatch(current))
            markdown = $"**chunk {chunk.Name}**\n\n{chunk.Description}\n\nKeys: {string.Join(", ", chunk.Keys.Select(k => $"`{k.Name}`"))}";
        else if (ChunkAt(lines, line) is { } name && chunks.FirstOrDefault(c => c.Name == name)?.Keys.FirstOrDefault(k => k.Name == word) is { } key
                 && Regex.IsMatch(current[end..], @"^\s*:"))
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

    private static string Explain(QueenKey key) =>
        key.Description
        + (key.Values.Length > 0 ? $"\n\nValues: {string.Join(", ", key.Values.Select(v => $"`{v}`"))}" : "")
        + (key.Example is { } example ? $"\n\nExample: `{key.Name}: {example}`" : "");

    private static JsonObject Item(string label, string documentation, int kind, string insert) => new()
    {
        ["label"] = label,
        ["kind"] = kind,
        ["insertText"] = insert,
        ["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation },
    };
}
