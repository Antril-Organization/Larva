using Larva.IL.Encoding;

namespace Larva.IR.Encoding
{
    /// <summary>Inverse of <see cref="IrEncoder"/>.</summary>
    public static class IrDecoder
    {
        public static IrModule Decode(ReadOnlySpan<byte> bytes)
        {
            NibbleReader reader = new(bytes);
            return Decode(ref reader);
        }

        /// <param name="attributes">Whether the encoding has attributes (objects before version 3 do not).</param>
        public static IrModule Decode(ref NibbleReader reader, bool attributes = true)
        {
            string moduleName = ReadName(ref reader);
            var structs = new StructDef[ReadCount(ref reader)];
            for (int i = 0; i < structs.Length; i++)
            {
                string name = ReadName(ref reader);
                var generics = ReadGenerics(ref reader);
                int meta = reader.ReadNibble();
                string[] fieldNames = [];
                if ((meta & IrFormat.StructFieldNames) != 0)
                {
                    fieldNames = new string[ReadCount(ref reader)];
                    for (int f = 0; f < fieldNames.Length; f++)
                        fieldNames[f] = ReadName(ref reader);
                }
                bool mutable = (meta & IrFormat.StructMutable) != 0;
                var (structLinkage, structSymbol) = ReadLinkage(ref reader);
                var fields = new IrType[ReadCount(ref reader)];
                for (int f = 0; f < fields.Length; f++)
                    fields[f] = ReadType(ref reader);
                var baseStruct = reader.ReadNibble() != 0 ? (StructType)ReadType(ref reader) : null;
                IrAttribute[] typeAttributes = [];
                IrAttribute[][] fieldAttributes = [];
                if (attributes)
                {
                    typeAttributes = ReadAttributes(ref reader);
                    fieldAttributes = new IrAttribute[ReadCount(ref reader)][];
                    for (int f = 0; f < fieldAttributes.Length; f++)
                        fieldAttributes[f] = ReadAttributes(ref reader);
                }
                structs[i] = new StructDef(name, fields)
                {
                    Attributes = typeAttributes,
                    FieldAttributes = fieldAttributes,
                    Base = baseStruct,
                    Generics = generics,
                    FieldNames = fieldNames,
                    Reflect = IrFormat.DecodeReflect(meta),
                    Linkage = structLinkage,
                    Symbol = structSymbol,
                    Mutable = mutable,
                };
            }

            var functions = new IrFunction[ReadCount(ref reader)];
            for (int i = 0; i < functions.Length; i++)
                functions[i] = ReadFunction(ref reader, attributes);
            var data = new StaticData[ReadCount(ref reader)];
            for (int i = 0; i < data.Length; i++)
                data[i] = ReadStaticData(ref reader);
            var globals = new GlobalDef[ReadCount(ref reader)];
            for (int i = 0; i < globals.Length; i++)
                globals[i] = ReadGlobal(ref reader);
            var implementations = new Implementation[ReadCount(ref reader)];
            for (int i = 0; i < implementations.Length; i++)
            {
                string implementationName = ReadName(ref reader);
                implementations[i] = new Implementation(implementationName, ReadCount(ref reader));
            }
            return new IrModule(structs, functions) { Name = moduleName, Data = data, Implementations = implementations, Globals = globals };
        }

        private static ImplementationRvalue ReadImplementation(ref NibbleReader reader)
        {
            string name = ReadName(ref reader);
            var parameters = new IrType[ReadCount(ref reader)];
            for (int p = 0; p < parameters.Length; p++)
                parameters[p] = ReadType(ref reader);
            return new ImplementationRvalue(new Requirement(name, parameters, ReadType(ref reader)));
        }

        private static FunctionRefRvalue ReadFunctionRef(ref NibbleReader reader)
        {
            int function = ReadCount(ref reader);
            var typeArguments = new IrType[ReadCount(ref reader)];
            for (int t = 0; t < typeArguments.Length; t++)
                typeArguments[t] = ReadType(ref reader);
            return new FunctionRefRvalue(function, typeArguments);
        }

        private static HandleCastRvalue ReadHandleCast(ref NibbleReader reader)
        {
            var handle = ReadOperand(ref reader);
            return new HandleCastRvalue(handle, ReadType(ref reader));
        }

        private static IntrinsicRvalue ReadIntrinsic(ref NibbleReader reader)
        {
            string name = ReadName(ref reader);
            var arguments = new Operand[ReadCount(ref reader)];
            for (int a = 0; a < arguments.Length; a++)
                arguments[a] = ReadOperand(ref reader);
            return new IntrinsicRvalue(name, arguments);
        }

        private static GlobalRvalue ReadGenericGlobal(ref NibbleReader reader)
        {
            int global = ReadCount(ref reader);
            var arguments = new IrType[ReadCount(ref reader)];
            for (int a = 0; a < arguments.Length; a++)
                arguments[a] = ReadType(ref reader);
            return new GlobalRvalue(global) { TypeArguments = arguments };
        }

        internal static GlobalDef ReadGlobal(ref NibbleReader reader)
        {
            string name = ReadName(ref reader);
            var type = ReadType(ref reader);
            int flags = ReadCount(ref reader);
            var linkage = (Linkage)(flags >> 1 & 3);
            string? symbol = linkage == Linkage.Import ? ReadName(ref reader) : null;
            System.Numerics.BigInteger? initial = (flags & 1) != 0 ? reader.ReadSigned() : null;
            return new GlobalDef(name, type, initial) { GenericCount = flags >> 3, Linkage = linkage, Symbol = symbol };
        }

        internal static StaticData ReadStaticData(ref NibbleReader reader)
        {
            var element = ReadType(ref reader);
            var values = new System.Numerics.BigInteger[ReadCount(ref reader)];
            for (int v = 0; v < values.Length; v++)
                values[v] = reader.ReadSigned();
            return new StaticData(element, values);
        }

        private static IrAttribute[] ReadAttributes(ref NibbleReader reader)
        {
            var result = new IrAttribute[ReadCount(ref reader)];
            for (int a = 0; a < result.Length; a++)
            {
                string name = ReadName(ref reader);
                var arguments = new AttributeArgument[ReadCount(ref reader)];
                for (int i = 0; i < arguments.Length; i++)
                {
                    arguments[i] = reader.ReadNibble() switch
                    {
                        0 => ReadConstantArgument(ref reader),
                        1 => new TextArgument(ReadName(ref reader)),
                        2 => new TypeArgument(ReadType(ref reader)),
                        var kind => throw new InvalidDataException($"unknown attribute argument kind {kind}"),
                    };
                }
                result[a] = new IrAttribute(name, arguments);
            }
            return result;
        }

        private static ConstantArgument ReadConstantArgument(ref NibbleReader reader)
        {
            var type = ReadType(ref reader);
            return new ConstantArgument(new ConstOperand(reader.ReadSignedBig(), type));
        }

        private static IrFunction ReadFunction(ref NibbleReader reader, bool attributes)
        {
            string name = ReadName(ref reader);
            int functionFlags = reader.ReadNibble();
            bool isUnsafe = (functionFlags & IrFormat.FunctionUnsafe) != 0;
            var (linkage, symbol) = ReadLinkage(ref reader);
            var generics = ReadGenerics(ref reader);
            var regionParams = new string[ReadCount(ref reader)];
            for (int i = 0; i < regionParams.Length; i++)
                regionParams[i] = ReadName(ref reader);
            int argumentCount = ReadCount(ref reader);

            var locals = new LocalDecl[ReadCount(ref reader)];
            for (int i = 0; i < locals.Length; i++)
            {
                int flags = reader.ReadNibble();
                var type = ReadType(ref reader);
                string? localName = (flags & IrFormat.LocalNamed) != 0 ? ReadName(ref reader) : null;
                int? region = (flags & IrFormat.LocalRegion) != 0 ? ReadCount(ref reader) : null;
                locals[i] = new LocalDecl(type, localName, (flags & IrFormat.LocalMutable) != 0, region);
            }
            var functionAttributes = attributes ? ReadAttributes(ref reader) : [];
            int? callerRegion = null;
            if (functionAttributes.FirstOrDefault(a => a.Name == IrFunction.CallerRegionAttribute) is { Arguments: [ConstantArgument { Value: var marked }] })
            {
                callerRegion = (int)marked.Bits;
                functionAttributes = [.. functionAttributes.Where(a => a.Name != IrFunction.CallerRegionAttribute)];
            }

            var blocks = new BasicBlock[ReadCount(ref reader)];
            for (int b = 0; b < blocks.Length; b++)
            {
                var statements = new Statement[ReadCount(ref reader)];
                for (int s = 0; s < statements.Length; s++)
                    statements[s] = ReadStatement(ref reader);
                blocks[b] = new BasicBlock(statements, ReadTerminator(ref reader));
            }
            return new IrFunction(name, argumentCount, locals, blocks)
            {
                Generics = generics,
                Unsafe = isUnsafe,
                RegionParams = regionParams,
                Reflect = IrFormat.DecodeReflect(functionFlags),
                Linkage = linkage,
                Symbol = symbol,
                Attributes = functionAttributes,
                Mangled = (functionFlags & IrFormat.FunctionMangled) != 0,
                CallerRegion = callerRegion,
            };
        }

        private static Statement ReadStatement(ref NibbleReader reader)
        {
            var tag = ReadTag(ref reader);
            switch (tag)
            {
                case StatementTag.StorageLive:
                    return new StorageLiveStatement(ReadCount(ref reader));
                case StatementTag.StorageDead:
                    return new StorageDeadStatement(ReadCount(ref reader));
                case StatementTag.Nop:
                    return new NopStatement();
                case StatementTag.Free:
                    return new FreeStatement(ReadPlace(ref reader));
                case StatementTag.StoreGlobal:
                {
                    int global = ReadCount(ref reader);
                    return new StoreGlobalStatement(global, ReadOperand(ref reader));
                }
                case StatementTag.StoreGenericGlobal:
                {
                    int global = ReadCount(ref reader);
                    var arguments = new IrType[ReadCount(ref reader)];
                    for (int a = 0; a < arguments.Length; a++)
                        arguments[a] = ReadType(ref reader);
                    return new StoreGlobalStatement(global, ReadOperand(ref reader)) { TypeArguments = arguments };
                }
                case StatementTag.RegionEnter:
                    return new RegionEnterStatement(ReadCount(ref reader));
                case StatementTag.RegionExit:
                    return new RegionExitStatement(ReadCount(ref reader));
            }

            var target = ReadPlace(ref reader);
            Rvalue value = tag switch
            {
                StatementTag.AssignUse => new UseRvalue(ReadOperand(ref reader)),
                StatementTag.AssignBinary => ReadBinary(ref reader),
                StatementTag.AssignUnary => ReadUnary(ref reader),
                StatementTag.AssignRef => new RefRvalue(ReadPlace(ref reader), Mutable: false),
                StatementTag.AssignRefMut => new RefRvalue(ReadPlace(ref reader), Mutable: true),
                StatementTag.AssignCast => ReadCast(ref reader),
                StatementTag.AssignSizeOf => new SizeOfRvalue(ReadType(ref reader)),
                StatementTag.AssignAlloc => ReadAlloc(ref reader),
                StatementTag.AssignArray => ReadArray(ref reader),
                StatementTag.AssignLen => new LenRvalue(ReadPlace(ref reader)),
                StatementTag.AssignCaught => new CaughtRvalue(),
                StatementTag.AssignStatic => new StaticRvalue(ReadCount(ref reader)),
                StatementTag.AssignZero => new ZeroRvalue(ReadType(ref reader)),
                StatementTag.AssignFiller => new ZeroRvalue(ReadType(ref reader), Filler: true),
                StatementTag.AssignGlobal => new GlobalRvalue(ReadCount(ref reader)),
                StatementTag.AssignGenericGlobal => ReadGenericGlobal(ref reader),
                StatementTag.AssignTypeId => new TypeIdRvalue(ReadType(ref reader)),
                StatementTag.AssignImplementation => ReadImplementation(ref reader),
                StatementTag.AssignClassId => new ClassIdRvalue(ReadType(ref reader)),
                StatementTag.AssignClassIdEnd => new ClassIdRvalue(ReadType(ref reader), End: true),
                StatementTag.AssignIntrinsic => ReadIntrinsic(ref reader),
                StatementTag.AssignFunctionRef => ReadFunctionRef(ref reader),
                StatementTag.AssignInstances => new InstancesRvalue(ReadCount(ref reader)),
                StatementTag.AssignHandleCast => ReadHandleCast(ref reader),
                StatementTag.AssignWrap => new WrapRvalue(ReadOperand(ref reader)),
                StatementTag.AssignIsNull => new IsNullRvalue(ReadPlace(ref reader)),
                StatementTag.AssignUnwrap => new UnwrapRvalue(ReadOperand(ref reader)),
                _ => throw new InvalidDataException($"Unknown statement tag {(int)tag} at nibble {reader.Position}."),
            };
            return new AssignStatement(target, value);
        }

        private static BinaryRvalue ReadBinary(ref NibbleReader reader)
        {
            var op = (BinaryOp)reader.ReadNibble();
            var left = ReadOperand(ref reader);
            return new BinaryRvalue(op, left, ReadOperand(ref reader));
        }

        private static UnaryRvalue ReadUnary(ref NibbleReader reader)
        {
            var op = (UnaryOp)reader.ReadNibble();
            if (op > UnaryOp.Not)
                throw new InvalidDataException($"Unknown unary op {(int)op} at nibble {reader.Position}.");
            return new UnaryRvalue(op, ReadOperand(ref reader));
        }

        private static AllocRvalue ReadAlloc(ref NibbleReader reader)
        {
            int nibble = reader.ReadNibble();
            int kind = nibble & 7;
            if (kind > (int)AllocKind.Raw)
                throw new InvalidDataException($"Unknown allocation kind {kind} at nibble {reader.Position}.");
            var value = ReadOperand(ref reader);
            return new AllocRvalue((AllocKind)kind, value, (nibble & 8) != 0 ? ReadOperand(ref reader) : null);
        }

        private static ArrayRvalue ReadArray(ref NibbleReader reader)
        {
            int nibble = reader.ReadNibble();
            int kind = nibble & 3;
            if (kind > (int)AllocKind.Raw)
                throw new InvalidDataException($"Unknown allocation kind {kind} at nibble {reader.Position}.");
            var element = ReadType(ref reader);
            var owner = (nibble & 4) != 0 ? (StructType)ReadType(ref reader) : null;
            return new ArrayRvalue((AllocKind)kind, element, ReadOperand(ref reader)) { Filled = (nibble & 8) != 0, Owner = owner };
        }

        private static CastRvalue ReadCast(ref NibbleReader reader)
        {
            var operand = ReadOperand(ref reader);
            return new CastRvalue(operand, ReadType(ref reader));
        }

        private static Terminator ReadTerminator(ref NibbleReader reader)
        {
            var tag = (TerminatorTag)reader.ReadNibble();
            switch (tag)
            {
                case TerminatorTag.Goto:
                    return new GotoTerminator(ReadCount(ref reader));
                case TerminatorTag.If:
                {
                    var condition = ReadOperand(ref reader);
                    int then = ReadCount(ref reader);
                    return new IfTerminator(condition, then, ReadCount(ref reader));
                }
                case TerminatorTag.Switch:
                {
                    var value = ReadOperand(ref reader);
                    var targets = new int[ReadCount(ref reader)];
                    for (int i = 0; i < targets.Length; i++)
                        targets[i] = ReadCount(ref reader);
                    return new SwitchTerminator(value, targets, ReadCount(ref reader));
                }
                case TerminatorTag.Return:
                    return new ReturnTerminator();
                case TerminatorTag.Call:
                {
                    int code = ReadCount(ref reader);
                    int function = code - 2;
                    RequirementRef? required = code == 0 ? new RequirementRef(ReadCount(ref reader), ReadCount(ref reader)) : null;
                    var calleeOperand = code == 1 ? ReadOperand(ref reader) : null;
                    if (code < 2)
                        function = -1;
                    var typeArguments = new IrType[ReadCount(ref reader)];
                    for (int i = 0; i < typeArguments.Length; i++)
                        typeArguments[i] = ReadType(ref reader);
                    var arguments = new Operand[ReadCount(ref reader)];
                    for (int i = 0; i < arguments.Length; i++)
                        arguments[i] = ReadOperand(ref reader);
                    var destination = ReadPlace(ref reader);
                    int target = ReadCount(ref reader);
                    return new CallTerminator(function, arguments, destination, target)
                    {
                        Required = required,
                        Callee = calleeOperand,
                        TypeArguments = typeArguments,
                        Handlers = ReadHandlers(ref reader),
                    };
                }
                case TerminatorTag.Drop:
                {
                    var place = ReadPlace(ref reader);
                    return new DropTerminator(place, ReadCount(ref reader));
                }
                case TerminatorTag.Unreachable:
                    return new UnreachableTerminator();
                case TerminatorTag.Throw:
                {
                    var value = ReadOperand(ref reader);
                    return new ThrowTerminator(value) { Handlers = ReadHandlers(ref reader) };
                }
                case TerminatorTag.Resume:
                    return new ResumeTerminator { Handlers = ReadHandlers(ref reader) };
                default:
                    throw new InvalidDataException($"Unknown terminator tag {(int)tag} at nibble {reader.Position}.");
            }
        }

        private static (Linkage Linkage, string? Symbol) ReadLinkage(ref NibbleReader reader)
        {
            int value = reader.ReadNibble();
            if (value > (int)Linkage.LinkOnce)
                throw new InvalidDataException($"Unknown linkage {value} at nibble {reader.Position}.");
            var linkage = (Linkage)value;
            return (linkage, linkage == Linkage.Import ? ReadName(ref reader) : null);
        }

        private static StatementTag ReadTag(ref NibbleReader reader)
        {
            int value = 0, nibble;
            while ((nibble = reader.ReadNibble()) == IrFormat.StatementTagEscape)
                value += IrFormat.StatementTagEscape;
            return (StatementTag)(value + nibble);
        }

        private static CatchClause[] ReadHandlers(ref NibbleReader reader)
        {
            var handlers = new CatchClause[ReadCount(ref reader)];
            for (int i = 0; i < handlers.Length; i++)
            {
                IrType? type = reader.ReadNibble() != 0 ? ReadType(ref reader) : null;
                handlers[i] = new CatchClause(type, ReadCount(ref reader));
            }
            return handlers;
        }

        private static Operand ReadOperand(ref NibbleReader reader)
        {
            int tag = reader.ReadNibble();
            switch (tag)
            {
                case IrFormat.OperandCopy:
                    return new CopyOperand(ReadPlace(ref reader));
                case IrFormat.OperandMove:
                    return new MoveOperand(ReadPlace(ref reader));
                case IrFormat.OperandConst:
                    var type = ReadType(ref reader);
                    return new ConstOperand(type.IsUnit ? 0 : reader.ReadSignedBig(type.BitWidth ?? 0), type);
                default:
                    throw new InvalidDataException($"Unknown operand tag {tag} at nibble {reader.Position}.");
            }
        }

        private static Place ReadPlace(ref NibbleReader reader)
        {
            int local = ReadCount(ref reader);
            var projections = new Projection[ReadCount(ref reader)];
            for (int i = 0; i < projections.Length; i++)
            {
                int tag = reader.ReadNibble();
                projections[i] = tag switch
                {
                    IrFormat.ProjectionDeref => Projection.Deref,
                    IrFormat.ProjectionField => Projection.Field(ReadCount(ref reader)),
                    IrFormat.ProjectionIndex => Projection.Index(ReadCount(ref reader)),
                    _ => throw new InvalidDataException($"Unknown projection tag {tag} at nibble {reader.Position}."),
                };
            }
            return new Place(local, projections);
        }

        internal static IrType ReadType(ref NibbleReader reader)
        {
            int tag = reader.ReadNibble();
            return tag switch
            {
                IrFormat.TypeStruct => ReadStructType(ref reader),
                IrFormat.TypeRef => new ReferenceType(ReadType(ref reader), Mutable: false),
                IrFormat.TypeRefMut => new ReferenceType(ReadType(ref reader), Mutable: true),
                IrFormat.TypeExtended => ReadExtendedType(ref reader),
                <= (int)Primitive.F64 => new PrimitiveType((Primitive)tag),
                _ => throw new InvalidDataException($"Unknown type tag {tag} at nibble {reader.Position}."),
            };
        }

        private static IrType ReadExtendedType(ref NibbleReader reader)
        {
            int tag = reader.ReadNibble();
            switch (tag)
            {
                case IrFormat.ExtendedArray:
                    return new ArrayType(ReadType(ref reader));
                case IrFormat.ExtendedGc:
                    return new GcType(ReadType(ref reader));
                case IrFormat.ExtendedBox:
                    return new BoxType(ReadType(ref reader));
                case IrFormat.ExtendedPointer:
                    return new PointerType(ReadType(ref reader), Mutable: false);
                case IrFormat.ExtendedPointerMut:
                    return new PointerType(ReadType(ref reader), Mutable: true);
                case IrFormat.ExtendedGenericParam:
                    return new GenericParamType(ReadCount(ref reader));
                case IrFormat.ExtendedFixedArray:
                {
                    var element = ReadType(ref reader);
                    int count = ReadCount(ref reader);
                    var sizeOf = reader.ReadNibble() != 0 ? ReadType(ref reader) : null;
                    return new FixedArrayType(element, count, sizeOf);
                }
                case IrFormat.ExtendedOptional:
                    return new OptionalType(ReadType(ref reader));
                case IrFormat.ExtendedFunction:
                {
                    var parameters = new IrType[ReadCount(ref reader)];
                    for (int p = 0; p < parameters.Length; p++)
                        parameters[p] = ReadType(ref reader);
                    return new FunctionType(parameters, ReadType(ref reader));
                }
            }

            var kind = (Primitive)(tag + IrFormat.ExtendedPrimitiveBase);
            if (kind is < Primitive.F16 or > Primitive.U256)
                throw new InvalidDataException($"Unknown extended type tag {tag} at nibble {reader.Position}.");
            return new PrimitiveType(kind);
        }

        private static StructType ReadStructType(ref NibbleReader reader)
        {
            int id = ReadCount(ref reader);
            var arguments = new IrType[ReadCount(ref reader)];
            for (int i = 0; i < arguments.Length; i++)
                arguments[i] = ReadType(ref reader);
            return new StructType(id, arguments);
        }

        internal static GenericParam[] ReadGenerics(ref NibbleReader reader)
        {
            var generics = new GenericParam[ReadCount(ref reader)];
            for (int i = 0; i < generics.Length; i++)
            {
                // (see ReadImplementation for the same layout of one requirement)
                string name = ReadName(ref reader);
                var constraint = ReadConstraint(ref reader);
                var requirements = new Requirement[ReadCount(ref reader)];
                for (int r = 0; r < requirements.Length; r++)
                {
                    string requirementName = ReadName(ref reader);
                    var parameters = new IrType[ReadCount(ref reader)];
                    for (int p = 0; p < parameters.Length; p++)
                        parameters[p] = ReadType(ref reader);
                    requirements[r] = new Requirement(requirementName, parameters, ReadType(ref reader));
                }
                generics[i] = new GenericParam(name, constraint) { Requirements = requirements };
            }
            return generics;
        }

        private static TypeConstraint ReadConstraint(ref NibbleReader reader)
        {
            int tag = reader.ReadNibble();
            switch (tag)
            {
                case IrFormat.ConstraintAny:
                    return TypeConstraint.Any;
                case IrFormat.ConstraintGroup:
                    int group = reader.ReadNibble();
                    if (group > (int)TypeGroup.Unborrowed)
                        throw new InvalidDataException($"Unknown type group {group} at nibble {reader.Position}.");
                    return new GroupConstraint((TypeGroup)group);
                case IrFormat.ConstraintExact:
                    return new ExactConstraint(ReadType(ref reader));
                case IrFormat.ConstraintAnd:
                    return new AndConstraint(ReadConstraints(ref reader));
                case IrFormat.ConstraintOr:
                    return new OrConstraint(ReadConstraints(ref reader));
                case IrFormat.ConstraintNot:
                    return new NotConstraint(ReadConstraint(ref reader));
                case IrFormat.ConstraintSize:
                    return new SizeAtMostConstraint(ReadCount(ref reader));
                default:
                    throw new InvalidDataException($"Unknown constraint tag {tag} at nibble {reader.Position}.");
            }
        }

        private static TypeConstraint[] ReadConstraints(ref NibbleReader reader)
        {
            var parts = new TypeConstraint[ReadCount(ref reader)];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = ReadConstraint(ref reader);
            return parts;
        }

        internal static string ReadName(ref NibbleReader reader)
        {
            var bytes = new byte[ReadCount(ref reader)];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = reader.ReadByte();
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Reads an unsigned varint used as a count or index, rejecting values that cannot be valid.</summary>
        internal static int ReadCount(ref NibbleReader reader)
        {
            ulong value = reader.ReadUnsigned();
            if (value > (ulong)reader.Length)
                throw new InvalidDataException($"Value {value} at nibble {reader.Position} exceeds stream size.");
            return (int)value;
        }
    }
}
