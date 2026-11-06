using Larva.Hir;
using Larva.Hir.Services;

namespace Larva.Bindings
{
    /// <summary>
    /// From a description (and, when given, the library's real exports) to a Larva HIR assembly: enums, structs and unions
    /// as value types, and static classes whose methods are <c>[Import]</c> declarations, each in its namespace. Any
    /// frontend on Larva can reference it, and a language's view writes it as that language's source, in one file or in
    /// the files the description says (<see cref="Files"/>).
    /// The calls are direct (the C convention, no marshalling beyond a string's temporary copy): zero cost.
    /// </summary>
    public static class BindingGenerator
    {
        /// <summary>
        /// The assembly for <paramref name="description"/>. With <paramref name="exports"/> (the binary's names), every
        /// entry must be among them: the missing ones are an error naming them all. Strings and layouts in the
        /// description make the assembly reference System.Core.
        /// </summary>
        public static HirAssembly Build(BindingDescription description, IReadOnlyList<string>? exports = null)
        {
            if (exports is not null)
            {
                var known = new HashSet<string>(exports, StringComparer.Ordinal);
                var missing = description.Functions.Where(f => !known.Contains(f.Entry)).Select(f => f.Entry).ToList();
                if (missing.Count > 0)
                    throw new BindingException($"{description.Library} does not export: {string.Join(", ", missing)}");
            }
            var assembly = new HirAssembly { Name = description.Assembly };

            // Enums first: structs and functions name them.
            var enums = new Dictionary<string, TypeToken>(StringComparer.Ordinal);
            foreach (var definition in description.Enums)
            {
                var underlying = (PrimitiveType)BindingDescription.ParseType(definition.Underlying, 0, assembly, parameter: false);
                var enumeration = new TypeDef
                {
                    Namespace = definition.Namespace ?? description.Namespace,
                    Name = Name(definition.Name),
                    Kind = definition.IsFlags ? TypeKind.Bitflags : TypeKind.Enum,
                    Access = Access.Public,
                    Underlying = underlying,
                    Doc = $"{(definition.IsFlags ? "Flags" : "Values")} of {description.Library}.",
                };
                var token = assembly.Add(enumeration);
                enums[definition.Name] = token;
                var self = new NamedType(token, []);
                foreach (var (name, value) in definition.Members)
                {
                    enumeration.Fields.Add(new FieldDef(Name(name), self)
                    {
                        Flags = FieldFlags.Static | FieldFlags.Const,
                        Access = Access.Public,
                        Value = new EnumConstant(self, value),
                    });
                }
            }

            var structs = new Dictionary<string, TypeToken>(StringComparer.Ordinal);
            var values = new List<TypeDef>();
            foreach (var definition in description.Structs)
            {
                var value = new TypeDef
                {
                    Namespace = definition.Namespace ?? description.Namespace,
                    Name = Name(definition.Name),
                    Kind = TypeKind.Struct,
                    Access = Access.Public,
                    Doc = definition.IsUnion
                        ? $"C union {definition.Name} of {description.Library}: its members share the same bytes."
                        : $"C struct {definition.Name} of {description.Library}: its fields in C's layout.",
                };
                if (definition.IsUnion || definition.Pack > 0)
                    value.Attributes.Add(LayoutAttribute(assembly, definition.IsUnion, definition.Pack));
                structs[definition.Name] = assembly.Add(value); // all added first: a field may point to a struct declared below
                values.Add(value);
            }
            for (int s = 0; s < description.Structs.Count; s++)
            {
                var definition = description.Structs[s];
                foreach (var (name, fieldType) in definition.Fields)
                {
                    values[s].Fields.Add(new FieldDef(Name(name), BindingDescription.ParseType(fieldType, s + 1, assembly, parameter: false, structs, field: true, enums))
                    {
                        Access = Access.Public,
                        // A union: every member at offset 0, the value as large and aligned as its largest member.
                        Attributes = definition.IsUnion ? [OffsetAttribute(assembly, 0)] : [],
                    });
                }
            }

            // One static class per namespace and class name, in the order they first appear.
            var classes = new Dictionary<(string Namespace, string Class), TypeDef>();
            TypeDef ClassOf(string? ns, string? name)
            {
                var key = (ns ?? description.Namespace, name ?? description.Class);
                if (!classes.TryGetValue(key, out var type))
                {
                    type = new TypeDef
                    {
                        Namespace = key.Item1,
                        Name = Name(key.Item2),
                        Kind = TypeKind.Class,
                        Flags = TypeFlags.Static,
                        Access = Access.Public,
                        Doc = $"Functions of {description.Library}, called directly.",
                    };
                    classes[key] = type;
                }
                return type;
            }
            foreach (var constant in description.Constants)
            {
                bool wide = constant.Value < int.MinValue || constant.Value > int.MaxValue;
                ClassOf(constant.Namespace, constant.Class).Fields.Add(new FieldDef(Name(constant.Name), wide ? HirType.I64 : HirType.I32)
                {
                    Flags = FieldFlags.Static | FieldFlags.Readonly,
                    Access = Access.Public,
                    Value = new IntegerConstant(constant.Value, wide ? Primitive.I64 : Primitive.I32),
                });
            }
            int line = 0;
            foreach (var function in description.Functions)
            {
                line++;
                var parameters = function.Parameters.Select(p => new Parameter(Name(p.Name), BindingDescription.ParseType(p.Type, line, assembly, parameter: true, structs, enums: enums))).ToArray();
                var result = BindingDescription.ParseType(function.Result, line, assembly, parameter: false, structs, enums: enums);
                ClassOf(function.Namespace, function.Class).Methods.Add(new MethodDef(Name(function.Name), result, parameters)
                {
                    Flags = MethodFlags.Static | MethodFlags.Import,
                    Access = Access.Public,
                    Kind = MethodKind.Normal,
                    ExternName = function.Entry,
                    Library = description.Library,
                });
            }
            if (classes.Count == 0)
                ClassOf(null, null);
            foreach (var type in classes.Values)
                assembly.Add(type);
            return assembly;
        }

        /// <summary>
        /// Each type's source file (by its full name: `Namespace.Name`), from the description's <c>file</c> placements;
        /// types without one are not in the map.
        /// </summary>
        public static Dictionary<string, string> Files(BindingDescription description)
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            void Put(string? ns, string name, string? file)
            {
                if (file is not null)
                    files.TryAdd($"{ns ?? description.Namespace}.{Name(name)}", file);
            }
            foreach (var e in description.Enums)
                Put(e.Namespace, e.Name, e.File);
            foreach (var s in description.Structs)
                Put(s.Namespace, s.Name, s.File);
            foreach (var c in description.Constants)
                Put(c.Namespace, c.Class ?? description.Class, c.File);
            foreach (var f in description.Functions)
                Put(f.Namespace, f.Class ?? description.Class, f.File);
            return files;
        }

        private const string Interop = "System.Runtime.InteropServices";

        /// <summary>
        /// The words names are not (the keywords of the languages installed: larva sets them from their packs): a C name that
        /// is one (`internal`, `loop`, `delete`…) gets a `_` after it; the C symbol keeps its own.
        /// </summary>
        public static IReadOnlySet<string> Reserved { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The extension of the source files a profile splits the bindings into (the language's: larva sets it).</summary>
        public static string SourceExtension { get; set; } = "";

        internal static string Name(string name) => Reserved.Contains(name) ? name + "_" : name;

        /// <summary>`[ValueLayout(Layout.Explicit)]` (a union) or `[ValueLayout(Layout.Sequential, Pack = n)]`, referencing System.Core.</summary>
        private static HirAttribute LayoutAttribute(HirAssembly assembly, bool isExplicit, int pack)
        {
            var layout = new NamedType(assembly.Reference("System.Core", Interop, "Layout"), []);
            var attribute = assembly.Reference("System.Core", Interop, "ValueLayoutAttribute");
            var constructor = assembly.Reference(new MemberRef(MemberKind.Method, attribute, ".new", 0, [layout], HirType.Void));
            return new HirAttribute(constructor, [new EnumConstant(layout, isExplicit ? 1 : 0)])
            {
                Named = pack > 0 ? [("Pack", new IntegerConstant(pack, Primitive.I32))] : [],
            };
        }

        /// <summary>`[FieldOffset(n)]`, referencing System.Core.</summary>
        private static HirAttribute OffsetAttribute(HirAssembly assembly, int offset)
        {
            var attribute = assembly.Reference("System.Core", Interop, "FieldOffsetAttribute");
            var constructor = assembly.Reference(new MemberRef(MemberKind.Method, attribute, ".new", 0, [HirType.I32], HirType.Void));
            return new HirAttribute(constructor, [new IntegerConstant(offset, Primitive.I32)]);
        }
    }
}
