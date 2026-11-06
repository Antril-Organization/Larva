using Larva.IR;
using Larva.IR.Lowering;
using Larva.IR.Reflection;
using Larva.IR.Text;

namespace Larva.Runtime
{
    /// <summary>A type at run time: name, layout and fields (as far as its reflection level allows).</summary>
    public sealed class TypeInfo
    {
        private readonly RuntimeModule module;
        private readonly FieldInfo[]? fields;
        private readonly Dictionary<string, TypeInfo> instances = [];

        public IrType Type { get; }
        public string Name { get; }
        public ReflectionLevel Level { get; }

        /// <summary>Size, alignment, flags and GC pointer map; <c>null</c> for generic definitions and unsized types.</summary>
        public TypeLayout? Layout { get; }

        public GenericParam[] Generics { get; }
        public bool IsGenericDefinition => Generics.Length > 0;

        private TypeInfo(RuntimeModule module, IrType type, string name, ReflectionLevel level, TypeLayout? layout,
            GenericParam[] generics, FieldInfo[]? fields)
        {
            this.module = module;
            Type = type;
            Name = name;
            Level = level;
            Layout = layout;
            Generics = generics;
            this.fields = fields;
        }

        /// <summary>Fields with names and offsets. Requires <see cref="ReflectionLevel.Layout"/>.</summary>
        public IReadOnlyList<FieldInfo> Fields =>
            fields ?? throw new ReflectionException(Level < ReflectionLevel.Layout
                ? $"fields of `{DisplayName}` were trimmed (reflection level {Level.ToString().ToLowerInvariant()})"
                : $"`{DisplayName}` has no fields");

        public FieldInfo GetField(string name) =>
            Fields.FirstOrDefault(f => f.Name == name) ?? throw new ReflectionException($"`{DisplayName}` has no field `{name}`");

        private string DisplayName => Name.Length > 0 ? Name : "<trimmed type>";

        internal static TypeInfo ForStruct(RuntimeModule module, int id, StructMetadata def)
        {
            var type = new StructType(id);
            FieldInfo[]? fields = null;
            if (def.Level >= ReflectionLevel.Layout && def.FieldOffsets.Length == def.Fields.Length && !def.IsGenericDefinition)
                fields = MakeFields(module, def.FieldNames, def.Fields, def.FieldOffsets);
            return new TypeInfo(module, type, def.Name, def.Level, def.Layout, def.Generics, fields);
        }

        internal static TypeInfo ForType(RuntimeModule module, IrType type)
        {
            if (type is not StructType st)
                return new TypeInfo(module, type, IrPrinter.FormatType(type, module.Shapes), ReflectionLevel.Full, Layouts.Of(type, module.Shapes), [], null);

            // Run-time instantiation of a generic struct: layout and offsets computed here, with the definition's names.
            var def = module.Metadata.Structs[st.Id];
            var offsets = Layouts.FieldOffsets(st, module.Shapes);
            FieldInfo[]? fields = def.Level >= ReflectionLevel.Layout && offsets is not null
                ? MakeFields(module, def.FieldNames, [.. def.Fields.Select(f => f.Substitute(st.Arguments))], offsets)
                : null;
            return new TypeInfo(module, type, def.Level >= ReflectionLevel.Layout ? IrPrinter.FormatType(type, module.Shapes) : "",
                def.Level, Layouts.Of(type, module.Shapes), [], fields);
        }

        private static FieldInfo[] MakeFields(RuntimeModule module, string[] names, IrType[] types, int[] offsets) =>
            [.. types.Select((t, i) => new FieldInfo(module, i < names.Length ? names[i] : $"_{i}", i, offsets[i], t))];

        /// <summary>
        /// Instantiates a generic struct with types known only at run time. Checks the constraints, then computes the layout
        /// from the definition: no code is needed for a type, so this always works in an AOT image.
        /// </summary>
        public TypeInfo MakeGeneric(params TypeInfo[] arguments)
        {
            if (!IsGenericDefinition)
                throw new ReflectionException($"`{DisplayName}` is not a generic definition");
            if (Level < ReflectionLevel.Full)
                throw new ReflectionException($"`{DisplayName}` needs `#[reflect(full)]` to be instantiated at run time");
            CheckArguments(module, DisplayName, Generics, arguments);

            string key = string.Join(",", arguments.Select(a => a.Type.ToString()));
            if (!instances.TryGetValue(key, out var instance))
                instances[key] = instance = module.TypeOf(new StructType(((StructType)Type).Id, [.. arguments.Select(a => a.Type)]));
            return instance;
        }

        internal static void CheckArguments(RuntimeModule module, string target, GenericParam[] generics, TypeInfo[] arguments)
        {
            if (arguments.Length != generics.Length)
                throw new ReflectionException($"`{target}` takes {generics.Length} type argument(s), got {arguments.Length}");
            for (int i = 0; i < generics.Length; i++)
            {
                if (!Constraints.Satisfies(arguments[i].Type, generics[i].Constraint, module.Context))
                    throw new UnsatisfiedConstraintException(
                        $"`{arguments[i].Name}` does not satisfy `{generics[i].Name}: {IrPrinter.FormatConstraint(generics[i].Constraint, module.Shapes, generics)}` required by `{target}`");
            }
        }

        public override string ToString() => DisplayName;
    }

    /// <summary>A field: name, position, byte offset and type.</summary>
    public sealed class FieldInfo
    {
        private readonly RuntimeModule module;

        public string Name { get; }
        public int Index { get; }
        public int Offset { get; }
        public IrType Type { get; }

        internal FieldInfo(RuntimeModule module, string name, int index, int offset, IrType type)
        {
            this.module = module;
            Name = name;
            Index = index;
            Offset = offset;
            Type = type;
        }

        public TypeInfo FieldType => module.TypeOf(Type);

        /// <summary>Reads the field through a live borrow (shared or exclusive). Struct-typed fields come back as a view.</summary>
        public object GetValue(Borrow borrow)
        {
            borrow.EnsureAlive();
            return Type is StructType
                ? borrow.Target.View(FieldType, Offset)
                : Values.Read(Type, borrow.Target.Span(Offset));
        }

        /// <summary>Writes the field; needs an exclusive borrow, exactly as a static <c>&amp;mut</c> would.</summary>
        public void SetValue(Borrow borrow, object value)
        {
            borrow.EnsureAlive();
            if (!borrow.IsMutable)
                throw new BorrowConflictException($"cannot write `{Name}` through a shared borrow; use BorrowMut()");
            Values.Write(Type, borrow.Target.Span(Offset), value, Name);
        }

        public override string ToString() => $"{Name} @{Offset}: {IrPrinter.FormatType(Type, module.Shapes)}";
    }
}
