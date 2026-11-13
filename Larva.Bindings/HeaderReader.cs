using System.Text;

namespace Larva.Bindings
{
    /// <summary>
    /// A C header read into a binding description: function prototypes, the typedefs, structs and enums they need, and
    /// the integer <c>#define</c>s. A subset of C: no conditional compilation is evaluated (preprocess the header first,
    /// see <see cref="Preprocess"/>, which runs clang when it is installed). Structs whose fields the bindings can carry
    /// keep their layout (<c>struct Name { … }</c> in the description; functions take pointers to them), and so do unions,
    /// nested anonymous aggregates, arrays (<c>T name[N]</c> as <c>T[N]</c>) and <c>#pragma pack</c> (<c>struct Name pack N</c>);
    /// the others (bitfields, unknown field types) are opaque: pointers to them are <c>void*</c>. Variadic functions and functions over types the bindings cannot carry are skipped and listed in
    /// <see cref="Skipped"/> with the reason.
    /// </summary>
    public sealed class HeaderReader
    {
        /// <summary>Functions that were left out, each with why.</summary>
        public List<(string Name, string Why)> Skipped { get; } = [];

        /// <summary>Structs left opaque although they have a body, each with why.</summary>
        public List<(string Name, string Why)> Opaque { get; } = [];

        /// <summary>Declarations the reader could not read and left out, each as <c>file:line</c> with why; the rest of the header is read.</summary>
        public List<(string Where, string Why)> Unparsed { get; } = [];

        /// <summary>A C enum: the names it goes by (its tag and the typedefs of it), its members and the header it is in.</summary>
        public sealed record CEnum(List<string> Names, List<(string Name, long Value)> Members, string? File);

        /// <summary>The C enums read, in order (their members are among the constants too).</summary>
        public List<CEnum> Enums { get; } = [];

        /// <summary>The header each C name was declared in (functions, structs, constants, enums), when the text said (line markers).</summary>
        public Dictionary<string, string> Origins { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The typedef (or enum tag) a function's parameter or result or a struct's field was written with, as C wrote it:
        /// keys <c>Function#0</c> (the first parameter), <c>Function#result</c>, <c>Struct.field</c>.
        /// </summary>
        public Dictionary<string, string> TypedefUses { get; } = new(StringComparer.Ordinal);

        /// <summary>The description type a C typedef stands for (<c>SDL_InitFlags</c> → <c>u32</c>), or null.</summary>
        public string? TypeOf(string typedef) => DescriptionType(typedef, parameter: false, out _);

        private readonly HashSet<string> enumNames = new(StringComparer.Ordinal);
        private string? currentFile;
        private int currentLine;

        /// <summary>The header a declaration came from, kept among the declarations where its line marker was.</summary>
        private const string FileMarker = "\u0002file";

        /// <summary>A field as written: its C type, name, and the element count when it is an array.</summary>
        private sealed record RawField(string Type, string Name, int? Count);

        /// <summary>A struct with a body, before its fields are resolved (lazily: a field may name a typedef written later).</summary>
        private sealed class RawStruct
        {
            public required string Name;
            public required List<RawField> Fields;
            public bool IsUnion;
            public int Pack;
            public string? Why;                       // set when it turns out opaque
            public List<(string Type, string Name)>? Resolved;
            public bool Resolving;
        }

        private const int MaxArrayFields = 1 << 20;

        /// <summary>The `#pragma pack` in force where the reader is, and the ones `push` saved.</summary>
        private int pack;
        private readonly Stack<int> packs = new();

        /// <summary>A `#pragma pack(…)` kept among the declarations, where it was, to apply in order.</summary>
        private const string PackMarker = "\u0001pack";

        private readonly Dictionary<string, string> typedefs = new(StringComparer.Ordinal);               // alias -> C type text
        private readonly Dictionary<string, (string Result, string Parameters)> functionTypedefs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> functionTypes = new(StringComparer.Ordinal);          // resolved func*<…> : r per alias
        private readonly Dictionary<string, string> structNames = new(StringComparer.Ordinal);            // tag or typedef -> struct name
        private readonly Dictionary<string, RawStruct> structs = new(StringComparer.Ordinal);             // struct name -> body
        private readonly List<RawStruct> structOrder = [];
        private readonly HashSet<string> opaque = new(StringComparer.Ordinal);                            // tags and typedefs without a usable body
        private readonly List<(string Name, long Value)> constants = [];
        private readonly Dictionary<string, long> constantValues = new(StringComparer.Ordinal);
        private readonly List<string> functions = [];
        private readonly HashSet<string> functionNames = new(StringComparer.Ordinal);
        private readonly bool longIs64, wcharIs32, longDoubleIs64;

        /// <param name="longIs64">Whether C's <c>long</c> is 64 bits (LP64, Linux and macOS) or 32 (Windows). Default: the host's.</param>
        /// <param name="wcharIs32">
        /// Whether <c>wchar_t</c> is 32 bits (Linux, macOS: <c>u32</c>) or 16 (Windows: <c>wchar</c>, a UTF-16 string as a
        /// parameter). Default: as <paramref name="longIs64"/> says (every LP64 platform has the 32-bit one).
        /// </param>
        /// <param name="longDoubleIs64">
        /// Whether <c>long double</c> is a plain double (Windows, macOS on ARM: <c>f64</c>) or wider (x86-64 Linux: 16 bytes,
        /// a type the bindings cannot carry). Default: the host's when <paramref name="longIs64"/> is not given, else not LP64.
        /// </param>
        public HeaderReader(bool? longIs64 = null, bool? wcharIs32 = null, bool? longDoubleIs64 = null)
        {
            this.longIs64 = longIs64 ?? !OperatingSystem.IsWindows();
            this.wcharIs32 = wcharIs32 ?? this.longIs64;
            this.longDoubleIs64 = longDoubleIs64 ?? (longIs64 is null ? HostLongDoubleIs64 : !this.longIs64);
        }

        /// <summary>Whether this machine's C `long double` is a plain double: Windows, and macOS on ARM.</summary>
        public static bool HostLongDoubleIs64 => OperatingSystem.IsWindows()
            || OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

        /// <summary>Reads <paramref name="header"/> (its text) for <paramref name="library"/>: the description text.</summary>
        /// <summary>
        /// Where the `#define`s worth reading are (the header's folder and the include folders given): a preprocessed header
        /// (clang `-E -dD`) keeps every macro, the system's and the compiler's too, each after a line marker naming its file.
        /// Empty: every file.
        /// </summary>
        public List<string> DefineRoots { get; } = [];

        private static readonly System.Text.RegularExpressions.Regex LineMarker = new(@"^#\s*(?:line\s+)?\d+\s+\x22([^\x22]*)\x22", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// The file a line marker names, as a path with `/`: the preprocessor writes it as a C string, `\\` for each
        /// backslash and every byte outside ASCII as an octal escape (`\303\261` for `ñ`, UTF-8).
        /// </summary>
        private static string MarkerPath(string quoted)
        {
            if (!quoted.Contains('\\'))
                return quoted;
            var bytes = new List<byte>(quoted.Length);
            for (int i = 0; i < quoted.Length; i++)
            {
                char c = quoted[i];
                if (c == '\\' && i + 1 < quoted.Length)
                {
                    char next = quoted[i + 1];
                    if (next is >= '0' and <= '7')
                    {
                        int value = 0, digits = 0;
                        while (digits < 3 && i + 1 < quoted.Length && quoted[i + 1] is >= '0' and <= '7')
                        {
                            value = value * 8 + (quoted[++i] - '0');
                            digits++;
                        }
                        bytes.Add((byte)value);
                        continue;
                    }
                    if (next is '\\' or '"' or '\'')
                    {
                        bytes.Add((byte)next);
                        i++;
                        continue;
                    }
                }
                bytes.AddRange(Encoding.UTF8.GetBytes([c]));
            }
            return Encoding.UTF8.GetString(bytes.ToArray()).Replace('\\', '/');
        }

        public string Read(string header, string library, string? className = null)
        {
            string code = StripComments(header);
            string? file = null;
            var roots = DefineRoots.Select(r => Path.GetFullPath(r).Replace('\\', '/').TrimEnd('/') + "/").ToList();
            foreach (string line in code.Split('\n'))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith('#'))
                    continue;
                if (LineMarker.Match(trimmed) is { Success: true } marker)
                {
                    file = MarkerPath(marker.Groups[1].Value);
                    continue;
                }
                // Before any line marker (a header read as it is) every define counts; after one, only the user's files'.
                if (file is null || roots.Count == 0 || roots.Any(r => file.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                {
                    currentFile = file;
                    Define(trimmed);
                }
            }
            currentFile = null;
            var declarations = Declarations(code).Select(d => (Text: d.Text.StartsWith(FileMarker, StringComparison.Ordinal) ? d.Text : Callable.Replace(StripDecorations(d.Text), "(*").Trim(), d.Line))
                .Where(d => d.Text.Length > 0).ToList();
            // Types first, prototypes after: a prototype may use a typedef written below it in another header.
            foreach (bool types in new[] { true, false })
            {
                currentFile = null;
                foreach (var (declaration, line) in declarations)
                {
                    if (declaration.StartsWith(FileMarker, StringComparison.Ordinal))
                        currentFile = declaration[FileMarker.Length..];
                    else if (IsTypeDeclaration(declaration) == types)
                        TryDeclaration(declaration, line);
                }
            }
            var text = new StringBuilder();
            text.Append("library ").Append(library).Append('\n');
            if (className is not null)
                text.Append("class ").Append(className).Append('\n');
            foreach (var (name, value) in constants)
                text.Append("const ").Append(name).Append(" = ").Append(value).Append('\n');
            foreach (var definition in structOrder)
            {
                if (ResolveStruct(definition) is { } fields)
                    text.Append(definition.IsUnion ? "union " : "struct ").Append(definition.Name)
                        .Append(definition.Pack is > 0 and < 8 ? $" pack {definition.Pack}" : "").Append(" { ").Append(string.Join("; ", fields.Select(f => $"{f.Type} {f.Name}"))).Append(" }\n");
            }
            foreach (string function in functions)
                text.Append(function).Append('\n');
            return text.ToString();
        }

        /// <summary>Reads a header file; with <paramref name="preprocess"/>, through clang first (when it is on the machine).</summary>
        public string ReadFile(string path, string library, string? className = null, bool preprocess = true, IEnumerable<string>? includeDirectories = null)
        {
            var includes = (includeDirectories ?? []).ToList();
            string text = preprocess && Preprocess(path, includes) is { } expanded ? expanded : File.ReadAllText(path);
            if (DefineRoots.Count == 0)
                DefineRoots.AddRange([Path.GetDirectoryName(Path.GetFullPath(path))!, .. includes]);
            return Read(text, library, className);
        }

        /// <summary>The header expanded by clang (<c>-E -P</c>), or null when clang is not found or fails.</summary>
        public static string? Preprocess(string path, IEnumerable<string>? includeDirectories = null)
        {
            string? clang = FindClang();
            if (clang is null)
                return null;
            // clang writes UTF-8 (the console's code page would mangle non-ASCII text in string literals).
            var start = new System.Diagnostics.ProcessStartInfo(clang) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            start.ArgumentList.Add("-E");
            start.ArgumentList.Add("-dD"); // the #defines too, each after a line marker naming its file
            start.ArgumentList.Add("-x");
            start.ArgumentList.Add("c");
            foreach (string directory in (includeDirectories ?? []).Concat(SystemIncludes()))
            {
                start.ArgumentList.Add("-I");
                start.ArgumentList.Add(directory);
            }
            start.ArgumentList.Add(path);
            try
            {
                using var process = System.Diagnostics.Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                process.StandardError.ReadToEnd();
                process.WaitForExit(120_000);
                return process.ExitCode == 0 ? output.Result : null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The C runtime's include folders when clang does not find them itself: on Windows the SDK's ucrt, um and shared
        /// plus the newest MSVC (vcruntime.h); elsewhere clang knows its own.
        /// </summary>
        public static List<string> SystemIncludes()
        {
            var folders = new List<string>();
            if (!OperatingSystem.IsWindows())
                return folders;
            string kits = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)", "Windows Kits", "10", "Include");
            if (Directory.Exists(kits) && Directory.GetDirectories(kits).Where(d => File.Exists(Path.Combine(d, "ucrt", "stdlib.h"))).OrderByDescending(Path.GetFileName).FirstOrDefault() is { } sdk)
                folders.AddRange([Path.Combine(sdk, "ucrt"), Path.Combine(sdk, "um"), Path.Combine(sdk, "shared")]);
            foreach (string programs in new[] { Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)" })
            {
                string studio = Path.Combine(programs, "Microsoft Visual Studio");
                if (!Directory.Exists(studio))
                    continue;
                var msvc = Directory.GetDirectories(studio).SelectMany(v => Directory.GetDirectories(v))
                    .Select(e => Path.Combine(e, "VC", "Tools", "MSVC")).Where(Directory.Exists)
                    .SelectMany(Directory.GetDirectories).Where(d => File.Exists(Path.Combine(d, "include", "vcruntime.h")))
                    .OrderByDescending(Path.GetFileName).FirstOrDefault();
                if (msvc is not null)
                {
                    folders.Add(Path.Combine(msvc, "include"));
                    break;
                }
            }
            return folders;
        }

        public static string? FindClang()
        {
            string exe = OperatingSystem.IsWindows() ? "clang.exe" : "clang";
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (directory.Length > 0 && File.Exists(Path.Combine(directory, exe)))
                    return Path.Combine(directory, exe);
            }
            foreach (string candidate in new[] { @"C:\Program Files\LLVM\bin\clang.exe", "/usr/bin/clang", "/usr/local/bin/clang", "/opt/homebrew/opt/llvm/bin/clang" })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        // ── Text ────────────────────────────────────────────────────

        private static string StripComments(string text)
        {
            var result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++; // a line continuation
                    continue;
                }
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                        i++;
                    result.Append('\n');
                    continue;
                }
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    int newlines = end < 0 ? 0 : text[i..end].Count(c => c == '\n');
                    result.Append('\n', newlines);
                    i = end < 0 ? text.Length : end + 1;
                    continue;
                }
                if (text[i] == '"' || text[i] == '\'')
                {
                    char quote = text[i];
                    result.Append(quote);
                    i++;
                    while (i < text.Length && text[i] != quote)
                    {
                        if (text[i] == '\\')
                            result.Append(text[i++]);
                        if (i < text.Length)
                            result.Append(text[i++]);
                    }
                    if (i < text.Length)
                        result.Append(quote);
                    continue;
                }
                result.Append(text[i]);
            }
            return result.ToString();
        }

        /// <summary><c>#define NAME number</c>: a constant. Other directives are ignored.</summary>
        private void Define(string line)
        {
            string[] parts = line[1..].Trim().Split((char[])[' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[0] != "define" || parts[1].Contains('('))
                return;
            string body = parts[2].Trim();
            // `UINT64_C(0x20)`, `SDL_UINT64_C(0x20)`: the literal of a constant-making macro.
            if (System.Text.RegularExpressions.Regex.Match(body, @"^[A-Za-z_]\w*_C\s*\(\s*([^()]+?)\s*\)$") is { Success: true } wrapped)
                body = wrapped.Groups[1].Value;
            if (IsIdentifier(parts[1]) && Evaluate(body) is { } value)
                Constant(parts[1], value);
        }

        private void Constant(string name, long value)
        {
            if (constantValues.TryAdd(name, value))
            {
                constants.Add((name, value));
                if (currentFile is not null)
                    Origins.TryAdd(name, currentFile);
            }
        }

        /// <summary>The typedef or enum tag a C type is written with (<c>const SDL_InitFlags</c> → <c>SDL_InitFlags</c>), or null.</summary>
        private string? WrittenTypedef(string cType)
        {
            var words = Tokens(cType).Where(w => w is not ("const" or "volatile" or "*" or "(" or ")" or "__cdecl" or "__stdcall")).ToList();
            if (words.Count == 2 && words[0] is "enum" or "struct" or "union")
                return words[1];
            if (words.Count == 1 && (typedefs.ContainsKey(words[0]) || enumNames.Contains(words[0]) || structNames.ContainsKey(words[0])))
                return words[0];
            return null;
        }

        /// <summary>
        /// Top-level declarations (ending in <c>;</c>), bodies <c>{ … }</c> of functions skipped, directives dropped, each
        /// with the line it starts on (the header's own line after a line marker). Text left unterminated at the end
        /// (an unmatched <c>{</c>) is a declaration too, for the reader to refuse with its line.
        /// </summary>
        private static List<(string Text, int Line)> Declarations(string code)
        {
            var declarations = new List<(string, int)>();
            var current = new StringBuilder();
            int depth = 0, parens = 0, externBlocks = 0, line = 1, startLine = 1;
            bool directive = false, lineStart = true;
            void Take()
            {
                declarations.Add((current.ToString(), startLine));
                current.Clear();
            }
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '\n')
                    line++;
                if (lineStart && c == '#')
                    directive = true;
                lineStart = c == '\n';
                if (directive)
                {
                    if (c == '\n')
                    {
                        directive = false;
                        // `#pragma pack(…)`: kept in place (it changes the structs declared after it).
                        int lineStart2 = code.LastIndexOf('\n', Math.Max(0, i - 1)) + 1;
                        string text = code[lineStart2..i].Trim();
                        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^#\s*pragma\s+pack\s*\("))
                            declarations.Add((PackMarker + text[(text.IndexOf('(') + 1)..text.LastIndexOf(')')], line));
                        else if (LineMarker.Match(text) is { Success: true } marker)
                        {
                            declarations.Add((FileMarker + MarkerPath(marker.Groups[1].Value), line));
                            line = int.Parse(System.Text.RegularExpressions.Regex.Match(text, @"\d+").Value); // the next line's number in that file
                        }
                    }
                    continue;
                }
                if (current.Length == 0 && !char.IsWhiteSpace(c))
                    startLine = line;
                if (c == '{')
                {
                    // `extern "C" {` (a header read without the preprocessor, or an unguarded block): the declarations
                    // inside are read as they are, the braces dropped.
                    if (depth == 0 && parens == 0 && current.ToString().Trim() is "extern \"C\"" or "extern \"C++\"")
                    {
                        externBlocks++;
                        current.Clear();
                        continue;
                    }
                    depth++;
                    current.Append(c);
                    continue;
                }
                if (c == '}')
                {
                    if (depth == 0 && externBlocks > 0)
                    {
                        externBlocks--;
                        current.Clear();
                        continue;
                    }
                    depth--;
                    current.Append(c);
                    // A function definition: its body ends the declaration without a `;`.
                    if (depth == 0 && parens == 0 && !current.ToString().TrimStart().StartsWith("typedef") && !StartsWithAggregate(current.ToString()))
                        Take();
                    continue;
                }
                if (c == '(') parens++;
                else if (c == ')') parens--;
                if (c == ';' && depth == 0)
                {
                    Take();
                    continue;
                }
                if (current.Length > 0 || !char.IsWhiteSpace(c))
                    current.Append(c == '\n' ? ' ' : c); // no leading whitespace: an empty `current` marks where a declaration starts
            }
            if (current.ToString().Trim().Length > 0)
                Take();
            return declarations;
        }

        private static bool StartsWithAggregate(string text)
        {
            string t = text.TrimStart();
            return t.StartsWith("struct ") || t.StartsWith("union ") || t.StartsWith("enum ");
        }

        /// <summary>Typedefs, enums and aggregate definitions (not a prototype whose result is `struct X*`).</summary>
        private static bool IsTypeDeclaration(string t)
        {
            if (t.StartsWith(PackMarker, StringComparison.Ordinal))
                return true;
            if (t.StartsWith("typedef ") || t.StartsWith("enum{"))
                return true;
            if (!StartsWithAggregate(t))
                return false;
            // `enum Tag F(enum Tag t);` and `struct S* F(void);` are prototypes; `enum Tag { … }`, `enum Tag;` are declarations.
            int brace = t.IndexOf('{'), paren = t.IndexOf('(');
            return paren < 0 || brace >= 0 && brace < paren;
        }

        private static readonly System.Text.RegularExpressions.Regex Decorations = new(
            @"\b(extern|static|inline|__inline|__forceinline|__inline__|_Noreturn|__restrict|restrict|__unaligned)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly string[] DecoratingKeywords = ["__pragma"];

        /// <summary>
        /// A declaration without its decorations: `__attribute__((…))` and `__declspec(…)` through <see cref="LayoutAttributes"/>
        /// (their parentheses matched: `visibility("default")`, `align(16)` nest; `packed` / `aligned(n)` kept as markers for
        /// the struct they belong to), `__pragma(…)` likewise, wherever they stand, and the storage words.
        /// </summary>
        private static string StripDecorations(string declaration)
        {
            declaration = LayoutAttributes(declaration);
            var text = new StringBuilder(declaration.Length);
            for (int i = 0; i < declaration.Length;)
            {
                string? keyword = DecoratingKeywords.FirstOrDefault(k => string.CompareOrdinal(declaration, i, k, 0, k.Length) == 0
                    && (i == 0 || !(char.IsLetterOrDigit(declaration[i - 1]) || declaration[i - 1] == '_')));
                if (keyword is not null)
                {
                    int open = i + keyword.Length;
                    while (open < declaration.Length && char.IsWhiteSpace(declaration[open]))
                        open++;
                    if (open < declaration.Length && declaration[open] == '(' && MatchingParenthesis(declaration, open) is var close && close > open)
                    {
                        text.Append(' ');
                        i = close + 1;
                        continue;
                    }
                }
                text.Append(declaration[i++]);
            }
            return Decorations.Replace(text.ToString(), " ");
        }

        /// <summary>`(__cdecl *name)`, `( * name)`: a function pointer's declarator, written `(*name)` for the rest of the reader.</summary>
        private static readonly System.Text.RegularExpressions.Regex Callable = new(
            @"\(\s*(?:(?:__cdecl|__stdcall|__fastcall|__vectorcall|WINAPI|CALLBACK|APIENTRY|SDLCALL)\s*)*\*\s*",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// A layout attribute kept as a word where it was written, for the struct it belongs to: `__attribute__((packed))`
        /// and `__attribute__((aligned(n)))` / `__declspec(align(n))` (n: 0 when it is not a literal). The other
        /// attributes are dropped here, with balanced parentheses (`aligned(16)` has its own).
        /// </summary>
        private const string PackedMarker = "__larva_packed__";
        private const string AlignedMarker = "__larva_aligned_";

        private static string LayoutAttributes(string declaration)
        {
            if (!declaration.Contains("__attribute__") && !declaration.Contains("__declspec"))
                return declaration;
            var text = new StringBuilder();
            for (int i = 0; i < declaration.Length;)
            {
                int keyword = declaration.IndexOf("__", i, StringComparison.Ordinal);
                if (keyword < 0 || !(Starts(keyword, "__attribute__") || Starts(keyword, "__declspec")))
                {
                    text.Append(declaration, i, (keyword < 0 ? declaration.Length : keyword + 2) - i);
                    i = keyword < 0 ? declaration.Length : keyword + 2;
                    continue;
                }
                bool attribute = Starts(keyword, "__attribute__");
                int open = declaration.IndexOf('(', keyword), close = open < 0 ? -1 : MatchingParenthesis(declaration, open);
                if (close < 0)
                {
                    text.Append(declaration, i, declaration.Length - i);
                    break;
                }
                text.Append(declaration, i, keyword - i);
                string inside = declaration[(open + 1)..close].Trim();
                if (attribute && inside.StartsWith('(') && inside.EndsWith(')'))
                    inside = inside[1..^1];
                foreach (string item in BindingDescription.SplitTopLevel(inside))
                {
                    string a = item.Trim();
                    if (a is "packed" or "__packed__")
                        text.Append(' ').Append(PackedMarker).Append(' ');
                    else if (a is "aligned" or "__aligned__" || a.StartsWith("aligned") || a.StartsWith("__aligned__") || !attribute && a.StartsWith("align"))
                    {
                        int argument = a.IndexOf('(');
                        long n = argument >= 0 && a.EndsWith(')') ? Integer(a[(argument + 1)..^1]) ?? 0 : 0;
                        text.Append(' ').Append(AlignedMarker).Append(n).Append("__ ");
                    }
                }
                text.Append(' ');
                i = close + 1;
            }
            return text.ToString();

            bool Starts(int at, string word) => string.CompareOrdinal(declaration, at, word, 0, word.Length) == 0;
        }

        /// <summary>
        /// The layout attributes of a struct: taken out of the text around its body (not out of the body: those belong to
        /// its fields or nested structs).
        /// </summary>
        private static string TakeLayout(string text, out bool packed, out int? aligned)
        {
            packed = false;
            aligned = null;
            if (!text.Contains("__larva_"))
                return text;
            int open = text.IndexOf('{'), close = text.LastIndexOf('}');
            string head = open < 0 ? text : text[..open], body = open < 0 ? "" : text[open..(close + 1)], tail = open < 0 ? "" : text[(close + 1)..];
            bool isPacked = false;
            int? alignment = null;
            string result = Take(head) + body + Take(tail);
            packed = isPacked;
            aligned = alignment;
            return result;

            string Take(string part)
            {
                var kept = new StringBuilder();
                foreach (string word in part.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (word == PackedMarker)
                        isPacked = true;
                    else if (word.StartsWith(AlignedMarker, StringComparison.Ordinal) && int.TryParse(word[AlignedMarker.Length..].TrimEnd('_'), out int n))
                        alignment = Math.Max(alignment ?? 0, n);
                    else
                        kept.Append(word).Append(' ');
                }
                return kept.ToString();
            }
        }

        /// <summary>`push[, n]`, `pop`, `n` or nothing: the pack in force from here on (0: natural alignment).</summary>
        private void Pragma(string arguments)
        {
            var parts = arguments.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                pack = 0;
                return;
            }
            if (parts[0] == "push")
            {
                packs.Push(pack);
                if (parts.Length > 1 && int.TryParse(parts[^1], out int pushed))
                    pack = pushed;
                return;
            }
            if (parts[0] == "pop")
            {
                pack = packs.Count > 0 ? packs.Pop() : 0;
                return;
            }
            if (int.TryParse(parts[0], out int value))
                pack = value;
        }

        /// <summary>
        /// One declaration, or a warning in <see cref="Unparsed"/> naming its line: what the reader cannot make sense of
        /// (text C would refuse, a shape the reader does not know) is left out, never the whole header.
        /// </summary>
        private void TryDeclaration(string t, int line)
        {
            currentLine = line;
            try
            {
                Declaration(t);
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidOperationException or FormatException or NullReferenceException)
            {
                string shown = t.Length > 60 ? t[..57] + "…" : t;
                Unparsed.Add(($"{currentFile ?? "header"}:{line}", $"`{shown}` could not be read"));
            }
        }

        private void Declaration(string t)
        {
            if (t.StartsWith(PackMarker, StringComparison.Ordinal))
            {
                Pragma(t[PackMarker.Length..]);
                return;
            }
            if (t.StartsWith("typedef "))
            {
                Typedef(t[8..].Trim());
                return;
            }
            if ((t.StartsWith("enum ") || t.StartsWith("enum{")) && IsTypeDeclaration(t))
            {
                int brace = t.IndexOf('{');
                string tag = brace < 0 ? "" : t[4..brace].Trim();
                Enum(t, IsIdentifier(tag) ? [tag] : []); // `enum Tag;` alone: nothing to read
                return;
            }
            if (IsTypeDeclaration(t))
            {
                t = TakeLayout(t, out bool packed, out int? aligned);
                int brace = t.IndexOf('{');
                string tag = (brace < 0 ? t : t[..brace]).Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
                if (tag.Length == 0)
                    return;
                if (brace >= 0)
                    DefineStruct(tag, tag, t[(brace + 1)..t.LastIndexOf('}')], t.StartsWith("union "), packed, aligned);
                else if (!structNames.ContainsKey(tag))
                    opaque.Add(tag); // a forward declaration: opaque unless a body follows
                return;
            }
            t = TakeLayout(t, out _, out _); // a layout attribute on a function means nothing to its binding
            int body = t.IndexOf('{');
            Prototype(body < 0 ? t : t[..body]); // a function definition: its prototype is the part before the body
        }

        private void Typedef(string t)
        {
            // typedef <ret> (*Name)(params);  or  typedef <ret> (CALL *Name)(params);
            int pointerOpen = t.IndexOf("(*", StringComparison.Ordinal);
            if (pointerOpen < 0)
            {
                int star = t.IndexOf('(');
                if (star >= 0 && t.IndexOf('*', star) is var s2 && s2 > star && s2 < t.IndexOf(')', star))
                    pointerOpen = star;
            }
            // A `(*` inside a struct's body is a function pointer field, not this typedef's shape.
            int firstBrace = t.IndexOf('{');
            if (firstBrace >= 0 && firstBrace < pointerOpen)
                pointerOpen = -1;
            if (pointerOpen >= 0 && t.IndexOf(')', pointerOpen) is var pointerClose && pointerClose > 0 && t.IndexOf('(', pointerClose) is var paramsOpen && paramsOpen > 0)
            {
                string name = Tokens(t[(pointerOpen + 1)..pointerClose]).LastOrDefault(w => IsIdentifier(w)) ?? "";
                if (name.Length > 0)
                    functionTypedefs[name] = (t[..pointerOpen].Trim(), t[(paramsOpen + 1)..t.LastIndexOf(')')]);
                return;
            }
            int brace = t.IndexOf('{');
            if (brace >= 0)
            {
                // typedef struct [Tag] { … } Name, *PName;  /  typedef enum { … } Name;
                t = TakeLayout(t, out bool packed, out int? aligned);
                brace = t.IndexOf('{');
                int close = t.LastIndexOf('}');
                var declarators = Declarators(t[(close + 1)..]);
                string head = t[..brace].Trim();
                if (head.StartsWith("enum"))
                {
                    string enumTag = head.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
                    Enum(t[..(close + 1)], [.. declarators.Where(d => d.Stars == 0).Select(d => d.Name), .. IsIdentifier(enumTag) ? [enumTag] : Array.Empty<string>()]);
                    foreach (var (name, stars) in declarators)
                        typedefs[name] = "int" + new string('*', stars);
                    return;
                }
                string tag = head.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
                string structName = declarators.FirstOrDefault(d => d.Stars == 0).Name ?? tag;
                if (structName.Length == 0)
                    return;
                DefineStruct(structName, tag, t[(brace + 1)..close], head.StartsWith("union"), packed, aligned);
                foreach (var (name, stars) in declarators)
                {
                    if (name != structName)
                        typedefs[name] = structName + new string('*', stars);
                }
                return;
            }
            // typedef <target> Name, *PName;
            int firstComma = t.IndexOf(',');
            var targetWords = Tokens(firstComma < 0 ? t : t[..firstComma]);
            if (targetWords.Count < 2)
                return;
            string alias = targetWords[^1];
            if (!IsIdentifier(alias))
                return;
            int aliasStars = targetWords.Count(w => w == "*");
            string target = string.Join(' ', targetWords.Take(targetWords.Count - 1).Where(w => w != "*"));
            var all = new List<(string Name, int Stars)> { (alias, aliasStars) };
            if (firstComma >= 0)
                all.AddRange(Declarators(t[firstComma..]));
            foreach (var (name, stars) in all)
            {
                string pointers = new('*', stars);
                if (target.StartsWith("struct ") || target.StartsWith("union "))
                {
                    string tag = target.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
                    if (stars == 0 && tag.Length > 0)
                        structNames[name] = tag; // resolved later: the body may come after
                    else
                        typedefs[name] = target + pointers;
                    if (tag.Length > 0 && !structNames.ContainsKey(tag))
                        opaque.Add(tag);
                }
                else if (target.StartsWith("enum "))
                {
                    typedefs[name] = "int" + pointers;
                    // `typedef enum Tag Name;`: another name of that enum.
                    string tag = target.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
                    if (stars == 0 && Enums.FirstOrDefault(e => e.Names.Contains(tag)) is { } known && !known.Names.Contains(name))
                    {
                        known.Names.Insert(0, name);
                        enumNames.Add(name);
                    }
                }
                else
                    typedefs[name] = target + pointers;
            }
        }

        /// <summary><c>Name, *PName, **PPName</c> after a typedef's body.</summary>
        private static List<(string Name, int Stars)> Declarators(string text)
        {
            var list = new List<(string, int)>();
            foreach (string piece in text.Split(','))
            {
                var tokens = Tokens(piece);
                string? name = tokens.LastOrDefault(IsIdentifier);
                if (name is not null)
                    list.Add((name, tokens.Count(w => w == "*")));
            }
            return list;
        }

        /// <summary>
        /// A struct with a body. <paramref name="packed"/> (<c>__attribute__((packed))</c>) packs it to 1 whatever
        /// <c>#pragma pack</c> says; <paramref name="aligned"/> (<c>aligned(n)</c>, <c>__declspec(align(n))</c>) is an
        /// alignment the description cannot carry: the struct is opaque, with the reason (never a wrong layout).
        /// </summary>
        private void DefineStruct(string name, string tag, string body, bool isUnion = false, bool packed = false, int? aligned = null)
        {
            if (structs.ContainsKey(name))
            {
                // Defined twice (two headers, or a header read without the preprocessor and its include guard): the first wins.
                Unparsed.Add(($"{currentFile ?? "header"}:{currentLine}", $"{(isUnion ? "union" : "struct")} `{name}` is defined twice: the first definition is kept"));
                return;
            }
            var raw = new RawStruct { Name = name, Fields = [], IsUnion = isUnion, Pack = packed ? 1 : pack };
            if (aligned is { } alignment)
                raw.Why = $"aligned({(alignment > 0 ? alignment.ToString() : "…")}): an alignment the bindings cannot carry";
            if (currentFile is not null)
                Origins.TryAdd(name, currentFile);
            int anonymous = 0;
            {
                foreach (string piece in SplitBody(body))
                {
                    string declaration = piece.Trim();
                    if (declaration.Length == 0)
                        continue;
                    if (declaration.Contains('{'))
                    {
                        // a nested `struct { … } a, *b;` or `union { … };`: its own definition, named after the outer one
                        declaration = TakeLayout(declaration, out bool nestedPacked, out int? nestedAligned);
                        int open = declaration.IndexOf('{'), close = declaration.LastIndexOf('}');
                        bool nestedUnion = declaration.StartsWith("union");
                        var declarators = Declarators(declaration[(close + 1)..]);
                        if (declarators.Count == 0)
                            declarators.Add(($"anon{anonymous++}", 0));
                        string nested = $"{name}_{declarators[0].Name}";
                        DefineStruct(nested, "", declaration[(open + 1)..close], nestedUnion, nestedPacked, nestedAligned);
                        foreach (var (fieldName, stars) in declarators)
                            raw.Fields.Add(Field(nested, stars, declaration[(close + 1)..], fieldName));
                        continue;
                    }
                    if (declaration.Contains("__larva_"))
                    {
                        // `int x __attribute__((aligned(8)));`, a packed field: a layout of its own the description cannot carry
                        string written = System.Text.RegularExpressions.Regex.Replace(declaration.Replace(PackedMarker, "packed"), AlignedMarker + @"(\d+)__", "aligned($1)");
                        raw.Why = $"field `{written}`: an attribute on a field (packed, aligned) the bindings cannot carry";
                        break;
                    }
                    if (declaration.Contains(':'))
                    {
                        raw.Why = "bitfields";
                        break;
                    }
                    if (declaration.Contains("(*"))
                    {
                        // a function pointer field: `ret (*name)(params)`, or an array of them: `ret (*name[N])(params)`
                        var (type, fieldName) = SplitParameter(declaration, raw.Fields.Count + 1);
                        int declaratorOpen = declaration.IndexOf("(*", StringComparison.Ordinal), declaratorClose = declaration.IndexOf(')', declaratorOpen);
                        string declarator = declaratorClose < 0 ? declaration[declaratorOpen..] : declaration[declaratorOpen..declaratorClose];
                        raw.Fields.Add(Field(type, 0, declarator, fieldName)); // `[N]` in the declarator: N function pointers in a row
                        continue;
                    }
                    var parts = BindingDescription.SplitTopLevel(declaration);
                    string firstPiece = parts[0];
                    int firstBracket = firstPiece.IndexOf('[');
                    var first = Tokens(firstBracket < 0 ? firstPiece : firstPiece[..firstBracket]);
                    int nameIndex = first.FindLastIndex(w => IsIdentifier(w) && !IsTypeWord(w));
                    if (nameIndex < 1)
                    {
                        raw.Why = $"field `{declaration}`";
                        break;
                    }
                    string baseType = string.Join(' ', first.Take(nameIndex).Where(w => w != "*"));
                    int baseStars = first.Take(nameIndex).Count(w => w == "*");
                    raw.Fields.Add(Field(baseType, baseStars, firstPiece, first[nameIndex]));
                    foreach (string more in parts.Skip(1))
                    {
                        int bracket = more.IndexOf('[');
                        var tokens = Tokens(bracket < 0 ? more : more[..bracket]);
                        string? fieldName = tokens.FirstOrDefault(IsIdentifier);
                        if (fieldName is null)
                            continue;
                        raw.Fields.Add(Field(baseType, tokens.Count(w => w == "*"), more, fieldName));
                    }
                }
            }
            structs[name] = raw;
            structOrder.Add(raw);
            structNames[name] = name;
            if (tag.Length > 0)
                structNames[tag] = name;
            opaque.Remove(name);
            opaque.Remove(tag);
        }

        /// <summary>The members of a body, split at the `;` outside nested braces and parentheses.</summary>
        private static List<string> SplitBody(string body)
        {
            var pieces = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c is '{' or '(') depth++;
                else if (c is '}' or ')') depth--;
                else if (c == ';' && depth == 0)
                {
                    pieces.Add(body[start..i]);
                    start = i + 1;
                }
            }
            pieces.Add(body[start..]);
            return pieces;
        }

        private RawField Field(string baseType, int stars, string declaration, string name)
        {
            int? count = null;
            if (declaration.Contains('['))
            {
                count = 0;
                foreach (var m in System.Text.RegularExpressions.Regex.Matches(declaration, @"\[([^\]]*)\]").Cast<System.Text.RegularExpressions.Match>())
                {
                    string inside = m.Groups[1].Value.Trim();
                    long? n = Evaluate(inside);
                    if (n is null || n <= 0 || n > MaxArrayFields)
                    {
                        count = -1; // unknown or too long: the struct stays opaque
                        break;
                    }
                    count = count == 0 ? (int)n : count * (int)n;
                }
            }
            return new RawField(baseType + new string('*', stars), name, count);
        }

        /// <summary>The struct's fields as description types, or null when it is opaque (with the reason recorded).</summary>
        private List<(string Type, string Name)>? ResolveStruct(RawStruct raw)
        {
            if (raw.Resolved is not null)
                return raw.Resolved;
            if (raw.Why is not null || raw.Resolving)
            {
                if (raw.Why is not null && !Opaque.Any(o => o.Name == raw.Name))
                    Opaque.Add((raw.Name, raw.Why));
                return null;
            }
            raw.Resolving = true;
            var fields = new List<(string, string)>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in raw.Fields)
            {
                string? type = DescriptionType(field.Type, parameter: false, out string? why, field: true);
                if (type is null)
                {
                    raw.Why = $"field `{field.Name}`: {why}";
                    break;
                }
                if (WrittenTypedef(field.Type) is { } fieldTypedef)
                    TypedefUses[$"{raw.Name}.{field.Name}"] = fieldTypedef;
                int before = fields.Count;
                if (field.Count is null)
                    fields.Add((type, field.Name));
                else if (field.Count < 0 || field.Count > MaxArrayFields)
                {
                    raw.Why = $"field `{field.Name}`: an array of unknown or too great a length";
                    break;
                }
                else
                    fields.Add(($"{type}[{field.Count}]", field.Name)); // `T name[N]`: an inline array
                if (fields.Skip(before).Any(f => !names.Add(f.Item2)))
                {
                    raw.Why = $"field `{field.Name}` is declared twice";
                    break;
                }
            }
            raw.Resolving = false;
            if (raw.Why is not null)
            {
                Opaque.Add((raw.Name, raw.Why));
                return null;
            }
            raw.Resolved = fields;
            return fields;
        }

        private void Enum(string t, List<string> names)
        {
            int open = t.IndexOf('{'), close = t.LastIndexOf('}');
            if (open < 0 || close < open)
                return;
            var members = new List<(string, long)>();
            if (names.Count > 0)
            {
                Enums.Add(new CEnum(names, members, currentFile));
                foreach (string name in names)
                {
                    enumNames.Add(name);
                    if (currentFile is not null)
                        Origins.TryAdd(name, currentFile);
                }
            }
            // The next value: null after a member whose expression could not be evaluated (the members numbered after
            // it are unknown too, until one says its value); each is skipped with the reason, never given a wrong value.
            long? next = 0;
            string? unknown = null;
            foreach (string piece in SplitMembers(t[(open + 1)..close]))
            {
                string entry = piece.Trim();
                if (entry.Length == 0)
                    continue;
                int equals = entry.IndexOf('=');
                string name = (equals < 0 ? entry : entry[..equals]).Trim();
                if (!IsIdentifier(name))
                    continue;
                if (equals >= 0)
                {
                    string expression = entry[(equals + 1)..].Trim();
                    next = Evaluate(expression);
                    if (next is null)
                    {
                        Skipped.Add((name, $"enum member: its value `{expression}` could not be evaluated"));
                        unknown = name;
                        continue;
                    }
                }
                else if (next is null)
                {
                    Skipped.Add((name, $"enum member: numbered after `{unknown}`, whose value could not be evaluated"));
                    continue;
                }
                Constant(name, next.Value);
                members.Add((name, next.Value));
                next++;
            }
        }

        /// <summary>An enum's members, split at the commas outside parentheses (`A = MAX(1, 2)`; `<<` is no bracket).</summary>
        private static List<string> SplitMembers(string body)
        {
            var pieces = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] == '(') depth++;
                else if (body[i] == ')') depth--;
                else if (body[i] == ',' && depth == 0)
                {
                    pieces.Add(body[start..i]);
                    start = i + 1;
                }
            }
            pieces.Add(body[start..]);
            return pieces;
        }

        private void Prototype(string t)
        {
            int open = t.IndexOf('(');
            if (open < 0)
                return;
            int close = MatchingParenthesis(t, open);
            if (close < 0)
                return;
            var head = Tokens(t[..open]);
            if (head.Count == 0)
                return;
            string name = head[^1];
            if (!IsIdentifier(name) || functionNames.Contains(name))
                return; // declared twice (several headers): the first wins
            string result = string.Join(' ', head.Take(head.Count - 1));
            string parameters = t[(open + 1)..close];
            if (parameters.Contains("..."))
            {
                Skipped.Add((name, "variadic"));
                return;
            }
            string? resultType = DescriptionType(result, parameter: false, out string? why);
            if (resultType is null)
            {
                Skipped.Add((name, $"result `{result}`: {why}"));
                return;
            }
            var list = new List<string>();
            var uses = new List<(string Key, string Typedef)>();
            if (WrittenTypedef(result) is { } resultTypedef)
                uses.Add(($"{name}#result", resultTypedef));
            int index = 0;
            foreach (string piece in SplitParameters(parameters))
            {
                string p = piece.Trim();
                if (p.Length == 0 || p == "void")
                    continue;
                var (type, parameterName) = SplitParameter(p, ++index);
                string? mapped = DescriptionType(type, parameter: true, out why);
                if (mapped is null)
                {
                    Skipped.Add((name, $"parameter `{p}`: {why}"));
                    return;
                }
                if (WrittenTypedef(type) is { } parameterTypedef)
                    uses.Add(($"{name}#{index - 1}", parameterTypedef));
                list.Add($"{mapped} {parameterName}");
            }
            foreach (var (key, typedef) in uses)
                TypedefUses[key] = typedef;
            if (currentFile is not null)
                Origins.TryAdd(name, currentFile);
            functionNames.Add(name);
            functions.Add($"{name}({string.Join(", ", list)}){(resultType == "void" ? "" : $" : {resultType}")}");
        }

        /// <summary>The type and the name of a parameter (<c>const char* name</c>, a function pointer, an array, an unnamed one).</summary>
        private static (string Type, string Name) SplitParameter(string p, int index)
        {
            int pointerOpen = p.IndexOf("(*", StringComparison.Ordinal);
            if (pointerOpen >= 0 && p.IndexOf(')', pointerOpen) is var pointerClose && pointerClose > 0)
            {
                string declarator = p[(pointerOpen + 2)..pointerClose];
                if (declarator.IndexOf('[') is var arrayOpen && arrayOpen >= 0)
                    declarator = declarator[..arrayOpen]; // `(*name[N])`: the name is before the length
                string name = Tokens(declarator).LastOrDefault(IsIdentifier) ?? $"p{index}";
                return (p[..pointerOpen] + "(*)" + p[(pointerClose + 1)..], name);
            }
            int bracket = p.IndexOf('[');
            string array = "";
            if (bracket >= 0)
            {
                array = new string('*', p.Count(c => c == '[')); // `T name[]` / `T name[][N]`: pointers
                p = p[..bracket].Trim();
            }
            var tokens = Tokens(p);
            if (tokens.Count >= 2 && IsIdentifier(tokens[^1]) && !IsTypeWord(tokens[^1]))
                return (string.Join(' ', tokens.Take(tokens.Count - 1)) + array, tokens[^1]);
            return (string.Join(' ', tokens) + array, $"p{index}");
        }

        /// <summary>
        /// A C type as a description type, or null (with why) when a binding cannot carry it. A struct with layout is
        /// <c>Name*</c> behind pointers and, as a <paramref name="field"/> of another struct, <c>Name</c> by value.
        /// </summary>
        private string? DescriptionType(string c, bool parameter, out string? why, bool field = false)
        {
            why = null;
            var words = Tokens(c).Where(w => w is not ("const" or "volatile" or "extern" or "static" or "inline" or "__inline" or "__forceinline"
                or "__cdecl" or "__stdcall" or "__fastcall" or "__vectorcall" or "WINAPI" or "APIENTRY" or "CALLBACK" or "NTAPI" or "__restrict" or "restrict" or "signed" or "__unaligned")).ToList();
            // a function pointer written out: ret (*)(params), read from the text (the words lose the commas between parameters)
            if (c.IndexOf("(*", StringComparison.Ordinal) is var star && star >= 0 && c.IndexOf(')', star) is var starClose && starClose > 0
                && c.IndexOf('(', starClose) is var listOpen && listOpen > 0 && c.LastIndexOf(')') is var listClose && listClose > listOpen)
            {
                string type = FunctionType(c[..star], c[(listOpen + 1)..listClose]) ?? "";
                if (type.Length == 0)
                    why = "a function pointer over types the bindings cannot carry";
                return type.Length == 0 ? null : type;
            }
            string joined = string.Join(' ', words);
            int pointerOpen = joined.IndexOf("( *", StringComparison.Ordinal);
            if (pointerOpen < 0)
                pointerOpen = joined.IndexOf("(*", StringComparison.Ordinal);
            if (pointerOpen >= 0 && joined.IndexOf(')', pointerOpen) is var pc && pc > 0 && joined.IndexOf('(', pc) is var po && po > 0)
            {
                string type = FunctionType(joined[..pointerOpen], joined[(po + 1)..joined.LastIndexOf(')')]) ?? "";
                if (type.Length == 0)
                    why = "a function pointer over types the bindings cannot carry";
                return type.Length == 0 ? null : type;
            }
            int stars = words.Count(w => w == "*");
            words = words.Where(w => w != "*").ToList();
            if (words.Count == 0)
            {
                why = "no type";
                return null;
            }
            string baseText = string.Join(' ', words);
            if (words.Count == 1 && FunctionTypedef(words[0]) is { } functionType)
            {
                // A pointer to a function pointer (an out parameter that receives one): an untyped pointer.
                return stars > 0 ? Pointers("void", stars) : functionType;
            }
            // Whether the pointee is const (`const char*`, `char const*`, `LPCSTR`): a `const` before the first `*` of a
            // level qualifies that level's base type, which is the pointee unless a typedef below it adds the pointer
            // (`const LPSTR` is `char* const`: a const pointer to writable chars).
            bool constPointee = ConstBase(Tokens(c), out _);
            // typedef aliases, followed to the end
            for (int guard = 0; guard < 16 && words.Count == 1 && typedefs.TryGetValue(words[0], out string? target); guard++)
            {
                var targetWords = Tokens(target);
                stars += targetWords.Count(w => w == "*");
                bool constTarget = ConstBase(targetWords, out bool starBelow);
                if (starBelow)
                    constPointee = constTarget; // the const seen so far qualified this pointer, not what it points to
                else
                    constPointee |= constTarget;
                words = targetWords.Where(w => w is not ("const" or "volatile" or "*")).ToList();
                baseText = string.Join(' ', words);
            }
            if (words[0] == "enum")
                return Pointers("i32", stars); // `enum Tag`: its number (the language may give it the enum type by its name)
            // Windows' string pointer names when no header defined them: `const char*` / `char*` (and wide).
            if (words.Count == 1 && words[0] is "LPCSTR" or "PCSTR" or "LPSTR" or "PSTR" or "LPCWSTR" or "PCWSTR" or "LPWSTR" or "PWSTR")
            {
                stars++;
                constPointee = words[0] is "LPCSTR" or "PCSTR" or "LPCWSTR" or "PCWSTR";
                words = [words[0].EndsWith("WSTR", StringComparison.Ordinal) ? "wchar_t" : "char"];
                baseText = words[0];
            }
            string tagOrName = words[0] is "struct" or "union" ? words[^1] : words[0];
            if (StructName(tagOrName) is { } structName)
            {
                if (stars == 0 && !field)
                {
                    why = $"a struct by value (`{baseText}`)";
                    return null;
                }
                return Pointers(structName, stars);
            }
            if (words[0] is "struct" or "union" || opaque.Contains(tagOrName) || structs.ContainsKey(tagOrName) || structNames.ContainsKey(tagOrName))
            {
                if (stars == 0)
                {
                    why = $"a struct by value (`{baseText}`)";
                    return null;
                }
                return Pointers("void", stars); // opaque: pointers to it are void*
            }
            string canonical = Canonical(words);
            string? primitive = Primitive(canonical);
            if (primitive is null)
            {
                why = canonical == "long double" ? "`long double` is wider than f64 on this platform (16 bytes): not a type the bindings carry" : $"unknown type `{baseText}`";
                return null;
            }
            if (primitive == "char" || primitive == "wchar")
            {
                // `const char*` as a parameter is a C string (a copy with its terminator; null when C takes NULL);
                // `char*` a buffer C writes into (bytes the caller owns: a copy would lose what C wrote); as a result a
                // pointer to bytes.
                if (stars == 1 && parameter)
                    return constPointee ? primitive == "char" ? "str?" : "String<u16>?" : primitive == "char" ? "u8*" : "u16*";
                return Pointers(primitive == "char" ? "i8" : "u16", stars);
            }
            if (primitive == "void" && stars == 0 && (parameter || field))
            {
                why = "void";
                return null;
            }
            return Pointers(primitive, stars);
        }

        /// <summary>
        /// Whether a `const` qualifies the base type of one level of a C type (before its first `*`, or anywhere when it
        /// has none), and whether the level has a `*` of its own.
        /// </summary>
        private static bool ConstBase(List<string> tokens, out bool hasStar)
        {
            int star = tokens.IndexOf("*");
            hasStar = star >= 0;
            return tokens.Take(hasStar ? star : tokens.Count).Contains("const");
        }

        /// <summary>The struct with layout behind a tag or typedef name (aliases followed), or null when it has none.</summary>
        private string? StructName(string name)
        {
            for (int guard = 0; guard < 16 && structNames.TryGetValue(name, out string? next); guard++)
            {
                if (structs.TryGetValue(next, out var raw))
                    return raw.Resolving ? next : ResolveStruct(raw) is null ? null : next; // resolving: a pointer to itself (`Node* next`)
                if (next == name)
                    break;
                name = next;
            }
            return null;
        }

        private string? FunctionTypedef(string name)
        {
            if (functionTypes.TryGetValue(name, out string? resolved))
                return resolved.Length == 0 ? null : resolved;
            if (!functionTypedefs.TryGetValue(name, out var raw))
                return null;
            functionTypes[name] = ""; // guards recursion
            string? type = FunctionType(raw.Result, raw.Parameters);
            if (type is null)
            {
                functionTypes.Remove(name);
                typedefs[name] = "void*"; // a function pointer the bindings cannot describe is carried as an untyped pointer
                return null;
            }
            functionTypes[name] = type;
            return type;
        }

        private string? FunctionType(string result, string parameters)
        {
            string? r = DescriptionType(result, parameter: false, out _);
            if (r is null)
                return null;
            var list = new List<string>();
            foreach (string piece in SplitParameters(parameters))
            {
                string p = piece.Trim();
                if (p.Length == 0 || p == "void")
                    continue;
                var (type, _) = SplitParameter(p, list.Count + 1);
                string? mapped = DescriptionType(type, parameter: false, out _);
                if (mapped is null || mapped.TrimEnd('?') is "str" or "String<u16>")
                    return null;
                list.Add(mapped);
            }
            return $"func*<{string.Join(", ", list)}> : {r}";
        }

        private static string Pointers(string type, int stars) => type + new string('*', stars);

        private static readonly HashSet<string> BasicTypeWords = new(StringComparer.Ordinal) { "unsigned", "short", "long", "int", "char", "double", "float" };

        /// <summary>
        /// C's basic type words in one spelling, whatever their order (`long unsigned int` is clang's `size_t`,
        /// `unsigned long long int`, `int long`): the spelling <see cref="Primitive"/> knows. Other words stay as written.
        /// </summary>
        private static string Canonical(List<string> words)
        {
            if (words.Count < 2 || !words.All(BasicTypeWords.Contains))
                return string.Join(' ', words);
            bool unsigned = words.Contains("unsigned");
            int longs = words.Count(w => w == "long");
            if (words.Contains("double"))
                return longs > 0 ? "long double" : "double";
            if (words.Contains("float"))
                return "float";
            if (words.Contains("char"))
                return unsigned ? "unsigned char" : "char";
            string width = words.Contains("short") ? "short" : longs >= 2 ? "long long" : longs == 1 ? "long" : "int";
            return unsigned ? "unsigned " + width : width;
        }

        /// <summary>C's own type names to the description's; char and wchar_t stay as such for the caller to decide.</summary>
        private string? Primitive(string c) => c switch
        {
            "void" => "void",
            "char" or "signed char" => "char", "unsigned char" => "u8",
            "wchar_t" => wcharIs32 ? "u32" : "wchar", "WCHAR" or "char16_t" => "wchar",
            "short" or "short int" or "signed short" or "signed short int" or "int16_t" or "SHORT" or "INT16" => "i16",
            "unsigned short" or "unsigned short int" or "uint16_t" or "USHORT" or "WORD" or "UINT16" => "u16",
            "int" or "signed int" or "int32_t" or "INT" or "INT32" or "BOOL" or "LONG32" or "errno_t" => "i32",
            "unsigned" or "unsigned int" or "uint32_t" or "UINT" or "UINT32" or "DWORD" or "ULONG32" => "u32",
            "long" or "long int" or "signed long" or "LONG" => longIs64 ? "i64" : "i32",
            "unsigned long" or "unsigned long int" or "ULONG" => longIs64 ? "u64" : "u32",
            "long long" or "long long int" or "signed long long" or "int64_t" or "__int64" or "LONGLONG" or "INT64" or "ptrdiff_t" or "intptr_t" or "ssize_t" or "LONG_PTR" or "INT_PTR" => "i64",
            "unsigned long long" or "unsigned long long int" or "uint64_t" or "unsigned __int64" or "ULONGLONG" or "UINT64" or "size_t" or "uintptr_t" or "rsize_t" or "ULONG_PTR" or "UINT_PTR" or "DWORD64" or "DWORD_PTR" or "SIZE_T" => "u64",
            "int8_t" or "INT8" => "i8", "uint8_t" or "BYTE" or "UINT8" or "UCHAR" => "u8",
            "float" or "FLOAT" => "f32", "double" or "DOUBLE" => "f64", "long double" => longDoubleIs64 ? "f64" : null,
            "bool" or "_Bool" or "BOOLEAN" => "bool",
            "HANDLE" or "HMODULE" or "HINSTANCE" or "HWND" or "FARPROC" or "LPVOID" or "PVOID" or "HGLOBAL" or "HLOCAL" or "HDC" => "void*",
            _ => null,
        };

        private static bool IsTypeWord(string w) => w is "int" or "char" or "short" or "long" or "unsigned" or "signed" or "float" or "double" or "void" or "bool" or "_Bool" or "wchar_t" or "const" or "volatile" or "struct" or "union" or "enum";

        private static List<string> Tokens(string text)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                {
                    current.Append(c);
                    continue;
                }
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                if (c == '*' || c == '(' || c == ')')
                    tokens.Add(c.ToString());
            }
            if (current.Length > 0)
                tokens.Add(current.ToString());
            return tokens;
        }

        private static IEnumerable<string> SplitParameters(string text) => BindingDescription.SplitTopLevel(text);

        private static int MatchingParenthesis(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static bool IsIdentifier(string w) => w.Length > 0 && (char.IsLetter(w[0]) || w[0] == '_') && w.All(c => char.IsLetterOrDigit(c) || c == '_');

        /// <summary>
        /// A C integer constant expression over literals (integer, character) and the constants read so far (`#define`s,
        /// enum members): arithmetic, shifts, bitwise and logical operators, comparisons, `?:`, parentheses and casts to
        /// integer types. Null when it is anything else (a string, a float, `sizeof`, an unknown name).
        /// </summary>
        private long? Evaluate(string expression)
        {
            if (Integer(expression) is { } literal)
                return literal;
            var evaluator = new ConstantExpression(expression, constantValues);
            return evaluator.Evaluate();
        }

        /// <summary>A recursive-descent evaluator of C integer constant expressions (C precedence, 64-bit arithmetic).</summary>
        private sealed class ConstantExpression(string text, Dictionary<string, long> constants)
        {
            private int at;
            private bool failed;

            public long? Evaluate()
            {
                long value = Conditional();
                SkipSpaces();
                return failed || at < text.Length ? null : value;
            }

            private void SkipSpaces()
            {
                while (at < text.Length && char.IsWhiteSpace(text[at]))
                    at++;
            }

            private bool Take(string op)
            {
                SkipSpaces();
                if (string.CompareOrdinal(text, at, op, 0, op.Length) != 0)
                    return false;
                // `<` is not `<<` or `<=`, `&` not `&&`, `|` not `||`, `=` only as `==`
                char next = at + op.Length < text.Length ? text[at + op.Length] : '\0';
                if (op is "<" or ">" && (next == op[0] || next == '=') || op is "&" or "|" && next == op[0] || op is "!" && next == '=')
                    return false;
                at += op.Length;
                return true;
            }

            private long Conditional()
            {
                long condition = Binary(0);
                if (!Take("?"))
                    return condition;
                long whenTrue = Conditional();
                if (!Take(":"))
                    failed = true;
                long whenFalse = Conditional();
                return condition != 0 ? whenTrue : whenFalse;
            }

            private static readonly string[][] Levels =
            [
                ["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<=", ">=", "<", ">"], ["<<", ">>"], ["+", "-"], ["*", "/", "%"],
            ];

            private long Binary(int level)
            {
                if (level == Levels.Length)
                    return Unary();
                long left = Binary(level + 1);
                while (true)
                {
                    string? op = Levels[level].FirstOrDefault(Take);
                    if (op is null)
                        return left;
                    long right = Binary(level + 1);
                    left = op switch
                    {
                        "||" => left != 0 || right != 0 ? 1 : 0,
                        "&&" => left != 0 && right != 0 ? 1 : 0,
                        "|" => left | right,
                        "^" => left ^ right,
                        "&" => left & right,
                        "==" => left == right ? 1 : 0,
                        "!=" => left != right ? 1 : 0,
                        "<=" => left <= right ? 1 : 0,
                        ">=" => left >= right ? 1 : 0,
                        "<" => left < right ? 1 : 0,
                        ">" => left > right ? 1 : 0,
                        "<<" => right is >= 0 and < 64 ? left << (int)right : Fail(),
                        ">>" => right is >= 0 and < 64 ? left >> (int)right : Fail(),
                        "+" => left + right,
                        "-" => left - right,
                        "*" => left * right,
                        "/" => right != 0 ? left / right : Fail(),
                        _ => right != 0 ? left % right : Fail(),
                    };
                }
            }

            private long Fail()
            {
                failed = true;
                return 0;
            }

            private long Unary()
            {
                if (Take("-"))
                    return -Unary();
                if (Take("+"))
                    return Unary();
                if (Take("~"))
                    return ~Unary();
                if (Take("!"))
                    return Unary() == 0 ? 1 : 0;
                SkipSpaces();
                if (at < text.Length && text[at] == '(')
                {
                    int close = MatchingParenthesis(text, at);
                    if (close < 0)
                        return Fail();
                    string inside = text[(at + 1)..close].Trim();
                    // a cast to an integer type: `(DWORD)`, `(unsigned long)`, `(int32_t)`
                    if (inside.Length > 0 && inside.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => IsTypeWord(w) || IsIdentifier(w) && !constants.ContainsKey(w)) && !inside.Contains('*'))
                    {
                        at = close + 1;
                        return Unary();
                    }
                    at++;
                    long value = Conditional();
                    if (!Take(")"))
                        return Fail();
                    return value;
                }
                return Primary();
            }

            private long Primary()
            {
                SkipSpaces();
                if (at >= text.Length)
                    return Fail();
                char c = text[at];
                if (c == '\'')
                {
                    int end = text.IndexOf('\'', at + 1);
                    if (end < 0 || end == at + 1)
                        return Fail();
                    string inside = text[(at + 1)..end];
                    at = end + 1;
                    return inside.Length == 1 ? inside[0] : inside is ['\\', var escaped] ? escaped switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => escaped } : Fail();
                }
                int start = at;
                while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_'))
                    at++;
                string word = text[start..at];
                if (word.Length == 0)
                    return Fail();
                if (char.IsDigit(word[0]))
                    return Integer(word) ?? Fail();
                if (constants.TryGetValue(word, out long known))
                    return known;
                // a constant-making macro around a literal: `UINT64_C(0x20)`
                if (word.EndsWith("_C", StringComparison.Ordinal) && Take("("))
                {
                    long value = Conditional();
                    return Take(")") ? value : Fail();
                }
                return Fail(); // sizeof, a type, an unknown name
            }
        }

        /// <summary>An integer literal (decimal, hex, octal `0755`, binary `0b101`, with C suffixes, in parentheses, negated), or null.</summary>
        private static long? Integer(string text)
        {
            text = text.Trim();
            while (text.StartsWith('(') && text.EndsWith(')'))
                text = text[1..^1].Trim();
            bool negative = text.StartsWith('-');
            if (negative)
                text = text[1..].Trim();
            text = text.TrimEnd('u', 'U', 'l', 'L');
            long value;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value))
                    return null;
            }
            else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                if (text.Length < 3 || text.Length > 66 || !text[2..].All(c => c is '0' or '1'))
                    return null;
                value = Convert.ToInt64(text[2..], 2);
            }
            else if (text.Length > 1 && text[0] == '0' && text.All(char.IsAsciiDigit))
            {
                // C's octal: a leading zero (`0755` is 493).
                if (text.Length > 22 || !text.All(c => c is >= '0' and <= '7'))
                    return null;
                value = Convert.ToInt64(text, 8);
            }
            else if (!long.TryParse(text, out value))
                return null;
            return negative ? -value : value;
        }
    }
}
