using Larva.Build;

/// <summary>
/// <c>larva config</c>: this machine's BUILD defaults, which a project gets for each key its <c>.queen</c> does not set
/// (and builds without a project): <c>larva config</c> lists them, <c>larva config borrowcheck</c> shows one,
/// <c>larva config borrowcheck enforce</c> sets it, <c>larva config --unset borrowcheck</c> goes back to the built-in value.
/// A settings file that is not JSON is reported and never written over (its other keys would be lost): <c>--reset</c>
/// starts a new file.
/// </summary>
internal static class ConfigCommand
{
    private const string Usage = "usage: larva config [--reset] [<key> [<value>] | --unset <key>]";

    public static int Run(string[] args)
    {
        bool reset = args.Contains("--reset");
        args = args.Where(a => a != "--reset").ToArray();
        // A file that cannot be read: said, and nothing is written over it (unless --reset).
        if (!LarvaDefaults.ReadFile(out _, out string? problem) && !reset)
        {
            Console.Error.WriteLine($"larva: {problem}");
            Console.Error.WriteLine(args.Length == 2
                ? "larva config: the settings file is not written over (fix it, or `larva config --reset` to start a new one)"
                : "larva config: the built-in defaults apply (fix the file, or `larva config --reset` to start a new one)");
            return 1;
        }
        var configured = reset && problem is not null ? new Dictionary<string, string>(StringComparer.Ordinal) : LarvaDefaults.Configured();
        switch (args)
        {
            case [] when reset:
                LarvaDefaults.Save(configured, reset: true);
                Console.WriteLine($"# {LarvaDefaults.ConfigPath}: a new settings file");
                return 0;
            case []:
            {
                Console.WriteLine($"# {LarvaDefaults.ConfigPath}");
                var effective = LarvaDefaults.Effective();
                foreach (var key in LarvaDefaults.Keys)
                {
                    if (effective.TryGetValue(key, out var value))
                        Console.WriteLine($"{key}: {value}{(configured.ContainsKey(key) ? "" : "   (built in)")}");
                    else
                        Console.WriteLine($"{key}: -   (the language's / larva's own)");
                }
                return 0;
            }
            case ["--unset", var key]:
                if (!LarvaDefaults.Keys.Contains(key))
                    return Fail($"`{key}` is not a setting with a default ({string.Join(", ", LarvaDefaults.Keys)})");
                configured.Remove(key);
                if (!Save(configured, reset))
                    return 1;
                Console.WriteLine($"{key}: {(LarvaDefaults.BuiltIn.TryGetValue(key, out var builtIn) ? builtIn + "   (built in)" : "-")}");
                return 0;
            case [var key]:
                if (!LarvaDefaults.Keys.Contains(key))
                    return Fail($"`{key}` is not a setting with a default ({string.Join(", ", LarvaDefaults.Keys)})");
                Console.WriteLine(LarvaDefaults.Effective().GetValueOrDefault(key, "-"));
                return 0;
            case [var key, var value]:
                if (LarvaDefaults.Problem(key, value) is { } wrong)
                    return Fail(wrong);
                // Trusted registry keys are added to those already trusted (`--unset` forgets them all).
                if (key == LarvaDefaults.LanguagesTrust)
                    value = string.Join(",", configured.GetValueOrDefault(key, "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Concat(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.Ordinal));
                configured[key] = value;
                if (!Save(configured, reset))
                    return 1;
                Console.WriteLine($"{key}: {value}");
                return 0;
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    private static bool Save(IReadOnlyDictionary<string, string> values, bool reset)
    {
        try
        {
            LarvaDefaults.Save(values, reset);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"larva: {e.Message}");
            return false;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"larva config: {message}");
        return 1;
    }
}
