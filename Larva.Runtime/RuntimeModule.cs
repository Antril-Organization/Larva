using Larva.IR;
using Larva.IR.Lowering;
using Larva.IR.Reflection;
using Larva.IR.Text;

namespace Larva.Runtime
{
    /// <summary>
    /// The reflection view of a compiled module, built from its <see cref="ModuleMetadata"/> alone (as an AOT image would
    /// ship it). Types created at run time (<see cref="TypeInfo.MakeGeneric"/>) and the witness tables handed to shared
    /// generic code are registered here.
    /// </summary>
    public sealed class RuntimeModule
    {
        private readonly Dictionary<IrType, TypeInfo> types = [];
        private readonly Dictionary<string, TypeInfo> typesByName = [];
        private readonly Dictionary<string, MethodInfo> methods = [];
        private readonly List<TypeLayout> witnesses = [];
        private readonly List<IrType> witnessTypes = [];
        private readonly Dictionary<IrType, int> witnessIds = [];

        public ModuleMetadata Metadata { get; }

        /// <summary>Struct layouts and constraints, as the compiler's IR sees them (for layouts of run-time instantiations).</summary>
        internal IrModule Shapes { get; }

        internal TypeContext Context { get; }

        private RuntimeModule(ModuleMetadata metadata)
        {
            Metadata = metadata;
            Shapes = new IrModule(
                [.. metadata.Structs.Select(s => new StructDef(s.Name, s.Fields) { Generics = s.Generics, FieldNames = s.FieldNames })], []);
            Context = new TypeContext(Shapes, []);

            for (int id = 0; id < metadata.Structs.Length; id++)
            {
                var def = metadata.Structs[id];
                var info = TypeInfo.ForStruct(this, id, def);
                types[info.Type] = info;
                if (def.Name.Length > 0 && def.InstanceOf is null)
                    typesByName[def.Name] = info;
            }
            foreach (var method in metadata.Methods)
                methods[method.Name] = new MethodInfo(this, method);
        }

        public static RuntimeModule Load(ReadOnlySpan<byte> metadata) => new(MetadataEncoding.Decode(metadata));

        public static RuntimeModule From(ModuleMetadata metadata) => new(metadata);

        public IEnumerable<TypeInfo> Types => typesByName.Values;
        public IEnumerable<MethodInfo> Methods => methods.Values;

        public TypeInfo GetType(string name) =>
            typesByName.TryGetValue(name, out var type) ? type
            : throw new ReflectionException($"no type named `{name}` (unknown, or trimmed below `layout`)");

        public MethodInfo GetMethod(string name) =>
            methods.TryGetValue(name, out var method) ? method
            : throw new ReflectionException($"no method named `{name}` (only `#[reflect(full)]` functions are invocable)");

        /// <summary>The <see cref="TypeInfo"/> of any concrete type: primitives, pointers, compiled or run-time instantiated structs.</summary>
        public TypeInfo TypeOf(IrType type)
        {
            if (types.TryGetValue(type, out var known))
                return known;
            if (type.IsGeneric())
                throw new ReflectionException($"`{IrPrinter.FormatType(type, Shapes)}` is open; instantiate its generic parameters first");

            // A compiled instantiation (e.g. Pair<i32, f64>) is listed under its own id; prefer it.
            if (type is StructType { Arguments.Length: > 0 } generic)
            {
                for (int id = 0; id < Metadata.Structs.Length; id++)
                {
                    if (Metadata.Structs[id].InstanceOf == generic)
                        return types[generic] = types[new StructType(id)];
                }
            }

            var info = TypeInfo.ForType(this, type);
            types[type] = info;
            return info;
        }

        /// <summary>Id of the witness table for <paramref name="type"/>, created on first use (what <c>gen.wit</c> would push).</summary>
        public int WitnessFor(TypeInfo type)
        {
            if (witnessIds.TryGetValue(type.Type, out int id))
                return id;
            var layout = type.Layout ?? throw new ReflectionException($"`{type.Name}` has no layout and cannot be a type argument");
            witnesses.Add(layout);
            witnessTypes.Add(type.Type);
            witnessIds[type.Type] = witnesses.Count - 1;
            return witnesses.Count - 1;
        }

        public TypeLayout Witness(int id) => witnesses[id];

        /// <summary>The type a witness id stands for (how the interpreter turns invoke slots back into type arguments).</summary>
        public IrType WitnessType(int id) => witnessTypes[id];

        /// <summary>Allocates a zero-initialized value of <paramref name="type"/> (simulated memory, as the VM's allocator would).</summary>
        public RuntimeObject New(TypeInfo type)
        {
            var layout = type.Layout ?? throw new ReflectionException($"`{type.Name}` has no layout and cannot be created");
            return RuntimeObject.Allocate(type, layout.Size);
        }
    }
}
