using System.Globalization;
using System.Text;

namespace Larva.Build.Profiles
{
    /// <summary>
    /// Reads a profile's <c>template.tqueen</c>: one declaration a line (no commas between them, so a key's type and its
    /// modifiers read as written).
    /// <code>
    /// tqueen 1.0;
    /// profile project: "A Larva project"
    /// open chunks
    /// /// The project: its name, kind and version.
    /// chunk PROJ required:
    ///    /// The project's name.
    ///    name: TEXT = $folder
    ///    type: [exe, library, tool] = exe
    ///    command: REGEX("^[a-z][a-z0-9-]*$")  only when type == tool
    /// rules:
    ///    error when BUILD.target == wasm and BUILD.backend == x64: "the x64 back end does not make WebAssembly"
    /// </code>
    /// Problems are <c>TQ…</c> diagnostics at their line and column; what can be read is kept.
    /// </summary>
    public sealed class ProfileParser
    {
        public const string SupportedVersion = "1.0";

        /// <summary>The type keywords (completion and the meta-schema).</summary>
        public static readonly string[] TypeKeywords = ["TEXT", "IDENTIFIER", "NAME", "INT", "NUMBER", "BOOL", "VERSION", "PATH", "RPATH", "APATH", "FILE", "FOLDER", "GLOB", "URL", "REGEX", "LIST"];

        /// <summary>The words a key line takes after its type.</summary>
        public static readonly string[] Modifiers = ["required", "many", "only", "when", "deprecated", "example"];

        private readonly string file;
        private readonly List<QueenDiagnostic> diagnostics;
        private readonly Func<string, QueenProfile?> resolve;

        private ProfileParser(string file, List<QueenDiagnostic> diagnostics, Func<string, QueenProfile?> resolve)
        {
            this.file = file;
            this.diagnostics = diagnostics;
            this.resolve = resolve;
        }

        /// <summary>
        /// The profile <paramref name="text"/> declares (null when its header is not readable). <paramref name="resolve"/>
        /// finds the profile an <c>extends</c> names.
        /// </summary>
        public static QueenProfile? Parse(string text, string file, string name, List<QueenDiagnostic> diagnostics, Func<string, QueenProfile?>? resolve = null) =>
            new ProfileParser(file, diagnostics, resolve ?? (_ => null)).Profile(text, name);

        private void Error(string code, string message, int line, int column) =>
            diagnostics.Add(new QueenDiagnostic(code, message, file, line, column));

        private QueenProfile? Profile(string text, string folderName)
        {
            var lines = text.Split('\n');
            int at = 0;
            // The header: `tqueen 1.0;`.
            while (at < lines.Length && Tokens(lines[at], at + 1).Count == 0)
                at++;
            var header = at < lines.Length ? Tokens(lines[at], at + 1) : [];
            if (header is not [{ Text: "tqueen" }, { Text: var version } versionToken, ..])
            {
                Error("TQ001", "a .tqueen file starts with `tqueen <version>;` (as in `tqueen 1.0;`)", at + 1, 1);
                return null;
            }
            if (version != SupportedVersion)
                Error("TQ002", $"this larva reads `tqueen {SupportedVersion}`, not `tqueen {version}`", versionToken.Line, versionToken.Column);
            at++;

            string description = "";
            QueenProfile? baseProfile = null;
            bool open = false, declared = false;
            var chunks = new List<ChunkSpec>();
            var rules = new List<RuleSpec>();
            ChunkSpec? chunk = null;
            bool inRules = false;
            var doc = new StringBuilder();
            for (; at < lines.Length; at++)
            {
                int lineNumber = at + 1;
                string line = lines[at].TrimEnd('\r');
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    doc.Append(doc.Length > 0 ? " " : "").Append(trimmed[3..].Trim());
                    continue;
                }
                var tokens = Tokens(line, lineNumber);
                if (tokens.Count == 0)
                    continue;
                string pending = doc.ToString();
                doc.Clear();
                var first = tokens[0];
                switch (first.Text)
                {
                    case "profile" when !declared:
                    {
                        // `profile NAME [extends BASE]: "description"`.
                        declared = true;
                        int i = 1;
                        if (i < tokens.Count && tokens[i].IsWord)
                        {
                            if (tokens[i].Text != folderName)
                                Error("TQ003", $"the profile is named after its folder: `profile {folderName}`, not `{tokens[i].Text}`", tokens[i].Line, tokens[i].Column);
                            i++;
                        }
                        if (i + 1 < tokens.Count && tokens[i].Text == "extends")
                        {
                            var named = tokens[i + 1];
                            baseProfile = named.Text == folderName ? null : resolve(named.Text);
                            if (baseProfile is null)
                                Error("TQ004", $"no profile `{named.Text}` to extend", named.Line, named.Column);
                            i += 2;
                        }
                        if (i < tokens.Count && tokens[i].Text == ":")
                            i++;
                        if (i < tokens.Count && tokens[i].IsString)
                            description = tokens[i].Text;
                        continue;
                    }
                    case "open" when tokens is [_, { Text: "chunks" }]:
                        open = true;
                        continue;
                    case "chunk":
                    {
                        inRules = false;
                        chunk = Chunk(tokens, pending, lineNumber);
                        if (chunk is not null)
                        {
                            if (chunks.Any(c => c.Name == chunk.Name))
                                Error("TQ005", $"chunk `{chunk.Name}` is declared twice", first.Line, first.Column);
                            chunks.Add(chunk);
                        }
                        continue;
                    }
                    case "rules" when tokens is [_, { Text: ":" }]:
                        inRules = true;
                        chunk = null;
                        continue;
                }
                if (inRules)
                {
                    if (Rule(tokens, lineNumber) is { } rule)
                        rules.Add(rule);
                    continue;
                }
                if (chunk is null)
                {
                    Error("TQ006", $"expected `profile`, `chunk NAME:` or `rules:`, found `{first.Text}`", first.Line, first.Column);
                    continue;
                }
                if (Key(tokens, pending, lineNumber) is { } key)
                {
                    if (chunk.Key(key.Name) is not null)
                        Error("TQ007", $"`{key.Name}` is declared twice in chunk `{chunk.Name}`", first.Line, first.Column);
                    else
                        chunk.Keys.Add(key);
                }
            }
            if (!declared)
                Error("TQ008", $"a profile says what it is: `profile {folderName}: \"…\"`", 1, 1);
            var profile = new QueenProfile { Name = folderName, Description = description, File = file, Base = baseProfile, OpenChunks = open };
            profile.OwnChunks.AddRange(chunks);
            profile.OwnRules.AddRange(rules);
            // Conditions name keys that exist (of the key's own chunk, or `CHUNK.key`).
            foreach (var spec in chunks)
            {
                foreach (var key in spec.Keys)
                {
                    CheckKeys(profile, spec, key.OnlyWhen, key.Line);
                    CheckKeys(profile, spec, key.RequiredWhen, key.Line);
                }
            }
            foreach (var rule in rules)
                CheckKeys(profile, null, rule.When, rule.Line);
            return profile;
        }

        private void CheckKeys(QueenProfile profile, ChunkSpec? own, Condition? condition, int line)
        {
            foreach (var key in condition?.Keys ?? [])
            {
                var chunk = key.Chunk is { } named ? profile.Chunk(named) : own;
                if (chunk is null)
                    Error("TQ009", key.Chunk is null ? $"`{key.Key}` in a rule names its chunk: `CHUNK.{key.Key}`" : $"no chunk `{key.Chunk}`", line, 1);
                else if (chunk.Key(key.Key) is null && !chunk.Open)
                    Error("TQ009", $"chunk `{chunk.Name}` has no key `{key.Key}`", line, 1);
            }
        }

        // ── chunk NAME [required] [open [TYPE]]: ──

        private ChunkSpec? Chunk(List<Token> tokens, string doc, int line)
        {
            if (tokens.Count < 2 || !tokens[1].IsWord || !IsChunkName(tokens[1].Text))
            {
                Error("TQ010", "a chunk is `chunk NAME:` (upper-case letters, digits and `_`)", line, tokens[0].Column);
                return null;
            }
            bool required = false, open = false;
            ValueType other = new PathType(PathKind.Any);
            int i = 2;
            for (; i < tokens.Count && tokens[i].Text != ":"; i++)
            {
                switch (tokens[i].Text)
                {
                    case "required":
                        required = true;
                        break;
                    case "open":
                        open = true;
                        // `open TYPE`: what its other keys take.
                        if (i + 1 < tokens.Count && tokens[i + 1].Text != ":")
                        {
                            int at = i + 1;
                            if (Type(tokens, ref at) is { } type)
                                other = type;
                            i = at - 1;
                        }
                        break;
                    default:
                        Error("TQ011", $"a chunk takes `required` and `open`, not `{tokens[i].Text}`", tokens[i].Line, tokens[i].Column);
                        break;
                }
            }
            if (i >= tokens.Count)
                Error("TQ012", $"expected `:` after `chunk {tokens[1].Text}`", line, tokens[^1].Column);
            return new ChunkSpec { Name = tokens[1].Text, Doc = doc, Required = required, Open = open, OtherKeys = other, Line = line };
        }

        private static bool IsChunkName(string name) =>
            name.Length > 0 && char.IsAsciiLetterUpper(name[0]) && name.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');

        // ── key: TYPE [modifiers] ──

        private KeySpec? Key(List<Token> tokens, string doc, int line)
        {
            if (tokens is not [{ IsWord: true } name, { Text: ":" }, ..])
            {
                Error("TQ013", "a key is `name: TYPE` (and its modifiers)", line, tokens[0].Column);
                return null;
            }
            int i = 2;
            if (i >= tokens.Count)
            {
                Error("TQ014", $"`{name.Text}` has a type (`TEXT`, `[a, b]`, `INT(0..3)`…)", line, tokens[1].Column + 1);
                return null;
            }
            var type = Type(tokens, ref i);
            if (type is null)
                return null;
            string? value = null, deprecated = null, example = null;
            bool required = false, many = false;
            Condition? onlyWhen = null, requiredWhen = null;
            while (i < tokens.Count)
            {
                var token = tokens[i];
                switch (token.Text)
                {
                    case "=" when i + 1 < tokens.Count:
                        value = tokens[i + 1].Text;
                        i += 2;
                        break;
                    case "required" when i + 1 < tokens.Count && tokens[i + 1].Text == "when":
                        i += 2;
                        requiredWhen = Condition(tokens, ref i);
                        break;
                    case "required":
                        required = true;
                        i++;
                        break;
                    case "many":
                        many = true;
                        i++;
                        break;
                    case "only" when i + 1 < tokens.Count && tokens[i + 1].Text == "when":
                        i += 2;
                        onlyWhen = Condition(tokens, ref i);
                        break;
                    case "deprecated" or "example" when i + 1 < tokens.Count && (tokens[i + 1].IsString || tokens[i + 1].IsWord):
                        if (token.Text == "deprecated")
                            deprecated = tokens[i + 1].Text;
                        else
                            example = tokens[i + 1].Text;
                        i += 2;
                        break;
                    default:
                        Error("TQ015", $"after the type: `= value`, `required`, `many`, `only when …`, `required when …`, `deprecated \"…\"` or `example \"…\"`, not `{token.Text}`", token.Line, token.Column);
                        return new KeySpec { Name = name.Text, Type = type, Doc = doc, Default = value, Required = required, Many = many, OnlyWhen = onlyWhen, RequiredWhen = requiredWhen, Deprecated = deprecated, Example = example, Line = line };
                }
            }
            if (value is not null)
            {
                // A default is a value of the key's type (`$folder` and the other variables are filled in later).
                var parsed = QueenParser.Classify(value, line, 1);
                if (!value.StartsWith('$') && !type.Accepts(parsed))
                    Error("TQ016", $"the default of `{name.Text}` is {type.Describe()}, not `{value}`", line, tokens[1].Column);
            }
            return new KeySpec { Name = name.Text, Type = type, Doc = doc, Default = value, Required = required, Many = many, OnlyWhen = onlyWhen, RequiredWhen = requiredWhen, Deprecated = deprecated, Example = example, Line = line };
        }

        /// <summary><c>A | B | …</c>, each a keyword (with its arguments) or <c>[a, b]</c>.</summary>
        private ValueType? Type(List<Token> tokens, ref int i)
        {
            var alternatives = new List<ValueType>();
            while (true)
            {
                if (One(tokens, ref i) is not { } one)
                    return null;
                alternatives.Add(one);
                if (i < tokens.Count && tokens[i].Text == "|")
                {
                    i++;
                    continue;
                }
                break;
            }
            return alternatives.Count == 1 ? alternatives[0] : new UnionType([.. alternatives]);
        }

        private ValueType? One(List<Token> tokens, ref int i)
        {
            if (i >= tokens.Count)
            {
                Error("TQ017", "a type is missing", tokens[^1].Line, tokens[^1].Column + 1);
                return null;
            }
            var token = tokens[i];
            if (token.Text == "[")
            {
                var options = new List<string>();
                for (i++; i < tokens.Count && tokens[i].Text != "]"; i++)
                {
                    if (tokens[i].Text != ",")
                        options.Add(tokens[i].Text);
                }
                if (i >= tokens.Count)
                {
                    Error("TQ018", "a list of values closes with `]`", token.Line, token.Column);
                    return new EnumType([.. options]);
                }
                i++;
                return new EnumType([.. options]);
            }
            i++;
            List<Token>? arguments = null;
            if (i < tokens.Count && tokens[i].Text == "(")
            {
                arguments = [];
                int depth = 1;
                for (i++; i < tokens.Count; i++)
                {
                    if (tokens[i].Text == "(")
                        depth++;
                    else if (tokens[i].Text == ")" && --depth == 0)
                        break;
                    arguments.Add(tokens[i]);
                }
                if (i >= tokens.Count)
                    Error("TQ019", $"`{token.Text}(` closes with `)`", token.Line, token.Column);
                else
                    i++;
            }
            switch (token.Text)
            {
                case "TEXT": return new TextType();
                case "IDENTIFIER": return new IdentifierType();
                case "NAME": return new NameType();
                case "NUMBER": return new NumberType();
                case "BOOL": return new BoolType();
                case "VERSION": return new VersionType();
                case "PATH": return new PathType(PathKind.Any);
                case "RPATH": return new PathType(PathKind.Relative);
                case "APATH": return new PathType(PathKind.Absolute);
                case "FOLDER": return new FolderType();
                case "GLOB": return new GlobType();
                case "URL": return new UrlType();
                case "INT":
                {
                    if (arguments is null or [])
                        return new IntType(null, null);
                    // `0..3`, `1..`, `..10`.
                    string range = string.Concat(arguments.Select(a => a.Text));
                    int dots = range.IndexOf("..", StringComparison.Ordinal);
                    long? Bound(string s) => long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n) ? n : null;
                    if (dots < 0 || dots > 0 && Bound(range[..dots]) is null || dots + 2 < range.Length && Bound(range[(dots + 2)..]) is null)
                    {
                        Error("TQ020", $"`INT` takes a range: `INT(0..3)`, not `INT({range})`", token.Line, token.Column);
                        return new IntType(null, null);
                    }
                    return new IntType(Bound(range[..dots]), Bound(range[(dots + 2)..]));
                }
                case "FILE":
                {
                    if (arguments is [{ Text: "profile" }, { IsWord: true } profile])
                        return new FileType([], profile.Text);
                    if (arguments is not null && arguments.Any(a => !a.Text.StartsWith('.')))
                        Error("TQ021", "`FILE` takes extensions (`FILE(.dll .lva)`) or a profile (`FILE(profile project)`)", token.Line, token.Column);
                    return new FileType([.. (arguments ?? []).Select(a => a.Text).Where(a => a.StartsWith('.'))], null);
                }
                case "REGEX":
                {
                    if (arguments is not [{ IsString: true } pattern])
                    {
                        Error("TQ022", "`REGEX` takes a pattern in quotes: `REGEX(\"^[a-z]+$\")`", token.Line, token.Column);
                        return new TextType();
                    }
                    try
                    {
                        return new RegexType(pattern.Text);
                    }
                    catch (ArgumentException e)
                    {
                        Error("TQ022", $"`{pattern.Text}` is not a pattern: {e.Message}", pattern.Line, pattern.Column);
                        return new TextType();
                    }
                }
                case "LIST":
                {
                    int at = 0;
                    if (arguments is null or [] || Type(arguments, ref at) is not { } item)
                    {
                        Error("TQ023", "`LIST` takes the type of its items: `LIST(GLOB)`", token.Line, token.Column);
                        return new ListType(new TextType());
                    }
                    return new ListType(item);
                }
                default:
                    Error("TQ024", $"`{token.Text}` is not a type ({string.Join(", ", TypeKeywords)}, or `[a, b]`)", token.Line, token.Column);
                    return null;
            }
        }

        // ── conditions ──

        /// <summary>Up to the end of the line, a `:` (a rule's message follows) or the next modifier.</summary>
        private Condition? Condition(List<Token> tokens, ref int i)
        {
            int start = i;
            int end = i;
            while (end < tokens.Count && tokens[end].Text != ":" && !(tokens[end].Text is "required" or "many" or "deprecated" or "example" or "=" || tokens[end].Text == "only" && end + 1 < tokens.Count && tokens[end + 1].Text == "when"))
                end++;
            var part = tokens.GetRange(start, end - start);
            i = end;
            if (part.Count == 0)
            {
                var at = start < tokens.Count ? tokens[start] : tokens[^1];
                Error("TQ025", "`when` is followed by a condition (`type == tool`)", at.Line, at.Column);
                return null;
            }
            int position = 0;
            var condition = Or(part, ref position);
            if (condition is not null && position < part.Count)
            {
                Error("TQ026", $"unexpected `{part[position].Text}` in the condition", part[position].Line, part[position].Column);
                return null;
            }
            return condition;
        }

        private Condition? Or(List<Token> t, ref int i)
        {
            var left = And(t, ref i);
            while (left is not null && i < t.Count && t[i].Text == "or")
            {
                i++;
                if (And(t, ref i) is not { } right)
                    return null;
                left = new OrCondition(left, right);
            }
            return left;
        }

        private Condition? And(List<Token> t, ref int i)
        {
            var left = Not(t, ref i);
            while (left is not null && i < t.Count && t[i].Text == "and")
            {
                i++;
                if (Not(t, ref i) is not { } right)
                    return null;
                left = new AndCondition(left, right);
            }
            return left;
        }

        private Condition? Not(List<Token> t, ref int i)
        {
            if (i < t.Count && t[i].Text == "not")
            {
                i++;
                return Not(t, ref i) is { } inner ? new NotCondition(inner) : null;
            }
            return Primary(t, ref i);
        }

        private Condition? Primary(List<Token> t, ref int i)
        {
            if (i >= t.Count)
            {
                Error("TQ025", "the condition ends too soon", t[^1].Line, t[^1].Column);
                return null;
            }
            if (t[i].Text == "(")
            {
                i++;
                var inner = Or(t, ref i);
                if (i < t.Count && t[i].Text == ")")
                    i++;
                else
                    Error("TQ026", "a `(` in a condition closes with `)`", t[Math.Min(i, t.Count - 1)].Line, t[Math.Min(i, t.Count - 1)].Column);
                return inner;
            }
            if (t[i].Text == "exists" && i + 3 < t.Count && t[i + 1].Text == "(" && t[i + 3].Text == ")")
            {
                var reference = Reference(t[i + 2].Text);
                i += 4;
                return new ExistsCondition(reference);
            }
            if (!t[i].IsWord || i + 1 >= t.Count)
            {
                Error("TQ026", $"a condition compares a key: `key == value`, `key != value`, `key in [a, b]`, `exists(key)`, not `{t[i].Text}`", t[i].Line, t[i].Column);
                return null;
            }
            var key = Reference(t[i].Text);
            string op = t[i + 1].Text;
            i += 2;
            if (op is "==" or "!=" && i < t.Count)
                return new CompareCondition(key, op == "==", t[i++].Text);
            if (op == "in" && i < t.Count && t[i].Text == "[")
            {
                var values = new List<string>();
                for (i++; i < t.Count && t[i].Text != "]"; i++)
                {
                    if (t[i].Text != ",")
                        values.Add(t[i].Text);
                }
                i++;
                return new InCondition(key, [.. values]);
            }
            Error("TQ026", $"a condition compares with `==`, `!=` or `in [...]`, not `{op}`", t[i - 1].Line, t[i - 1].Column);
            return null;
        }

        /// <summary><c>gc</c>, <c>gc.collector</c> (a key of the same chunk) or <c>BUILD.gc</c> (an upper-case chunk first).</summary>
        internal static KeyRef Reference(string text)
        {
            int dot = text.IndexOf('.');
            return dot > 0 && IsChunkName(text[..dot]) ? new KeyRef(text[..dot], text[(dot + 1)..]) : new KeyRef(null, text);
        }

        // ── rules ──

        private RuleSpec? Rule(List<Token> tokens, int line)
        {
            // `error|warning [CODE] when COND: "message"`.
            if (tokens[0].Text is not ("error" or "warning"))
            {
                Error("TQ027", "a rule is `error when COND: \"message\"` or `warning when …`", line, tokens[0].Column);
                return null;
            }
            int i = 1;
            string code = "QN142";
            if (i < tokens.Count && tokens[i].Text != "when")
                code = tokens[i++].Text;
            if (i >= tokens.Count || tokens[i].Text != "when")
            {
                Error("TQ027", "a rule says `when` it applies", line, tokens[Math.Min(i, tokens.Count - 1)].Column);
                return null;
            }
            i++;
            if (Condition(tokens, ref i) is not { } condition)
                return null;
            if (i + 1 >= tokens.Count || tokens[i].Text != ":" || !tokens[i + 1].IsString)
            {
                Error("TQ028", "a rule ends with its message: `: \"…\"`", line, tokens[Math.Min(i, tokens.Count - 1)].Column);
                return null;
            }
            return new RuleSpec(tokens[0].Text == "error", code, condition, tokens[i + 1].Text, line);
        }

        // ── tokens ──

        /// <summary>A word, a string (its text without quotes) or a symbol, at its 1-based line and column.</summary>
        internal readonly record struct Token(string Text, int Line, int Column, bool IsString)
        {
            public bool IsWord => !IsString && Text.Length > 0 && (char.IsLetterOrDigit(Text[0]) || Text[0] is '_' or '$' or '.' or '-' or '^');
        }

        internal static List<Token> Tokens(string line, int lineNumber)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }
                // A comment (`//` at the start or after a space).
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                    break;
                int start = i;
                if (c == '"')
                {
                    var text = new StringBuilder();
                    for (i++; i < line.Length && line[i] != '"'; i++)
                    {
                        if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] is '"' or '\\')
                            i++;
                        text.Append(line[i]);
                    }
                    i++;
                    tokens.Add(new Token(text.ToString(), lineNumber, start + 1, true));
                    continue;
                }
                if (c is '=' or '!' && i + 1 < line.Length && line[i + 1] == '=')
                {
                    tokens.Add(new Token(line.Substring(i, 2), lineNumber, start + 1, false));
                    i += 2;
                    continue;
                }
                if (c is '[' or ']' or '(' or ')' or ',' or '|' or ':' or '=' or ';')
                {
                    tokens.Add(new Token(c.ToString(), lineNumber, start + 1, false));
                    i++;
                    continue;
                }
                while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] is not ('[' or ']' or '(' or ')' or ',' or '|' or ':' or ';' or '"')
                       && !(line[i] is '=' or '!' && i + 1 < line.Length && line[i + 1] == '=') && !(line[i] == '=' && i > start))
                    i++;
                if (i == start)
                    i++;
                tokens.Add(new Token(line[start..i], lineNumber, start + 1, false));
            }
            return tokens;
        }
    }
}
