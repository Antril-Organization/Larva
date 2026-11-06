namespace Larva.IR
{
    /// <summary>A place in a source file (1-based line and column), as the frontend that wrote the code knew it.</summary>
    public readonly record struct SourcePosition(string File, int Line, int Column)
    {
        public override string ToString() => $"{File}({Line},{Column})";
    }
}
