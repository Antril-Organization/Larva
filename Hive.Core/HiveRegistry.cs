using System.Text.Json;
using Larva.Build;

namespace Hive
{
    /// <summary>A published version in a registry's index.</summary>
    public sealed record HiveIndexVersion
    {
        public required string Version { get; init; }

        /// <summary>The package file, relative to the package's folder in the registry.</summary>
        public required string File { get; init; }

        /// <summary>The SHA-256 of the package file (hex).</summary>
        public required string Sha256 { get; init; }

        /// <summary>The fingerprint of the key that signed it (null: unsigned).</summary>
        public string? Key { get; init; }

        public HiveDependency[] Dependencies { get; init; } = [];

        /// <summary>Withdrawn by its author: installed only when a lock already names it.</summary>
        public bool Yanked { get; init; }
    }

    /// <summary>
    /// <c>Author/Name/index.json</c> in a registry: every published version, and the public keys (by fingerprint) that sign
    /// them. A registry is only files, so a folder or any static web server can be one.
    /// </summary>
    public sealed record HiveIndex
    {
        public required string Package { get; init; }
        public Dictionary<string, string> Keys { get; init; } = [];
        public List<HiveIndexVersion> Versions { get; init; } = [];

        public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, HiveManifest.Json);

        public static HiveIndex Parse(byte[] bytes, string package)
        {
            try
            {
                return JsonSerializer.Deserialize<HiveIndex>(bytes, HiveManifest.Json) ?? throw new HiveException($"the index of {package} is empty");
            }
            catch (JsonException e)
            {
                throw new HiveException($"the index of {package} is not valid: {e.Message}");
            }
        }
    }

    /// <summary>Where packages are published and fetched from (see <see cref="HiveSettings.Providers"/>).</summary>
    public abstract class HiveProvider
    {
        public abstract string Address { get; }

        /// <summary>A package's index, or null when the registry does not have the package.</summary>
        public abstract HiveIndex? Index(PackageReference package);

        /// <summary>The bytes of a package file named in the index.</summary>
        public abstract byte[] Download(PackageReference package, HiveIndexVersion version);

        /// <summary>Adds a version to the registry (its index updated).</summary>
        public abstract void Publish(PackageReference package, HiveIndexVersion version, byte[] file, string? publicKey);

        /// <summary>
        /// A provider from how a project names it: a folder (relative to <paramref name="baseFolder"/>), <c>file:</c> and a
        /// folder, an <c>https://</c> address, or a host and path (<c>antril.org/hive</c>, read as https).
        /// </summary>
        public static HiveProvider Parse(string text, string baseFolder)
        {
            text = text.Trim();
            if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return new HiveFolderProvider(Path.GetFullPath(Path.Combine(baseFolder, text[5..].TrimStart('/'))));
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return new HiveHttpProvider(new Uri(text.TrimEnd('/') + "/"));
            string local = Path.GetFullPath(Path.Combine(baseFolder, text));
            if (Directory.Exists(local) || text.StartsWith('.') || Path.IsPathRooted(text))
                return new HiveFolderProvider(local);
            return new HiveHttpProvider(new Uri("https://" + text.TrimEnd('/') + "/"));
        }

        protected static string FolderOf(PackageReference package) => $"{package.Author}/{package.Name}";

        /// <summary>
        /// Adds <paramref name="version"/> to an index. A package's first version brings its key; later ones must be signed
        /// by a key the index already has (a new key is only accepted for a package that has none), so a registry cannot be
        /// used to swap a package's signer quietly.
        /// </summary>
        protected static HiveIndex Add(HiveIndex? index, PackageReference package, HiveIndexVersion version, string? publicKey)
        {
            index ??= new HiveIndex { Package = package.Id };
            if (index.Versions.Any(v => v.Version == version.Version))
                throw new HiveException($"{package.Id} {version.Version} is already published: publish a new version");
            if (version.Key is { } fingerprint)
            {
                if (publicKey is null || HiveKeys.Fingerprint(publicKey) != fingerprint)
                    throw new HiveException("the package's key does not match the key it is published with");
                if (!index.Keys.ContainsKey(fingerprint))
                {
                    if (index.Keys.Count > 0)
                        throw new HiveException($"{package.Id} is signed with other keys ({string.Join(", ", index.Keys.Keys.Select(HiveArchive.Short))}): sign it with one of them");
                    index.Keys[fingerprint] = publicKey;
                }
            }
            else if (index.Keys.Count > 0)
            {
                throw new HiveException($"{package.Id} is signed: an unsigned version cannot be added (set `sign` in HIVE)");
            }
            index.Versions.Add(version);
            index.Versions.Sort((a, b) => VersionRange.Compare(VersionRange.Parse(a.Version)!.Version, VersionRange.Parse(b.Version)!.Version));
            return index;
        }
    }

    /// <summary>A registry in a folder: <c>Author/Name/index.json</c> and the package files beside it.</summary>
    public sealed class HiveFolderProvider(string root) : HiveProvider
    {
        public override string Address => root;

        public override HiveIndex? Index(PackageReference package)
        {
            string path = Path.Combine(root, FolderOf(package), "index.json");
            return File.Exists(path) ? HiveIndex.Parse(File.ReadAllBytes(path), package.Id) : null;
        }

        public override byte[] Download(PackageReference package, HiveIndexVersion version)
        {
            if (!HiveArchive.SafePath(version.File))
                throw new HiveException($"the index of {package.Id} names an unsafe file: `{version.File}`");
            string path = Path.Combine(root, FolderOf(package), version.File);
            return File.Exists(path) ? File.ReadAllBytes(path) : throw new HiveException($"the registry lists {package.Id} {version.Version} but does not have its file");
        }

        public override void Publish(PackageReference package, HiveIndexVersion version, byte[] file, string? publicKey)
        {
            string folder = Path.Combine(root, FolderOf(package));
            var index = Add(Index(package), package, version, publicKey);
            string target = Path.Combine(folder, version.File);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file);
            string indexPath = Path.Combine(folder, "index.json");
            File.WriteAllBytes(indexPath + ".partial", index.ToBytes());
            File.Move(indexPath + ".partial", indexPath, overwrite: true);
        }
    }

    /// <summary>
    /// A registry on the web: the same files over HTTPS (plain HTTP only to this machine). Publishing sends the package with
    /// <c>PUT Author/Name/version</c> and the token in <c>HIVE_TOKEN</c>; the registry checks it and updates the index.
    /// </summary>
    public sealed class HiveHttpProvider : HiveProvider
    {
        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(2) };
        private readonly Uri root;

        public HiveHttpProvider(Uri root)
        {
            if (root.Scheme != Uri.UriSchemeHttps && !root.IsLoopback)
                throw new HiveException($"`{root}`: registries are reached over https (plain http only on this machine)");
            this.root = root;
        }

        public override string Address => root.ToString();

        public override HiveIndex? Index(PackageReference package)
        {
            using var response = Get(new Uri(root, $"{FolderOf(package)}/index.json"));
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            return HiveIndex.Parse(Body(response, 16 * 1024 * 1024), package.Id);
        }

        public override byte[] Download(PackageReference package, HiveIndexVersion version)
        {
            if (!HiveArchive.SafePath(version.File))
                throw new HiveException($"the index of {package.Id} names an unsafe file: `{version.File}`");
            using var response = Get(new Uri(root, $"{FolderOf(package)}/{version.File}"));
            return Body(response, HiveArchive.MaxUnpacked);
        }

        public override void Publish(PackageReference package, HiveIndexVersion version, byte[] file, string? publicKey)
        {
            string token = Environment.GetEnvironmentVariable("HIVE_TOKEN")
                ?? throw new HiveException($"publishing to {root} needs your token in HIVE_TOKEN");
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(root, $"{FolderOf(package)}/{version.Version}"))
            {
                Content = new ByteArrayContent(file),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (publicKey is not null)
                request.Headers.Add("Hive-Public-Key", publicKey);
            request.Headers.Add("Hive-Sha256", version.Sha256);
            using var response = Send(request);
            if (!response.IsSuccessStatusCode)
                throw new HiveException($"{root} refused the package: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        private static HttpResponseMessage Get(Uri address) => Send(new HttpRequestMessage(HttpMethod.Get, address));

        private static HttpResponseMessage Send(HttpRequestMessage request)
        {
            try
            {
                var response = Client.Send(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                    throw new HiveException($"{request.RequestUri}: {(int)response.StatusCode} {response.ReasonPhrase}");
                return response;
            }
            catch (HttpRequestException e)
            {
                throw new HiveException($"cannot reach {request.RequestUri?.Host}: {e.Message}");
            }
            catch (TaskCanceledException)
            {
                throw new HiveException($"{request.RequestUri?.Host} did not answer in time");
            }
        }

        private static byte[] Body(HttpResponseMessage response, long limit)
        {
            if (response.Content.Headers.ContentLength > limit)
                throw new HiveException("the registry sent more than a package can be");
            using var input = response.Content.ReadAsStream();
            var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                if (output.Length + read > limit)
                    throw new HiveException("the registry sent more than a package can be");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }
}
