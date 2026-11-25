using System.Globalization;
using System.Text.RegularExpressions;

namespace Larva.Build.Profiles
{
    /// <summary>
    /// What a kind of <c>.queen</c> file can say: its chunks, their keys, the values each takes, and rules between them.
    /// A profile is a folder (<c>Profiles/project/</c>) whose <c>template.tqueen</c> declares it; a file chooses its profile
    /// by its own name (<c>project.queen</c> is read with <c>project</c>). See <see cref="ProfileCatalog"/>.
    /// </summary>
    public sealed class QueenProfile
    {
        public required string Name { get; init; }
        public string Description { get; init; } = "";

        /// <summary>The <c>template.tqueen</c> it was read from (a path, or <c>builtin:name</c>).</summary>
        public required string File { get; init; }

        /// <summary>The profile it extends (its chunks and rules come first; this one's add to them or replace them).</summary>
        public QueenProfile? Base { get; init; }

        /// <summary>Whether chunks it does not declare are allowed (kept as written, for tools).</summary>
        public bool OpenChunks { get; init; }

        public List<ChunkSpec> OwnChunks { get; } = [];
        public List<RuleSpec> OwnRules { get; } = [];

        /// <summary>Its chunks and its bases' (a chunk this profile declares again replaces the base's).</summary>
        public IEnumerable<ChunkSpec> Chunks =>
            Base is null ? OwnChunks : Base.Chunks.Where(c => OwnChunks.All(o => o.Name != c.Name)).Concat(OwnChunks);

        public IEnumerable<RuleSpec> Rules => Base is null ? OwnRules : Base.Rules.Concat(OwnRules);

        public ChunkSpec? Chunk(string name) => OwnChunks.FirstOrDefault(c => c.Name == name) ?? Base?.Chunk(name);

        /// <summary>Whether this profile is <paramref name="name"/> or extends it.</summary>
        public bool Is(string name) => Name == name || Base?.Is(name) == true;

        public bool AllowsOtherChunks => OpenChunks || Base?.AllowsOtherChunks == true;
    }

    /// <summary><c>chunk NAME [required] [open]:</c> and its keys.</summary>
    public sealed class ChunkSpec
    {
        public required string Name { get; init; }
        public string Doc { get; init; } = "";
        public bool Required { get; init; }

        /// <summary>Whether keys it does not declare are allowed (DEPS: <c>Name: path</c>), as <see cref="OtherKeys"/>.</summary>
        public bool Open { get; init; }

        /// <summary>The type of the keys an open chunk does not declare (a path by default).</summary>
        public ValueType OtherKeys { get; init; } = new PathType(PathKind.Any);

        public int Line { get; init; }
        public List<KeySpec> Keys { get; } = [];

        public KeySpec? Key(string name) => Keys.FirstOrDefault(k => k.Name == name);
    }

    /// <summary><c>name: TYPE [= default] [required] [many] [only when …] [required when …] [deprecated "…"] [example "…"]</c>.</summary>
    public sealed class KeySpec
    {
        public required string Name { get; init; }
        public required ValueType Type { get; init; }
        public string Doc { get; init; } = "";

        /// <summary>The value when the file does not say (as written: <c>exe</c>, <c>$folder</c>), or null.</summary>
        public string? Default { get; init; }

        public bool Required { get; init; }

        /// <summary>Whether the key may be given more than once (each entry a value: DEPS <c>package</c>).</summary>
        public bool Many { get; init; }

        /// <summary><c>only when …</c>: the key means something only then (a warning otherwise).</summary>
        public Condition? OnlyWhen { get; init; }

        /// <summary><c>required when …</c>: the key must be there then.</summary>
        public Condition? RequiredWhen { get; init; }

        public string? Deprecated { get; init; }
        public string? Example { get; init; }
        public int Line { get; init; }
    }

    /// <summary><c>error|warning [QNxxx] when COND: "message"</c>.</summary>
    public sealed record RuleSpec(bool IsError, string Code, Condition When, string Message, int Line);

    // ── Value types ────────────────────────────────────────

    /// <summary>A key's type: what values it takes. <see cref="Accepts"/> checks a value; <see cref="Describe"/> says it.</summary>
    public abstract record ValueType
    {
        public abstract bool Accepts(QueenValue value);

        /// <summary>For a message: "`exe`, `library` or `tool`", "a whole number from 0 to 3".</summary>
        public abstract string Describe();

        /// <summary>The values to offer when completing (an enumeration's, true/false, a range's).</summary>
        public virtual IEnumerable<string> Values => [];

        /// <summary>A single value or a list: the items one by one.</summary>
        protected static IEnumerable<QueenValue> Items(QueenValue value) => value is QueenList list ? list.Items : [value];

        /// <summary>Plain text, a name, a number, a version or a bool (not a list).</summary>
        protected static bool Scalar(QueenValue value) => value is not QueenList;
    }

    public sealed record TextType : ValueType
    {
        public override bool Accepts(QueenValue value) => Scalar(value);
        public override string Describe() => "text";
    }

    /// <summary>A name without dots (<c>MyApp</c>, <c>my_tool</c>).</summary>
    public sealed record IdentifierType : ValueType
    {
        public override bool Accepts(QueenValue value) => value is QueenIdentifier { Name: var n } && !n.Contains('.');
        public override string Describe() => "a name (letters, digits and `_`)";
    }

    /// <summary>A name with dots (<c>MyApp.Memory.Collector</c>).</summary>
    public sealed record NameType : ValueType
    {
        public override bool Accepts(QueenValue value) => value is QueenIdentifier;
        public override string Describe() => "a name (`Namespace.Type`)";
    }

    public sealed record IntType(long? Min, long? Max) : ValueType
    {
        public override bool Accepts(QueenValue value) =>
            value is QueenNumber { IsInteger: true, Value: var n } && (Min is null || n >= Min) && (Max is null || n <= Max);

        public override string Describe() => (Min, Max) switch
        {
            ({ } a, { } b) => $"a whole number from {a} to {b}",
            ({ } a, null) => $"a whole number from {a}",
            (null, { } b) => $"a whole number up to {b}",
            _ => "a whole number",
        };

        public override IEnumerable<string> Values =>
            Min is { } a && Max is { } b && b - a <= 16 ? Enumerable.Range(0, (int)(b - a + 1)).Select(i => (a + i).ToString(CultureInfo.InvariantCulture)) : [];
    }

    public sealed record NumberType : ValueType
    {
        public override bool Accepts(QueenValue value) => value is QueenNumber;
        public override string Describe() => "a number";
    }

    public sealed record BoolType : ValueType
    {
        public override bool Accepts(QueenValue value) => value is QueenBool;
        public override string Describe() => "`true` or `false`";
        public override IEnumerable<string> Values => ["true", "false"];
    }

    /// <summary><c>v1.0</c>, <c>v2.3.11</c> (a plain number such as <c>1.2</c> is one too).</summary>
    public sealed record VersionType : ValueType
    {
        public override bool Accepts(QueenValue value) => value is QueenVersion || value is QueenNumber n && QueenParser.ParseTerms(n.Raw) is not null;
        public override string Describe() => "a version (`v1.0`, `v2.3.11`)";
    }

    public enum PathKind : byte { Any, Relative, Absolute }

    /// <summary><c>PATH</c> (relative to the file's folder, or absolute), <c>RPATH</c> (relative only), <c>APATH</c> (absolute only).</summary>
    public sealed record PathType(PathKind Kind) : ValueType
    {
        public override bool Accepts(QueenValue value) => Scalar(value) && value.Display.Length > 0 && Kind switch
        {
            PathKind.Relative => !Rooted(value.Display),
            PathKind.Absolute => Rooted(value.Display),
            _ => true,
        };

        /// <summary>Rooted on any system, whichever this one is (<c>/x</c>, <c>C:/x</c>, <c>\\server</c>).</summary>
        internal static bool Rooted(string path) =>
            path.StartsWith('/') || path.StartsWith('\\') || path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

        public override string Describe() => Kind switch
        {
            PathKind.Relative => "a path relative to the file's folder",
            PathKind.Absolute => "an absolute path",
            _ => "a path",
        };
    }

    /// <summary><c>FILE(.dll .lva)</c>: a path with one of those extensions; <c>FILE(profile project)</c>: a file of that profile.</summary>
    public sealed record FileType(string[] Extensions, string? Profile) : ValueType
    {
        public override bool Accepts(QueenValue value) => Scalar(value) && value.Display.Length > 0
            && (Extensions.Length == 0 || Extensions.Any(e => value.Display.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            && (Profile is null || ProfileCatalog.ForFile(value.Display)?.Is(Profile) == true);

        public override string Describe() => Profile is { } profile ? $"a `{profile}` file (`{profile}.queen`)"
            : Extensions.Length > 0 ? $"a file ({string.Join(", ", Extensions.Select(e => $"`{e}`"))})" : "a file";
    }

    public sealed record FolderType : ValueType
    {
        public override bool Accepts(QueenValue value) => Scalar(value) && value.Display.Length > 0;
        public override string Describe() => "a folder";
    }

    /// <summary>A file, a folder or a pattern (<c>*</c> within a folder, <c>**</c> across folders).</summary>
    public sealed record GlobType : ValueType
    {
        public override bool Accepts(QueenValue value) => Scalar(value) && value.Display.Length > 0;
        public override string Describe() => "a file, a folder or a pattern (`src/**`)";
    }

    /// <summary>An address: with its scheme (<c>https://…</c>, <c>file:…</c>) or a host and a path (<c>antril.org/hive</c>).</summary>
    public sealed record UrlType : ValueType
    {
        private static readonly Regex Address = new(@"^([a-z][a-z0-9+.-]*:\S+|[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+(:[0-9]+)?(/\S*)?)$", RegexOptions.CultureInvariant);

        public override bool Accepts(QueenValue value) => Scalar(value) && Address.IsMatch(value.Display) && !(value.Display.Length >= 2 && value.Display[1] == ':');
        public override string Describe() => "an address (`https://…`, `antril.org/hive`)";
    }

    public sealed record RegexType(string Pattern) : ValueType
    {
        private readonly Regex regex = new(Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        public override bool Accepts(QueenValue value)
        {
            if (!Scalar(value))
                return false;
            try
            {
                return regex.IsMatch(value.Display);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        public override string Describe() => $"text that matches `{Pattern}`";
    }

    /// <summary><c>[a, b, c]</c>: one of those words.</summary>
    public sealed record EnumType(string[] Options) : ValueType
    {
        public override bool Accepts(QueenValue value) => Scalar(value) && Options.Contains(value.Display);

        public override string Describe() => Options.Length switch
        {
            0 => "nothing",
            1 => $"`{Options[0]}`",
            _ => string.Join(", ", Options[..^1].Select(o => $"`{o}`")) + $" or `{Options[^1]}`",
        };

        public override IEnumerable<string> Values => Options;
    }

    /// <summary><c>A | B</c>: either (<c>[generic, native] | TEXT</c> offers those words and takes any text).</summary>
    public sealed record UnionType(ValueType[] Alternatives) : ValueType
    {
        public override bool Accepts(QueenValue value) => Alternatives.Any(a => a.Accepts(value));
        public override string Describe() => string.Join(", or ", Alternatives.Select(a => a.Describe()));
        public override IEnumerable<string> Values => Alternatives.SelectMany(a => a.Values).Distinct();
    }

    /// <summary><c>LIST(T)</c>: a list of T, or a single T.</summary>
    public sealed record ListType(ValueType Item) : ValueType
    {
        public override bool Accepts(QueenValue value) => Items(value).All(Item.Accepts);
        public override string Describe() => $"{Item.Describe()}, or a list of them";
        public override IEnumerable<string> Values => Item.Values;
    }

    // ── Conditions ─────────────────────────────────────────

    /// <summary>A key a condition reads: <c>gc</c> (in the key's own chunk) or <c>BUILD.gc</c>.</summary>
    public sealed record KeyRef(string? Chunk, string Key)
    {
        public override string ToString() => Chunk is null ? Key : $"{Chunk}.{Key}";
    }

    /// <summary>What a condition asks of a file: the value of a key (as written, or its default), or null.</summary>
    public delegate string? ValueLookup(KeyRef key);

    public abstract record Condition
    {
        public abstract bool Holds(ValueLookup values);

        /// <summary>The keys it reads (the first is where a rule is reported).</summary>
        public abstract IEnumerable<KeyRef> Keys { get; }

        /// <summary>As written, for messages.</summary>
        public abstract string Text { get; }
    }

    public sealed record CompareCondition(KeyRef Key, bool Equal, string Value) : Condition
    {
        public override bool Holds(ValueLookup values) => string.Equals(values(Key), Value, StringComparison.Ordinal) == Equal;
        public override IEnumerable<KeyRef> Keys => [Key];
        public override string Text => $"{Key} {(Equal ? "==" : "!=")} {Value}";
    }

    public sealed record InCondition(KeyRef Key, string[] Values) : Condition
    {
        public override bool Holds(ValueLookup values) => values(Key) is { } value && Values.Contains(value);
        public override IEnumerable<KeyRef> Keys => [Key];
        public override string Text => $"{Key} in [{string.Join(", ", Values)}]";
    }

    public sealed record ExistsCondition(KeyRef Key) : Condition
    {
        public override bool Holds(ValueLookup values) => values(Key) is not null;
        public override IEnumerable<KeyRef> Keys => [Key];
        public override string Text => $"exists({Key})";
    }

    public sealed record NotCondition(Condition Inner) : Condition
    {
        public override bool Holds(ValueLookup values) => !Inner.Holds(values);
        public override IEnumerable<KeyRef> Keys => Inner.Keys;
        public override string Text => $"not {Inner.Text}";
    }

    public sealed record AndCondition(Condition Left, Condition Right) : Condition
    {
        public override bool Holds(ValueLookup values) => Left.Holds(values) && Right.Holds(values);
        public override IEnumerable<KeyRef> Keys => Left.Keys.Concat(Right.Keys);
        public override string Text => $"{Left.Text} and {Right.Text}";
    }

    public sealed record OrCondition(Condition Left, Condition Right) : Condition
    {
        public override bool Holds(ValueLookup values) => Left.Holds(values) || Right.Holds(values);
        public override IEnumerable<KeyRef> Keys => Left.Keys.Concat(Right.Keys);
        public override string Text => $"{Left.Text} or {Right.Text}";
    }
}
