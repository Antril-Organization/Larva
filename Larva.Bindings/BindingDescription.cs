using Larva.Hir;

namespace Larva.Bindings
{
    /// <summary>
    /// Where a declaration goes: its namespace, the static class that holds it (functions and constants) and the source
    /// file it is written to. Null: the description's own (<see cref="BindingDescription.Namespace"/>, <see cref="BindingDescription.Class"/>, one file).
    /// </summary>
    public interface IPlaced
    {
        string? Namespace { get; }
        string? File { get; }
    }

    /// <summary>One imported function: the C symbol, the binding's name, parameters and result (types as written).</summary>
    public sealed record BindingFunction(string Entry, string Name, IReadOnlyList<(string Name, string Type)> Parameters, string Result) : IPlaced
    {
        public string? Namespace { get; init; }
        public string? Class { get; init; }
        public string? File { get; init; }
    }

    /// <summary>A C struct with its layout: the fields in order (types as written), laid out as C does (sequential, natural alignment).</summary>
    public sealed record BindingStruct(string Name, IReadOnlyList<(string Name, string Type)> Fields, bool IsUnion = false, int Pack = 0) : IPlaced
    {
        public string? Namespace { get; init; }
        public string? File { get; init; }
    }

    /// <summary>An integer constant (a `#define`, or an enum value no enum took), kept in a static class.</summary>
    public sealed record BindingConstant(string Name, long Value) : IPlaced
    {
        public string? Namespace { get; init; }
        public string? Class { get; init; }
        public string? File { get; init; }
    }

    /// <summary>An enum (or, <see cref="IsFlags"/>, a bitflag enum) over a number type, with its members' values.</summary>
    public sealed record BindingEnum(string Name, string Underlying, bool IsFlags, IReadOnlyList<(string Name, long Value)> Members) : IPlaced
    {
        public string? Namespace { get; init; }
        public string? File { get; init; }
    }

    /// <summary>
    /// The side description of a library's API, since a binary gives symbol names but no signatures. A text file:
    /// <code>
    /// # comments
    /// library kernel32.dll
    /// assembly Kernel32Bindings        (optional: the class name + "Bindings")
    /// namespace Bindings               (optional)
    /// class Kernel32                   (optional: from the library's name)
    /// GetCurrentProcessId() : u32
    /// Sleep(u32 milliseconds)
    /// GetModuleHandleW(u16* name) : void*
    /// lstrlenW(String&lt;u16&gt; text) : i32
    /// qsort(void* items, u64 count, u64 size, func*&lt;void*, void*&gt; : i32 compare)
    /// Beep as MakeBeep(u32 frequency, u32 duration) : bool
    /// const MAX_PATH = 260
    /// struct FILETIME { u32 dwLowDateTime; u32 dwHighDateTime }
    /// union LARGE_INTEGER { FILETIME parts; i64 QuadPart }
    /// struct Packed pack 1 { u8 tag; u32 value; u8[16] name }
    /// GetSystemTimeAsFileTime(FILETIME* time)
    /// enum Mode : i32 { Read = 1, Write = 2 }
    /// enum Access : u32 flags { Read = 1, Write = 2, Execute = 4 }
    /// namespace Win32.Time            (from here on: the namespace of what follows)
    /// class Time                      (from here on: the class of the functions and constants that follow)
    /// file Time.ant                   (from here on: the source file what follows is written to)
    /// </code>
    /// An enum is a type of its own: functions and fields name it by value (<c>Mode mode</c>) or by pointer. The first
    /// <c>namespace</c> and <c>class</c> are the description's defaults.
    /// A <c>union</c> becomes a value with <c>[ValueLayout(Layout.Explicit)]</c> and every member at
    /// <c>[FieldOffset(0)]</c>; <c>pack N</c> (C's <c>#pragma pack</c>) a <c>[ValueLayout(Layout.Sequential, Pack = N)]</c>;
    /// a field <c>T[N]</c> an inline array (<c>fixed T[N]</c>).
    /// A <c>struct</c> becomes a value type with public fields in declaration order (C's layout); functions take and
    /// give pointers to it (<c>Name*</c>), and other structs hold it by value (declared before) or point to it (anywhere).
    /// Types: the numbers (<c>i8</c> … <c>u64</c>, <c>f32</c>, <c>f64</c>), <c>bool</c>, <c>void</c>, pointers to them
    /// (<c>void*</c>, <c>u8*</c>), C function pointers <c>func*&lt;params&gt; : result</c>, and as parameters the strings
    /// <c>str</c> (a UTF-8 copy with a terminator) and <c>String&lt;u16&gt;</c> (UTF-16): what a language's binder lets cross to C.
    /// <c>as</c> renames the generated method; the entry stays.
    /// </summary>
    public sealed class BindingDescription
    {
        public string Library { get; set; } = "";
        public string Assembly { get; set; } = "";
        public string Namespace { get; set; } = "Bindings";
        public string Class { get; set; } = "";
        public List<BindingFunction> Functions { get; } = [];

        /// <summary>Integer constants (<c>const NAME = value</c>: a header's <c>#define</c>s and enum values).</summary>
        public List<BindingConstant> Constants { get; } = [];

        /// <summary>Enums, in declaration order.</summary>
        public List<BindingEnum> Enums { get; } = [];

        /// <summary>
        /// Leaves out the functions <paramref name="exports"/> does not name (a header declares more than a build of the
        /// library exports: other platforms, inline helpers): the names left out.
        /// </summary>
        public List<string> KeepExported(IReadOnlyList<string> exports)
        {
            var known = new HashSet<string>(exports, StringComparer.Ordinal);
            var missing = Functions.Where(f => !known.Contains(f.Entry)).Select(f => f.Entry).ToList();
            Functions.RemoveAll(f => !known.Contains(f.Entry));
            return missing;
        }

        /// <summary>Structs with layout, in declaration order.</summary>
        public List<BindingStruct> Structs { get; } = [];

        public static BindingDescription Parse(string text)
        {
            var description = new BindingDescription();
            var probe = new HirAssembly { Name = "probe" }; // types are checked now, resolved again into the real assembly
            var probeStructs = new Dictionary<string, TypeToken>(StringComparer.Ordinal);
            var probeEnums = new Dictionary<string, TypeToken>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                // Every struct's and enum's name first: a struct may point to one declared below it.
                string line = raw.Trim();
                if ((line.StartsWith("struct ") || line.StartsWith("union ")) && line.IndexOf('{') is var brace && brace > 6)
                {
                    string name = line[(line.StartsWith("struct ") ? 7 : 6)..brace].Trim().Split(' ')[0];
                    if (name.Length > 0 && seen.Add(name))
                        probeStructs[name] = probe.Add(new TypeDef { Name = name, Kind = TypeKind.Struct });
                }
                else if (line.StartsWith("enum ") && line.IndexOf('{') is var open && open > 5)
                {
                    string name = line[5..open].Trim().Split(' ', ':')[0];
                    if (name.Length > 0 && seen.Add(name))
                        probeEnums[name] = probe.Add(new TypeDef { Name = name, Kind = TypeKind.Enum });
                }
            }
            string? currentNamespace = null, currentClass = null, currentFile = null;
            bool namespaceGiven = false;
            int lineNumber = 0;
            foreach (string raw in text.Split('\n'))
            {
                lineNumber++;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                string[] words = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                switch (words[0])
                {
                    case "library": description.Library = Value(words, lineNumber); continue;
                    case "assembly": description.Assembly = Value(words, lineNumber); continue;
                    case "namespace":
                        currentNamespace = Value(words, lineNumber);
                        if (!namespaceGiven)
                            description.Namespace = currentNamespace;
                        namespaceGiven = true;
                        continue;
                    case "class":
                        currentClass = Value(words, lineNumber);
                        if (description.Class.Length == 0)
                            description.Class = currentClass;
                        continue;
                    case "file": currentFile = Value(words, lineNumber); continue;
                    case "enum":
                    {
                        var parsed = ParseEnum(Value(words, lineNumber), lineNumber) with { Namespace = currentNamespace, File = currentFile };
                        if (description.Enums.Any(e => e.Name == parsed.Name) || description.Structs.Any(s => s.Name == parsed.Name))
                            throw new BindingException($"line {lineNumber}: `{parsed.Name}` is declared twice");
                        description.Enums.Add(parsed);
                        continue;
                    }
                    case "struct" or "union":
                    {
                        var definition = ParseStruct(Value(words, lineNumber), lineNumber, words[0] == "union") with { Namespace = currentNamespace, File = currentFile };
                        if (description.Structs.Any(s => s.Name == definition.Name))
                            throw new BindingException($"line {lineNumber}: struct `{definition.Name}` is declared twice");
                        foreach (var (_, type) in definition.Fields)
                        {
                            string element = type.TrimEnd('*');
                            if (element.Length == type.Length && element != definition.Name && probeStructs.ContainsKey(element) && !description.Structs.Any(s => s.Name == element))
                                throw new BindingException($"line {lineNumber}: struct `{element}` is held by value before it is declared");
                            ParseType(type, lineNumber, probe, parameter: false, probeStructs, field: true, probeEnums);
                        }
                        description.Structs.Add(definition);
                        continue;
                    }
                    case "const":
                    {
                        string[] parts = Value(words, lineNumber).Split('=', 2);
                        if (parts.Length != 2 || !long.TryParse(parts[1].Trim(), out long value))
                            throw new BindingException($"line {lineNumber}: a constant is `const NAME = integer`");
                        description.Constants.Add(new BindingConstant(parts[0].Trim(), value) { Namespace = currentNamespace, Class = currentClass, File = currentFile });
                        continue;
                    }
                }
                var function = ParseFunction(line, lineNumber) with { Namespace = currentNamespace, Class = currentClass, File = currentFile };
                foreach (var (_, type) in function.Parameters)
                    ParseType(type, lineNumber, probe, parameter: true, probeStructs, enums: probeEnums);
                ParseType(function.Result, lineNumber, probe, parameter: false, probeStructs, enums: probeEnums);
                description.Functions.Add(function);
            }
            if (description.Library.Length == 0)
                throw new BindingException("the description names no `library`");
            if (description.Class.Length == 0)
            {
                string stem = Path.GetFileNameWithoutExtension(description.Library);
                if (stem.StartsWith("lib", StringComparison.Ordinal) && stem.Length > 3)
                    stem = stem[3..];
                description.Class = char.ToUpperInvariant(stem[0]) + stem[1..];
            }
            if (description.Assembly.Length == 0)
                description.Assembly = description.Class + "Bindings";
            return description;
        }

        private static string Value(string[] words, int line) =>
            words.Length == 2 ? words[1].Trim() : throw new BindingException($"line {line}: `{words[0]}` needs a value");

        /// <summary><c>Entry [as Name](type name, …)[ : result]</c>.</summary>
        private static BindingFunction ParseFunction(string line, int lineNumber)
        {
            int open = line.IndexOf('(');
            if (open < 0)
                throw new BindingException($"line {lineNumber}: a function is `Name(type param, …) : result`");
            int close = MatchingParenthesis(line, open);
            if (close < 0)
                throw new BindingException($"line {lineNumber}: missing `)`");
            string head = line[..open].Trim();
            string entry = head, name = head;
            int rename = head.IndexOf(" as ", StringComparison.Ordinal);
            if (rename >= 0)
            {
                entry = head[..rename].Trim();
                name = head[(rename + 4)..].Trim();
            }
            if (entry.Length == 0 || name.Length == 0)
                throw new BindingException($"line {lineNumber}: the function has no name");
            var parameters = new List<(string, string)>();
            string inside = line[(open + 1)..close].Trim();
            if (inside.Length > 0)
            {
                foreach (string piece in SplitTopLevel(inside))
                {
                    string declaration = piece.Trim();
                    int space = declaration.LastIndexOf(' ');
                    if (space < 0)
                        throw new BindingException($"line {lineNumber}: a parameter is `type name`, not `{declaration}`");
                    parameters.Add((declaration[(space + 1)..], declaration[..space].Trim()));
                }
            }
            string tail = line[(close + 1)..].Trim();
            string result = "void";
            if (tail.Length > 0)
            {
                if (!tail.StartsWith(':'))
                    throw new BindingException($"line {lineNumber}: after the parameters comes `: result` or nothing");
                result = tail[1..].Trim();
            }
            return new BindingFunction(entry, name, parameters, result);
        }

        /// <summary><c>Name : u32 [flags] { A = 1, B = 2 }</c> (the base is i32 when left out).</summary>
        private static BindingEnum ParseEnum(string text, int lineNumber)
        {
            int open = text.IndexOf('{'), close = text.LastIndexOf('}');
            if (open < 0 || close < open)
                throw new BindingException($"line {lineNumber}: an enum is `enum Name : u32 [flags] {{ A = 1, B = 2 }}`");
            string[] head = text[..open].Replace(":", " : ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string name = head.Length > 0 ? head[0] : "";
            string underlying = "i32";
            bool flags = false;
            for (int i = 1; i < head.Length; i++)
            {
                if (head[i] == ":" && i + 1 < head.Length)
                    underlying = head[++i];
                else if (head[i] == "flags")
                    flags = true;
                else
                    throw new BindingException($"line {lineNumber}: `{head[i]}` in an enum's head (`enum Name : u32 [flags] {{ … }}`)");
            }
            if (name.Length == 0 || underlying is not ("i8" or "u8" or "i16" or "u16" or "i32" or "u32" or "i64" or "u64"))
                throw new BindingException($"line {lineNumber}: an enum has a name and an integer base, not `{text[..open].Trim()}`");
            var members = new List<(string, long)>();
            foreach (string piece in text[(open + 1)..close].Split(','))
            {
                string member = piece.Trim();
                if (member.Length == 0)
                    continue;
                string[] parts = member.Split('=', 2);
                if (parts.Length != 2 || !long.TryParse(parts[1].Trim(), out long value))
                    throw new BindingException($"line {lineNumber}: an enum member is `Name = integer`, not `{member}`");
                members.Add((parts[0].Trim(), value));
            }
            return new BindingEnum(name, underlying, flags, members);
        }

        /// <summary>The description as text again (what <see cref="Parse"/> reads), with each placement change as a line.</summary>
        public string ToText()
        {
            var text = new System.Text.StringBuilder();
            text.Append("library ").Append(Library).Append('\n');
            if (Assembly.Length > 0)
                text.Append("assembly ").Append(Assembly).Append('\n');
            text.Append("namespace ").Append(Namespace).Append('\n');
            text.Append("class ").Append(Class).Append('\n');
            string? ns = Namespace, cls = Class, file = null;
            void Place(string? itemNamespace, string? itemClass, string? itemFile)
            {
                string wantNamespace = itemNamespace ?? Namespace, wantClass = itemClass ?? Class;
                if (itemFile != file && itemFile is not null)
                    text.Append("file ").Append(file = itemFile).Append('\n');
                if (wantNamespace != ns)
                    text.Append("namespace ").Append(ns = wantNamespace).Append('\n');
                if (wantClass != cls)
                    text.Append("class ").Append(cls = wantClass).Append('\n');
            }
            foreach (var e in Enums)
            {
                Place(e.Namespace, null, e.File);
                text.Append("enum ").Append(e.Name).Append(" : ").Append(e.Underlying).Append(e.IsFlags ? " flags" : "").Append(" { ")
                    .Append(string.Join(", ", e.Members.Select(m => $"{m.Name} = {m.Value}"))).Append(" }\n");
            }
            foreach (var s in Structs)
            {
                Place(s.Namespace, null, s.File);
                text.Append(s.IsUnion ? "union " : "struct ").Append(s.Name).Append(!s.IsUnion && s.Pack > 0 ? $" pack {s.Pack}" : "").Append(" { ")
                    .Append(string.Join("; ", s.Fields.Select(f => $"{f.Type} {f.Name}"))).Append(" }\n");
            }
            foreach (var c in Constants)
            {
                Place(c.Namespace, c.Class, c.File);
                text.Append("const ").Append(c.Name).Append(" = ").Append(c.Value).Append('\n');
            }
            foreach (var f in Functions)
            {
                Place(f.Namespace, f.Class, f.File);
                text.Append(f.Entry).Append(f.Name != f.Entry ? $" as {f.Name}" : "").Append('(')
                    .Append(string.Join(", ", f.Parameters.Select(p => $"{p.Type} {p.Name}"))).Append(')')
                    .Append(f.Result == "void" ? "" : $" : {f.Result}").Append('\n');
            }
            return text.ToString();
        }

        /// <summary><c>Name { type field; type field }</c>.</summary>
        private static BindingStruct ParseStruct(string text, int lineNumber, bool isUnion)
        {
            int open = text.IndexOf('{'), close = text.LastIndexOf('}');
            string[] head = open < 0 ? [] : text[..open].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string name = head.Length > 0 ? head[0] : "";
            int pack = 0;
            if (head.Length == 3 && head[1] == "pack" && int.TryParse(head[2], out int packed) && packed is 1 or 2 or 4 or 8 or 16)
                pack = packed;
            else if (head.Length != 1)
                name = "";
            if (open < 0 || close < open || name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
                throw new BindingException($"line {lineNumber}: a struct is `struct Name [pack N] {{ type field; … }}`");
            var fields = new List<(string, string)>();
            foreach (string piece in text[(open + 1)..close].Split(';'))
            {
                string declaration = piece.Trim();
                if (declaration.Length == 0)
                    continue;
                int space = declaration.LastIndexOf(' ');
                if (space < 0)
                    throw new BindingException($"line {lineNumber}: a field is `type name`, not `{declaration}`");
                fields.Add((declaration[(space + 1)..], declaration[..space].Trim()));
            }
            return new BindingStruct(name, fields, isUnion, pack);
        }

        /// <summary>Size and alignment of a description type as C lays it out (a struct sequential, a union its largest member).</summary>
        public static (int Size, int Align) Layout(string type, IReadOnlyList<BindingStruct> structs, IReadOnlyList<BindingEnum>? enums = null)
        {
            type = type.Trim();
            if (type.EndsWith(']') && type.LastIndexOf('[') is var bracket && bracket > 0 && int.TryParse(type[(bracket + 1)..^1], out int count))
            {
                var (elementSize, elementAlign) = Layout(type[..bracket], structs, enums);
                return (elementSize * count, elementAlign);
            }
            if (type.EndsWith('*') || type.StartsWith("func*", StringComparison.Ordinal))
                return (8, 8);
            switch (type)
            {
                case "i8" or "u8" or "bool": return (1, 1);
                case "i16" or "u16": return (2, 2);
                case "i32" or "u32" or "f32": return (4, 4);
                case "i64" or "u64" or "f64": return (8, 8);
            }
            if (structs.FirstOrDefault(s => s.Name == type) is not { } definition)
            {
                if (enums?.FirstOrDefault(e => e.Name == type) is { } enumeration)
                    return Layout(enumeration.Underlying, structs);
                throw new BindingException($"`{type}` has no layout");
            }
            int size = 0, align = 1;
            foreach (var (_, fieldType) in definition.Fields)
            {
                var (fieldSize, fieldAlign) = Layout(fieldType, structs, enums);
                if (definition.Pack > 0)
                    fieldAlign = Math.Min(fieldAlign, definition.Pack);
                align = Math.Max(align, fieldAlign);
                if (definition.IsUnion)
                    size = Math.Max(size, fieldSize);
                else
                    size = (size + fieldAlign - 1) / fieldAlign * fieldAlign + fieldSize;
            }
            return ((size + align - 1) / align * align, align);
        }

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

        /// <summary>Comma-separated pieces, commas inside <c>&lt;…&gt;</c> or <c>(…)</c> not counting.</summary>
        public static List<string> SplitTopLevel(string text)
        {
            var pieces = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is '<' or '(') depth++;
                else if (c is '>' or ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    pieces.Add(text[start..i]);
                    start = i + 1;
                }
            }
            pieces.Add(text[start..]);
            return pieces;
        }

        /// <summary>
        /// A type as written, into <paramref name="assembly"/> (strings become references to System.Core's String).
        /// Strings are parameters only (a C function gives a pointer, not a string of this side).
        /// </summary>
        public static HirType ParseType(string text, int lineNumber, HirAssembly assembly, bool parameter, IReadOnlyDictionary<string, TypeToken>? structs = null, bool field = false,
            IReadOnlyDictionary<string, TypeToken>? enums = null)
        {
            text = text.Trim();
            // `T[N]` as a field: an inline array (C's `T name[N]`).
            if (field && text.EndsWith(']') && text.LastIndexOf('[') is var open && open > 0)
            {
                if (!int.TryParse(text[(open + 1)..^1], out int count) || count <= 0)
                    throw new BindingException($"line {lineNumber}: an array field is `T[N]` with N above 0, not `{text}`");
                return new InlineArrayType(ParseType(text[..open], lineNumber, assembly, parameter: false, structs, field: true, enums), count);
            }
            if (text.StartsWith("func*<", StringComparison.Ordinal))
            {
                int close = MatchingAngle(text, 5);
                if (close < 0)
                    throw new BindingException($"line {lineNumber}: missing `>` in `{text}`");
                string inside = text[6..close].Trim();
                var parameters = inside.Length == 0 ? [] : SplitTopLevel(inside).Select(p => ParseType(p, lineNumber, assembly, parameter: false, structs, enums: enums)).ToArray();
                string rest = text[(close + 1)..].Trim();
                if (!rest.StartsWith(':'))
                    throw new BindingException($"line {lineNumber}: a `func*<…>` says its result: `func*<i32> : i32`");
                return new NativeFuncType(parameters, ParseType(rest[1..], lineNumber, assembly, parameter: false, structs, enums: enums));
            }
            if (text.TrimEnd('?') is "str" or "String<u8>" or "String<u16>")
            {
                if (!parameter)
                    throw new BindingException($"line {lineNumber}: a C function gives numbers, bools, pointers or func*, not a string (`{text}`)");
                var unit = text.TrimEnd('?') == "String<u16>" ? HirType.Of(Primitive.U16) : HirType.U8;
                var named = new NamedType(assembly.Reference("System.Core", "System", "String", 1), [unit]);
                // `str?`: the C function takes NULL too.
                return text.EndsWith('?') ? new NullableType(named) : named;
            }
            int stars = 0;
            while (text.EndsWith('*'))
            {
                stars++;
                text = text[..^1].Trim();
            }
            if (enums is not null && enums.TryGetValue(text, out var enumToken))
            {
                // An enum crosses by value (it is its number) or by pointer.
                HirType named = new NamedType(enumToken, []);
                for (int s = 0; s < stars; s++)
                    named = new PointerType(named);
                return named;
            }
            if (structs is not null && structs.TryGetValue(text, out var token))
            {
                if (stars == 0 && !field)
                    throw new BindingException($"line {lineNumber}: a struct crosses to C by pointer (`{text}*`), by value only as a field of another struct");
                HirType named = new NamedType(token, []);
                for (int s = 0; s < stars; s++)
                    named = new PointerType(named);
                return named;
            }
            HirType type = text switch
            {
                "i8" => HirType.Of(Primitive.I8), "i16" => HirType.Of(Primitive.I16), "i32" => HirType.I32, "i64" => HirType.I64,
                "u8" => HirType.U8, "u16" => HirType.Of(Primitive.U16), "u32" => HirType.Of(Primitive.U32), "u64" => HirType.Of(Primitive.U64),
                "f32" => HirType.Of(Primitive.F32), "f64" => HirType.F64, "bool" => HirType.Bool, "void" => HirType.Void,
                _ => throw new BindingException($"line {lineNumber}: `{text}` is not a type a binding can carry (numbers, bool, void, pointers, func*, declared structs, and strings as parameters)"),
            };
            for (int s = 0; s < stars; s++)
                type = new PointerType(type);
            return type;
        }

        private static int MatchingAngle(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '<') depth++;
                else if (text[i] == '>' && --depth == 0) return i;
            }
            return -1;
        }
    }
}
