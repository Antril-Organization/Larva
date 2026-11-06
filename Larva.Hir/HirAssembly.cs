using System.Numerics;

namespace Larva.Hir
{
    /// <summary>
    /// A Larva HIR assembly (<c>.lva</c>): the unit a frontend produces, a library is referenced by and a package carries.
    /// Its types are definitions; types and members of other assemblies are named through <see cref="TypeRefs"/> and
    /// <see cref="MemberRefs"/>, resolved when assemblies are loaded together.
    /// </summary>
    /// <summary>A run of a linked assembly's types that came from one input assembly.</summary>
    public readonly record struct HirOrigin(string Assembly, int FirstType, int TypeCount);

    public sealed class HirAssembly
    {
        public const string Extension = ".lva";

        public string Name { get; set; } = "";
        public int[] Version { get; set; } = [1, 0];
        public List<AssemblyRef> References { get; } = [];
        public List<TypeRef> TypeRefs { get; } = [];
        public List<MemberRef> MemberRefs { get; } = [];
        public List<TypeDef> Types { get; } = [];
        public List<HirAttribute> Attributes { get; } = [];
        public List<HirResource> Resources { get; } = [];

        /// <summary>
        /// What a language keeps with the assemblies it writes for its own compiler (templates expanded where they are
        /// used, say), by name: read back when a program references the assembly, never linked into a program.
        /// </summary>
        public List<HirResource> LanguageData { get; } = [];

        /// <summary>Source files that <see cref="SourceLocation.File"/> indexes (debug information).</summary>
        public List<string> Files { get; } = [];

        /// <summary>After linking: which input assembly each run of <see cref="Types"/> came from (in memory only).</summary>
        public List<HirOrigin> Origins { get; } = [];

        /// <summary>The input assembly type <paramref name="type"/> came from, or null when not linked from inputs.</summary>
        public string? OriginOf(int type)
        {
            foreach (var origin in Origins)
            {
                if (type >= origin.FirstType && type < origin.FirstType + origin.TypeCount)
                    return origin.Assembly;
            }
            return null;
        }

        /// <summary>The program's entry (<c>Main</c>), for executables.</summary>
        public MemberToken? EntryPoint { get; set; }

        public TypeDef Type(TypeToken token) => token.IsDefinition ? Types[token.Index] : throw new InvalidOperationException($"{token} is a reference");
        public MethodDef Method(MemberToken token) => token.IsDefinition ? Types[token.Type].Methods[token.Member] : throw new InvalidOperationException($"{token} is a reference");
        public FieldDef Field(MemberToken token) => token.IsDefinition ? Types[token.Type].Fields[token.Member] : throw new InvalidOperationException($"{token} is a reference");

        /// <summary>Adds a type and returns its token.</summary>
        public TypeToken Add(TypeDef type)
        {
            Types.Add(type);
            return TypeToken.Def(Types.Count - 1);
        }

        /// <summary>A reference to <paramref name="type"/> of <paramref name="assembly"/> (made once, then reused).</summary>
        public TypeToken Reference(string assembly, string ns, string name, int arity = 0)
        {
            int a = References.FindIndex(r => r.Name == assembly);
            if (a < 0)
            {
                References.Add(new AssemblyRef(assembly, []));
                a = References.Count - 1;
            }
            var reference = new TypeRef(a, ns, name, arity);
            int index = TypeRefs.IndexOf(reference);
            if (index < 0)
            {
                TypeRefs.Add(reference);
                index = TypeRefs.Count - 1;
            }
            return TypeToken.Ref(index);
        }

        public MemberToken Reference(MemberRef member)
        {
            int index = MemberRefs.FindIndex(m => m.Equals(member));
            if (index < 0)
            {
                MemberRefs.Add(member);
                index = MemberRefs.Count - 1;
            }
            return MemberToken.Ref(index);
        }

        public override string ToString() => Text.HirPrinter.Print(this);
    }

    /// <summary>Another assembly this one uses, with the version it was compiled against (empty: any).</summary>
    public sealed record AssemblyRef(string Name, int[] Version)
    {
        public bool Equals(AssemblyRef? other) => other is not null && Name == other.Name && Version.AsSpan().SequenceEqual(other.Version);
        public override int GetHashCode() => Name.GetHashCode();
    }

    /// <summary>A type of <see cref="HirAssembly.References"/>[<c>Assembly</c>], by namespace, name and generic arity.</summary>
    public sealed record TypeRef(int Assembly, string Namespace, string Name, int Arity);

    public enum MemberKind : byte { Field, Method, Property, Event }

    /// <summary>
    /// A member of another assembly's type, by name and signature (the signature tells overloads apart): for a field or a
    /// property, <c>Result</c> is its type and <c>Parameters</c> its indices.
    /// </summary>
    public sealed record MemberRef(MemberKind Kind, TypeToken Parent, string Name, int GenericArity, HirType[] Parameters, HirType Result)
    {
        public bool Equals(MemberRef? other) => other is not null && Kind == other.Kind && Parent == other.Parent && Name == other.Name
            && GenericArity == other.GenericArity && Result == other.Result && Parameters.AsSpan().SequenceEqual(other.Parameters);
        public override int GetHashCode() => HashCode.Combine(Kind, Parent, Name, GenericArity, Result, Hash.Of(Parameters));
    }

    public enum TypeKind : byte { Class, Struct, Interface, Enum, Bitflags, Func }

    [Flags]
    public enum TypeFlags : ushort
    {
        None = 0,
        Abstract = 1 << 0,
        Final = 1 << 1,
        Static = 1 << 2,
        Record = 1 << 3,
        Unsafe = 1 << 4,
        /// <summary>A type a frontend made (closures, reflection tables): hidden from reflection and completion.</summary>
        Generated = 1 << 5,
        /// <summary>
        /// A value type that gives a primitive its members (<c>Int32</c> for <c>i32</c>): <see cref="TypeDef.Underlying"/> is
        /// the primitive, and the type is that primitive (its methods take <c>this</c> as the primitive's value).
        /// </summary>
        Primitive = 1 << 6,
        /// <summary>
        /// An extension block (<c>ext(i32 x) { … }</c>): a generated class whose static methods extend the type in
        /// <see cref="TypeDef.Underlying"/>; unless the block is <see cref="Static"/>, each method's first parameter is the
        /// receiver.
        /// </summary>
        Extension = 1 << 7,
    }

    /// <summary>Who can see a declaration (public, internal, private…).</summary>
    public enum Access : byte { Public, Internal, Friendly, Inherited, Private, File }

    public sealed class TypeDef
    {
        public string Namespace { get; set; } = "";
        public string Name { get; set; } = "";
        public TypeKind Kind { get; set; }
        public TypeFlags Flags { get; set; }
        public Access Access { get; set; }

        /// <summary>The type this one is nested in (an index into <see cref="HirAssembly.Types"/>), or -1.</summary>
        public int DeclaringType { get; set; } = -1;

        /// <summary>Type parameters, the declaring types' first (a nested type of a generic type repeats them).</summary>
        public List<GenericParam> Generics { get; } = [];

        /// <summary>The base class (classes), or null for the root.</summary>
        public HirType? Base { get; set; }

        public List<HirType> Interfaces { get; } = [];

        /// <summary>Enums: the integer type of their values.</summary>
        public HirType? Underlying { get; set; }

        public List<FieldDef> Fields { get; } = [];
        public List<MethodDef> Methods { get; } = [];
        public List<PropertyDef> Properties { get; } = [];
        public List<EventDef> Events { get; } = [];
        public List<OperatorDef> Operators { get; } = [];
        public List<HirAttribute> Attributes { get; } = [];
        public SourceLocation Location { get; set; } = SourceLocation.None;

        /// <summary>The documentation the source wrote for it (null: none).</summary>
        public string? Doc { get; set; }

        public string FullName => Namespace.Length == 0 ? Name : $"{Namespace}.{Name}";

        public int AddField(FieldDef field) { Fields.Add(field); return Fields.Count - 1; }
        public int AddMethod(MethodDef method) { Methods.Add(method); return Methods.Count - 1; }

        public override string ToString() => FullName;
    }

    [Flags]
    public enum ConstraintKinds : ushort
    {
        None = 0,
        Value = 1 << 0, Plain = 1 << 1, Copy = 1 << 2, Unmanaged = 1 << 3, Managed = 1 << 4, FixedSize = 1 << 5,
        Numeric = 1 << 6, Integer = 1 << 7, SignedInteger = 1 << 8, UnsignedInteger = 1 << 9, Float = 1 << 10,
        /// <summary><c>new()</c>: has a public constructor without parameters.</summary>
        New = 1 << 11,
        /// <summary><c>enum</c>: an enum (of any base); <c>bitflag enum</c>: a bitflag enum (bitwise operators).</summary>
        Enum = 1 << 12, Bitflags = 1 << 13,
        /// <summary>A class (an object, held by a handle): its <see cref="GenericParam.Types"/> name a class it derives from.</summary>
        Object = 1 << 14,
    }

    public sealed record GenericParam(string Name, bool Variadic = false)
    {
        public ConstraintKinds Kinds { get; init; }

        /// <summary>Base class and interfaces the argument must have.</summary>
        public HirType[] Types { get; init; } = [];

        /// <summary><c>T : [u8, u16]</c>: the argument is one of these (empty: no list).</summary>
        public HirType[] OneOf { get; init; } = [];

        /// <summary>Operators the argument must support, as <c>symbol(operands) : result</c> in terms of the parameter.</summary>
        public OperatorRequirement[] Operators { get; init; } = [];

        /// <summary>Types the argument converts to.</summary>
        public HirType[] ConvertsTo { get; init; } = [];

        /// <summary>Types that convert to the argument (<c>(T)n</c> with a number: a requirement the numbers meet).</summary>
        public HirType[] ConvertsFrom { get; init; } = [];

        /// <summary><c>T : new(i32, str)</c>: constructors the argument must have, by their exact parameter types (one entry each).</summary>
        public HirType[][] Constructors { get; init; } = [];

        /// <summary><c>T : fixed(N)</c>: the argument's size in bytes (the sum of its fields); 0 when not asked.</summary>
        public int FixedSize { get; init; }

        public HirType? Default { get; init; }
    }

    public sealed record OperatorRequirement(string Symbol, HirType[] Operands, HirType Result);

    [Flags]
    public enum FieldFlags : byte
    {
        None = 0, Static = 1 << 0, Readonly = 1 << 1,
        /// <summary>A compile-time constant: <see cref="FieldDef.Value"/>, no storage.</summary>
        Const = 1 << 2,
        /// <summary>Thread-local static.</summary>
        ThreadStatic = 1 << 3,
        /// <summary>A field a frontend made (the storage of <c>get; set;</c>): hidden from reflection and completion.</summary>
        Generated = 1 << 4,
        /// <summary>
        /// An array kept in the object (<c>fixed T[] units;</c>, the last field of a final class): one allocation with the
        /// object, its length given by the constructor (the type's <c>$fixed{n}</c> method for its n-th constructor). It is
        /// reached by index and length only, never as an array value.
        /// </summary>
        InPlace = 1 << 5,
    }

    public sealed record FieldDef(string Name, HirType Type)
    {
        public FieldFlags Flags { get; init; }
        public Access Access { get; init; }
        public HirConstant? Value { get; init; }
        public HirAttribute[] Attributes { get; init; } = [];
        public SourceLocation Location { get; init; } = SourceLocation.None;

        /// <summary>The documentation the source wrote for it (null: none).</summary>
        public string? Doc { get; init; }
    }

    public enum MethodKind : byte { Normal, Constructor, StaticConstructor, Finalizer, Getter, Setter, Adder, Remover, Operator, Conversion }

    [Flags]
    public enum MethodFlags : ushort
    {
        None = 0,
        Static = 1 << 0, Virtual = 1 << 1, Override = 1 << 2, Abstract = 1 << 3, Final = 1 << 4,
        Async = 1 << 5, Iterator = 1 << 6, Unsafe = 1 << 7,
        /// <summary>The body is a runtime service: <see cref="MethodDef.ExternName"/>.</summary>
        Extern = 1 << 8,
        /// <summary>Imported from a C library (<c>[Import]</c>): called with the C convention.</summary>
        Import = 1 << 9,
        /// <summary>Exported to C (<c>[Export]</c>).</summary>
        Export = 1 << 10,
        Generated = 1 << 11,
        /// <summary>Conversions: implicit (else explicit).</summary>
        Implicit = 1 << 12,
        /// <summary>Inlined wherever it is called (<c>[Inline]</c>).</summary>
        Inline = 1 << 13,
        /// <summary>Never inlined (<c>[NoInline]</c>: a rarely taken path kept out of its callers).</summary>
        NoInline = 1 << 14,
        /// <summary>Rarely run (once, or on failures): compiled for size.</summary>
        Cold = 1 << 15,
    }

    [Flags]
    public enum ParameterFlags : byte
    {
        None = 0,
        /// <summary>The last parameter takes any number of arguments as an array.</summary>
        Params = 1 << 0,
        /// <summary>Has <see cref="Parameter.Default"/>.</summary>
        Optional = 1 << 1,
    }

    public sealed record Parameter(string Name, HirType Type)
    {
        public ParameterFlags Flags { get; init; }
        public HirConstant? Default { get; init; }
        public HirAttribute[] Attributes { get; init; } = [];
    }

    public sealed class MethodDef
    {
        public MethodDef(string name, HirType result, params Parameter[] parameters)
        {
            Name = name;
            Result = result;
            Parameters = [.. parameters];
        }

        public string Name { get; set; }
        public MethodKind Kind { get; set; }
        public MethodFlags Flags { get; set; }
        public Access Access { get; set; }
        public List<GenericParam> Generics { get; } = [];
        public List<Parameter> Parameters { get; }
        public HirType Result { get; set; }

        /// <summary><see cref="MethodFlags.Extern"/>: the runtime service; <see cref="MethodFlags.Import"/>: the C symbol.</summary>
        public string? ExternName { get; set; }

        /// <summary><see cref="MethodFlags.Import"/>: the library (<c>user32.dll</c>, <c>libc.so.6</c>).</summary>
        public string? Library { get; set; }

        public List<HirAttribute> Attributes { get; } = [];
        public SourceLocation Location { get; set; } = SourceLocation.None;

        /// <summary>The documentation the source wrote for it (null: none).</summary>
        public string? Doc { get; set; }

        public bool IsStatic => (Flags & MethodFlags.Static) != 0;

        private HirBody? body;
        private Func<HirBody>? pending;

        /// <summary>The body (null for abstract, extern and imported methods). Read from a file when first asked for.</summary>
        public HirBody? Body
        {
            get
            {
                if (pending is not null)
                {
                    body = pending();
                    pending = null;
                }
                return body;
            }
            set { body = value; pending = null; }
        }

        /// <summary>Whether the body is still in the file (not decoded yet).</summary>
        public bool BodyPending => pending is not null;

        internal void LoadBodyLater(Func<HirBody> load) => pending = load;

        public override string ToString() => Name;
    }

    [Flags]
    public enum MemberFlags : byte { None = 0, Static = 1 << 0, Virtual = 1 << 1, Override = 1 << 2, Abstract = 1 << 3 }

    /// <summary>A property (or an indexer, with <see cref="Indices"/>): its accessors are methods of the same type.</summary>
    public sealed record PropertyDef(string Name, HirType Type)
    {
        public HirType[] Indices { get; init; } = [];
        public int Getter { get; init; } = -1;
        public int Setter { get; init; } = -1;
        public MemberFlags Flags { get; init; }
        public Access Access { get; init; }
        public HirAttribute[] Attributes { get; init; } = [];
        public SourceLocation Location { get; init; } = SourceLocation.None;

        /// <summary>The documentation the source wrote for it (null: none).</summary>
        public string? Doc { get; init; }
    }

    public sealed record EventDef(string Name, HirType Type)
    {
        public int Adder { get; init; } = -1;
        public int Remover { get; init; } = -1;
        public MemberFlags Flags { get; init; }
        public Access Access { get; init; }
        public HirAttribute[] Attributes { get; init; } = [];
    }

    public enum Fixity : byte { Prefix, Postfix, Binary }

    public enum Associativity : byte { Left, Right, None }

    /// <summary>
    /// An operator the type declares: its symbol (built-in or one of the program's own), how it parses, and the method that
    /// implements it. Frontends read these to parse code that uses the operator.
    /// </summary>
    public sealed record OperatorDef(string Symbol, Fixity Fixity, int Method)
    {
        public int Precedence { get; init; }
        public Associativity Associativity { get; init; }
    }

    /// <summary>An attribute: its constructor, positional arguments and named ones (fields or properties).</summary>
    public sealed record HirAttribute(MemberToken Constructor, HirConstant[] Arguments)
    {
        public (string Name, HirConstant Value)[] Named { get; init; } = [];
    }

    public sealed record HirResource(string Name, byte[] Data);

    /// <summary>Where a declaration or statement is: <c>File</c> indexes <see cref="HirAssembly.Files"/> (-1: unknown).</summary>
    public readonly record struct SourceLocation(int File, int Line, int Column)
    {
        public static readonly SourceLocation None = new(-1, 0, 0);
        public bool IsKnown => File >= 0 && Line > 0;
    }

    /// <summary>A compile-time constant (field values, defaults, attribute arguments, literals).</summary>
    public abstract record HirConstant
    {
        public static HirConstant Int(long value, Primitive type = Primitive.I32) => new IntegerConstant(value, type);
    }

    public sealed record IntegerConstant(BigInteger Value, Primitive Type) : HirConstant;

    /// <summary>A float, as its IEEE bit pattern in <see cref="Type"/>'s width.</summary>
    public sealed record FloatConstant(BigInteger Bits, Primitive Type) : HirConstant
    {
        public static FloatConstant Of(double value) => new(new BigInteger(BitConverter.DoubleToUInt64Bits(value)), Primitive.F64);
        public static FloatConstant Of(float value) => new(new BigInteger(BitConverter.SingleToUInt32Bits(value)), Primitive.F32);
    }

    public sealed record BoolConstant(bool Value) : HirConstant;

    public sealed record CharConstant(int Value) : HirConstant;

    public sealed record StringConstant(string Value) : HirConstant;

    public sealed record NullConstant : HirConstant;

    public sealed record TypeConstant(HirType Type) : HirConstant;

    /// <summary>
    /// A member of an enum type, by value. An enum whose base is a string (<c>enum Word : str</c>) holds its members'
    /// texts in <see cref="Text"/>; its values are the members' places (0, 1, …), and that is what a <c>Word</c> is at run time.
    /// </summary>
    public sealed record EnumConstant(HirType Type, BigInteger Value, string? Text = null) : HirConstant;

    public sealed record ArrayConstant(HirType Element, HirConstant[] Items) : HirConstant
    {
        public bool Equals(ArrayConstant? other) => other is not null && Element == other.Element && Items.AsSpan().SequenceEqual(other.Items);
        public override int GetHashCode() => HashCode.Combine(Element, Items.Length);
    }
}
