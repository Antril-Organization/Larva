using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Larva.Cli.Tests;

/// <summary>
/// <c>larva languages</c> with a registry: publishing (packs signed per platform, one multipart request), approving into a
/// folder registry, then search, install, use, update and remove from it, and every way a pack is refused.
/// </summary>
public sealed class LanguagesRegistryTests
{
    [Fact]
    public void Publish_PacksAndSignsEachPlatform_AndSendsOneMultipartRequest()
    {
        var world = new LanguageWorld("languages-publish");
        string publication = world.Publication("0.8.0", LanguageWorld.Platform, "linux-x64");
        var requests = new List<(string Method, string Path, string? Authorization, string? ContentType, byte[] Body)>();
        using var server = new HttpListener();
        int port = FreePort();
        string root = $"http://127.0.0.1:{port}/languages-api/";
        server.Prefixes.Add(root);
        server.Start();
        _ = Task.Run(() =>
        {
            while (server.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = server.GetContext();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                var body = new MemoryStream();
                context.Request.InputStream.CopyTo(body);
                lock (requests)
                    requests.Add((context.Request.HttpMethod, context.Request.Url!.AbsolutePath, context.Request.Headers["Authorization"], context.Request.ContentType, body.ToArray()));
                var (status, answer) = (context.Request.HttpMethod, context.Request.Url.AbsolutePath) switch
                {
                    ("POST", "/languages-api/requests") when requests.Count(r => r.Method == "POST") > 1 => (409, "antril 0.8.0 is already pending"),
                    ("POST", "/languages-api/requests") => (202, """{"id":"req-42","status":"pending"}"""),
                    ("GET", "/languages-api/requests/req-42") => (200, """{"id":"req-42","language":"antril","version":"0.8.0","platforms":["linux-x64","win-x64"],"status":"rejected","reason":"needs a license","submitted":"2026-11-05T10:00:00Z","reviewed":"2026-11-05T11:00:00Z"}"""),
                    _ => (404, "no such request"),
                };
                context.Response.StatusCode = status;
                if (status >= 400)
                    context.Response.StatusDescription = answer;
                byte[] bytes = Encoding.UTF8.GetBytes(answer);
                context.Response.ContentType = status >= 400 ? "text/plain" : "application/json";
                context.Response.OutputStream.Write(bytes);
                context.Response.Close();
            }
        }, TestContext.Current.CancellationToken);

        // Without a token, nothing is sent.
        Assert.Contains("HIVE_TOKEN", world.Larva.Fails("languages", "publish", publication, "--key", world.AuthorKey, "--provider", root));
        // A folder registry takes no requests.
        Assert.Contains("approve", world.Larva.Fails("languages", "publish", publication, "--key", world.AuthorKey, "--provider", world.Registry));

        world.Larva.Token = "hive_test-token";
        string output = world.Larva.Ok("languages", "publish", publication, "--key", world.AuthorKey, "--provider", root);
        Assert.Contains("request req-42: pending", output);

        var post = Assert.Single(requests, r => r.Method == "POST");
        Assert.Equal("/languages-api/requests", post.Path);
        Assert.Equal("Bearer hive_test-token", post.Authorization);
        Assert.StartsWith("multipart/form-data", post.ContentType);
        var parts = Multipart(post.Body, post.ContentType!);
        Assert.Equal(["linux-x64", LanguageWorld.Platform], parts.Keys.Order(StringComparer.Ordinal).ToArray());
        string? fingerprint = null;
        foreach (var (platform, (fileName, zip)) in parts)
        {
            Assert.Equal($"antril-0.8.0-{platform}.zip", fileName);
            using var archive = new ZipArchive(new MemoryStream(zip));
            Assert.Contains(archive.Entries, e => e.FullName == "Antril.dll");
            using var reader = new StreamReader(archive.GetEntry("language.json")!.Open());
            var manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            Assert.Null(manifest["registry"]);
            Assert.NotNull(manifest["files"]?["Antril.dll"]);
            string publicKey = (string)manifest["publicKey"]!;
            Assert.True(LanguageWorld.Verify(LanguageWorld.Canonical(manifest, "signature", "registry"), (string)manifest["signature"]!, publicKey),
                $"{platform}: the author's signature covers the canonical manifest");
            fingerprint ??= (string)manifest["fingerprint"]!;
            Assert.Equal(fingerprint, (string)manifest["fingerprint"]!);
        }

        // The registry's refusal is shown with its reason.
        Assert.Contains("409 antril 0.8.0 is already pending", world.Larva.Fails("languages", "publish", publication, "--key", world.AuthorKey, "--provider", root));

        string status = world.Larva.Ok("languages", "status", "req-42", "--provider", root);
        Assert.Contains("request req-42: rejected", status);
        Assert.Contains("needs a license", status);
        Assert.Contains("not-there", world.Larva.Fails("languages", "status", "not-there", "--provider", root));
        server.Stop();
    }

    [Fact]
    public void Approve_Search_Install_Use_Update_Remove()
    {
        var world = new LanguageWorld("languages-install", trustRegistry: false);
        world.Approve("0.8.0");
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(world.Registry, "index.json")))!;
        Assert.Equal("0.8.0", (string?)index["languages"]![0]!["latest"]);
        var language = JsonNode.Parse(File.ReadAllText(Path.Combine(world.Registry, "antril", "index.json")))!;
        var pack = language["versions"]![0]!["packs"]![0]!;
        Assert.Equal(LanguageWorld.Platform, (string?)pack["platform"]);
        Assert.Equal($"antril-0.8.0-{LanguageWorld.Platform}.zip", (string?)pack["file"]);

        // The registry's signature covers the manifest with the author's signature.
        using (var archive = ZipFile.OpenRead(world.Zip("0.8.0")))
        {
            using var reader = new StreamReader(archive.GetEntry("language.json")!.Open());
            var manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            Assert.True(LanguageWorld.Verify(LanguageWorld.Canonical(manifest, "registry"), (string)manifest["registry"]!["signature"]!, world.RegistryPublicKey));
        }

        Assert.Contains("antril 0.8.0", world.Larva.Ok("languages", "search", "--provider", world.Registry));
        Assert.Contains("no language matches", world.Larva.Ok("languages", "search", "cobol", "--provider", world.Registry));

        // Not trusted yet; then trusted through `larva config`.
        Assert.Contains("does not trust", world.Larva.Fails("languages", "install", "antril", "--provider", world.Registry));
        world.Larva.Ok("config", "languages.trust", world.RegistryPublicKey);
        world.Larva.Ok("config", "languages.provider", "file:" + world.Registry);
        Assert.Contains("installed Antril 0.8.0", world.Larva.Ok("languages", "install", "antril@0.8.0"));
        Assert.Contains("already installed", world.Larva.Ok("languages", "install", "antril"));
        Assert.True(File.Exists(Path.Combine(world.User, "trust.json")));

        string listed = world.Larva.Ok("languages");
        Assert.Contains("Antril 0.8.0", listed);
        Assert.Contains("installed from a registry", listed);
        Assert.Contains("ok (files verified)", listed);

        // Used from the user's folder (LARVA_LANGUAGES is not set).
        File.WriteAllText(Path.Combine(world.Root, "Main.ant"), "Main() : i32 => 0;\n");
        world.Larva.Ok("build", "--check", "Main.ant");

        world.Approve("0.9.0");
        Assert.Contains("(was 0.8.0)", world.Larva.Ok("languages", "update"));
        Assert.Contains("up to date", world.Larva.Ok("languages", "update", "antril"));
        Assert.Contains("Antril 0.9.0", world.Larva.Ok("languages"));
        Assert.False(Directory.EnumerateDirectories(Path.Combine(world.User, ".staging")).Any(), "no staging folder is left");

        Assert.Contains("removed antril", world.Larva.Ok("languages", "remove", "antril"));
        Assert.False(Directory.Exists(Path.Combine(world.User, "antril")));
        Assert.True(Directory.Exists(world.Registry));
        Assert.Contains("not installed", world.Larva.Fails("languages", "remove", "antril"));
        Assert.Contains("no installed language reads .ant", world.Larva.Fails("build", "--check", "Main.ant"));
    }

    [Fact]
    public void Install_RefusesPacksWithoutATrustedRegistrySignature()
    {
        var world = new LanguageWorld("languages-registry-signature");
        string other = world.NewKey("other-registry.pem", out _);
        world.Approve("0.8.0", registryKey: other);
        Assert.Contains("which larva does not trust", world.Larva.Fails("languages", "install", "antril", "--provider", world.Registry));

        world.Approve("0.9.0");
        world.ChangeZip("0.9.0", folder =>
        {
            var manifest = LanguageWorld.Manifest(folder);
            manifest.Remove("registry");
            LanguageWorld.WriteManifest(folder, manifest);
        });
        Assert.Contains("no registry signed it", world.Larva.Fails("languages", "install", "antril@0.9.0", "--provider", world.Registry));
        Assert.False(Directory.Exists(Path.Combine(world.User, "antril")));
    }

    [Fact]
    public void Update_RefusesAnotherAuthorKey()
    {
        var world = new LanguageWorld("languages-author-pin");
        world.Approve("0.8.0");
        world.Larva.Ok("languages", "install", "antril", "--provider", world.Registry);

        // Another registry (trusted) offers a newer version signed by someone else.
        string elsewhere = Path.Combine(world.Root, "registry-2");
        world.Approve("0.9.0", authorKey: world.NewKey("impostor.pem", out _), registry: elsewhere);
        Assert.Contains("not by the key antril was first installed with", world.Larva.Fails("languages", "update", "antril", "--provider", elsewhere));
        Assert.Equal("0.8.0", (string?)LanguageWorld.Manifest(Path.Combine(world.User, "antril"))["version"]);

        // The folder registry also refuses a new author key for a language it has.
        string publication = world.Publication("0.9.1");
        LanguageWorld.Sign(Path.Combine(publication, LanguageWorld.Platform), Path.Combine(world.Root, "impostor.pem"));
        Assert.Contains("pinned key", world.Larva.Fails("languages", "approve", publication, "--registry-key", world.RegistryKey, "--into", world.Registry));

        // An installed pack whose pin does not match is not loaded.
        string trust = Path.Combine(world.User, "trust.json");
        var pins = JsonNode.Parse(File.ReadAllText(trust))!;
        pins["authors"]!["antril"]!["fingerprint"] = new string('0', 64);
        File.WriteAllText(trust, pins.ToJsonString());
        Assert.Contains("not by the key pinned", world.Larva.Fails("languages"));
    }

    [Fact]
    public void Install_RefusesTamperedPacks()
    {
        var world = new LanguageWorld("languages-tampered");
        world.Approve("0.8.0");

        // A file changed after signing (the zip's hash listed again, so only the pack can tell).
        world.ChangeZip("0.8.0", folder => File.AppendAllText(Path.Combine(folder, "ant-file.ico"), "x"));
        Assert.Contains("is not the file its manifest lists", world.Larva.Fails("languages", "install", "antril", "--provider", world.Registry));

        // The zip is not the one the index lists.
        world.Approve("0.9.0");
        string indexPath = Path.Combine(world.Registry, "antril", "index.json");
        File.WriteAllText(indexPath, File.ReadAllText(indexPath).Replace(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(world.Zip("0.9.0")))), new string('a', 64)));
        Assert.Contains("is not the file the registry's index lists", world.Larva.Fails("languages", "install", "antril@0.9.0", "--provider", world.Registry));

        // A path out of the pack.
        world.Approve("0.9.1");
        var buffer = new MemoryStream();
        using (var original = ZipFile.OpenRead(world.Zip("0.9.1")))
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in original.Entries)
            {
                using var output = zip.CreateEntry(entry.FullName).Open();
                using var input = entry.Open();
                input.CopyTo(output);
            }
            using var evil = new StreamWriter(zip.CreateEntry("../escaped.txt").Open());
            evil.Write("outside");
        }
        world.ReplaceZip("0.9.1", buffer.ToArray());
        Assert.Contains("unsafe path", world.Larva.Fails("languages", "install", "antril@0.9.1", "--provider", world.Registry));
        Assert.False(File.Exists(Path.Combine(world.User, ".staging", "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(world.User, "escaped.txt")));
        Assert.False(Directory.Exists(Path.Combine(world.User, "antril")));

        // An installed pack changed afterwards is not loaded.
        world.Approve("1.0.0");
        world.Larva.Ok("languages", "install", "antril", "--provider", world.Registry);
        string installed = Path.Combine(world.User, "antril");
        var manifest = LanguageWorld.Manifest(installed);
        manifest["description"] = "changed";
        LanguageWorld.WriteManifest(installed, manifest);
        Assert.Contains("NOT USABLE", world.Larva.Fails("languages"));
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The parts of a multipart/form-data body: name → (file name, bytes).</summary>
    private static Dictionary<string, (string? FileName, byte[] Bytes)> Multipart(byte[] body, string contentType)
    {
        string boundary = contentType.Split("boundary=")[1].Trim('"');
        byte[] delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var parts = new Dictionary<string, (string?, byte[])>();
        int at = IndexOf(body, delimiter, 0);
        while (at >= 0)
        {
            int start = at + delimiter.Length;
            if (body[start] == '-' && body[start + 1] == '-')
                break;
            start += 2; // CRLF
            int headersEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), start);
            string headers = Encoding.UTF8.GetString(body, start, headersEnd - start);
            int next = IndexOf(body, delimiter, headersEnd + 4);
            byte[] content = body[(headersEnd + 4)..(next - 2)];
            string disposition = headers.Split("\r\n").First(h => h.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase));
            string? Field(string name) => disposition.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..].Trim('"');
            Assert.Contains("application/zip", headers);
            parts[Field("name")!] = (Field("filename"), content);
            at = next;
        }
        return parts;
    }

    private static int IndexOf(byte[] data, byte[] pattern, int from)
    {
        for (int i = from; i <= data.Length - pattern.Length; i++)
        {
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                return i;
        }
        return -1;
    }
}
