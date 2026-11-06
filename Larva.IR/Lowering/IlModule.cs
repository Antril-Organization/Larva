using System.Numerics;
using Larva.IL;
using Larva.IR.Text;

namespace Larva.IR.Lowering
{
    /// <summary>
    /// A function lowered to IL. IR arguments <c>_1.._N</c> become <c>arg.0..arg.N-1</c>; the return place
    /// <c>_0</c> becomes <c>loc.0</c> and the remaining IR locals follow in order.
    /// </summary>
    public sealed record IlFunction(string Name, int ArgumentCount, int[] LocalTypes, OperationBlock Body)
    {
        /// <summary>Witnesses a shared generic function receives before its arguments (0 for ordinary functions).</summary>
        public int TypeParameterCount { get; init; }

        /// <summary>The GC domain of the function (<see cref="IrFunction.Domain"/>): what it allocates belongs to it.</summary>
        public int Domain { get; init; }

        /// <summary>Type tokens of the arguments (open in shared code, resolved against the witnesses).</summary>
        public int[] ArgumentTypes { get; init; } = [];

        /// <summary>Tracing GCs: every argument / local slot that may ever hold <c>gc</c> handles (conservative frame map).</summary>
        public GcRoot[] GcRoots { get; init; } = [];

        /// <summary>Tracing GCs: precise roots at each safepoint (calls and <c>sys.poll</c>), by operation index.</summary>
        public Safepoint[] Safepoints { get; init; } = [];

        /// <summary>Exception handlers, searched in order when an exception reaches an operation of this function.</summary>
        public ExceptionHandler[] Handlers { get; init; } = [];

        /// <summary>
        /// A call into C code that the VM makes itself (its body is what native code runs): an import (<c>Larva.Import</c>,
        /// library and symbol) or a <c>func*</c> call helper (<c>Larva.NativeCall</c>, the address is the first argument).
        /// </summary>
        public NativeCall? Native { get; init; }
    }

    /// <summary>What a function calls in C: <see cref="Library"/>'s <see cref="Symbol"/>, or (both null) its first argument.</summary>
    public sealed record NativeCall(string? Library, string? Symbol);

    /// <summary>
    /// Operations <see cref="Start"/>..<see cref="End"/> (exclusive) are protected: an exception thrown by one of them (a call, a
    /// <c>eh.throw</c> or <c>eh.rethrow</c>) whose type matches <see cref="CatchType"/> (a type token; <c>null</c> catches
    /// everything) clears the operand stack, pushes the exception and continues at <see cref="Handler"/>.
    /// </summary>
    public readonly record struct ExceptionHandler(int Start, int End, int? CatchType, int Handler)
    {
        public override string ToString() =>
            $"[#{Start}, #{End}) catch {(CatchType is int t ? $"type.{t}" : "_")} -> #{Handler}";
    }

    /// <summary>
    /// The slots a tracing GC must scan when it stops at operation <see cref="Operation"/>: only those live afterwards and
    /// initialized on some path. Values on the operand stack are not listed (the VM knows their types from the IL).
    /// </summary>
    public readonly record struct Safepoint(int Operation, GcRoot[] Roots)
    {
        public override string ToString() => $"#{Operation}: [{string.Join(", ", Roots)}]";
    }

    /// <summary>
    /// A stack slot a tracing GC must scan: <see cref="Offsets"/> are the byte offsets of handles inside the slot's value,
    /// or <c>null</c> when they come from a witness (generic value in shared code).
    /// </summary>
    public readonly record struct GcRoot(bool IsArgument, int Slot, int[]? Offsets)
    {
        public override string ToString() =>
            $"{(IsArgument ? "arg" : "loc")}.{Slot} {(Offsets is null ? "[witness]" : $"[{string.Join(", ", Offsets)}]")}";
    }

    /// <summary>
    /// Lowering output. IL <c>type.N</c> operands index <see cref="Types"/> (open types mention <c>T0</c>…);
    /// <c>fld.N</c> operands index <see cref="Fields"/>; <c>const.N</c> operands index <see cref="Constants"/>;
    /// <c>fn.N</c> operands index <see cref="Functions"/>. <see cref="Witnesses"/> holds the static witness tables of
    /// the concrete types passed to shared generic functions.
    /// </summary>
    public sealed record IlModule(IrType[] Types, FieldRef[] Fields, PoolConstant[] Constants, WitnessInfo[] Witnesses, IlFunction[] Functions)
    {
        /// <summary>Reflection metadata (structs, invocable methods, invoke stubs); see <see cref="Reflection.MetadataEncoding"/>.</summary>
        public Reflection.ModuleMetadata Metadata { get; init; } = new([], []);

        /// <summary>The GC mode the code was generated for; a runtime must provide these capabilities.</summary>
        public GcConfig Gc { get; init; } = GcConfig.Default;

        /// <summary>The GC domains of a linked program (<see cref="IrModule.Domains"/>); empty with one domain.</summary>
        public GcConfig[] Domains { get; init; } = [];

        /// <summary>Whether a tracing collector has to run: the program's GC traces, or any of its domains does.</summary>
        public bool Traces => (Gc.Capabilities & GcCapabilities.Tracing) != 0 || Domains.Any(d => (d.Capabilities & GcCapabilities.Tracing) != 0);

        /// <summary>Whether objects made by functions of <paramref name="domain"/> are reference counted.</summary>
        public bool Counts(int domain) => Domains.Length <= 1 || domain < 0 || domain >= Domains.Length
            ? (Gc.Capabilities & GcCapabilities.RefCounting) != 0
            : (Domains[domain].Capabilities & GcCapabilities.RefCounting) != 0;

        /// <summary>Function to run when the module is executed as a program, or -1.</summary>
        public int Entry { get; init; } = -1;

        /// <summary>Read-only static arrays (<c>lit.data N</c> pushes a reference to entry N).</summary>
        public StaticData[] Data { get; init; } = [];

        /// <summary>Global variables of plain data (<c>lit.global</c> / <c>lit.setglobal</c>).</summary>
        public GlobalDef[] Globals { get; init; } = [];

        public override string ToString()
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine($".gc     {Gc}");
            if (Entry >= 0)
                builder.AppendLine($".entry  fn.{Entry} ({Functions[Entry].Name})");
            for (int i = 0; i < Types.Length; i++)
                builder.AppendLine($".type   type.{i} = {Types[i]}");
            for (int i = 0; i < Fields.Length; i++)
                builder.AppendLine($".field  fld.{i} = {Fields[i].Owner}.{Fields[i].Field}");
            for (int i = 0; i < Constants.Length; i++)
                builder.AppendLine($".const  const.{i} = {Constants[i]}");
            foreach (var witness in Witnesses)
                builder.AppendLine($".witness type.{witness.Type} = {{ {witness.Layout} }}");
            for (int i = 0; i < Data.Length; i++)
                builder.AppendLine($".data   data.{i} = {Data[i]}");
            builder.Append(Metadata);
            foreach (var function in Functions)
            {
                builder.AppendLine();
                string witnesses = function.TypeParameterCount > 0 ? $"witnesses: {function.TypeParameterCount}, " : "";
                builder.AppendLine($".function {function.Name} ({witnesses}args: {function.ArgumentCount}, locals: [{string.Join(", ", function.LocalTypes.Select(t => $"type.{t}"))}])");
                if (function.GcRoots.Length > 0)
                    builder.AppendLine($".gcroots {string.Join(", ", function.GcRoots)}");
                foreach (var safepoint in function.Safepoints)
                    builder.AppendLine($".safepoint {safepoint}    ; {function.Body.Operations[safepoint.Operation]}");
                foreach (var handler in function.Handlers)
                    builder.AppendLine($".handler {handler}");
                builder.Append(function.Body);
            }
            return builder.ToString();
        }
    }

    /// <summary>Field <see cref="Field"/> of <see cref="Owner"/>; the owner may be an open generic struct in shared code.</summary>
    public readonly record struct FieldRef(StructType Owner, int Field);

    /// <summary>Static witness table of a concrete type (type token + layout).</summary>
    public readonly record struct WitnessInfo(int Type, TypeLayout Layout);

    /// <summary>A constant too wide for <c>ldc</c>, loaded with <c>ldk.wide const.N</c>. Same encoding as <see cref="ConstOperand"/>.</summary>
    public readonly record struct PoolConstant(BigInteger Bits, IrType Type)
    {
        public override string ToString() => IrPrinter.FormatConstant(new ConstOperand(Bits, Type));
    }

    /// <summary>Interns IR types, struct fields and wide constants into IL tokens.</summary>
    internal sealed class TokenTable
    {
        private readonly List<IrType> types = [];
        private readonly Dictionary<IrType, int> typeTokens = [];
        private readonly List<FieldRef> fields = [];
        private readonly Dictionary<FieldRef, int> fieldTokens = [];

        public int Type(IrType type)
        {
            if (!typeTokens.TryGetValue(type, out int token))
            {
                token = types.Count;
                types.Add(type);
                typeTokens.Add(type, token);
            }
            return token;
        }

        public int Field(StructType owner, int field)
        {
            FieldRef key = new(owner, field);
            if (!fieldTokens.TryGetValue(key, out int token))
            {
                token = fields.Count;
                fields.Add(key);
                fieldTokens.Add(key, token);
            }
            return token;
        }

        private readonly List<PoolConstant> constants = [];
        private readonly Dictionary<PoolConstant, int> constantTokens = [];

        public int Constant(BigInteger bits, IrType type)
        {
            PoolConstant key = new(bits, type);
            if (!constantTokens.TryGetValue(key, out int token))
            {
                token = constants.Count;
                constants.Add(key);
                constantTokens.Add(key, token);
            }
            return token;
        }

        private readonly List<WitnessInfo> witnesses = [];

        /// <summary>Type token for a witness; concrete types also get a static witness table.</summary>
        public int Witness(IrType type, IrModule module)
        {
            int token = Type(type);
            if (!type.IsGeneric() && witnesses.All(w => w.Type != token))
            {
                var layout = Layouts.Of(type, module)
                    ?? throw new InvalidOperationException($"`{type}` has no static layout and cannot be a type argument of shared code.");
                witnesses.Add(new WitnessInfo(token, layout));
            }
            return token;
        }

        public WitnessInfo[] Witnesses => [.. witnesses];
        public IrType[] Types => [.. types];
        public PoolConstant[] Constants => [.. constants];
        public FieldRef[] Fields => [.. fields];
    }
}
