using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Larva.Cli.Tests;

/// <summary>
/// <c>larva lsp</c> as a process over its standard streams: with an installed language, the background borrow check of a
/// project that enforces it reaches the editor; and what a bad message does to the loop (logged and skipped).
/// </summary>
public sealed class LspTests
{
    [Fact]
    public void BorrowCheck_PublishesTheProjectsBorrowErrors()
    {
        var world = new LanguageWorld("lsp-borrows");
        world.Approve("0.8.0");
        world.Larva.Ok("languages", "install", "antril", "--provider", world.Registry);

        // A project that enforces the borrow checker, and a source whose only error is a borrow error: the analysis
        // finds nothing, the borrow check (compiled, linked with System.Core and System.Bindings, lowered) finds it.
        string app = Path.Combine(world.Root, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.queen"), "queen 1.0;\nchunk PROJ:\n   name: App\nchunk BUILD:\n   backend: llvm,\n   borrowcheck: enforce,\n   output: out/App.exe\n");
        string source = Path.Combine(app, "Program.ant");
        const string program = "import System;\nvalue Counter { public: i32 n; }\nBoth(&mut Counter a, &mut Counter b) { a.n = 1; b.n = 2; }\nMain()\n{\n    Counter x = new Counter();\n    Both(&mut x, &mut x);\n    Console.WriteLine(x.n);\n}\n";
        File.WriteAllText(source, program);

        using var server = new Server(world.Larva);
        server.Request("initialize", new JsonObject { ["processId"] = null, ["rootUri"] = Uri(app), ["capabilities"] = new JsonObject() });
        server.Notify("initialized", new JsonObject());
        server.Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(source), ["languageId"] = "antril", ["version"] = 1, ["text"] = program },
        });
        // The analysis publishes first (no error), the borrow check a moment later.
        var analysis = server.Diagnostics(Uri(source));
        Assert.True(analysis is { Count: 0 }, "the analysis finds no error: " + analysis?.ToJsonString());
        var borrows = server.Diagnostics(Uri(source));
        Assert.True(borrows is { Count: 1 }, "the borrow check publishes its error: " + borrows?.ToJsonString() + "\n" + server.Log);
        var error = borrows![0]!;
        Assert.Equal("ConflictingBorrow", (string?)error["code"]);
        Assert.Equal("borrow checker", (string?)error["source"]);
        Assert.Equal(1, (int?)error["severity"]);
        Assert.Equal(6, (int?)error["range"]!["start"]!["line"]);
        Assert.DoesNotContain("borrow check:", server.Log);
        server.Request("shutdown", null);
        server.Notify("exit", null);
    }

    /// <summary>
    /// A burst of typing (20 changes sent at once) then a completion: the changes a newer one replaces are never analyzed,
    /// so the completion is answered at once, about the last text.
    /// </summary>
    [Fact]
    public void A_burst_of_changes_does_not_hold_up_completion()
    {
        var world = new LanguageWorld("lsp-burst");
        world.Approve("0.8.0");
        world.Larva.Ok("languages", "install", "antril", "--provider", world.Registry);
        string app = Path.Combine(world.Root, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.queen"), "queen 1.0;\nchunk PROJ:\n   name: App\nchunk BUILD:\n   output: out/App.exe\n");
        string source = Path.Combine(app, "Program.ant");
        // Big enough that analyzing every change in turn takes seconds.
        string body = string.Concat(Enumerable.Range(0, 400).Select(j =>
            $"Work{j}(i32 a) : i32\n{{\n    i32 s = 0;\n    loop (i32 k = 0..a) {{ s += k * {j}; }}\n    Console.WriteLine(s);\n    return s;\n}}\n"));
        string Text(int version) => $"import System;\nHelper{version}() {{ }}\nMain()\n{{\n    Hel\n}}\n" + body;
        File.WriteAllText(source, Text(0));

        using var server = new Server(world.Larva);
        server.Request("initialize", new JsonObject { ["processId"] = null, ["rootUri"] = Uri(app), ["capabilities"] = new JsonObject() });
        server.Notify("initialized", new JsonObject());
        server.Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(source), ["languageId"] = "antril", ["version"] = 0, ["text"] = Text(0) },
        });
        Assert.NotNull(server.Diagnostics(Uri(source)));

        var clock = Stopwatch.StartNew();
        for (int version = 1; version <= 20; version++)
        {
            server.Notify("textDocument/didChange", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = Uri(source), ["version"] = version },
                ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = Text(version) }),
            });
        }
        var completion = server.Request("textDocument/completion", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(source) },
            ["position"] = new JsonObject { ["line"] = 4, ["character"] = 7 },
        });
        clock.Stop();
        var items = (completion as JsonObject)?["items"] as JsonArray ?? completion as JsonArray ?? [];
        var helpers = items.Select(i => (string?)i!["label"]).Where(l => l?.StartsWith("Helper", StringComparison.Ordinal) == true).ToList();
        Assert.True(helpers.SequenceEqual(["Helper20"]), "the completion is about the last text: " + string.Join(", ", helpers) + "\n" + server.Log);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"the completion is answered in {clock.Elapsed.TotalSeconds:F1} s");
        // The document's diagnostics are published once more (for the last text), not once per change.
        Assert.NotNull(server.Diagnostics(Uri(source)));
        server.Request("shutdown", null);
        server.Notify("exit", null);
    }

    private static string Uri(string path) => "file:///" + path.Replace('\\', '/').Replace(":", "%3A");

    /// <summary>The language server of this working copy, in the world of a <see cref="LarvaProcess"/>, driven over its standard streams.</summary>
    private sealed class Server : IDisposable
    {
        private readonly Process process;
        private readonly Stream input, output;
        private readonly StringBuilder log = new();
        private readonly System.Collections.Concurrent.BlockingCollection<JsonObject> inbox = [];
        private readonly List<JsonObject> pending = [];
        private int nextId = 1;

        public Server(LarvaProcess larva)
        {
            Assert.True(File.Exists(LarvaProcess.Dll), $"the larva command line is built: {LarvaProcess.Dll}");
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = larva.WorkingDirectory,
            };
            start.ArgumentList.Add(LarvaProcess.Dll);
            start.ArgumentList.Add("lsp");
            start.Environment["LARVA_CONFIG"] = larva.Config;
            start.Environment["LARVA_USER_LANGUAGES"] = larva.UserLanguages;
            start.Environment.Remove("LARVA_LANGUAGES");
            process = Process.Start(start)!;
            process.ErrorDataReceived += (_, e) =>
            {
                lock (log)
                    log.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();
            input = process.StandardInput.BaseStream;
            output = process.StandardOutput.BaseStream;
            new Thread(() =>
            {
                try
                {
                    while (Read() is { } message)
                        inbox.Add(message);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
                inbox.CompleteAdding();
            }) { IsBackground = true }.Start();
        }

        /// <summary>What the server wrote to its standard error.</summary>
        public string Log
        {
            get
            {
                lock (log)
                    return log.ToString();
            }
        }

        public JsonNode? Request(string method, JsonNode? parameters)
        {
            int id = nextId++;
            Send(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters?.DeepClone() });
            while (Receive() is { } message)
            {
                if (message["id"] is { } got && got.GetValue<int>() == id && message["method"] is null)
                    return message["result"];
                pending.Add(message);
            }
            return null;
        }

        public void Notify(string method, JsonNode? parameters) =>
            Send(new JsonObject { ["method"] = method, ["params"] = parameters?.DeepClone() });

        /// <summary>The next diagnostics published for <paramref name="uri"/> (waiting for them; null when none come).</summary>
        public JsonArray? Diagnostics(string uri)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                if (IsDiagnostics(pending[i], uri))
                {
                    var found = pending[i]["params"]!["diagnostics"] as JsonArray;
                    pending.RemoveAt(i);
                    return found;
                }
            }
            while (Receive() is { } message)
            {
                if (IsDiagnostics(message, uri))
                    return message["params"]!["diagnostics"] as JsonArray;
                pending.Add(message);
            }
            return null;
        }

        private static bool IsDiagnostics(JsonObject message, string uri) =>
            message["method"]?.GetValue<string>() == "textDocument/publishDiagnostics"
            && string.Equals(message["params"]?["uri"]?.GetValue<string>(), uri, StringComparison.OrdinalIgnoreCase);

        private JsonObject? Receive()
        {
            try
            {
                return inbox.TryTake(out var message, TimeSpan.FromSeconds(60)) ? message : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private void Send(JsonObject message)
        {
            message["jsonrpc"] = "2.0";
            byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            try
            {
                input.Write(header);
                input.Write(body);
                input.Flush();
            }
            catch (IOException)
            {
                // The server has ended: what was asked gets no answer, and its check fails.
            }
        }

        private JsonObject? Read()
        {
            var header = new StringBuilder();
            int length = -1;
            while (true)
            {
                int b = output.ReadByte();
                if (b < 0)
                    return null;
                header.Append((char)b);
                if (header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    foreach (string line in header.ToString().Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = int.Parse(line["Content-Length:".Length..].Trim());
                    }
                    break;
                }
            }
            if (length < 0)
                return null;
            var body = new byte[length];
            for (int read = 0; read < length;)
            {
                int n = output.Read(body, read, length - read);
                if (n <= 0)
                    return null;
                read += n;
            }
            return JsonNode.Parse(body) as JsonObject;
        }

        public void Dispose()
        {
            try
            {
                input.Close();
            }
            catch (IOException)
            {
            }
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
            process.Dispose();
        }
    }

    // ── The message loop over bad input ──

    /// <summary>Runs <c>larva lsp</c> over <paramref name="input"/> (raw bytes: headers and bodies); the messages it sent, its exit code and standard error.</summary>
    private static (List<JsonObject> Messages, int Code, string Errors) Serve(byte[] input)
    {
        string root = TestEnvironment.TempFolder("lsp");
        Assert.True(File.Exists(LarvaProcess.Dll), $"the larva command line is built: {LarvaProcess.Dll}");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        start.ArgumentList.Add(LarvaProcess.Dll);
        start.ArgumentList.Add("lsp");
        start.Environment["LARVA_CONFIG"] = Path.Combine(root, "defaults.json");
        start.Environment["LARVA_USER_LANGUAGES"] = Path.Combine(root, "user-languages");
        start.Environment.Remove("LARVA_LANGUAGES");
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = new MemoryStream();
        var copying = process.StandardOutput.BaseStream.CopyToAsync(output);
        process.StandardInput.BaseStream.Write(input);
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(60_000), "larva lsp ends with its input");
        copying.Wait();
        return (Parse(output.ToArray()), process.ExitCode, errors.Result);
    }

    private static byte[] Message(string body) => Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");

    private static List<JsonObject> Parse(byte[] bytes)
    {
        var messages = new List<JsonObject>();
        int at = 0;
        while (at < bytes.Length)
        {
            int headerEnd = Encoding.ASCII.GetString(bytes, at, bytes.Length - at).IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.True(headerEnd >= 0, "every message has a header");
            string header = Encoding.ASCII.GetString(bytes, at, headerEnd);
            int length = int.Parse(header["Content-Length:".Length..].Trim());
            at += headerEnd + 4;
            messages.Add(JsonNode.Parse(bytes.AsSpan(at, length).ToArray())!.AsObject());
            at += length;
        }
        return messages;
    }

    [Fact]
    public void Bad_messages_are_logged_and_skipped_and_the_loop_goes_on()
    {
        var input = new MemoryStream();
        // A header that is not a number, then a body that is not JSON, then a JSON array, then a request without a
        // method, then an unknown method, then the normal end.
        input.Write(Encoding.ASCII.GetBytes("Content-Length: lots\r\n\r\n"));
        input.Write(Message("{ this is not json"));
        input.Write(Message("[1, 2, 3]"));
        input.Write(Message("""{"jsonrpc":"2.0","id":7}"""));
        input.Write(Message("""{"jsonrpc":"2.0","id":8,"method":"larva/nothing","params":{}}"""));
        input.Write(Message("""{"jsonrpc":"2.0","method":"larva/nothingNotified","params":{}}"""));
        input.Write(Message("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}"""));
        input.Write(Message("""{"jsonrpc":"2.0","method":"initialized","params":{}}"""));
        input.Write(Message("""{"jsonrpc":"2.0","id":2,"method":"shutdown"}"""));
        input.Write(Message("""{"jsonrpc":"2.0","method":"exit"}"""));

        var (messages, code, errors) = Serve(input.ToArray());
        Assert.Equal(0, code);
        // Each bad message is logged to the editor and on standard error.
        var logs = messages.Where(m => (string?)m["method"] == "window/logMessage").Select(m => (string)m["params"]!["message"]!).ToList();
        Assert.Contains(logs, l => l.Contains("bad header"));
        Assert.Contains(logs, l => l.Contains("not JSON"));
        Assert.Contains(logs, l => l.Contains("not a JSON object"));
        Assert.Contains("larva lsp: bad header", errors);
        // A request without a method, an unknown method: error responses, not a crash.
        Assert.Equal(-32600, (int)messages.Single(m => (int?)m["id"] == 7)["error"]!["code"]!);
        Assert.Equal(-32601, (int)messages.Single(m => (int?)m["id"] == 8)["error"]!["code"]!);
        // The loop went on: initialize and shutdown were answered.
        Assert.NotNull(messages.Single(m => (int?)m["id"] == 1)["result"]!["capabilities"]);
        Assert.Contains(messages, m => (int?)m["id"] == 2);
    }
}
