using System.Text;

namespace Larva.Polyglot
{
    /// <summary>How a name's words are joined.</summary>
    public enum NameStyle : byte
    {
        /// <summary>As the library wrote it.</summary>
        Keep,
        /// <summary><c>WindowFlags</c>.</summary>
        Pascal,
        /// <summary><c>windowFlags</c>.</summary>
        Camel,
        /// <summary><c>window_flags</c>.</summary>
        Snake,
        /// <summary><c>WINDOW_FLAGS</c>.</summary>
        UpperSnake,
        /// <summary><c>windowflags</c> (Java package segments).</summary>
        Lower,
        /// <summary><c>window-flags</c> (npm packages).</summary>
        Kebab,
    }

    /// <summary>What happens to an acronym (a word of two or more capitals) in the styles that keep capitals.</summary>
    public enum AcronymPolicy : byte
    {
        /// <summary>As the library wrote it: <c>GPUDevice</c>, <c>HTTPClient</c>, <c>IOStream</c>.</summary>
        Keep,
        /// <summary>Two letters stay capitals, longer ones become words: <c>GpuDevice</c>, <c>HttpClient</c>, <c>IOStream</c>.</summary>
        ShortUpper,
        /// <summary>Every acronym is a word: <c>GpuDevice</c>, <c>HttpClient</c>, <c>IoStream</c>.</summary>
        Words,
    }

    /// <summary>
    /// Names split into words and joined again in another style. The words are those of System.Bindings' <c>Names.Words</c>
    /// (the other direction, C names into a binding): split at <c>_</c>, at a lower-to-upper change and where an acronym
    /// ends (<c>GPUDevice</c> → GPU, Device); the tests hold both to the same answers.
    /// </summary>
    public static class Naming
    {
        /// <summary>The words of <paramref name="name"/>.</summary>
        public static List<string> Words(string name)
        {
            var words = new List<string>();
            foreach (string part in name.Split('_'))
            {
                if (part.Length == 0)
                    continue;
                int start = 0;
                for (int i = 1; i < part.Length; i++)
                {
                    bool lowerToUpper = IsLower(part[i - 1]) && IsUpper(part[i]);
                    bool acronymEnd = IsUpper(part[i - 1]) && IsUpper(part[i]) && i + 1 < part.Length && IsLower(part[i + 1]);
                    if (lowerToUpper || acronymEnd)
                    {
                        words.Add(part[start..i]);
                        start = i;
                    }
                }
                words.Add(part[start..]);
            }
            return words;
        }

        /// <summary>Whether a word is an acronym: two or more letters, none of them lower case.</summary>
        public static bool IsAcronym(string word) => word.Length >= 2 && !HasLower(word) && word.Any(IsUpper);

        /// <summary><paramref name="name"/> in <paramref name="style"/>, its acronyms as <paramref name="acronyms"/> says.</summary>
        public static string Shape(string name, NameStyle style, AcronymPolicy acronyms = AcronymPolicy.Keep)
        {
            if (style == NameStyle.Keep || name.Length == 0)
                return name;
            var words = Words(name);
            if (words.Count == 0)
                return name;
            // A name in capitals only with several words (`MAX_DEPTH`) has no acronyms: its words are ordinary words. One
            // word in capitals (`IO`, `URL`) is an acronym.
            bool screaming = !HasLower(name) && words.Count > 1;
            var text = new StringBuilder(name.Length + 4);
            for (int i = 0; i < words.Count; i++)
            {
                string word = words[i];
                switch (style)
                {
                    case NameStyle.Snake or NameStyle.UpperSnake or NameStyle.Kebab:
                        if (i > 0)
                            text.Append(style == NameStyle.Kebab ? '-' : '_');
                        text.Append(style == NameStyle.UpperSnake ? word.ToUpperInvariant() : word.ToLowerInvariant());
                        break;
                    case NameStyle.Lower:
                        text.Append(word.ToLowerInvariant());
                        break;
                    case NameStyle.Camel when i == 0:
                        text.Append(HasLower(word) ? First(word, upper: false) : word.ToLowerInvariant());
                        break;
                    default:
                        text.Append(screaming ? First(word.ToLowerInvariant(), upper: true) : Capital(word, acronyms));
                        break;
                }
            }
            return text.ToString();
        }

        /// <summary>A word that starts a capitalized name or follows another: an acronym as the policy says.</summary>
        private static string Capital(string word, AcronymPolicy acronyms) =>
            IsAcronym(word) && (acronyms == AcronymPolicy.Words || acronyms == AcronymPolicy.ShortUpper && word.Length > 2)
                ? First(word.ToLowerInvariant(), upper: true)
                : First(word, upper: true);

        /// <summary>A name that can be written: one that starts with a digit gets a <c>_</c> before it.</summary>
        public static string Writable(string name) => name.Length > 0 && char.IsAsciiDigit(name[0]) ? "_" + name : name;

        private static bool IsUpper(char c) => c is >= 'A' and <= 'Z';
        private static bool IsLower(char c) => c is >= 'a' and <= 'z';

        private static bool HasLower(string text)
        {
            foreach (char c in text)
            {
                if (IsLower(c))
                    return true;
            }
            return false;
        }

        private static string First(string word, bool upper) =>
            word.Length == 0 ? word : (upper ? char.ToUpperInvariant(word[0]) : char.ToLowerInvariant(word[0])) + word[1..];
    }
}
