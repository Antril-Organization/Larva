namespace Larva.IR.Linking
{
    /// <summary>
    /// What a native back end can compile of a module: functions whose values it can represent, minus every function that
    /// calls or takes the address of one it cannot (transitively). The standard library declares members for every
    /// built-in type (System.Float256…), so a back end without one of them leaves those members, and whatever reaches them
    /// (their reflection, their boxes), out instead of failing; code a program runs never reaches them.
    /// </summary>
    public static class Compilable
    {
        /// <summary>Whether a function's values contain, at any depth (pointees, fields, type arguments), a primitive <paramref name="lacking"/> says the back end lacks.</summary>
        public static bool Uses(IrFunction function, IrModule module, Func<PrimitiveType, bool> lacking)
        {
            var visited = new HashSet<IrType>();
            bool Has(IrType type)
            {
                if (!visited.Add(type))
                    return false;
                switch (type)
                {
                    case PrimitiveType primitive:
                        return lacking(primitive);
                    case StructType st:
                        return st.Arguments.Any(Has)
                            || st.Id >= 0 && st.Id < module.Structs.Length && module.Structs[st.Id].Fields.Any(f => Has(f.Substitute(st.Arguments)));
                    case ReferenceType r: return Has(r.Pointee);
                    case PointerType p: return Has(p.Pointee);
                    case GcType g: return Has(g.Pointee);
                    case BoxType b: return Has(b.Pointee);
                    case OptionalType o: return Has(o.Inner);
                    case ArrayType a: return Has(a.Element);
                    case FixedArrayType f: return Has(f.Element);
                    case FunctionType f: return f.Parameters.Any(Has) || Has(f.Result);
                    default: return false;
                }
            }
            return function.Locals.Any(l => Has(l.Type));
        }

        /// <summary>
        /// The module with every function the back end cannot compile, but whose signature it can, replaced by a stub that
        /// stops the program (<c>unreachable</c>): tables of instances and reflection may name such a function (a member of
        /// System.Float256…) without the program ever calling it, so nothing that names it is left out.
        /// </summary>
        public static IrModule Stub(IrModule module, Func<IrFunction, IrModule, bool> supports)
        {
            IrFunction[]? functions = null;
            for (int f = 0; f < module.Functions.Length; f++)
            {
                var function = module.Functions[f];
                if (function.IsDeclaration || function.IsGeneric || supports(function, module))
                    continue;
                var signature = function with { Locals = function.Locals[..(function.ArgumentCount + 1)], Blocks = [] };
                if (!supports(signature, module))
                    continue;
                functions ??= [.. module.Functions];
                functions[f] = signature with { Blocks = [new BasicBlock([], new UnreachableTerminator())] };
            }
            return functions is null ? module : module with { Functions = functions };
        }

        public static bool[] Of(IrModule module, Func<IrFunction, IrModule, bool> supports)
        {
            var functions = module.Functions;
            var ok = new bool[functions.Length];
            var callers = new List<int>[functions.Length];
            for (int f = 0; f < functions.Length; f++)
            {
                ok[f] = functions[f].IsDeclaration || supports(functions[f], module);
                callers[f] = [];
            }
            for (int f = 0; f < functions.Length; f++)
            {
                foreach (int target in References(functions[f]))
                {
                    if (target >= 0 && target < functions.Length)
                        callers[target].Add(f);
                }
            }
            var pending = new Queue<int>(Enumerable.Range(0, functions.Length).Where(f => !ok[f]));
            while (pending.TryDequeue(out int f))
            {
                foreach (int caller in callers[f])
                {
                    if (ok[caller])
                    {
                        ok[caller] = false;
                        pending.Enqueue(caller);
                    }
                }
            }
            return ok;
        }

        /// <summary>The functions a function calls directly or whose address it takes.</summary>
        public static IEnumerable<int> References(IrFunction function)
        {
            foreach (var block in function.Blocks)
            {
                if (block.Terminator is CallTerminator { Callee: null, Required: null } call)
                    yield return call.Function;
                foreach (var statement in block.Statements)
                {
                    if (statement is AssignStatement { Value: FunctionRefRvalue reference })
                        yield return reference.Function;
                }
            }
        }
    }
}
