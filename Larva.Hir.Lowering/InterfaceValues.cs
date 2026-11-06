namespace Larva.Hir.Lowering
{
    using Larva.IR;
    using H = Larva.Hir;

    /// <summary>Calls through interface values, boxing and unboxing, and type tests against interfaces and values.</summary>
    internal sealed partial class BodyLowerer
    {
        private HirLowerer.TypeInfo? InterfaceOf(H.HirType type) =>
            type is H.NamedType { Type.IsDefinition: true } n && lowerer.Types[n.Type.Index].IsInterface ? lowerer.Types[n.Type.Index] : null;

        /// <summary>Whether a HIR type is a value that becomes an object in a box (a primitive, a value type, an enum).</summary>
        private bool IsBoxable(H.HirType type) => type is H.PrimitiveType { Kind: not H.Primitive.Void }
            || type is H.NamedType { Type.IsDefinition: true } n && lowerer.Types[n.Type.Index] is { IsStruct: true } or { IsEnum: true };

        /// <summary>
        /// The itable of interface <paramref name="iface"/> (with its arguments) in the object (the local
        /// <paramref name="root"/>), or null: into a new local. The vtable's fixed entry of the interface is read first; the
        /// finder's search runs only when its id is not the itable's.
        /// </summary>
        private int FindItable(int root, H.NamedType iface)
        {
            var info = lowerer.Types[iface.Type.Index];
            var itable = lowerer.Itables[info.Index];
            var arguments = iface.Arguments.Select(Lower).ToArray();
            var tableType = new IR.PointerType(IrType.Struct(itable.Struct, arguments), Mutable: true);
            var result = new OptionalType(tableType);
            int found = Temp(result);
            int color = HirLowerer.Color(info.Name);
            int vt = Temp(HirLowerer.VtablePointer(IrType.Struct(lowerer.ObjectVtable)));
            int address = Temp(IrType.U64);
            int test = Temp(IrType.Bool);
            int id = Temp(IrType.I32);
            int want = Temp(IrType.I32);
            int raw = Temp(IrType.U64);
            int table = Temp(tableType);
            int check = NewBlock(), hit = NewBlock(), search = NewBlock(), next = NewBlock();
            b.Unsafe = true;
            b.Assign(vt, Operand.Copy(new Place(root).Deref().Field(1)))
             .Assign(address, Rvalue.Cast(Operand.Copy(vt), IrType.U64))
             .Assign(test, Rvalue.Binary(BinaryOp.Eq, Operand.Copy(address), Operand.Const(0, IrType.U64)))
             .If(Operand.Copy(test), search, check);
            At(check);
            b.Assign(id, Operand.Copy(new Place(vt).Deref().Field(1 + color).Field(0)))
             .Assign(want, Rvalue.TypeId(IrType.Struct(itable.Struct, arguments)))
             .Assign(test, Rvalue.Binary(BinaryOp.Eq, Operand.Copy(id), Operand.Copy(want)))
             .If(Operand.Copy(test), hit, search);
            At(hit);
            b.Assign(raw, Operand.Copy(new Place(vt).Deref().Field(1 + color).Field(1)))
             .Assign(table, Rvalue.Cast(Operand.Copy(raw), tableType))
             .Assign(found, Rvalue.Wrap(Operand.Copy(table)))
             .Goto(next);
            At(search);
            b.Call(itable.Finder, arguments, [Operand.Copy(root)], new Place(found), next, Handlers());
            At(next);
            return found;
        }

        /// <summary>A call of an interface method on an interface value: its itable found, the method called through it.</summary>
        private void InterfaceCall(H.CallExpr call, H.NamedType iface, HirLowerer.TypeInfo declaring, H.MethodDef callee, Place target)
        {
            var info = lowerer.Types[iface.Type.Index];
            var itable = lowerer.Itables[info.Index];
            int slot = itable.Methods.FindIndex(m => m.Interface == declaring && m.Method == callee);
            if (slot < 0)
                throw new LoweringException($"{info.Name} has no method {callee.Name} in its table");
            int receiver = Temp(lowerer.RootHandle);
            Into(new Place(receiver), call.Receiver!);
            int found = FindItable(receiver, iface);
            var arguments = iface.Arguments.Select(Lower).ToArray();
            var tableType = new IR.PointerType(IrType.Struct(itable.Struct, arguments), Mutable: true);
            int table = Temp(tableType);
            b.Unsafe = true;
            b.Assign(table, Rvalue.Unwrap(Operand.Copy(found)));
            var fieldType = lowerer.Structs[itable.Struct].Fields[slot].Substitute(arguments);
            int function = Temp(fieldType);
            b.Assign(function, Operand.Copy(new Place(table).Deref().Field(slot)));
            var operands = new List<Operand> { Operand.Copy(receiver) };
            foreach (var argument in call.Arguments)
                operands.Add(Value(argument));
            int next = NewBlock();
            b.CallIndirect(Operand.Copy(function), [.. operands], target, next, Handlers());
            At(next);
        }

        /// <summary>A value boxed: a new object of its box class holding a copy of it, as `any`.</summary>
        private void BoxInto(Operand value, H.HirType valueType, Place target, IrType targetType)
        {
            if (lowerer.ValueInfo(valueType) is not { } info)
                throw Unsupported($"boxing a {H.Text.HirPrinter.Print(valueType, lowerer.Assembly)} (its type's members are not in the program)");
            var box = lowerer.BoxOf(info);
            moduleView = null;
            var arguments = valueType is H.NamedType n ? n.Arguments.Select(Lower).ToArray() : [];
            var boxStruct = IrType.Struct(box.Struct, arguments);
            int made = Temp(boxStruct);
            int vt = Temp(HirLowerer.VtablePointer(IrType.Struct(box.Vtable, arguments)));
            b.Assign(made, Rvalue.ZeroFiller(boxStruct));
            b.Assign(new Place(made).Field(0), Rvalue.ClassId(boxStruct));
            Emit(box.Maker, arguments, [], new Place(vt));
            b.Assign(new Place(made).Field(1), Rvalue.HandleCast(Operand.Copy(vt), HirLowerer.VtablePointer(IrType.Struct(lowerer.ObjectVtable))));
            b.Assign(new Place(made).Field(2), value);
            var handleType = lowerer.Handle(boxStruct);
            int handle = Temp(handleType);
            b.Assign(handle, new AllocRvalue(lowerer.ObjectAlloc, Operand.Move(made)));
            b.Assign(target, Rvalue.HandleCast(Operand.Copy(handle), targetType));
        }

        private static bool IsHandle(IrType type) => type is GcType or IR.PointerType { Pointee: StructType } or OptionalType { Inner: GcType or IR.PointerType };

        /// <summary>The handle type of a value's box, and its struct (to test an object against it).</summary>
        private IrType BoxHandle(H.HirType valueType)
        {
            if (lowerer.ValueInfo(valueType) is not { } info)
                throw Unsupported($"unboxing a {H.Text.HirPrinter.Print(valueType, lowerer.Assembly)}");
            var box = lowerer.BoxOf(info);
            moduleView = null;
            var arguments = valueType is H.NamedType n ? n.Arguments.Select(Lower).ToArray() : [];
            return lowerer.Handle(IrType.Struct(box.Struct, arguments));
        }

        /// <summary>The value in a box (the object already known to be one), into <paramref name="target"/>.</summary>
        private void UnboxInto(Operand handle, IrType handleType, H.HirType valueType, Place target)
        {
            var boxHandle = BoxHandle(valueType);
            int root = Temp(lowerer.RootHandle);
            b.Assign(root, ToRoot(handle, handleType));
            int boxed = Temp(boxHandle);
            b.Unsafe = true;
            b.Assign(boxed, Rvalue.HandleCast(Operand.Copy(root), boxHandle));
            b.Assign(target, Copied(new Place(boxed).Deref().Field(2), valueType));
        }

        /// <summary>
        /// Jumps to <paramref name="yes"/> when the object (a handle, maybe nullable) is a <paramref name="target"/>: a class
        /// (its class id), an interface (its itable), or a value (its box).
        /// </summary>
        private void TestType(Operand handle, IrType handleType, H.HirType target, int yes, int no)
        {
            if (InterfaceOf(target) is not null)
            {
                int value = Temp(handleType);
                b.Assign(value, handle);
                int root = Temp(lowerer.RootHandle);
                if (handleType is OptionalType)
                {
                    int isNull = Temp(IrType.Bool);
                    int present = NewBlock();
                    b.Assign(isNull, Rvalue.IsNull(value)).If(Operand.Copy(isNull), no, present);
                    At(present);
                }
                b.Assign(root, ToRoot(Operand.Copy(value), handleType));
                int found = FindItable(root, (H.NamedType)target);
                int missing = Temp(IrType.Bool);
                b.Assign(missing, Rvalue.IsNull(found)).If(Operand.Copy(missing), no, yes);
                open = false;
                return;
            }
            if (IsBoxable(target))
            {
                TypeTest(handle, handleType, BoxHandle(target), yes, no);
                return;
            }
            TypeTest(handle, handleType, Lower(target), yes, no);
        }
    }
}
