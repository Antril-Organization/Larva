using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Larva.Cli.Tests;

/// <summary><c>larva lsp</c>: the message loop over standard input and output, and what a bad message does to it.</summary>
public sealed class LspTests
{
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
