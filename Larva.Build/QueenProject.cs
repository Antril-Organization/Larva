using Larva.IR;

namespace Larva.Build
{
    /// <summary>What a project builds: a program, a library, or a tool (a program larva installs and runs as `larva <command>`).</summary>
    public enum ProjectKind : byte { Exe, Library, Tool }

    public enum ProjectBackend : byte { Vm, Llvm, X64 }

    /// <summary>What <c>larva build</c> makes: a native executable, or WebAssembly (with its JavaScript runtime and a page).</summary>
    public enum ProjectTarget : byte { Native, Wasm }

    /// <summary>
    /// Evaluates the <c>$[…]</c> expressions of a project file, in order: each result as text, or null when it fails
    /// (reported in <paramref name="diagnostics"/>). Runs the project's language (with the standard library in <paramref name="stdlib"/>).
    /// </summary>
    public delegate string?[] QueenEvaluator(IReadOnlyList<QueenExpression> expressions, string? stdlib, string file, List<QueenDiagnostic> diagnostics);

    /// <summary>
    /// A project: its <c>.queen</c> file read and checked. Known chunks: <c>PROJ</c> (what the project is), <c>BUILD</c>
    /// (how it is compiled: every compilation option) and <c>DEPS</c> (other projects compiled with it); any other chunk
    /// is kept as written, for tools. Sources: every file of an installed language (<see cref="SourceExtensions"/>) under the project's folder, except subfolders with their
    /// own <c>.queen</c> and what <c>ignore</c> names.
    /// </summary>
    public sealed class QueenProject
    {
        public const string Extension = ".queen";

        /// <summary>A project's file: <c>project.queen</c> at the top of its folder (the name chooses its profile).</summary>
        public const string ProjectFile = "project" + Extension;

        /// <summary>The profile the file was read with (<c>project</c>, or one that extends it).</summary>
        public Profiles.QueenProfile? Profile { get; init; }

        /// <summary>The source files' extensions: those of the languages installed (larva sets them from their manifests).</summary>
        public static IReadOnlyList<string> SourceExtensions { get; set; } = [];

        /// <summary>Whether <paramref name="path"/> is a source file of an installed language.</summary>
        public static bool IsSource(string path) => SourceExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        public required string File { get; init; }
        public string Folder => Path.GetDirectoryName(Path.GetFullPath(File))!;
        public required QueenDocument Document { get; init; }

        public string Name { get; private set; } = "";
        public ProjectKind Kind { get; private set; } = ProjectKind.Exe;
        public uint[]? Version { get; private set; }
        public string? Author { get; private set; }
        public string? Description { get; private set; }

        /// <summary>A tool's command (PROJ <c>command</c>; by default its name in lower case); null for other projects.</summary>
        public string? Command { get; private set; }

        /// <summary><c>tool: Author/Name[@version]</c> entries of <c>TOOLS</c>: the tools this project uses.</summary>
        public List<PackageReference> Tools { get; } = [];

        public GcConfig Gc { get; private set; } = GcConfig.Default;
        public ProjectBackend Backend { get; private set; } = ProjectBackend.Vm;
        public ProjectTarget Target { get; private set; } = ProjectTarget.Native;

        /// <summary>A native platform other than this machine's (<c>linux-x64</c>, <c>macos-arm64</c>…), or null.</summary>
        public string? Platform { get; private set; }

        /// <summary>Native platforms a project can name in <c>target</c>.</summary>
        public static readonly string[] Platforms = ["windows-x64", "linux-x64", "linux-arm64", "macos-x64", "macos-arm64"];
        public int Optimization { get; private set; } = 2;

        /// <summary>The processor for the LLVM back end (BUILD <c>cpu</c>): null is <c>generic</c>; <c>native</c> is this machine's.</summary>
        public string? Cpu { get; private set; }
        public ReflectionLevel Reflection { get; private set; } = ReflectionLevel.Layout;
        public BorrowCheckMode BorrowCheck { get; private set; } = BorrowCheckMode.Enforce;

        /// <summary><c>unsafe: deny</c> turns it off: the project's own code may not use pointers or <c>unsafe</c>.</summary>
        public bool AllowUnsafe { get; private set; } = true;

        /// <summary><c>gc: custom</c>: the class that implements <c>System.IGarbageCollector</c> (<c>gc.collector</c>).</summary>
        public string? GcCollector { get; private set; }

        /// <summary>Whether <c>gc: custom</c> was chosen.</summary>
        public bool CustomGc { get; private set; }
        public GenericsMode Generics { get; private set; } = GenericsMode.Shared;
        /// <summary>The standard library folder (absolute), or null for none.</summary>
        public string? Stdlib { get; private set; }
        /// <summary>Where <c>exe</c>/<c>build</c> write (absolute); null for the default (<c>bin/NAME.exe</c> / <c>.lvo</c>).</summary>
        public string? Output { get; private set; }
        public List<string> Ignore { get; } = [];

        public List<QueenProject> Dependencies { get; } = [];

        /// <summary>Compiled libraries in <c>DEPS</c> (<c>.lvo</c> files or folders of them), absolute.</summary>
        public List<string> Libraries { get; } = [];

        /// <summary><c>package: Author/Name[@version]</c> entries of <c>DEPS</c>: resolved by Hive (see <see cref="QueenPackageResolver"/>).</summary>
        public List<PackageReference> Packages { get; } = [];

        /// <summary>Native libraries in <c>DEPS</c> (<c>lib: …</c>), absolute: copied next to the build and found at run time.</summary>
        public List<string> NativeLibraries { get; } = [];

        /// <summary>Files copied next to the build (<c>FILES</c>): each source file and its path under the build's folder.</summary>
        public List<(string Source, string Target)> CopiedFiles { get; } = [];

        /// <summary>Files built into the program (<c>RESOURCES</c>): each one's name and file, in a stable order.</summary>
        public List<(string Name, string File)> Resources { get; } = [];

        /// <summary>The <c>HIVE</c> chunk (packaging).</summary>
        public HiveSettings Hive { get; private set; } = new();

        /// <summary>The access of declarations that say none (BUILD <c>access.default</c>); null for the language's defaults.</summary>
        public string? DefaultAccess { get; private set; }

        /// <summary>The project's own source files (absolute paths), in a stable order.</summary>
        public List<string> Sources { get; } = [];

        /// <summary>The compilation options of the <c>BUILD</c> chunk.</summary>
        public CompilationOptions Compilation => CompilationOptions.Default with
        {
            Gc = Gc,
            Reflection = Reflection,
            BorrowCheck = BorrowCheck,
            Generics = Generics,
            Optimize = Optimization > 0,
        };

        /// <summary>Where a build goes when <c>output</c> is not set: <c>bin/NAME.exe</c> or <c>bin/NAME.lvo</c>.</summary>
        public string DefaultOutput(bool native) =>
            Output ?? Path.Combine(Folder, "bin", Name + (native && Kind != ProjectKind.Library ? Target == ProjectTarget.Wasm ? ".wasm" : ExecutableExtension : ".lvo"));

        private string ExecutableExtension => (Platform ?? (OperatingSystem.IsWindows() ? "windows" : "unix")).StartsWith("windows", StringComparison.Ordinal) ? ".exe" : "";

        /// <summary>Whether <paramref name="path"/> is a compiled Larva object, or a folder of them.</summary>
        public static bool IsCompiled(string path) =>
            System.IO.File.Exists(path) && (path.EndsWith(Larva.IR.Linking.ObjectFile.Extension, StringComparison.OrdinalIgnoreCase) || IsAssembly(path))
            || Directory.Exists(path) && Directory.EnumerateFiles(path, "*" + Larva.IR.Linking.ObjectFile.Extension).Any();

        /// <summary>
        /// A compiled Larva library (declarations and bodies, such as `larva bind` or `larva lib` write): a <c>.lva</c>, or a
        /// <c>.dll</c> that carries one (a PE image with a <c>.lva</c> section; a native DLL does not).
        /// </summary>
        public static bool IsAssembly(string path)
        {
            if (path.EndsWith(".lva", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(path))
                return false;
            try
            {
                using var file = System.IO.File.OpenRead(path);
                var head = new byte[Math.Min(file.Length, 1024)];
                file.ReadExactly(head);
                return head.Length > 2 && head[0] == 'M' && head[1] == 'Z' && head.AsSpan().IndexOf(".lva\0\0\0\0"u8) >= 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>The project's sources after its dependencies' (each once), for one compilation.</summary>
        public List<string> AllSources()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(QueenProject project)
            {
                foreach (var dependency in project.Dependencies)
                    Add(dependency);
                foreach (var source in project.Sources)
                {
                    if (seen.Add(source))
                        result.Add(source);
                }
            }
            Add(this);
            return result;
        }

        /// <summary>The compiled libraries of this project and its dependencies (each once).</summary>
        public List<string> AllLibraries()
        {
            var result = new List<string>();
            void Add(QueenProject project)
            {
                foreach (var dependency in project.Dependencies)
                    Add(dependency);
                foreach (var library in project.Libraries.Where(l => !result.Contains(l, StringComparer.OrdinalIgnoreCase)))
                    result.Add(library);
            }
            Add(this);
            return result;
        }

        /// <summary>
        /// The project file a path names: the <c>.queen</c> file itself (its name is checked when it is loaded: QN140), or
        /// the project file of a folder (<c>project.queen</c>, or a package's <c>package.queen</c>). Null when there is none.
        /// </summary>
        public static string? Find(string path)
        {
            if (System.IO.File.Exists(path) && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(path);
            return Directory.Exists(path) ? InFolder(path) : null;
        }

        /// <summary>The project file directly in <paramref name="folder"/> (<c>project.queen</c>, else <c>package.queen</c>…), or null.</summary>
        public static string? InFolder(string folder)
        {
            foreach (string name in Profiles.ProfileCatalog.ProjectFileNames)
            {
                string file = Path.Combine(folder, name);
                if (System.IO.File.Exists(file))
                    return Path.GetFullPath(file);
            }
            return null;
        }

        /// <summary>
        /// The project a file or folder belongs to: the nearest project file from <paramref name="start"/> up. The search
        /// stops at the top of the repository (a folder with <c>.git</c>) and at <paramref name="stops"/> (an editor's
        /// workspace folders): a project file above them is not this code's.
        /// </summary>
        public static string? Nearest(string start, IReadOnlyCollection<string>? stops = null)
        {
            string? folder = Directory.Exists(start) ? Path.GetFullPath(start) : Path.GetDirectoryName(Path.GetFullPath(start));
            for (; folder is not null; folder = Path.GetDirectoryName(folder))
            {
                if (InFolder(folder) is { } found)
                    return found;
                if (Directory.Exists(Path.Combine(folder, ".git")) || System.IO.File.Exists(Path.Combine(folder, ".git"))
                    || stops?.Any(s => string.Equals(Path.GetFullPath(s).TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) == true)
                    return null;
            }
            return null;
        }

        /// <summary>The resources of this project and its dependencies (each name once: the project's own win).</summary>
        public List<(string Name, string File)> AllResources()
        {
            var result = new List<(string Name, string File)>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            void Add(QueenProject project)
            {
                foreach (var resource in project.Resources.Where(r => names.Add(r.Name)))
                    result.Add(resource);
                foreach (var dependency in project.Dependencies)
                    Add(dependency);
            }
            Add(this);
            return result;
        }

        /// <summary>The native libraries of this project and its dependencies (each once).</summary>
        public List<string> AllNativeLibraries()
        {
            var result = new List<string>();
            void Add(QueenProject project)
            {
                foreach (var dependency in project.Dependencies)
                    Add(dependency);
                foreach (var library in project.NativeLibraries.Where(l => !result.Contains(l, StringComparer.OrdinalIgnoreCase)))
                    result.Add(library);
            }
            Add(this);
            return result;
        }

        public static QueenProject? Load(string path, QueenEvaluator? evaluator, List<QueenDiagnostic> diagnostics, QueenPackageResolver? packages = null) =>
            Load(Path.GetFullPath(path), evaluator, diagnostics, [], packages);

        /// <summary>
        /// Reads a project from <paramref name="text"/> (an editor's buffer, not saved yet) as if it were the file at
        /// <paramref name="path"/>: its dependencies are read from disk.
        /// </summary>
        public static QueenProject? LoadText(string path, string text, List<QueenDiagnostic> diagnostics, QueenPackageResolver? packages = null)
        {
            pendingText = (Path.GetFullPath(path), text);
            try
            {
                return Load(Path.GetFullPath(path), null, diagnostics, [], packages);
            }
            finally
            {
                pendingText = null;
            }
        }

        [ThreadStatic]
        private static (string Path, string Text)? pendingText;

        /// <summary>
        /// The BUILD settings in effect, as written: this machine's defaults (<c>larva config</c>), then the project's own
        /// BUILD entries, which win. What a build is really made with (and what tells one build from the next: a change
        /// of any of them, in the file or in the defaults, means building again).
        /// </summary>
        public SortedDictionary<string, string> EffectiveBuild()
        {
            var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in LarvaDefaults.Effective().Where(v => LarvaDefaults.IsBuildKey(v.Key)))
                settings[key] = value;
            foreach (var entry in Document.Named("BUILD").SelectMany(c => c.Entries))
                settings[entry.Key] = entry.Value.Display;
            return settings;
        }

        /// <summary>
        /// The project a build without a <c>.queen</c> has: nothing but this machine's defaults (<c>larva config</c>), read
        /// as a project's BUILD chunk is, so that every key applies the same way with or without a project file.
        /// </summary>
        public static QueenProject FromDefaults(string name)
        {
            var diagnostics = new List<QueenDiagnostic>();
            string path = Path.Combine(Directory.GetCurrentDirectory(), name + Extension);
            var document = QueenParser.Parse($"queen 1.0;\n\nchunk PROJ:\n   name: {name}\n", path, diagnostics)
                ?? QueenParser.Parse("queen 1.0;\n\nchunk PROJ:\n   name: program\n", path, diagnostics)!;
            var project = new QueenProject { File = path, Document = document };
            project.Read(diagnostics);
            return project;
        }

        /// <summary>Whether the project's BUILD chunk sets <paramref name="key"/> itself (not a default).</summary>
        public bool Sets(string key) =>
            Document.Chunks.Where(c => c.Name == "BUILD").SelectMany(c => c.Entries).Any(e => e.Key == key) || LarvaDefaults.Configured().ContainsKey(key);

        /// <summary>
        /// What the project's frontend is told (<c>{ "access": "file", "borrows": false, "unsafe": false, "safe": […] }</c>): its
        /// default access, whether it may borrow (not with the borrow checker off: a borrow nothing checks is an error), and
        /// with <c>unsafe: deny</c> the files that may not be unsafe (its own: a dependency keeps what its project allows).
        /// </summary>
        public System.Text.Json.Nodes.JsonObject FrontendOptions()
        {
            var options = new System.Text.Json.Nodes.JsonObject { ["borrows"] = BorrowCheck != BorrowCheckMode.Off };
            if (DefaultAccess is { } access)
                options["access"] = access;
            var denied = SafeSources();
            if (denied.Count > 0)
            {
                options["unsafe"] = false;
                options["safe"] = new System.Text.Json.Nodes.JsonArray([.. denied.Select(s => (System.Text.Json.Nodes.JsonNode)s)]);
            }
            return options;
        }

        /// <summary>The sources that may not be unsafe: those of this project and of its dependencies that say <c>unsafe: deny</c>.</summary>
        public List<string> SafeSources()
        {
            var result = new List<string>();
            var seen = new HashSet<QueenProject>();
            void Add(QueenProject project)
            {
                if (!seen.Add(project))
                    return;
                foreach (var dependency in project.Dependencies)
                    Add(dependency);
                if (!project.AllowUnsafe)
                    result.AddRange(project.Sources);
            }
            Add(this);
            return result;
        }

        private static QueenProject? Load(string path, QueenEvaluator? evaluator, List<QueenDiagnostic> diagnostics, List<string> loading, QueenPackageResolver? packages)
        {
            if (loading.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.Add(new QueenDiagnostic("QN100", $"projects depend on each other in a cycle: {string.Join(" -> ", loading.Append(path).Select(Path.GetFileName))}", path, 1, 1));
                return null;
            }
            string text;
            try
            {
                text = pendingText is { } pending && string.Equals(pending.Path, path, StringComparison.OrdinalIgnoreCase) ? pending.Text : System.IO.File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new QueenDiagnostic("QN101", $"cannot read the project: {e.Message}", path, 1, 1));
                return null;
            }
            int errorsBefore = diagnostics.Count(d => d.IsError);
            // The file's name says its profile: a project is `project.queen` (or a file whose profile extends it, a Hive
            // package's `package.queen`).
            if (!Profiles.ProfileCatalog.IsProjectFile(path) || Profiles.ProfileCatalog.ForFile(path) is not { } profile)
            {
                diagnostics.Add(new QueenDiagnostic("QN140", $"`{Path.GetFileName(path)}` is not a project file: a project is `{ProjectFile}` (rename it; `PROJ name` keeps the project's name)", path, 1, 1));
                return null;
            }
            if (QueenParser.Parse(text, path, diagnostics) is not { } document)
                return null;
            var project = new QueenProject { File = path, Document = document, Profile = profile };
            project.Evaluate(evaluator, diagnostics);
            Profiles.QueenValidator.Validate(document, profile, diagnostics);
            project.Read(diagnostics);
            if (diagnostics.Count(d => d.IsError) > errorsBefore)
                return null;
            project.FindSources(diagnostics);
            project.ReadFiles(diagnostics);
            loading.Add(path);
            // DEPS: `package: Author/Name[@version]` (Hive), `proj: path` (a project), `larvaobj: path` (.lvo files),
            // `assembly: path` (a .lva, compiled against and linked), `lib: path` (a native library); `local: path` and
            // `Name: path` are read as a project, .lvo files or a .lva.
            foreach (var entry in document.Named("DEPS").SelectMany(c => c.Entries))
            {
                string relative = entry.Value.Display;
                string target = Path.GetFullPath(Path.Combine(project.Folder, relative));
                switch (entry.Key)
                {
                    case "package":
                        if (PackageReference.Parse(relative, entry.Value.Line, entry.Value.Column) is { } package)
                            project.Packages.Add(package);
                        else
                            project.Report(diagnostics, "QN104", $"`package` is `Author/Name`, optionally with a version (`Author/Name@1.2`, `@^1.2`, `@>=1.2`), not `{relative}`", entry.Value);
                        continue;
                    case "lib":
                        if (System.IO.File.Exists(target))
                            project.NativeLibraries.Add(target);
                        else
                            project.Report(diagnostics, "QN106", $"native library `{relative}` not found", entry.Value);
                        continue;
                    case "assembly":
                        if (System.IO.File.Exists(target) && IsAssembly(target))
                            project.Libraries.Add(target);
                        else
                            project.Report(diagnostics, "QN108", $"`assembly` names a Larva library (`.dll` or `.lva`), not `{relative}`", entry.Value);
                        continue;
                    case "larvaobj":
                        if (IsCompiled(target))
                            project.Libraries.Add(target);
                        else
                            project.Report(diagnostics, "QN107", $"`larvaobj` names a `.lvo` file or a folder of them, not `{relative}`", entry.Value);
                        continue;
                }
                if (Find(target) is not { } dependencyFile)
                {
                    // A compiled library: compiled against its declarations and linked, not compiled again.
                    if (entry.Key != "proj" && IsCompiled(target))
                        project.Libraries.Add(target);
                    else
                        project.Report(diagnostics, "QN102", entry.Key == "proj"
                            ? $"`proj` names a `.queen` file or a folder with one, not `{relative}`"
                            : $"dependency `{entry.Key}`: no project at `{relative}` (a .queen file, a folder with one, or compiled .lvo libraries)", entry.Value);
                    continue;
                }
                project.AddDependency(dependencyFile, entry.Key, entry.Value, evaluator, diagnostics, loading, packages);
            }
            // Packages: Hive turns them into installed projects or compiled libraries.
            if (project.Packages.Count > 0)
            {
                if (packages is null)
                {
                    foreach (var package in project.Packages)
                        diagnostics.Add(new QueenDiagnostic("QN105", $"package `{package}`: only tools that use Hive resolve packages; it is skipped", path, package.Line, package.Column, IsError: false));
                }
                else
                {
                    foreach (var resolved in packages(project, project.Packages, diagnostics))
                    {
                        if (Find(resolved) is { } packageProject)
                            project.AddDependency(packageProject, "package", null, evaluator, diagnostics, loading, packages);
                        else if (IsCompiled(resolved))
                            project.Libraries.Add(resolved);
                        else if (System.IO.File.Exists(resolved))
                            project.NativeLibraries.Add(resolved);
                    }
                }
            }
            loading.RemoveAt(loading.Count - 1);
            return diagnostics.Count(d => d.IsError) > errorsBefore ? null : project;
        }

        private void AddDependency(string file, string key, QueenValue? at, QueenEvaluator? evaluator, List<QueenDiagnostic> diagnostics, List<string> loading, QueenPackageResolver? packages)
        {
            if (Dependencies.Any(d => string.Equals(d.File, file, StringComparison.OrdinalIgnoreCase)))
                return;
            if (Load(file, evaluator, diagnostics, loading, packages) is not { } dependency)
                return;
            if (dependency.Kind != ProjectKind.Library && at is not null)
                Report(diagnostics, "QN103", $"dependency `{key}` is `type: exe`: a project depends on libraries", at);
            Dependencies.Add(dependency);
        }

        private void Report(List<QueenDiagnostic> diagnostics, string code, string message, QueenValue at, bool error = true) =>
            diagnostics.Add(new QueenDiagnostic(code, message, File, at.Line, at.Column, error));

        // ── $[…] ───────────────────────────────────────────────

        /// <summary>Replaces every <c>$[…]</c> with its result (all of them evaluated in one run).</summary>
        private void Evaluate(QueenEvaluator? evaluator, List<QueenDiagnostic> diagnostics)
        {
            var expressions = new List<QueenExpression>();
            void Collect(QueenValue value)
            {
                if (value is QueenTemplate template)
                    expressions.AddRange(template.Expressions);
                else if (value is QueenList list)
                    foreach (var item in list.Items)
                        Collect(item);
            }
            foreach (var entry in Document.Chunks.SelectMany(c => c.Entries))
                Collect(entry.Value);
            if (expressions.Count == 0)
                return;
            if (evaluator is null)
            {
                foreach (var expression in expressions)
                    diagnostics.Add(new QueenDiagnostic("QN110", "`$[…]` needs a frontend to run (only `larva` evaluates it)", File, expression.Line, expression.Column));
                return;
            }
            // The standard library for `$[…]`: the BUILD's when it is written as plain text, else LARVA_STDLIB.
            string? stdlib = Environment.GetEnvironmentVariable("LARVA_STDLIB");
            if (Document.Named("BUILD").Select(c => c["stdlib"]?.Value).FirstOrDefault(v => v is not null) is { } written and not QueenTemplate)
                stdlib = written is QueenIdentifier { Name: "none" } ? null : Path.GetFullPath(Path.Combine(Folder, written.Display));
            var results = evaluator(expressions, stdlib, File, diagnostics);
            var byExpression = expressions.Select((e, i) => (e, results[i])).ToDictionary(p => p.e, p => p.Item2);

            QueenValue Replace(QueenValue value)
            {
                switch (value)
                {
                    case QueenList list:
                        return list with { Items = [.. list.Items.Select(Replace)] };
                    case QueenTemplate { Parts: [QueenExpression only] } single:
                        // A value that is only `$[…]` takes the kind of its result.
                        return byExpression[only] is { } result ? QueenParser.Classify(result, single.Line, single.Column) : new QueenText("", single.Line, single.Column);
                    case QueenTemplate template:
                        // Text with `$[…]` inside reads like unquoted text once filled in (`v$[n].0` is a version).
                        return QueenParser.Classify(string.Concat(template.Parts.Select(p => p is QueenExpression e ? byExpression[e] ?? "" : (string)p)), template.Line, template.Column);
                    default:
                        return value;
                }
            }
            foreach (var chunk in Document.Chunks)
            {
                for (int i = 0; i < chunk.Entries.Count; i++)
                    chunk.Entries[i] = chunk.Entries[i] with { Value = Replace(chunk.Entries[i].Value) };
            }
        }

        // ── Known chunks ───────────────────────────────────────

        /// <summary>
        /// The project's settings, from its chunks. What may be written, the values each key takes and what is required
        /// were checked against the file's profile (<see cref="Profiles.QueenValidator"/>): a value it rejected falls back
        /// to its default here, with nothing more reported. What needs the disk or other projects is checked here.
        /// </summary>
        private void Read(List<QueenDiagnostic> diagnostics)
        {
            var proj = Document.Named("PROJ").FirstOrDefault();
            // The name: PROJ's, else the project's folder's.
            Name = proj?["name"] is { Value: QueenIdentifier or QueenText or QueenVersion or QueenNumber } name && name.Value.Display.Length > 0
                ? name.Value.Display : Path.GetFileName(Folder);
            if (proj is null)
                return;
            if (proj["type"] is { } type)
            {
                Kind = type.Value.Display switch
                {
                    "library" => ProjectKind.Library,
                    "tool" => ProjectKind.Tool,
                    _ => ProjectKind.Exe,
                };
            }
            if (proj["command"] is { } command && Kind == ProjectKind.Tool && IsCommandName(command.Value.Display))
                Command = command.Value.Display;
            if (Kind == ProjectKind.Tool && Command is null && Name.Length > 0)
                Command = Name.ToLowerInvariant();
            if (proj["version"] is { } version)
            {
                Version = version.Value switch
                {
                    QueenVersion v => v.Terms,
                    QueenNumber n when QueenParser.ParseTerms(n.Raw) is { } terms => terms,
                    _ => null,
                };
            }
            Author = proj["author"]?.Value.Display;
            Description = proj["description"]?.Value.Display;

            // The machine's defaults first (`larva config`), then the project's own BUILD entries, which win.
            var defaults = new QueenChunk("BUILD", [.. LarvaDefaults.Entries(LarvaDefaults.Effective())], 0, 0);
            foreach (var build in Document.Named("BUILD").Prepend(defaults))
            {
                foreach (var entry in build.Entries)
                {
                    var value = entry.Value;
                    switch (entry.Key)
                    {
                        case "gc":
                            Gc = value.Display switch
                            {
                                "default" or "tracing" => GcConfig.Default,
                                "manual" => GcConfig.Manual,
                                "refcount" => GcConfig.RefCount,
                                "arena" => GcConfig.Arena,
                                "custom" => GcConfig.Default,
                                _ => Bad(value, "`gc` is `default`, `manual`, `refcount`, `arena` or `custom`", GcConfig.Default),
                            };
                            CustomGc = value.Display == "custom";
                            break;
                        case "gc.collector":
                            GcCollector = value.Display;
                            break;
                        case "unsafe":
                            AllowUnsafe = value.Display switch
                            {
                                "allow" => true,
                                "deny" => false,
                                _ => Bad(value, "`unsafe` is `allow` or `deny`", true),
                            };
                            break;
                        case "backend":
                            Backend = value.Display switch
                            {
                                "vm" => ProjectBackend.Vm,
                                "llvm" => ProjectBackend.Llvm,
                                "x64" => ProjectBackend.X64,
                                _ => Bad(value, "`backend` is `vm`, `llvm` or `x64`", ProjectBackend.Vm),
                            };
                            break;
                        case "target":
                            Target = value.Display switch
                            {
                                "native" => ProjectTarget.Native,
                                "wasm" => ProjectTarget.Wasm,
                                var platform when Platforms.Contains(platform) => ProjectTarget.Native,
                                _ => Bad(value, $"`target` is `native`, `wasm` or a platform ({string.Join(", ", Platforms)})", ProjectTarget.Native),
                            };
                            Platform = Platforms.Contains(value.Display) ? value.Display : null;
                            break;
                        case "cpu":
                            Cpu = value.Display is { Length: > 0 } processor && !processor.Contains(' ') ? processor
                                : Bad<string?>(value, "`cpu` is `generic`, `native` or an LLVM processor name (`x86-64-v3`)", null);
                            break;
                        case "optimization":
                            Optimization = value is QueenNumber { IsInteger: true, Value: >= 0 and <= 3 } level ? (int)level.Value
                                : Bad(value, "`optimization` is a level from 0 to 3", 2);
                            break;
                        case "stdlib":
                            Stdlib = value is QueenIdentifier { Name: "none" } ? null : Path.GetFullPath(Path.Combine(Folder, value.Display));
                            stdlibSet = true;
                            break;
                        case "output":
                            Output = Path.GetFullPath(Path.Combine(Folder, value.Display));
                            break;
                        case "ignore":
                            Ignore.AddRange(value is QueenList list ? list.Items.Select(i => i.Display) : [value.Display]);
                            break;
                        case "reflection":
                            Reflection = value.Display switch
                            {
                                "none" => ReflectionLevel.None,
                                "layout" => ReflectionLevel.Layout,
                                "full" => ReflectionLevel.Full,
                                _ => Bad(value, "`reflection` is `none`, `layout` or `full`", ReflectionLevel.Layout),
                            };
                            break;
                        case "borrowcheck":
                            BorrowCheck = value.Display switch
                            {
                                "enforce" => BorrowCheckMode.Enforce,
                                "warn" => BorrowCheckMode.Warn,
                                "off" => BorrowCheckMode.Off,
                                _ => Bad(value, "`borrowcheck` is `enforce`, `warn` or `off`", BorrowCheckMode.Enforce),
                            };
                            break;
                        case "access.default":
                            DefaultAccess = value.Display is "public" or "private" or "internal" or "friendly" or "inherited" or "file"
                                ? value.Display
                                : Bad<string?>(value, "`access.default` is `public`, `private`, `internal`, `friendly`, `inherited` or `file`", null);
                            break;
                        case "generics":
                            Generics = value.Display switch
                            {
                                "shared" => GenericsMode.Shared,
                                "specialize" => GenericsMode.Specialize,
                                _ => Bad(value, "`generics` is `shared` or `specialize`", GenericsMode.Shared),
                            };
                            break;
                        default:
                            break; // the profile reported it (QN121)
                    }
                }
            }
            var hive = new HiveSettings();
            foreach (var entry in Document.Named("HIVE").SelectMany(c => c.Entries))
            {
                var value = entry.Value;
                switch (entry.Key)
                {
                    case "provider":
                        hive = hive with { Providers = [.. hive.Providers, .. (value is QueenList list ? list.Items.Select(i => i.Display) : [value.Display])] };
                        break;
                    case "package":
                        if (PackageReference.Parse(value.Display, value.Line, value.Column) is { Version: null } published)
                            hive = hive with { Package = published };
                        else
                            Report(diagnostics, "QN124", $"`package` is `Author/Name` (no version: PROJ's `version` is the one published), not `{value.Display}`", value);
                        break;
                    case "license":
                        hive = hive with { License = value.Display };
                        break;
                    case "publish":
                        hive = hive with
                        {
                            PublishSources = value.Display switch
                            {
                                "compiled" => false,
                                "source" => true,
                                _ => Bad(value, "`publish` is `compiled` or `source`", false),
                            },
                        };
                        break;
                    case "sign":
                        hive = hive with { SigningKey = Path.GetFullPath(Path.Combine(Folder, value.Display)) };
                        break;
                    case "verify":
                        hive = hive with
                        {
                            RequireSignatures = value.Display switch
                            {
                                "strict" => true,
                                "hashes" => false,
                                _ => Bad(value, "`verify` is `strict` or `hashes`", true),
                            },
                        };
                        break;
                    case "cache":
                        hive = hive with { Cache = Path.GetFullPath(Path.Combine(Folder, value.Display)) };
                        break;
                    case "private":
                        hive = hive with { Private = value is QueenBool { Value: true } };
                        break;
                    default:
                        break; // the profile reported it (QN121)
                }
            }
            if (hive.Providers.Length == 0)
                hive = hive with { Providers = HiveSettings.Defaults() };
            foreach (var entry in Document.Named("TOOLS").SelectMany(c => c.Entries))
            {
                if (entry.Key != "tool")
                    continue; // the profile reported it (QN121)
                foreach (var value in entry.Value is QueenList listed ? listed.Items : [entry.Value])
                {
                    if (PackageReference.Parse(value.Display, value.Line, value.Column) is { } tool)
                        Tools.Add(tool);
                    else
                        Report(diagnostics, "QN104", $"`tool` is `Author/Name`, optionally with a version (`Author/Name@1.2`, `@^1.2`), not `{value.Display}`", value);
                }
            }
            Hive = hive;

            if (!stdlibSet)
                Stdlib = Environment.GetEnvironmentVariable("LARVA_STDLIB") is { } fromEnvironment ? Path.GetFullPath(fromEnvironment) : null;

            // A value the profile rejected (QN124, or this machine's defaults, which `larva config` checked): the default.
            static T Bad<T>(QueenValue value, string message, T fallback) => fallback;
        }

        private bool stdlibSet;

        /// <summary>Whether <paramref name="name"/> can be a tool's command: lower-case letters, digits and `-`, starting with a letter.</summary>
        public static bool IsCommandName(string name) =>
            name.Length is > 0 and <= 64 && name[0] is >= 'a' and <= 'z' && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

        // ── Sources ────────────────────────────────────────────

        /// <summary>The values of a key in every chunk of a kind, a list's items one by one.</summary>
        private IEnumerable<QueenValue> Values(string chunk, string key) =>
            Document.Named(chunk).SelectMany(c => c.Entries).Where(e => e.Key == key)
                .SelectMany(e => e.Value is QueenList list ? list.Items : [e.Value]);

        private void FindSources(List<QueenDiagnostic> diagnostics)
        {
            Ignore.AddRange(Values("SOURCES", "exclude").Select(v => v.Display));
            bool scan = Values("SOURCES", "scan").LastOrDefault() is not QueenBool { Value: false };
            var patterns = Ignore.Select(p => p.Replace('\\', '/').Trim('/')).Where(p => p.Length > 0).ToList();
            if (scan)
                Walk(Folder, top: true);
            var seen = new HashSet<string>(Sources, StringComparer.OrdinalIgnoreCase);
            foreach (var include in Values("SOURCES", "include"))
            {
                var found = Expand(Folder, include.Display, trees).Where(IsSource).ToList();
                if (found.Count == 0)
                    Report(diagnostics, "QN130", $"`include: {include.Display}` names no source file ({string.Join(", ", SourceExtensions)})", include, error: false);
                foreach (var file in found.Where(f => !Ignored(Relative(f)) && seen.Add(f)))
                    Sources.Add(file);
            }
            return;

            void Walk(string folder, bool top)
            {
                // Each folder listed once (its files, its subfolders and whether they are links). A folder this user may
                // not read (a protected system folder under a home directory) holds nothing of the project.
                List<(string Path, bool Folder, bool Link)> entries;
                try
                {
                    entries = [.. new System.IO.Enumeration.FileSystemEnumerable<(string, bool, bool)>(folder,
                        (ref System.IO.Enumeration.FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
                        new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 })];
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                {
                    return;
                }
                // A nested project (a subfolder with its own project file) is not part of this one.
                if (!top && entries.Any(e => !e.Folder && Profiles.ProfileCatalog.IsProjectFile(e.Path)))
                    return;
                foreach (var (file, _, _) in entries.Where(e => !e.Folder && IsSource(e.Path)).OrderBy(e => e.Path, StringComparer.Ordinal))
                {
                    if (!Ignored(Relative(file)))
                        Sources.Add(Path.GetFullPath(file));
                }
                foreach (var (sub, _, link) in entries.Where(e => e.Folder).OrderBy(e => e.Path, StringComparer.Ordinal))
                {
                    // Hidden folders, build output, packages, and links to other folders are not part of this one.
                    if (Pruned(Path.GetFileName(sub)) || link || Ignored(Relative(sub)))
                        continue;
                    Walk(sub, top: false);
                }
            }
            string Relative(string path) => Path.GetRelativePath(Folder, path).Replace('\\', '/');
            bool Ignored(string relative) => patterns.Any(p => Matches(relative, p));
        }

        /// <summary><c>FILES</c> (copied next to the build) and <c>RESOURCES</c> (built in), expanded to files.</summary>
        private void ReadFiles(List<QueenDiagnostic> diagnostics)
        {
            string given = Values("FILES", "into").LastOrDefault()?.Display.Replace('\\', '/') ?? "";
            string into = given.Trim('/');
            if (into.Split('/').Contains("..") || given.StartsWith('/') || Path.IsPathRooted(given) || into.Contains(':'))
            {
                // Relative and inside: not `..`, not `/x`, not `C:/x` (a rooted path on any system, whichever this one is).
                Report(diagnostics, "QN131", "`into` is a folder under the build's: it cannot climb out with `..` or be an absolute path", Values("FILES", "into").Last());
                into = "";
            }
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var copy in Values("FILES", "copy"))
            {
                var found = Expand(Folder, copy.Display, trees);
                if (found.Count == 0)
                    Report(diagnostics, "QN130", $"`copy: {copy.Display}` names no file", copy, error: false);
                foreach (var file in found)
                {
                    // A file keeps its place under the project; one from outside it goes to the top.
                    string relative = Path.GetRelativePath(Folder, file).Replace('\\', '/');
                    if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                        relative = Path.GetFileName(file);
                    string target = into.Length > 0 ? $"{into}/{relative}" : relative;
                    if (targets.Add(target))
                        CopiedFiles.Add((file, target));
                }
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Document.Named("RESOURCES").SelectMany(c => c.Entries))
            {
                var items = entry.Value is QueenList list ? list.Items : [entry.Value];
                foreach (var item in items)
                {
                    var found = Expand(Folder, item.Display, trees);
                    if (found.Count == 0)
                    {
                        Report(diagnostics, "QN130", $"`{entry.Key}: {item.Display}` names no file", item);
                        continue;
                    }
                    if (entry.Key != "embed" && found.Count > 1)
                    {
                        Report(diagnostics, "QN132", $"a named resource (`{entry.Key}: …`) is one file; `embed:` takes patterns", item);
                        continue;
                    }
                    foreach (var file in found)
                    {
                        // `embed`: named by its path under the project; `name: file`: by that name.
                        string name = entry.Key != "embed" ? entry.Key : Path.GetRelativePath(Folder, file).Replace('\\', '/');
                        if (name.StartsWith("../", StringComparison.Ordinal))
                            name = Path.GetFileName(file);
                        if (names.Add(name))
                            Resources.Add((name, file));
                        else
                            Report(diagnostics, "QN133", $"two resources are named `{name}`", item);
                    }
                }
            }
        }

        /// <summary>
        /// The files a path or pattern names, relative to <paramref name="folder"/> (or absolute): a file, every file under a
        /// folder, or a pattern where <c>*</c> and <c>?</c> stay within a folder and <c>**</c> crosses folders. Sorted.
        /// </summary>
        public static List<string> Expand(string folder, string pattern) => Expand(folder, pattern, null);

        /// <summary>The folders this project's patterns walked (each once, whatever the number of patterns under it).</summary>
        private readonly FileTrees trees = new();

        private static List<string> Expand(string folder, string pattern, FileTrees? trees)
        {
            trees ??= new FileTrees();
            pattern = pattern.Replace('\\', '/').Trim();
            if (pattern.Length == 0)
                return [];
            // Packages (`node_modules`) are only searched by a pattern that names them.
            bool packages = pattern.Contains("node_modules", StringComparison.OrdinalIgnoreCase);
            if (pattern.IndexOfAny(['*', '?']) < 0)
            {
                string path = Path.GetFullPath(Path.Combine(folder, pattern));
                if (System.IO.File.Exists(path))
                    return [path];
                if (Directory.Exists(path))
                    return [.. trees.Under(path, packages).Select(f => f.File).OrderBy(f => f, StringComparer.Ordinal)];
                return [];
            }
            // The fixed folders before the first wildcard are where the search starts.
            var parts = pattern.Split('/');
            int fixedParts = Array.FindIndex(parts, p => p.IndexOfAny(['*', '?']) >= 0);
            string root = Path.GetFullPath(Path.Combine(folder, string.Join('/', parts[..fixedParts])));
            if (!Directory.Exists(root))
                return [];
            string rest = string.Join('/', parts[fixedParts..]);
            var regex = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(rest)
                .Replace("\\*\\*/", "(.*/)?").Replace("\\*\\*", ".*").Replace("\\*", "[^/]*").Replace("\\?", "[^/]") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return [.. trees.Under(root, packages).Where(f => regex.IsMatch(f.Relative)).Select(f => f.File).OrderBy(f => f, StringComparer.Ordinal)];
        }

        /// <summary>
        /// A folder a walk or a pattern never enters: hidden folders, build output (<c>bin</c>, <c>obj</c>) and, unless
        /// <paramref name="packages"/>, packages (<c>node_modules</c>).
        /// </summary>
        private static bool Pruned(string name, bool packages = false) =>
            name.StartsWith('.') || name is "bin" or "obj" || !packages && name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The files under folders, each folder walked once, without what <see cref="Pruned"/> leaves out (nor hidden files,
        /// nor files named <c>bin</c> or <c>obj</c>): a folder inside one already walked is read from that walk.
        /// </summary>
        private sealed class FileTrees
        {
            private readonly List<(string Root, bool Packages, List<string> Files)> walked = [];

            /// <summary>The files under <paramref name="root"/> (a full path), with their paths relative to it (with <c>/</c>).</summary>
            public IEnumerable<(string File, string Relative)> Under(string root, bool packages)
            {
                string prefix = WithSeparator(root);
                foreach (var (top, withPackages, files) in walked)
                {
                    string topPrefix = WithSeparator(top);
                    if (withPackages != packages || !prefix.StartsWith(topPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // A folder below one walked: its files are in that walk, unless a folder between was left out.
                    string between = prefix[topPrefix.Length..].Replace('\\', '/').Trim('/');
                    if (between.Length > 0 && between.Split('/').Any(p => Pruned(p, packages)))
                        continue;
                    return files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(f => (f, f[prefix.Length..].Replace('\\', '/')));
                }
                var found = new System.IO.Enumeration.FileSystemEnumerable<string>(root, (ref System.IO.Enumeration.FileSystemEntry e) => e.ToFullPath(),
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 })
                {
                    ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => !e.IsDirectory && !Hidden(e.FileName),
                    ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => !Pruned(e.FileName.ToString(), packages),
                }.ToList();
                walked.Add((root, packages, found));
                return found.Select(f => (f, f[prefix.Length..].Replace('\\', '/')));
            }

            // Hidden files, and files named as build output, are never picked up by a folder or a pattern.
            private static bool Hidden(ReadOnlySpan<char> name) =>
                name.StartsWith('.') || name.SequenceEqual("bin") || name.SequenceEqual("obj");

            private static string WithSeparator(string folder) =>
                folder.EndsWith(Path.DirectorySeparatorChar) || folder.EndsWith(Path.AltDirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        }

        /// <summary>A path (relative to the project) matches an ignore pattern: the same path, or a <c>*</c> wildcard over it.</summary>
        private static bool Matches(string path, string pattern)
        {
            if (!pattern.Contains('*'))
                return string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase);
            var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(path, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

    }
}
