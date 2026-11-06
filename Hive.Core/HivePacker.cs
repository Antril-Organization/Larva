using System.Text;
using Larva.Build;

namespace Hive
{
    /// <summary>
    /// Makes a package from a library project. A compiled package (the default, and the lightest) carries the project's
    /// Larva objects (<c>lib/</c>), not its sources; <c>publish: source</c> adds them (<c>src/</c>). Either way it carries the
    /// native libraries it needs (<c>native/</c>) and its resources, and a <c>package.queen</c> that describes it as a library
    /// project, which is what a program that uses the package compiles with.
    /// </summary>
    public static class HivePacker
    {
        public const string ProjectFile = "package.queen";

        /// <summary>The name a project publishes as: HIVE's <c>package</c>, else PROJ's <c>author</c> and <c>name</c>.</summary>
        public static PackageReference NameOf(QueenProject project)
        {
            if (project.Hive.Package is { } named)
                return named;
            string author = (project.Author ?? "").Replace(' ', '-');
            return PackageReference.Parse($"{author}/{project.Name}") is { Version: null } fromProject ? fromProject
                : throw new HiveException($"the package needs a name: set `package: Author/Name` in `chunk HIVE:` (PROJ's author `{project.Author}` and name `{project.Name}` do not make one)");
        }

        /// <summary>
        /// Writes the package of <paramref name="project"/> to <paramref name="output"/>. <paramref name="compiled"/>: the
        /// folder with its <c>.lvo</c> files (from <c>larva build</c>). <paramref name="gc"/>: the GC mode they were built for.
        /// </summary>
        public static HiveManifest Pack(QueenProject project, string compiled, string output, string? gc, string? privatePem)
        {
            if (project.Kind != ProjectKind.Library)
                throw new HiveException($"{project.Name} is `type: exe`: packages are libraries");
            if (project.Hive.Private)
                throw new HiveException($"{project.Name} is private (`private: true` in HIVE)");
            if (project.Version is not { } version)
                throw new HiveException($"{project.Name} has no `version` in PROJ: packages are published by version");
            var name = NameOf(project);
            bool sources = project.Hive.PublishSources;

            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string inPackage, string source)
            {
                if (!HiveArchive.SafePath(inPackage))
                    throw new HiveException($"`{inPackage}` cannot be a path in a package (rename it)");
                if (!files.TryAdd(inPackage, source) && !string.Equals(files[inPackage], source, StringComparison.OrdinalIgnoreCase))
                    throw new HiveException($"two files would be `{inPackage}` in the package");
            }

            // The code: the library compiled (a Larva library, `.dll`, in `compiled`; or Larva objects, `.lvo`), or its
            // sources; and the Larva libraries it uses (`assembly:`, `larvaobj:` in DEPS), compiled as they are.
            var lvos = Directory.Exists(compiled) ? Directory.GetFiles(compiled, "*.lvo").OrderBy(f => f, StringComparer.Ordinal).ToList() : [];
            var assemblies = new List<string>();
            if (Directory.Exists(compiled))
                assemblies.AddRange(Directory.GetFiles(compiled).Where(QueenProject.IsAssembly).OrderBy(f => f, StringComparer.Ordinal));
            assemblies.AddRange(project.AllLibraries().Where(QueenProject.IsAssembly));
            if (!sources && lvos.Count == 0 && assemblies.Count == 0)
                throw new HiveException($"nothing compiled to pack: no Larva library in {compiled} and no `assembly:` in DEPS");
            if (sources)
            {
                foreach (var source in project.AllSources())
                {
                    string relative = Path.GetRelativePath(project.Folder, source).Replace('\\', '/');
                    if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                        relative = $"shared/{HiveManifest.Hash(Encoding.UTF8.GetBytes(Path.GetDirectoryName(source)!))[..8]}/{Path.GetFileName(source)}";
                    Add($"src/{relative}", source);
                }
            }
            else
            {
                foreach (var lvo in lvos)
                    Add($"lib/{Path.GetFileName(lvo)}", lvo);
            }
            foreach (var library in project.AllLibraries().Where(l => !QueenProject.IsAssembly(l)))
                foreach (var lvo in File.Exists(library) ? [library] : Directory.GetFiles(library, "*.lvo"))
                    Add($"lib/{Path.GetFileName(lvo)}", lvo);
            var assemblyFiles = new List<string>();
            foreach (var assembly in assemblies.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string inPackage = $"lib/{Path.GetFileName(assembly)}";
                Add(inPackage, assembly);
                assemblyFiles.Add(inPackage);
            }
            var natives = new List<string>();
            foreach (var native in project.AllNativeLibraries())
            {
                string inPackage = $"native/{Path.GetFileName(native)}";
                Add(inPackage, native);
                natives.Add(inPackage);
            }
            var resources = new List<(string Key, string Path)>();
            foreach (var (resourceName, file) in project.AllResources())
            {
                // A resource named by its path keeps it; one named by the project (`logo: …`) goes to named/.
                bool byPath = resourceName.Contains('/') || resourceName.Contains('.');
                string inPackage = byPath ? resourceName : $"named/{resourceName}";
                if (inPackage.StartsWith("lib/", StringComparison.Ordinal) || inPackage.StartsWith("src/", StringComparison.Ordinal) || inPackage.StartsWith("native/", StringComparison.Ordinal) || inPackage == ProjectFile)
                    throw new HiveException($"resource `{resourceName}` would collide with the package's own files: rename it");
                Add(inPackage, file);
                resources.Add((byPath ? "embed" : resourceName, inPackage));
            }

            string versionText = VersionRange.Format(version);
            var dependencies = project.Packages.Select(p => new HiveDependency(p.Id, p.Version?.ToString() ?? "")).ToArray();
            string queen = Path.Combine(Path.GetTempPath(), $"hive-{Guid.NewGuid():N}.queen");
            bool hasObjects = (!sources && lvos.Count > 0) || project.AllLibraries().Any(l => !QueenProject.IsAssembly(l));
            File.WriteAllText(queen, ProjectText(name, versionText, sources, hasObjects, assemblyFiles, natives, dependencies, resources));
            try
            {
                Add(ProjectFile, queen);
                var manifest = new HiveManifest
                {
                    Package = name.Id,
                    Version = versionText,
                    Description = project.Description,
                    License = project.Hive.License,
                    Kind = sources ? "source" : "compiled",
                    Gc = sources ? null : gc,
                    Dependencies = dependencies,
                };
                return HiveArchive.Create(output, manifest, files, privatePem);
            }
            finally
            {
                File.Delete(queen);
            }
        }

        private static string ProjectText(PackageReference name, string version, bool sources, bool hasLibraries, List<string> assemblies,
            List<string> natives, HiveDependency[] dependencies, List<(string Key, string Path)> resources)
        {
            var text = new StringBuilder();
            text.Append("queen 1.0;\n\n");
            text.Append($"chunk PROJ:\n   name: {name.Name},\n   type: library,\n   version: v{version},\n   author: {name.Author}\n\n");
            text.Append(sources ? "chunk SOURCES:\n   scan: false,\n   include: src/**\n\n" : "chunk SOURCES:\n   scan: false\n\n");
            var deps = new List<string>();
            if (hasLibraries)
                deps.Add("larvaobj: lib");
            deps.AddRange(assemblies.Select(a => $"assembly: {Quote(a)}"));
            deps.AddRange(natives.Select(n => $"lib: {Quote(n)}"));
            deps.AddRange(dependencies.Select(d => $"package: {d.Package}{(d.Version.Length > 0 ? "@" + d.Version : "")}"));
            if (deps.Count > 0)
                text.Append("chunk DEPS:\n   ").Append(string.Join(",\n   ", deps)).Append("\n\n");
            if (resources.Count > 0)
                text.Append("chunk RESOURCES:\n   ").Append(string.Join(",\n   ", resources.Select(r => $"{r.Key}: {Quote(r.Path)}"))).Append('\n');
            return text.ToString();
        }

        private static string Quote(string path) => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
