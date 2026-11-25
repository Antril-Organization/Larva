namespace Larva.Build.Profiles
{
    /// <summary>
    /// Checks a <c>.queen</c> document against its profile: the chunks and keys it may have, the values each takes, what
    /// is required (always, or when a condition holds), what only means something under a condition, and the profile's
    /// rules. The codes are the project reader's:
    /// <list type="bullet">
    /// <item>QN011: a key given twice that is not <c>many</c> (an error).</item>
    /// <item>QN120: a required chunk is missing; QN122: a required key is missing (errors).</item>
    /// <item>QN121: a key the chunk does not have; QN141: a chunk the profile does not have (warnings).</item>
    /// <item>QN124: a value of the wrong type (an error).</item>
    /// <item>QN125: a key a condition requires is missing (an error); QN126: a key set where its condition does not hold (a warning).</item>
    /// <item>QN143: a deprecated key (a warning). Rules: their own code (QN142 by default).</item>
    /// </list>
    /// Values with <c>$[…]</c> not evaluated yet are not checked.
    /// </summary>
    public static class QueenValidator
    {
        public static void Validate(QueenDocument document, QueenProfile profile, List<QueenDiagnostic> diagnostics)
        {
            string file = document.File;
            void Report(string code, string message, int line, int column, bool error) =>
                diagnostics.Add(new QueenDiagnostic(code, message, file, line, column, error));

            string folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(file)) ?? "") ?? "";
            ValueLookup Lookup(ChunkSpec? own) => key =>
            {
                string? chunkName = key.Chunk ?? own?.Name;
                if (chunkName is null)
                    return null;
                if (document.Named(chunkName).SelectMany(c => c.Entries).LastOrDefault(e => e.Key == key.Key) is { } entry)
                    return entry.Value.Display;
                return profile.Chunk(chunkName)?.Key(key.Key)?.Default is { } fallback ? Fill(fallback, folder, file) : null;
            };

            foreach (var chunk in document.Chunks)
            {
                if (profile.Chunk(chunk.Name) is not { } spec)
                {
                    if (!profile.AllowsOtherChunks)
                        Report("QN141", $"`{chunk.Name}` is not a chunk of a `{profile.Name}` file ({string.Join(", ", profile.Chunks.Select(c => c.Name))})", chunk.Line, chunk.Column, false);
                    continue;
                }
                var values = Lookup(spec);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in chunk.Entries)
                {
                    var key = spec.Key(entry.Key);
                    if (key is null && !spec.Open)
                    {
                        Report("QN121", $"`{entry.Key}` is not a {chunk.Name} key ({string.Join(", ", spec.Keys.Select(k => k.Name))})", entry.Value.Line, entry.Value.Column, false);
                        continue;
                    }
                    if (!seen.Add(entry.Key) && key is { Many: false })
                        Report("QN011", $"`{entry.Key}` is set twice in chunk `{chunk.Name}`", entry.Line, entry.Column, true);
                    var type = key?.Type ?? spec.OtherKeys;
                    if (!Evaluated(entry.Value))
                        continue;
                    if (!type.Accepts(entry.Value))
                        Report("QN124", $"`{entry.Key}` is {type.Describe()}, not `{entry.Value.Display}`", entry.Value.Line, entry.Value.Column, true);
                    if (key?.Deprecated is { } why)
                        Report("QN143", $"`{entry.Key}` is deprecated: {why}", entry.Line, entry.Column, false);
                    if (key?.OnlyWhen is { } only && !only.Holds(values))
                        Report("QN126", $"`{entry.Key}` is for when {only.Text}: here it does nothing", entry.Value.Line, entry.Value.Column, false);
                }
                foreach (var key in spec.Keys)
                {
                    if (chunk[key.Name] is not null)
                        continue;
                    if (key.Required)
                        Report("QN122", $"{chunk.Name} needs a `{key.Name}` ({key.Type.Describe()})", chunk.Line, chunk.Column, true);
                    else if (key.RequiredWhen is { } when && when.Holds(values))
                        Report("QN125", $"{when.Text}: {chunk.Name} needs `{key.Name}` ({key.Type.Describe()})", chunk.Line, chunk.Column, true);
                }
            }
            foreach (var spec in profile.Chunks.Where(c => c.Required && !document.Named(c.Name).Any()))
                Report("QN120", $"a `{profile.Name}` file has a `chunk {spec.Name}:`", 1, 1, true);

            foreach (var rule in profile.Rules)
            {
                var values = Lookup(null);
                if (!rule.When.Holds(values))
                    continue;
                // Reported at the first key the rule reads that the file sets (else at the top).
                var at = rule.When.Keys.Select(k => k.Chunk is { } c ? document.Named(c).SelectMany(x => x.Entries).LastOrDefault(e => e.Key == k.Key) : null).FirstOrDefault(e => e is not null);
                Report(rule.Code, rule.Message, at?.Value.Line ?? 1, at?.Value.Column ?? 1, rule.IsError);
            }
        }

        /// <summary>Whether a value has nothing left to evaluate (no <c>$[…]</c>).</summary>
        private static bool Evaluated(QueenValue value) => value switch
        {
            QueenTemplate => false,
            QueenList list => list.Items.All(Evaluated),
            _ => true,
        };

        /// <summary>A default as the file would say it: <c>$folder</c> is the file's folder's name, <c>$file</c> its name.</summary>
        public static string Fill(string value, string folder, string file) => value switch
        {
            "$folder" => folder,
            "$file" => Path.GetFileNameWithoutExtension(file),
            _ => value,
        };

        /// <summary>Why <paramref name="value"/> (as written) is not a value of a profile's <paramref name="chunk"/> <paramref name="key"/>, or null when it is.</summary>
        public static string? Problem(QueenProfile profile, string chunk, string key, string value)
        {
            if (profile.Chunk(chunk)?.Key(key) is not { } spec)
                return $"`{key}` is not a {chunk} key";
            return spec.Type.Accepts(QueenParser.Classify(value, 1, 1)) ? null : $"`{key}` is {spec.Type.Describe()}";
        }
    }
}
