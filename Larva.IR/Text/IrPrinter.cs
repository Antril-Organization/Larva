using System.Text;

namespace Larva.IR.Text
{
    /// <summary>
    /// Renders IR in a MIR-like text form:
    /// <code>
    /// fn max&lt;T: numeric&gt;(_1: T, _2: T) -> T {
    ///     let mut _0: T;      // return
    ///     bb0: {
    ///         _3 = Gt(copy _1, copy _2);
    ///         if move _3 -> [true: bb1, false: bb2];
    ///     }
    /// }
    /// </code>
    /// Generic parameters print by name when the enclosing function or struct is known, as <c>T0</c>, <c>T1</c>… otherwise.
    /// </summary>
    public static class IrPrinter
    {
        /// <summary>Print every float constant as its bit pattern (<c>0x3FF8000000000000_f64</c>), as tools comparing IR do.</summary>
        public static bool FloatsAsBits { get; set; }

        public static string Print(IrModule module)
        {
            StringBuilder builder = new();
            if (module.Name.Length > 0)
                builder.AppendLine($"module {module.Name}");
            for (int i = 0; i < module.Structs.Length; i++)
            {
                var def = module.Structs[i];
                var fields = def.Fields.Select((f, j) =>
                    (j < def.FieldNames.Length ? $"{def.FieldNames[j]}: " : "") + FormatType(f, module, def.Generics));
                builder.AppendLine(
                    $"{string.Concat(def.Attributes.Select(a => a + " "))}{FormatReflect(def.Reflect)}{FormatLinkage(def.Linkage)}struct {def.Name}{FormatGenerics(def.Generics, module)} {{ {string.Join(", ", fields)} }}" +
                    $"{(def.Symbol is null ? "" : $" from {def.Symbol}")}    // struct#{i}");
            }
            for (int i = 0; i < module.Data.Length; i++)
                builder.AppendLine($"static#{i} = {module.Data[i]}");
            foreach (var global in module.Globals)
                builder.AppendLine($"global {global.Name}: {FormatType(global.Type, module)}{(global.Initial is { } initial ? $" = {initial}" : "")}");
            foreach (var implementation in module.Implementations)
                builder.AppendLine($"impl {implementation.Name} = {FunctionName(implementation.Function, module)}");
            for (int i = 0; i < module.Functions.Length; i++)
            {
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append($"// fn#{i}\n");
                Print(builder, module.Functions[i], module);
            }
            return builder.ToString();
        }

        public static string Print(IrFunction function, IrModule? module = null)
        {
            StringBuilder builder = new();
            Print(builder, function, module);
            return builder.ToString();
        }

        private static void Print(StringBuilder builder, IrFunction function, IrModule? module)
        {
            var generics = function.Generics;
            string InRegion(LocalDecl local) => local.Region is int r && r < function.RegionParams.Length ? $" in '{function.RegionParams[r]}" : "";
            var arguments = Enumerable.Range(1, function.ArgumentCount)
                .Select(a => $"{(function.Locals[a].Mutable ? "mut " : "")}_{a}: {FormatType(function.Locals[a].Type, module, generics)}{InRegion(function.Locals[a])}");
            var parameters = function.RegionParams.Select(r => $"'{r}").ToList();
            string typeParameters = FormatGenerics(generics, module);
            if (typeParameters.Length > 0)
                parameters.Add(typeParameters[1..^1]);
            string header = parameters.Count == 0 ? "" : $"<{string.Join(", ", parameters)}>";
            builder.Append($"{string.Concat(function.Attributes.Select(a => a + " "))}{FormatReflect(function.Reflect)}{FormatLinkage(function.Linkage)}{(function.Unsafe ? "unsafe " : "")}fn {function.Name}{header}({string.Join(", ", arguments)})");
            if (function.IsDeclaration)
            {
                builder.Append($" -> {FormatType(function.ReturnType, module, generics)}{InRegion(function.Locals[0])}");
                builder.Append(function.Symbol is null ? ";\n" : $" from {function.Symbol};\n");
                return;
            }
            builder.Append($" -> {FormatType(function.ReturnType, module, generics)}{InRegion(function.Locals[0])} {{\n");

            for (int l = 0; l < function.Locals.Length; l++)
            {
                if (function.IsArgument(l))
                    continue;
                var local = function.Locals[l];
                string declaration = $"    let {(local.Mutable ? "mut " : "")}_{l}: {FormatType(local.Type, module, generics)};";
                builder.Append(local.Name is null ? declaration : $"{declaration.PadRight(32)}// {local.Name}").Append('\n');
            }

            for (int b = 0; b < function.Blocks.Length; b++)
            {
                builder.Append($"\n    bb{b}: {{\n");
                foreach (var statement in function.Blocks[b].Statements)
                    builder.Append($"        {Format(statement, module, generics)}\n");
                builder.Append($"        {Format(function.Blocks[b].Terminator, module, generics)}\n");
                builder.Append("    }\n");
            }
            builder.Append("}\n");
        }

        private static string FormatLinkage(Linkage linkage) => linkage switch
        {
            Linkage.Export => "export ",
            Linkage.Import => "import ",
            Linkage.LinkOnce => "linkonce ",
            _ => "",
        };

        private static string FormatReflect(ReflectionLevel? level) =>
            level is { } l ? $"#[reflect({l.ToString().ToLowerInvariant()})] " : "";

        // ── Types and constraints ──────────────────────────────────

        public static string FormatType(IrType type, IrModule? module = null, IReadOnlyList<GenericParam>? generics = null) => type switch
        {
            GenericParamType p when generics is not null && p.Index < generics.Count => generics[p.Index].Name,
            StructType st => $"{StructName(st.Id, module)}{(st.Arguments.Length == 0 ? "" : $"<{string.Join(", ", st.Arguments.Select(a => FormatType(a, module, generics)))}>")}",
            ReferenceType reference => $"&{(reference.Mutable ? "mut " : "")}{FormatType(reference.Pointee, module, generics)}",
            GcType gc => $"gc {FormatType(gc.Pointee, module, generics)}",
            BoxType box => $"box {FormatType(box.Pointee, module, generics)}",
            PointerType pointer => $"*{(pointer.Mutable ? "mut " : "")}{FormatType(pointer.Pointee, module, generics)}",
            ArrayType array => $"[{FormatType(array.Element, module, generics)}]",
            OptionalType optional => $"?{FormatType(optional.Inner, module, generics)}",
            FunctionType function => $"fn({string.Join(", ", function.Parameters.Select(p => FormatType(p, module, generics)))}) -> {FormatType(function.Result, module, generics)}",
            FixedArrayType inline => inline.SizeOf is null ? $"[{FormatType(inline.Element, module, generics)}; {inline.Count}]"
                : $"[{FormatType(inline.Element, module, generics)}; {(inline.Count == 1 ? "" : $"{inline.Count} * ")}sizeof({FormatType(inline.SizeOf, module, generics)})]",
            _ => type.ToString(),
        };

        public static string FormatConstraint(TypeConstraint constraint, IrModule? module = null, IReadOnlyList<GenericParam>? generics = null)
        {
            string Part(TypeConstraint c, bool parenthesizeOr) =>
                c is OrConstraint && parenthesizeOr || c is AndConstraint
                    ? $"({FormatConstraint(c, module, generics)})"
                    : FormatConstraint(c, module, generics);

            return constraint switch
            {
                AnyConstraint => "any",
                GroupConstraint g => g.Group.ToString().ToLowerInvariant(),
                ExactConstraint e => FormatType(e.Type, module, generics),
                AndConstraint conj => string.Join(" & ", conj.Parts.Select(p => Part(p, parenthesizeOr: true))),
                OrConstraint disj => string.Join(" | ", disj.Parts.Select(p => Part(p, parenthesizeOr: false))),
                NotConstraint neg => $"!{(neg.Inner is AndConstraint or OrConstraint ? $"({FormatConstraint(neg.Inner, module, generics)})" : FormatConstraint(neg.Inner, module, generics))}",
                SizeAtMostConstraint size => $"size <= {size.Bits}",
                _ => throw new ArgumentOutOfRangeException(nameof(constraint)),
            };
        }

        /// <summary><c>&lt;T: numeric, U&gt;</c>, or empty for non-generic items.</summary>
        public static string FormatGenerics(IReadOnlyList<GenericParam> generics, IrModule? module = null) =>
            generics.Count == 0
                ? ""
                : $"<{string.Join(", ", generics.Select(g => FormatGeneric(g, module, generics)))}>";

        private static string FormatGeneric(GenericParam g, IrModule? module, IReadOnlyList<GenericParam> generics)
        {
            var parts = new List<string>();
            if (g.Constraint is not AnyConstraint)
                parts.Add(FormatConstraint(g.Constraint, module, generics));
            parts.AddRange(g.Requirements.Select(r => FormatRequirement(r, module, generics)));
            return parts.Count == 0 ? g.Name : $"{g.Name}: {string.Join(" + ", parts)}";
        }

        public static string FormatRequirement(Requirement requirement, IrModule? module = null, IReadOnlyList<GenericParam>? generics = null) =>
            $"{requirement.Name}({string.Join(", ", requirement.Parameters.Select(p => FormatType(p, module, generics)))}) -> {FormatType(requirement.Result, module, generics)}";

        private static string StructName(int id, IrModule? module) =>
            module is not null && id < module.Structs.Length ? module.Structs[id].Name : $"struct#{id}";

        // ── Places and values ──────────────────────────────────────

        public static string Format(Place place)
        {
            string text = $"_{place.Local}";
            foreach (var projection in place.Projections)
            {
                text = projection switch
                {
                    DerefProjection => $"(*{text})",
                    FieldProjection field => $"{text}.{field.FieldIndex}",
                    IndexProjection index => $"{text}[_{index.Local}]",
                    _ => throw new ArgumentOutOfRangeException(nameof(place)),
                };
            }
            return text;
        }

        public static string Format(Operand operand, IrModule? module = null) => operand switch
        {
            CopyOperand copy => $"copy {Format(copy.Place)}",
            MoveOperand move => $"move {Format(move.Place)}",
            ConstOperand constant => $"const {FormatConstant(constant, module)}",
            _ => throw new ArgumentOutOfRangeException(nameof(operand)),
        };

        /// <summary>
        /// Constant text with a type suffix: <c>42_i32</c>, <c>true</c>, <c>1.5_f64</c>. Floats without a .NET type
        /// (f128, f256) print their bit pattern: <c>0x3FFF8000…_f128</c>.
        /// </summary>
        public static string FormatConstant(ConstOperand constant, IrModule? module = null)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return constant.Type switch
            {
                PrimitiveType { Kind: Primitive.Unit } => "()",
                PrimitiveType { Kind: Primitive.Bool } => constant.Bits.IsZero ? "false" : "true",
                PrimitiveType { Kind: Primitive.F16 } when !FloatsAsBits => $"{BitConverter.UInt16BitsToHalf((ushort)constant.Bits).ToString(culture)}_f16",
                PrimitiveType { Kind: Primitive.F32 } when !FloatsAsBits => $"{BitConverter.UInt32BitsToSingle((uint)constant.Bits).ToString(culture)}_f32",
                PrimitiveType { Kind: Primitive.F64 } when !FloatsAsBits => $"{BitConverter.UInt64BitsToDouble((ulong)constant.Bits).ToString(culture)}_f64",
                { IsFloat: true } type => $"0x{constant.Bits.ToString("X", culture).TrimStart('0').PadLeft(1, '0')}_{FormatType(type, module)}",
                var type => $"{constant.Bits.ToString(culture)}_{FormatType(type, module)}",
            };
        }

        public static string Format(Rvalue rvalue, IrModule? module = null, IReadOnlyList<GenericParam>? generics = null) => rvalue switch
        {
            UseRvalue use => Format(use.Operand, module),
            BinaryRvalue binary => $"{binary.Op}({Format(binary.Left, module)}, {Format(binary.Right, module)})",
            UnaryRvalue unary => $"{unary.Op}({Format(unary.Operand, module)})",
            RefRvalue reference => $"&{(reference.Mutable ? "mut " : "")}{Format(reference.Place)}",
            CastRvalue cast => $"{Format(cast.Operand, module)} as {FormatType(cast.Type, module, generics)}",
            SizeOfRvalue size => $"SizeOf({FormatType(size.Type, module, generics)})",
            ArrayRvalue array => $"{(array.Kind == AllocKind.Raw ? "alloc" : array.Kind.ToString().ToLowerInvariant())} {(array.Owner is { } owner ? FormatType(owner, module, generics) + " " : "")}[{FormatType(array.Element, module, generics)}; {Format(array.Length, module)}]",
            LenRvalue len => $"Len({Format(len.Array)})",
            StaticRvalue data => $"static#{data.Data}",
            IntrinsicRvalue intrinsic => $"intrinsic \"{intrinsic.Name}\"({string.Join(", ", intrinsic.Arguments.Select(a => Format(a, module)))})",
            GlobalRvalue global => (module is not null && global.Global < module.Globals.Length ? module.Globals[global.Global].Name : $"global#{global.Global}")
                + FormatTypeArguments(global.TypeArguments, module, generics),
            TypeIdRvalue typeId => $"typeid {FormatType(typeId.Type, module, generics)}",
            ImplementationRvalue implementation => $"impl {FormatRequirement(implementation.Requirement, module, generics)}",
            ClassIdRvalue classId => $"classid{(classId.End ? ".end" : "")} {FormatType(classId.Class, module, generics)}",
            ZeroRvalue zero => $"{(zero.Filler ? "filler" : "zeroed")} {FormatType(zero.Type, module, generics)}",
            InstancesRvalue instances => $"instances {FunctionName(instances.Function, module)}",
            FunctionRefRvalue reference => $"fn {FunctionName(reference.Function, module)}{FormatTypeArguments(reference.TypeArguments, module, generics)}",
            HandleCastRvalue cast => $"{Format(cast.Handle, module)} as {FormatType(cast.Target, module, generics)}",
            WrapRvalue wrap => $"wrap {Format(wrap.Value, module)}",
            IsNullRvalue test => $"is_null {Format(test.Value)}",
            UnwrapRvalue unwrap => $"unwrap {Format(unwrap.Value, module)}",
            CaughtRvalue => "caught",
            AllocRvalue alloc => $"{(alloc.Kind == AllocKind.Raw ? "alloc" : alloc.Kind.ToString().ToLowerInvariant())} {Format(alloc.Value, module)}"
                + (alloc.Finalizer is { } finalizer ? $" finalized {Format(finalizer, module)}" : ""),
            _ => throw new ArgumentOutOfRangeException(nameof(rvalue)),
        };

        public static string Format(Statement statement, IrModule? module = null, IReadOnlyList<GenericParam>? generics = null) => statement switch
        {
            AssignStatement assign => $"{Format(assign.Target)} = {Format(assign.Value, module, generics)};",
            StorageLiveStatement live => $"StorageLive(_{live.Local});",
            StorageDeadStatement dead => $"StorageDead(_{dead.Local});",
            NopStatement => "nop;",
            FreeStatement free => $"free({Format(free.Pointer)});",
            StoreGlobalStatement store => $"{(module is not null && store.Global < module.Globals.Length ? module.Globals[store.Global].Name : $"global#{store.Global}")}{FormatTypeArguments(store.TypeArguments, module, null)} = {Format(store.Value, module)};",
            RegionEnterStatement enter => $"region_enter(r{enter.Region});",
            RegionExitStatement exit => $"region_exit(r{exit.Region});",
            _ => throw new ArgumentOutOfRangeException(nameof(statement)),
        };

        public static string Format(Terminator terminator, IrModule? module = null, IReadOnlyList<GenericParam>? generics = null) => terminator switch
        {
            GotoTerminator jump => $"goto -> bb{jump.Target};",
            IfTerminator branch => $"if {Format(branch.Condition, module)} -> [true: bb{branch.Then}, false: bb{branch.Else}];",
            SwitchTerminator @switch =>
                $"switch {Format(@switch.Value, module)} -> [{string.Concat(@switch.Targets.Select((t, i) => $"{i}: bb{t}, "))}otherwise: bb{@switch.Otherwise}];",
            ReturnTerminator => "return;",
            CallTerminator { Callee: { } callee } call =>
                $"{Format(call.Destination)} = call {Format(callee, module)}({string.Join(", ", call.Arguments.Select(a => Format(a, module)))}) -> bb{call.Target}{FormatHandlers(call.Handlers, module, generics, " unwind")};",
            CallTerminator { Required: { } required } call =>
                $"{Format(call.Destination)} = require {(generics is not null && required.Param < generics.Count ? generics[required.Param].Name : $"T{required.Param}")}" +
                $".{(generics is not null && required.Param < generics.Count && required.Index < generics[required.Param].Requirements.Length ? generics[required.Param].Requirements[required.Index].Name : $"#{required.Index}")}" +
                $"({string.Join(", ", call.Arguments.Select(a => Format(a, module)))}) -> bb{call.Target}{FormatHandlers(call.Handlers, module, generics, " unwind")};",
            CallTerminator call =>
                $"{Format(call.Destination)} = {FunctionName(call.Function, module)}{FormatTypeArguments(call.TypeArguments, module, generics)}" +
                $"({string.Join(", ", call.Arguments.Select(a => Format(a, module)))}) -> bb{call.Target}{FormatHandlers(call.Handlers, module, generics, " unwind")};",
            ThrowTerminator @throw => $"throw {Format(@throw.Value, module)}{FormatHandlers(@throw.Handlers, module, generics, "")};",
            ResumeTerminator resume => $"resume{FormatHandlers(resume.Handlers, module, generics, "")};",
            DropTerminator drop => $"drop({Format(drop.Place)}) -> bb{drop.Target};",
            UnreachableTerminator => "unreachable;",
            _ => throw new ArgumentOutOfRangeException(nameof(terminator)),
        };

        private static string FormatHandlers(CatchClause[] handlers, IrModule? module, IReadOnlyList<GenericParam>? generics, string prefix) =>
            handlers.Length == 0
                ? ""
                : $"{prefix} [{string.Join(", ", handlers.Select(h => $"catch {(h.Type is null ? "_" : FormatType(h.Type, module, generics))} -> bb{h.Block}"))}]";

        private static string FormatTypeArguments(IrType[] arguments, IrModule? module, IReadOnlyList<GenericParam>? generics) =>
            arguments.Length == 0 ? "" : $"::<{string.Join(", ", arguments.Select(a => FormatType(a, module, generics)))}>";

        private static string FunctionName(int function, IrModule? module) =>
            module is not null && function < module.Functions.Length ? module.Functions[function].Name : $"fn#{function}";
    }
}
