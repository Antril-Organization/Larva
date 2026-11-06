using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Larva.Build;

/// <summary>
/// What larva trusts in a language installed from a registry (<see cref="LanguagePack.FromRegistry"/>): the registry's
/// signature (<c>registry</c> in the manifest), by a key built into larva or added with <c>larva config languages.trust</c>,
/// and the author's key, pinned in <c>trust.json</c> of the user's language folder the first time the language is installed.
/// </summary>
internal static partial class LanguageTrust
{
    /// <summary>The registry keys larva ships with (base64 SubjectPublicKeyInfo, ECDSA P-256): only public keys.</summary>
    private static readonly string[] BuiltInRegistryKeys =
    [
        // d564cff9f59773de5596532778d7d563bb81376d5a2eb2e6bd5eff48e2ecfcff
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEi+debF+O//rdbnBmx7HVFLoxxVr+wjBNimtlWnZDNpzAYhiVLDTubji2RyrPxtykvnmrX3KeYCL1n5rVpyh5TQ==",
    ];

    public const string FileName = "trust.json";

    public static string FilePath => Path.Combine(LanguageRegistry.UserFolder, FileName);

    /// <summary>A key's fingerprint: the SHA-256 of its SubjectPublicKeyInfo, hex.</summary>
    public static string Fingerprint(string publicKey) => Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKey)));

    public static string Short(string? fingerprint) => fingerprint is null ? "?" : fingerprint[..Math.Min(16, fingerprint.Length)];

    /// <summary>The registry keys larva trusts, by fingerprint: the built-in ones and <c>languages.trust</c>.</summary>
    public static Dictionary<string, string> RegistryKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in BuiltInRegistryKeys.Concat(LarvaDefaults.List(LarvaDefaults.LanguagesTrust)))
        {
            if (LarvaDefaults.IsPublicKey(key))
                keys.TryAdd(Fingerprint(key), key);
        }
        return keys;
    }

    // ── Pinned author keys ───────────────────────

    private static JsonObject Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllBytes(FilePath)) is JsonObject trust)
                return trust;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable file pins nothing: installed languages are then refused, never trusted.
        }
        return new JsonObject();
    }

    private static void Save(JsonObject trust)
    {
        Directory.CreateDirectory(LanguageRegistry.UserFolder);
        File.WriteAllBytes(FilePath + ".partial", Json.Indented(trust));
        File.Move(FilePath + ".partial", FilePath, overwrite: true);
    }

    /// <summary>The author key fingerprint pinned for a language, or null.</summary>
    public static string? Pinned(string id) => Json.Text(Load()["authors"]?[id], "fingerprint");

    public static void Pin(string id, string fingerprint, string publicKey)
    {
        var trust = Load();
        if (trust["authors"] is not JsonObject authors)
            trust["authors"] = authors = new JsonObject();
        authors[id] = new JsonObject { ["fingerprint"] = fingerprint, ["publicKey"] = publicKey };
        Save(trust);
    }

    public static bool Forget(string id)
    {
        var trust = Load();
        if (trust["authors"] is not JsonObject authors || !authors.Remove(id))
            return false;
        Save(trust);
        return true;
    }

    // ── Checks ─────────────────────────────────────

    /// <summary>Why a pack installed from a registry cannot be used (its author's own signature already checked), or null.</summary>
    public static string? Problem(LanguagePack pack)
    {
        if (!pack.Signed)
            return "it was installed from a registry, but its author did not sign it";
        if (RegistryProblem(pack.Manifest) is { } problem)
            return problem;
        if (Pinned(pack.Id) is not { } pinned)
            return $"no author key is pinned for `{pack.Id}` ({FilePath}): install it with `larva languages install {pack.Id}`";
        if (!string.Equals(pinned, pack.Fingerprint, StringComparison.OrdinalIgnoreCase))
            return $"it is signed by author key {Short(pack.Fingerprint)}, not by the key pinned for `{pack.Id}` ({Short(pinned)})";
        return null;
    }

    /// <summary>Why a manifest's <c>registry</c> signature is not one by a trusted registry key, or null.</summary>
    public static string? RegistryProblem(JsonObject manifest)
    {
        if (manifest["registry"] is not JsonObject registry)
            return "no registry signed it (its manifest has no `registry`)";
        if (Json.Text(registry, "fingerprint") is not { } fingerprint || Json.Text(registry, "signature") is not { } signature)
            return "its `registry` signature is incomplete (`fingerprint` and `signature`)";
        if (!RegistryKeys().TryGetValue(fingerprint, out var key))
            return $"it is signed by registry key {Short(fingerprint)}, which larva does not trust (`larva config languages.trust <public key>` trusts one)";
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return "its registry signature is not base64";
        }
        return LanguagePack.VerifySignature(LanguagePack.RegistryCanonical(manifest), bytes, key)
            ? null : "its registry signature does not match its manifest (changed since the registry signed it)";
    }

    // ── What a registry accepts ────────────────────

    /// <summary>A language id: lowercase letters, digits and <c>-</c>.</summary>
    public static bool IsId(string id) => id.Length is > 0 and <= 64 && IdPattern().IsMatch(id);

    /// <summary>An exact version (<c>1.2.0</c>, <c>1.2.0-beta.1</c>), never a range.</summary>
    public static bool IsExactVersion(string version) => version.Length <= 64 && VersionPattern().IsMatch(version);

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$")]
    private static partial Regex VersionPattern();
}

/// <summary>The platforms a language pack is built for: one pack (and one zip) each.</summary>
internal static class LanguagePlatform
{
    public static readonly string[] Known = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];

    /// <summary>This larva's platform: the packs it can load.</summary>
    public static string Current
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unknown";
            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                var other => other.ToString().ToLowerInvariant(),
            };
            return $"{os}-{arch}";
        }
    }
}

/// <summary>Small helpers over <see cref="JsonNode"/> (no reflection: larva is compiled ahead of time).</summary>
internal static class Json
{
    public static string? Text(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    public static int? Number(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue(out int number) ? number : null;

    public static long? Long(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue(out long number) ? number : null;

    public static IEnumerable<JsonObject> Objects(JsonNode? node, string key) =>
        node is JsonObject obj && obj[key] is JsonArray list ? list.OfType<JsonObject>() : [];

    public static byte[] Indented(JsonNode node)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            node.WriteTo(writer);
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    public static JsonObject? Parse(byte[] bytes, string what)
    {
        try
        {
            return JsonNode.Parse(bytes) as JsonObject ?? throw new LanguagePackException($"{what} is not a JSON object");
        }
        catch (JsonException e)
        {
            throw new LanguagePackException($"{what} is not valid JSON: {e.Message}");
        }
    }
}

/// <summary>
/// A languages registry: <c>index.json</c> (every approved language), <c>{id}/index.json</c> (its versions and packs) and
/// <c>{id}/{version}/{id}-{version}-{platform}.zip</c>. A folder (<c>file:</c> or a path) or an https address (plain http
/// only to this machine); publishing requests go to https registries only.
/// </summary>
internal abstract class LanguageSource
{
    public abstract string Address { get; }

    /// <summary>A file of the registry (a path relative to its root), or null when it does not have it.</summary>
    public abstract byte[]? Get(string relative, long limit);

    public JsonObject? Json(string relative) => Get(relative, 16 * 1024 * 1024) is { } bytes ? global::Json.Parse(bytes, $"{Address} {relative}") : null;

    /// <summary>The registry named by <c>--provider</c>, else <c>languages.provider</c>, else the default one.</summary>
    public static LanguageSource For(string? option) =>
        Parse(option ?? LarvaDefaults.Effective().GetValueOrDefault(LarvaDefaults.LanguagesProvider) ?? LarvaDefaults.DefaultLanguagesProvider);

    public static LanguageSource Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            string path = text[5..];
            // file:///C:/x and file:/x: the path after the slashes (a drive letter keeps no slash before it).
            if (path.StartsWith("//"))
                path = path.TrimStart('/');
            if (OperatingSystem.IsWindows() && path.StartsWith('/') && path.Length > 2 && path[2] == ':')
                path = path[1..];
            else if (!OperatingSystem.IsWindows() && !path.StartsWith('/') && text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                path = "/" + path;
            return new FolderLanguageSource(Path.GetFullPath(path));
        }
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new HttpLanguageSource(new Uri(text.TrimEnd('/') + "/"));
        if (Directory.Exists(text) || text.StartsWith('.') || Path.IsPathRooted(text))
            return new FolderLanguageSource(Path.GetFullPath(text));
        return new HttpLanguageSource(new Uri("https://" + text.TrimEnd('/') + "/"));
    }
}

internal sealed class FolderLanguageSource(string root) : LanguageSource
{
    public string Root { get; } = root;

    public override string Address => Root;

    public override byte[]? Get(string relative, long limit)
    {
        string path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new LanguagePackException($"`{relative}` is outside the registry");
        if (!File.Exists(path))
            return null;
        if (new FileInfo(path).Length > limit)
            throw new LanguagePackException($"{path} is larger than larva accepts");
        return File.ReadAllBytes(path);
    }
}

internal sealed class HttpLanguageSource : LanguageSource
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Uri root;

    public HttpLanguageSource(Uri root)
    {
        if (root.Scheme != Uri.UriSchemeHttps && !root.IsLoopback)
            throw new LanguagePackException($"`{root}`: registries are reached over https (plain http only on this machine)");
        this.root = root;
    }

    public override string Address => root.ToString();

    public override byte[]? Get(string relative, long limit)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, relative));
        using var response = Send(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new LanguagePackException($"{request.RequestUri}: {Failure(response)}");
        return Body(response, limit);
    }

    /// <summary><c>POST requests</c>: one multipart part per platform (named after it), each the zip of its pack.</summary>
    public JsonObject Request(IReadOnlyList<(string Platform, string FileName, byte[] Zip)> packs)
    {
        using var content = new MultipartFormDataContent();
        foreach (var (platform, fileName, zip) in packs)
        {
            var part = new ByteArrayContent(zip);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            content.Add(part, platform, fileName);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "requests")) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token()
            ?? throw new LanguagePackException($"sending a request to {root} needs your API token in HIVE_TOKEN"));
        using var response = Send(request);
        if (!response.IsSuccessStatusCode)
            throw new LanguagePackException($"{root} refused the request: {Failure(response)}");
        return global::Json.Parse(Body(response, 1024 * 1024), "the registry's answer")!;
    }

    /// <summary><c>GET requests/{id}</c>: where a publication request stands (null: no such request of yours).</summary>
    public JsonObject? Status(string requestId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "requests/" + Uri.EscapeDataString(requestId)));
        if (Token() is { } token)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = Send(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new LanguagePackException($"{request.RequestUri}: {Failure(response)}");
        return global::Json.Parse(Body(response, 1024 * 1024), "the registry's answer");
    }

    private static string? Token() => Environment.GetEnvironmentVariable("HIVE_TOKEN") is { Length: > 0 } token ? token : null;

    private static HttpResponseMessage Send(HttpRequestMessage request)
    {
        try
        {
            return Client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (HttpRequestException e)
        {
            throw new LanguagePackException($"cannot reach {request.RequestUri?.Host}: {e.Message}");
        }
        catch (TaskCanceledException)
        {
            throw new LanguagePackException($"{request.RequestUri?.Host} did not answer in time");
        }
    }

    /// <summary>The registry's reason: its plain-text body, else the reason phrase.</summary>
    private static string Failure(HttpResponseMessage response)
    {
        string text = "";
        try
        {
            text = Encoding.UTF8.GetString(Body(response, 64 * 1024)).Trim();
        }
        catch (Exception e) when (e is LanguagePackException or IOException or HttpRequestException)
        {
        }
        if (text.Length > 500)
            text = text[..500] + "…";
        return $"{(int)response.StatusCode} {(text.Length > 0 && !text.StartsWith('<') ? text : response.ReasonPhrase)}";
    }

    private static byte[] Body(HttpResponseMessage response, long limit)
    {
        if (response.Content.Headers.ContentLength > limit)
            throw new LanguagePackException("the registry sent more than larva accepts");
        using var input = response.Content.ReadAsStream();
        var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (output.Length + read > limit)
                throw new LanguagePackException("the registry sent more than larva accepts");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}

/// <summary>
/// <c>larva languages search | install | update | remove</c>: languages from a registry, in the user's language folder.
/// An install downloads the platform's zip, checks its SHA-256 against the registry's index, unpacks it (no path leaves the
/// pack) into a staging folder, verifies all of it (files, author and registry signatures, the pinned author key, abi,
/// larva version) and only then moves it into place.
/// </summary>
internal static class LanguageInstaller
{
    public const long MaxPack = 1L << 30;

    private static string Staging => Path.Combine(LanguageRegistry.UserFolder, ".staging");

    public static int Search(string? text, LanguageSource source)
    {
        var index = source.Json("index.json") ?? throw new LanguagePackException($"{source.Address} has no index.json: not a languages registry");
        var found = Json.Objects(index, "languages").Where(l => text is null || Matches(l, text)).ToList();
        if (found.Count == 0)
        {
            Console.WriteLine(text is null ? $"{source.Address} has no languages" : $"no language matches `{text}` in {source.Address}");
            return 0;
        }
        foreach (var language in found.OrderBy(l => Json.Text(l, "id"), StringComparer.Ordinal))
        {
            string id = Json.Text(language, "id") ?? "?";
            string extensions = language["extensions"] is JsonArray list ? string.Join(" ", list.Select(e => e?.ToString())) : "";
            var installed = Installed(id);
            string mark = installed is null ? "" : $"   (installed: {installed.Version})";
            Console.WriteLine($"{id} {Json.Text(language, "latest") ?? "?"}  {Json.Text(language, "name") ?? id} ({extensions}){mark}");
            if (Json.Text(language, "description") is { } description)
                Console.WriteLine($"  {description}");
            if (Json.Text(language, "author") is { } author)
                Console.WriteLine($"  author:   {author}");
            if (Json.Text(language, "homepage") is { } homepage)
                Console.WriteLine($"  homepage: {homepage}");
        }
        return 0;
    }

    private static bool Matches(JsonObject language, string text) =>
        new[] { "id", "name", "description", "author" }.Any(k => Json.Text(language, k)?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
        || language["extensions"] is JsonArray list && list.Any(e => e?.ToString().TrimStart('.').Equals(text.TrimStart('.'), StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>The pack installed in the user's folder for an id, or null.</summary>
    private static LanguagePack? Installed(string id)
    {
        string folder = Path.Combine(LanguageRegistry.UserFolder, id);
        return LanguageTrust.IsId(id) && File.Exists(Path.Combine(folder, LanguagePack.ManifestName))
            ? LanguagePack.Read(folder, out _, fromRegistry: true) : null;
    }

    public static int Install(string reference, LanguageSource source)
    {
        string id = reference, version = "";
        if (reference.IndexOf('@') is >= 0 and var at)
            (id, version) = (reference[..at], reference[(at + 1)..]);
        if (!LanguageTrust.IsId(id))
            throw new LanguagePackException($"`{id}` is not a language id (lowercase letters, digits and -)");
        if (version.Length > 0 && !LanguageTrust.IsExactVersion(version))
            throw new LanguagePackException($"`{version}` is not an exact version (1.2.0)");
        var index = Index(id, source);
        var chosen = Choose(index, id, version.Length > 0 ? version : null, source);
        var installed = Installed(id);
        if (installed is not null && installed.Version == Json.Text(chosen.Version, "version") && installed.Verify() is null)
        {
            Console.WriteLine($"{id} {installed.Version} is already installed ({installed.Folder})");
            return 0;
        }
        InstallPack(id, index, chosen.Version, chosen.Pack, source, installed);
        return 0;
    }

    public static int Update(string? id, LanguageSource source)
    {
        List<string> ids;
        if (id is not null)
        {
            if (Installed(id) is null)
                throw new LanguagePackException($"{id} is not installed in {LanguageRegistry.UserFolder} (`larva languages install {id}`)");
            ids = [id];
        }
        else
        {
            ids = Directory.Exists(LanguageRegistry.UserFolder)
                ? [.. Directory.EnumerateDirectories(LanguageRegistry.UserFolder).Select(Path.GetFileName).OfType<string>().Where(i => Installed(i) is not null).Order(StringComparer.Ordinal)]
                : [];
            if (ids.Count == 0)
            {
                Console.WriteLine($"no language is installed from a registry ({LanguageRegistry.UserFolder})");
                return 0;
            }
        }
        int failed = 0;
        foreach (string language in ids)
        {
            try
            {
                var installed = Installed(language)!;
                var index = Index(language, source);
                var chosen = Choose(index, language, null, source);
                string newest = Json.Text(chosen.Version, "version")!;
                if (LarvaVersion.Compare(newest, installed.Version) <= 0 && installed.Verify() is null)
                    Console.WriteLine($"{language} {installed.Version} is up to date");
                else
                    InstallPack(language, index, chosen.Version, chosen.Pack, source, installed);
            }
            catch (LanguagePackException e)
            {
                Console.Error.WriteLine($"larva languages: {language}: {e.Message}");
                failed++;
            }
        }
        return failed == 0 ? 0 : 1;
    }

    public static int Remove(string id, bool forgetKey)
    {
        if (!LanguageTrust.IsId(id))
            throw new LanguagePackException($"`{id}` is not a language id (lowercase letters, digits and -)");
        string folder = Path.Combine(LanguageRegistry.UserFolder, id);
        if (Installed(id) is null)
        {
            string elsewhere = LanguageRegistry.Named(id) is { } other ? $"; {other.Name} in {other.Folder} came with larva or is a working copy, and is not removed by larva" : "";
            throw new LanguagePackException($"{id} is not installed in {LanguageRegistry.UserFolder}{elsewhere}");
        }
        Directory.Delete(folder, recursive: true);
        Console.WriteLine($"removed {id} ({folder})");
        if (forgetKey && LanguageTrust.Forget(id))
            Console.WriteLine($"forgot the author key pinned for {id}");
        return 0;
    }

    private static JsonObject Index(string id, LanguageSource source) =>
        source.Json($"{id}/index.json") ?? throw new LanguagePackException($"{source.Address} has no language `{id}` (`larva languages search`)");

    /// <summary>The version to install (the newest this larva can use, or the one asked for) and its pack for this platform.</summary>
    private static (JsonObject Version, JsonObject Pack) Choose(JsonObject index, string id, string? wanted, LanguageSource source)
    {
        string platform = LanguagePlatform.Current;
        var versions = Json.Objects(index, "versions").Where(v => Json.Text(v, "version") is not null).ToList();
        if (wanted is not null)
        {
            var version = versions.FirstOrDefault(v => Json.Text(v, "version") == wanted)
                ?? throw new LanguagePackException($"{source.Address} has no {id} {wanted} (it has {List(versions)})");
            if (Unusable(version) is { } why)
                throw new LanguagePackException($"{id} {wanted}: {why}");
            return (version, PackFor(version, id, platform));
        }
        var usable = versions.Where(v => Unusable(v) is null && Json.Objects(v, "packs").Any(p => Json.Text(p, "platform") == platform))
            .OrderByDescending(v => Json.Text(v, "version")!, Comparer<string>.Create(LarvaVersion.Compare)).ToList();
        if (usable.Count > 0)
            return (usable[0], PackFor(usable[0], id, platform));
        if (versions.Count == 0)
            throw new LanguagePackException($"{id} has no published version");
        var newest = versions.OrderByDescending(v => Json.Text(v, "version")!, Comparer<string>.Create(LarvaVersion.Compare)).First();
        throw new LanguagePackException($"no version of {id} works here: {Json.Text(newest, "version")} " +
            (Unusable(newest) ?? $"has no pack for {platform}"));
    }

    private static string List(List<JsonObject> versions) =>
        versions.Count == 0 ? "none" : string.Join(", ", versions.Select(v => Json.Text(v, "version")));

    /// <summary>Why this larva cannot use a version (its abi, the larva it needs), or null.</summary>
    private static string? Unusable(JsonObject version)
    {
        if (Json.Number(version, "abi") is { } abi && abi != LanguagePack.Abi)
            return $"it answers requests of version {abi}; this larva makes those of version {LanguagePack.Abi}";
        if (Json.Text(version, "larva") is { } needed && LarvaVersion.Compare(LarvaVersion.Current, needed) < 0)
            return $"it needs larva {needed} or later (this is {LarvaVersion.Current})";
        return null;
    }

    private static JsonObject PackFor(JsonObject version, string id, string platform) =>
        Json.Objects(version, "packs").FirstOrDefault(p => Json.Text(p, "platform") == platform)
        ?? throw new LanguagePackException($"{id} {Json.Text(version, "version")} has no pack for {platform} " +
            $"(it has {string.Join(", ", Json.Objects(version, "packs").Select(p => Json.Text(p, "platform")))})");

    private static void InstallPack(string id, JsonObject index, JsonObject versionEntry, JsonObject packEntry, LanguageSource source, LanguagePack? previous)
    {
        string version = Json.Text(versionEntry, "version")!;
        string platform = Json.Text(packEntry, "platform")!;
        if (!LanguageTrust.IsExactVersion(version))
            throw new LanguagePackException($"the registry lists `{version}`, not an exact version");
        string file = $"{id}-{version}-{platform}.zip";
        byte[] zip = source.Get($"{id}/{version}/{file}", MaxPack)
            ?? throw new LanguagePackException($"{source.Address} lists {id} {version} for {platform} but does not have {file}");
        if (Json.Long(packEntry, "size") is { } size && size != zip.Length)
            throw new LanguagePackException($"{file} is {zip.Length} bytes; the registry's index says {size}");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(zip));
        if (!string.Equals(sha256, Json.Text(packEntry, "sha256"), StringComparison.OrdinalIgnoreCase))
            throw new LanguagePackException($"{file} is not the file the registry's index lists (its SHA-256 is {sha256}, the index says {Json.Text(packEntry, "sha256") ?? "none"})");

        CleanStaging();
        string staging = Path.Combine(Staging, Guid.NewGuid().ToString("N"));
        try
        {
            PackArchive.Extract(zip, staging);
            var pack = LanguagePack.Read(staging, out var problem) ?? throw new LanguagePackException($"{file}: {problem}");
            if (pack.Id != id || pack.Version != version)
                throw new LanguagePackException($"{file} holds {pack.Id} {pack.Version}, not {id} {version}");
            if (pack.Verify() is { } invalid)
                throw new LanguagePackException($"{id} {version} cannot be used: {invalid}");
            if (!pack.Signed)
                throw new LanguagePackException($"{id} {version} is not signed by its author");
            if (LanguageTrust.RegistryProblem(pack.Manifest) is { } untrusted)
                throw new LanguagePackException($"{id} {version} is refused: {untrusted}");
            string fingerprint = pack.Fingerprint!;
            string? pinned = LanguageTrust.Pinned(id);
            if (pinned is not null && !string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new LanguagePackException($"{id} {version} is signed by author key {LanguageTrust.Short(fingerprint)}, not by the key {id} was first installed with ({LanguageTrust.Short(pinned)}): refused. " +
                    $"If its author really changed keys, `larva languages remove {id} --forget-key` and install it again");
            if (Json.Text(index, "fingerprint") is { } listed && !string.Equals(listed, fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new LanguagePackException($"{id} {version} is signed by author key {LanguageTrust.Short(fingerprint)}, but the registry lists {LanguageTrust.Short(listed)} for {id}");

            string target = Path.Combine(LanguageRegistry.UserFolder, id);
            if (Directory.Exists(target))
            {
                string old = staging + ".old";
                Directory.Move(target, old);
                try
                {
                    Directory.Move(staging, target);
                }
                catch
                {
                    Directory.Move(old, target);
                    throw;
                }
                TryDelete(old);
            }
            else
            {
                Directory.Move(staging, target);
            }
            if (pinned is null)
                LanguageTrust.Pin(id, fingerprint, Json.Text(pack.Manifest, "publicKey")!);
            LanguageRegistry.Reset();
            string was = previous is null ? "" : $" (was {previous.Version})";
            Console.WriteLine($"installed {pack.Name} {version}{was} for {platform} in {target}");
            Console.WriteLine($"  author key {LanguageTrust.Short(fingerprint)}{(pinned is null ? " (pinned)" : "")}, registry key {LanguageTrust.Short(Json.Text(pack.Manifest["registry"], "fingerprint"))}");
            if (LanguageRegistry.Named(id) is { } used && !used.FromRegistry)
                Console.WriteLine($"  note: {used.Name} in {used.Folder} comes first; larva uses that one");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new LanguagePackException($"{id} {version} could not be installed: {e.Message}");
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Leftovers of installs that were stopped (or whose old folder was in use).</summary>
    private static void CleanStaging()
    {
        if (!Directory.Exists(Staging))
            return;
        foreach (string folder in Directory.EnumerateDirectories(Staging))
            TryDelete(folder);
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use (a pack an editor has loaded): removed by the next install.
        }
    }
}

/// <summary>A pack as a zip: its files at the root, <c>language.json</c> among them.</summary>
internal static class PackArchive
{
    private const long MaxUnpacked = 2L << 30;
    private const int MaxEntries = 10_000;

    /// <summary>The zip of a pack folder (entries in ordinal order, <c>/</c> separators).</summary>
    public static byte[] Create(string folder)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(p => (Path: p, Relative: Path.GetRelativePath(folder, p).Replace('\\', '/'))).OrderBy(p => p.Relative, StringComparer.Ordinal).Select(p => p.Path))
            {
                var entry = zip.CreateEntry(Path.GetRelativePath(folder, path).Replace('\\', '/'), CompressionLevel.Optimal);
                using var output = entry.Open();
                using var input = File.OpenRead(path);
                input.CopyTo(output);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>Unpacks a pack into a new folder; a path that would leave it (or is not a plain relative path) refuses the whole pack.</summary>
    public static void Extract(byte[] bytes, string folder)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries)
            throw new LanguagePackException($"the pack has more than {MaxEntries} files");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName;
            if (!IsSafe(name))
                throw new LanguagePackException($"the pack holds an unsafe path, `{name}`: refused");
            string path = Path.GetFullPath(Path.Combine(folder, name));
            if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new LanguagePackException($"the pack holds a path outside it, `{name}`: refused");
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            total += entry.Length;
            if (total > MaxUnpacked)
                throw new LanguagePackException("the pack unpacks to more than larva accepts");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    private static bool IsSafe(string name)
    {
        if (name.Length is 0 or > 400 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':'))
            return false;
        var parts = name.TrimEnd('/').Split('/');
        return parts.All(p => p.Length > 0 && p is not ("." or "..") && !p.EndsWith('.') && !p.EndsWith(' ') && !p.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*'));
    }
}
