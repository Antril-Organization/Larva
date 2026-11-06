using Larva.Build;

/// <summary>
/// <c>larva config</c>: this machine's BUILD defaults, which a project gets for each key its <c>.queen</c> does not set
/// (and builds without a project): <c>larva config</c> lists them, <c>larva config borrowcheck</c> shows one,
/// <c>larva config borrowcheck enforce</c> sets it, <c>larva config --unset borrowcheck</c> goes back to the built-in value.
/// </summary>
internal static class ConfigCommand
{
    public static int Run(string[] args)
    {
        var configured = LarvaDefaults.Configured();
        switch (args)
        {
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
                LarvaDefaults.Save(configured);
                Console.WriteLine($"{key}: {(LarvaDefaults.BuiltIn.TryGetValue(key, out var builtIn) ? builtIn + "   (built in)" : "-")}");
                return 0;
            case [var key]:
                if (!LarvaDefaults.Keys.Contains(key))
                    return Fail($"`{key}` is not a setting with a default ({string.Join(", ", LarvaDefaults.Keys)})");
                Console.WriteLine(LarvaDefaults.Effective().GetValueOrDefault(key, "-"));
                return 0;
            case [var key, var value]:
                if (LarvaDefaults.Problem(key, value) is { } problem)
                    return Fail(problem);
                // Trusted registry keys are added to those already trusted (`--unset` forgets them all).
                if (key == LarvaDefaults.LanguagesTrust)
                    value = string.Join(",", LarvaDefaults.List(key).Concat(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.Ordinal));
                configured[key] = value;
                LarvaDefaults.Save(configured);
                Console.WriteLine($"{key}: {value}");
                return 0;
            default:
                Console.Error.WriteLine("usage: larva config [<key> [<value>] | --unset <key>]");
                return 2;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"larva config: {message}");
        return 1;
    }
}
