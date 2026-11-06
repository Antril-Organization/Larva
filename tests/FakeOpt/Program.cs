// Stand-in for LLVM's `opt` in the Polly test: checks the command line Larva builds and copies the module through.
int o = Array.IndexOf(args, "-o");
bool expected = args.Contains("-load-pass-plugin=LLVMPolly.dll") && args.Contains("-passes=default<O2>") && args.Contains("-S")
    && args.Contains("-polly-vectorizer=stripmine");
if (!expected || o < 0 || o + 2 >= args.Length)
{
    Console.Error.WriteLine($"unexpected arguments: {string.Join(' ', args)}");
    return 1;
}
File.WriteAllText(args[o + 1], File.ReadAllText(args[^1]) + "\n; through the stand-in opt\n");
return 0;
