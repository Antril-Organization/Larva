namespace Larva.X64
{
    public enum Reg : byte { Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi, R8, R9, R10, R11, R12, R13, R14, R15 }

    public enum Xmm : byte { X0, X1, X2, X3, X4, X5 }

    /// <summary>A memory operand <c>[base + displacement]</c>.</summary>
    public readonly record struct Mem(Reg Base, int Displacement)
    {
        public static Mem At(Reg reg, int displacement = 0) => new(reg, displacement);
    }

    /// <summary>Condition codes, as the low nibble of <c>Jcc</c> / <c>SETcc</c>.</summary>
    public enum Cond : byte
    {
        O = 0x0, NO = 0x1, B = 0x2, AE = 0x3, E = 0x4, NE = 0x5, BE = 0x6, A = 0x7,
        S = 0x8, NS = 0x9, P = 0xA, NP = 0xB, L = 0xC, GE = 0xD, LE = 0xE, G = 0xF,
    }

    public sealed class Label
    {
        internal int Position = -1;
        internal readonly List<int> Fixups = [];
        public bool IsBound => Position >= 0;
    }

    public enum RelocationKind : byte { Rel32, Addr64, Addr32NB }

    /// <summary>A reference to a symbol at <see cref="Offset"/> (the 4- or 8-byte field to patch).</summary>
    public readonly record struct Relocation(int Offset, string Symbol, RelocationKind Kind);

    /// <summary>
    /// An x86-64 machine-code encoder for the subset the direct back end emits: integer moves and arithmetic on 64-bit
    /// registers, sized loads and stores, SSE scalar floats, branches to labels and calls to symbols (relocated later).
    /// </summary>
    public sealed class Assembler
    {
        private readonly List<byte> code = [];

        public List<Relocation> Relocations { get; } = [];
        public int Position => code.Count;
        public byte[] ToArray() => [.. code];

        /// <summary>Adds another assembler's code at the end, with its relocations moved along.</summary>
        public void Append(Assembler other)
        {
            int at = code.Count;
            code.AddRange(other.code);
            foreach (var relocation in other.Relocations)
                Relocations.Add(relocation with { Offset = relocation.Offset + at });
        }

        // ── Raw encoding ───────────────────────────────────────

        private void Byte(int value) => code.Add((byte)value);

        private void Int32(int value)
        {
            for (int i = 0; i < 4; i++)
                code.Add((byte)(value >> (8 * i)));
        }

        private void Int64(long value)
        {
            for (int i = 0; i < 8; i++)
                code.Add((byte)(value >> (8 * i)));
        }

        public void PatchInt32(int at, int value) => Patch32(at, value);

        private void Patch32(int at, int value)
        {
            for (int i = 0; i < 4; i++)
                code[at + i] = (byte)(value >> (8 * i));
        }

        private static int Low(int reg) => reg & 7;
        private static bool High(int reg) => reg >= 8;

        /// <summary>REX prefix; emitted when any bit is set or <paramref name="force"/> (byte registers past <c>bl</c>).</summary>
        private void Rex(bool w, int reg, int rm, bool force = false)
        {
            int rex = 0x40 | (w ? 8 : 0) | (High(reg) ? 4 : 0) | (High(rm) ? 1 : 0);
            if (rex != 0x40 || force)
                Byte(rex);
        }

        private void ModRegister(int reg, int rm) => Byte(0xC0 | Low(reg) << 3 | Low(rm));

        private void ModMemory(int reg, Mem memory)
        {
            int @base = (int)memory.Base;
            int disp = memory.Displacement;
            bool small = disp >= -128 && disp <= 127;
            Byte((small ? 0x40 : 0x80) | Low(reg) << 3 | Low(@base));
            if (Low(@base) == 4)
                Byte(0x24); // SIB: base = rsp / r12, no index
            if (small)
                Byte(disp);
            else
                Int32(disp);
        }

        /// <summary><c>op reg, r/m</c> with a register r/m.</summary>
        private void RR(bool w, byte[] opcode, int reg, int rm)
        {
            Rex(w, reg, rm);
            foreach (var b in opcode)
                Byte(b);
            ModRegister(reg, rm);
        }

        /// <summary><c>op reg, [mem]</c>, with an optional mandatory prefix (66 / F2 / F3) before REX.</summary>
        private void RM(bool w, byte[] opcode, int reg, Mem memory, int prefix = -1, bool forceRex = false)
        {
            if (prefix >= 0)
                Byte(prefix);
            Rex(w, reg, (int)memory.Base, forceRex);
            foreach (var b in opcode)
                Byte(b);
            ModMemory(reg, memory);
        }

        // ── Moves ──────────────────────────────────────────────

        public void Mov(Reg dst, Reg src)
        {
            if (dst != src)
                RR(true, [0x89], (int)src, (int)dst);
        }

        public void MovImm(Reg dst, long value)
        {
            if (value == 0)
            {
                RR(false, [0x31], (int)dst, (int)dst); // xor r32, r32 (zero-extends)
            }
            else if (value is >= int.MinValue and <= int.MaxValue)
            {
                Rex(true, 0, (int)dst);
                Byte(0xC7);
                ModRegister(0, (int)dst);
                Int32((int)value);
            }
            else
            {
                Rex(true, 0, (int)dst);
                Byte(0xB8 + Low((int)dst));
                Int64(value);
            }
        }

        /// <summary>Loads <paramref name="size"/> bytes into a 64-bit register, sign- or zero-extended.</summary>
        public void Load(Reg dst, Mem src, int size, bool signed)
        {
            int d = (int)dst;
            switch (size, signed)
            {
                case (8, _): RM(true, [0x8B], d, src); break;
                case (4, true): RM(true, [0x63], d, src); break;
                case (4, false): RM(false, [0x8B], d, src); break;
                case (2, true): RM(true, [0x0F, 0xBF], d, src); break;
                case (2, false): RM(false, [0x0F, 0xB7], d, src); break;
                case (1, true): RM(true, [0x0F, 0xBE], d, src); break;
                case (1, false): RM(false, [0x0F, 0xB6], d, src); break;
                default: throw new ArgumentException($"load of {size} bytes");
            }
        }

        /// <summary>Stores the low <paramref name="size"/> bytes of a register.</summary>
        public void Store(Mem dst, Reg src, int size)
        {
            int s = (int)src;
            switch (size)
            {
                case 8: RM(true, [0x89], s, dst); break;
                case 4: RM(false, [0x89], s, dst); break;
                case 2: RM(false, [0x89], s, dst, prefix: 0x66); break;
                case 1: RM(false, [0x88], s, dst, forceRex: s is >= 4 and < 8); break;
                default: throw new ArgumentException($"store of {size} bytes");
            }
        }

        public void Lea(Reg dst, Mem src) => RM(true, [0x8D], (int)dst, src);

        /// <summary><c>mov byte [mem], imm8</c>.</summary>
        public void StoreByte(Mem dst, byte value)
        {
            RM(false, [0xC6], 0, dst);
            Byte(value);
        }

        /// <summary><c>cmp byte [mem], 0</c>.</summary>
        public void TestByte(Mem src)
        {
            RM(false, [0x80], 7, src);
            Byte(0);
        }

        // ── Integer arithmetic ─────────────────────────────────

        public void Add(Reg dst, Reg src) => RR(true, [0x01], (int)src, (int)dst);
        public void Sub(Reg dst, Reg src) => RR(true, [0x29], (int)src, (int)dst);
        public void Adc(Reg dst, Reg src) => RR(true, [0x11], (int)src, (int)dst);
        public void Sbb(Reg dst, Reg src) => RR(true, [0x19], (int)src, (int)dst);
        public void And(Reg dst, Reg src) => RR(true, [0x21], (int)src, (int)dst);
        public void Or(Reg dst, Reg src) => RR(true, [0x09], (int)src, (int)dst);
        public void Xor(Reg dst, Reg src) => RR(true, [0x31], (int)src, (int)dst);
        public void Cmp(Reg left, Reg right) => RR(true, [0x39], (int)right, (int)left);
        public void Test(Reg left, Reg right) => RR(true, [0x85], (int)right, (int)left);
        public void Imul(Reg dst, Reg src) => RR(true, [0x0F, 0xAF], (int)dst, (int)src);

        private void AluImm(int digit, Reg dst, int value)
        {
            Rex(true, 0, (int)dst);
            if (value is >= -128 and <= 127)
            {
                Byte(0x83);
                ModRegister(digit, (int)dst);
                Byte(value);
            }
            else
            {
                Byte(0x81);
                ModRegister(digit, (int)dst);
                Int32(value);
            }
        }

        public void AddImm(Reg dst, int value) { if (value != 0) AluImm(0, dst, value); }
        public void SubImm(Reg dst, int value) => AluImm(5, dst, value);
        public void AndImm(Reg dst, int value) => AluImm(4, dst, value);
        public void CmpImm(Reg dst, int value) => AluImm(7, dst, value);

        /// <summary><c>imul dst, src, imm32</c>.</summary>
        public void ImulImm(Reg dst, Reg src, int value)
        {
            Rex(true, (int)dst, (int)src);
            Byte(0x69);
            ModRegister((int)dst, (int)src);
            Int32(value);
        }

        private void Group3(int digit, Reg reg)
        {
            Rex(true, 0, (int)reg);
            Byte(0xF7);
            ModRegister(digit, (int)reg);
        }

        public void Not(Reg reg) => Group3(2, reg);
        public void Neg(Reg reg) => Group3(3, reg);
        public void Div(Reg divisor) => Group3(6, divisor);

        /// <summary><c>mul r64</c>: rdx:rax = rax · r (unsigned).</summary>
        public void Mul(Reg factor) => Group3(4, factor);
        public void Idiv(Reg divisor) => Group3(7, divisor);

        /// <summary><c>cqo</c>: sign-extends rax into rdx.</summary>
        public void Cqo() { Byte(0x48); Byte(0x99); }

        private void ShiftCl(int digit, Reg reg)
        {
            Rex(true, 0, (int)reg);
            Byte(0xD3);
            ModRegister(digit, (int)reg);
        }

        public void ShlCl(Reg reg) => ShiftCl(4, reg);
        public void ShrCl(Reg reg) => ShiftCl(5, reg);
        public void SarCl(Reg reg) => ShiftCl(7, reg);

        /// <summary><c>shl/shr/sar reg, imm8</c>.</summary>
        public void ShiftImm(int digit, Reg reg, int amount)
        {
            Rex(true, 0, (int)reg);
            Byte(0xC1);
            ModRegister(digit, (int)reg);
            Byte(amount);
        }

        /// <summary>Sign- or zero-extends the low <paramref name="size"/> bytes of a register in place.</summary>
        public void Extend(Reg reg, int size, bool signed)
        {
            int r = (int)reg;
            switch (size, signed)
            {
                case (8, _): break;
                case (4, true): RR(true, [0x63], r, r); break;          // movsxd
                case (4, false): RR(false, [0x89], r, r); break;        // mov r32, r32
                case (2, true): RR(true, [0x0F, 0xBF], r, r); break;
                case (2, false): RR(false, [0x0F, 0xB7], r, r); break;
                case (1, true): Rex(true, r, r); Byte(0x0F); Byte(0xBE); ModRegister(r, r); break;
                case (1, false): Rex(false, r, r, force: r is >= 4 and < 8); Byte(0x0F); Byte(0xB6); ModRegister(r, r); break;
            }
        }

        /// <summary><c>setcc al</c>; <c>movzx eax, al</c>.</summary>
        public void SetCondition(Cond condition, Reg dst)
        {
            int d = (int)dst;
            Rex(false, 0, d, force: d is >= 4 and < 8);
            Byte(0x0F);
            Byte(0x90 | (int)condition);
            ModRegister(0, d);
            Extend(dst, 1, false);
        }

        // ── Control flow ───────────────────────────────────────

        public void Bind(Label label)
        {
            label.Position = code.Count;
            foreach (int fixup in label.Fixups)
                Patch32(fixup, label.Position - (fixup + 4));
            label.Fixups.Clear();
        }

        private void Target(Label label)
        {
            if (label.IsBound)
            {
                Int32(label.Position - (code.Count + 4));
            }
            else
            {
                label.Fixups.Add(code.Count);
                Int32(0);
            }
        }

        public void Jmp(Label label) { Byte(0xE9); Target(label); }

        public void Jcc(Cond condition, Label label) { Byte(0x0F); Byte(0x80 | (int)condition); Target(label); }

        /// <summary><c>call symbol</c> (rel32, relocated).</summary>
        public void Call(string symbol)
        {
            Byte(0xE8);
            Relocations.Add(new Relocation(code.Count, symbol, RelocationKind.Rel32));
            Int32(0);
        }

        /// <summary><c>lea dst, [rip + symbol]</c> (rel32, relocated: the displacement is the instruction's last field).</summary>
        public void LeaRip(Reg dst, string symbol)
        {
            Rex(true, (int)dst, 0);
            Byte(0x8D);
            Byte(0x05 | Low((int)dst) << 3);
            Relocations.Add(new Relocation(code.Count, symbol, RelocationKind.Rel32));
            Int32(0);
        }

        /// <summary><c>mov dst, [rip + symbol]</c>: loads 8 bytes from a symbol (rel32, relocated).</summary>
        public void LoadRip(Reg dst, string symbol)
        {
            Rex(true, (int)dst, 0);
            Byte(0x8B);
            Byte(0x05 | Low((int)dst) << 3);
            Relocations.Add(new Relocation(code.Count, symbol, RelocationKind.Rel32));
            Int32(0);
        }

        /// <summary><c>call reg</c> (an indirect call through a register).</summary>
        public void CallRegister(Reg target)
        {
            Rex(false, 0, (int)target);
            Byte(0xFF);
            ModRegister(2, (int)target);
        }

        public void Ret() => Byte(0xC3);
        public void Nop() => Byte(0x90);
        public void Int3() => Byte(0xCC);
        public void PushRbp() => Byte(0x55);
        public void PopRbp() => Byte(0x5D);

        /// <summary><c>sub rsp, imm32</c> — always the 7-byte form, so the prologue has a fixed size.</summary>
        public void SubRsp32(int value)
        {
            Byte(0x48); Byte(0x81); Byte(0xEC);
            Int32(value);
        }

        // ── SSE scalar floats ──────────────────────────────────

        private static int Scalar(int size) => size == 4 ? 0xF3 : 0xF2;

        /// <summary><c>movss/movsd</c>; a half (size 2) goes into the low 16 bits with <c>pinsrw</c>.</summary>
        public void LoadFloat(Xmm dst, Mem src, int size)
        {
            if (size == 2)
            {
                RM(false, [0x0F, 0xC4], (int)dst, src, 0x66);
                Byte(0);
                return;
            }
            RM(false, [0x0F, 0x10], (int)dst, src, Scalar(size));
        }

        /// <summary><c>movss/movsd</c>; a half (size 2) comes out of the low 16 bits with <c>pextrw m16</c> (SSE4.1).</summary>
        public void StoreFloat(Mem dst, Xmm src, int size)
        {
            if (size == 2)
            {
                RM(false, [0x0F, 0x3A, 0x15], (int)src, dst, 0x66);
                Byte(0);
                return;
            }
            RM(false, [0x0F, 0x11], (int)src, dst, Scalar(size));
        }

        /// <summary><c>vcvtph2ps dst, src</c> (F16C): the half in the low 16 bits of <paramref name="src"/> as a single.</summary>
        public void HalfToSingle(Xmm dst, Xmm src)
        {
            Vex((int)dst, (int)src);
            Byte(0xC4); Byte(0xE2); Byte(0x79); Byte(0x13);
            Byte((byte)(0xC0 | ((int)dst << 3) | (int)src));
        }

        /// <summary><c>vcvtps2ph dst, src, 0</c> (F16C): the single in <paramref name="src"/> as a half, rounded to nearest even.</summary>
        public void SingleToHalf(Xmm dst, Xmm src)
        {
            Vex((int)dst, (int)src);
            Byte(0xC4); Byte(0xE3); Byte(0x79); Byte(0x1D);
            Byte((byte)(0xC0 | ((int)src << 3) | (int)dst));
            Byte(0);
        }

        private static void Vex(int a, int b)
        {
            if (a > 7 || b > 7)
                throw new X64BackendException("half conversions use xmm0..xmm7");
        }

        private void Sse(int prefix, bool w, byte opcode, int reg, int rm)
        {
            if (prefix >= 0)
                Byte(prefix);
            Rex(w, reg, rm);
            Byte(0x0F);
            Byte(opcode);
            ModRegister(reg, rm);
        }

        public void FloatAdd(Xmm dst, Xmm src, int size) => Sse(Scalar(size), false, 0x58, (int)dst, (int)src);
        public void FloatMul(Xmm dst, Xmm src, int size) => Sse(Scalar(size), false, 0x59, (int)dst, (int)src);
        public void FloatSub(Xmm dst, Xmm src, int size) => Sse(Scalar(size), false, 0x5C, (int)dst, (int)src);
        public void FloatDiv(Xmm dst, Xmm src, int size) => Sse(Scalar(size), false, 0x5E, (int)dst, (int)src);

        /// <summary><c>ucomiss/ucomisd left, right</c>.</summary>
        public void FloatCompare(Xmm left, Xmm right, int size) => Sse(size == 4 ? -1 : 0x66, false, 0x2E, (int)left, (int)right);

        /// <summary><c>cvtsi2ss/sd dst, src64</c>.</summary>
        public void IntToFloat(Xmm dst, Reg src, int size) => Sse(Scalar(size), true, 0x2A, (int)dst, (int)src);

        /// <summary><c>cvttss2si/cvttsd2si dst64, src</c> (truncating).</summary>
        public void FloatToInt(Reg dst, Xmm src, int size) => Sse(Scalar(size), true, 0x2C, (int)dst, (int)src);

        /// <summary><c>cvtss2sd</c> (4 → 8) or <c>cvtsd2ss</c> (8 → 4).</summary>
        public void FloatResize(Xmm dst, Xmm src, int fromSize) => Sse(Scalar(fromSize), false, 0x5A, (int)dst, (int)src);

        /// <summary><c>movq xmm, r64</c>.</summary>
        public void BitsToFloat(Xmm dst, Reg src) => Sse(0x66, true, 0x6E, (int)dst, (int)src);

        /// <summary><c>movq r64, xmm</c>.</summary>
        public void FloatToBits(Reg dst, Xmm src) => Sse(0x66, true, 0x7E, (int)src, (int)dst);
    }
}
