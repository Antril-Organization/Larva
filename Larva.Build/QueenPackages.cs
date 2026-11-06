using System.Globalization;

namespace Larva.Build
{
    /// <summary>
    /// Turns a project's <c>package:</c> entries into what they stand for once installed: <c>.queen</c> files (packages
    /// that carry sources), <c>.lvo</c> files or folders (compiled packages) and native libraries. Hive provides it; problems
    /// go to <paramref name="diagnostics"/>.
    /// </summary>
    public delegate IReadOnlyList<string> QueenPackageResolver(QueenProject project, IReadOnlyList<PackageReference> packages, List<QueenDiagnostic> diagnostics);

    /// <summary>
    /// A package: <c>Author/Name</c> and the versions that will do, written after <c>@</c>: exactly one (<c>1.2</c> or
    /// <c>=1.2</c>), any compatible one (<c>^1.2</c>: same first non-zero term, not older), or at least one (<c>&gt;=1.2</c>).
    /// No version: the newest.
    /// </summary>
    public sealed record PackageReference(string Author, string Name, VersionRange? Version, int Line = 0, int Column = 0)
    {
        public string Id => $"{Author}/{Name}";

        public override string ToString() => Version is null ? Id : $"{Id}@{Version}";

        public static PackageReference? Parse(string text, int line = 0, int column = 0)
        {
            text = text.Trim();
            string? version = null;
            int at = text.IndexOf('@');
            if (at >= 0)
            {
                version = text[(at + 1)..];
                text = text[..at];
            }
            var parts = text.Split('/');
            if (parts.Length != 2 || !parts.All(IsPart))
                return null;
            VersionRange? range = null;
            if (version is not null && (range = VersionRange.Parse(version)) is null)
                return null;
            return new PackageReference(parts[0], parts[1], range, line, column);
        }

        /// <summary>A part of a package's name: letters, digits, <c>_</c>, <c>-</c> and dots, starting with a letter or digit.</summary>
        public static bool IsPart(string part) =>
            part.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(part[0]) && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            && !part.Contains("..", StringComparison.Ordinal);
    }

    public enum VersionOperator : byte { Exact, Compatible, AtLeast }

    /// <summary>The versions a package reference accepts (see <see cref="PackageReference"/>).</summary>
    public sealed record VersionRange(VersionOperator Operator, uint[] Version)
    {
        public static VersionRange? Parse(string text)
        {
            text = text.Trim();
            var op = VersionOperator.Exact;
            if (text.StartsWith('^'))
            {
                op = VersionOperator.Compatible;
                text = text[1..];
            }
            else if (text.StartsWith(">=", StringComparison.Ordinal))
            {
                op = VersionOperator.AtLeast;
                text = text[2..];
            }
            else if (text.StartsWith('='))
            {
                text = text[1..];
            }
            if (text.StartsWith('v') || text.StartsWith('V'))
                text = text[1..];
            return QueenParser.ParseTerms(text) is { Length: > 0 } terms ? new VersionRange(op, terms) : null;
        }

        public bool Accepts(uint[] candidate) => Operator switch
        {
            VersionOperator.Exact => Compare(candidate, Version) == 0,
            VersionOperator.AtLeast => Compare(candidate, Version) >= 0,
            _ => Compare(candidate, Version) >= 0 && Compatible(candidate),
        };

        /// <summary><c>^1.2</c> takes 1.x from 1.2 on; <c>^0.3</c> takes 0.3.x; <c>^0.0.4</c> only 0.0.4.</summary>
        private bool Compatible(uint[] candidate)
        {
            int lead = Array.FindIndex(Version, t => t != 0);
            if (lead < 0)
                lead = Version.Length - 1;
            for (int i = 0; i <= lead; i++)
            {
                if (Term(candidate, i) != Version[i])
                    return false;
            }
            return true;
        }

        private static uint Term(uint[] version, int i) => i < version.Length ? version[i] : 0;

        public static int Compare(uint[] a, uint[] b)
        {
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int order = Term(a, i).CompareTo(Term(b, i));
                if (order != 0)
                    return order;
            }
            return 0;
        }

        public static string Format(uint[] version) => string.Join('.', version.Select(t => t.ToString(CultureInfo.InvariantCulture)));

        public override string ToString() => Operator switch
        {
            VersionOperator.Compatible => "^",
            VersionOperator.AtLeast => ">=",
            _ => "",
        } + Format(Version);
    }

    /// <summary>The <c>HIVE</c> chunk of a project: where packages come from and how the project is published.</summary>
    public sealed record HiveSettings
    {
        public const string DefaultProvider = "antril.org/hive";

        /// <summary>The registries of a project that names none: this machine's (<c>larva config hive.provider</c>), else <see cref="DefaultProvider"/>.</summary>
        public static string[] Defaults() => LarvaDefaults.HiveProviders() is { Length: > 0 } configured ? configured : [DefaultProvider];

        /// <summary>Registries, tried in order: addresses or folders.</summary>
        public string[] Providers { get; init; } = [];

        /// <summary>The name it publishes as (by default PROJ's <c>author</c> and <c>name</c>).</summary>
        public PackageReference? Package { get; init; }

        public string? License { get; init; }

        /// <summary><c>publish: source</c>: packages carry the sources too, not only compiled Larva objects.</summary>
        public bool PublishSources { get; init; }

        /// <summary>The private key published packages are signed with (absolute), if any.</summary>
        public string? SigningKey { get; init; }

        /// <summary><c>verify: strict</c> (the default): installed packages must be signed; <c>hashes</c>: hashes are enough.</summary>
        public bool RequireSignatures { get; init; } = true;

        /// <summary>Where installed packages are kept (absolute), or null for the user's Hive cache.</summary>
        public string? Cache { get; init; }

        /// <summary><c>private: true</c>: never published.</summary>
        public bool Private { get; init; }
    }
}
