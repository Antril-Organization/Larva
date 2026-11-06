namespace Larva.IR
{
    /// <summary>
    /// An operation every type argument of a generic parameter must provide, beyond what <see cref="TypeConstraint"/>
    /// can say about the type itself: an operator (<c>op+(T, T) -> T</c>), a conversion (<c>conv(T) -> U</c>) or a named
    /// static member (<c>Zero() -> T</c>). Types are written in terms of the declaring item's generic parameters.
    /// <para>Names: <c>op</c> + the operator symbol for binary operators and prefix unary operators, the same followed by
    /// <c>.postfix</c> for postfix ones, <c>conv</c> for an explicit conversion, anything else for named members. A
    /// requirement is met by a built-in operation when every type is a primitive that has it (<c>op+</c> on <c>i32</c>,
    /// <c>conv</c> between numbers), otherwise by a function the module lists in <see cref="IrModule.Implementations"/>.</para>
    /// </summary>
    public sealed record Requirement(string Name, IrType[] Parameters, IrType Result)
    {
        public const string Conversion = "conv";
        /// <summary><c>new(T1, T2) -> T</c>: a constructor (met by the type's allocator or value constructor).</summary>
        public const string Constructor = "new";
        public const string PostfixSuffix = ".postfix";

        public Requirement Substitute(IrType[] arguments) =>
            new(Name, [.. Parameters.Select(p => p.Substitute(arguments))], Result.Substitute(arguments));

        public Requirement Map(Func<IrType, IrType> map) => new(Name, [.. Parameters.Select(map)], map(Result));

        public bool IsGeneric => Parameters.Any(p => p.IsGeneric()) || Result.IsGeneric();

        public bool SameAs(Requirement other) => Name == other.Name && Result == other.Result && Parameters.SequenceEqual(other.Parameters);

        public override string ToString() => $"{Name}({string.Join(", ", Parameters)}) -> {Result}";
    }

    /// <summary>A call through a requirement: <see cref="Index"/> of the requirements of generic parameter <see cref="Param"/>.</summary>
    public sealed record RequirementRef(int Param, int Index);

    /// <summary>
    /// <see cref="Function"/> implements requirement <see cref="Name"/> for the types of its own signature (a generic
    /// function implements it for every instantiation that matches).
    /// </summary>
    public sealed record Implementation(string Name, int Function);

    /// <summary>How a requirement is met for concrete types.</summary>
    public abstract record Resolution;

    public sealed record BuiltinBinary(BinaryOp Op) : Resolution;

    public sealed record BuiltinUnary(UnaryOp Op) : Resolution;

    public sealed record BuiltinCast(IrType Target) : Resolution;

    public sealed record FunctionResolution(int Function, IrType[] TypeArguments) : Resolution;

    public static class Requirements
    {
        /// <summary>The suffix of an implementation used when no other meets the requirement.</summary>
        public const string FallbackSuffix = "!fallback";

        private static readonly Dictionary<string, BinaryOp> BinaryOps = new()
        {
            ["op+"] = BinaryOp.Add, ["op-"] = BinaryOp.Sub, ["op*"] = BinaryOp.Mul, ["op/"] = BinaryOp.Div, ["op%"] = BinaryOp.Rem,
            ["op&"] = BinaryOp.And, ["op|"] = BinaryOp.Or, ["op^"] = BinaryOp.Xor, ["op<<"] = BinaryOp.Shl, ["op>>"] = BinaryOp.Shr,
            ["op=="] = BinaryOp.Eq, ["op!="] = BinaryOp.Ne, ["op<"] = BinaryOp.Lt, ["op<="] = BinaryOp.Le, ["op>"] = BinaryOp.Gt, ["op>="] = BinaryOp.Ge,
        };

        /// <summary>The operation built into primitives that meets <paramref name="requirement"/>, if any.</summary>
        public static Resolution? Builtin(Requirement requirement)
        {
            var p = requirement.Parameters;
            var r = requirement.Result;
            if (p.Any(t => t is not PrimitiveType) || r is not PrimitiveType)
                return null;
            static bool Numeric(IrType t) => t.IsInteger || t.IsFloat;

            if (requirement.Name == Requirement.Conversion)
                return p.Length == 1 && Numeric(p[0]) && Numeric(r) ? new BuiltinCast(r) : null;

            if (p.Length == 1)
            {
                return requirement.Name switch
                {
                    "op-" when p[0] == r && (r.IsSigned || r.IsFloat) => new BuiltinUnary(UnaryOp.Neg),
                    "op!" when p[0] == r && r == IrType.Bool => new BuiltinUnary(UnaryOp.Not),
                    "op~" when p[0] == r && r.IsInteger => new BuiltinUnary(UnaryOp.Not),
                    _ => null,
                };
            }
            if (p.Length != 2 || !BinaryOps.TryGetValue(requirement.Name, out var op))
                return null;
            bool valid = op switch
            {
                BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul or BinaryOp.Div or BinaryOp.Rem => p[0] == p[1] && p[0] == r && Numeric(r),
                BinaryOp.And or BinaryOp.Or or BinaryOp.Xor => p[0] == p[1] && p[0] == r && (r.IsInteger || r == IrType.Bool),
                BinaryOp.Shl or BinaryOp.Shr => p[0] == r && r.IsInteger && p[1].IsInteger,
                BinaryOp.Eq or BinaryOp.Ne => p[0] == p[1] && r == IrType.Bool && (Numeric(p[0]) || p[0] == IrType.Bool),
                _ => p[0] == p[1] && r == IrType.Bool && Numeric(p[0]),
            };
            return valid ? new BuiltinBinary(op) : null;
        }

        /// <summary>
        /// How <paramref name="requirement"/> is met: a built-in operation, or the one implementation whose signature
        /// matches (binding its own type parameters). <paramref name="problem"/> explains a failure.
        /// </summary>
        public static Resolution? Resolve(Requirement requirement, TypeContext context, out string? problem, int depth = 0)
        {
            problem = null;
            if (Builtin(requirement) is { } builtin)
                return builtin;
            var module = context.Module;
            var matches = new List<FunctionResolution>();
            foreach (var implementation in module.Implementations.Where(i => i.Name == requirement.Name))
            {
                var function = module.Functions[implementation.Function];
                if (function.ArgumentCount != requirement.Parameters.Length)
                    continue;
                var bindings = new IrType?[function.Generics.Length];
                bool fits = Unify(function.ReturnType, requirement.Result, bindings);
                for (int a = 0; fits && a < function.ArgumentCount; a++)
                    fits = Unify(function.Locals[a + 1].Type, requirement.Parameters[a], bindings);
                if (!fits || bindings.Any(b => b is null))
                    continue;
                var arguments = bindings.Select(b => b!).ToArray();
                // The implementation's own constraints and requirements hold for these arguments.
                bool satisfied = depth < 8;
                for (int g = 0; satisfied && g < arguments.Length; g++)
                {
                    satisfied = Constraints.Satisfies(arguments[g], function.Generics[g].Constraint, context)
                        && function.Generics[g].Requirements.All(r => Check(r.Substitute(arguments), context, depth + 1) is null);
                }
                if (satisfied)
                    matches.Add(new FunctionResolution(implementation.Function, arguments));
            }
            if (matches.Count == 1)
                return matches[0];
            // Several fit: the most specific wins, the one whose parameters every other's can be made into (a class's own
            // `$typeof`, or the one for functions, over the one for pointers, where classes and functions are pointers:
            // Manual mode).
            if (matches.Count > 1)
            {
                var specific = matches.Where(m => matches.All(o => o == m || Instance(module.Functions[m.Function], module.Functions[o.Function])
                    && !Instance(module.Functions[o.Function], module.Functions[m.Function]))).ToList();
                if (specific.Count == 1)
                    return specific[0];
            }
            // A requirement may have a last resort, listed as `name!fallback` (the run-time type of a type nobody describes),
            // and that one its own (`name!fallback!fallback`), tried in turn.
            if (matches.Count == 0 && module.Implementations.Any(i => i.Name == requirement.Name + FallbackSuffix))
                return Resolve(requirement with { Name = requirement.Name + FallbackSuffix }, context, out problem, depth);
            problem = matches.Count == 0
                ? null
                : $"is ambiguous: {string.Join(", ", matches.Select(m => module.Functions[m.Function].Name))}";
            return null;
        }

        /// <summary>Whether <paramref name="general"/>'s parameters can be made into <paramref name="particular"/>'s.</summary>
        private static bool Instance(IrFunction particular, IrFunction general)
        {
            var bindings = new IrType?[general.Generics.Length];
            for (int a = 1; a <= general.ArgumentCount; a++)
                if (!Unify(general.Locals[a].Type, particular.Locals[a].Type, bindings))
                    return false;
            return true;
        }

        /// <summary>
        /// Whether <paramref name="requirement"/> (already instantiated) holds where <paramref name="context"/> applies: a
        /// requirement that mentions the context's type parameters must be one of theirs, or be met by a generic
        /// implementation. Returns null when it holds, else what is wrong.
        /// </summary>
        public static string? Check(Requirement requirement, TypeContext context, int depth = 0)
        {
            if (requirement.IsGeneric && context.Generics.Any(g => g.Requirements.Any(r => r.SameAs(requirement))))
                return null;
            if (Resolve(requirement, context, out string? problem, depth) is not null)
                return null;
            return problem ?? "is not provided";
        }

        /// <summary>
        /// <see cref="Check"/> for a call's type arguments: a parameter of the caller limited to a finite list of types
        /// (`T : [i16, f32]`, a number group) is compiled per type, so a requirement on it holds when it holds for each of
        /// them. Only for checking a call: resolving an implementation uses <see cref="Check"/>, which stays exact.
        /// </summary>
        public static string? CheckPerMember(Requirement requirement, TypeContext context)
        {
            if (Check(requirement, context) is not { } problem)
                return null;
            if (!requirement.IsGeneric)
                return problem;
            for (int g = 0; g < context.Generics.Count; g++)
            {
                var param = new GenericParamType(g);
                if (!requirement.Parameters.Append(requirement.Result).Any(t => t.Walk().Contains(param))
                    || Constraints.Members(context.Generics[g].Constraint, context) is not { Count: > 0 } members)
                    continue;
                int at = g;
                if (members.All(member => Check(requirement.Substitute([.. Enumerable.Range(0, context.Generics.Count).Select(i => i == at ? member : (IrType)new GenericParamType(i))]), context) is null))
                    return null;
            }
            return problem;
        }

        /// <summary>Binds the generic parameters of <paramref name="pattern"/> so it equals <paramref name="type"/>.</summary>
        public static bool Unify(IrType pattern, IrType type, IrType?[] bindings)
        {
            switch (pattern, type)
            {
                case (GenericParamType g, _) when g.Index < bindings.Length:
                    if (bindings[g.Index] is null)
                    {
                        bindings[g.Index] = type;
                        return true;
                    }
                    return bindings[g.Index] == type;
                case (StructType p, StructType t) when p.Id == t.Id && p.Arguments.Length == t.Arguments.Length:
                    for (int i = 0; i < p.Arguments.Length; i++)
                    {
                        if (!Unify(p.Arguments[i], t.Arguments[i], bindings))
                            return false;
                    }
                    return true;
                case (ReferenceType p, ReferenceType t) when p.Mutable == t.Mutable:
                    return Unify(p.Pointee, t.Pointee, bindings);
                case (PointerType p, PointerType t) when p.Mutable == t.Mutable:
                    return Unify(p.Pointee, t.Pointee, bindings);
                case (BoxType p, BoxType t):
                    return Unify(p.Pointee, t.Pointee, bindings);
                case (GcType p, GcType t):
                    return Unify(p.Pointee, t.Pointee, bindings);
                case (ArrayType p, ArrayType t):
                    return Unify(p.Element, t.Element, bindings);
                case (OptionalType p, OptionalType t):
                    return Unify(p.Inner, t.Inner, bindings);
                case (FixedArrayType p, FixedArrayType t) when p.Count == t.Count:
                    return Unify(p.Element, t.Element, bindings) && (p.SizeOf is null ? t.SizeOf is null : t.SizeOf is not null && Unify(p.SizeOf, t.SizeOf, bindings));
                default:
                    return pattern == type;
            }
        }
    }
}
