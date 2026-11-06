namespace Larva.IR.Lowering
{
    public enum IdKind : byte { Type, Class, ClassEnd }

    /// <summary>
    /// The ids a native library's code needs but only the whole program can number: <c>typeid T</c> and
    /// <c>classid C</c> / <c>classid.end C</c>. Each becomes a global of the library (a slot) that the program sets when it
    /// starts. Types are those of the module before monomorphization (generic structs with their arguments).
    /// </summary>
    public sealed class IdSlots
    {
        /// <summary>In slot order: what each slot holds and the global (of the monomorphized module) that is the slot.</summary>
        public List<(IdKind Kind, IrType Type, int Global)> Entries { get; } = [];

        internal Dictionary<(IdKind, IrType), int> Globals { get; } = [];
    }
}
