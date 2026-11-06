namespace Larva.IR.Linking
{
    /// <summary>
    /// The object-file symbol of a Larva function in native code. Names starting with <c>larva_</c> belong to the runtime
    /// and its entry wrappers and stay as they are. Other functions get a prefix (<c>larva.</c>) so they cannot clash with C
    /// symbols; mangled ones (<see cref="IrFunction.Mangled"/>) are <c>larva.$</c> and 16 hex digits of a hash of their
    /// linked name instead: stable between builds (libraries keep linking), short, and without the name. So are operators
    /// and overloads, whose names carry their parameter types.
    /// </summary>
    public static class NativeSymbols
    {
        public static string Of(IrFunction function, string? prefix) =>
            prefix is not null && ExportName(function) is { } exported ? exported : Of(function.Name, function.Mangled, prefix);

        /// <summary>The C name an exported function has (<c>[Export("name")]</c>, attribute <c>Larva.Export</c>), if any.</summary>
        public static string? ExportName(IrFunction function) =>
            function.Attributes.FirstOrDefault(a => a.Name == "Larva.Export") is { Arguments: [TextArgument name] } ? name.Value : null;

        public static string Of(string name, bool mangled, string? prefix)
        {
            if (prefix is null || name.StartsWith("larva_", StringComparison.Ordinal))
                return name;
            // Operators and overloads (`op==(A, B)`, `F(i32, i32)`) are hashed too: `=`, `,` and spaces are not valid in an
            // export list.
            return mangled || name.IndexOfAny(['(', '=']) >= 0 ? $"{prefix}${Hash(name):x16}" : prefix + name;
        }

        /// <summary>FNV-1a, 64 bits, over the UTF-16 code units of the name.</summary>
        public static ulong Hash(string name)
        {
            ulong hash = 14695981039346656037;
            foreach (char c in name)
                hash = (hash ^ c) * 1099511628211;
            return hash;
        }
    }
}
