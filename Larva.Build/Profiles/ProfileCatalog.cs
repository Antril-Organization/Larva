namespace Larva.Build.Profiles
{
    /// <summary>
    /// The queen profiles larva knows, by name. A profile is a folder whose <c>template.tqueen</c> declares it; a
    /// <c>.queen</c> file is read with the profile its own name says (<c>project.queen</c>: <c>project</c>,
    /// <c>nest.queen</c>: <c>nest</c>). Where a file is decides which profiles it sees, searched in this order:
    /// <list type="number">
    /// <item>the profiles its project's tools bring, then the user's tools' (<see cref="ToolFolders"/>, which larva sets: a
    /// tool package carries <c>profiles/&lt;name&gt;/template.tqueen</c>);</item>
    /// <item>the repository's own, in <c>.larva/profiles/</c> (from the file's folder up to the repository's top);</item>
    /// <item><c>LARVA_PROFILES</c> (folders separated by the system's path separator), then <c>Profiles/</c> next to larva;</item>
    /// <item>larva's built-in profiles (<c>project</c>, <c>package</c>).</item>
    /// </list>
    /// A built-in profile's name is larva's: only the folders of step 3 may replace it (a tool or a repository cannot say
    /// what a project is). Each template is read once, and again when its file changes.
    /// </summary>
    public static class ProfileCatalog
    {
        public const string TemplateFile = "template.tqueen";
        public const string Extension = ".queen";
        public const string TemplateExtension = ".tqueen";

        /// <summary>A repository's own profiles: <c>.larva/profiles/&lt;name&gt;/template.tqueen</c>.</summary>
        public const string RepositoryFolder = ".larva/profiles";

        private static readonly object gate = new();
        private static readonly Dictionary<string, Entry> cache = new(StringComparer.OrdinalIgnoreCase);

        private sealed record Entry(QueenProfile? Profile, string Stamp, List<QueenDiagnostic> Problems);

        /// <summary>
        /// The folders of profiles the tools bring for a file or folder (its project's tools, then the user's): each a
        /// folder of <c>&lt;name&gt;/template.tqueen</c>. Set by larva, which knows Hive and its tools; none when unset.
        /// </summary>
        public static Func<string, IEnumerable<string>>? ToolFolders { get; set; }

        /// <summary>The folders searched before the built-in profiles everywhere: <c>LARVA_PROFILES</c>, then <c>Profiles/</c> next to larva.</summary>
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

        /// <summary>
        /// The folders searched for a file or folder at <paramref name="near"/> (none: only the global ones), in order:
        /// its tools', its repository's, the global ones.
        /// </summary>
        public static IReadOnlyList<string> FoldersFor(string? near)
        {
            var folders = new List<string>();
            if (near is not null)
            {
                string start = Path.GetFullPath(near);
                if (ToolFolders is { } tools)
                {
                    try
                    {
                        folders.AddRange(tools(start));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
                    {
                        // A tool store that cannot be read brings nothing.
                    }
                }
                for (string? folder = Directory.Exists(start) ? start : Path.GetDirectoryName(start); folder is not null; folder = Path.GetDirectoryName(folder))
                {
                    string repository = Path.Combine(folder, RepositoryFolder);
                    if (Directory.Exists(repository))
                        folders.Add(repository);
                    if (Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git")))
                        break;
                }
            }
            folders.AddRange(Folders);
            return folders;
        }

        /// <summary>The names of the built-in profiles.</summary>
        public static IReadOnlyList<string> BuiltIn { get; } = [.. typeof(ProfileCatalog).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Profiles/", StringComparison.Ordinal) && n.EndsWith("/" + TemplateFile, StringComparison.Ordinal))
            .Select(n => n["Profiles/".Length..^(TemplateFile.Length + 1)]).Order(StringComparer.Ordinal)];

        /// <summary>
        /// The profile a file's name chooses (<c>…/project.queen</c>: <c>project</c>), as the file's place sees it (its
        /// tools' and repository's profiles too), or null when no profile has that name.
        /// </summary>
        public static QueenProfile? ForFile(string path) => NameOf(path) is { } name ? Get(name, Path.GetDirectoryName(Path.GetFullPath(path))) : null;

        /// <summary>The profile name a <c>.queen</c> file's name says, or null for another kind of file.</summary>
        public static string? NameOf(string path)
        {
            string file = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/').Split('/')[^1]);
            return file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) && file.Length > Extension.Length ? file[..^Extension.Length] : null;
        }

        /// <summary>Whether a file is a project's (<c>project.queen</c>, or a file whose profile extends <c>project</c>: <c>package.queen</c>).</summary>
        public static bool IsProjectFile(string path) => NameOf(path) is { } name && Get(name)?.Is("project") == true;

        /// <summary>
        /// The file names that are projects' (in the order a folder is searched): <c>project.queen</c>, <c>package.queen</c>.
        /// Only larva's own profiles make projects (a tool's profile may extend <c>project</c>, but its files are not projects).
        /// </summary>
        public static IEnumerable<string> ProjectFileNames =>
            new[] { "project" }.Concat(BuiltIn.Where(n => n != "project")).Where(n => Get(n)?.Is("project") == true).Select(n => n + Extension);

        /// <summary>Every profile's name a file or folder at <paramref name="near"/> sees (built in, and in its folders).</summary>
        public static IEnumerable<string> Names(string? near = null)
        {
            var names = new SortedSet<string>(BuiltIn, StringComparer.Ordinal);
            foreach (string folder in FoldersFor(near).Where(Directory.Exists))
            {
                foreach (string sub in Directory.EnumerateDirectories(folder).Where(d => File.Exists(Path.Combine(d, TemplateFile))))
                    names.Add(Path.GetFileName(sub));
            }
            return names;
        }

        /// <summary>The profile named <paramref name="name"/> as a file or folder at <paramref name="near"/> sees it (none: the global ones), or null.</summary>
        public static QueenProfile? Get(string name, string? near = null) => Load(name, near, [])?.Profile;

        /// <summary>The problems of the profile's <c>template.tqueen</c> (TQ…), when it was read.</summary>
        public static IReadOnlyList<QueenDiagnostic> ProblemsOf(string name, string? near = null) => Load(name, near, [])?.Problems ?? [];

        /// <summary>Where the profile <paramref name="name"/> comes from for <paramref name="near"/>: its template's path, or <c>builtin:name</c>; null when there is none.</summary>
        public static string? SourceOf(string name, string? near = null) => Locate(name, near)?.File;

        /// <summary>Forgets what was read (a <c>.tqueen</c> was saved): profiles are read again when asked for.</summary>
        public static void Invalidate()
        {
            lock (gate)
                cache.Clear();
        }

        private static Entry? Load(string name, string? near, HashSet<string> resolving)
        {
            if (name.Length == 0 || name.IndexOfAny(['/', '\\', '.']) >= 0)
                return null;
            if (Locate(name, near) is not var (file, stamp))
                return null;
            lock (gate)
            {
                if (cache.TryGetValue(file, out var cached) && cached.Stamp == stamp)
                    return cached;
            }
            if (Read(name, file) is not { } text)
                return null;
            // `extends`: the base is the one this place sees, read first (a cycle of profiles stops at the one seen again).
            resolving.Add(name);
            var problems = new List<QueenDiagnostic>();
            var profile = ProfileParser.Parse(text, file, name, problems, other => resolving.Contains(other) ? null : Load(other, near, resolving)?.Profile);
            resolving.Remove(name);
            var entry = new Entry(profile, stamp, problems);
            lock (gate)
                cache[file] = entry;
            return entry;
        }

        /// <summary>Where a profile's template is for <paramref name="near"/>, and what tells a change, without reading it; null when there is none.</summary>
        private static (string File, string Stamp)? Locate(string name, string? near)
        {
            // Larva's own names: only the global folders may replace them (not a tool's or a repository's).
            bool own = BuiltIn.Contains(name);
            foreach (string folder in own ? Folders : FoldersFor(near))
            {
                string file = Path.Combine(folder, name, TemplateFile);
                try
                {
                    if (File.Exists(file))
                        return (Path.GetFullPath(file), File.GetLastWriteTimeUtc(file).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
            return own ? ($"builtin:{name}", "builtin") : null;
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
