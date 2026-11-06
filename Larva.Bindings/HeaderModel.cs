using System.Text.RegularExpressions;

namespace Larva.Bindings
{
    /// <summary>An enum read from C (a C enum, or one larva made from constants), before a profile names it.</summary>
    public sealed record HeaderEnum(string Name, string Underlying, bool IsFlags, List<(string Name, long Value)> Members, List<string> Typedefs, string? File);

    /// <summary>
    /// What a binding profile is given: C headers read into a description (functions, structs, constants, in C's names),
    /// the C enums taken out of the constants, what the library does not export left out, and where each declaration
    /// comes from (its header) and which typedef C wrote each type with.
    /// </summary>
    public sealed class HeaderModel
    {
        public required BindingDescription Description { get; init; }
        public required HeaderReader Reader { get; init; }
        public required List<HeaderEnum> Enums { get; init; }

        /// <summary>The functions the headers declare and the library does not export (left out).</summary>
        public required List<string> NotExported { get; init; }

        /// <summary>The header that declares a C name, or null.</summary>
        public string? Origin(string cName) => Reader.Origins.GetValueOrDefault(cName);

        /// <summary>The typedef C wrote a parameter (<c>entry#index</c>), result (<c>entry#result</c>) or field (<c>struct.field</c>) with.</summary>
        public string? Typedef(string key) => Reader.TypedefUses.GetValueOrDefault(key);

        /// <summary>
        /// Reads <paramref name="headers"/> (one header, or an umbrella including each) with <paramref name="includes"/>,
        /// through clang when installed. <paramref name="library"/>'s exports keep the functions it has (with
        /// <paramref name="checkExports"/>, a missing one is an error). Without <paramref name="allHeaders"/>, only what the
        /// headers' folders and the include folders declare stays, with the system types it uses.
        /// </summary>
        public static HeaderModel Read(IReadOnlyList<string> headers, string libraryName, string? library, IReadOnlyList<string> includes,
            bool long64, bool enumsFromC, bool autoFlags, bool allHeaders, bool checkExports)
        {
            if (headers.Count == 0)
                throw new BindingException("no header to read (the profile's `Headers`, or a .h on the command line)");
            var reader = new HeaderReader(long64);
            string header = headers[0];
            string? umbrella = null;
            if (headers.Count > 1)
            {
                umbrella = Path.Combine(Path.GetTempPath(), $"larva-bind-{Guid.NewGuid():N}.h");
                File.WriteAllLines(umbrella, headers.Select(h => $"#include \"{Path.GetFullPath(h).Replace('\\', '/')}\""));
                header = umbrella;
            }
            string text;
            try
            {
                // The headers' own folders count as the user's (their #defines become constants, their declarations stay).
                var folders = includes.Concat(headers.Select(h => Path.GetDirectoryName(Path.GetFullPath(h))!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                text = reader.ReadFile(header, libraryName, null, includeDirectories: folders);
            }
            finally
            {
                if (umbrella is not null)
                    File.Delete(umbrella);
            }
            var description = BindingDescription.Parse(text);
            var notExported = new List<string>();
            if (library is not null && File.Exists(library))
            {
                var exports = Exports.Read(library);
                if (checkExports)
                {
                    var known = new HashSet<string>(exports, StringComparer.Ordinal);
                    var missing = description.Functions.Where(f => !known.Contains(f.Entry)).Select(f => f.Entry).ToList();
                    if (missing.Count > 0)
                        throw new BindingException($"{Path.GetFileName(library)} does not export: {string.Join(", ", missing.Take(20))}{(missing.Count > 20 ? ", …" : "")}");
                }
                notExported = description.KeepExported(exports);
            }

            // C enums: their members leave the constants.
            var enums = new List<HeaderEnum>();
            if (enumsFromC)
            {
                var constants = description.Constants.ToDictionary(c => c.Name, StringComparer.Ordinal);
                var consumed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var cEnum in reader.Enums)
                {
                    var members = cEnum.Members.Where(m => constants.ContainsKey(m.Name) && !consumed.Contains(m.Name)).ToList();
                    if (members.Count == 0)
                        continue;
                    consumed.UnionWith(members.Select(m => m.Name));
                    string key = cEnum.Names[0];
                    enums.Add(new HeaderEnum(key, Underlying(members.Select(m => m.Value)), autoFlags && LooksLikeFlags(members.Select(m => m.Value)), members,
                        [.. cEnum.Names], cEnum.File ?? reader.Origins.GetValueOrDefault(key)));
                }
                description.Constants.RemoveAll(c => consumed.Contains(c.Name));
            }

            // Scope: the declarations of the user's headers, and the structs of the system's they use.
            if (!allHeaders && reader.DefineRoots.Count > 0)
            {
                var roots = reader.DefineRoots.Select(r => Path.GetFullPath(r).Replace('\\', '/').TrimEnd('/') + "/").ToList();
                bool Mine(string cName) => reader.Origins.GetValueOrDefault(cName) is not { } origin
                    || roots.Any(r => origin.Replace('\\', '/').StartsWith(r, StringComparison.OrdinalIgnoreCase));
                var used = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Queue<string>();
                void Use(string typeText)
                {
                    foreach (Match m in Regex.Matches(typeText, @"[A-Za-z_]\w*"))
                    {
                        if (used.Add(m.Value))
                            pending.Enqueue(m.Value);
                    }
                }
                foreach (var function in description.Functions)
                {
                    Use(function.Result);
                    foreach (var p in function.Parameters)
                        Use(p.Type);
                }
                foreach (var structure in description.Structs.Where(x => Mine(x.Name)))
                {
                    used.Add(structure.Name);
                    foreach (var f in structure.Fields)
                        Use(f.Type);
                }
                foreach (var e in enums.Where(x => Mine(x.Members.Count > 0 ? x.Members[0].Name : x.Name)))
                    used.Add(e.Name);
                while (pending.Count > 0)
                {
                    string name = pending.Dequeue();
                    if (description.Structs.FirstOrDefault(x => x.Name == name) is { } structure)
                    {
                        foreach (var f in structure.Fields)
                            Use(f.Type);
                    }
                }
                description.Structs.RemoveAll(x => !used.Contains(x.Name));
                enums.RemoveAll(x => !used.Contains(x.Name));
                description.Constants.RemoveAll(c => !Mine(c.Name));
            }
            return new HeaderModel { Description = description, Reader = reader, Enums = enums, NotExported = notExported };
        }

        /// <summary>Distinct bits (zero allowed), three or more of them: a bitflag enum.</summary>
        public static bool LooksLikeFlags(IEnumerable<long> values)
        {
            var bits = values.Where(v => v != 0).ToList();
            return bits.Count >= 3 && bits.All(v => v > 0 && (v & (v - 1)) == 0) && bits.Distinct().Count() == bits.Count;
        }

        /// <summary>The smallest of i32, u32, i64, u64 that holds every value.</summary>
        public static string Underlying(IEnumerable<long> values)
        {
            var list = values.ToList();
            if (list.All(v => v >= int.MinValue && v <= int.MaxValue))
                return "i32";
            if (list.All(v => v >= 0 && v <= uint.MaxValue))
                return "u32";
            return list.Any(v => v < 0) ? "i64" : "u64";
        }
    }
}
