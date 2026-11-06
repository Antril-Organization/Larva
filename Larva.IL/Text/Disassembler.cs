using System.Text;
using Larva.IL.Encoding;

namespace Larva.IL.Text
{
    public sealed record DisassemblyOptions
    {
        public static readonly DisassemblyOptions Default = new();

        /// <summary>Show the nibble offset of each operation inside the encoded block.</summary>
        public bool ShowOffsets { get; init; } = true;

        /// <summary>Show the encoded nibbles (one hex digit per nibble) of each operation.</summary>
        public bool ShowEncoding { get; init; } = true;

        /// <summary>Append a size summary to the block header.</summary>
        public bool ShowSummary { get; init; } = true;
    }

    /// <summary>
    /// Renders IL as assembly text:
    /// <code>
    /// loop:                           ; 6 ops · 15 nibbles · 8 bytes
    ///   L0:
    ///     0001  01      ldloc     loc.1
    ///     0003  9B      jmp       L0
    /// </code>
    /// </summary>
    public static class Disassembler
    {
        private const int MnemonicWidth = 14;

        public static string Disassemble(OperationBlock block, DisassemblyOptions? options = null)
        {
            options ??= DisassemblyOptions.Default;
            var labels = CollectLabels(block);

            // Encode once to get per-operation offsets and bytes.
            NibbleWriter writer = new();
            writer.WriteUnsigned((ulong)block.Operations.Length);
            var starts = new int[block.Operations.Length + 1];
            for (int i = 0; i < block.Operations.Length; i++)
            {
                starts[i] = writer.Length;
                IlEncoder.Encode(writer, block.Operations[i], i);
            }
            starts[^1] = writer.Length;

            string hex = writer.ToHex();
            int encodingWidth = Math.Max(4, MaxWidth(starts));

            StringBuilder builder = new();
            builder.Append(block.BlockId).Append(':');
            if (options.ShowSummary)
            {
                builder.Append(' ', Math.Max(1, 32 - block.BlockId.Length - 1));
                builder.Append($"; {block.Operations.Length} ops · {writer.Length} nibbles · {(writer.Length + 1) / 2} bytes");
            }
            builder.AppendLine();

            string LabelName(long target) => labels.TryGetValue(target, out var name) ? name : $"@{target}";

            for (int i = 0; i <= block.Operations.Length; i++)
            {
                if (labels.TryGetValue(i, out var label))
                    builder.Append("  ").Append(label).AppendLine(":");
                if (i == block.Operations.Length)
                    break;

                builder.Append("    ");
                if (options.ShowOffsets)
                    builder.Append($"{starts[i]:X4}  ");
                if (options.ShowEncoding)
                    builder.Append(hex[starts[i]..starts[i + 1]].PadRight(encodingWidth)).Append("  ");
                builder.AppendLine(Format(block.Operations[i], LabelName));
            }
            return builder.ToString();
        }

        /// <summary>Formats one operation as <c>mnemonic operand, operand</c>.</summary>
        public static string Format(Operation operation, Func<long, string> labelName)
        {
            string mnemonic = operation.Mnemonic;
            if (operation.Operands.Length == 0 && !operation.HasLabelTable)
                return mnemonic;

            var operands = new string[operation.Operands.Length];
            for (int i = 0; i < operands.Length; i++)
                operands[i] = FormatOperand(operation.OperandKindAt(i), operation.Operands[i], labelName);

            string list = string.Join(", ", operands);
            if (operation.HasLabelTable)
            {
                int fixedCount = operation.Shape.Length - 1;
                list = string.Join(", ", operands[..fixedCount].Append($"[{string.Join(", ", operands[fixedCount..])}]"));
            }
            return $"{mnemonic.PadRight(MnemonicWidth - 1)} {list}";
        }

        private static string FormatOperand(OperandKind kind, long value, Func<long, string> labelName) =>
            kind == OperandKind.Label ? labelName(value) : kind.Prefix() + value;

        private static Dictionary<long, string> CollectLabels(OperationBlock block)
        {
            SortedSet<long> targets = [];
            foreach (var operation in block.Operations)
            {
                for (int i = 0; i < operation.Operands.Length; i++)
                {
                    long target = operation.Operands[i];
                    if (operation.OperandKindAt(i) == OperandKind.Label && target >= 0 && target <= block.Operations.Length)
                        targets.Add(target);
                }
            }

            Dictionary<long, string> labels = [];
            foreach (var target in targets)
                labels[target] = $"L{labels.Count}";
            return labels;
        }

        private static int MaxWidth(int[] starts)
        {
            int max = 0;
            for (int i = 0; i < starts.Length - 1; i++)
                max = Math.Max(max, starts[i + 1] - starts[i]);
            return max;
        }
    }
}
