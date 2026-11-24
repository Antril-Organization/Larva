using Larva.Hir;

namespace Larva.Hir.Lowering
{
    /// <summary>
    /// A program's <c>Main</c>, as the languages write it, made into what an executable starts:
    /// <list type="bullet">
    /// <item><c>Main(str[] args)</c>: an entry <c>$Main()</c> beside it reads the command line
    /// (<c>System.Environment.GetCommandLineArgs().ToArray()</c>) and calls it.</item>
    /// <item><c>gc: custom</c>: the entry first installs the project's collector (<c>System.GC.Install(new Collector())</c>).</item>
    /// <item>A <c>Main</c> without parameters, or with the command line, that returns nothing or an integer is a
    /// <em>program</em>: it takes any number of words on its command line, and its integer is the process's exit code.</item>
    /// </list>
    /// Other entries (integers, bools or floats as parameters; a bool or float result) start as they always did: the
    /// command line gives their numbers and their result is printed.
    /// </summary>
    public static class ProgramEntry
    {
        /// <summary>
        /// The entry the executable starts, and whether it is a program. Adds <c>$Main()</c> to the entry's type when
        /// <c>Main</c> takes the command line or <paramref name="collector"/> (the full name of the project's collector
        /// class) is to be installed first; null with <paramref name="error"/> when that cannot be done.
        /// </summary>
        public static (MemberToken Entry, bool Program)? Prepare(HirAssembly assembly, MemberToken entry, out string? error, string? collector = null)
        {
            error = null;
            if (!entry.IsDefinition)
                return (entry, false);
            var type = assembly.Types[entry.Type];
            var main = type.Methods[entry.Member];
            bool integerOrNothing = main.Result is PrimitiveType { Kind: var kind } && (kind == Primitive.Void || IsInteger(kind));
            var first = new List<Stmt>();
            if (collector is not null)
            {
                if (Install(assembly, collector, out error) is not { } install)
                    return null;
                first.Add(install);
            }
            if (main.Parameters.Count == 0)
                return first.Count == 0 ? (entry, integerOrNothing) : (Made(type, entry, main, first, []), integerOrNothing);
            if (main.Parameters.Count != 1 || main.Parameters[0].Type is not ArrayType { Rank: 1 } words)
            {
                if (first.Count > 0)
                {
                    error = $"gc.collector: {type.FullName}.{main.Name} takes numbers from the command line; a program with its own collector has a Main without them";
                    return null;
                }
                return (entry, false);
            }

            // Main(str[] args): System.Environment.GetCommandLineArgs() is a List<str>; its ToArray() the words.
            var arguments = Find(assembly, "System", "Environment", "GetCommandLineArgs", 0);
            if (arguments is not { } getArgs || assembly.Types[getArgs.Type].Methods[getArgs.Member].Result is not NamedType { Type.IsDefinition: true } list
                || list.Arguments is not [var element] || !element.Equals(words.Element))
            {
                error = $"{type.FullName}.{main.Name}: a program's command line is an array of text (System.Core's Environment.GetCommandLineArgs)";
                return null;
            }
            var listType = assembly.Types[list.Type.Index];
            int toArray = listType.Methods.FindIndex(m => m.Name == "ToArray" && m.Parameters.Count == 0 && !m.IsStatic);
            if (toArray < 0)
            {
                error = "System.Core has no List<T>.ToArray()";
                return null;
            }
            Expr read = new CallExpr(new CallExpr(null, getArgs, [], [], list), MemberToken.Def(list.Type.Index, toArray), [element], [], words);
            return (Made(type, entry, main, first, [read]), integerOrNothing);
        }

        /// <summary><c>$Main()</c>: <paramref name="first"/>, then Main(<paramref name="arguments"/>) and its result.</summary>
        private static MemberToken Made(TypeDef type, MemberToken entry, MethodDef main, List<Stmt> first, Expr[] arguments)
        {
            Expr call = new CallExpr(null, entry, [], arguments, main.Result);
            Stmt[] body = main.Result.Equals(HirType.Void) ? [.. first, new ExprStmt(call), new ReturnStmt(null)] : [.. first, new ReturnStmt(call)];
            int made = type.AddMethod(new MethodDef("$Main", main.Result)
            {
                Flags = MethodFlags.Static,
                Access = Access.Private,
                Location = main.Location,
                Body = new HirBody([], new BlockStmt(body)),
            });
            return MemberToken.Def(entry.Type, made);
        }

        /// <summary><c>System.GC.Install(new Collector());</c> for the class named <paramref name="collector"/>.</summary>
        private static Stmt? Install(HirAssembly assembly, string collector, out string? error)
        {
            error = null;
            var install = Find(assembly, "System", "GC", "Install", 1);
            int gcInterface = assembly.Types.FindIndex(t => t.Namespace == "System" && t.Name == "IGarbageCollector" && t.DeclaringType < 0);
            if (install is null || gcInterface < 0)
            {
                error = "gc: custom: System.Core has no GC.Install (a System.Core older than this larva)";
                return null;
            }
            int index = assembly.Types.FindIndex(t => t.FullName == collector && t.DeclaringType < 0);
            if (index < 0)
            {
                error = $"gc.collector: no class `{collector}` (Namespace.Class)";
                return null;
            }
            var type = assembly.Types[index];
            if (type.Kind != TypeKind.Class || type.Generics.Count > 0 || (type.Flags & (TypeFlags.Abstract | TypeFlags.Static)) != 0
                || !Implements(assembly, index, gcInterface))
            {
                error = $"gc.collector: `{collector}` is not a class that implements System.IGarbageCollector";
                return null;
            }
            // Its constructor without parameters (none written: the class is made as it is).
            bool constructors = type.Methods.Any(m => m.Kind == MethodKind.Constructor && !m.IsStatic);
            int constructor = type.Methods.FindIndex(m => m.Kind == MethodKind.Constructor && !m.IsStatic && m.Parameters.Count == 0);
            if (constructors && constructor < 0)
            {
                error = $"gc.collector: `{collector}` needs a constructor without parameters";
                return null;
            }
            var made = new NewExpr(constructor < 0 ? null : MemberToken.Def(index, constructor), [], [], new NamedType(TypeToken.Def(index), []));
            var parameter = assembly.Types[install.Value.Type].Methods[install.Value.Member].Parameters[0].Type;
            return new ExprStmt(new CallExpr(null, install.Value, [], [new ConvertExpr(made, ConversionKind.Upcast, parameter)], HirType.Void));
        }

        /// <summary>Whether type <paramref name="type"/> (or a base of it) lists interface <paramref name="target"/>.</summary>
        private static bool Implements(HirAssembly assembly, int type, int target)
        {
            for (int t = type; t >= 0;)
            {
                var def = assembly.Types[t];
                if (def.Interfaces.Any(i => i is NamedType { Type.IsDefinition: true } n && n.Type.Index == target))
                    return true;
                t = def.Base is NamedType { Type.IsDefinition: true } b ? b.Type.Index : -1;
            }
            return false;
        }

        private static bool IsInteger(Primitive kind) => kind is Primitive.I8 or Primitive.I16 or Primitive.I32 or Primitive.I64
            or Primitive.U8 or Primitive.U16 or Primitive.U32 or Primitive.U64;

        /// <summary>A static method of a non-generic type, by its namespace, type and name.</summary>
        private static MemberToken? Find(HirAssembly assembly, string space, string name, string method, int parameters)
        {
            for (int t = 0; t < assembly.Types.Count; t++)
            {
                var type = assembly.Types[t];
                if (type.Namespace != space || type.Name != name || type.DeclaringType >= 0 || type.Generics.Count > 0)
                    continue;
                int m = type.Methods.FindIndex(x => x.Name == method && x.IsStatic && x.Parameters.Count == parameters && x.Generics.Count == 0);
                if (m >= 0)
                    return MemberToken.Def(t, m);
            }
            return null;
        }
    }
}
