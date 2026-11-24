using System.Text;

namespace Larva.Polyglot
{
    /// <summary>
    /// What <c>larva polyglot names</c> prints: each declaration of the library and its name in each language, one row
    /// per declaration, a column per language (a member with two names shows both: <c>getDepth/setDepth</c>).
    /// </summary>
    public static class NamesTable
    {
        /// <summary>The table of <paramref name="model"/>, limited to declarations whose name in the library starts with <paramref name="filter"/>.</summary>
        public static string Write(PolyglotModel model, string? filter = null)
        {
            var rows = new List<string[]>();
            var languages = model.Targets.Select(t => t.Language).ToList();
            rows.Add(["", "kind", .. model.Targets.Select(t => t.Id)]);
            foreach (var type in model.Types)
            {
                if (filter is null || type.SourceName.StartsWith(filter, StringComparison.Ordinal))
                {
                    if (type.IsModule)
                        rows.Add([type.SourceName, "namespace", .. languages.Select(l => type.Namespaces[l] + (type.Names[l].Length > 0 ? $" ({type.Names[l]})" : ""))]);
                    else
                        rows.Add([type.SourceName, type.Source.Kind.ToString().ToLowerInvariant(), .. languages.Select(l => type.Names[l])]);
                }
                foreach (var member in type.Members)
                {
                    if (filter is not null && !member.SourceName.StartsWith(filter, StringComparison.Ordinal))
                        continue;
                    rows.Add(["  " + member.Display, Kind(member.Kind), .. languages.Select(l => member.Names[l] is { Length: > 0 } names ? string.Join("/", names) : "—")]);
                }
            }
            int[] widths = [.. Enumerable.Range(0, rows[0].Length).Select(c => rows.Max(r => r[c].Length))];
            var text = new StringBuilder();
            foreach (var row in rows)
            {
                for (int c = 0; c < row.Length; c++)
                {
                    text.Append(row[c]);
                    if (c < row.Length - 1)
                        text.Append(' ', widths[c] - row[c].Length + 2);
                }
                text.Append('\n');
            }
            return text.ToString();
        }

        private static string Kind(NameKind kind) => kind switch
        {
            NameKind.EnumMember => "enum value",
            _ => kind.ToString().ToLowerInvariant(),
        };
    }
}
