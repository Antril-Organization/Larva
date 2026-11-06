namespace Larva.IR
{
    /// <summary>What a garbage-collection mode needs from generated code.</summary>
    [Flags]
    public enum GcCapabilities : ushort
    {
        None = 0,
        /// <summary>Finds live objects by tracing: needs pointer maps per type and stack maps at safepoints.</summary>
        Tracing = 1 << 0,
        /// <summary>May relocate objects: interior borrows must be pinned across safepoints.</summary>
        Moving = 1 << 1,
        /// <summary>Generational / incremental: needs write barriers when storing <c>gc</c> handles.</summary>
        Barriers = 1 << 2,
        /// <summary>Reference counting: needs retain/release at copies and drops.</summary>
        RefCounting = 1 << 3,
        /// <summary>Allows explicit <c>free</c>.</summary>
        ManualFree = 1 << 4,
        /// <summary>Objects live in arenas freed as a block.</summary>
        Regions = 1 << 5,
    }

    public enum GcMode : byte
    {
        /// <summary>Tracing, moving, generational.</summary>
        Default,
        /// <summary>Manual allocation and release (C/C++ style). <c>gc T</c> is not allowed.</summary>
        Manual,
        /// <summary>Automatic reference counting (Swift style).</summary>
        RefCount,
        /// <summary>Arena / region allocation.</summary>
        Arena,
        /// <summary>External implementation of the runtime's GC interface, declaring its own capabilities.</summary>
        Plugin,
    }

    /// <summary>The program-wide GC mode. One mode per program.</summary>
    public sealed record GcConfig(GcMode Mode, GcCapabilities Capabilities, string? PluginName = null)
    {
        public static readonly GcConfig Default = new(GcMode.Default, GcCapabilities.Tracing | GcCapabilities.Moving | GcCapabilities.Barriers);
        public static readonly GcConfig Manual = new(GcMode.Manual, GcCapabilities.ManualFree);
        public static readonly GcConfig RefCount = new(GcMode.RefCount, GcCapabilities.RefCounting);
        public static readonly GcConfig Arena = new(GcMode.Arena, GcCapabilities.Regions);

        public static GcConfig Plugin(string name, GcCapabilities capabilities) => new(GcMode.Plugin, capabilities, name);

        /// <summary>Whether <c>gc T</c> handles can exist: some mechanism must reclaim their objects.</summary>
        public bool ManagesObjects =>
            (Capabilities & (GcCapabilities.Tracing | GcCapabilities.RefCounting | GcCapabilities.Regions)) != 0;

        public override string ToString() => PluginName is null ? $"{Mode} ({Capabilities})" : $"{Mode}:{PluginName} ({Capabilities})";
    }
}
