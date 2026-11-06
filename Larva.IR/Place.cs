namespace Larva.IR
{
    /// <summary>One step from a place to a sub-place.</summary>
    public abstract record Projection
    {
        public static readonly Projection Deref = new DerefProjection();
        public static Projection Field(int index) => new FieldProjection(index);
        public static Projection Index(int local) => new IndexProjection(local);
    }

    /// <summary><c>*p</c></summary>
    public sealed record DerefProjection : Projection;

    /// <summary><c>p.N</c>, field index inside the struct.</summary>
    public sealed record FieldProjection(int FieldIndex) : Projection;

    /// <summary><c>p[_N]</c>, index taken from a local.</summary>
    public sealed record IndexProjection(int Local) : Projection;

    /// <summary>A memory location: a local plus a chain of projections, e.g. <c>(*_1).0</c>.</summary>
    public sealed record Place(int Local, params Projection[] Projections)
    {
        public static implicit operator Place(int local) => new(local);

        public bool IsLocal => Projections.Length == 0;

        public Place Project(Projection projection) => new(Local, [.. Projections, projection]);

        public Place Deref() => Project(Projection.Deref);
        public Place Field(int index) => Project(Projection.Field(index));
        public Place Index(int local) => Project(Projection.Index(local));

        /// <summary>The place without its last projection.</summary>
        public Place Parent => IsLocal
            ? throw new InvalidOperationException("A bare local has no parent place.")
            : new(Local, Projections[..^1]);

        public bool Equals(Place? other) =>
            other is not null && Local == other.Local && Projections.AsSpan().SequenceEqual(other.Projections);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(Local);
            foreach (var projection in Projections)
                hash.Add(projection);
            return hash.ToHashCode();
        }

        public override string ToString() => Text.IrPrinter.Format(this);
    }
}
