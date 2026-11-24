using Larva.Hir;
using Larva.Hir.Services;

namespace Larva.Polyglot
{
    /// <summary>The languages a library can be offered to.</summary>
    public enum TargetLanguage : byte { C, CSharp, Python, Java, TypeScript }

    /// <summary>What a name names (what a profile's <c>Name</c> hook is asked about).</summary>
    public enum NameKind : byte
    {
        Namespace,
        Type,
        Interface,
        /// <summary>The class C# and Java put a namespace's functions and constants in.</summary>
        ModuleClass,
        Constructor,
        Method,
        Property,
        Indexer,
        Field,
        Constant,
        EnumMember,
        Event,
        Operator,
        Parameter,
    }

    /// <summary>A member as the rules see it: what it is, its name in the library, and what decides its foreign name.</summary>
    public sealed record MemberRequest(PolyglotType Owner, NameKind Kind, string Name, CatalogMember Member)
    {
        /// <summary>A property or field of type <c>bool</c> (Java: <c>isX</c>).</summary>
        public bool IsBool => Member.ValueType is PrimitiveType { Kind: Primitive.Bool };

        /// <summary>Whether a property or field can be written.</summary>
        public bool CanWrite => Member.CanWrite;

        /// <summary>An operator's symbol (<c>+</c>), and whether it takes one operand.</summary>
        public string? Symbol { get; init; }
        public bool Unary { get; init; }
    }

    /// <summary>
    /// One language's naming rules: the style of each kind of name, its reserved words and how a name that is one is
    /// escaped, whether it overloads, and how operators are written. One instance per language and generation.
    /// </summary>
    public abstract class TargetRules
    {
        public abstract TargetLanguage Language { get; }

        /// <summary>Its name on the command line and in <c>.queen</c> (<c>python</c>), then other spellings (<c>py</c>).</summary>
        public abstract string Id { get; }
        public virtual IReadOnlyList<string> Aliases => [];

        /// <summary>The language's reserved words (what a name must not be).</summary>
        public abstract IReadOnlySet<string> Reserved { get; }

        /// <summary>Whether two functions of one scope can share a name (C#, Java); else an overload set is one function that dispatches, or (C) gets suffixes.</summary>
        public abstract bool Overloads { get; }

        /// <summary>Whether overloads become one function that dispatches on its arguments (Python, TypeScript).</summary>
        public virtual bool Dispatches => false;

        /// <summary>Whether the language writes operators as operators (C#, Python); else they are named functions.</summary>
        public virtual bool OperatorSyntax => false;

        /// <summary>Whether an interface loses its <c>I</c> prefix (<c>IReader</c> → <c>Reader</c>) when no other type takes that name.</summary>
        public virtual bool DropsInterfacePrefix => false;

        /// <summary>
        /// Names every type of the language already has and a member must not take (Java's <c>getClass</c>, C#'s
        /// <c>GetType</c>): such a member gets a <c>_</c> after its name.
        /// </summary>
        public virtual IReadOnlySet<string> Inherited => empty;
        private static readonly HashSet<string> empty = [];

        /// <summary>What the capitalized styles do with acronyms.</summary>
        public AcronymPolicy Acronyms { get; init; }

        /// <summary><paramref name="name"/> in a style, with this language's acronym policy, writable (no leading digit).</summary>
        public string Shape(string name, NameStyle style) => Naming.Writable(Naming.Shape(name, style, Acronyms));

        /// <summary>A name that is a reserved word, escaped (<c>from</c> → <c>from_</c>).</summary>
        public virtual string Escape(string name, NameKind kind) => Reserved.Contains(name) ? name + "_" : name;

        /// <summary>A namespace (<c>Acme.Json</c>) as the language names the module or package.</summary>
        public abstract string Namespace(string ns);

        /// <summary>A type's name (generic arity left out; C: with its namespace's prefix).</summary>
        public abstract string Type(PolyglotType type);

        /// <summary>The class a namespace's functions and constants are in, or null when the language has free functions.</summary>
        public virtual string? ModuleClass(string ns, string assembly) => null;

        /// <summary>
        /// The names a member takes in its scope (the first is the one shown): one, or a getter and a setter, or none when
        /// the language cannot express it (an operator it has no syntax for).
        /// </summary>
        public abstract string[] Member(MemberRequest member);

        /// <summary>A parameter's name.</summary>
        public abstract string Parameter(string name);

        /// <summary>
        /// For languages that dispatch overloads: what a value of <paramref name="type"/> is when the language looks at it
        /// (Python: every integer is an <c>int</c>). Two overloads whose parameters look alike cannot be told apart.
        /// </summary>
        public virtual string Looks(HirType type, HirCatalog catalog) => type.ToString();

        /// <summary>The name a C++-style operator symbol has as words (<c>+</c> → add, unary <c>-</c> → negate), or null.</summary>
        protected static string? OperatorWords(string symbol, bool unary) => (symbol, unary) switch
        {
            ("+", false) => "Add",
            ("-", false) => "Subtract",
            ("*", false) => "Multiply",
            ("/", false) => "Divide",
            ("%", false) => "Remainder",
            ("==", false) => "Equals",
            ("!=", false) => "NotEquals",
            ("<", false) => "LessThan",
            ("<=", false) => "LessOrEqual",
            (">", false) => "GreaterThan",
            (">=", false) => "GreaterOrEqual",
            ("&", false) => "And",
            ("|", false) => "Or",
            ("^", false) => "Xor",
            ("<<", false) => "ShiftLeft",
            (">>", false) => "ShiftRight",
            ("-", true) => "Negate",
            ("+", true) => "Plus",
            ("!", true) => "Not",
            ("~", true) => "Complement",
            ("++", true) => "Increment",
            ("--", true) => "Decrement",
            _ => null,
        };

        /// <summary>The type's name before its style: an interface's without its <c>I</c> where the language drops it.</summary>
        protected string TypeName(PolyglotType type) =>
            DropsInterfacePrefix && type.Source.IsInterface && !type.KeepsInterfacePrefix ? WithoutI(type.Source.Name) : type.Source.Name;

        /// <summary>An interface's name without the <c>I</c> prefix it has (<c>IReader</c> → <c>Reader</c>; <c>IOStream</c> stays).</summary>
        public static string WithoutI(string name) =>
            name.Length > 2 && name[0] == 'I' && char.IsAsciiLetterUpper(name[1]) && char.IsAsciiLetterLower(name[2]) ? name[1..] : name;

        /// <summary>The rules of <paramref name="language"/>.</summary>
        public static TargetRules For(TargetLanguage language, AcronymPolicy? acronyms = null, string javaPackageRoot = "", string csharpRoot = "") => language switch
        {
            TargetLanguage.C => new CRules(),
            TargetLanguage.CSharp => new CSharpRules { Acronyms = acronyms ?? AcronymPolicy.Keep, NamespaceRoot = csharpRoot },
            TargetLanguage.Python => new PythonRules { Acronyms = acronyms ?? AcronymPolicy.Keep },
            TargetLanguage.Java => new JavaRules { Acronyms = acronyms ?? AcronymPolicy.Keep, PackageRoot = javaPackageRoot },
            _ => new TypeScriptRules { Acronyms = acronyms ?? AcronymPolicy.Keep },
        };

        /// <summary>The language named <paramref name="name"/> (its id or an alias), or null.</summary>
        public static TargetLanguage? Parse(string name)
        {
            foreach (var language in Enum.GetValues<TargetLanguage>())
            {
                var rules = For(language);
                if (rules.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || rules.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return language;
            }
            return null;
        }

        /// <summary>The ids of every language, for messages.</summary>
        public static string Known => string.Join(", ", Enum.GetValues<TargetLanguage>().Select(l => For(l).Id));

        /// <summary>The last segment of a namespace, or the assembly's name when it has none.</summary>
        protected static string LastSegment(string ns, string assembly) =>
            ns.Length == 0 ? assembly.Replace(".", "") : ns[(ns.LastIndexOf('.') + 1)..];

        /// <summary>Whether a property's name already reads as a question (<c>IsEmpty</c>, <c>HasValue</c>, <c>CanRead</c>).</summary>
        protected static bool IsQuestion(string name) =>
            Naming.Words(name) is [var first, _, ..] && first is "Is" or "Has" or "Can" or "is" or "has" or "can";
    }

    /// <summary>C: every name global, prefixed by its namespace and type (<c>acme_json_parser_parse</c>); constants in capitals.</summary>
    public sealed class CRules : TargetRules
    {
        private static readonly HashSet<string> reserved = new(StringComparer.Ordinal)
        {
            // C (C23 included) and what a C++ compiler reading the header also refuses.
            "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum", "extern", "float", "for", "goto",
            "if", "inline", "int", "long", "register", "restrict", "return", "short", "signed", "sizeof", "static", "struct", "switch",
            "typedef", "union", "unsigned", "void", "volatile", "while", "bool", "true", "false", "alignas", "alignof", "static_assert",
            "thread_local", "nullptr", "typeof", "constexpr", "_Bool", "_Complex", "_Imaginary", "_Atomic", "_Generic", "_Noreturn",
            "class", "new", "delete", "this", "template", "namespace", "operator", "private", "protected", "public", "virtual", "friend",
            "try", "catch", "throw", "using", "typename", "explicit", "mutable", "export", "and", "or", "not", "xor",
        };

        public override TargetLanguage Language => TargetLanguage.C;
        public override string Id => "c";
        public override IReadOnlyList<string> Aliases => ["h"];
        public override IReadOnlySet<string> Reserved => reserved;
        public override bool Overloads => false;

        public override string Namespace(string ns) =>
            string.Join("_", ns.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(s => Shape(s, NameStyle.Snake)));

        /// <summary>A type's name with <c>_t</c> (<c>acme_json_parser_t</c>): its functions take the name without it, so a type and a function never meet.</summary>
        public override string Type(PolyglotType type) => Prefix(type) + "_t";

        /// <summary>What a type's functions start with (<c>acme_json_parser</c>); a module's: its namespace's.</summary>
        public string Prefix(PolyglotType type) => type.IsModule ? Namespace(type.Namespace)
            : Join(type.Owner is { } outer ? Prefix(outer) : Namespace(type.Namespace), Shape(type.Source.Name, NameStyle.Snake));

        public override string[] Member(MemberRequest m)
        {
            string prefix = Prefix(m.Owner);
            string snake = Shape(m.Name, NameStyle.Snake);
            return m.Kind switch
            {
                NameKind.Constructor => [Join(prefix, "new")],
                NameKind.Property or NameKind.Field => m.CanWrite ? [Join(prefix, "get_" + snake), Join(prefix, "set_" + snake)] : [Join(prefix, "get_" + snake)],
                NameKind.Indexer => m.CanWrite ? [Join(prefix, "get_item"), Join(prefix, "set_item")] : [Join(prefix, "get_item")],
                NameKind.Constant or NameKind.EnumMember => [Join(prefix.ToUpperInvariant(), Shape(m.Name, NameStyle.UpperSnake))],
                NameKind.Event => [Join(prefix, "add_" + snake), Join(prefix, "remove_" + snake)],
                NameKind.Operator => OperatorWords(m.Symbol ?? "", m.Unary) is { } words ? [Join(prefix, "op_" + Shape(words, NameStyle.Snake))] : [],
                _ => [Escape(Join(prefix, snake), m.Kind)],
            };
        }

        public override string Parameter(string name) => Escape(Shape(name, NameStyle.Snake), NameKind.Parameter);

        /// <summary>The suffix an overload gets from its parameter types (<c>_i64_str</c>).</summary>
        public static string OverloadSuffix(IReadOnlyList<Parameter> parameters, HirCatalog catalog) =>
            parameters.Count == 0 ? "_0" : "_" + string.Join("_", parameters.Select(p => TypeWord(p.Type, catalog)));

        /// <summary>A type as a word of a C name (<c>i64</c>, <c>str</c>, <c>c_type</c>): overload suffixes, list types.</summary>
        public static string TypeWord(HirType type, HirCatalog catalog) => type switch
        {
            PrimitiveType p => p.Kind.Keyword(),
            NullableType n => "opt_" + TypeWord(n.Inner, catalog),
            ArrayType a => TypeWord(a.Element, catalog) + "_array",
            NamedType n when catalog.TypeOf(n) is { } t => t.FullName == "System.String" ? "str" : Naming.Shape(t.Name, NameStyle.Snake),
            FuncType => "fn",
            _ => "x",
        };

        private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + "_" + name;
    }

    /// <summary>C#: Pascal case for every member, camel case for parameters, <c>I</c> before an interface's name; <c>@</c> escapes.</summary>
    public sealed class CSharpRules : TargetRules
    {
        private static readonly HashSet<string> reserved = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal",
            "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float",
            "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
            "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref", "return",
            "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try",
            "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        };

        public override TargetLanguage Language => TargetLanguage.CSharp;
        public override string Id => "csharp";
        public override IReadOnlyList<string> Aliases => ["cs", "c#"];
        public override IReadOnlySet<string> Reserved => reserved;
        public override bool Overloads => true;
        public override bool OperatorSyntax => true;
        public override IReadOnlySet<string> Inherited => inherited;
        private static readonly HashSet<string> inherited = ["GetType", "MemberwiseClone", "Finalize"];

        public override string Escape(string name, NameKind kind) => Reserved.Contains(name) ? "@" + name : name;

        /// <summary>What every namespace starts with (empty: the library's own).</summary>
        public string NamespaceRoot { get; init; } = "";

        public override string Namespace(string ns)
        {
            string own = string.Join(".", ns.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(s => Escape(Shape(s, NameStyle.Pascal), NameKind.Namespace)));
            return NamespaceRoot.Length == 0 ? own : own.Length == 0 ? NamespaceRoot : NamespaceRoot + "." + own;
        }

        public override string Type(PolyglotType type)
        {
            string name = Shape(type.Source.Name, NameStyle.Pascal);
            if (type.Source.IsInterface && WithoutI(name) == name)
                name = "I" + name;
            return Escape(name, NameKind.Type);
        }

        public override string? ModuleClass(string ns, string assembly) => Shape(LastSegment(ns, assembly), NameStyle.Pascal) + "Functions";

        public override string[] Member(MemberRequest m) => m.Kind switch
        {
            NameKind.Constructor => [m.Owner.Names[TargetLanguage.CSharp]],
            NameKind.Indexer => ["this[]"],
            NameKind.Operator => m.Symbol is "+" or "-" or "*" or "/" or "%" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "&" or "|" or "^"
                or "<<" or ">>" or "!" or "~" or "++" or "--" ? ["operator " + m.Symbol] : [],
            _ => [Escape(Shape(m.Name, NameStyle.Pascal), m.Kind)],
        };

        public override string Parameter(string name) => Escape(Shape(name, NameStyle.Camel), NameKind.Parameter);
    }

    /// <summary>Python (PEP 8): CapWords types, snake_case functions and attributes, capitals for constants; <c>_</c> after a keyword.</summary>
    public sealed class PythonRules : TargetRules
    {
        private static readonly HashSet<string> reserved = new(StringComparer.Ordinal)
        {
            "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else",
            "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
            "return", "try", "while", "with", "yield",
        };

        public override TargetLanguage Language => TargetLanguage.Python;
        public override string Id => "python";
        public override IReadOnlyList<string> Aliases => ["py"];
        public override IReadOnlySet<string> Reserved => reserved;
        public override bool Overloads => false;
        public override bool Dispatches => true;
        public override bool OperatorSyntax => true;
        public override bool DropsInterfacePrefix => true;

        public override string Namespace(string ns) =>
            string.Join(".", ns.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(s => Escape(Shape(s, NameStyle.Snake), NameKind.Namespace)));

        public override string Type(PolyglotType type) => Escape(Shape(TypeName(type), NameStyle.Pascal), NameKind.Type);

        public override string[] Member(MemberRequest m) => m.Kind switch
        {
            NameKind.Constructor => ["__init__"],
            NameKind.Method when m.Name == "ToString" && m.Member.Parameters.Count == 0 => ["__str__"],
            NameKind.Constant or NameKind.EnumMember => [Escape(Shape(m.Name, NameStyle.UpperSnake), m.Kind)],
            NameKind.Event => ["on_" + Shape(m.Name, NameStyle.Snake)],
            NameKind.Indexer => m.CanWrite ? ["__getitem__", "__setitem__"] : ["__getitem__"],
            NameKind.Operator => Dunder(m.Symbol ?? "", m.Unary) is { } dunder ? [dunder] : [],
            _ => [Escape(Shape(m.Name, NameStyle.Snake), m.Kind)],
        };

        private static string? Dunder(string symbol, bool unary) => (symbol, unary) switch
        {
            ("+", false) => "__add__",
            ("-", false) => "__sub__",
            ("*", false) => "__mul__",
            ("/", false) => "__truediv__",
            ("%", false) => "__mod__",
            ("==", false) => "__eq__",
            ("!=", false) => "__ne__",
            ("<", false) => "__lt__",
            ("<=", false) => "__le__",
            (">", false) => "__gt__",
            (">=", false) => "__ge__",
            ("&", false) => "__and__",
            ("|", false) => "__or__",
            ("^", false) => "__xor__",
            ("<<", false) => "__lshift__",
            (">>", false) => "__rshift__",
            ("-", true) => "__neg__",
            ("+", true) => "__pos__",
            ("~", true) => "__invert__",
            _ => null,
        };

        public override string Parameter(string name) => Escape(Shape(name, NameStyle.Snake), NameKind.Parameter);

        public override string Looks(HirType type, HirCatalog catalog) => type switch
        {
            PrimitiveType { Kind: Primitive.Bool } => "bool",
            PrimitiveType { Kind: Primitive.Char } => "str",
            PrimitiveType p when p.Kind.IsInteger() => "int",
            PrimitiveType p when p.Kind.IsFloat() => "float",
            NullableType n => Looks(n.Inner, catalog),
            // An array of numbers is a typed buffer (its type code tells i32 from u64); any other is a sequence.
            ArrayType { Element: PrimitiveType e, Rank: 1 } => $"buffer of {e.Kind.Keyword()}",
            ArrayType => "sequence",
            FuncType => "callable",
            // An enum is an IntEnum: an int, but one whose class the entry checks before plain ints.
            NamedType n when catalog.TypeOf(n) is { } t => t.FullName == "System.String" ? "str" : t.FullName,
            _ => type.ToString(),
        };
    }

    /// <summary>Java: Pascal types, camel members, getters and setters for properties, capitals for constants; <c>_</c> after a keyword.</summary>
    public sealed class JavaRules : TargetRules
    {
        private static readonly HashSet<string> reserved = new(StringComparer.Ordinal)
        {
            "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default", "do",
            "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if", "implements", "import", "instanceof",
            "int", "interface", "long", "native", "new", "package", "private", "protected", "public", "return", "short", "static",
            "strictfp", "super", "switch", "synchronized", "this", "throw", "throws", "transient", "try", "void", "volatile", "while",
            "true", "false", "null", "_", "yield",
        };

        /// <summary>What the package of every namespace starts with (<c>com.acme</c>).</summary>
        public string PackageRoot { get; init; } = "";

        public override TargetLanguage Language => TargetLanguage.Java;
        public override string Id => "java";
        public override IReadOnlySet<string> Reserved => reserved;
        public override bool Overloads => true;
        public override bool DropsInterfacePrefix => true;
        public override IReadOnlySet<string> Inherited => inherited;
        private static readonly HashSet<string> inherited = ["getClass", "notify", "notifyAll", "wait", "finalize", "clone"];

        public override string Namespace(string ns)
        {
            var segments = ns.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(s => Escape(Shape(s, NameStyle.Lower), NameKind.Namespace));
            string package = string.Join(".", segments);
            return PackageRoot.Length == 0 ? package : package.Length == 0 ? PackageRoot : PackageRoot + "." + package;
        }

        public override string Type(PolyglotType type) => Escape(Shape(TypeName(type), NameStyle.Pascal), NameKind.Type);

        public override string? ModuleClass(string ns, string assembly) => Shape(LastSegment(ns, assembly), NameStyle.Pascal) + "Functions";

        public override string[] Member(MemberRequest m)
        {
            string pascal = Shape(m.Name, NameStyle.Pascal);
            // `IsEmpty` reads `isEmpty()` and is set with `setEmpty()`; `Enabled` (a bool) reads `isEnabled()`.
            string Getter() => m.IsBool ? (IsQuestion(m.Name) ? Shape(m.Name, NameStyle.Camel) : "is" + pascal) : "get" + pascal;
            string Setter() => "set" + (m.IsBool && IsQuestion(m.Name) && Naming.Words(m.Name)[0] is "Is" or "is" ? pascal[2..] : pascal);
            return m.Kind switch
            {
                NameKind.Constructor => [m.Owner.Names[TargetLanguage.Java]],
                NameKind.Field when m.Owner.IsRecord => [Escape(Shape(m.Name, NameStyle.Camel), m.Kind)],
                NameKind.Property or NameKind.Field => m.CanWrite ? [Getter(), Setter()] : [Getter()],
                NameKind.Indexer => m.CanWrite ? ["get", "set"] : ["get"],
                NameKind.Constant or NameKind.EnumMember => [Escape(Shape(m.Name, NameStyle.UpperSnake), m.Kind)],
                NameKind.Event => ["add" + pascal + "Listener", "remove" + pascal + "Listener"],
                NameKind.Operator => OperatorWords(m.Symbol ?? "", m.Unary) is { } words ? [Shape(words, NameStyle.Camel)] : [],
                _ => [Escape(Shape(m.Name, NameStyle.Camel), m.Kind)],
            };
        }

        public override string Parameter(string name) => Escape(Shape(name, NameStyle.Camel), NameKind.Parameter);

        /// <summary>A parameter's type after erasure: what makes two overloads the same method to the JVM.</summary>
        public static string Erased(HirType type, HirCatalog catalog) => type switch
        {
            PrimitiveType p => p.Kind.Keyword(),
            NullableType n => Erased(n.Inner, catalog) is var inner && n.Inner is PrimitiveType ? "boxed " + inner : inner,
            ArrayType a => Erased(a.Element, catalog) + "[]",
            NamedType n when catalog.TypeOf(n) is { } t => t.FullName,
            GenericParamType => "Object",
            _ => type.ToString(),
        };
    }

    /// <summary>TypeScript: Pascal types, camel members, capitals for constants; reserved words escaped only where they cannot be members.</summary>
    public sealed class TypeScriptRules : TargetRules
    {
        private static readonly HashSet<string> reserved = new(StringComparer.Ordinal)
        {
            "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "enum", "export",
            "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", "return", "super",
            "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while", "with", "yield", "let", "static", "implements",
            "interface", "package", "private", "protected", "public", "await", "arguments", "eval",
        };

        public override TargetLanguage Language => TargetLanguage.TypeScript;
        public override string Id => "typescript";
        public override IReadOnlyList<string> Aliases => ["ts", "js", "javascript"];
        public override IReadOnlySet<string> Reserved => reserved;
        public override bool Overloads => false;
        public override bool Dispatches => true;
        public override bool DropsInterfacePrefix => true;
        public override IReadOnlySet<string> Inherited => inherited;
        private static readonly HashSet<string> inherited = ["constructor"];

        /// <summary>Members may be reserved words (<c>list.delete()</c>); names that stand alone may not.</summary>
        public override string Escape(string name, NameKind kind) =>
            kind is NameKind.Method or NameKind.Property or NameKind.Field or NameKind.Constant or NameKind.EnumMember or NameKind.Event
                ? name : base.Escape(name, kind);

        public override string Namespace(string ns) =>
            string.Join("/", ns.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(s => Shape(s, NameStyle.Kebab)));

        public override string Type(PolyglotType type) => Escape(Shape(TypeName(type), NameStyle.Pascal), NameKind.Type);

        public override string[] Member(MemberRequest m)
        {
            // A namespace's functions are exported names of the module: they stand alone.
            var kind = m.Owner.IsModule && m.Kind == NameKind.Method ? NameKind.Parameter : m.Kind;
            return m.Kind switch
            {
                NameKind.Constructor => ["constructor"],
                NameKind.Constant => [Escape(Shape(m.Name, NameStyle.UpperSnake), kind)],
                NameKind.EnumMember => [Escape(Shape(m.Name, NameStyle.Pascal), kind)],
                NameKind.Event => ["on" + Shape(m.Name, NameStyle.Pascal)],
                NameKind.Indexer => m.CanWrite ? ["get", "set"] : ["get"],
                NameKind.Operator => OperatorWords(m.Symbol ?? "", m.Unary) is { } words ? [Shape(words, NameStyle.Camel)] : [],
                _ => [Escape(Shape(m.Name, NameStyle.Camel), kind)],
            };
        }

        public override string Parameter(string name) => Escape(Shape(name, NameStyle.Camel), NameKind.Parameter);

        public override string Looks(HirType type, HirCatalog catalog) => type switch
        {
            PrimitiveType { Kind: Primitive.Bool } => "boolean",
            PrimitiveType { Kind: Primitive.Char } => "string",
            PrimitiveType { Kind: Primitive.I64 or Primitive.U64 or Primitive.I128 or Primitive.U128 or Primitive.I256 or Primitive.U256 } => "bigint",
            PrimitiveType p when p.Kind.IsInteger() || p.Kind.IsFloat() => "number",
            NullableType n => Looks(n.Inner, catalog),
            // An array of numbers is a typed array (Int32Array, BigUint64Array…).
            ArrayType { Element: PrimitiveType e, Rank: 1 } => $"typed array of {e.Kind.Keyword()}",
            ArrayType => "array",
            FuncType => "function",
            NamedType n when catalog.TypeOf(n) is { } t => t.FullName == "System.String" ? "string" : t.IsEnum ? "number" : t.FullName,
            _ => type.ToString(),
        };
    }
}
