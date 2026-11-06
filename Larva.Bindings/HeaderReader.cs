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
        private readonly bool longIs64;

        /// <param name="longIs64">Whether C's <c>long</c> is 64 bits (LP64, Linux and macOS) or 32 (Windows).</param>
        public HeaderReader(bool? longIs64 = null) => this.longIs64 = longIs64 ?? !OperatingSystem.IsWindows();

        /// <summary>Reads <paramref name="header"/> (its text) for <paramref name="library"/>: the description text.</summary>
        /// <summary>
        /// Where the `#define`s worth reading are (the header's folder and the include folders given): a preprocessed header
        /// (clang `-E -dD`) keeps every macro, the system's and the compiler's too, each after a line marker naming its file.
        /// Empty: every file.
        /// </summary>
        public List<string> DefineRoots { get; } = [];

        private static readonly System.Text.RegularExpressions.Regex LineMarker = new(@"^#\s*(?:line\s+)?\d+\s+\x22([^\x22]*)\x22", System.Text.RegularExpressions.RegexOptions.Compiled);

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
                    file = marker.Groups[1].Value.Replace("\\\\", "/").Replace('\\', '/');
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
            var declarations = Declarations(code).Select(d => d.StartsWith(FileMarker, StringComparison.Ordinal) ? d : Callable.Replace(Decorations.Replace(d, " "), "(*").Trim())
                .Where(d => d.Length > 0).ToList();
            // Types first, prototypes after: a prototype may use a typedef written below it in another header.
            foreach (string declaration in declarations)
            {
                if (declaration.StartsWith(FileMarker, StringComparison.Ordinal))
                    currentFile = declaration[FileMarker.Length..];
                else if (IsTypeDeclaration(declaration))
                    Declaration(declaration);
            }
            currentFile = null;
            foreach (string declaration in declarations)
            {
                if (declaration.StartsWith(FileMarker, StringComparison.Ordinal))
                    currentFile = declaration[FileMarker.Length..];
                else if (!IsTypeDeclaration(declaration))
                    Declaration(declaration);
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
                        .Append(!definition.IsUnion && definition.Pack is > 0 and < 8 ? $" pack {definition.Pack}" : "").Append(" { ").Append(string.Join("; ", fields.Select(f => $"{f.Type} {f.Name}"))).Append(" }\n");
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
            var start = new System.Diagnostics.ProcessStartInfo(clang) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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
            if (Integer(body) is { } value && IsIdentifier(parts[1]))
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

        /// <summary>Top-level declarations (ending in <c>;</c>), bodies <c>{ … }</c> of functions skipped, directives dropped.</summary>
        private static List<string> Declarations(string code)
        {
            var declarations = new List<string>();
            var current = new StringBuilder();
            int depth = 0, parens = 0;
            bool directive = false, lineStart = true;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
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
                        string line = code[lineStart2..i].Trim();
                        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^#\s*pragma\s+pack\s*\("))
                            declarations.Add(PackMarker + line[(line.IndexOf('(') + 1)..line.LastIndexOf(')')]);
                        else if (LineMarker.Match(line) is { Success: true } marker)
                            declarations.Add(FileMarker + marker.Groups[1].Value.Replace("\\\\", "/").Replace('\\', '/'));
                    }
                    continue;
                }
                if (c == '{')
                {
                    depth++;
                    current.Append(c);
                    continue;
                }
                if (c == '}')
                {
                    depth--;
                    current.Append(c);
                    // A function definition: its body ends the declaration without a `;`.
                    if (depth == 0 && parens == 0 && !current.ToString().TrimStart().StartsWith("typedef") && !StartsWithAggregate(current.ToString()))
                    {
                        declarations.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }
                if (c == '(') parens++;
                else if (c == ')') parens--;
                if (c == ';' && depth == 0)
                {
                    declarations.Add(current.ToString());
                    current.Clear();
                    continue;
                }
                current.Append(c == '\n' ? ' ' : c);
            }
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
            if (t.StartsWith("typedef ") || t.StartsWith("enum ") || t.StartsWith("enum{"))
                return true;
            if (!StartsWithAggregate(t))
                return false;
            int brace = t.IndexOf('{'), paren = t.IndexOf('(');
            return paren < 0 || brace >= 0 && brace < paren;
        }

        private static readonly System.Text.RegularExpressions.Regex Decorations = new(
            @"__declspec\s*\([^)]*\)|__attribute__\s*\(\(.*?\)\)|__pragma\s*\([^)]*\)|\b(extern|static|inline|__inline|__forceinline|__inline__|_Noreturn|__restrict|restrict|__unaligned)\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>`(__cdecl *name)`, `( * name)`: a function pointer's declarator, written `(*name)` for the rest of the reader.</summary>
        private static readonly System.Text.RegularExpressions.Regex Callable = new(
            @"\(\s*(?:(?:__cdecl|__stdcall|__fastcall|__vectorcall|WINAPI|CALLBACK|APIENTRY|SDLCALL)\s*)*\*\s*",
            System.Text.RegularExpressions.RegexOptions.Compiled);

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
            if (t.StartsWith("enum ") || t.StartsWith("enum{"))
            {
                int brace = t.IndexOf('{');
                string tag = brace < 0 ? "" : t[4..brace].Trim();
                Enum(t, IsIdentifier(tag) ? [tag] : []);
                return;
            }
            if (IsTypeDeclaration(t))
            {
                int brace = t.IndexOf('{');
                string tag = (brace < 0 ? t : t[..brace]).Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
                if (tag.Length == 0)
                    return;
                if (brace >= 0)
                    DefineStruct(tag, tag, t[(brace + 1)..t.LastIndexOf('}')], t.StartsWith("union "));
                else if (!structNames.ContainsKey(tag))
                    opaque.Add(tag); // a forward declaration: opaque unless a body follows
                return;
            }
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
                DefineStruct(structName, tag, t[(brace + 1)..close], head.StartsWith("union"));
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

        private void DefineStruct(string name, string tag, string body, bool isUnion = false)
        {
            var raw = new RawStruct { Name = name, Fields = [], IsUnion = isUnion, Pack = pack };
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
                        int open = declaration.IndexOf('{'), close = declaration.LastIndexOf('}');
                        bool nestedUnion = declaration.StartsWith("union");
                        var declarators = Declarators(declaration[(close + 1)..]);
                        if (declarators.Count == 0)
                            declarators.Add(($"anon{anonymous++}", 0));
                        string nested = $"{name}_{declarators[0].Name}";
                        DefineStruct(nested, "", declaration[(open + 1)..close], nestedUnion);
                        foreach (var (fieldName, stars) in declarators)
                            raw.Fields.Add(Field(nested, stars, declaration[(close + 1)..], fieldName));
                        continue;
                    }
                    if (declaration.Contains(':'))
                    {
                        raw.Why = "bitfields";
                        break;
                    }
                    if (declaration.Contains("(*"))
                    {
                        // a function pointer field: `ret (*name)(params)`
                        var (type, fieldName) = SplitParameter(declaration, raw.Fields.Count + 1);
                        raw.Fields.Add(new RawField(type, fieldName, null));
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
                    long? n = Integer(inside) ?? (constantValues.TryGetValue(inside, out long known) ? known : null);
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
            long next = 0;
            foreach (string piece in t[(open + 1)..close].Split(','))
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
                    if (Integer(expression) is { } given)
                        next = given;
                    else if (constantValues.TryGetValue(expression, out long known))
                        next = known;
                    else
                        continue; // an expression: not followed
                }
                Constant(name, next);
                members.Add((name, next));
                next++;
            }
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
                string name = Tokens(p[(pointerOpen + 2)..pointerClose]).LastOrDefault(IsIdentifier) ?? $"p{index}";
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
            // typedef aliases, followed to the end
            for (int guard = 0; guard < 16 && words.Count == 1 && typedefs.TryGetValue(words[0], out string? target); guard++)
            {
                var targetWords = Tokens(target);
                stars += targetWords.Count(w => w == "*");
                words = targetWords.Where(w => w is not ("const" or "volatile" or "*")).ToList();
                baseText = string.Join(' ', words);
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
            string? primitive = Primitive(baseText);
            if (primitive is null)
            {
                why = $"unknown type `{baseText}`";
                return null;
            }
            if (primitive == "char" || primitive == "wchar")
            {
                // char* as a parameter is a C string (a copy with its terminator; null when C takes NULL), as a result a
                // pointer to bytes.
                if (stars == 1 && parameter)
                    return primitive == "char" ? "str?" : "String<u16>?";
                return Pointers(primitive == "char" ? "i8" : "u16", stars);
            }
            if (primitive == "void" && stars == 0 && (parameter || field))
            {
                why = "void";
                return null;
            }
            return Pointers(primitive, stars);
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

        /// <summary>C's own type names to the description's; char and wchar_t stay as such for the caller to decide.</summary>
        private string? Primitive(string c) => c switch
        {
            "void" => "void",
            "char" or "signed char" => "char", "unsigned char" => "u8",
            "wchar_t" or "WCHAR" or "char16_t" => "wchar",
            "short" or "short int" or "signed short" or "signed short int" or "int16_t" or "SHORT" or "INT16" => "i16",
            "unsigned short" or "unsigned short int" or "uint16_t" or "USHORT" or "WORD" or "UINT16" => "u16",
            "int" or "signed int" or "int32_t" or "INT" or "INT32" or "BOOL" or "LONG32" or "errno_t" => "i32",
            "unsigned" or "unsigned int" or "uint32_t" or "UINT" or "UINT32" or "DWORD" or "ULONG32" => "u32",
            "long" or "long int" or "signed long" or "LONG" => longIs64 ? "i64" : "i32",
            "unsigned long" or "unsigned long int" or "ULONG" => longIs64 ? "u64" : "u32",
            "long long" or "long long int" or "signed long long" or "int64_t" or "__int64" or "LONGLONG" or "INT64" or "ptrdiff_t" or "intptr_t" or "ssize_t" or "LONG_PTR" or "INT_PTR" => "i64",
            "unsigned long long" or "unsigned long long int" or "uint64_t" or "unsigned __int64" or "ULONGLONG" or "UINT64" or "size_t" or "uintptr_t" or "rsize_t" or "ULONG_PTR" or "UINT_PTR" or "DWORD64" or "DWORD_PTR" or "SIZE_T" => "u64",
            "int8_t" or "INT8" => "i8", "uint8_t" or "BYTE" or "UINT8" or "UCHAR" => "u8",
            "float" or "FLOAT" => "f32", "double" or "DOUBLE" or "long double" => "f64",
            "bool" or "_Bool" or "BOOLEAN" => "bool",
            "HANDLE" or "HMODULE" or "HINSTANCE" or "HWND" or "FARPROC" or "LPVOID" or "PVOID" or "HGLOBAL" or "HLOCAL" or "HDC" => "void*",
            "LPCSTR" or "LPSTR" or "PCSTR" or "PSTR" => "char*",
            "LPCWSTR" or "LPWSTR" or "PCWSTR" or "PWSTR" => "wchar*",
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

        /// <summary>An integer literal (decimal, hex, with C suffixes, in parentheses, negated), or null.</summary>
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
            else if (!long.TryParse(text, out value))
                return null;
            return negative ? -value : value;
        }
    }
}
