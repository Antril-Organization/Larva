using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Larva.Build;

namespace Hive
{
    /// <summary>A file a package carries: where it goes in the package, its size and its SHA-256 (hex).</summary>
    public sealed record HiveFile(string Path, long Size, string Sha256);

    /// <summary>A package a package needs: <c>Author/Name</c> and the versions that will do (<c>^1.2</c>, empty: any).</summary>
    public sealed record HiveDependency(string Package, string Version);

    /// <summary>
    /// <c>hive.json</c>, the first entry of a package: what it is, what it needs and every file it carries with its hash.
    /// A package is signed by signing these bytes (<c>hive.sig</c>), so the hashes vouch for every file.
    /// </summary>
    public sealed record HiveManifest
    {
        public const int CurrentFormat = 1;

        public int Format { get; init; } = CurrentFormat;
        public required string Package { get; init; }
        public required string Version { get; init; }
        public string? Description { get; init; }
        public string? License { get; init; }

        /// <summary><c>compiled</c> (Larva objects only) or <c>source</c> (its <c>.ant</c> files too).</summary>
        public string Kind { get; init; } = "compiled";

        /// <summary>The GC mode its Larva objects were checked for: a program in another mode needs a source package.</summary>
        public string? Gc { get; init; }

        public HiveDependency[] Dependencies { get; init; } = [];
        public HiveFile[] Files { get; init; } = [];

        /// <summary>The fingerprint of the key that signed it, or null when it is not signed.</summary>
        public string? Key { get; init; }

        [JsonIgnore]
        public PackageReference Reference => PackageReference.Parse(Package)!;

        [JsonIgnore]
        public uint[] VersionTerms => VersionRange.Parse(Version)!.Version;

        /// <summary>
        /// How Hive's files are written: camelCase, indented, no nulls. Its types' metadata is generated at compile time
        /// (<see cref="HiveJson"/>): larva is compiled ahead of time, with no reflection for JSON.
        /// </summary>
        internal static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = HiveJson.Default,
        };

        public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

        /// <summary>The manifest in <paramref name="bytes"/>, or why it is not one.</summary>
        public static HiveManifest Parse(byte[] bytes)
        {
            HiveManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<HiveManifest>(bytes, Json);
            }
            catch (JsonException e)
            {
                throw new HiveException($"hive.json is not valid: {e.Message}");
            }
            if (manifest is null)
                throw new HiveException("hive.json is empty");
            if (manifest.Format > CurrentFormat)
                throw new HiveException($"the package is in format {manifest.Format}; this Hive reads up to {CurrentFormat}: update Hive");
            if (PackageReference.Parse(manifest.Package) is not { Version: null })
                throw new HiveException($"`{manifest.Package}` is not a package name (Author/Name)");
            if (VersionRange.Parse(manifest.Version) is not { Operator: VersionOperator.Exact })
                throw new HiveException($"`{manifest.Version}` is not a version");
            foreach (var file in manifest.Files)
            {
                if (!HiveArchive.SafePath(file.Path))
                    throw new HiveException($"the package names an unsafe path: `{file.Path}`");
                if (file.Size < 0 || file.Sha256.Length != 64)
                    throw new HiveException($"the package describes `{file.Path}` badly");
            }
            foreach (var dependency in manifest.Dependencies)
            {
                if (PackageReference.Parse(dependency.Package) is not { Version: null } || dependency.Version.Length > 0 && VersionRange.Parse(dependency.Version) is null)
                    throw new HiveException($"the package needs `{dependency.Package}@{dependency.Version}`, which is not a package reference");
            }
            return manifest;
        }

        public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Something Hive could not do, said for the person using it.</summary>
    public sealed class HiveException(string message) : Exception(message);

    /// <summary>The JSON metadata of what Hive reads and writes (manifests, indexes, locks, trusted keys).</summary>
    [JsonSerializable(typeof(HiveManifest))]
    [JsonSerializable(typeof(HiveIndex))]
    [JsonSerializable(typeof(HiveLock))]
    [JsonSerializable(typeof(Dictionary<string, List<string>>))]
    internal sealed partial class HiveJson : JsonSerializerContext;
}
