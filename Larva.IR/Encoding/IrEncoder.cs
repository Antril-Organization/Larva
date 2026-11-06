using Larva.IL.Encoding;

namespace Larva.IR.Encoding
{
    /// <summary>Writes an <see cref="IrModule"/> in the nibble format described by <see cref="IrFormat"/>.</summary>
    public static class IrEncoder
    {
        public static byte[] Encode(IrModule module)
        {
            NibbleWriter writer = new();
            Encode(writer, module);
            return writer.ToArray();
        }

        public static int SizeInNibbles(IrModule module)
        {
            NibbleWriter writer = new();
            Encode(writer, module);
            return writer.Length;
        }

        public static void Encode(NibbleWriter writer, IrModule module)
        {
            WriteName(writer, module.Name);
            writer.WriteUnsigned((ulong)module.Structs.Length);
            foreach (var def in module.Structs)
            {
                WriteName(writer, def.Name);
                WriteGenerics(writer, def.Generics);
                writer.WriteNibble(IrFormat.EncodeReflect(def.Reflect) | (def.FieldNames.Length > 0 ? IrFormat.StructFieldNames : 0) | (def.Mutable ? IrFormat.StructMutable : 0));
                if (def.FieldNames.Length > 0)
                {
                    writer.WriteUnsigned((ulong)def.FieldNames.Length);
                    foreach (var fieldName in def.FieldNames)
                        WriteName(writer, fieldName);
                }
                WriteLinkage(writer, def.Linkage, def.Symbol);
                writer.WriteUnsigned((ulong)def.Fields.Length);
                foreach (var field in def.Fields)
                    WriteType(writer, field);
                writer.WriteNibble(def.Base is null ? 0 : 1);
                if (def.Base is not null)
                    WriteType(writer, def.Base);
                WriteAttributes(writer, def.Attributes);
                writer.WriteUnsigned((ulong)def.FieldAttributes.Length);
                foreach (var field in def.FieldAttributes)
                    WriteAttributes(writer, field);
            }

            writer.WriteUnsigned((ulong)module.Functions.Length);
            foreach (var function in module.Functions)
                WriteFunction(writer, function);

            writer.WriteUnsigned((ulong)module.Data.Length);
            foreach (var data in module.Data)
                WriteStaticData(writer, data);

            writer.WriteUnsigned((ulong)module.Globals.Length);
            foreach (var global in module.Globals)
                WriteGlobal(writer, global);

            writer.WriteUnsigned((ulong)module.Implementations.Length);
            foreach (var implementation in module.Implementations)
            {
                WriteName(writer, implementation.Name);
                writer.WriteUnsigned((ulong)implementation.Function);
            }
        }

        /// <summary><c>count:uv {name args:uv {0 type bits:sv | 1 name | 2 type}*}*</c></summary>
        private static void WriteAttributes(NibbleWriter writer, IrAttribute[] attributes)
        {
            writer.WriteUnsigned((ulong)attributes.Length);
            foreach (var attribute in attributes)
            {
                WriteName(writer, attribute.Name);
                writer.WriteUnsigned((ulong)attribute.Arguments.Length);
                foreach (var argument in attribute.Arguments)
                {
                    switch (argument)
                    {
                        case ConstantArgument constant:
                            writer.WriteNibble(0);
                            WriteType(writer, constant.Value.Type);
                            writer.WriteSigned(constant.Value.Bits);
                            break;
                        case TextArgument text:
                            writer.WriteNibble(1);
                            WriteName(writer, text.Value);
                            break;
                        case TypeArgument type:
                            writer.WriteNibble(2);
                            WriteType(writer, type.Type);
                            break;
                    }
                }
            }
        }

        internal static void WriteGlobal(NibbleWriter writer, GlobalDef global)
        {
            WriteName(writer, global.Name);
            WriteType(writer, global.Type);
            // Bit 0: has an initial value; bits 1-2: linkage; the rest: the number of type parameters.
            writer.WriteUnsigned((global.Initial is null ? 0u : 1u) | (ulong)global.Linkage << 1 | (ulong)global.GenericCount << 3);
            if (global.Linkage == Linkage.Import)
                WriteName(writer, global.Symbol ?? "");
            if (global.Initial is { } initial)
                writer.WriteSigned(initial);
        }

        internal static void WriteStaticData(NibbleWriter writer, StaticData data)
        {
            WriteType(writer, data.Element);
            writer.WriteUnsigned((ulong)data.Values.Length);
            foreach (var value in data.Values)
                writer.WriteSigned(value);
        }

        private static void WriteFunction(NibbleWriter writer, IrFunction function)
        {
            WriteName(writer, function.Name);
            writer.WriteNibble((function.Unsafe ? IrFormat.FunctionUnsafe : 0) | (function.Mangled ? IrFormat.FunctionMangled : 0) | IrFormat.EncodeReflect(function.Reflect));
            WriteLinkage(writer, function.Linkage, function.Symbol);
            WriteGenerics(writer, function.Generics);
            writer.WriteUnsigned((ulong)function.RegionParams.Length);
            foreach (var region in function.RegionParams)
                WriteName(writer, region);
            writer.WriteUnsigned((ulong)function.ArgumentCount);
            writer.WriteUnsigned((ulong)function.Locals.Length);
            foreach (var local in function.Locals)
            {
                int flags = (local.Mutable ? IrFormat.LocalMutable : 0)
                    | (local.Name is not null ? IrFormat.LocalNamed : 0)
                    | (local.Region is not null ? IrFormat.LocalRegion : 0);
                writer.WriteNibble(flags);
                WriteType(writer, local.Type);
                if (local.Name is not null)
                    WriteName(writer, local.Name);
                if (local.Region is int region)
                    writer.WriteUnsigned((ulong)region);
            }
            WriteAttributes(writer, function.CallerRegion is int callerRegion
                ? [.. function.Attributes, new IrAttribute(IrFunction.CallerRegionAttribute, [new ConstantArgument((ConstOperand)Operand.Int(callerRegion, IrType.I32))])]
                : function.Attributes);

            writer.WriteUnsigned((ulong)function.Blocks.Length);
            foreach (var block in function.Blocks)
            {
                writer.WriteUnsigned((ulong)block.Statements.Length);
                foreach (var statement in block.Statements)
                    WriteStatement(writer, statement);
                WriteTerminator(writer, block.Terminator);
            }
        }

        private static void WriteStatement(NibbleWriter writer, Statement statement)
        {
            switch (statement)
            {
                case AssignStatement assign:
                    WriteAssign(writer, assign);
                    break;
                case StorageLiveStatement live:
                    WriteTag(writer, StatementTag.StorageLive);
                    writer.WriteUnsigned((ulong)live.Local);
                    break;
                case StorageDeadStatement dead:
                    WriteTag(writer, StatementTag.StorageDead);
                    writer.WriteUnsigned((ulong)dead.Local);
                    break;
                case NopStatement:
                    WriteTag(writer, StatementTag.Nop);
                    break;
                case FreeStatement free:
                    WriteTag(writer, StatementTag.Free);
                    WritePlace(writer, free.Pointer);
                    break;
                case StoreGlobalStatement store:
                    WriteTag(writer, store.TypeArguments.Length > 0 ? StatementTag.StoreGenericGlobal : StatementTag.StoreGlobal);
                    writer.WriteUnsigned((ulong)store.Global);
                    if (store.TypeArguments.Length > 0)
                    {
                        writer.WriteUnsigned((ulong)store.TypeArguments.Length);
                        foreach (var argument in store.TypeArguments)
                            WriteType(writer, argument);
                    }
                    WriteOperand(writer, store.Value);
                    break;
                case RegionEnterStatement enter:
                    WriteTag(writer, StatementTag.RegionEnter);
                    writer.WriteUnsigned((ulong)enter.Region);
                    break;
                case RegionExitStatement exit:
                    WriteTag(writer, StatementTag.RegionExit);
                    writer.WriteUnsigned((ulong)exit.Region);
                    break;
                default:
                    throw new NotSupportedException(statement.GetType().Name);
            }
        }

        private static void WriteAssign(NibbleWriter writer, AssignStatement assign)
        {
            var tag = assign.Value switch
            {
                UseRvalue => StatementTag.AssignUse,
                BinaryRvalue => StatementTag.AssignBinary,
                UnaryRvalue => StatementTag.AssignUnary,
                RefRvalue { Mutable: false } => StatementTag.AssignRef,
                RefRvalue { Mutable: true } => StatementTag.AssignRefMut,
                CastRvalue => StatementTag.AssignCast,
                SizeOfRvalue => StatementTag.AssignSizeOf,
                AllocRvalue => StatementTag.AssignAlloc,
                ArrayRvalue => StatementTag.AssignArray,
                LenRvalue => StatementTag.AssignLen,
                CaughtRvalue => StatementTag.AssignCaught,
                StaticRvalue => StatementTag.AssignStatic,
                IntrinsicRvalue => StatementTag.AssignIntrinsic,
                ZeroRvalue { Filler: true } => StatementTag.AssignFiller,
                ZeroRvalue => StatementTag.AssignZero,
                GlobalRvalue { TypeArguments.Length: > 0 } => StatementTag.AssignGenericGlobal,
                GlobalRvalue => StatementTag.AssignGlobal,
                TypeIdRvalue => StatementTag.AssignTypeId,
                ImplementationRvalue => StatementTag.AssignImplementation,
                ClassIdRvalue { End: true } => StatementTag.AssignClassIdEnd,
                ClassIdRvalue => StatementTag.AssignClassId,
                FunctionRefRvalue => StatementTag.AssignFunctionRef,
                InstancesRvalue => StatementTag.AssignInstances,
                HandleCastRvalue => StatementTag.AssignHandleCast,
                WrapRvalue => StatementTag.AssignWrap,
                IsNullRvalue => StatementTag.AssignIsNull,
                UnwrapRvalue => StatementTag.AssignUnwrap,
                _ => throw new NotSupportedException(assign.Value.GetType().Name),
            };
            WriteTag(writer, tag);
            WritePlace(writer, assign.Target);

            switch (assign.Value)
            {
                case UseRvalue use:
                    WriteOperand(writer, use.Operand);
                    break;
                case BinaryRvalue binary:
                    writer.WriteNibble((int)binary.Op);
                    WriteOperand(writer, binary.Left);
                    WriteOperand(writer, binary.Right);
                    break;
                case UnaryRvalue unary:
                    writer.WriteNibble((int)unary.Op);
                    WriteOperand(writer, unary.Operand);
                    break;
                case RefRvalue reference:
                    WritePlace(writer, reference.Place);
                    break;
                case CastRvalue cast:
                    WriteOperand(writer, cast.Operand);
                    WriteType(writer, cast.Type);
                    break;
                case SizeOfRvalue size:
                    WriteType(writer, size.Type);
                    break;
                case AllocRvalue alloc:
                    writer.WriteNibble((int)alloc.Kind | (alloc.Finalizer is null ? 0 : 8));
                    WriteOperand(writer, alloc.Value);
                    if (alloc.Finalizer is { } finalizer)
                        WriteOperand(writer, finalizer);
                    break;
                case ArrayRvalue array:
                    // Bit 4: an owner struct follows the element (kinds are 0..2).
                    writer.WriteNibble((int)array.Kind | (array.Filled ? 8 : 0) | (array.Owner is null ? 0 : 4));
                    WriteType(writer, array.Element);
                    if (array.Owner is { } owner)
                        WriteType(writer, owner);
                    WriteOperand(writer, array.Length);
                    break;
                case LenRvalue len:
                    WritePlace(writer, len.Array);
                    break;
                case StaticRvalue data:
                    writer.WriteUnsigned((ulong)data.Data);
                    break;
                case IntrinsicRvalue intrinsic:
                    WriteName(writer, intrinsic.Name);
                    writer.WriteUnsigned((ulong)intrinsic.Arguments.Length);
                    foreach (var argument in intrinsic.Arguments)
                        WriteOperand(writer, argument);
                    break;
                case ZeroRvalue zero:
                    WriteType(writer, zero.Type);
                    break;
                case GlobalRvalue global:
                    writer.WriteUnsigned((ulong)global.Global);
                    if (global.TypeArguments.Length > 0)
                    {
                        writer.WriteUnsigned((ulong)global.TypeArguments.Length);
                        foreach (var argument in global.TypeArguments)
                            WriteType(writer, argument);
                    }
                    break;
                case TypeIdRvalue typeId:
                    WriteType(writer, typeId.Type);
                    break;
                case ImplementationRvalue implementation:
                    WriteName(writer, implementation.Requirement.Name);
                    writer.WriteUnsigned((ulong)implementation.Requirement.Parameters.Length);
                    foreach (var type in implementation.Requirement.Parameters)
                        WriteType(writer, type);
                    WriteType(writer, implementation.Requirement.Result);
                    break;
                case ClassIdRvalue classId:
                    WriteType(writer, classId.Class);
                    break;
                case InstancesRvalue instances:
                    writer.WriteUnsigned((ulong)instances.Function);
                    break;
                case FunctionRefRvalue reference:
                    writer.WriteUnsigned((ulong)reference.Function);
                    writer.WriteUnsigned((ulong)reference.TypeArguments.Length);
                    foreach (var typeArgument in reference.TypeArguments)
                        WriteType(writer, typeArgument);
                    break;
                case HandleCastRvalue cast:
                    WriteOperand(writer, cast.Handle);
                    WriteType(writer, cast.Target);
                    break;
                case WrapRvalue wrap:
                    WriteOperand(writer, wrap.Value);
                    break;
                case IsNullRvalue test:
                    WritePlace(writer, test.Value);
                    break;
                case UnwrapRvalue unwrap:
                    WriteOperand(writer, unwrap.Value);
                    break;
            }
        }

        private static void WriteTerminator(NibbleWriter writer, Terminator terminator)
        {
            switch (terminator)
            {
                case GotoTerminator jump:
                    writer.WriteNibble((int)TerminatorTag.Goto);
                    writer.WriteUnsigned((ulong)jump.Target);
                    break;
                case IfTerminator branch:
                    writer.WriteNibble((int)TerminatorTag.If);
                    WriteOperand(writer, branch.Condition);
                    writer.WriteUnsigned((ulong)branch.Then);
                    writer.WriteUnsigned((ulong)branch.Else);
                    break;
                case SwitchTerminator @switch:
                    writer.WriteNibble((int)TerminatorTag.Switch);
                    WriteOperand(writer, @switch.Value);
                    writer.WriteUnsigned((ulong)@switch.Targets.Length);
                    foreach (var target in @switch.Targets)
                        writer.WriteUnsigned((ulong)target);
                    writer.WriteUnsigned((ulong)@switch.Otherwise);
                    break;
                case ReturnTerminator:
                    writer.WriteNibble((int)TerminatorTag.Return);
                    break;
                case CallTerminator call:
                    writer.WriteNibble((int)TerminatorTag.Call);
                    // 0 = through a requirement (param, index follow); 1 = indirect (the callee follows); else the function index + 2.
                    writer.WriteUnsigned(call.Required is not null ? 0 : call.Callee is not null ? 1 : (ulong)call.Function + 2);
                    if (call.Required is { } required)
                    {
                        writer.WriteUnsigned((ulong)required.Param);
                        writer.WriteUnsigned((ulong)required.Index);
                    }
                    if (call.Callee is { } callee)
                        WriteOperand(writer, callee);
                    writer.WriteUnsigned((ulong)call.TypeArguments.Length);
                    foreach (var typeArgument in call.TypeArguments)
                        WriteType(writer, typeArgument);
                    writer.WriteUnsigned((ulong)call.Arguments.Length);
                    foreach (var argument in call.Arguments)
                        WriteOperand(writer, argument);
                    WritePlace(writer, call.Destination);
                    writer.WriteUnsigned((ulong)call.Target);
                    WriteHandlers(writer, call.Handlers);
                    break;
                case ThrowTerminator @throw:
                    writer.WriteNibble((int)TerminatorTag.Throw);
                    WriteOperand(writer, @throw.Value);
                    WriteHandlers(writer, @throw.Handlers);
                    break;
                case ResumeTerminator resume:
                    writer.WriteNibble((int)TerminatorTag.Resume);
                    WriteHandlers(writer, resume.Handlers);
                    break;
                case DropTerminator drop:
                    writer.WriteNibble((int)TerminatorTag.Drop);
                    WritePlace(writer, drop.Place);
                    writer.WriteUnsigned((ulong)drop.Target);
                    break;
                case UnreachableTerminator:
                    writer.WriteNibble((int)TerminatorTag.Unreachable);
                    break;
                default:
                    throw new NotSupportedException(terminator.GetType().Name);
            }
        }

        /// <summary>Statement tags 0-14 take one nibble; 15 escapes to a second nibble (tag - 15).</summary>
        private static void WriteTag(NibbleWriter writer, StatementTag tag)
        {
            int value = (int)tag;
            if (value < IrFormat.StatementTagEscape)
            {
                writer.WriteNibble(value);
                return;
            }
            // 15 + n for tags 15..29; 15, 15, n beyond (each escape adds 15).
            while (value >= IrFormat.StatementTagEscape)
            {
                writer.WriteNibble(IrFormat.StatementTagEscape);
                value -= IrFormat.StatementTagEscape;
            }
            writer.WriteNibble(value);
        }

        private static void WriteHandlers(NibbleWriter writer, CatchClause[] handlers)
        {
            writer.WriteUnsigned((ulong)handlers.Length);
            foreach (var handler in handlers)
            {
                writer.WriteNibble(handler.Type is null ? 0 : 1);
                if (handler.Type is { } type)
                    WriteType(writer, type);
                writer.WriteUnsigned((ulong)handler.Block);
            }
        }

        private static void WriteOperand(NibbleWriter writer, Operand operand)
        {
            switch (operand)
            {
                case CopyOperand copy:
                    writer.WriteNibble(IrFormat.OperandCopy);
                    WritePlace(writer, copy.Place);
                    break;
                case MoveOperand move:
                    writer.WriteNibble(IrFormat.OperandMove);
                    WritePlace(writer, move.Place);
                    break;
                case ConstOperand constant:
                    writer.WriteNibble(IrFormat.OperandConst);
                    WriteType(writer, constant.Type);
                    if (!constant.Type.IsUnit)
                        writer.WriteSigned(constant.Bits);
                    break;
                default:
                    throw new NotSupportedException(operand.GetType().Name);
            }
        }

        private static void WritePlace(NibbleWriter writer, Place place)
        {
            writer.WriteUnsigned((ulong)place.Local);
            writer.WriteUnsigned((ulong)place.Projections.Length);
            foreach (var projection in place.Projections)
            {
                switch (projection)
                {
                    case DerefProjection:
                        writer.WriteNibble(IrFormat.ProjectionDeref);
                        break;
                    case FieldProjection field:
                        writer.WriteNibble(IrFormat.ProjectionField);
                        writer.WriteUnsigned((ulong)field.FieldIndex);
                        break;
                    case IndexProjection index:
                        writer.WriteNibble(IrFormat.ProjectionIndex);
                        writer.WriteUnsigned((ulong)index.Local);
                        break;
                }
            }
        }

        internal static void WriteType(NibbleWriter writer, IrType type)
        {
            switch (type)
            {
                case PrimitiveType { Kind: <= Primitive.F64 } primitive:
                    writer.WriteNibble((int)primitive.Kind);
                    break;
                case PrimitiveType primitive:
                    writer.WriteNibble(IrFormat.TypeExtended);
                    writer.WriteNibble((int)primitive.Kind - IrFormat.ExtendedPrimitiveBase);
                    break;
                case StructType st:
                    writer.WriteNibble(IrFormat.TypeStruct);
                    writer.WriteUnsigned((ulong)st.Id);
                    writer.WriteUnsigned((ulong)st.Arguments.Length);
                    foreach (var argument in st.Arguments)
                        WriteType(writer, argument);
                    break;
                case GcType gc:
                    WriteExtended(writer, IrFormat.ExtendedGc);
                    WriteType(writer, gc.Pointee);
                    break;
                case BoxType box:
                    WriteExtended(writer, IrFormat.ExtendedBox);
                    WriteType(writer, box.Pointee);
                    break;
                case PointerType pointer:
                    WriteExtended(writer, pointer.Mutable ? IrFormat.ExtendedPointerMut : IrFormat.ExtendedPointer);
                    WriteType(writer, pointer.Pointee);
                    break;
                case GenericParamType parameter:
                    WriteExtended(writer, IrFormat.ExtendedGenericParam);
                    writer.WriteUnsigned((ulong)parameter.Index);
                    break;
                case ReferenceType reference:
                    writer.WriteNibble(reference.Mutable ? IrFormat.TypeRefMut : IrFormat.TypeRef);
                    WriteType(writer, reference.Pointee);
                    break;
                case ArrayType array:
                    writer.WriteNibble(IrFormat.TypeExtended);
                    writer.WriteNibble(IrFormat.ExtendedArray);
                    WriteType(writer, array.Element);
                    break;
                case FixedArrayType inline:
                    WriteExtended(writer, IrFormat.ExtendedFixedArray);
                    WriteType(writer, inline.Element);
                    writer.WriteUnsigned((ulong)inline.Count);
                    writer.WriteNibble(inline.SizeOf is null ? 0 : 1);
                    if (inline.SizeOf is not null)
                        WriteType(writer, inline.SizeOf);
                    break;
                case OptionalType optional:
                    WriteExtended(writer, IrFormat.ExtendedOptional);
                    WriteType(writer, optional.Inner);
                    break;
                case FunctionType function:
                    WriteExtended(writer, IrFormat.ExtendedFunction);
                    writer.WriteUnsigned((ulong)function.Parameters.Length);
                    foreach (var parameter in function.Parameters)
                        WriteType(writer, parameter);
                    WriteType(writer, function.Result);
                    break;
                default:
                    throw new NotSupportedException(type.GetType().Name);
            }
        }

        private static void WriteExtended(NibbleWriter writer, int tag)
        {
            writer.WriteNibble(IrFormat.TypeExtended);
            writer.WriteNibble(tag);
        }

        internal static void WriteGenerics(NibbleWriter writer, GenericParam[] generics)
        {
            writer.WriteUnsigned((ulong)generics.Length);
            foreach (var parameter in generics)
            {
                WriteName(writer, parameter.Name);
                WriteConstraint(writer, parameter.Constraint);
                writer.WriteUnsigned((ulong)parameter.Requirements.Length);
                foreach (var requirement in parameter.Requirements)
                {
                    WriteName(writer, requirement.Name);
                    writer.WriteUnsigned((ulong)requirement.Parameters.Length);
                    foreach (var type in requirement.Parameters)
                        WriteType(writer, type);
                    WriteType(writer, requirement.Result);
                }
            }
        }

        private static void WriteConstraint(NibbleWriter writer, TypeConstraint constraint)
        {
            switch (constraint)
            {
                case AnyConstraint:
                    writer.WriteNibble(IrFormat.ConstraintAny);
                    break;
                case GroupConstraint group:
                    writer.WriteNibble(IrFormat.ConstraintGroup);
                    writer.WriteNibble((int)group.Group);
                    break;
                case ExactConstraint exact:
                    writer.WriteNibble(IrFormat.ConstraintExact);
                    WriteType(writer, exact.Type);
                    break;
                case AndConstraint conj:
                    writer.WriteNibble(IrFormat.ConstraintAnd);
                    WriteConstraints(writer, conj.Parts);
                    break;
                case OrConstraint disj:
                    writer.WriteNibble(IrFormat.ConstraintOr);
                    WriteConstraints(writer, disj.Parts);
                    break;
                case NotConstraint neg:
                    writer.WriteNibble(IrFormat.ConstraintNot);
                    WriteConstraint(writer, neg.Inner);
                    break;
                case SizeAtMostConstraint size:
                    writer.WriteNibble(IrFormat.ConstraintSize);
                    writer.WriteUnsigned((ulong)size.Bits);
                    break;
                default:
                    throw new NotSupportedException(constraint.GetType().Name);
            }
        }

        private static void WriteConstraints(NibbleWriter writer, TypeConstraint[] parts)
        {
            writer.WriteUnsigned((ulong)parts.Length);
            foreach (var part in parts)
                WriteConstraint(writer, part);
        }

        private static void WriteLinkage(NibbleWriter writer, Linkage linkage, string? symbol)
        {
            writer.WriteNibble((int)linkage);
            if (linkage == Linkage.Import)
                WriteName(writer, symbol ?? "");
        }

        internal static void WriteName(NibbleWriter writer, string name)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
            writer.WriteUnsigned((ulong)bytes.Length);
            foreach (byte b in bytes)
                writer.WriteByte(b);
        }
    }
}
