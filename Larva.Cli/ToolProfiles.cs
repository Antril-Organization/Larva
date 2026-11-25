using Hive;
using Larva.Build;
using Larva.Build.Profiles;

/// <summary>
/// The queen profiles tools bring: a tool package carries <c>profiles/&lt;name&gt;/template.tqueen</c> (its project's
/// <c>profiles/</c> folder, packed by <c>larva hive pack</c>), and a file sees the profiles of its project's tools (TOOLS,
/// at the versions its hive.lock pins), then those of the user's tools (<c>larva tool install -g</c>). Profiles are
/// declarations only: what a tool brings runs nothing (the package was checked when it was installed).
/// </summary>
internal static class ToolProfiles
{
    /// <summary>A tool package's profiles, under its folder in the Hive cache.</summary>
    public const string PackageFolder = Hive.HivePacker.ProfilesFolder;

    /// <summary>Gives the profile catalog the tools' profiles (for every command of this process).</summary>
    public static void Register() => ProfileCatalog.ToolFolders = For;

    private static readonly object gate = new();
    private static readonly Dictionary<string, (string Stamp, List<string> Folders)> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folders of profiles for a file or folder: its project's tools', then the user's tools'.</summary>
    private static IEnumerable<string> For(string near)
    {
        string? queen = QueenProject.Nearest(near);
        string key = queen ?? "";
        // What tells a change: the project file, its lock and the user's tools.
        string stamp = string.Join('|', new[] { queen, queen is null ? null : Path.Combine(Path.GetDirectoryName(queen)!, HiveLock.FileName), ToolCommand.GlobalList }
            .Select(f => f is not null && File.Exists(f) ? File.GetLastWriteTimeUtc(f).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-"));
        lock (gate)
        {
            if (cache.TryGetValue(key, out var known) && known.Stamp == stamp)
                return known.Folders;
        }
        var folders = new List<string>();
        void Add(HiveCache store, PackageReference package, string? version)
        {
            if (version is null)
                return;
            string folder = Path.Combine(store.Folder(package, version), PackageFolder);
            if (Directory.Exists(folder) && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                folders.Add(folder);
        }
        if (queen is not null && QueenParser.Parse(File.ReadAllText(queen), queen, []) is { } document)
        {
            // The project file read as written (not loaded: its sources are not looked for), for its TOOLS and its cache.
            var store = document.Named("HIVE").SelectMany(c => c.Entries).LastOrDefault(e => e.Key == "cache") is { } written
                ? new HiveCache(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(queen)!, written.Value.Display)))
                : HiveCache.Default();
            var pins = HiveLock.Load(Path.GetDirectoryName(queen)!);
            foreach (var value in document.Named("TOOLS").SelectMany(c => c.Entries).Where(e => e.Key == "tool")
                         .SelectMany(e => e.Value is QueenList list ? list.Items : [e.Value]))
            {
                if (PackageReference.Parse(value.Display) is not { } tool)
                    continue;
                Add(store, tool, pins?.Find(tool.Id)?.Version
                    ?? store.Versions(tool).FirstOrDefault(v => tool.Version is null || tool.Version.Accepts(VersionRange.Parse(v)!.Version)));
            }
        }
        foreach (var tool in ToolCommand.LoadGlobal())
            Add(HiveCache.Default(), tool.Package, tool.Version);
        lock (gate)
            cache[key] = (stamp, folders);
        return folders;
    }
}
