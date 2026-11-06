namespace Larva.Build
{
    /// <summary>A key of a chunk: what it sets, and the words it takes (empty when it takes free text, a number or a path).</summary>
    public sealed record QueenKey(string Name, string Description, string[] Values, string? Example = null);

    /// <summary>A chunk a project file can have, and its keys.</summary>
    public sealed record QueenChunkSchema(string Name, string Description, QueenKey[] Keys);

    /// <summary>
    /// What a <c>.queen</c> file can say: its chunks, their keys and the values each takes. The project reader checks keys
    /// against it, and the editor completes and explains them from it.
    /// </summary>
    public static class QueenSchema
    {
        public static readonly string[] Platforms = ["windows-x64", "linux-x64", "linux-arm64", "macos-x64", "macos-arm64"];

        public static readonly QueenChunkSchema Proj = new("PROJ", "The project: its name, kind and version.",
        [
            new("name", "The project's name (the name of its executable or library).", [], "MyApp"),
            new("type", "What the project builds: a program or a library.", ["exe", "library"]),
            new("version", "The project's version.", [], "v1.0"),
            new("author", "Who wrote it.", [], "My Organization"),
            new("description", "What it is (no commas: a comma ends the entry).", []),
        ]);

        public static readonly QueenChunkSchema Build = new("BUILD", "How the project is compiled: back end, memory management and the language features it allows.",
        [
            new("gc", "How memory is managed: `default` (tracing collector), `manual` (`delete`), `refcount`, `arena`, or `custom` (a collector the project writes, see `gc.collector`).",
                ["default", "tracing", "manual", "refcount", "arena", "custom"]),
            new("gc.collector", "With `gc: custom`: the class that implements `System.IGarbageCollector`.", [], "MyApp.Memory.Collector"),
            new("backend", "What runs or compiles the program: the VM, LLVM or the x64 back end.", ["vm", "llvm", "x64"]),
            new("target", "Where it runs: this machine (`native`), WebAssembly, or a platform.", ["native", "wasm", .. Platforms]),
            new("optimization", "Optimization level, 0 to 3.", ["0", "1", "2", "3"]),
            new("cpu", "The processor the LLVM back end compiles for: `generic` (any of the platform: portable), `native` (this machine's, with all it has: AVX2 and more; the program may not run on an older processor), or an LLVM name (`x86-64-v3`, `znver3`…).",
                ["generic", "native", "x86-64-v2", "x86-64-v3", "x86-64-v4"]),
            new("stdlib", "The standard library: a folder, or `none`.", ["none"], "../StandardLibrary"),
            new("output", "Where the build goes.", [], "bin/MyApp.exe"),
            new("ignore", "Files or folders left out of the build.", [], "[tests, drafts]"),
            new("reflection", "How much types describe of themselves at run time.", ["none", "layout", "full"]),
            new("borrowcheck", "The borrow checker: errors (`enforce`), warnings (`warn`) or nothing (`off`).", ["enforce", "warn", "off"]),
            new("unsafe", "Whether the project's code may use `unsafe` (pointers): `allow` or `deny`.", ["allow", "deny"]),
            new("generics", "How generic code is compiled: shared, or one copy per type (`specialize`).", ["shared", "specialize"]),
            new("access.default", "The access of declarations that say none.", ["public", "private", "internal", "friendly", "inherited", "file"]),
        ]);

        public static readonly QueenChunkSchema Deps = new("DEPS", "What this project uses: packages, projects, compiled Larva objects and native libraries (each key any number of times).",
        [
            new("package", "A Hive package: `Author/Name`, or with a version: `Author/Name@1.2` (exactly), `@^1.2` (compatible), `@>=1.2`. Installed with `larva hive install`.", [], "Acme/Json@^1.0"),
            new("proj", "A project compiled with this one: its `.queen` file or its folder.", [], "../Shared/Shared.queen"),
            new("larvaobj", "A compiled Larva object (`.lvo`), or a folder of them: linked, not compiled again.", [], "libs/Geometry.lvo"),
            new("assembly", "A compiled Larva library (`.dll`, or a bare `.lva`, such as the bindings `larva bind` writes): compiled against and linked, with no source.", [], "bindings/SDL3.dll"),
            new("lib", "A native library (`.dll`, `.so`, `.dylib`, `.wasm`) the code imports from: copied next to the build and found at run time.", [], "native/zlib.dll"),
        ]);

        public static readonly QueenChunkSchema Sources = new("SOURCES", "Which source files are the project's (by default, every one of an installed language under its folder).",
        [
            new("include", "More source files: a file, a folder or a pattern (`*` within a folder, `**` across folders), also outside the project's folder.", [], "../Shared/**"),
            new("exclude", "Files or folders left out (like BUILD `ignore`).", [], "drafts/**"),
            new("scan", "Whether the project's own folder is searched for source files (`false`: only what `include` names).", ["true", "false"]),
        ]);

        public static readonly QueenChunkSchema Files = new("FILES", "Files copied next to the build (configuration, assets): paths keep their place under the project.",
        [
            new("copy", "A file, a folder or a pattern to copy (any number of times).", [], "assets/**"),
            new("into", "The folder, under the build's, where the copies go (default: the build's own).", [], "data"),
        ]);

        public static readonly QueenChunkSchema Resources = new("RESOURCES", "Files built into the program, read with `System.Resources.Read(name)`.",
        [
            new("embed", "A file, a folder or a pattern built in, each named by its path under the project (`images/logo.png`).", [], "images/**"),
        ]);

        public static readonly QueenChunkSchema Hive = new("HIVE", "Packaging with Hive: where packages come from and how this project is published.",
        [
            new("provider", "The package registry: an address (`antril.org/hive`, `https://…`) or a folder (a local registry).", [], "antril.org/hive"),
            new("package", "The name this project is published as: `Author/Name` (by default PROJ's `author` and `name`).", [], "Acme/Json"),
            new("license", "The license it is published under (an SPDX name).", [], "MIT"),
            new("publish", "What a package carries: compiled Larva objects only (`compiled`, the lightest) or its sources too (`source`).", ["compiled", "source"]),
            new("sign", "The private key that signs published packages (a file made by `hive key`).", [], "keys/hive.key"),
            new("verify", "How packages are checked when installed: signatures and hashes required (`strict`), hashes only (`hashes`).", ["strict", "hashes"]),
            new("cache", "Where installed packages are kept (by default the user's Hive cache).", [], ".hive"),
            new("private", "`true`: the project is never published.", ["true", "false"]),
        ]);

        public static readonly QueenChunkSchema[] Chunks = [Proj, Build, Deps, Sources, Files, Resources, Hive];

        public static QueenChunkSchema? Chunk(string name) => Chunks.FirstOrDefault(c => c.Name == name);

        public static string[] KeyNames(QueenChunkSchema chunk) => [.. chunk.Keys.Select(k => k.Name)];
    }
}
