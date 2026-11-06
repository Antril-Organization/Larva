namespace Larva.IL
{
    /// <summary>
    /// What an operand means. All operands are stored as nibble-varints (see
    /// <see cref="Encoding.NibbleWriter"/>); the kind decides signedness and how it is printed.
    /// </summary>
    public enum OperandKind : byte
    {
        /// <summary>Signed immediate, zigzag encoded. Printed as <c>42</c>.</summary>
        Immediate,
        /// <summary>Unsigned immediate (bit widths, trap codes, ids). Printed as <c>8</c>.</summary>
        Unsigned,
        /// <summary>Local slot index. Printed as <c>loc.1</c>.</summary>
        Local,
        /// <summary>Argument index. Printed as <c>arg.0</c>.</summary>
        Argument,
        /// <summary>Field token. Printed as <c>fld.3</c>.</summary>
        Field,
        /// <summary>Function token. Printed as <c>fn.2</c>.</summary>
        Function,
        /// <summary>Type token. Printed as <c>type.4</c>.</summary>
        Type,
        /// <summary>Index into the module's constant pool (values wider than 64 bits). Printed as <c>const.0</c>.</summary>
        Constant,
        /// <summary>
        /// Branch target. In memory it is the absolute operation index inside the block;
        /// on the wire it is the zigzag offset relative to the next operation, so short loops cost one nibble.
        /// </summary>
        Label,
        /// <summary>
        /// Variable-length list of labels (only valid as the last operand). On the wire: unsigned count,
        /// then each label as in <see cref="Label"/>.
        /// </summary>
        LabelTable,
    }

    public static class OperandKindExtensions
    {
        public static bool IsSigned(this OperandKind kind) => kind is OperandKind.Immediate or OperandKind.Label;

        public static string Prefix(this OperandKind kind) => kind switch
        {
            OperandKind.Local => "loc.",
            OperandKind.Argument => "arg.",
            OperandKind.Field => "fld.",
            OperandKind.Function => "fn.",
            OperandKind.Type => "type.",
            OperandKind.Constant => "const.",
            _ => "",
        };
    }
}
