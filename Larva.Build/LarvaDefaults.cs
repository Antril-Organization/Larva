using System.Text.Json;
using System.Text.Json.Nodes;

namespace Larva.Build
{
    /// <summary>
    /// The BUILD settings this machine uses where a project says nothing (and for builds without a project), kept in a
    /// small JSON file: <c>LARVA_CONFIG</c>, else <c>%APPDATA%/Larva/defaults.json</c> (<c>~/.config/larva/defaults.json</c>
    /// elsewhere). <c>larva config</c> reads and writes it; <c>larva new</c> writes the values into the project it makes.
    /// Without the file, the built-in defaults apply: the borrow checker is off.
    /// </summary>
    public static class LarvaDefaults
    {
        /// <summary>
        /// The keys a default may be given for: BUILD keys (paths and lists stay per project), and <c>hive.provider</c>,
        /// the package registries (comma-separated, tried in order) of projects whose HIVE chunk names none.
        /// </summary>
        public static readonly string[] Keys = ["gc", "backend", "target", "optimization", "cpu", "reflection", "borrowcheck", "unsafe", "generics", "access.default", HiveProvider, LanguagesProvider, LanguagesTrust];

        /// <summary>The registries of projects that name none (<c>larva config hive.provider https://www.antril.org/hive-api</c>).</summary>
        public const string HiveProvider = "hive.provider";

        /// <summary>The languages registry <c>larva languages</c> installs from and publishes to (an https address or a folder).</summary>
        public const string LanguagesProvider = "languages.provider";

        /// <summary>The languages registry used when none is configured.</summary>
        public const string DefaultLanguagesProvider = "https://www.antril.org/languages-api/";

        /// <summary>More registry keys larva trusts to sign languages (base64 SubjectPublicKeyInfo, comma-separated).</summary>
        public const string LanguagesTrust = "languages.trust";

        /// <summary>Whether a key is a BUILD key (not one of Hive's or of the languages registry's).</summary>
        public static bool IsBuildKey(string key) => key is not (HiveProvider or LanguagesProvider or LanguagesTrust);

        /// <summary>The comma-separated values of a key (none configured: empty).</summary>
        public static string[] List(string key) =>
            Configured().GetValueOrDefault(key) is { Length: > 0 } listed
                ? [.. listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
                : [];

        /// <summary>The registries this machine gives projects that name none (none configured: empty).</summary>
        public static string[] HiveProviders() =>
            Configured().GetValueOrDefault(HiveProvider) is { Length: > 0 } listed
                ? [.. listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
                : [];

        /// <summary>What applies when the file does not say: the borrow checker off (`larva config borrowcheck enforce` turns it on).</summary>
        public static readonly IReadOnlyDictionary<string, string> BuiltIn = new Dictionary<string, string>
        {
            ["borrowcheck"] = "off",
            [LanguagesProvider] = DefaultLanguagesProvider,
        };

        /// <summary>What a new project's BUILD chunk says when neither the file nor the built-in defaults name a key.</summary>
        public static readonly IReadOnlyDictionary<string, string> ForNewProjects = new Dictionary<string, string>
        {
            ["gc"] = "default",
            ["backend"] = "llvm",
            ["optimization"] = "3",
            ["borrowcheck"] = "off",
            ["unsafe"] = "allow",
        };

        public static string ConfigPath
        {
            get
            {
                if (Environment.GetEnvironmentVariable("LARVA_CONFIG") is { Length: > 0 } given)
                    return Path.GetFullPath(given);
                string root = OperatingSystem.IsWindows()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                    : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return Path.Combine(root, OperatingSystem.IsWindows() ? "Larva" : "larva", "defaults.json");
            }
        }

        /// <summary>
        /// The values the file sets (none when there is no file, or it cannot be read: a build goes on with the built-in
        /// defaults, and a file that is not JSON is reported once, on standard error).
        /// </summary>
        public static Dictionary<string, string> Configured()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (ReadFile(out var json, out string? problem) && json is not null)
            {
                foreach (var (key, value) in json)
                {
                    if (Keys.Contains(key) && value is JsonValue v && v.TryGetValue(out string? text))
                        values[key] = text;
                }
            }
            else if (problem is not null && !reported)
            {
                reported = true;
                Console.Error.WriteLine($"larva: {problem} (the built-in defaults apply; `larva config --reset` writes a new file)");
            }
            return values;
        }

        private static bool reported;

        /// <summary>
        /// Reads the file as it is: <paramref name="json"/> is its object (null when there is no file), or
        /// <paramref name="problem"/> says why it could not be read (<c>path: error</c>). True when it was read.
        /// </summary>
        public static bool ReadFile(out JsonObject? json, out string? problem)
        {
            json = null;
            problem = null;
            try
            {
                if (!File.Exists(ConfigPath))
                    return true;
                if (JsonNode.Parse(File.ReadAllText(ConfigPath)) is JsonObject read)
                {
                    json = read;
                    return true;
                }
                problem = $"{ConfigPath}: not a JSON object of settings";
                return false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                problem = $"{ConfigPath}: {e.Message}";
                return false;
            }
        }

        /// <summary>The values in effect: the file's, then the built-in ones for what it does not set.</summary>
        public static Dictionary<string, string> Effective()
        {
            var values = Configured();
            foreach (var (key, value) in BuiltIn)
                values.TryAdd(key, value);
            return values;
        }

        /// <summary>
        /// Writes the settings: the file's other keys (a newer larva's, a tool's) are kept as they are, so nothing is lost
        /// by writing one key. A file that cannot be read is not written over (<see cref="InvalidDataException"/> with the
        /// problem) unless <paramref name="reset"/>: then a new file replaces it.
        /// </summary>
        public static void Save(IReadOnlyDictionary<string, string> values, bool reset = false)
        {
            if (!ReadFile(out var json, out string? problem) && !reset)
                throw new InvalidDataException($"{problem}: not written over (fix it, or `larva config --reset` to start a new file)");
            json ??= new JsonObject();
            foreach (var key in Keys)
            {
                if (values.TryGetValue(key, out var value))
                    json[key] = value;
                else
                    json.Remove(key);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }

        /// <summary>Why <paramref name="value"/> is not a value of <paramref name="key"/>, or null when it is.</summary>
        public static string? Problem(string key, string value)
        {
            if (!Keys.Contains(key))
                return $"`{key}` is not a setting with a default ({string.Join(", ", Keys)})";
            if (key == HiveProvider)
                return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 0 && !value.Any(char.IsWhiteSpace)
                    ? null : "`hive.provider` is one or more registries, separated by commas (`https://www.antril.org/hive-api`)";
            if (key == LanguagesProvider)
                return value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains(',')
                    ? null : $"`{LanguagesProvider}` is one registry: an https address (`{DefaultLanguagesProvider}`) or a folder";
            if (key == LanguagesTrust)
            {
                var keys = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return keys.Length > 0 && keys.All(IsPublicKey)
                    ? null : $"`{LanguagesTrust}` is one or more public keys (ECDSA P-256, base64 SubjectPublicKeyInfo), separated by commas";
            }
            // A BUILD key: what a project's BUILD chunk takes (the `project` profile's).
            return Profiles.ProfileCatalog.Get("project") is { } project ? Profiles.QueenValidator.Problem(project, "BUILD", key, value) : null;
        }

        /// <summary>Whether <paramref name="text"/> is an ECDSA P-256 public key (base64 SubjectPublicKeyInfo).</summary>
        public static bool IsPublicKey(string text)
        {
            try
            {
                using var key = System.Security.Cryptography.ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(text), out _);
                return key.KeySize == 256;
            }
            catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException)
            {
                return false;
            }
        }

        /// <summary>The borrow checker's mode for a build without a project.</summary>
        public static Larva.IR.BorrowCheckMode BorrowCheck() => Effective().GetValueOrDefault("borrowcheck") switch
        {
            "enforce" => Larva.IR.BorrowCheckMode.Enforce,
            "warn" => Larva.IR.BorrowCheckMode.Warn,
            _ => Larva.IR.BorrowCheckMode.Off,
        };

        /// <summary>What the frontend of a build without a project is told (as <see cref="QueenProject.FrontendOptions"/>).</summary>
        public static JsonObject FrontendOptions()
        {
            var options = new JsonObject { ["borrows"] = BorrowCheck() != Larva.IR.BorrowCheckMode.Off };
            if (Effective().GetValueOrDefault("access.default") is { } access)
                options["access"] = access;
            if (Effective().GetValueOrDefault("unsafe") == "deny")
                options["unsafe"] = false;
            return options;
        }

        /// <summary>The values as a BUILD chunk's entries, read like a project's (a project's own entries come after and win).</summary>
        public static IReadOnlyList<QueenEntry> Entries(IReadOnlyDictionary<string, string> values)
        {
            var build = values.Where(v => IsBuildKey(v.Key)).ToList();
            if (build.Count == 0)
                return [];
            string text = "queen 1.0;\n\nchunk BUILD:\n" + string.Join(",\n", build.Select(v => $"   {v.Key}: {v.Value}")) + "\n";
            var document = QueenParser.Parse(text, ConfigPath, []);
            return document?.Named("BUILD").SelectMany(c => c.Entries).ToList() ?? [];
        }
    }
}
