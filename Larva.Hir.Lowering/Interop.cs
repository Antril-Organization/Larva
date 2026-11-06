namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using H = Larva.Hir;
    using IR = Larva.IR;

    /// <summary>
    /// Interop with C:
    /// <list type="bullet">
    /// <item><c>[Import("lib", "Entry")]</c>: the function finds the C function's address the first time (<c>native.symbol</c>,
    /// kept in a global) and calls it; its IR attribute <c>Larva.Import</c> tells the VM to make the native call itself.
    /// A string parameter travels as a temporary null-terminated copy (UTF-8 for <c>str</c>, UTF-16 for
    /// <c>String&lt;u16&gt;</c>), and a null <c>str?</c> as a null pointer: the import is then a wrapper around a raw
    /// import taking pointers.</item>
    /// <item><c>[Export("name")]</c>: the function's C name in native images (attribute <c>Larva.Export</c>).</item>
    /// <item><c>func*</c>: an IR function type; <c>&amp;F</c> its address; a call goes through a helper per signature
    /// (attribute <c>Larva.NativeCall</c>) so the VM knows it calls C.</item>
    /// <item>Pointer arithmetic: through <c>u64</c>, in whole elements.</item>
    /// </list>
    /// </summary>
    internal sealed partial class BodyLowerer
    {
        private static bool IsCString(H.HirType type, out H.Primitive unit) => IsCString(type, out unit, out _);

        /// <summary>A string C takes as a pointer (<c>String&lt;u8&gt;</c>, <c>String&lt;u16&gt;</c>), or one that may be null (<paramref name="nullable"/>).</summary>
        private static bool IsCString(H.HirType type, out H.Primitive unit, out bool nullable)
        {
            nullable = type is H.NullableType;
            if (type is H.NullableType { Inner: var inner })
                type = inner;
            unit = H.Primitive.U8;
            if (type is H.NamedType { Arguments: [H.PrimitiveType { Kind: var u and (H.Primitive.U8 or H.Primitive.U16) }] } named
                && named.Type.IsDefinition)
            {
                unit = u;
                return true;
            }
            return false;
        }

        private IrFunction Import()
        {
            string library = method.Library ?? throw new LoweringException("an import names its library");
            string entry = method.ExternName ?? method.Name;
            var stringParameters = Enumerable.Range(0, parameters.Length)
                .Where(p => IsCString(method.Parameters[p].Type, out _) && lowerer.IsStringType(method.Parameters[p].Type is H.NullableType { Inner: var s } ? s : method.Parameters[p].Type)).ToList();
            if (stringParameters.Count == 0)
                return lowerer.NativeImportBody(b, library, entry, [.. parameters.Select(p => Operand.Copy(p))], plan.Parameters, plan.Result);

            // Strings first become temporary C strings (in a buffer on the stack when they fit, as .NET does, else in
            // native memory given back after the call); the raw import takes pointers.
            var rawParameters = plan.Parameters.ToArray();
            var arguments = new List<Operand>();
            var buffers = new List<(int Chosen, int OnStack, H.Primitive Unit)>();
            b.Unsafe = true;
            for (int p = 0; p < parameters.Length; p++)
            {
                if (!stringParameters.Contains(p))
                {
                    arguments.Add(Operand.Copy(parameters[p]));
                    continue;
                }
                IsCString(method.Parameters[p].Type, out var unit, out bool nullable);
                var element = HirLowerer.Primitive(unit);
                var pointer = IrType.PtrMut(element);
                rawParameters[p] = pointer;
                int capacity = unit == H.Primitive.U8 ? 128 : 64;
                var bufferType = new FixedArrayType(element, capacity);
                int stack = b.Local(bufferType, "cstring", mutable: true);
                b.Assign(stack, Rvalue.Zero(bufferType));
                int reference = Temp(IrType.RefMut(bufferType));
                int raw = Temp(IrType.PtrMut(bufferType));
                int onStack = Temp(pointer);
                b.Assign(reference, Rvalue.RefMut(new Place(stack)))
                 .Assign(raw, Rvalue.Cast(Operand.Move(reference), IrType.PtrMut(bufferType)))
                 .Assign(onStack, Rvalue.Cast(Operand.Copy(raw), pointer));
                int chosen = Temp(pointer);
                int next = NewBlock();
                // `str?`: null is a null pointer; a string is copied as below.
                Operand text = Operand.Copy(parameters[p]);
                if (nullable)
                {
                    int isNull = Temp(IrType.Bool), whenNull = NewBlock(), whenText = NewBlock();
                    b.Assign(isNull, Rvalue.IsNull(new Place(parameters[p])));
                    b.If(Operand.Copy(isNull), whenNull, whenText);
                    At(whenNull);
                    b.Assign(chosen, Rvalue.Cast(Operand.Const(0, IrType.U64), pointer)).Goto(next);
                    At(whenText);
                    int unwrapped = Temp(((OptionalType)Lower(method.Parameters[p].Type)).Inner);
                    b.Assign(unwrapped, Rvalue.Unwrap(Operand.Copy(parameters[p])));
                    text = Operand.Copy(unwrapped);
                }
                int fits = Temp(IrType.Bool);
                Emit(lowerer.CStringFunction(unit == H.Primitive.U8 ? "Utf8Into" : "Utf16Into"),
                    [text, Operand.Copy(onStack), Operand.Const(capacity, IrType.I32)], new Place(fits));
                int small = NewBlock(), large = NewBlock();
                b.If(Operand.Copy(fits), small, large);
                At(small);
                b.Assign(chosen, Operand.Copy(onStack)).Goto(next);
                At(large);
                Emit(lowerer.CStringFunction(unit == H.Primitive.U8 ? "Utf8" : "Utf16"), [text], new Place(chosen));
                b.Goto(next);
                At(next);
                buffers.Add((chosen, onStack, unit));
                arguments.Add(Operand.Copy(chosen));
            }
            int rawImport = lowerer.RawImport(plan.Name + "$native", library, entry, rawParameters, plan.Result);
            int value = Temp(plan.Result);
            Emit(rawImport, arguments, new Place(value));
            foreach (var (chosen, onStack, unit) in buffers)
            {
                // Only what native memory held is given back (not the stack's copy, not a null).
                int a = Temp(IrType.U64), c = Temp(IrType.U64), notStack = Temp(IrType.Bool), notNull = Temp(IrType.Bool), different = Temp(IrType.Bool);
                b.Assign(a, Rvalue.Cast(Operand.Copy(chosen), IrType.U64))
                 .Assign(c, Rvalue.Cast(Operand.Copy(onStack), IrType.U64))
                 .Assign(notStack, Rvalue.Binary(BinaryOp.Ne, Operand.Copy(a), Operand.Copy(c)))
                 .Assign(notNull, Rvalue.Binary(BinaryOp.Ne, Operand.Copy(a), Operand.Const(0, IrType.U64)))
                 .Assign(different, Rvalue.Binary(BinaryOp.And, Operand.Copy(notStack), Operand.Copy(notNull)));
                int free = NewBlock(), next = NewBlock();
                b.If(Operand.Copy(different), free, next);
                At(free);
                Emit(lowerer.CStringFunction(unit == H.Primitive.U8 ? "Free" : "Free16"), [Operand.Copy(chosen)], new Place(Temp(IrType.Unit)));
                b.Goto(next);
                At(next);
            }
            b.Assign(FunctionBuilder.ReturnPlace, Operand.Copy(value)).Return();
            return b.Build();
        }

        /// <summary>A call through a <c>func*</c>: the helper of its signature, with the address first.</summary>
        private void NativeInvoke(H.InvokeExpr invoke, Place target)
        {
            var signature = (FunctionType)Lower(invoke.Function.Type);
            var arguments = new List<Operand> { Value(invoke.Function) };
            foreach (var argument in invoke.Arguments)
                arguments.Add(Value(argument));
            Emit(lowerer.NativeCallHelper(signature), arguments, target);
        }

        /// <summary><c>&amp;F</c>: the function's address.</summary>
        private void NativeFunctionAddress(H.MethodRefExpr reference, Place target) =>
            b.Assign(target, Rvalue.FunctionRef(lowerer.MethodFunctions[reference.Method], []));

        /// <summary>Two pointers compared or subtracted, or a pointer moved by a number of elements.</summary>
        private void PointerBinary(H.BinaryExpr bin, Place target)
        {
            var left = Value(bin.Left);
            var right = Value(bin.Right);
            b.Unsafe = true;
            if (bin.Left.Type is H.PointerType && bin.Right.Type is H.PointerType)
            {
                int a = Temp(IrType.U64), c = Temp(IrType.U64);
                b.Assign(a, Rvalue.Cast(left, IrType.U64)).Assign(c, Rvalue.Cast(right, IrType.U64));
                if (bin.Op != H.BinaryOp.Subtract)
                {
                    b.Assign(target, Rvalue.Binary(Op(bin.Op), Operand.Copy(a), Operand.Copy(c)));
                    return;
                }
                // p - q: the elements between them.
                var pointer = (IR.PointerType)Lower(bin.Left.Type);
                int bytes = Temp(IrType.U64), signed = Temp(IrType.I64), size = Temp(IrType.I64);
                b.Assign(bytes, Rvalue.Binary(BinaryOp.Sub, Operand.Copy(a), Operand.Copy(c)))
                 .Assign(signed, Rvalue.Cast(Operand.Copy(bytes), IrType.I64))
                 .Assign(size, Rvalue.Cast(ElementSize(pointer), IrType.I64))
                 .Assign(target, Rvalue.Binary(BinaryOp.Div, Operand.Copy(signed), Operand.Copy(size)));
                return;
            }
            b.Assign(target, PointerOffset(Op(bin.Op), left, right, (IR.PointerType)Lower(bin.Type)));
        }

        /// <summary>The size of what <paramref name="pointer"/> points at, in bytes (1 for <c>void*</c>), as a <c>u64</c> operand.</summary>
        private Operand ElementSize(IR.PointerType pointer)
        {
            if (pointer.Pointee == IrType.Unit)
                return Operand.Const(1, IrType.U64);
            int size = Temp(IrType.U64);
            b.Assign(size, Rvalue.SizeOf(pointer.Pointee));
            return Operand.Copy(size);
        }

        /// <summary><c>pointer ± count</c>, moving by whole elements.</summary>
        private Rvalue PointerOffset(BinaryOp op, Operand pointerValue, Operand count, IR.PointerType pointer)
        {
            b.Unsafe = true;
            int raw = Temp(IrType.U64), n = Temp(IrType.U64), scaled = Temp(IrType.U64), moved = Temp(IrType.U64);
            b.Assign(raw, Rvalue.Cast(pointerValue, IrType.U64))
             .Assign(n, Rvalue.Cast(count, IrType.U64));
            var size = ElementSize(pointer);
            b.Assign(scaled, Rvalue.Binary(BinaryOp.Mul, Operand.Copy(n), size))
             .Assign(moved, Rvalue.Binary(op, Operand.Copy(raw), Operand.Copy(scaled)));
            return Rvalue.Cast(Operand.Copy(moved), pointer);
        }
    }

    public sealed partial class HirLowerer
    {
        /// <summary>Whether <paramref name="type"/> is <c>System.String&lt;T&gt;</c>.</summary>
        internal bool IsStringType(H.HirType type) =>
            type is H.NamedType { Type.IsDefinition: true } named && Types[named.Type.Index].Definition.FullName == "System.String";

        private readonly Dictionary<string, int> cStringFunctions = [];

        /// <summary>A function of <c>System.Runtime.InteropServices.CStrings</c> (the library's C string helpers).</summary>
        internal int CStringFunction(string name)
        {
            lock (cStringFunctions)
            {
                if (cStringFunctions.TryGetValue(name, out int known))
                    return known;
                foreach (var info in Types)
                {
                    if (info.Definition.FullName != "System.Runtime.InteropServices.CStrings")
                        continue;
                    int m = info.Definition.Methods.FindIndex(x => x.Name == name);
                    if (m >= 0)
                        return cStringFunctions[name] = MethodFunctions[H.MemberToken.Def(info.Index, m)];
                }
            }
            throw new LoweringException($"strings to C need System.Runtime.InteropServices.CStrings.{name} (System.Core)");
        }

        private readonly Dictionary<(string, string), int> importGlobals = [];

        /// <summary>The global keeping the address of C function <paramref name="entry"/> of <paramref name="library"/> (0 until found).</summary>
        private int ImportGlobal(string library, string entry)
        {
            lock (Globals)
            {
                if (!importGlobals.TryGetValue((library, entry), out int global))
                {
                    global = Globals.Count;
                    Globals.Add(new GlobalDef($"$import.{library}.{entry}", IrType.U64));
                    importGlobals[(library, entry)] = global;
                }
                return global;
            }
        }

        /// <summary>
        /// The body of an import (in <paramref name="b"/>, whose arguments are <paramref name="arguments"/>): the C function's
        /// address, found once, then the call.
        /// </summary>
        internal IrFunction NativeImportBody(FunctionBuilder b, string library, string entry, Operand[] arguments, IrType[] parameters, IrType result)
        {
            int global = ImportGlobal(library, entry);
            var text = new IR.ReferenceType(new IR.ArrayType(IrType.U16), Mutable: false);
            int address = b.Local(IrType.U64, "address", mutable: true);
            int missing = b.Local(IrType.Bool, "missing", mutable: true);
            int resolve = b.NewBlock(), call = b.NewBlock(), done = b.NewBlock();
            b.Assign(address, Rvalue.ReadGlobal(global))
             .Assign(missing, Rvalue.Binary(BinaryOp.Eq, Operand.Copy(address), Operand.Const(0, IrType.U64)))
             .If(Operand.Copy(missing), resolve, call);
            int lib = b.Local(text, "library", mutable: true), name = b.Local(text, "entry", mutable: true);
            b.At(resolve)
             .Assign(lib, Rvalue.Static(StringData(library)))
             .Assign(name, Rvalue.Static(StringData(entry)))
             .Assign(address, new IntrinsicRvalue("native.symbol", [Operand.Copy(lib), Operand.Copy(name)]))
             .StoreGlobal(global, Operand.Copy(address))
             .Goto(call);
            var signature = new FunctionType(parameters, result);
            int code = b.Local(signature, "code", mutable: true);
            b.Unsafe = true;
            b.At(call)
             .Assign(code, Rvalue.Cast(Operand.Copy(address), signature))
             .CallIndirect(Operand.Copy(code), arguments, FunctionBuilder.ReturnPlace, done);
            b.At(done).Return();
            var built = b.Build();
            return built with { Attributes = [.. built.Attributes, new IrAttribute("Larva.Import", [new TextArgument(library), new TextArgument(entry)])] };
        }

        private readonly Dictionary<string, int> rawImports = [];

        /// <summary>An import's raw form (strings as pointers), made once.</summary>
        internal int RawImport(string name, string library, string entry, IrType[] parameters, IrType result)
        {
            lock (Functions)
            {
                if (rawImports.TryGetValue(name, out int known))
                    return known;
                var b = new FunctionBuilder(name, result) { Linkage = Linkage.Internal };
                var arguments = parameters.Select((p, i) => Operand.Copy(b.Argument(p, $"a{i}", mutable: true))).ToArray();
                int function = Functions.Count;
                Functions.Add(NativeImportBody(b, library, entry, arguments, parameters, result));
                return rawImports[name] = function;
            }
        }

        private readonly Dictionary<FunctionType, int> nativeCalls = [];

        /// <summary><c>$NativeCall{n}(code, args…)</c>: calls the C function at <c>code</c> (attribute <c>Larva.NativeCall</c>).</summary>
        internal int NativeCallHelper(FunctionType signature)
        {
            lock (Functions)
            {
                if (nativeCalls.TryGetValue(signature, out int known))
                    return known;
                var b = new FunctionBuilder($"$NativeCall{nativeCalls.Count}", signature.Result) { Linkage = Linkage.Internal };
                int code = b.Argument(signature, "code", mutable: true);
                var arguments = signature.Parameters.Select((p, i) => Operand.Copy(b.Argument(p, $"a{i}", mutable: true))).ToArray();
                int done = b.NewBlock();
                b.CallIndirect(Operand.Copy(code), arguments, FunctionBuilder.ReturnPlace, done);
                b.At(done).Return();
                var built = b.Build();
                int function = Functions.Count;
                Functions.Add(built with { Attributes = [.. built.Attributes, new IrAttribute("Larva.NativeCall", [])] });
                return nativeCalls[signature] = function;
            }
        }
    }
}
