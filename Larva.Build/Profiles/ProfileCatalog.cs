namespace Larva.Build.Profiles
{
    /// <summary>
    /// The queen profiles larva knows, by name. A profile is a folder whose <c>template.tqueen</c> declares it; a
    /// <c>.queen</c> file is read with the profile its own name says (<c>project.queen</c>: <c>project</c>). Larva's own
    /// profiles (<c>project</c>, <c>package</c>) are built in; a folder in <c>LARVA_PROFILES</c> (separated by the
    /// system's path separator) or <c>Profiles/</c> next to larva adds profiles or replaces those. Each profile is read
    /// once, and again when its file changes.
    /// </summary>
    public static class ProfileCatalog
    {
        public const string TemplateFile = "template.tqueen";
        public const string Extension = ".queen";
        public const string TemplateExtension = ".tqueen";

        private static readonly object gate = new();
        private static readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);

        private sealed record Entry(QueenProfile? Profile, string Stamp, List<QueenDiagnostic> Problems);

        /// <summary>The folders searched before the built-in profiles: <c>LARVA_PROFILES</c>, then <c>Profiles/</c> next to larva.</summary>
        public static IReadOnlyList<string> Folders
        {
            get
            {
                var folders = new List<string>();
                if (Environment.GetEnvironmentVariable("LARVA_PROFILES") is { Length: > 0 } list)
                    folders.AddRange(list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                folders.Add(Path.Combine(AppContext.BaseDirectory, "Profiles"));
                return folders;
            }
        }

        /// <summary>The names of the built-in profiles.</summary>
        public static IReadOnlyList<string> BuiltIn { get; } = [.. typeof(ProfileCatalog).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Profiles/", StringComparison.Ordinal) && n.EndsWith("/" + TemplateFile, StringComparison.Ordinal))
            .Select(n => n["Profiles/".Length..^(TemplateFile.Length + 1)]).Order(StringComparer.Ordinal)];

        /// <summary>The profile a file's name chooses (<c>…/project.queen</c>: <c>project</c>), or null when no profile has that name.</summary>
        public static QueenProfile? ForFile(string path) => NameOf(path) is { } name ? Get(name) : null;

        /// <summary>The profile name a <c>.queen</c> file's name says, or null for another kind of file.</summary>
        public static string? NameOf(string path)
        {
            string file = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/').Split('/')[^1]);
            return file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && file.Length > Extension.Length ? file[..^Extension.Length] : null;
        }

        /// <summary>Whether a file is a project's (<c>project.queen</c>, or a file whose profile extends <c>project</c>: <c>package.queen</c>).</summary>
        public static bool IsProjectFile(string path) => ForFile(path)?.Is("project") == true;

        /// <summary>The file names that are projects' (in the order a folder is searched): <c>project.queen</c>, <c>package.queen</c>.</summary>
        public static IEnumerable<string> ProjectFileNames =>
            new[] { "project" }.Concat(Names().Where(n => n != "project")).Where(n => Get(n)?.Is("project") == true).Select(n => n + Extension);

        /// <summary>Every profile's name (built in, and in the folders).</summary>
        public static IEnumerable<string> Names()
        {
            var names = new SortedSet<string>(BuiltIn, StringComparer.Ordinal);
            foreach (string folder in Folders.Where(Directory.Exists))
            {
                foreach (string sub in Directory.EnumerateDirectories(folder).Where(d => File.Exists(Path.Combine(d, TemplateFile))))
                    names.Add(Path.GetFileName(sub));
            }
            return names;
        }

        /// <summary>The profile named <paramref name="name"/>, or null.</summary>
        public static QueenProfile? Get(string name) => Load(name, [])?.Profile;

        /// <summary>The problems of the profile's <c>template.tqueen</c> (TQ…), when it was read.</summary>
        public static IReadOnlyList<QueenDiagnostic> ProblemsOf(string name) => Load(name, [])?.Problems ?? [];

        /// <summary>Forgets what was read (a <c>.tqueen</c> was saved): profiles are read again when asked for.</summary>
        public static void Invalidate()
        {
            lock (gate)
                cache.Clear();
        }

        private static Entry? Load(string name, HashSet<string> resolving)
        {
            if (name.Length == 0 || name.IndexOfAny(['/', '\\', '.']) >= 0)
                return null;
            if (Locate(name) is not var (file, stamp))
                return null;
            lock (gate)
            {
                if (cache.TryGetValue(name, out var cached) && cached.Stamp == stamp)
                    return cached;
            }
            if (Read(name, file) is not { } text)
                return null;
            // `extends`: the base is read first (a cycle of profiles stops at the one seen again).
            resolving.Add(name);
            var problems = new List<QueenDiagnostic>();
            var profile = ProfileParser.Parse(text, file, name, problems, other => resolving.Contains(other) ? null : Load(other, resolving)?.Profile);
            resolving.Remove(name);
            var entry = new Entry(profile, stamp, problems);
            lock (gate)
                cache[name] = entry;
            return entry;
        }

        /// <summary>Where a profile's template is (a folder's first, else built in) and what tells a change, without reading it; null when there is none.</summary>
        private static (string File, string Stamp)? Locate(string name)
        {
            foreach (string folder in Folders)
            {
                string file = Path.Combine(folder, name, TemplateFile);
                try
                {
                    if (File.Exists(file))
                        return (file, $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
            return BuiltIn.Contains(name) ? ($"builtin:{name}", "builtin") : null;
        }

        private static string? Read(string name, string file)
        {
            try
            {
                if (!file.StartsWith("builtin:", StringComparison.Ordinal))
                    return File.ReadAllText(file);
                using var stream = typeof(ProfileCatalog).Assembly.GetManifestResourceStream($"Profiles/{name}/{TemplateFile}");
                if (stream is null)
                    return null;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
