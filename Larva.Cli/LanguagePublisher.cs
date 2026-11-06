using System.Security.Cryptography;
using System.Text.Json.Nodes;

/// <summary>
/// <c>larva languages publish</c>: a publication request to a languages registry (one pack per platform, each completed,
/// signed with the author's key, verified and zipped, all sent as one multipart request); <c>larva languages status</c>:
/// where a request stands; <c>larva languages approve</c> (administrators, and tests): what a registry does when a request is
/// approved, into a folder registry: the registry's signature added to every platform's manifest, the zips and the indexes
/// written.
/// </summary>
internal static class LanguagePublisher
{
    /// <summary>A platform's pack, read and verified (author signature included).</summary>
    private sealed record PlatformPack(string Platform, LanguagePack Pack);

    public static int Publish(string folder, string? keyFile, LanguageSource source)
    {
        if (source is not HttpLanguageSource http)
            throw new LanguagePackException($"{source.Address} is a folder: requests are sent to an https registry (an administrator adds to a folder registry with `larva languages approve`)");
        folder = Path.GetFullPath(folder);
        using var key = keyFile is null ? null : LoadKey(keyFile);
        var platforms = PlatformFolders(folder);
        var packs = new List<PlatformPack>();
        foreach (var (platform, path) in platforms)
        {
            if (key is not null)
                Console.WriteLine($"{platform}: {LanguagesCommand.PackFolder(path, key, ask: platform == LanguagePlatform.Current)}");
            packs.Add(new PlatformPack(platform, Checked(path, platform)));
        }
        var (id, version) = Agree(packs);
        var zips = packs.Select(p => (p.Platform, $"{id}-{version}-{p.Platform}.zip", PackArchive.Create(p.Pack.Folder))).ToList();
        var answer = http.Request(zips);
        string request = Json.Text(answer, "id") ?? "?";
        Console.WriteLine($"request {request}: {Json.Text(answer, "status") ?? "pending"} ({id} {version}: {string.Join(", ", packs.Select(p => p.Platform))})");
        Console.WriteLine($"  an administrator of {http.Address} reviews it: `larva languages status {request}`");
        return 0;
    }

    public static int Status(string requestId, LanguageSource source)
    {
        if (source is not HttpLanguageSource http)
            throw new LanguagePackException($"{source.Address} is a folder: it has no requests");
        var status = http.Status(requestId) ?? throw new LanguagePackException($"{http.Address} has no request {requestId} of yours (is HIVE_TOKEN set?)");
        string platforms = status["platforms"] is JsonArray list ? string.Join(", ", list.Select(p => p?.ToString())) : "";
        Console.WriteLine($"request {Json.Text(status, "id") ?? requestId}: {Json.Text(status, "status") ?? "?"}");
        Console.WriteLine($"  language:  {Json.Text(status, "language")} {Json.Text(status, "version")} ({platforms})");
        if (Json.Text(status, "submitted") is { } submitted)
            Console.WriteLine($"  submitted: {submitted}");
        if (Json.Text(status, "reviewed") is { } reviewed)
            Console.WriteLine($"  reviewed:  {reviewed}");
        if (Json.Text(status, "reason") is { Length: > 0 } reason)
            Console.WriteLine($"  reason:    {reason}");
        return 0;
    }

    /// <summary>
    /// A request approved into a folder registry: <paramref name="source"/> is a pack folder (of <paramref name="platform"/>,
    /// else this machine's) or a request folder (a subfolder or a <c>&lt;platform&gt;.zip</c> per platform).
    /// </summary>
    public static int Approve(string source, string registryKeyFile, string into, string? platform)
    {
        source = Path.GetFullPath(source);
        into = Path.GetFullPath(into);
        using var key = LoadKey(registryKeyFile);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        string registryFingerprint = LanguageTrust.Fingerprint(publicKey);
        string work = Path.Combine(Path.GetTempPath(), "larva-approve-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            // Every pack is copied (or unpacked) first: what was sent is never changed.
            var inputs = new List<(string Platform, string Folder)>();
            if (File.Exists(Path.Combine(source, LanguagePack.ManifestName)))
            {
                string name = platform ?? LanguagePlatform.Current;
                if (!LanguagePlatform.Known.Contains(name))
                    throw new LanguagePackException($"`{name}` is not a platform ({string.Join(", ", LanguagePlatform.Known)})");
                inputs.Add((name, Copy(source, Path.Combine(work, name))));
            }
            else if (Directory.Exists(source))
            {
                foreach (string zip in Directory.EnumerateFiles(source, "*.zip").Order(StringComparer.Ordinal))
                {
                    string stem = Path.GetFileNameWithoutExtension(zip);
                    string name = LanguagePlatform.Known.FirstOrDefault(p => stem == p || stem.EndsWith("-" + p, StringComparison.Ordinal))
                        ?? throw new LanguagePackException($"{zip}: not named after a platform ({string.Join(", ", LanguagePlatform.Known)})");
                    PackArchive.Extract(File.ReadAllBytes(zip), Path.Combine(work, name));
                    inputs.Add((name, Path.Combine(work, name)));
                }
                if (inputs.Count == 0)
                    inputs.AddRange(PlatformFolders(source).Select(p => (p.Platform, Copy(p.Folder, Path.Combine(work, p.Platform)))));
            }
            else
            {
                throw new LanguagePackException($"{source} does not exist");
            }
            if (inputs.Select(i => i.Platform).Distinct().Count() != inputs.Count)
                throw new LanguagePackException("the request has two packs for one platform");

            var packs = inputs.Select(i => new PlatformPack(i.Platform, Checked(i.Folder, i.Platform))).ToList();
            var (id, version) = Agree(packs);
            string author = packs[0].Pack.Fingerprint!;
            string languageFolder = Path.Combine(into, id);
            string indexPath = Path.Combine(languageFolder, "index.json");
            var index = File.Exists(indexPath) ? Json.Parse(File.ReadAllBytes(indexPath), indexPath)! : new JsonObject();
            if (Json.Text(index, "fingerprint") is { } pinned && !string.Equals(pinned, author, StringComparison.OrdinalIgnoreCase))
                throw new LanguagePackException($"{id} is signed by author key {LanguageTrust.Short(author)}, not by its pinned key {LanguageTrust.Short(pinned)}");
            if (Json.Objects(index, "versions").Any(v => Json.Text(v, "version") == version))
                throw new LanguagePackException($"{id} {version} is already published");

            var entries = new JsonArray();
            string versionFolder = Path.Combine(languageFolder, version);
            Directory.CreateDirectory(versionFolder);
            foreach (var (name, pack) in packs.OrderBy(p => p.Platform, StringComparer.Ordinal))
            {
                var manifest = pack.Manifest;
                manifest["registry"] = new JsonObject
                {
                    ["fingerprint"] = registryFingerprint,
                    ["signature"] = Convert.ToBase64String(key.SignData(LanguagePack.RegistryCanonical(manifest), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
                };
                File.WriteAllBytes(Path.Combine(pack.Folder, LanguagePack.ManifestName), Json.Indented(manifest));
                byte[] zip = PackArchive.Create(pack.Folder);
                string file = $"{id}-{version}-{name}.zip";
                File.WriteAllBytes(Path.Combine(versionFolder, file), zip);
                entries.Add((JsonNode)new JsonObject
                {
                    ["platform"] = name,
                    ["file"] = file,
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(zip)),
                    ["size"] = zip.Length,
                });
            }

            var first = packs[0].Pack;
            var versions = new JsonArray();
            foreach (var existing in Json.Objects(index, "versions"))
                versions.Add(existing.DeepClone());
            versions.Add((JsonNode)new JsonObject
            {
                ["version"] = version,
                ["larva"] = Json.Text(first.Manifest, "larva"),
                ["abi"] = Json.Number(first.Manifest, "abi"),
                ["published"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["packs"] = entries,
            });
            var sorted = new JsonArray([.. versions.OfType<JsonObject>().OrderBy(v => Json.Text(v, "version")!, Comparer<string>.Create(LarvaVersion.Compare)).Select(v => (JsonNode)v.DeepClone())]);
            string latest = Json.Text(sorted[^1], "version")!;
            bool newest = latest == version;
            var language = new JsonObject
            {
                ["id"] = id,
                ["name"] = newest ? first.Name : Json.Text(index, "name") ?? first.Name,
                ["description"] = newest ? first.Description : Json.Text(index, "description") ?? first.Description,
                ["author"] = newest ? first.Author : Json.Text(index, "author") ?? first.Author,
                ["homepage"] = newest ? first.Homepage : Json.Text(index, "homepage") ?? first.Homepage,
                ["extensions"] = newest || index["extensions"] is null ? new JsonArray([.. first.Extensions.Select(e => (JsonNode)JsonValue.Create(e))]) : index["extensions"]!.DeepClone(),
                ["fingerprint"] = author,
                ["publicKey"] = Json.Text(first.Manifest, "publicKey"),
                ["versions"] = sorted,
            };
            WriteAtomic(indexPath, Json.Indented(language));

            string rootPath = Path.Combine(into, "index.json");
            var root = File.Exists(rootPath) ? Json.Parse(File.ReadAllBytes(rootPath), rootPath)! : new JsonObject();
            var languages = new JsonArray();
            foreach (var other in Json.Objects(root, "languages").Where(l => Json.Text(l, "id") != id))
                languages.Add(other.DeepClone());
            languages.Add((JsonNode)new JsonObject
            {
                ["id"] = id,
                ["name"] = language["name"]?.DeepClone(),
                ["description"] = language["description"]?.DeepClone(),
                ["author"] = language["author"]?.DeepClone(),
                ["homepage"] = language["homepage"]?.DeepClone(),
                ["extensions"] = language["extensions"]!.DeepClone(),
                ["latest"] = latest,
            });
            root["languages"] = new JsonArray([.. languages.OfType<JsonObject>().OrderBy(l => Json.Text(l, "id"), StringComparer.Ordinal).Select(l => (JsonNode)l.DeepClone())]);
            WriteAtomic(rootPath, Json.Indented(root));

            Console.WriteLine($"approved {id} {version} ({string.Join(", ", packs.Select(p => p.Platform))}) into {into}");
            Console.WriteLine($"  author key {LanguageTrust.Short(author)}, registry key {LanguageTrust.Short(registryFingerprint)}");
            return 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ── Shared ─────────────────────────────────────

    /// <summary>The platform folders of a publication: every subfolder, each named after a platform.</summary>
    private static List<(string Platform, string Folder)> PlatformFolders(string folder)
    {
        if (!Directory.Exists(folder))
            throw new LanguagePackException($"{folder} does not exist");
        if (File.Exists(Path.Combine(folder, LanguagePack.ManifestName)))
            throw new LanguagePackException($"{folder} is a pack: put each platform's pack in a subfolder named after it ({string.Join(", ", LanguagePlatform.Known)})");
        var found = new List<(string, string)>();
        foreach (string sub in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(sub);
            if (!LanguagePlatform.Known.Contains(name))
                throw new LanguagePackException($"{sub}: `{name}` is not a platform ({string.Join(", ", LanguagePlatform.Known)})");
            found.Add((name, sub));
        }
        if (found.Count == 0)
            throw new LanguagePackException($"{folder} has no platform folders ({string.Join(", ", LanguagePlatform.Known)})");
        return found;
    }

    /// <summary>A platform's pack, checked as a registry checks it: files, author signature, abi, id, exact version.</summary>
    private static LanguagePack Checked(string folder, string platform)
    {
        var pack = LanguagePack.Read(folder, out var problem) ?? throw new LanguagePackException($"{platform}: {problem}");
        if (pack.Verify() is { } invalid)
            throw new LanguagePackException($"{platform}: the pack cannot be used: {invalid}");
        if (!pack.Signed)
            throw new LanguagePackException($"{platform}: the pack is not signed (--key author.pem signs it)");
        if (Json.Text(pack.Manifest, "id") is not { } id || !LanguageTrust.IsId(id))
            throw new LanguagePackException($"{platform}: `id` must be lowercase letters, digits and -");
        if (Json.Text(pack.Manifest, "version") is not { } version || !LanguageTrust.IsExactVersion(version))
            throw new LanguagePackException($"{platform}: `version` must be exact (1.2.0)");
        return pack;
    }

    private static (string Id, string Version) Agree(List<PlatformPack> packs)
    {
        var first = packs[0].Pack;
        foreach (var (platform, pack) in packs.Skip(1))
        {
            if (pack.Id != first.Id || pack.Version != first.Version)
                throw new LanguagePackException($"{platform} is {pack.Id} {pack.Version}, {packs[0].Platform} is {first.Id} {first.Version}: every platform's pack is the same language and version");
            if (!string.Equals(pack.Fingerprint, first.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new LanguagePackException($"{platform} and {packs[0].Platform} are signed by different author keys");
        }
        return (first.Id, first.Version);
    }

    private static ECDsa LoadKey(string file)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(file));
            return key;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            key.Dispose();
            throw new LanguagePackException($"{file} is not a signing key: {e.Message}");
        }
    }

    private static string Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string path in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
        return to;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".partial", bytes);
        File.Move(path + ".partial", path, overwrite: true);
    }
}
