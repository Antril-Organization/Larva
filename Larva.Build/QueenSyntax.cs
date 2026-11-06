using System.Globalization;
using System.Text;

namespace Larva.Build
{
    /// <summary>A problem in a <c>.queen</c> file, at a 1-based line and column.</summary>
    public sealed record QueenDiagnostic(string Code, string Message, string File, int Line, int Column, bool IsError = true)
    {
        public override string ToString() => $"{File}({Line},{Column}): {(IsError ? "error" : "warning")} {Code}: {Message}";
    }

    /// <summary>A value of a <c>.queen</c> entry.</summary>
    public abstract record QueenValue(int Line, int Column)
    {
        /// <summary>The value as it reads: what a text, a key or an error message shows.</summary>
        public abstract string Display { get; }
    }

    /// <summary>Plain text: unquoted (up to the comma or the end of the line, trimmed) or between quotes.</summary>
    public sealed record QueenText(string Value, int Line, int Column) : QueenValue(Line, Column)
    {
        public override string Display => Value;
    }

    /// <summary>A number, with or without decimals (<c>2</c>, <c>-3</c>, <c>1.5</c>).</summary>
    public sealed record QueenNumber(decimal Value, string Raw, int Line, int Column) : QueenValue(Line, Column)
    {
        public bool IsInteger => decimal.Truncate(Value) == Value;
        public override string Display => Raw;
    }

    /// <summary><c>v1.0</c>, <c>v2.3.11.4</c>: any number of <c>u32</c> terms after a <c>v</c>.</summary>
    public sealed record QueenVersion(uint[] Terms, int Line, int Column) : QueenValue(Line, Column)
    {
        public override string Display => "v" + string.Join('.', Terms);
    }

    public sealed record QueenBool(bool Value, int Line, int Column) : QueenValue(Line, Column)
    {
        public override string Display => Value ? "true" : "false";
    }

    /// <summary>A name (<c>exe</c>, <c>refcount</c>, <c>Color.Red</c>): an enum member or a symbol the key gives meaning to.</summary>
    public sealed record QueenIdentifier(string Name, int Line, int Column) : QueenValue(Line, Column)
    {
        public override string Display => Name;
    }

    /// <summary><c>[a, b, c]</c>.</summary>
    public sealed record QueenList(QueenValue[] Items, int Line, int Column) : QueenValue(Line, Column)
    {
        public override string Display => "[" + string.Join(", ", Items.Select(i => i.Display)) + "]";
    }

    /// <summary>
    /// A value with <c>$[expression]</c> parts, before they are evaluated: literal text and expressions in order. A value
    /// that is a single <c>$[…]</c> takes the kind of its result (a number stays a number); one mixed with text is text.
    /// </summary>
    public sealed record QueenTemplate(object[] Parts, int Line, int Column) : QueenValue(Line, Column)
    {
        public IEnumerable<QueenExpression> Expressions => Parts.OfType<QueenExpression>();
        public override string Display => string.Concat(Parts.Select(p => p is QueenExpression e ? $"$[{e.Source}]" : (string)p));
    }

    /// <summary>The expression inside <c>$[…]</c> (in the project's language).</summary>
    public sealed record QueenExpression(string Source, int Line, int Column);

    public sealed record QueenEntry(string Key, QueenValue Value, int Line, int Column);

    /// <summary><c>chunk NAME:</c> and its entries, in order.</summary>
    public sealed record QueenChunk(string Name, List<QueenEntry> Entries, int Line, int Column)
    {
        public QueenEntry? this[string key] => Entries.FirstOrDefault(e => e.Key == key);
    }

    /// <summary>A parsed <c>.queen</c> file: the format version from its header and its chunks.</summary>
    public sealed record QueenDocument(string File, QueenVersion FormatVersion, List<QueenChunk> Chunks)
    {
        public IEnumerable<QueenChunk> Named(string name) => Chunks.Where(c => c.Name == name);
    }

    /// <summary>
    /// Reads a <c>.queen</c> file:
    /// <code>
    /// queen 1.0;
    /// chunk PROJ:
    ///    name: System,
    ///    version: v1.0
    /// </code>
    /// An entry ends at a comma or at the end of its line; a list (<c>[…]</c>) or a <c>$[…]</c> may span lines.
    /// <c>//</c> and <c>/* … */</c> start a comment at the start of a line or after a space (so <c>Tests/*.ant</c> is text).
    /// </summary>
    public sealed class QueenParser
    {
        public const string SupportedVersion = "1.0";

        private readonly string text;
        private readonly string file;
        private readonly List<QueenDiagnostic> diagnostics;
        private int position;
        private int line = 1, column = 1;

        private QueenParser(string text, string file, List<QueenDiagnostic> diagnostics)
        {
            this.text = text;
            this.file = file;
            this.diagnostics = diagnostics;
        }

        public static QueenDocument? Parse(string text, string file, List<QueenDiagnostic> diagnostics) =>
            new QueenParser(text, file, diagnostics).Document();


        private char Current => position < text.Length ? text[position] : '\0';
        private char Peek(int ahead) => position + ahead < text.Length ? text[position + ahead] : '\0';
        private bool AtEnd => position >= text.Length;

        private void Advance()
        {
            if (Current == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
            position++;
        }

        private void Error(string code, string message, int atLine, int atColumn) =>
            diagnostics.Add(new QueenDiagnostic(code, message, file, atLine, atColumn));

        /// <summary>Spaces, line breaks and comments.</summary>
        private void SkipTrivia(bool newlines = true)
        {
            while (!AtEnd)
            {
                if (Current is ' ' or '\t' or '\r' || newlines && Current == '\n')
                    Advance();
                else if (Current == '/' && Peek(1) == '/' && (position == 0 || char.IsWhiteSpace(text[position - 1])))
                {
                    while (!AtEnd && Current != '\n')
                        Advance();
                }
                else if (Current == '/' && Peek(1) == '*' && (position == 0 || char.IsWhiteSpace(text[position - 1])))
                {
                    while (!AtEnd && !(Current == '*' && Peek(1) == '/'))
                        Advance();
                    Advance();
                    Advance();
                }
                else
                    return;
            }
        }

        private string Word()
        {
            var builder = new StringBuilder();
            while (char.IsLetterOrDigit(Current) || Current is '_' or '-' or '.')
            {
                builder.Append(Current);
                Advance();
            }
            return builder.ToString();
        }

        private QueenDocument? Document()
        {
            SkipTrivia();
            int startLine = line, startColumn = column;
            if (Word() != "queen")
            {
                Error("QN001", "a .queen file starts with `queen <version>;` (as in `queen 1.0;`)", startLine, startColumn);
                return null;
            }
            SkipTrivia(newlines: false);
            int versionLine = line, versionColumn = column;
            string version = Word();
            var terms = ParseTerms(version);
            if (terms is null)
            {
                Error("QN002", $"`{version}` is not a format version (as in `1.0`)", versionLine, versionColumn);
                return null;
            }
            if (version != SupportedVersion)
                Error("QN003", $"this compiler reads `queen {SupportedVersion}`, not `queen {version}`", versionLine, versionColumn);
            SkipTrivia(newlines: false);
            if (Current == ';')
                Advance();
            else
                Error("QN004", "expected `;` after the format version", line, column);

            var chunks = new List<QueenChunk>();
            while (true)
            {
                SkipTrivia();
                if (AtEnd)
                    break;
                int chunkLine = line, chunkColumn = column;
                string keyword = Word();
                if (keyword != "chunk")
                {
                    Error("QN005", $"expected `chunk NAME:`, found `{(keyword.Length > 0 ? keyword : Current.ToString())}`", chunkLine, chunkColumn);
                    SkipLine();
                    continue;
                }
                SkipTrivia(newlines: false);
                int nameLine = line, nameColumn = column;
                string name = Word();
                if (name.Length == 0)
                    Error("QN006", "a chunk has a name: `chunk NAME:`", nameLine, nameColumn);
                SkipTrivia(newlines: false);
                if (Current == ':')
                    Advance();
                else
                    Error("QN007", $"expected `:` after `chunk {name}`", line, column);
                var chunk = new QueenChunk(name, [], chunkLine, chunkColumn);
                if (chunks.Any(c => c.Name == name))
                    Error("QN008", $"chunk `{name}` appears twice", nameLine, nameColumn);
                chunks.Add(chunk);
                Entries(chunk);
            }
            return new QueenDocument(file, new QueenVersion(terms!, versionLine, versionColumn), chunks);
        }

        private void SkipLine()
        {
            while (!AtEnd && Current != '\n')
                Advance();
        }

        /// <summary>The entries of a chunk, up to the next <c>chunk</c> or the end of the file.</summary>
        private void Entries(QueenChunk chunk)
        {
            while (true)
            {
                SkipTrivia();
                if (AtEnd)
                    return;
                int keyLine = line, keyColumn = column;
                int save = position, saveLine = line, saveColumn = column;
                string key = Word();
                if (key == "chunk")
                {
                    // The next chunk: rewind to its keyword.
                    position = save;
                    line = saveLine;
                    column = saveColumn;
                    return;
                }
                if (key.Length == 0)
                {
                    Error("QN009", $"expected `key: value`, found `{Current}`", keyLine, keyColumn);
                    SkipLine();
                    continue;
                }
                SkipTrivia(newlines: false);
                if (Current != ':')
                {
                    Error("QN010", $"expected `:` after `{key}`", line, column);
                    SkipLine();
                    continue;
                }
                Advance();
                SkipTrivia(newlines: false);
                var value = Value(inList: false);
                // Dependencies, sources, copied files and resources can be given any number of times.
                if (chunk[key] is not null && !Repeatable(chunk.Name, key))
                    Error("QN011", $"`{key}` is set twice in chunk `{chunk.Name}`", keyLine, keyColumn);
                else
                    chunk.Entries.Add(new QueenEntry(key, value, keyLine, keyColumn));
                SkipTrivia(newlines: false);
                if (Current == ',')
                    Advance();
            }
        }

        private QueenValue Value(bool inList)
        {
            int valueLine = line, valueColumn = column;
            if (Current == '[' && !(Peek(-1) == '$'))
            {
                Advance();
                var items = new List<QueenValue>();
                while (true)
                {
                    SkipTrivia();
                    if (Current == ']')
                    {
                        Advance();
                        break;
                    }
                    if (AtEnd)
                    {
                        Error("QN012", "a list closes with `]`", valueLine, valueColumn);
                        break;
                    }
                    items.Add(Value(inList: true));
                    SkipTrivia();
                    if (Current == ',')
                        Advance();
                    else if (AtEnd)
                    {
                        Error("QN012", "a list closes with `]`", valueLine, valueColumn);
                        break;
                    }
                    else if (Current != ']')
                    {
                        Error("QN013", "list items are separated by `,`", line, column);
                        break;
                    }
                }
                return new QueenList([.. items], valueLine, valueColumn);
            }
            if (Current == '"' || Current == '\'')
            {
                char quote = Current;
                Advance();
                var builder = new StringBuilder();
                while (!AtEnd && Current != quote && Current != '\n')
                {
                    if (Current == '\\' && Peek(1) == quote)
                        Advance();
                    builder.Append(Current);
                    Advance();
                }
                if (Current == quote)
                    Advance();
                else
                    Error("QN014", $"text that opens with {quote} closes with it on the same line", valueLine, valueColumn);
                return new QueenText(builder.ToString(), valueLine, valueColumn);
            }

            // Raw: up to a comma, the end of the line, or `]` in a list; `$[…]` parts may contain anything.
            var parts = new List<object>();
            var literal = new StringBuilder();
            while (!AtEnd && Current != '\n' && Current != ',' && !(inList && Current == ']'))
            {
                // A comment after a space (`Tests/*.ant` and `https://…` are text).
                if (Current == '/' && Peek(1) is '*' or '/' && position > 0 && char.IsWhiteSpace(text[position - 1]))
                    break;
                if (Current == '$' && Peek(1) == '[')
                {
                    int exprLine = line, exprColumn = column;
                    Advance();
                    Advance();
                    var source = new StringBuilder();
                    int depth = 1;
                    char? inString = null;
                    while (!AtEnd)
                    {
                        char c = Current;
                        if (inString is { } open)
                        {
                            if (c == '\\')
                            {
                                source.Append(c);
                                Advance();
                            }
                            else if (c == open)
                                inString = null;
                        }
                        else if (c is '"' or '\'')
                            inString = c;
                        else if (c == '[')
                            depth++;
                        else if (c == ']' && --depth == 0)
                            break;
                        source.Append(Current);
                        Advance();
                    }
                    if (Current == ']')
                        Advance();
                    else
                        Error("QN015", "`$[` closes with `]`", exprLine, exprColumn);
                    if (literal.Length > 0)
                    {
                        parts.Add(literal.ToString());
                        literal.Clear();
                    }
                    parts.Add(new QueenExpression(source.ToString().Trim(), exprLine, exprColumn));
                    continue;
                }
                literal.Append(Current);
                Advance();
            }
            if (literal.Length > 0)
                parts.Add(literal.ToString());
            if (parts.Any(p => p is QueenExpression))
            {
                // Trim the outer spaces of the literal parts at the ends.
                if (parts[0] is string first)
                    parts[0] = first.TrimStart();
                if (parts[^1] is string last)
                    parts[^1] = last.TrimEnd();
                parts.RemoveAll(p => p is "");
                return new QueenTemplate([.. parts], valueLine, valueColumn);
            }
            string raw = literal.ToString().Trim();
            if (raw.Length == 0)
                Error("QN016", "an entry has a value", valueLine, valueColumn);
            return Classify(raw, valueLine, valueColumn);
        }

        /// <summary>
        /// What unquoted text is: <c>true</c>/<c>false</c>, a number, a version (<c>v</c> and <c>u32</c> terms), a name
        /// (letters, digits, <c>_</c>, dots between names), or else text.
        /// </summary>
        /// <summary>Keys a chunk may give more than once: every DEPS entry, and lists of files.</summary>
        public static bool Repeatable(string chunk, string key) => chunk switch
        {
            "DEPS" => true,
            "SOURCES" => key is "include" or "exclude",
            "FILES" => key == "copy",
            "RESOURCES" => key == "embed",
            "HIVE" => key == "provider",
            _ => key is "local" or "package",
        };

        public static QueenValue Classify(string raw, int line, int column)
        {
            if (raw is "true" or "false")
                return new QueenBool(raw == "true", line, column);
            if (decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number)
                && raw.Any(char.IsDigit))
                return new QueenNumber(number, raw, line, column);
            if (raw.Length > 1 && raw[0] is 'v' or 'V' && char.IsDigit(raw[1]) && ParseTerms(raw[1..]) is { } terms)
                return new QueenVersion(terms, line, column);
            if (IsName(raw))
                return new QueenIdentifier(raw, line, column);
            return new QueenText(raw, line, column);
        }

        private static bool IsName(string raw) =>
            raw.Split('.').All(part => part.Length > 0 && (char.IsLetter(part[0]) || part[0] == '_') && part.All(c => char.IsLetterOrDigit(c) || c == '_'));

        /// <summary><c>1.2.3</c> as <c>u32</c> terms; null when it is not one.</summary>
        internal static uint[]? ParseTerms(string version)
        {
            var parts = version.Split('.');
            var terms = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out terms[i]))
                    return null;
            }
            return terms;
        }
    }
}
