namespace Larva.IR
{
    /// <summary>
    /// Incremental construction of an <see cref="IrFunction"/>. Declare arguments before any other local,
    /// open blocks with <see cref="NewBlock"/>, select one with <see cref="At"/>, append statements and
    /// close it with a terminator.
    /// </summary>
    public sealed class FunctionBuilder
    {
        private sealed class PendingBlock
        {
            public readonly List<Statement> Statements = [];
            public Terminator? Terminator;
        }

        private readonly string name;
        private readonly List<LocalDecl> locals;
        private readonly List<PendingBlock> blocks = [];
        private readonly List<GenericParam> generics = [];
        private readonly List<string> regionParams = [];
        private int argumentCount;
        private int current;

        public FunctionBuilder(string name, IrType returnType)
        {
            this.name = name;
            locals = [new LocalDecl(returnType, "return", Mutable: true)];
            current = NewBlock();
        }

        /// <summary>The return place, <c>_0</c>.</summary>
        public static Place ReturnPlace => new(0);

        public int CurrentBlock => current;

        /// <summary>Marks the function <c>unsafe fn</c>.</summary>
        public bool Unsafe { get; set; }

        /// <summary><c>#[reflect(...)]</c> on the function.</summary>
        public ReflectionLevel? Reflect { get; set; }

        public Linkage Linkage { get; set; }

        /// <summary>
        /// Makes this a declaration of <paramref name="symbol"/> (<c>module::name</c>) exported by another module: only the
        /// signature (arguments, return type, generics, regions) is kept.
        /// </summary>
        public FunctionBuilder ImportFrom(string symbol)
        {
            Linkage = Linkage.Import;
            importSymbol = symbol;
            return this;
        }

        private string? importSymbol;

        /// <summary>Declares a generic parameter and returns the type that refers to it in this function.</summary>
        public IrType Generic(string name, TypeConstraint? constraint = null)
        {
            generics.Add(new GenericParam(name, constraint ?? TypeConstraint.Any));
            return new GenericParamType(generics.Count - 1);
        }

        public int GenericCount => generics.Count;

        /// <summary>Adds a requirement to generic parameter <paramref name="param"/>; returns its index for <see cref="CallRequired"/>.</summary>
        public int Require(int param, Requirement requirement)
        {
            generics[param] = generics[param] with { Requirements = [.. generics[param].Requirements, requirement] };
            return generics[param].Requirements.Length - 1;
        }

        /// <summary>Declares a region parameter (<c>'a</c>) and returns its index for <see cref="Argument"/> / <see cref="ReturnsIn"/>.</summary>
        /// <summary>See <see cref="IrFunction.CallerRegion"/>.</summary>
        public int? CallerRegion { get; set; }

        public int RegionParam(string name)
        {
            regionParams.Add(name);
            return regionParams.Count - 1;
        }

        /// <param name="region">Region parameter the argument belongs to; <c>null</c> keeps it private to the call.</param>
        public int Argument(IrType type, string? name = null, bool mutable = false, int? region = null)
        {
            if (locals.Count != argumentCount + 1)
                throw new InvalidOperationException("Arguments must be declared before other locals.");
            argumentCount++;
            return AddLocal(new LocalDecl(type, name, mutable, region));
        }

        /// <summary>Declares that the result may contain values from region parameter <paramref name="region"/>.</summary>
        public FunctionBuilder ReturnsIn(int region)
        {
            locals[0] = locals[0] with { Region = region };
            return this;
        }

        public int Local(IrType type, string? name = null, bool mutable = false) => AddLocal(new LocalDecl(type, name, mutable));

        /// <summary>Unnamed temporary.</summary>
        public int Temp(IrType type) => AddLocal(new LocalDecl(type));

        /// <summary>The type of a place over the locals declared so far.</summary>
        public IrType TypeOf(Place place, IrModule module) => new IrFunction(name, argumentCount, [.. locals], []).TypeOf(place, module);

        public int NewBlock()
        {
            blocks.Add(new PendingBlock());
            return blocks.Count - 1;
        }

        public FunctionBuilder At(int block)
        {
            current = block;
            return this;
        }

        public FunctionBuilder Assign(Place target, Rvalue value) => Append(new AssignStatement(target, value));
        public FunctionBuilder StorageLive(int local) => Append(new StorageLiveStatement(local));
        public FunctionBuilder StorageDead(int local) => Append(new StorageDeadStatement(local));
        public FunctionBuilder Free(Place pointer) => Append(new FreeStatement(pointer));
        public FunctionBuilder StoreGlobal(int global, Operand value) => Append(new StoreGlobalStatement(global, value));
        public FunctionBuilder StoreGlobal(int global, IrType[] typeArguments, Operand value) =>
            Append(new StoreGlobalStatement(global, value) { TypeArguments = typeArguments });
        public FunctionBuilder RegionEnter(int region) => Append(new RegionEnterStatement(region));
        public FunctionBuilder RegionExit(int region) => Append(new RegionExitStatement(region));

        public void Goto(int target) => Terminate(new GotoTerminator(target));
        public void If(Operand condition, int then, int @else) => Terminate(new IfTerminator(condition, then, @else));
        public void Switch(Operand value, int[] targets, int otherwise) => Terminate(new SwitchTerminator(value, targets, otherwise));
        public void Return() => Terminate(new ReturnTerminator());
        public void Call(int function, Operand[] arguments, Place destination, int target) =>
            Terminate(new CallTerminator(function, arguments, destination, target));
        public void Call(int function, IrType[] typeArguments, Operand[] arguments, Place destination, int target) =>
            Terminate(new CallTerminator(function, arguments, destination, target) { TypeArguments = typeArguments });
        public void Drop(Place place, int target) => Terminate(new DropTerminator(place, target));
        public void Unreachable() => Terminate(new UnreachableTerminator());

        /// <summary>A call whose exceptions may be caught by <paramref name="handlers"/>.</summary>
        public void Call(int function, IrType[] typeArguments, Operand[] arguments, Place destination, int target, CatchClause[] handlers) =>
            Terminate(new CallTerminator(function, arguments, destination, target) { TypeArguments = typeArguments, Handlers = handlers });

        /// <summary><c>destination = require T.name(args)</c>: requirement <paramref name="index"/> of generic parameter <paramref name="param"/>.</summary>
        public void CallRequired(int param, int index, Operand[] arguments, Place destination, int target, params CatchClause[] handlers) =>
            Terminate(new CallTerminator(-1, arguments, destination, target) { Required = new RequirementRef(param, index), Handlers = handlers });

        /// <summary><c>destination = call callee(args)</c> through a function address.</summary>
        public void CallIndirect(Operand callee, Operand[] arguments, Place destination, int target, params CatchClause[] handlers) =>
            Terminate(new CallTerminator(-1, arguments, destination, target) { Callee = callee, Handlers = handlers });

        public void Throw(Operand value, params CatchClause[] handlers) => Terminate(new ThrowTerminator(value) { Handlers = handlers });

        public void Resume(params CatchClause[] handlers) => Terminate(new ResumeTerminator { Handlers = handlers });

        public IrFunction Build()
        {
            if (Linkage == Linkage.Import)
                return new IrFunction(name, argumentCount, [.. locals], [])
                {
                    Generics = [.. generics],
                    Unsafe = Unsafe,
                    RegionParams = [.. regionParams],
                    Reflect = Reflect,
                    Linkage = Linkage.Import,
                    CallerRegion = CallerRegion,
                    Symbol = importSymbol,
                };

            var built = new BasicBlock[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
            {
                var terminator = blocks[i].Terminator ?? throw new InvalidOperationException($"{name}: bb{i} has no terminator.");
                foreach (var successor in terminator.Successors)
                {
                    if (successor < 0 || successor >= blocks.Count)
                        throw new InvalidOperationException($"{name}: bb{i} jumps to missing bb{successor}.");
                }
                built[i] = new BasicBlock([.. blocks[i].Statements], terminator);
            }
            return new IrFunction(name, argumentCount, [.. locals], built)
            {
                Generics = [.. generics],
                Unsafe = Unsafe,
                RegionParams = [.. regionParams],
                Reflect = Reflect,
                Linkage = Linkage,
                CallerRegion = CallerRegion,
                Positions = positions,
            };
        }

        private int AddLocal(LocalDecl local)
        {
            locals.Add(local);
            return locals.Count - 1;
        }

        /// <summary>The source the statements appended from now on come from (null: unknown); see <see cref="IrFunction.Positions"/>.</summary>
        public SourcePosition? Position { get; set; }

        private Dictionary<(int, int), SourcePosition>? positions;

        private void Mark(int index)
        {
            if (Position is { } position)
                (positions ??= [])[(current, index)] = position;
        }

        private FunctionBuilder Append(Statement statement)
        {
            var block = blocks[current];
            if (block.Terminator is not null)
                throw new InvalidOperationException($"{name}: bb{current} is already terminated.");
            Mark(block.Statements.Count);
            block.Statements.Add(statement);
            return this;
        }

        private void Terminate(Terminator terminator)
        {
            var block = blocks[current];
            if (block.Terminator is not null)
                throw new InvalidOperationException($"{name}: bb{current} is already terminated.");
            Mark(block.Statements.Count);
            block.Terminator = terminator;
        }
    }
}
