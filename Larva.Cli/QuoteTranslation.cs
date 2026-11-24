using System.Globalization;
using Larva.Hir.Lowering;
using Larva.Runtime.Vm;
using H = Larva.Hir;

/// <summary>
/// Quoted functions translated while building. A parameter of type <c>System.Quoted&lt;F&gt;</c> with
/// <c>[System.CompileWith("Ns.Type.Function")]</c> names a function of a library that reads a quoted function's tree (a
/// query's filter, made into SQL). After the HIR is linked, every quoted function passed to such a parameter is found; a
/// small program, added to the linked assembly, calls the translator with each tree (generic over the type arguments of the
/// method's type: <c>Table&lt;User&gt;</c>'s <c>User</c>) and notes what it gives; the program runs in memory (x64; the VM
/// elsewhere). A text that starts with <c>!</c> is an error of the build, at the place the function was written; any other
/// becomes the Quoted's <c>Compiled</c>, a constant, so the library does not translate it while the program runs.
/// </summary>
internal static class QuoteTranslation
{
    private sealed record Site(long Number, string Place, H.NewExpr Quoted, H.MethodDef Translator, int TranslatorType, int TranslatorMethod, H.HirType[] TypeArguments, H.Expr Tree)
    {
        /// <summary>The name of the cache file of its translation (null: not kept).</summary>
        public string? Key { get; set; }
    }

    /// <summary>Translations kept between builds: one file each, named by what makes it (Key).</summary>
    private static string CacheFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Larva", "cache", "quotes");

    /// <summary>A tree as text, for the cache's key (the trees the frontends make: nodes of kind, name, index, children).</summary>
    private static void Canon(H.Expr e, System.Text.StringBuilder text)
    {
        switch (e)
        {
            case H.NewExpr made:
                text.Append('(');
                foreach (var argument in made.Arguments)
                {
                    Canon(argument, text);
                    text.Append(',');
                }
                text.Append(')');
                break;
            case H.ArrayLiteralExpr array:
                text.Append('[');
                foreach (var item in array.Items)
                {
                    Canon(item, text);
                    text.Append(';');
                }
                text.Append(']');
                break;
            case H.LiteralExpr { Value: H.StringConstant s }:
                text.Append('"').Append(s.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                break;
            case H.LiteralExpr { Value: H.IntegerConstant i }:
                text.Append(i.Value.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                text.Append('?').Append(e.GetType().Name);
                break;
        }
    }

    /// <summary>Finds the quoted functions of <paramref name="assembly"/>'s own code (not those of <paramref name="skipped"/>).</summary>
    private sealed class Finder(H.HirAssembly assembly, int quoted, int attribute, List<Site> sites, List<string> problems) : H.HirWalker
    {
        public override void Visit(H.Expr? e)
        {
            if (e is H.CallExpr { Method.Type: >= 0 } call)
                Look(call);
            base.Visit(e);
        }

        private void Look(H.CallExpr call)
        {
            var owner = assembly.Types[call.Method.Type];
            if (call.Method.Member < 0 || call.Method.Member >= owner.Methods.Count)
                return;
            var method = owner.Methods[call.Method.Member];
            for (int a = 0; a < call.Arguments.Length && a < method.Parameters.Count; a++)
            {
                if (call.Arguments[a] is not H.NewExpr { Type: H.NamedType { Type.Index: var made }, Arguments.Length: 6 } argument || made != quoted)
                    continue;
                var with = method.Parameters[a].Attributes.FirstOrDefault(x => x.Constructor.Type == attribute);
                if (with is not { Arguments: [H.StringConstant name] })
                    continue;
                if (argument.Arguments[1] is not H.CallExpr { Arguments: [_, H.LambdaExpr { Body: H.ReturnStmt { Value: { } tree } }] }
                    || argument.Arguments[3] is not H.LiteralExpr { Value: H.IntegerConstant number }
                    || argument.Arguments[4] is not H.LiteralExpr { Value: H.StringConstant place })
                    continue;
                if (Translator(name.Value) is not var (type, index, translator))
                {
                    problems.Add($"{place.Value}: error: [CompileWith(\"{name.Value}\")]: no static function {name.Value}(QuoteNode tree) : str");
                    continue;
                }
                // The translator's type arguments: those of the method's type (`Table<User>`: User), when it takes as many.
                H.HirType[] arguments = translator.Generics.Count == 0 ? [] : call.Receiver?.Type is H.NamedType { Arguments: var given } && given.Length == translator.Generics.Count ? given : null!;
                // Inside generic code (`T` not known yet): translated while the program runs.
                if (arguments is null || arguments.Any(Generic))
                    continue;
                sites.Add(new Site((long)number.Value, place.Value, argument, translator, type, index, arguments, tree));
            }
        }

        private (int Type, int Index, H.MethodDef Method)? Translator(string name)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0)
                return null;
            string typeName = name[..dot], methodName = name[(dot + 1)..];
            for (int t = 0; t < assembly.Types.Count; t++)
            {
                if (assembly.Types[t].FullName != typeName)
                    continue;
                var methods = assembly.Types[t].Methods;
                for (int m = 0; m < methods.Count; m++)
                    if (methods[m].Name == methodName && methods[m].IsStatic && methods[m].Parameters.Count == 1)
                        return (t, m, methods[m]);
            }
            return null;
        }

        private static bool Generic(H.HirType type) => type switch
        {
            H.GenericParamType => true,
            H.NamedType named => named.Arguments.Any(Generic),
            H.ArrayType array => Generic(array.Element),
            H.NullableType nullable => Generic(nullable.Inner),
            _ => false,
        };
    }

    /// <summary>
    /// Translates the quoted functions of <paramref name="assembly"/> (linked) whose parameters name a translator: false
    /// (the errors printed) when one cannot be translated or the translators do not run.
    /// </summary>
    /// <param name="inputs">The bytes of each input assembly (by name): a translation is kept between builds, keyed by
    /// its translator's assembly's, the type arguments and the tree.</param>
    public static bool Run(H.HirAssembly assembly, ISet<string> libraries, IReadOnlyDictionary<string, byte[]> inputs, Action<string> mark)
    {
        int quoted = assembly.Types.FindIndex(t => t.FullName == "System.Quoted");
        int attribute = assembly.Types.FindIndex(t => t.FullName == "System.CompileWithAttribute");
        int quotes = assembly.Types.FindIndex(t => t.FullName == "System.Quotes");
        if (quoted < 0 || attribute < 0 || quotes < 0)
            return true;
        var sites = new List<Site>();
        var problems = new List<string>();
        var finder = new Finder(assembly, quoted, attribute, sites, problems);
        foreach (var origin in assembly.Origins)
        {
            // The libraries' own code (System.Core, System.Data…) was checked when they were built.
            if (libraries.Contains(origin.Assembly))
                continue;
            for (int t = origin.FirstType; t < origin.FirstType + origin.TypeCount; t++)
                foreach (var method in assembly.Types[t].Methods)
                    if (method.Body is { } body)
                        finder.Visit(body.Block);
        }
        foreach (var problem in problems)
            Console.Error.WriteLine(problem);
        if (sites.Count == 0)
            return problems.Count == 0;

        // Kept from an earlier build: the same translator (its assembly unchanged), type arguments and tree.
        var results = new Dictionary<long, string>();
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var site in sites)
        {
            var origin = assembly.Origins.FirstOrDefault(o => site.TranslatorType >= o.FirstType && site.TranslatorType < o.FirstType + o.TypeCount);
            if (origin.Assembly is null || !inputs.TryGetValue(origin.Assembly, out byte[]? bytes))
                continue;
            if (!fingerprints.TryGetValue(origin.Assembly, out string? fingerprint))
                fingerprints[origin.Assembly] = fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var key = new System.Text.StringBuilder(fingerprint).Append('|').Append(assembly.Types[site.TranslatorType].FullName).Append('.').Append(site.Translator.Name).Append('|');
            foreach (var argument in site.TypeArguments)
                key.Append(H.Text.HirPrinter.Print(argument, assembly)).Append(',');
            key.Append('|');
            Canon(site.Tree, key);
            site.Key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.ToString())))[..32];
            string kept = Path.Combine(CacheFolder, site.Key + ".txt");
            try
            {
                if (File.Exists(kept))
                    results[site.Number] = File.ReadAllText(kept);
            }
            catch (IOException)
            {
            }
        }
        var missing = sites.Where(s => !results.ContainsKey(s.Number)).ToList();
        if (missing.Count == 0)
        {
            mark("quoted functions (kept)");
            return Apply(sites, results) && problems.Count == 0;
        }

        // The program: Quotes.Note(site, Translator<…>(tree)) for each, then Quotes.Save(file).
        var quotesType = assembly.Types[quotes];
        int note = quotesType.Methods.FindIndex(m => m.Name == "Note" && m.IsStatic);
        int save = quotesType.Methods.FindIndex(m => m.Name == "Save" && m.IsStatic);
        if (note < 0 || save < 0)
            return true;
        var text = quotesType.Methods[note].Parameters[1].Type;
        string file = Path.Combine(Path.GetTempPath(), $"larva-quotes-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
        var statements = new List<H.Stmt>();
        foreach (var site in missing)
        {
            var translate = new H.CallExpr(null, H.MemberToken.Def(site.TranslatorType, site.TranslatorMethod), site.TypeArguments, [site.Tree], text);
            statements.Add(new H.ExprStmt(new H.CallExpr(null, H.MemberToken.Def(quotes, note), [], [new H.LiteralExpr(new H.IntegerConstant(site.Number, H.Primitive.I64), H.HirType.Of(H.Primitive.I64)), translate], H.HirType.Void)));
        }
        statements.Add(new H.ExprStmt(new H.CallExpr(null, H.MemberToken.Def(quotes, save), [], [new H.LiteralExpr(new H.StringConstant(file), text)], H.HirType.Void)));
        statements.Add(new H.ReturnStmt(new H.LiteralExpr(new H.IntegerConstant(0, H.Primitive.I32), H.HirType.I32)));
        var run = new H.MethodDef("$TranslateQuotes", H.HirType.I32)
        {
            Flags = H.MethodFlags.Static | H.MethodFlags.Generated,
            Access = H.Access.Public,
            Body = new H.HirBody([], new H.BlockStmt([.. statements])),
        };
        var entry = assembly.EntryPoint;
        quotesType.Methods.Add(run);
        assembly.EntryPoint = H.MemberToken.Def(quotes, quotesType.Methods.Count - 1);
        string? output;
        try
        {
            output = Execute(assembly, assembly.Origins.Select(o => o.Assembly).ToHashSet(), file);
        }
        finally
        {
            quotesType.Methods.RemoveAt(quotesType.Methods.Count - 1);
            assembly.EntryPoint = entry;
        }
        mark("quoted functions");
        if (output is null)
            return false;

        foreach (string line in output.Split('\n'))
        {
            int tab = line.IndexOf('\t');
            if (tab > 0 && long.TryParse(line[..tab], NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
                results[number] = line[(tab + 1)..].Replace("\\n", "\n").Replace("\\\\", "\\");
        }
        // Kept for the next builds (errors too: the same tree gives the same error).
        foreach (var site in missing)
        {
            if (site.Key is null || !results.TryGetValue(site.Number, out string? made))
                continue;
            try
            {
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllText(Path.Combine(CacheFolder, site.Key + ".txt"), made);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return Apply(sites, results) && problems.Count == 0;
    }

    /// <summary>Each site's translation into its Quoted (Compiled), or its error printed: false when there is one.</summary>
    private static bool Apply(List<Site> sites, Dictionary<long, string> results)
    {
        bool ok = true;
        foreach (var site in sites)
        {
            if (!results.TryGetValue(site.Number, out string? result))
                continue;
            if (result.StartsWith('!'))
            {
                Console.Error.WriteLine($"{site.Place}: error: {result[1..]}");
                ok = false;
                continue;
            }
            // The Quoted's last argument (Compiled): the translation, a constant.
            var slot = site.Quoted.Arguments[5].Type;
            H.Expr literal = new H.LiteralExpr(new H.StringConstant(result), slot is H.NullableType { Inner: var inner } ? inner : slot);
            site.Quoted.Arguments[5] = slot is H.NullableType ? new H.ConvertExpr(literal, H.ConversionKind.Wrap, slot) : literal;
        }
        return ok;
    }

    /// <summary>The added program run (in memory on x64, else on the VM): the notes it saved, or null (the errors printed).</summary>
    private static unsafe string? Execute(H.HirAssembly assembly, HashSet<string> reachedOnly, string file)
    {
        var lowered = HirLowerer.Lower(assembly, new LoweringOptions { StaticConstructorsFromEntry = true, ReachedOnly = reachedOnly });
        if (!lowered.Success)
        {
            foreach (var error in lowered.Errors)
                Console.Error.WriteLine($"larva: translating quoted functions: {error}");
            return null;
        }
        string function = lowered.FunctionName(assembly.EntryPoint!.Value);
        var options = Larva.IR.CompilationOptions.Default with { Entry = function, BorrowCheck = Larva.IR.BorrowCheckMode.Off };
        try
        {
            if (OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
            {
                var verified = Larva.IR.Compiler.CompileObject(lowered.Module!, options);
                var program = verified.Success ? Larva.IR.Linking.Linker.Link([verified.Object!], new Larva.IR.Linking.LinkOptions { Entry = function, Compilation = options, LowerToIl = false }) : null;
                if (program is not { Success: true })
                {
                    foreach (var error in verified.TypeErrors.Take(10))
                        Console.Error.WriteLine($"larva: translating quoted functions: {error}");
                    foreach (var error in program?.Errors ?? [])
                        Console.Error.WriteLine($"larva: translating quoted functions: {error}");
                    return null;
                }
                Larva.Native.NativeRuntime.Reset();
                var native = Larva.X64.X64Backend.Compile(program, new Larva.X64.X64Options { Compilation = options });
                if (!native.Success)
                {
                    Console.Error.WriteLine($"larva: translating quoted functions: {native}");
                    return null;
                }
                int code;
                using (var image = native.Program!.Load())
                    code = ((delegate* unmanaged<int>)image.Address(program.Entry!))();
                Larva.Native.NativeRuntime.Reset();
                if (code != 0)
                {
                    Console.Error.WriteLine($"larva: translating quoted functions: the translators ended with {code}");
                    return null;
                }
            }
            else
            {
                var vm = Larva.IR.Compiler.Compile(lowered.Module!, options with { Optimize = false });
                if (!vm.Success)
                {
                    foreach (var error in vm.TypeErrors.Take(10))
                        Console.Error.WriteLine($"larva: translating quoted functions: {error}");
                    return null;
                }
                new Interpreter(vm.Il!, options: new InterpreterOptions { StepLimit = long.MaxValue }).Run(function);
            }
            return File.Exists(file) ? File.ReadAllText(file) : "";
        }
        catch (Exception e) when (e is UnhandledException or VmException)
        {
            Console.Error.WriteLine($"larva: translating quoted functions: a translator failed: {e.Message}");
            return null;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
