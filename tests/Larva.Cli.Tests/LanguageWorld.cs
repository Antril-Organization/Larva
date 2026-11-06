using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Larva.Cli.Tests;

/// <summary>
/// One test's world: a temporary folder with larva's settings, a user language folder, a folder registry, an author key and
/// a registry key (trusted through <c>languages.trust</c>), and copies of a real language pack to publish (the Antril dev
/// pack, used as data and never changed).
/// </summary>
internal sealed class LanguageWorld
{
    public static string SourcePack { get; } = Path.GetFullPath(Path.Combine(LarvaProcess.Repository, "..", "Antril", "artifacts", "pack", "Antril"));

    public static string Platform
    {
        get
        {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            return $"{os}-{(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64")}";
        }
    }

    public string Root { get; }
    public string Registry { get; }
    public string User { get; }
    public string AuthorKey { get; }
    public string RegistryKey { get; }
    public string RegistryPublicKey { get; }
    public LarvaProcess Larva { get; }

    public LanguageWorld(string name, bool trustRegistry = true)
    {
        Assert.True(File.Exists(Path.Combine(SourcePack, "language.json")), $"the Antril dev pack is built: {SourcePack}");
        Root = TestEnvironment.TempFolder(name);
        Registry = Path.Combine(Root, "registry");
        User = Path.Combine(Root, "user-languages");
        AuthorKey = NewKey("author.pem", out _);
        RegistryKey = NewKey("registry.pem", out string registryPublic);
        RegistryPublicKey = registryPublic;
        string config = Path.Combine(Root, "defaults.json");
        if (trustRegistry)
            File.WriteAllText(config, new JsonObject { ["languages.trust"] = RegistryPublicKey }.ToJsonString());
        Larva = new LarvaProcess { WorkingDirectory = Root, Config = config, UserLanguages = User };
    }

    /// <summary>A new ECDSA P-256 key in a PEM file of the world; its path (and its public key).</summary>
    public string NewKey(string file, out string publicKey)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = Path.Combine(Root, file);
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return path;
    }

    /// <summary>A publication folder: one subfolder per platform, each a copy of the pack at <paramref name="version"/>.</summary>
    public string Publication(string version, params string[] platforms)
    {
        string folder = Path.Combine(Root, "publish-" + version + "-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (string platform in platforms.Length == 0 ? [Platform] : platforms)
        {
            string pack = Path.Combine(folder, platform);
            Directory.CreateDirectory(pack);
            foreach (string file in Directory.EnumerateFiles(SourcePack))
                File.Copy(file, Path.Combine(pack, Path.GetFileName(file)));
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(pack, "language.json")))!.AsObject();
            manifest["version"] = version;
            File.WriteAllText(Path.Combine(pack, "language.json"), manifest.ToJsonString());
        }
        return folder;
    }

    /// <summary>A version packed and signed by <paramref name="authorKey"/>, approved by <paramref name="registryKey"/> into <paramref name="registry"/>.</summary>
    public void Approve(string version, string? authorKey = null, string? registryKey = null, string? registry = null)
    {
        string publication = Publication(version);
        Sign(Path.Combine(publication, Platform), authorKey ?? AuthorKey);
        Larva.Ok("languages", "approve", publication, "--registry-key", registryKey ?? RegistryKey, "--into", registry ?? Registry);
    }

    /// <summary>
    /// What <c>larva languages pack --key</c> does, without a process or loading the frontend (the publish test runs the
    /// real one): every file's SHA-256 in <c>files</c>, then the author's key and signature over the canonical manifest.
    /// </summary>
    public static void Sign(string pack, string keyFile)
    {
        var manifest = Manifest(pack);
        var files = new JsonObject();
        foreach (string path in Directory.EnumerateFiles(pack).Order(StringComparer.Ordinal).Where(p => Path.GetFileName(p) != "language.json"))
            files[Path.GetFileName(path)] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        manifest["files"] = files;
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyFile));
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        manifest["publicKey"] = Convert.ToBase64String(publicKey);
        manifest["fingerprint"] = Convert.ToHexStringLower(SHA256.HashData(publicKey));
        manifest["signature"] = Convert.ToBase64String(key.SignData(Canonical(manifest, "signature", "registry"), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        WriteManifest(pack, manifest);
    }

    public string Zip(string version, string? registry = null) =>
        Path.Combine(registry ?? Registry, "antril", version, $"antril-{version}-{Platform}.zip");

    /// <summary>Replaces a published zip and makes the registry's index list the new bytes (so only what is inside is wrong).</summary>
    public void ReplaceZip(string version, byte[] zip)
    {
        File.WriteAllBytes(Zip(version), zip);
        string indexPath = Path.Combine(Registry, "antril", "index.json");
        var index = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
        foreach (var entry in index["versions"]!.AsArray().Where(v => (string?)v!["version"] == version).SelectMany(v => v!["packs"]!.AsArray()))
        {
            entry!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(zip));
            entry["size"] = zip.Length;
        }
        File.WriteAllText(indexPath, index.ToJsonString());
    }

    /// <summary>A published zip changed: unpacked, <paramref name="change"/>d, packed again (the index updated).</summary>
    public void ChangeZip(string version, Action<string> change)
    {
        string folder = Path.Combine(Root, "unpacked-" + Guid.NewGuid().ToString("N")[..6]);
        ZipFile.ExtractToDirectory(Zip(version), folder);
        change(folder);
        string zip = folder + ".zip";
        ZipFile.CreateFromDirectory(folder, zip);
        ReplaceZip(version, File.ReadAllBytes(zip));
    }

    public static JsonObject Manifest(string folder) => JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "language.json")))!.AsObject();

    public static void WriteManifest(string folder, JsonObject manifest) => File.WriteAllText(Path.Combine(folder, "language.json"), manifest.ToJsonString());

    /// <summary>
    /// The bytes a signature covers: the manifest without the top-level <paramref name="excluded"/> keys, every object's keys
    /// in ordinal order, no spaces, UTF-8 as <see cref="Utf8JsonWriter"/> writes it by default.
    /// </summary>
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
                    foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal).Where(p => !excluded.Contains(p.Key)))
                    {
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

    public static bool Verify(byte[] data, string signature, string publicKey)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return key.VerifyData(data, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}
