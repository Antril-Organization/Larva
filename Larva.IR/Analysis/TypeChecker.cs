using Larva.IR.Text;

namespace Larva.IR.Analysis
{
    public enum TypeErrorKind : byte
    {
        /// <summary>A type argument does not satisfy the parameter's constraint.</summary>
        UnsatisfiedConstraint,
        /// <summary>Wrong number of type arguments for a function or struct.</summary>
        TypeArgumentCount,
        /// <summary>An operation on a generic value that its constraint does not allow (e.g. <c>Shl</c> on <c>T: numeric</c>).</summary>
        OperationNotAllowed,
        /// <summary><c>gc T</c> used in a GC mode that cannot manage objects.</summary>
        GcNotAvailable,
        /// <summary>Raw pointer dereference outside an <c>unsafe</c> function.</summary>
        UnsafeRequired,
        /// <summary>Reference to a generic parameter that is not in scope.</summary>
        UnknownGenericParameter,
        /// <summary>A value of the wrong type (allocation result, <c>free</c> operand).</summary>
        TypeMismatch,
        /// <summary>Misused exception handling: a handler block also reached normally, or <c>caught</c> outside a handler's first statement.</summary>
        InvalidHandler,
        /// <summary>Linkage rules: imports need a <c>module::name</c> symbol and no body, exports may not expose internal structs.</summary>
        LinkageViolation,
        /// <summary>Arena regions used in a GC mode without <see cref="GcCapabilities.Regions"/>.</summary>
        RegionsNotAvailable,
        /// <summary>Regions not properly nested: exiting the wrong one, re-entering an open one, open at return, or different on joining paths.</summary>
        RegionNesting,
        /// <summary>A value allocated in (or borrowed from) a region can outlive it.</summary>
        RegionEscape,
    }

    public sealed record TypeError(string Item, Location? At, TypeErrorKind Kind, string Message)
    {
        public override string ToString() => $"error[{Kind}]: {Message}\n   --> {Item}{(At is { } at ? $" {at}" : "")}";
    }

    /// <summary>
    /// Always-on checks that make generic code and the GC mode sound. Runs before the (optional) borrow checker
    /// and before monomorphization, so generic bodies are checked once against their constraints.
    /// </summary>
    public static class TypeChecker
    {
        /// <summary>Whether <paramref name="derived"/> is <paramref name="target"/> or extends it through <see cref="StructDef.Base"/>.</summary>
        public static bool DerivesFrom(StructType derived, StructType target, IrModule module)
        {
            StructType? current = derived;
            for (int depth = 0; current is not null && depth < 64; depth++)
            {
                if (current == target)
                    return true;
                current = module.Structs[current.Id].Base?.Substitute(current.Arguments) as StructType;
            }
            return false;
        }

        /// <summary>Types whose all-zero bytes are a value: no references, boxes or handles anywhere inside.</summary>
        internal static bool Zeroable(IrType type, TypeContext context, int depth = 0) => depth < 32 && type switch
        {
            PrimitiveType => true,
            FixedArrayType inline => Zeroable(inline.Element, context, depth + 1),
            OptionalType => true, // null
            StructType st => context.Module.Structs[st.Id].Fields.All(f => Zeroable(f.Substitute(st.Arguments), context, depth + 1)),
            GenericParamType => Constraints.Satisfies(type, TypeConstraint.Plain, context),
            _ => false,
        };

        public static IReadOnlyList<TypeError> Check(IrModule module, GcConfig gc)
        {
            List<TypeError> errors = [];

            foreach (var def in module.Structs)
            {
                TypeContext context = new(module, def.Generics);
                foreach (var field in def.Fields)
                    CheckType(field, context, gc, def.Name, null, errors);
                CheckLinkage(module, def.Name, def.Linkage, def.Symbol, def.Fields, errors);
            }
            for (int structId = 0; structId < module.Structs.Length; structId++)
            {
                var def = module.Structs[structId];
                if (def.Base is not { } baseType)
                    continue;
                var baseFields = module.Structs[baseType.Id].Fields.Select(f => f.Substitute(baseType.Arguments)).ToArray();
                if (baseFields.Length > def.Fields.Length || !baseFields.AsSpan().SequenceEqual(def.Fields.AsSpan(0, baseFields.Length)))
                    errors.Add(new(def.Name, null, TypeErrorKind.TypeMismatch, $"`{def.Name}` extends `{module.Structs[baseType.Id].Name}`, so it must start with its fields"));
            }
            foreach (var global in module.Globals)
            {
                CheckLinkage(module, global.Name, global.Linkage, global.Symbol, [global.Type], errors);
                if (global.Type.IsGeneric())
                {
                    // A generic global's instances are checked once the monomorphizer makes them.
                    if (global.GenericCount == 0)
                        errors.Add(new(global.Name, null, TypeErrorKind.TypeMismatch, $"global `{global.Name}` uses type parameters it does not declare"));
                    continue;
                }
                var globalContext = new TypeContext(module, []);
                bool plain = Zeroable(global.Type, globalContext) && global.IsPlain(module);
                bool shared = Constraints.Satisfies(global.Type, TypeConstraint.Copy, globalContext) && global.Type is not PrimitiveType;
                if (!plain && !shared)
                    errors.Add(new(global.Name, null, TypeErrorKind.TypeMismatch,
                        $"global `{global.Name}` is `{IrPrinter.FormatType(global.Type, module)}`: globals hold plain data, or a copyable handle or reference (no owned values)"));
                else if (global.Initial is not null && global.Type is not PrimitiveType)
                    errors.Add(new(global.Name, null, TypeErrorKind.TypeMismatch, $"global `{global.Name}`: only primitives have an initial constant"));
            }
            foreach (var function in module.Functions)
            {
                CheckLinkage(module, function.Name, function.Linkage, function.Symbol, [.. function.Locals.Take(function.ArgumentCount + 1).Select(l => l.Type)], errors);
                if (function.IsDeclaration != (function.Linkage == Linkage.Import))
                    errors.Add(new(function.Name, null, TypeErrorKind.LinkageViolation, function.IsDeclaration
                        ? "a function without a body must be an import"
                        : "an import is a declaration and cannot have a body"));
            }

            // Each function on its own, in parallel; the errors keep the functions' order.
            var perFunction = new List<TypeError>[module.Functions.Length];
            Parallel.For(0, module.Functions.Length, f =>
            {
                var found = new List<TypeError>();
                new FunctionChecker(module.Functions[f], module, gc, found).Run();
                perFunction[f] = found;
            });
            foreach (var found in perFunction)
                errors.AddRange(found);
            return errors;
        }

        /// <summary>Imports name their target; exports only mention exported (or imported) structs, so other modules can see them.</summary>
        private static void CheckLinkage(IrModule module, string item, Linkage linkage, string? symbol, IEnumerable<IrType> visible, List<TypeError> errors)
        {
            if (linkage == Linkage.Import && (symbol is null || !symbol.Contains("::")))
                errors.Add(new(item, null, TypeErrorKind.LinkageViolation, $"import `{item}` needs a `module::name` symbol"));
            if (linkage is not (Linkage.Export or Linkage.LinkOnce))
                return;
            foreach (var type in visible.SelectMany(t => t.Walk()).OfType<StructType>().Distinct())
            {
                if (module.Structs[type.Id].Linkage == Linkage.Internal)
                    errors.Add(new(item, null, TypeErrorKind.LinkageViolation,
                        $"exported `{item}` exposes internal struct `{module.Structs[type.Id].Name}`; export it too"));
            }
        }

        /// <summary>Validates every type nested in <paramref name="type"/>: parameters in scope, struct arguments, GC availability.</summary>
        private static void CheckType(IrType type, TypeContext context, GcConfig gc, string item, Location? at, List<TypeError> errors)
        {
            foreach (var nested in type.Walk())
            {
                switch (nested)
                {
                    case GenericParamType p when p.Index >= context.Generics.Count:
                        errors.Add(new(item, at, TypeErrorKind.UnknownGenericParameter, $"generic parameter T{p.Index} is not in scope"));
                        return;

                    case StructType st:
                        var def = context.Module.Structs[st.Id];
                        CheckArguments(def.Name, def.Generics, st.Arguments, context, item, at, errors);
                        break;
                }
            }
        }

        private static void CheckArguments(string target, GenericParam[] parameters, IrType[] arguments, TypeContext context,
            string item, Location? at, List<TypeError> errors)
        {
            if (parameters.Length != arguments.Length)
            {
                errors.Add(new(item, at, TypeErrorKind.TypeArgumentCount,
                    $"`{target}` takes {parameters.Length} type argument(s), got {arguments.Length}"));
                return;
            }

            for (int i = 0; i < parameters.Length; i++)
            {
                if (!Constraints.Satisfies(arguments[i], parameters[i].Constraint, context))
                    errors.Add(new(item, at, TypeErrorKind.UnsatisfiedConstraint,
                        $"`{IrPrinter.FormatType(arguments[i], context.Module, context.Generics)}` does not satisfy " +
                        $"`{parameters[i].Name}: {IrPrinter.FormatConstraint(parameters[i].Constraint, context.Module, parameters)}` required by `{target}`"));
                foreach (var requirement in parameters[i].Requirements)
                {
                    var instantiated = requirement.Substitute(arguments);
                    if (Requirements.CheckPerMember(instantiated, context) is { } problem)
                        errors.Add(new(item, at, TypeErrorKind.UnsatisfiedConstraint,
                            $"`{IrPrinter.FormatRequirement(instantiated, context.Module, context.Generics)}`, required of " +
                            $"`{parameters[i].Name}` by `{target}`, {problem}"));
                }
            }
        }

        private sealed class FunctionChecker(IrFunction function, IrModule module, GcConfig gc, List<TypeError> errors)
        {
            private readonly TypeContext context = TypeContext.For(function, module);

            public void Run()
            {
                foreach (var local in function.Locals)
                    CheckType(local.Type, context, gc, function.Name, null, errors);
                if (function.IsDeclaration)
                    return;
                for (int l = 0; l < function.Locals.Length; l++)
                {
                    if (function.Locals[l].Region is int region && (region >= function.RegionParams.Length || !(l == 0 || function.IsArgument(l))))
                        errors.Add(new(function.Name, null, TypeErrorKind.RegionNesting, region >= function.RegionParams.Length
                            ? $"`_{l}` refers to region parameter {region}, but the function declares {function.RegionParams.Length}"
                            : $"`_{l}` is not an argument or the return place; only signatures carry region annotations"));
                }

                CheckHandlers();
                for (int b = 0; b < function.Blocks.Length; b++)
                {
                    var block = function.Blocks[b];
                    for (int i = 0; i < block.Statements.Length; i++)
                        CheckStatement(block.Statements[i], new Location(b, i));
                    CheckTerminator(block.Terminator, new Location(b, block.Statements.Length));
                }
            }

            /// <summary>
            /// Handler blocks are entered only by exceptions (the exception is on the stack there), and <c>caught</c> reads it
            /// as their first statement, into a whole local of the clause's type.
            /// </summary>
            private void CheckHandlers()
            {
                Dictionary<int, List<CatchClause>> clauses = [];
                HashSet<int> normal = [0];
                for (int b = 0; b < function.Blocks.Length; b++)
                {
                    var terminator = function.Blocks[b].Terminator;
                    var handlers = terminator is IUnwinding unwinding ? unwinding.Handlers : [];
                    foreach (var clause in handlers)
                    {
                        if (!clauses.TryGetValue(clause.Block, out var list))
                            clauses[clause.Block] = list = [];
                        list.Add(clause);
                        if (clause.Type is { } type)
                            CheckType(type, context, gc, function.Name, new Location(b, function.Blocks[b].Statements.Length), errors);
                    }
                    var handlerTargets = handlers.Select(h => h.Block).ToHashSet();
                    foreach (int successor in terminator.Successors)
                    {
                        if (!handlerTargets.Contains(successor) || terminator is CallTerminator call && call.Target == successor)
                            normal.Add(successor);
                    }
                }

                foreach (var (block, list) in clauses)
                {
                    if (normal.Contains(block))
                        errors.Add(new(function.Name, new Location(block, 0), TypeErrorKind.InvalidHandler,
                            $"bb{block} is an exception handler and cannot also be reached normally"));
                }

                for (int b = 0; b < function.Blocks.Length; b++)
                {
                    var statements = function.Blocks[b].Statements;
                    for (int i = 0; i < statements.Length; i++)
                    {
                        if (statements[i] is not AssignStatement { Value: CaughtRvalue } assign)
                            continue;
                        Location at = new(b, i);
                        if (i != 0 || !clauses.TryGetValue(b, out var list))
                        {
                            errors.Add(new(function.Name, at, TypeErrorKind.InvalidHandler, "`caught` must be the first statement of a handler block"));
                            continue;
                        }
                        if (!assign.Target.IsLocal)
                            errors.Add(new(function.Name, at, TypeErrorKind.InvalidHandler, "`caught` must be stored into a whole local"));
                        var target = function.TypeOf(assign.Target, module);
                        if (list.Any(c => c.Type is null || c.Type != target))
                            errors.Add(new(function.Name, at, TypeErrorKind.InvalidHandler,
                                $"bb{b} is entered by {string.Join(", ", list.Select(c => c.Type is null ? "catch _" : $"catch {IrPrinter.FormatType(c.Type, module, function.Generics)}"))}; " +
                                $"`caught` into `{IrPrinter.FormatType(target, module, function.Generics)}` needs every clause to catch exactly that type"));
                    }
                }
            }

            private void CheckStatement(Statement statement, Location at)
            {
                if (statement is StoreGlobalStatement store)
                {
                    if (store.Global < 0 || store.Global >= module.Globals.Length)
                        errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"`global#{store.Global}` does not exist"));
                    else if (store.TypeArguments.Length != module.Globals[store.Global].GenericCount)
                        errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"`{module.Globals[store.Global].Name}` takes {module.Globals[store.Global].GenericCount} type argument(s)"));
                    else if (function.TypeOf(store.Value, module) != module.Globals[store.Global].Type.Substitute(store.TypeArguments))
                        errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                            $"`{module.Globals[store.Global].Name}` is `{IrPrinter.FormatType(module.Globals[store.Global].Type.Substitute(store.TypeArguments), module, function.Generics)}`, " +
                            $"not `{IrPrinter.FormatType(function.TypeOf(store.Value, module), module, function.Generics)}`"));
                    return;
                }
                if (statement is FreeStatement free)
                {
                    CheckPlace(free.Pointer, at);
                    if (!function.Unsafe)
                        errors.Add(new(function.Name, at, TypeErrorKind.UnsafeRequired, $"`free({IrPrinter.Format(free.Pointer)})` requires an `unsafe fn`"));
                    if (function.TypeOf(free.Pointer, module) is not PointerType { Mutable: true })
                        errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                            $"`free` expects a `*mut T` from `alloc`, got `{IrPrinter.FormatType(function.TypeOf(free.Pointer, module), module, function.Generics)}`"));
                    return;
                }

                if (statement is RegionEnterStatement or RegionExitStatement && (gc.Capabilities & GcCapabilities.Regions) == 0)
                {
                    errors.Add(new(function.Name, at, TypeErrorKind.RegionsNotAvailable,
                        $"arena regions are not available in GC mode {gc.Mode}"));
                    return;
                }

                if (statement is not AssignStatement assign)
                    return;

                CheckPlace(assign.Target, at);
                switch (assign.Value)
                {
                    case UseRvalue use:
                        CheckOperand(use.Operand, at);
                        break;
                    case BinaryRvalue binary:
                        CheckOperand(binary.Left, at);
                        CheckOperand(binary.Right, at);
                        RequireOnGeneric(binary.Op.ToString(), function.TypeOf(binary.Left, module), Requirement(binary.Op), at);
                        break;
                    case UnaryRvalue unary:
                        CheckOperand(unary.Operand, at);
                        RequireOnGeneric(unary.Op.ToString(), function.TypeOf(unary.Operand, module),
                            unary.Op == UnaryOp.Neg ? TypeConstraint.Numeric : TypeConstraint.Integer | TypeConstraint.Exact(IrType.Bool), at);
                        break;
                    case CastRvalue cast:
                        CheckOperand(cast.Operand, at);
                        CheckType(cast.Type, context, gc, function.Name, at, errors);
                        var scalar = TypeConstraint.Numeric | TypeConstraint.Exact(IrType.Bool);
                        RequireOnGeneric("as", function.TypeOf(cast.Operand, module), scalar, at);
                        RequireOnGeneric("as", cast.Type, scalar, at);
                        break;
                    case RefRvalue reference:
                        CheckPlace(reference.Place, at);
                        break;
                    case AllocRvalue alloc:
                        CheckOperand(alloc.Value, at);
                        var expected = alloc.ResultType(function.TypeOf(alloc.Value, module));
                        // A module without a collector may hold `gc T` objects of other GC domains, but cannot make them.
                        if (alloc.Kind == AllocKind.Gc && !gc.ManagesObjects)
                            errors.Add(new(function.Name, at, TypeErrorKind.GcNotAvailable,
                                $"`gc` allocation is not available in GC mode {gc.Mode}; use `box v` or `alloc v`"));
                        if (alloc.Finalizer is { } finalizer)
                        {
                            CheckOperand(finalizer, at);
                            var finalizerType = function.TypeOf(finalizer, module);
                            if (alloc.Kind != AllocKind.Gc || finalizerType is not FunctionType { Parameters: [var handle], Result.IsUnit: true }
                                || handle != expected)
                                errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                    $"a finalizer is a `fn({IrPrinter.FormatType(expected, module, function.Generics)})` on a `gc` allocation, not `{IrPrinter.FormatType(finalizerType, module, function.Generics)}`"));
                        }
                        var actual = function.TypeOf(assign.Target, module);
                        if (expected != actual)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"`{IrPrinter.Format(alloc, module, function.Generics)}` produces `{IrPrinter.FormatType(expected, module, function.Generics)}`, " +
                                $"but `{IrPrinter.Format(assign.Target)}` is `{IrPrinter.FormatType(actual, module, function.Generics)}`"));
                        break;
                    case ArrayRvalue array:
                        CheckOperand(array.Length, at);
                        CheckType(array.Element, context, gc, function.Name, at, errors);
                        if (array.Owner is { } owner)
                        {
                            CheckType(owner, context, gc, function.Name, at, errors);
                            var ownerFields = owner.Id < module.Structs.Length ? module.Structs[owner.Id].Fields : [];
                            if (ownerFields.Length == 0 || ownerFields[^1].Substitute(owner.Arguments) != new ArrayType(array.Element))
                                errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                    $"`{IrPrinter.FormatType(owner, module, function.Generics)}` does not end with a `[{IrPrinter.FormatType(array.Element, module, function.Generics)}]` field"));
                            else if (!owner.IsGeneric() && Lowering.Layouts.Trailing(owner, module) is null)
                                errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                    $"`{IrPrinter.FormatType(owner, module, function.Generics)}`'s last array must hold no references and align to at most 8 bytes"));
                        }
                        if (!function.TypeOf(array.Length, module).IsInteger)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, "array length must be an integer"));
                        if (!array.Filled && !Constraints.Satisfies(array.Element, TypeConstraint.Copy, context) && !Zeroable(array.Element, context))
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"array elements start zeroed, so `{IrPrinter.FormatType(array.Element, module, function.Generics)}` must be `copy` or valid as all-zero bytes"));
                        if (function.TypeOf(assign.Target, module) != array.ResultType)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"`{IrPrinter.Format(array, module, function.Generics)}` produces `{IrPrinter.FormatType(array.ResultType, module, function.Generics)}`, " +
                                $"but `{IrPrinter.Format(assign.Target)}` is `{IrPrinter.FormatType(function.TypeOf(assign.Target, module), module, function.Generics)}`"));
                        break;
                    case LenRvalue len:
                        CheckPlace(len.Array, at);
                        if (function.TypeOf(len.Array, module) is not ArrayType)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"`Len` needs an array place, `{IrPrinter.Format(len.Array)}` is not one"));
                        break;
                    case ZeroRvalue zero:
                        CheckType(zero.Type, context, gc, function.Name, at, errors);
                        if (!zero.Filler && !Zeroable(zero.Type, context))
                            errors.Add(new(function.Name, at, TypeErrorKind.OperationNotAllowed,
                                $"`zeroed {IrPrinter.FormatType(zero.Type, module, function.Generics)}`: all-zero bytes are not a valid value of this type"));
                        else if (function.TypeOf(assign.Target, module) != zero.Type)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"`zeroed {IrPrinter.FormatType(zero.Type, module, function.Generics)}` assigned to `{IrPrinter.Format(assign.Target)}`, " +
                                $"a `{IrPrinter.FormatType(function.TypeOf(assign.Target, module), module, function.Generics)}`"));
                        break;
                    case InstancesRvalue instances:
                    {
                        if (instances.Function < 0 || instances.Function >= module.Functions.Length)
                        {
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"`fn#{instances.Function}` does not exist"));
                            break;
                        }
                        var target = module.Functions[instances.Function];
                        var signature = Enumerable.Range(1, target.ArgumentCount).Select(a => target.Locals[a].Type).Append(target.ReturnType);
                        if (!target.IsGeneric || signature.Any(t => t.IsGeneric()))
                        {
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"`instances {target.Name}` takes a generic function whose signature mentions none of its type parameters"));
                            break;
                        }
                        var element = new FunctionType([.. Enumerable.Range(1, target.ArgumentCount).Select(a => target.Locals[a].Type)], target.ReturnType);
                        if (function.TypeOf(assign.Target, module) != IrType.Box(IrType.ArrayOf(element)))
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"`instances {target.Name}` is a `{IrPrinter.FormatType(IrType.Box(IrType.ArrayOf(element)), module)}`"));
                        break;
                    }
                    case FunctionRefRvalue reference:
                    {
                        if (reference.Function < 0 || reference.Function >= module.Functions.Length)
                        {
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"`fn#{reference.Function}` does not exist"));
                            break;
                        }
                        var target = module.Functions[reference.Function];
                        foreach (var typeArgument in reference.TypeArguments)
                            CheckType(typeArgument, context, gc, function.Name, at, errors);
                        CheckArguments(target.Name, target.Generics, reference.TypeArguments, context, function.Name, at, errors);
                        if (target.Generics.Length == reference.TypeArguments.Length)
                        {
                            var signature = new FunctionType([.. target.Locals.Skip(1).Take(target.ArgumentCount).Select(l => l.Type.Substitute(reference.TypeArguments))],
                                target.ReturnType.Substitute(reference.TypeArguments));
                            if (function.TypeOf(assign.Target, module) != signature)
                                errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                    $"`fn {target.Name}` is `{IrPrinter.FormatType(signature, module, function.Generics)}`, but `{IrPrinter.Format(assign.Target)}` is " +
                                    $"`{IrPrinter.FormatType(function.TypeOf(assign.Target, module), module, function.Generics)}`"));
                        }
                        break;
                    }
                    case HandleCastRvalue cast:
                    {
                        CheckOperand(cast.Handle, at);
                        var castFrom = function.TypeOf(cast.Handle, module);
                        // `?gc Derived` → `?gc Base` (or `?*mut`, objects of the manual heap): the same pointer, null stays null.
                        bool optionalHandles = castFrom is OptionalType { Inner: GcType or PointerType } && cast.Target is OptionalType { Inner: GcType or PointerType };
                        var from = optionalHandles ? ((OptionalType)castFrom).Inner : castFrom;
                        var castTarget = optionalHandles ? ((OptionalType)cast.Target).Inner : cast.Target;
                        // A type parameter that is an object (`T : Shape`): a handle once its argument is known.
                        bool objectParam = from is GenericParamType && castTarget is GcType or PointerType { Mutable: true }
                            && Constraints.Satisfies(from, TypeConstraint.Object, TypeContext.For(function, module));
                        bool sameKind = objectParam || (from, castTarget) is (GcType { Pointee: StructType or GenericParamType }, GcType { Pointee: StructType })
                            or (PointerType { Pointee: StructType or GenericParamType, Mutable: true }, PointerType { Pointee: StructType, Mutable: true });
                        if (!sameKind)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"a handle cast goes between `gc` (or `*mut`) handles to structs, not `{IrPrinter.FormatType(from, module, function.Generics)}` → " +
                                $"`{IrPrinter.FormatType(cast.Target, module, function.Generics)}`"));
                        else if (!objectParam && !function.Unsafe && (from.Pointee() is not StructType fromStruct || !DerivesFrom(fromStruct, (StructType)castTarget.Pointee()!, module)))
                            errors.Add(new(function.Name, at, TypeErrorKind.UnsafeRequired,
                                $"`{IrPrinter.FormatType(from, module, function.Generics)}` → `{IrPrinter.FormatType(cast.Target, module, function.Generics)}` is not a cast to a base: it needs an `unsafe fn` that knows the object's type"));
                        if (function.TypeOf(assign.Target, module) != cast.Target)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"a handle cast produces `{IrPrinter.FormatType(cast.Target, module, function.Generics)}`"));
                        break;
                    }
                    case WrapRvalue wrap:
                    {
                        CheckOperand(wrap.Value, at);
                        var inner = function.TypeOf(wrap.Value, module);
                        if (inner is not (GcType or BoxType or ReferenceType or PointerType))
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"only handles and references can be null, not `{IrPrinter.FormatType(inner, module, function.Generics)}`"));
                        else if (function.TypeOf(assign.Target, module) != new OptionalType(inner))
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"`wrap` produces `?{IrPrinter.FormatType(inner, module, function.Generics)}`"));
                        break;
                    }
                    case IsNullRvalue test:
                        CheckPlace(test.Value, at);
                        if (function.TypeOf(test.Value, module) is not OptionalType || function.TypeOf(assign.Target, module) != IrType.Bool)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, "`is null` tests a `?T` into a `bool`"));
                        break;
                    case UnwrapRvalue unwrap:
                        CheckOperand(unwrap.Value, at);
                        if (function.TypeOf(unwrap.Value, module) is not OptionalType optional || function.TypeOf(assign.Target, module) != optional.Inner)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, "`unwrap` turns a `?T` into its `T`"));
                        break;
                    case ClassIdRvalue classId:
                        // The class's definition (no type arguments: ids are per definition).
                        if (classId.Class is not StructType { } classStruct || classStruct.Id >= module.Structs.Length || !module.Structs[classStruct.Id].Mutable || function.TypeOf(assign.Target, module) != IrType.I32)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, "`classid` names a class and is an `i32`"));
                        break;
                    case ImplementationRvalue implementation:
                        foreach (var type in implementation.Requirement.Parameters.Append(implementation.Requirement.Result))
                            CheckType(type, context, gc, function.Name, at, errors);
                        if (function.TypeOf(assign.Target, module) != implementation.Type)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, "`impl` is the function type of its requirement"));
                        break;
                    case TypeIdRvalue typeId:
                        CheckType(typeId.Type, context, gc, function.Name, at, errors);
                        if (function.TypeOf(assign.Target, module) != IrType.I32)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, "`typeid` is an `i32`"));
                        break;
                    case GlobalRvalue global:
                        if (global.Global < 0 || global.Global >= module.Globals.Length)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"`global#{global.Global}` does not exist"));
                        else if (global.TypeArguments.Length != module.Globals[global.Global].GenericCount)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"`{module.Globals[global.Global].Name}` takes {module.Globals[global.Global].GenericCount} type argument(s)"));
                        else if (function.TypeOf(assign.Target, module) != module.Globals[global.Global].Type.Substitute(global.TypeArguments))
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"`{module.Globals[global.Global].Name}` is `{IrPrinter.FormatType(module.Globals[global.Global].Type.Substitute(global.TypeArguments), module, function.Generics)}`, " +
                                $"but `{IrPrinter.Format(assign.Target)}` is `{IrPrinter.FormatType(function.TypeOf(assign.Target, module), module, function.Generics)}`"));
                        break;
                    case IntrinsicRvalue intrinsic:
                    {
                        foreach (var argument in intrinsic.Arguments)
                            CheckOperand(argument, at);
                        if (Intrinsics.Find(intrinsic.Name) is not { } info)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"no runtime intrinsic `{intrinsic.Name}`"));
                        else if (!Intrinsics.Accepts(info, intrinsic.Arguments.Select(a => function.TypeOf(a, module)).ToArray()) || function.TypeOf(assign.Target, module) != info.Result)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"`{intrinsic.Name}` takes ({string.Join(", ", info.Parameters.Select(p => IrPrinter.FormatType(p, module)))}) and gives `{IrPrinter.FormatType(info.Result, module)}`"));
                        break;
                    }
                    case StaticRvalue data:
                        if (data.Data < 0 || data.Data >= module.Data.Length)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"`static#{data.Data}` does not exist"));
                        else if (function.TypeOf(assign.Target, module) != module.Data[data.Data].ReferenceType)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"`static#{data.Data}` is `{IrPrinter.FormatType(module.Data[data.Data].ReferenceType, module, function.Generics)}`, but " +
                                $"`{IrPrinter.Format(assign.Target)}` is `{IrPrinter.FormatType(function.TypeOf(assign.Target, module), module, function.Generics)}`"));
                        break;
                    case SizeOfRvalue size:
                        CheckType(size.Type, context, gc, function.Name, at, errors);
                        if (!Constraints.Satisfies(size.Type, TypeConstraint.FixedSize, context))
                            errors.Add(new(function.Name, at, TypeErrorKind.OperationNotAllowed,
                                $"`SizeOf` requires a `fixedsize` type, got `{IrPrinter.FormatType(size.Type, module, function.Generics)}`"));
                        break;
                }
            }

            private void CheckTerminator(Terminator terminator, Location at)
            {
                switch (terminator)
                {
                    case IfTerminator branch:
                        CheckOperand(branch.Condition, at);
                        break;
                    case SwitchTerminator @switch:
                        CheckOperand(@switch.Value, at);
                        break;
                    case DropTerminator drop:
                        CheckPlace(drop.Place, at);
                        break;
                    case ThrowTerminator @throw:
                        CheckOperand(@throw.Value, at);
                        break;
                    case CallTerminator { Callee: { } target } call:
                    {
                        foreach (var operand in call.Operands)
                            CheckOperand(operand, at);
                        CheckPlace(call.Destination, at);
                        if (function.TypeOf(target, module) is not FunctionType type)
                        {
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"an indirect call needs a `fn(…) -> …`, not `{IrPrinter.FormatType(function.TypeOf(target, module), module, function.Generics)}`"));
                            break;
                        }
                        string text = IrPrinter.FormatType(type, module, function.Generics);
                        if (call.Function != -1 || call.TypeArguments.Length > 0)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, "an indirect call names no function and takes no type arguments"));
                        if (call.Arguments.Length != type.Parameters.Length)
                        {
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"`{text}` takes {type.Parameters.Length} argument(s), got {call.Arguments.Length}"));
                            break;
                        }
                        for (int a = 0; a < call.Arguments.Length; a++)
                        {
                            if (function.TypeOf(call.Arguments[a], module) != type.Parameters[a])
                                errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                    $"argument {a} of `{text}` is `{IrPrinter.FormatType(function.TypeOf(call.Arguments[a], module), module, function.Generics)}`"));
                        }
                        if (function.TypeOf(call.Destination, module) != type.Result)
                            errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                                $"`{text}` returns `{IrPrinter.FormatType(type.Result, module, function.Generics)}`, not `{IrPrinter.FormatType(function.TypeOf(call.Destination, module), module, function.Generics)}`"));
                        break;
                    }
                    case CallTerminator { Required: { } required } call:
                        foreach (var argument in call.Arguments)
                            CheckOperand(argument, at);
                        CheckPlace(call.Destination, at);
                        CheckRequiredCall(call, required, at);
                        break;
                    case CallTerminator call:
                        foreach (var argument in call.Arguments)
                            CheckOperand(argument, at);
                        CheckPlace(call.Destination, at);
                        foreach (var typeArgument in call.TypeArguments)
                            CheckType(typeArgument, context, gc, function.Name, at, errors);
                        var callee = module.Functions[call.Function];
                        CheckArguments(callee.Name, callee.Generics, call.TypeArguments, context, function.Name, at, errors);
                        break;
                }
            }

            /// <summary>A call through a requirement: it exists, and the arguments and destination have its types.</summary>
            private void CheckRequiredCall(CallTerminator call, RequirementRef required, Location at)
            {
                if (required.Param < 0 || required.Param >= function.Generics.Length
                    || required.Index < 0 || required.Index >= function.Generics[required.Param].Requirements.Length)
                {
                    errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"requirement {required.Index} of type parameter {required.Param} does not exist"));
                    return;
                }
                var requirement = function.Generics[required.Param].Requirements[required.Index];
                string text = IrPrinter.FormatRequirement(requirement, module, function.Generics);
                if (call.Function != -1 || call.TypeArguments.Length > 0)
                    errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"a call through `{text}` names no function and takes no type arguments"));
                if (call.Arguments.Length != requirement.Parameters.Length)
                {
                    errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"`{text}` takes {requirement.Parameters.Length} argument(s), got {call.Arguments.Length}"));
                    return;
                }
                for (int a = 0; a < call.Arguments.Length; a++)
                {
                    if (function.TypeOf(call.Arguments[a], module) != requirement.Parameters[a])
                        errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                            $"argument {a} of `{text}` is `{IrPrinter.FormatType(function.TypeOf(call.Arguments[a], module), module, function.Generics)}`"));
                }
                if (function.TypeOf(call.Destination, module) != requirement.Result)
                    errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch,
                        $"`{text}` returns `{IrPrinter.FormatType(requirement.Result, module, function.Generics)}`, " +
                        $"but `{IrPrinter.Format(call.Destination)}` is `{IrPrinter.FormatType(function.TypeOf(call.Destination, module), module, function.Generics)}`"));
            }

            private void CheckOperand(Operand operand, Location at)
            {
                if (operand is CopyOperand { Place: var copied })
                    CheckPlace(copied, at);
                else if (operand is MoveOperand { Place: var moved })
                    CheckPlace(moved, at);
            }

            /// <summary>
            /// Index locals must be integers; raw pointers may only be dereferenced inside <c>unsafe</c> functions. Without a
            /// collector (Manual) object handles are raw pointers to structs: reading through them is what the frontend's
            /// own checks cover, so only pointers to other things keep the rule.
            /// </summary>
            private void CheckPlace(Place place, Location at)
            {
                foreach (int index in place.Projections.OfType<IndexProjection>().Select(p => p.Local))
                {
                    if (!function.Locals[index].Type.IsInteger)
                        errors.Add(new(function.Name, at, TypeErrorKind.TypeMismatch, $"index `_{index}` of `{IrPrinter.Format(place)}` must be an integer"));
                }
                if (function.Unsafe)
                    return;
                for (int j = 0; j < place.Projections.Length; j++)
                {
                    if (place.Projections[j] is DerefProjection
                        && function.TypeOf(new Place(place.Local, place.Projections[..j]), module).Unoptional() is PointerType pointer
                        && (gc.ManagesObjects || pointer.Pointee is not (StructType or ArrayType)))
                    {
                        errors.Add(new(function.Name, at, TypeErrorKind.UnsafeRequired,
                            $"dereference of raw pointer in `{IrPrinter.Format(place)}` requires an `unsafe fn`"));
                        return;
                    }
                }
            }

            /// <summary>When <paramref name="type"/> is a generic parameter, its constraint must imply <paramref name="required"/>.</summary>
            private void RequireOnGeneric(string operation, IrType type, TypeConstraint required, Location at)
            {
                if (type is not GenericParamType p || p.Index >= function.Generics.Length)
                    return;
                var parameter = function.Generics[p.Index];
                if (!Constraints.Implies(parameter.Constraint, required, context))
                    errors.Add(new(function.Name, at, TypeErrorKind.OperationNotAllowed,
                        $"`{operation}` on `{parameter.Name}` requires `{parameter.Name}: {IrPrinter.FormatConstraint(required, module, function.Generics)}`, " +
                        $"but it is only `{IrPrinter.FormatConstraint(parameter.Constraint, module, function.Generics)}`"));
            }

            private static TypeConstraint Requirement(BinaryOp op) => op switch
            {
                BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul or BinaryOp.Div or BinaryOp.Rem => TypeConstraint.Numeric,
                BinaryOp.Shl or BinaryOp.Shr => TypeConstraint.Integer,
                BinaryOp.And or BinaryOp.Or or BinaryOp.Xor => TypeConstraint.Integer | TypeConstraint.Exact(IrType.Bool),
                BinaryOp.Lt or BinaryOp.Le or BinaryOp.Gt or BinaryOp.Ge => TypeConstraint.Numeric,
                _ => TypeConstraint.Numeric | TypeConstraint.Exact(IrType.Bool), // Eq, Ne
            };
        }
    }
}
