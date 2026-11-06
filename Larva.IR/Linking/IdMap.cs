namespace Larva.IR.Linking
{
    /// <summary>
    /// Renumbers what IR refers to by index — structs, functions, globals and static data — in types, definitions and
    /// bodies. The linker uses it to move a module's items to their program-wide ids; trimming and splitting use it to
    /// pack a module after items are dropped (and, with mappings that record what they see, to find what is used).
    /// </summary>
    public sealed class IdMap(Func<int, int> structs, Func<int, int> functions, Func<int, int> globals, Func<int, int> data)
    {
        public IrType Type(IrType type) => type switch
        {
            StructType st => new StructType(structs(st.Id), [.. st.Arguments.Select(Type)]),
            ReferenceType r => r with { Pointee = Type(r.Pointee) },
            GcType g => new GcType(Type(g.Pointee)),
            BoxType b => new BoxType(Type(b.Pointee)),
            PointerType p => p with { Pointee = Type(p.Pointee) },
            ArrayType a => new ArrayType(Type(a.Element)),
            FixedArrayType f => f with { Element = Type(f.Element), SizeOf = f.SizeOf is null ? null : Type(f.SizeOf) },
            FunctionType f => new FunctionType([.. f.Parameters.Select(Type)], Type(f.Result)),
            OptionalType o => new OptionalType(Type(o.Inner)),
            _ => type,
        };

        public GenericParam[] Generics(GenericParam[] generics) =>
            [.. generics.Select(g => g with { Constraint = Constraint(g.Constraint), Requirements = [.. g.Requirements.Select(r => r.Map(Type))] })];

        public TypeConstraint Constraint(TypeConstraint constraint) => constraint switch
        {
            ExactConstraint e => new ExactConstraint(Type(e.Type)),
            AndConstraint a => new AndConstraint([.. a.Parts.Select(Constraint)]),
            OrConstraint o => new OrConstraint([.. o.Parts.Select(Constraint)]),
            NotConstraint n => new NotConstraint(Constraint(n.Inner)),
            _ => constraint,
        };

        public StructDef Struct(StructDef def) => def with
        {
            Fields = [.. def.Fields.Select(Type)],
            Generics = Generics(def.Generics),
            Base = def.Base is { } baseType ? (StructType)Type(baseType) : null,
            Attributes = Attributes(def.Attributes),
            FieldAttributes = [.. def.FieldAttributes.Select(Attributes)],
        };

        /// <summary>Attributes, with the types they name renumbered.</summary>
        public IrAttribute[] Attributes(IrAttribute[] attributes) =>
            attributes.Length == 0 ? attributes : [.. attributes.Select(a => a with
            {
                Arguments = [.. a.Arguments.Select(arg => arg is TypeArgument t ? new TypeArgument(Type(t.Type)) : arg)],
            })];

        public GlobalDef Global(GlobalDef global) => global with { Type = Type(global.Type) };

        public IrFunction Function(IrFunction function)
        {
            CatchClause[] Handlers(CatchClause[] clauses) => [.. clauses.Select(c => c with { Type = c.Type is null ? null : Type(c.Type) })];

            var blocks = function.Blocks.Select(block => new BasicBlock(
                [.. block.Statements.Select(statement => statement switch
                {
                    StoreGlobalStatement store => store with { Global = globals(store.Global), TypeArguments = [.. store.TypeArguments.Select(Type)] },
                    AssignStatement assign => assign with { Value = Value(assign.Value) },
                    _ => statement,
                })],
                block.Terminator switch
                {
                    CallTerminator call => call with
                    {
                        Function = call.Required is null && call.Callee is null ? functions(call.Function) : -1,
                        TypeArguments = [.. call.TypeArguments.Select(Type)],
                        Handlers = Handlers(call.Handlers),
                    },
                    ThrowTerminator @throw => @throw with { Handlers = Handlers(@throw.Handlers) },
                    ResumeTerminator resume => resume with { Handlers = Handlers(resume.Handlers) },
                    var other => other,
                })).ToArray();

            return function with
            {
                Locals = [.. function.Locals.Select(l => l with { Type = Type(l.Type) })],
                Blocks = blocks,
                Generics = Generics(function.Generics),
                Attributes = Attributes(function.Attributes),
            };
        }

        private Rvalue Value(Rvalue value) => value switch
        {
            CastRvalue cast => cast with { Type = Type(cast.Type) },
            SizeOfRvalue size => size with { Type = Type(size.Type) },
            ZeroRvalue zero => zero with { Type = Type(zero.Type) },
            ArrayRvalue array => array with { Element = Type(array.Element), Owner = array.Owner is { } owner ? (StructType)Type(owner) : null },
            StaticRvalue static_ => static_ with { Data = data(static_.Data) },
            GlobalRvalue global => global with { Global = globals(global.Global), TypeArguments = [.. global.TypeArguments.Select(Type)] },
            TypeIdRvalue typeId => typeId with { Type = Type(typeId.Type) },
            ImplementationRvalue implementation => new ImplementationRvalue(implementation.Requirement.Map(Type)),
            ClassIdRvalue classId => classId with { Class = Type(classId.Class) },
            InstancesRvalue instances => new InstancesRvalue(functions(instances.Function)),
            FunctionRefRvalue reference => new FunctionRefRvalue(functions(reference.Function), [.. reference.TypeArguments.Select(Type)]),
            HandleCastRvalue cast => cast with { Target = Type(cast.Target) },
            var other => other,
        };
    }
}
