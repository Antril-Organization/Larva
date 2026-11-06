using Larva.Hir.Encoding;

namespace Larva.Hir.Linking
{
    /// <summary>What linking made: the program as one assembly (null when something did not resolve), and what went wrong.</summary>
    public sealed class HirLinkResult
    {
        public required HirAssembly? Assembly { get; init; }
        public required IReadOnlyList<string> Errors { get; init; }
        public bool Success => Assembly is not null && Errors.Count == 0;
    }

    /// <summary>
    /// Links Larva HIR assemblies (<c>.lva</c>) into one: the types of each, after those of the assemblies it references,
    /// with every reference to another assembly's type or member made a reference to its definition. The result has no
    /// references left, so lowering sees the whole program; method bodies stay unread until lowering asks for them (they
    /// are read already linked).
    /// </summary>
    public static class HirLinker
    {
        /// <summary>
        /// Links <paramref name="inputs"/> (the bytes of <c>.lva</c> files, in any order: each comes after the assemblies it
        /// references) into an assembly named <paramref name="name"/>. The entry point is the last one an input has.
        /// </summary>
        public static HirLinkResult Link(string name, IReadOnlyList<byte[]> inputs)
        {
            var errors = new List<string>();
            // Pass 1: each assembly's own view (its references unresolved), to number the types of all of them.
            var views = inputs.Select(bytes => LvaFormat.Read(bytes)).ToList();
            var order = Order(views, errors);
            if (order is null)
                return new HirLinkResult { Assembly = null, Errors = errors };
            var offsets = new int[views.Count];
            var fileOffsets = new int[views.Count];
            var byName = new Dictionary<(string Assembly, string Namespace, string Name, int Arity), int>();
            int typeCount = 0, fileCount = 0;
            var origins = new List<HirOrigin>();
            foreach (int a in order)
            {
                offsets[a] = typeCount;
                fileOffsets[a] = fileCount;
                var view = views[a];
                origins.Add(new HirOrigin(view.Name, typeCount, view.Types.Count));
                for (int t = 0; t < view.Types.Count; t++)
                {
                    var type = view.Types[t];
                    if (!byName.TryAdd((view.Name, type.Namespace, type.Name, type.Generics.Count), typeCount + t))
                        errors.Add($"{view.Name}: `{type.FullName}` is defined twice");
                }
                typeCount += view.Types.Count;
                fileCount += view.Files.Count;
            }

            var merged = new HirAssembly { Name = name };
            merged.Origins.AddRange(origins);
            // Pass 2: each assembly read again with its tokens mapped into the merged numbering (references first).
            foreach (int a in order)
            {
                var view = views[a];
                int offset = offsets[a];
                TypeToken MapType(TypeToken token)
                {
                    if (token.IsDefinition)
                        return new TypeToken(offset + token.Index);
                    int index = ~token.Value;
                    if (index < 0 || index >= view.TypeRefs.Count)
                    {
                        errors.Add($"{view.Name}: type reference {index} is out of range");
                        return token;
                    }
                    var reference = view.TypeRefs[index];
                    string target = reference.Assembly < view.References.Count ? view.References[reference.Assembly].Name : "?";
                    if (byName.TryGetValue((target, reference.Namespace, reference.Name, reference.Arity), out int found))
                        return new TypeToken(found);
                    errors.Add($"{view.Name}: `{reference.Namespace}.{reference.Name}` is not in `{target}`");
                    return token;
                }
                // Every member reference resolved now (they name members of assemblies linked already): bodies are read
                // later, on several threads, and only look them up.
                var members = new MemberToken[view.MemberRefs.Count];
                for (int m = 0; m < members.Length; m++)
                    members[m] = Resolve(view, m, merged, MapType, errors);
                MemberToken MapMember(MemberToken token) =>
                    token.IsDefinition ? MemberToken.Def(offset + token.Type, token.Member)
                    : token.Member < members.Length ? members[token.Member] : throw new HirFormatException($"{view.Name}: member reference {token.Member} is out of range");
                var linked = LvaReader.Read(inputs[a], new TokenMap(MapType, MapMember, fileOffsets[a]));
                merged.Types.AddRange(linked.Types);
                merged.Files.AddRange(linked.Files);
                merged.Attributes.AddRange(linked.Attributes);
                merged.Resources.AddRange(linked.Resources);
                if (linked.EntryPoint is { } entry)
                    merged.EntryPoint = entry;
            }
            return new HirLinkResult { Assembly = errors.Count == 0 ? merged : null, Errors = errors };
        }

        /// <summary>The assemblies in an order where each comes after those it references (null when they reference in a circle).</summary>
        private static List<int>? Order(List<HirAssembly> views, List<string> errors)
        {
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int a = 0; a < views.Count; a++)
            {
                if (!index.TryAdd(views[a].Name, a))
                    errors.Add($"two assemblies are named `{views[a].Name}` (a program and a library it uses, say): rename one (a project's PROJ name, or a library's assembly)");
            }
            var state = new byte[views.Count];
            var order = new List<int>();
            bool Visit(int a)
            {
                if (state[a] == 2)
                    return true;
                if (state[a] == 1)
                {
                    errors.Add($"`{views[a].Name}` references itself through other assemblies");
                    return false;
                }
                state[a] = 1;
                foreach (var reference in views[a].References)
                {
                    if (!index.TryGetValue(reference.Name, out int target))
                    {
                        errors.Add($"`{views[a].Name}` references `{reference.Name}`, which is not linked");
                        continue;
                    }
                    if (!Visit(target))
                        return false;
                }
                state[a] = 2;
                order.Add(a);
                return true;
            }
            for (int a = 0; a < views.Count; a++)
            {
                if (!Visit(a))
                    return null;
            }
            return errors.Count == 0 ? order : null;
        }

        /// <summary>A member reference of <paramref name="view"/> as the definition it names in the merged assembly.</summary>
        private static MemberToken Resolve(HirAssembly view, int index, HirAssembly merged, Func<TypeToken, TypeToken> mapType, List<string> errors)
        {
            if (index >= view.MemberRefs.Count)
            {
                errors.Add($"{view.Name}: member reference {index} is out of range");
                return MemberToken.Def(0, 0);
            }
            var reference = view.MemberRefs[index];
            var parent = mapType(reference.Parent);
            if (!parent.IsDefinition || parent.Index >= merged.Types.Count)
            {
                errors.Add($"{view.Name}: `{reference.Name}` belongs to a type that is not linked yet");
                return MemberToken.Def(0, 0);
            }
            var type = merged.Types[parent.Index];
            var parameters = reference.Parameters.Select(p => MapTypes(p, mapType)).ToArray();
            var result = MapTypes(reference.Result, mapType);
            int found = -1;
            switch (reference.Kind)
            {
                case MemberKind.Method:
                    found = type.Methods.FindIndex(m => m.Name == reference.Name && m.Generics.Count == reference.GenericArity
                        && m.Parameters.Select(p => p.Type).SequenceEqual(parameters) && m.Result == result);
                    break;
                case MemberKind.Field:
                    found = type.Fields.FindIndex(f => f.Name == reference.Name);
                    break;
                case MemberKind.Property:
                    found = type.Properties.FindIndex(p => p.Name == reference.Name);
                    break;
                case MemberKind.Event:
                    found = type.Events.FindIndex(e => e.Name == reference.Name);
                    break;
            }
            if (found < 0)
            {
                errors.Add($"{view.Name}: `{type.FullName}` has no {reference.Kind.ToString().ToLowerInvariant()} `{reference.Name}` with that signature");
                return MemberToken.Def(parent.Index, 0);
            }
            return MemberToken.Def(parent.Index, found);
        }

        /// <summary><paramref name="type"/> with the type tokens in it mapped.</summary>
        public static HirType MapTypes(HirType type, Func<TypeToken, TypeToken> map) => type switch
        {
            NamedType n => new NamedType(map(n.Type), [.. n.Arguments.Select(a => MapTypes(a, map))]),
            ArrayType a => a with { Element = MapTypes(a.Element, map) },
            InlineArrayType i => i with { Element = MapTypes(i.Element, map), SizeOf = i.SizeOf is null ? null : MapTypes(i.SizeOf, map) },
            NullableType n => new NullableType(MapTypes(n.Inner, map)),
            ReferenceType r => r with { Target = MapTypes(r.Target, map) },
            PointerType p => p with { Target = MapTypes(p.Target, map) },
            FuncType f => new FuncType([.. f.Parameters.Select(x => MapTypes(x, map))], MapTypes(f.Result, map)),
            NativeFuncType f => new NativeFuncType([.. f.Parameters.Select(x => MapTypes(x, map))], MapTypes(f.Result, map)),
            PackExpansionType p => new PackExpansionType(MapTypes(p.Pattern, map)),
            _ => type,
        };
    }
}
