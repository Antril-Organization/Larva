namespace Larva.Runtime
{
    /// <summary>
    /// A value in (simulated) memory, accessed only through dynamic borrows. Several shared borrows or one exclusive borrow
    /// may be alive at a time, per allocation — the borrow checker's rule, enforced at run time because reflection is
    /// invisible to the static checker. Views of struct-typed fields share their allocation's borrow state.
    /// </summary>
    public sealed class RuntimeObject
    {
        private readonly byte[] memory;
        private readonly int offset;
        private readonly BorrowState state;

        public TypeInfo Type { get; }
        public int Size { get; }

        private RuntimeObject(TypeInfo type, byte[] memory, int offset, int size, BorrowState state)
        {
            Type = type;
            this.memory = memory;
            this.offset = offset;
            Size = size;
            this.state = state;
        }

        internal static RuntimeObject Allocate(TypeInfo type, int size) => new(type, new byte[size], 0, size, new BorrowState());

        /// <summary>Raw bytes of the value (a copy), e.g. to pass it by pointer in an invoke plan.</summary>
        public byte[] ToArray() => memory.AsSpan(offset, Size).ToArray();

        /// <summary>Shared borrow: any number may coexist, but none while an exclusive one is alive.</summary>
        public Borrow Borrow()
        {
            if (state.Writer)
                throw new BorrowConflictException($"`{Type}` is mutably borrowed");
            state.Readers++;
            return new Borrow(this, mutable: false);
        }

        /// <summary>Exclusive borrow: fails while any other borrow of the same allocation is alive.</summary>
        public Borrow BorrowMut()
        {
            if (state.Writer || state.Readers > 0)
                throw new BorrowConflictException(state.Writer ? $"`{Type}` is already mutably borrowed" : $"`{Type}` is borrowed ({state.Readers} shared)");
            state.Writer = true;
            return new Borrow(this, mutable: true);
        }

        internal void Release(bool mutable)
        {
            if (mutable)
                state.Writer = false;
            else
                state.Readers--;
        }

        internal Span<byte> Span(int fieldOffset) => memory.AsSpan(offset + fieldOffset);

        internal RuntimeObject View(TypeInfo type, int fieldOffset) =>
            new(type, memory, offset + fieldOffset, type.Layout?.Size ?? 0, state);

        public override string ToString() => $"{Type} ({Size} bytes)";

        private sealed class BorrowState
        {
            public int Readers;
            public bool Writer;
        }
    }

    /// <summary>A live dynamic borrow; ends when disposed. Using it afterwards is an error.</summary>
    public sealed class Borrow : IDisposable
    {
        private bool alive = true;

        public RuntimeObject Target { get; }
        public bool IsMutable { get; }

        internal Borrow(RuntimeObject target, bool mutable)
        {
            Target = target;
            IsMutable = mutable;
        }

        internal void EnsureAlive()
        {
            if (!alive)
                throw new BorrowConflictException($"borrow of `{Target.Type}` was already released");
        }

        public object this[string field]
        {
            get => Target.Type.GetField(field).GetValue(this);
            set => Target.Type.GetField(field).SetValue(this, value);
        }

        public void Dispose()
        {
            if (!alive)
                return;
            alive = false;
            Target.Release(IsMutable);
        }
    }
}
