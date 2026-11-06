namespace Larva.IR
{
    /// <summary>
    /// An attribute on a declaration (such as <c>[Name(args)]</c>): the attribute's full name and its arguments, kept for
    /// reflection and for tools reading compiled modules. Code generation ignores them.
    /// </summary>
    public sealed record IrAttribute(string Name, AttributeArgument[] Arguments)
    {
        public override string ToString() => $"[{Name}{(Arguments.Length > 0 ? $"({string.Join(", ", Arguments.Select(a => a.ToString()))})" : "")}]";
    }

    /// <summary>An attribute argument: a constant, a string or a type.</summary>
    public abstract record AttributeArgument;

    /// <summary>A numeric, bool or enum constant (enums as their base value).</summary>
    public sealed record ConstantArgument(ConstOperand Value) : AttributeArgument
    {
        public override string ToString() => Text.IrPrinter.FormatConstant(Value);
    }

    public sealed record TextArgument(string Value) : AttributeArgument
    {
        public override string ToString() => $"\"{Value}\"";
    }

    public sealed record TypeArgument(IrType Type) : AttributeArgument
    {
        public override string ToString() => $"typeof({Type})";
    }
}
