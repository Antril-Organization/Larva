using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Larva.Hir.Encoding;

/// <summary>
/// <c>larva languages</c>: the language packs installed, each checked (what it needs, its files, its signatures);
/// <c>pack &lt;folder&gt; [--key key.pem]</c> completes a pack's manifest (its files' SHA-256, the format it writes) and
/// signs it; <c>key &lt;key.pem&gt;</c> makes a signing key; <c>search</c>, <c>install</c>, <c>update</c> and <c>remove</c>
/// manage languages from a languages registry (<see cref="LanguageInstaller"/>); <c>publish</c> and <c>status</c> send and
/// follow a publication request, and <c>approve</c> (administrators) publishes into a folder registry (<see cref="LanguagePublisher"/>).
/// </summary>
internal static class LanguagesCommand
{
    public const string Usage = """
               larva languages                                    the language packs installed, checked
               larva languages search [<text>] [--provider <url>]  the languages a registry has
               larva languages install <id>[@<version>] [--provider <url>]   a language from a registry, verified, for this platform
               larva languages update [<id>] [--provider <url>]   the installed languages (or one) at their newest version
               larva languages remove <id> [--forget-key]          an installed language (--forget-key: its pinned author key too)
               larva languages publish <folder> [--key <author.pem>] [--provider <url>]   a publication request: one subfolder per
                                                                  platform (win-x64, linux-x64, osx-arm64...), each packed and signed
               larva languages status <request-id> [--provider <url>]   where a publication request stands
               larva languages pack <folder> [--key <key.pem>]     a pack's manifest completed (files, SHA-256) and signed
               larva languages key <key.pem>                      a signing key (ECDSA P-256)
               larva languages approve <request-folder|pack-folder> --registry-key <registry.pem> --into <folder-registry> [--platform <p>]
                                                                  (administrators) signs a request as the registry and publishes it
                                                                  into a folder registry
           registry: --provider, else `larva config languages.provider` (https://www.antril.org/languages-api/ built in), an
           https address or a folder; publishing needs HIVE_TOKEN. Languages install to LARVA_USER_LANGUAGES, else
           %LOCALAPPDATA%/Larva/Languages.
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "list" or "verify")
            return List();
        var rest = args.Skip(1).ToList();
        string? Option(string name)
        {
            int at = rest.IndexOf(name);
            if (at < 0 || at + 1 >= rest.Count)
                return null;
            string value = rest[at + 1];
            rest.RemoveRange(at, 2);
            return value;
        }
        bool Flag(string name) => rest.Remove(name);

        try
        {
            switch (args[0])
            {
                case "key" when rest.Count == 1:
                    return Key(rest[0]);
                case "pack" when rest.Count >= 1:
                {
                    string? key = Option("--key");
                    if (rest.Count != 1)
                        break;
                    return Pack(rest[0], key);
                }
                case "search":
                {
                    string? provider = Option("--provider");
                    if (rest.Count > 1)
                        break;
                    return LanguageInstaller.Search(rest.FirstOrDefault(), LanguageSource.For(provider));
                }
                case "install":
                {
                    string? provider = Option("--provider");
                    if (rest.Count != 1)
                        break;
                    return LanguageInstaller.Install(rest[0], LanguageSource.For(provider));
                }
                case "update":
                {
                    string? provider = Option("--provider");
                    if (rest.Count > 1)
                        break;
                    return LanguageInstaller.Update(rest.FirstOrDefault(), LanguageSource.For(provider));
                }
                case "remove":
                {
                    bool forget = Flag("--forget-key");
                    if (rest.Count != 1)
                        break;
                    return LanguageInstaller.Remove(rest[0], forget);
                }
                case "publish":
                {
                    string? key = Option("--key");
                    string? provider = Option("--provider");
                    if (rest.Count != 1)
                        break;
                    return LanguagePublisher.Publish(rest[0], key, LanguageSource.For(provider));
                }
                case "status":
                {
                    string? provider = Option("--provider");
                    if (rest.Count != 1)
                        break;
                    return LanguagePublisher.Status(rest[0], LanguageSource.For(provider));
                }
                case "approve":
                {
                    string? key = Option("--registry-key");
                    string? into = Option("--into");
                    string? platform = Option("--platform");
                    if (rest.Count != 1 || key is null || into is null)
                        break;
                    return LanguagePublisher.Approve(rest[0], key, into, platform);
                }
            }
        }
        catch (LanguagePackException e)
        {
            Console.Error.WriteLine($"larva languages: {e.Message}");
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or UriFormatException)
        {
            Console.Error.WriteLine($"larva languages: {e.Message}");
            return 1;
        }
        Console.Error.WriteLine("usage:\n" + Usage);
        return 2;
    }

    private static int List()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        foreach (string problem in LanguageRegistry.Problems)
            Console.Error.WriteLine($"larva: {problem}");
        if (LanguageRegistry.All.Count == 0)
        {
            Console.WriteLine($"no language pack installed (looked in {string.Join(", ", LanguageRegistry.Folders)})");
            return 1;
        }
        int failed = 0;
        foreach (var pack in LanguageRegistry.All)
        {
            string? problem = pack.Verify();
            Console.WriteLine($"{pack.Name} {pack.Version}  ({pack.Id}: {string.Join(" ", pack.Extensions)})");
            if (pack.Description is { } description)
                Console.WriteLine($"  {description}");
            if (pack.Author is { } author)
                Console.WriteLine($"  author:    {author}");
            if (pack.License is { } license)
                Console.WriteLine($"  license:   {license}");
            if (pack.Homepage is { } homepage)
                Console.WriteLine($"  homepage:  {homepage}");
            Console.WriteLine($"  folder:    {pack.Folder}");
            Console.WriteLine($"  signature: {(pack.Signed ? $"signed, key {pack.Fingerprint?[..Math.Min(16, pack.Fingerprint.Length)]}" : "unsigned")}");
            if (pack.FromRegistry || pack.Manifest["registry"] is not null)
                Console.WriteLine($"  registry:  {(pack.Manifest["registry"] is { } registry ? $"signed, key {LanguageTrust.Short(Json.Text(registry, "fingerprint"))}" : "unsigned")}{(pack.FromRegistry ? " (installed from a registry)" : "")}");
            Console.WriteLine($"  status:    {(problem is null ? "ok (files verified)" : "NOT USABLE: " + problem)}");
            if (problem is not null)
                failed++;
        }
        return failed == 0 ? 0 : 1;
    }

    private static int Key(string path)
    {
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"larva languages: {path} exists (a key is not written over)");
            return 1;
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        Console.WriteLine($"{path}: a signing key (fingerprint {fingerprint}); keep it private");
        Console.WriteLine($"public key: {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}");
        return 0;
    }

    /// <summary>
    /// The pack's manifest completed: <c>files</c> (every file of the folder but the manifest, with its SHA-256), <c>hir</c>
    /// (the format its frontend says it writes) and, with a key, <c>publicKey</c>, <c>fingerprint</c> and <c>signature</c>.
    /// </summary>
    private static int Pack(string folder, string? keyFile)
    {
        ECDsa? key = null;
        try
        {
            if (keyFile is not null)
            {
                key = ECDsa.Create();
                try
                {
                    key.ImportFromPem(File.ReadAllText(keyFile));
                }
                catch (Exception e) when (e is CryptographicException or ArgumentException or IOException)
                {
                    Console.Error.WriteLine($"larva languages: {keyFile} is not a signing key: {e.Message}");
                    return 1;
                }
            }
            Console.WriteLine($"{Path.GetFullPath(folder)}: {PackFolder(folder, key, ask: true)}");
            return 0;
        }
        catch (LanguagePackException e)
        {
            Console.Error.WriteLine($"larva languages: {e.Message}");
            return 1;
        }
        finally
        {
            key?.Dispose();
        }
    }

    /// <summary>
    /// Completes and (with a key) signs a pack's manifest, written back to its folder; what it did. <paramref name="ask"/>:
    /// the frontend is loaded and asked the format it writes (only a pack for this platform can be).
    /// </summary>
    public static string PackFolder(string folder, ECDsa? key, bool ask)
    {
        folder = Path.GetFullPath(folder);
        if (LanguagePack.Read(folder, out var problem) is not { } pack)
            throw new LanguagePackException(problem!);
        var manifest = pack.Manifest;
        foreach (string field in new[] { "publicKey", "fingerprint", "signature", "registry" })
            manifest.Remove(field);
        var files = new JsonObject();
        foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            if (relative != LanguagePack.ManifestName)
                files[relative] = LanguagePack.Hash(path);
        }
        manifest["files"] = files;
        manifest["abi"] ??= LanguagePack.Abi;
        if (ask || manifest["hir"] is null)
            manifest["hir"] = LvaFormat.Version;
        // The frontend answers: what it says it writes is what the manifest says.
        if (ask)
        {
            var loaded = LanguagePack.Read(folder, out _)!;
            foreach (var (name, value) in manifest)
                loaded.Manifest[name] = value?.DeepClone();
            if (loaded.Info["hir"]?.GetValue<int>() is { } hir)
                manifest["hir"] = hir;
        }
        if (key is not null)
        {
            byte[] publicKey = key.ExportSubjectPublicKeyInfo();
            manifest["publicKey"] = Convert.ToBase64String(publicKey);
            manifest["fingerprint"] = Convert.ToHexStringLower(SHA256.HashData(publicKey));
            manifest["signature"] = Convert.ToBase64String(key.SignData(LanguagePack.Canonical(manifest), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        }
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            manifest.WriteTo(writer);
        File.WriteAllText(Path.Combine(folder, LanguagePack.ManifestName), Encoding.UTF8.GetString(buffer.ToArray()) + "\n");
        return $"{pack.Name} {pack.Version}, {files.Count} file(s){(key is null ? ", unsigned" : $", signed (key {manifest["fingerprint"]!.GetValue<string>()[..16]})")}";
    }
}
