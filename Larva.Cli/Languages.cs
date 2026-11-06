using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Larva.Hir.Encoding;

/// <summary>A language pack that cannot be used (or a request it could not answer), with the reason.</summary>
internal sealed class LanguagePackException(string message) : Exception(message);

/// <summary>
/// A language Larva can compile: a folder <c>Languages/&lt;Name&gt;/</c> with everything that turns the language's
/// source into Larva HIR (its frontend, a library with the entry points of <see cref="Abi"/>) and its manifest,
/// <c>language.json</c>:
/// <list type="bullet">
/// <item>who and what: <c>id</c>, <c>name</c>, <c>version</c>, <c>description</c>, <c>author</c>, <c>license</c>,
/// <c>homepage</c>, the source <c>extensions</c>;</item>
/// <item>what it needs: <c>abi</c> (the requests it answers), <c>larva</c> (the oldest larva it works with), <c>hir</c>
/// (the format of the Larva libraries it reads and writes), <c>entry</c> (its library);</item>
/// <item>security: <c>files</c> (the SHA-256 of each file of the pack: a changed or unlisted library is never loaded) and,
/// signed, <c>publicKey</c>, <c>fingerprint</c> and <c>signature</c> (ECDSA P-256 over the manifest without its
/// signature, keys in order: <c>larva languages pack --key</c>).</item>
/// </list>
/// </summary>
internal sealed unsafe class LanguagePack
{
    /// <summary>The requests this larva makes (a pack's manifest says which it answers).</summary>
    public const int Abi = 1;

    public const string ManifestName = "language.json";

    public required string Folder { get; init; }
    public required JsonObject Manifest { get; init; }

    public string Id => Text("id") ?? Path.GetFileName(Folder).ToLowerInvariant();
    public string Name => Text("name") ?? Id;
    public string Version => Text("version") ?? "?";
    public string? Author => Text("author");
    public string? Description => Text("description");
    public string? License => Text("license");
    public string? Homepage => Text("homepage");
    public string? Entry => Text("entry");
    public IReadOnlyList<string> Extensions =>
        Manifest["extensions"] is JsonArray list ? [.. list.Select(e => e?.GetValue<string>() ?? "").Where(e => e.StartsWith('.'))] : [];

    /// <summary>What tells one build of the pack from another: its library's SHA-256 (as its manifest lists it).</summary>
    public string Identity => Entry is { } entry && Manifest["files"]?[entry] is JsonValue hash ? hash.GetValue<string>() : Version;

    public bool Signed => Manifest["signature"] is not null;

    /// <summary>
    /// Installed from a registry (it lives in the user's language folder): used only when a registry key larva trusts signed
    /// it and its author's key is the one pinned when the language was first installed (<see cref="LanguageTrust"/>).
    /// </summary>
    public bool FromRegistry { get; init; }
    public string? Fingerprint => Text("fingerprint");

    private string? Text(string key) => Manifest[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private delegate* unmanaged<byte*, int, int*, byte*> call;
    private delegate* unmanaged<byte*, void> free;
    private JsonObject? info;

    /// <summary>The pack in <paramref name="folder"/> (its manifest read, nothing checked or loaded yet), or null with why.</summary>
    public static LanguagePack? Read(string folder, out string? problem, bool fromRegistry = false)
    {
        problem = null;
        string path = Path.Combine(folder, ManifestName);
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(path)) is JsonObject manifest)
                return new LanguagePack { Folder = Path.GetFullPath(folder), Manifest = manifest, FromRegistry = fromRegistry };
            problem = $"{path}: not a JSON object";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = $"{path}: {e.Message}";
        }
        return null;
    }

    /// <summary>Why the pack cannot be used (what it needs, its files, its signature), or null.</summary>
    public string? Verify()
    {
        if (Manifest["abi"]?.GetValue<int>() is not { } abi)
            return "its manifest has no `abi`";
        if (abi != Abi)
            return $"it answers requests of version {abi}; this larva makes those of version {Abi}";
        if (Text("larva") is { } needed && LarvaVersion.Compare(LarvaVersion.Current, needed) < 0)
            return $"it needs larva {needed} or later (this is {LarvaVersion.Current})";
        if (Manifest["hir"] is JsonValue hir && hir.GetValue<int>() != LvaFormat.Version)
            return $"it writes Larva libraries of format {hir}; this larva reads format {LvaFormat.Version}";
        if (Entry is null)
            return "its manifest has no `entry`";
        if (Manifest["files"] is not JsonObject { Count: > 0 } files)
            return "its manifest lists no `files` (with their SHA-256): it is not loaded";
        if (!files.ContainsKey(Entry))
            return $"its library {Entry} is not among its `files`";
        foreach (var (file, hash) in files)
        {
            string path = Path.GetFullPath(Path.Combine(Folder, file));
            if (!path.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return $"`files` names {file}, outside the pack";
            if (!File.Exists(path))
                return $"{file} is missing";
            if (!string.Equals(Hash(path), hash?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                return $"{file} is not the file its manifest lists (changed since the pack was made)";
        }
        // Code the manifest does not list is never there by accident.
        foreach (string path in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(Folder, path).Replace('\\', '/');
            if (IsCode(path) && !files.ContainsKey(relative))
                return $"{relative} is not among its `files`";
        }
        if (Signed)
        {
            if (Text("publicKey") is not { } key || Manifest["signature"]?.GetValue<string>() is not { } signature)
                return "its signature has no `publicKey`";
            string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(key)));
            if (!string.Equals(fingerprint, Fingerprint, StringComparison.OrdinalIgnoreCase))
                return "its `fingerprint` is not its key's";
            if (!VerifySignature(Canonical(Manifest), Convert.FromBase64String(signature), key))
                return "its signature does not match its manifest (changed since it was signed)";
        }
        if (FromRegistry && LanguageTrust.Problem(this) is { } untrusted)
            return untrusted;
        return null;
    }

    private static bool IsCode(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".so" or ".dylib" or ".exe";

    /// <summary>A file's SHA-256, as the manifest writes it (hex).</summary>
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// The manifest as its author signs it: without <c>signature</c> and <c>registry</c>, every object's keys in ordinal
    /// order, no spaces, UTF-8 (strings escaped as <see cref="Utf8JsonWriter"/> does by default).
    /// </summary>
    public static byte[] Canonical(JsonObject manifest) => Canonical(manifest, "signature", "registry");

    /// <summary>The manifest as a registry signs it: without <c>registry</c> (so the author's signature is signed too).</summary>
    public static byte[] RegistryCanonical(JsonObject manifest) => Canonical(manifest, "registry");

    /// <summary>The manifest without its top-level <paramref name="excluded"/> keys, every object's keys in ordinal order, no spaces.</summary>
    public static byte[] Canonical(JsonObject manifest, params string[] excluded)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(manifest, writer, excluded);
        return buffer.ToArray();

        static void Write(JsonNode? node, Utf8JsonWriter writer, string[] excluded)
        {
            switch (node)
            {
                case JsonObject obj:
                    writer.WriteStartObject();
                    foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (excluded.Contains(key))
                            continue;
                        writer.WritePropertyName(key);
                        Write(value, writer, []);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonArray array:
                    writer.WriteStartArray();
                    foreach (var item in array)
                        Write(item, writer, []);
                    writer.WriteEndArray();
                    break;
                case null:
                    writer.WriteNullValue();
                    break;
                default:
                    node.WriteTo(writer);
                    break;
            }
        }
    }

    public static bool VerifySignature(byte[] data, byte[] signature, string publicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }

    // ── Loading and requests ───────────────────────

    /// <summary>Checked and loaded, once: native code, or (in a working copy of larva, not native) a .NET library.</summary>
    private void Load()
    {
        if (call != null)
            return;
        if (Verify() is { } problem)
            throw new LanguagePackException($"the language pack {Name} ({Folder}) cannot be used: {problem}");
        string path = Path.Combine(Folder, Entry!);
        if (IsManaged(path))
            LoadManaged(path);
        else
        {
            nint library = NativeLibrary.Load(path);
            call = (delegate* unmanaged<byte*, int, int*, byte*>)NativeLibrary.GetExport(library, "larva_language_call");
            free = (delegate* unmanaged<byte*, void>)NativeLibrary.GetExport(library, "larva_language_free");
        }
        info = Call("info", []) as JsonObject ?? throw new LanguagePackException($"{Name}: no answer to `info`");
        if (info["hir"]?.GetValue<int>() is { } hir && hir != LvaFormat.Version)
            throw new LanguagePackException($"the language pack {Name} writes Larva libraries of format {hir}; this larva reads format {LvaFormat.Version}");
    }

    private static bool IsManaged(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        return pe.HasMetadata;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only a JIT larva (a working copy) loads .NET packs.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Only a JIT larva (a working copy) loads .NET packs.")]
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Only a JIT larva (a working copy) loads .NET packs.")]
    private void LoadManaged(string path)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new LanguagePackException($"the language pack {Name} is .NET code ({path}): an installed larva loads native packs");
        var assembly = Assembly.LoadFrom(path);
        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                switch (method.GetCustomAttribute<UnmanagedCallersOnlyAttribute>()?.EntryPoint)
                {
                    case "larva_language_call":
                        call = (delegate* unmanaged<byte*, int, int*, byte*>)method.MethodHandle.GetFunctionPointer();
                        break;
                    case "larva_language_free":
                        free = (delegate* unmanaged<byte*, void>)method.MethodHandle.GetFunctionPointer();
                        break;
                }
            }
        }
        if (call == null || free == null)
            throw new LanguagePackException($"the language pack {Name}: {path} has no larva_language_call and larva_language_free");
    }

    /// <summary>One request's result; the pack's error as a <see cref="LanguagePackException"/>.</summary>
    public JsonNode? Call(string method, JsonObject parameters)
    {
        Load();
        byte[] request = Encoding.UTF8.GetBytes(new JsonObject { ["method"] = method, ["params"] = parameters }.ToJsonString());
        int length;
        byte* result;
        fixed (byte* bytes = request)
            result = call(bytes, request.Length, &length);
        JsonObject response;
        try
        {
            response = JsonNode.Parse(new ReadOnlySpan<byte>(result, length)) as JsonObject ?? throw new LanguagePackException($"{Name}: an answer that is not JSON");
        }
        finally
        {
            free(result);
        }
        if (response["error"] is { } error)
            throw new LanguagePackException(error.GetValue<string>());
        // Detached from the response, so callers can put it in their own messages.
        var answer = response["result"];
        response.Remove("result");
        return answer;
    }

    /// <summary>What the pack says of itself (<c>info</c>): keywords, the editor's token legend…</summary>
    public JsonObject Info
    {
        get
        {
            Load();
            return info!;
        }
    }

    public IReadOnlyList<string> Keywords => Info["keywords"] is JsonArray list ? [.. list.Select(k => k!.GetValue<string>())] : [];

    /// <summary>An assembly for a request: its file (read by the pack, once while it does not change).</summary>
    public static JsonObject Reference(string path) => new() { ["path"] = Path.GetFullPath(path) };

    /// <summary>An assembly for a request: its bytes.</summary>
    public static JsonObject Reference(byte[] lva) => new() { ["data"] = Convert.ToBase64String(lva) };

    public override string ToString() => $"{Name} {Version}";
}

/// <summary>
/// The language packs installed: every folder with a <c>language.json</c> in <c>LARVA_LANGUAGES</c> (folders separated
/// by <c>;</c>), <c>Languages/</c> next to larva and <c>%LOCALAPPDATA%/Larva/Languages</c>; the first of an id wins.
/// </summary>
internal static class LanguageRegistry
{
    private static List<LanguagePack>? packs;
    private static readonly List<string> problems = [];

    public static IReadOnlyList<string> Folders
    {
        get
        {
            var folders = new List<string>();
            if (Environment.GetEnvironmentVariable("LARVA_LANGUAGES") is { Length: > 0 } list)
                folders.AddRange(list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
            folders.Add(Path.Combine(AppContext.BaseDirectory, "Languages"));
            folders.Add(UserFolder);
            return folders;
        }
    }

    /// <summary>
    /// Where <c>larva languages install</c> puts languages: <c>LARVA_USER_LANGUAGES</c>, else
    /// <c>%LOCALAPPDATA%/Larva/Languages</c>. Its packs come from a registry and are checked as such
    /// (<see cref="LanguagePack.FromRegistry"/>).
    /// </summary>
    public static string UserFolder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        Environment.GetEnvironmentVariable("LARVA_USER_LANGUAGES") is { Length: > 0 } given
            ? given
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Larva", "Languages")));

    private static bool IsUserFolder(string folder) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), UserFolder,
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Forgets what was read (after the user's folder changed).</summary>
    public static void Reset()
    {
        packs = null;
        problems.Clear();
    }

    public static IReadOnlyList<LanguagePack> All
    {
        get
        {
            if (packs is not null)
                return packs;
            packs = [];
            foreach (string folder in Folders.Where(Directory.Exists))
            {
                bool user = IsUserFolder(folder);
                foreach (string pack in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(Path.Combine(pack, LanguagePack.ManifestName)))
                        continue;
                    if (LanguagePack.Read(pack, out var problem, fromRegistry: user) is not { } read)
                        problems.Add(problem!);
                    else if (!packs.Any(p => p.Id.Equals(read.Id, StringComparison.OrdinalIgnoreCase)))
                        packs.Add(read);
                }
            }
            return packs;
        }
    }

    /// <summary>Manifests that could not be read.</summary>
    public static IReadOnlyList<string> Problems
    {
        get
        {
            _ = All;
            return problems;
        }
    }

    public static IReadOnlyList<string> Extensions => [.. All.SelectMany(p => p.Extensions).Distinct(StringComparer.OrdinalIgnoreCase)];

    public static bool IsSource(string path) => ForFile(path) is not null;

    /// <summary>The pack whose source <paramref name="path"/> is (by its extension), or null.</summary>
    public static LanguagePack? ForFile(string path) =>
        All.FirstOrDefault(p => p.Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The pack by id, name or extension (<c>mylang</c>, <c>.ml</c>, <c>ml</c>), or null.</summary>
    public static LanguagePack? Named(string name) => All.FirstOrDefault(p =>
        p.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
        || p.Extensions.Any(e => e.Equals(name, StringComparison.OrdinalIgnoreCase) || e.Equals("." + name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The one pack <paramref name="sources"/> are written in: null (with why) when none is, or several.</summary>
    public static LanguagePack? For(IReadOnlyCollection<string> sources, out string? problem)
    {
        problem = null;
        var found = sources.Select(s => (Source: s, Pack: ForFile(s))).ToList();
        if (found.FirstOrDefault(f => f.Pack is null) is { Source: { } unknown })
        {
            problem = $"{unknown}: no installed language reads {Path.GetExtension(unknown)} files ({Installed()})";
            return null;
        }
        var languages = found.Select(f => f.Pack!).Distinct().ToList();
        if (languages.Count > 1)
        {
            problem = $"the sources are in {string.Join(" and ", languages.Select(l => l.Name))}: a program is compiled from one language (the others' code as libraries, .dll)";
            return null;
        }
        return languages.FirstOrDefault();
    }

    /// <summary>The installed languages, for messages.</summary>
    public static string Installed() => All.Count == 0
        ? "no language is installed: see `larva languages`"
        : "installed: " + string.Join(", ", All.Select(p => $"{p.Name} ({string.Join(" ", p.Extensions)})"));
}

/// <summary>Larva's version, and versions compared.</summary>
internal static class LarvaVersion
{
    public static string Current { get; } = typeof(LarvaVersion).Assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
        is [AssemblyInformationalVersionAttribute { InformationalVersion: var informational }, ..] ? informational.Split('+')[0] : "0.0.0";

    /// <summary>Negative when <paramref name="a"/> is older than <paramref name="b"/>: numbers compared part by part.</summary>
    public static int Compare(string a, string b)
    {
        var x = a.TrimStart('v', '>', '=', '^').Split('.', '-');
        var y = b.TrimStart('v', '>', '=', '^').Split('.', '-');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = i < x.Length && int.TryParse(x[i], out var px) ? px : 0;
            int q = i < y.Length && int.TryParse(y[i], out var qy) ? qy : 0;
            if (p != q)
                return p.CompareTo(q);
        }
        return 0;
    }
}
