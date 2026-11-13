using System.Diagnostics;
using Larva.Hir.Encoding;

namespace Larva.Bindings.Tests;

/// <summary>
/// Larva.Bindings: a library's exports, a description to a HIR assembly, C headers (of our own, and real ones through
/// clang) read into descriptions with structs, unions and packing, and `larva bind` on the command line. What a
/// language makes of the bindings (their source, programs that call them) is tested with that language.
/// </summary>
public sealed class BindingsTests : CheckedTest
{
    private static string System32(string library) => Path.Combine(Environment.SystemDirectory, library);

    private static void Log(string text) => TestContext.Current.TestOutputHelper?.WriteLine(text);

    private static void RequireWindows() => Assert.SkipUnless(OperatingSystem.IsWindows(), "the bindings tests use kernel32.dll");

    private static void RequireClang(string what) => Assert.SkipWhen(HeaderReader.FindClang() is null, $"no clang: {what} is not read");

    [Fact]
    public void Kernel32_exports_and_a_description_make_an_assembly_of_imports()
    {
        RequireWindows();
        string kernel32 = System32("kernel32.dll");
        var exports = Exports.Read(kernel32);
        Check("kernel32.dll's export directory is read", exports.Count > 1000 && exports.Contains("GetCurrentProcessId") && exports.Contains("Sleep"), $"{exports.Count} names");

        var description = BindingDescription.Parse("""
            # A few of kernel32's functions.
            library kernel32.dll
            GetCurrentProcessId() : u32
            GetCurrentThreadId() : u32
            GetTickCount64() : u64
            Sleep(u32 milliseconds)
            GetModuleHandleW(u16* name) : void*
            Beep as MakeBeep(u32 frequency, u32 duration) : bool
            """);
        Check("the description: library, class from its name, six functions", description.Library == "kernel32.dll" && description.Class == "Kernel32"
            && description.Assembly == "Kernel32Bindings" && description.Functions.Count == 6 && description.Functions[5].Entry == "Beep" && description.Functions[5].Name == "MakeBeep");

        string missing = "";
        try
        {
            BindingGenerator.Build(BindingDescription.Parse("library kernel32.dll\nNoSuchFunctionXyz() : u32\n"), exports);
        }
        catch (BindingException e)
        {
            missing = e.Message;
        }
        Check("an entry the library does not export is refused by name", missing.Contains("NoSuchFunctionXyz"), missing);

        var assembly = BindingGenerator.Build(description, exports);
        var roundTrip = LvaFormat.Read(LvaFormat.Write(assembly));
        Check("the HIR assembly: a static class of [Import] methods that survives the .lva format",
            roundTrip.Types.Count == 1 && roundTrip.Types[0].Name == "Kernel32" && roundTrip.Types[0].Methods.Count == 6
            && roundTrip.Types[0].Methods.All(m => (m.Flags & global::Larva.Hir.MethodFlags.Import) != 0 && m.Library == "kernel32.dll" && m.Body is null)
            && roundTrip.Types[0].Methods[5].ExternName == "Beep" && roundTrip.Types[0].Methods[5].Name == "MakeBeep");
    }

    // ── strings and func* ──

    [Fact]
    public void String_parameters_reference_System_Core_and_string_results_are_refused()
    {
        RequireWindows();
        var crt = BindingDescription.Parse("""
            library ucrtbase.dll
            class Crt
            abs(i32 x) : i32
            strlen(str text) : u64
            qsort(void* items, u64 count, u64 size, func*<void*, void*> : i32 compare)
            """);
        string rejected = "";
        try
        {
            BindingDescription.Parse("library x.dll\nName() : str\n");
        }
        catch (BindingException e)
        {
            rejected = e.Message;
        }
        Check("a string result is refused (C gives a pointer)", rejected.Contains("not a string"), rejected);
        var crtAssembly = BindingGenerator.Build(crt, Exports.Read(System32("ucrtbase.dll")));
        Check("string parameters reference System.Core's String", crtAssembly.References.Any(r => r.Name == "System.Core") && crtAssembly.TypeRefs.Any(t => t.Name == "String"));
    }

    // ── C headers ──

    [Fact]
    public void A_header_of_our_own_becomes_a_description()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            /* A header in the subset the reader knows. */
            #pragma once
            #include <stddef.h>
            #define CRT_VERSION 3
            #define CRT_FLAG (0x10)
            #define CRT_MIN -2L
            #define CRT_TEXT "no"      // not an integer: ignored
            #define CRT_MACRO(x) (x)   // a function-like macro: ignored
            typedef unsigned int DWORD;
            typedef struct _CTX CTX;    // opaque
            typedef int (__cdecl *Comparer)(const void*, const void*);
            enum Mode { MODE_A, MODE_B = 5, MODE_C };
            typedef enum { LEVEL_LOW, LEVEL_HIGH } Level;
            __declspec(dllimport) DWORD __stdcall GetTickCount(void);
            int abs(int x);
            size_t strlen(const char* s);
            void qsort(void* base, size_t n, size_t size, Comparer cmp);
            int lstrlenW(const wchar_t* text);
            CTX* Open(const char* name, Level level);
            int Printf(const char* fmt, ...);
            double Sum(const double values[], int count);
            int Inline(int x) { return x; }
            """, "ucrtbase.dll", "Crt");
        Log(description);
        Check("constants from #define and enums (decimal, hex, parenthesized, negative, auto-numbered)",
            description.Contains("const CRT_VERSION = 3\n") && description.Contains("const CRT_FLAG = 16\n") && description.Contains("const CRT_MIN = -2\n")
            && description.Contains("const MODE_B = 5\n") && description.Contains("const MODE_C = 6\n") && description.Contains("const LEVEL_HIGH = 1\n") && !description.Contains("CRT_TEXT"));
        Check("prototypes: calling conventions and declspec dropped, typedefs followed, char* as str? and wchar_t* as String<u16>? (C may take NULL), size_t as u64",
            description.Contains("GetTickCount() : u32\n") && description.Contains("abs(i32 x) : i32\n") && description.Contains("strlen(str? s) : u64\n") && description.Contains("lstrlenW(String<u16>? text) : i32\n"));
        Check("a function pointer typedef becomes func*, an opaque struct pointer void*, an enum i32, an array a pointer",
            description.Contains("qsort(void* base, u64 n, u64 size, func*<void*, void*> : i32 cmp)\n") && description.Contains("Open(str? name, i32 level) : void*\n")
            && description.Contains("Sum(f64* values, i32 count) : f64\n"));
        Check("variadic functions are skipped with the reason; a definition's prototype is kept",
            reader.Skipped.Any(s => s.Name == "Printf" && s.Why == "variadic") && description.Contains("Inline(i32 x) : i32\n"),
            string.Join("; ", reader.Skipped.Select(s => $"{s.Name}: {s.Why}")));
    }

    /// <summary>C's integer literals: a leading zero is octal (`0755` is 493, not 755), `0b` binary, `0x` hex, in #defines and enums alike.</summary>
    [Fact]
    public void Octal_and_binary_literals_are_read_as_C_reads_them()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            #define MODE 0755
            #define MASK 0777u
            #define HEX 0x1F
            #define BIN 0b101
            #define ZERO 0
            #define TEN 10
            #define NEG -010
            #define BAD 089
            enum E { EA = 0755, EB = 010, EC, ED = 0b11 };
            """, "lib.dll");
        Log(description);
        Check("octal #defines, with a suffix, negated; `0` and plain decimals as before; `089` is not a number",
            description.Contains("const MODE = 493\n") && description.Contains("const MASK = 511\n") && description.Contains("const NEG = -8\n")
            && description.Contains("const ZERO = 0\n") && description.Contains("const TEN = 10\n") && !description.Contains("BAD"));
        Check("hex and binary", description.Contains("const HEX = 31\n") && description.Contains("const BIN = 5\n"));
        Check("enum members: octal values, the next member counts on from them, binary",
            description.Contains("const EA = 493\n") && description.Contains("const EB = 8\n") && description.Contains("const EC = 9\n") && description.Contains("const ED = 3\n")
            && reader.Enums.Single().Members.Select(m => m.Value).SequenceEqual([493, 8, 9, 3]));
    }

    /// <summary>`void (*ops[4])(int)` in a struct: four function pointers in a row, the callback type kept, not one pointer.</summary>
    [Fact]
    public void Arrays_of_function_pointers_are_struct_fields_of_N_callbacks()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            #define OPS 4
            typedef struct { int tag; void (*ops[4])(int); int (*single)(void); double (*grid[2][3])(void); void (*named[OPS])(void*); } Table;
            void Use(Table* t);
            """, "lib.dll");
        Log(description);
        Check("the field is `func*<i32> : void[4]`; a single pointer stays as it was; a two-dimensional array is one row; a #define as the length",
            description.Contains("struct Table { i32 tag; func*<i32> : void[4] ops; func*<> : i32 single; func*<> : f64[6] grid; func*<void*> : void[4] named }\n")
            && description.Contains("Use(Table* t)\n"), string.Join("; ", reader.Opaque.Select(o => $"{o.Name}: {o.Why}")));
        var parsed = BindingDescription.Parse(description);
        Check("the layout counts the pointers: 4 (+4) + 32 + 8 + 48 + 32 bytes, 8-aligned", BindingDescription.Layout("Table", parsed.Structs) == (128, 8));
        var assembly = BindingGenerator.Build(parsed);
        var table = assembly.Types.Single(t => t.Name == "Table");
        Check("the assembly's struct holds an inline array of 4 native function types",
            table.Fields.Single(f => f.Name == "ops").Type is global::Larva.Hir.InlineArrayType { Count: 4, Element: global::Larva.Hir.NativeFuncType },
            table.Fields.Single(f => f.Name == "ops").Type.ToString());
    }

    /// <summary>Attributes with nested parentheses (`visibility("default")`, `align(16)`), before, inside or after a declarator, leave it readable.</summary>
    [Fact]
    public void Attributes_around_declarations_are_stripped_with_their_parentheses_matched()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            __attribute__((visibility("default"))) int Visible(int x);
            int __attribute__((visibility("default"))) Middle(int x);
            int After(int x) __attribute__((nonnull(1), warn_unused_result));
            extern __attribute__((visibility("default"))) __attribute__((noinline)) int Twice(int x);
            __declspec(dllexport) int Exported(int x);
            __declspec(align(16)) int Aligned(int x);
            extern int Plain(int x);
            static inline int Inlined(int x) { return x; }
            typedef struct __attribute__((packed)) { unsigned char c; unsigned int v; } Tight;
            int __attribute__((format(printf, 1, 2))) Formatted(const char* fmt, ...);
            void Use(Tight* t);
            """, "lib.dll");
        Log(description);
        Check("every attributed prototype is read; only the variadic one is skipped, for being variadic",
            description.Contains("Visible(i32 x) : i32\n") && description.Contains("Middle(i32 x) : i32\n") && description.Contains("After(i32 x) : i32\n")
            && description.Contains("Twice(i32 x) : i32\n") && description.Contains("Exported(i32 x) : i32\n") && description.Contains("Aligned(i32 x) : i32\n")
            && description.Contains("Plain(i32 x) : i32\n") && description.Contains("Inlined(i32 x) : i32\n") && description.Contains("Use(Tight* t)\n")
            && reader.Skipped.Count == 1 && reader.Skipped[0] == ("Formatted", "variadic"),
            string.Join("; ", reader.Skipped.Select(s => $"{s.Name}: {s.Why}")));
    }

    /// <summary>
    /// C's type words in any order (`long unsigned int` is how clang spells size_t), `enum Tag` as a type, and `struct Tag`
    /// as a type when the struct is defined later or only declared.
    /// </summary>
    [Fact]
    public void Reordered_type_words_enum_tags_and_struct_tags_are_types()
    {
        var reader = new HeaderReader(longIs64: true);
        string description = reader.Read("""
            enum Tag { TAG_A, TAG_B };
            typedef enum Tag TagT;
            struct Later;
            struct Never;
            long unsigned int Size(void);
            unsigned long long int Big(int long a, short unsigned b, long signed int c, char unsigned d, long double e);
            enum Tag GetTag(enum Tag t, enum Tag* out, TagT u);
            struct Later* MakeLater(const struct Later* from);
            struct Never* MakeNever(const struct Never* n, struct Unknown* u);
            struct Holder { enum Tag t; struct Later* later; };
            void Hold(struct Holder* h);
            struct Later { int x; };
            """, "lib.dll");
        Log(description);
        Check("`long unsigned int` is u64 (LP64), the other orders of the words likewise",
            description.Contains("Size() : u64\n") && description.Contains("Big(i64 a, u16 b, i64 c, u8 d, f64 e) : u64\n"));
        Check("`enum Tag` is i32 by value, i32* by pointer, through a typedef too; the prototype that starts with `enum` is read",
            description.Contains("GetTag(i32 t, i32* out, i32 u) : i32\n")
            && reader.TypedefUses.GetValueOrDefault("GetTag#0") == "Tag" && reader.TypedefUses.GetValueOrDefault("GetTag#result") == "Tag"
            && reader.TypedefUses.GetValueOrDefault("GetTag#2") == "TagT" && reader.Enums.Single().Names.SequenceEqual(["TagT", "Tag"]),
            string.Join("; ", reader.TypedefUses.Select(u => $"{u.Key}={u.Value}")));
        Check("`struct Later*` is Later* (its body comes later), `struct Never*` and `struct Unknown*` are void* (opaque), in fields too",
            description.Contains("MakeLater(Later* from) : Later*\n") && description.Contains("MakeNever(void* n, void* u) : void*\n")
            && description.Contains("struct Holder { i32 t; Later* later }\n") && description.Contains("Hold(Holder* h)\n")
            && reader.TypedefUses.GetValueOrDefault("Holder.t") == "Tag" && reader.Skipped.Count == 0,
            string.Join("; ", reader.Skipped.Select(s => $"{s.Name}: {s.Why}")));
    }

    /// <summary>`long double` and `wchar_t` have the platform's sizes: 8 and 2 bytes on Windows, 16 and 4 on x86-64 Linux.</summary>
    [Fact]
    public void Long_double_and_wchar_t_are_sized_by_the_platform()
    {
        const string header = """
            typedef struct { long double d; wchar_t w; } Wide;
            long double LD(long double x);
            void W(wchar_t w, const wchar_t* text, wchar_t* out, Wide* p);
            """;
        var windows = new HeaderReader(longIs64: false);
        string windowsDescription = windows.Read(header, "lib.dll");
        Log(windowsDescription);
        Check("Windows: long double is f64, wchar_t is u16 and a UTF-16 string as a parameter",
            windowsDescription.Contains("struct Wide { f64 d; u16 w }\n") && windowsDescription.Contains("LD(f64 x) : f64\n")
            && windowsDescription.Contains("W(u16 w, String<u16>? text, String<u16>? out, Wide* p)\n"));
        var linux = new HeaderReader(longIs64: true);
        string linuxDescription = linux.Read(header, "lib.so");
        Log(linuxDescription);
        Check("LP64: wchar_t is u32 (pointers to it u32*, no UTF-16 string); long double is 16 bytes, so LD is skipped and Wide opaque, each saying why",
            linuxDescription.Contains("W(u32 w, u32* text, u32* out, void* p)\n") && !linuxDescription.Contains("LD(") && !linuxDescription.Contains("struct Wide")
            && linux.Skipped.Any(s => s.Name == "LD" && s.Why.Contains("long double")) && linux.Opaque.Any(o => o.Name == "Wide" && o.Why.Contains("long double")),
            string.Join("; ", linux.Skipped.Select(s => $"{s.Name}: {s.Why}").Concat(linux.Opaque.Select(o => $"{o.Name}: {o.Why}"))));
        var mac = new HeaderReader(longIs64: true, longDoubleIs64: true);
        Check("given explicitly (macOS on ARM: LP64 with a plain-double long double)", mac.Read(header, "lib.dylib").Contains("struct Wide { f64 d; u32 w }\n"));
        var host = new HeaderReader();
        Check("by default the host's sizes", host.Read(header, "lib").Contains("struct Wide {") == HeaderReader.HostLongDoubleIs64
            && host.Read(header, "lib").Contains(OperatingSystem.IsWindows() ? "W(u16 w," : "W(u32 w,"));
    }

    /// <summary>
    /// A header under a path with non-ASCII characters: the preprocessor's line markers write them as octal escapes
    /// (`\303\261` for `ñ`), which must still match the header's folder for its #defines to count.
    /// </summary>
    [Fact]
    public void Headers_under_non_ASCII_paths_keep_their_defines()
    {
        string folder = Path.Combine(TestEnvironment.TempFolder("headers"), "año ñ");
        Directory.CreateDirectory(folder);
        string header = Path.Combine(folder, "api.h");
        File.WriteAllText(header, "#define A_ONE 1\n#define A_TWO 2\nint F(int x);\n");
        // Preprocessed text as clang writes it, without clang: the marker's path escaped.
        string escaped = Path.GetFullPath(header).Replace("\\", "\\\\").Replace("ñ", "\\303\\261");
        var reader = new HeaderReader(longIs64: false);
        reader.DefineRoots.Add(folder);
        string description = reader.Read($"# 1 \"{escaped}\"\n#define A_ONE 1\n# 1 \"<built-in>\" 1\n#define SYSTEM 9\n# 2 \"{escaped}\" 2\n#define A_TWO 2\nint F(int x);\n", "lib.dll");
        Log(description);
        Check("the defines after the escaped marker are the header's; a system one is not; the function knows its file",
            description.Contains("const A_ONE = 1\n") && description.Contains("const A_TWO = 2\n") && !description.Contains("SYSTEM")
            && reader.Origins.GetValueOrDefault("F") == Path.GetFullPath(header).Replace('\\', '/'), string.Join("; ", reader.Origins.Select(o => $"{o.Key}={o.Value}")));
        if (HeaderReader.FindClang() is null)
            return;
        var real = new HeaderReader(longIs64: false);
        string realDescription = real.ReadFile(header, "lib.dll");
        Log(realDescription);
        Check("through clang: both defines and the function", realDescription.Contains("const A_ONE = 1\n") && realDescription.Contains("const A_TWO = 2\n") && realDescription.Contains("F(i32 x) : i32\n"));
    }

    /// <summary>
    /// `extern "C" { … }`: the declarations inside are read as they are (the braces are not a body). Through clang the
    /// `#ifdef __cplusplus` guards go away because the preprocessor runs as C (`-x c`); read as it is, the block stays.
    /// </summary>
    [Fact]
    public void Extern_C_blocks_are_read_through()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            #ifdef __cplusplus
            extern "C" {
            #endif
            #define INSIDE 1
            int Inside(int x);
            typedef struct { int a; } S;
            extern "C" {
              extern int Nested(S* s);
              struct T { S s; int (*cb)(void); };
            }
            #ifdef __cplusplus
            }
            #endif
            int Outside(S* s, struct T* t);
            int Body(int x) { if (x) { return 1; } return 0; }
            """, "lib.dll");
        Log(description);
        Check("the functions and the struct inside the block, a nested block, and what follows the block",
            description.Contains("const INSIDE = 1\n") && description.Contains("struct S { i32 a }\n") && description.Contains("struct T { S s; func*<> : i32 cb }\n")
            && description.Contains("Inside(i32 x) : i32\n") && description.Contains("Nested(S* s) : i32\n") && description.Contains("Outside(S* s, T* t) : i32\n")
            && description.Contains("Body(i32 x) : i32\n") && reader.Skipped.Count == 0, string.Join("; ", reader.Skipped.Select(s => $"{s.Name}: {s.Why}")));
        if (HeaderReader.FindClang() is null)
            return;
        string folder = TestEnvironment.TempFolder("headers");
        string header = Path.Combine(folder, "externc.h");
        File.WriteAllText(header, "#ifdef __cplusplus\nextern \"C\" {\n#endif\nint Inside(int x);\n#ifdef __cplusplus\n}\n#endif\n");
        var real = new HeaderReader(longIs64: false);
        Check("through clang, run as C, the guarded block is gone and the function read", real.ReadFile(header, "lib.dll").Contains("Inside(i32 x) : i32\n"));
    }

    /// <summary>
    /// A header mixing declarations the reader can and cannot read: each unreadable one is left out with a warning naming
    /// its line (the header's own line after a line marker), and everything else is read; the bind never aborts.
    /// </summary>
    [Fact]
    public void Unreadable_declarations_are_skipped_with_their_line_and_the_rest_is_read()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            # 1 "C:\\api\\mixed.h"
            int Good1(int x);
            typedef struct { int a; } Twice;
            struct Twice { int b; };
            int Good2(int x);
            typedef void (*Broken)(int;
            int Good3(int x);
            typedef struct Pair { int a; } Pair;
            int Good4(int x);
            struct Open { int a;
            int Good5(int x);
            """, "lib.dll");
        Log(description);
        Log(string.Join("\n", reader.Unparsed.Select(u => $"{u.Where}: {u.Why}")));
        Check("the good prototypes before and between the bad declarations are read, and the struct defined twice keeps its first body",
            description.Contains("Good1(i32 x) : i32\n") && description.Contains("Good2(i32 x) : i32\n") && description.Contains("Good3(i32 x) : i32\n")
            && description.Contains("Good4(i32 x) : i32\n") && description.Contains("struct Twice { i32 a }\n") && description.Contains("struct Pair { i32 a }\n")
            && !description.Contains("Broken") && !description.Contains("Open"));
        Check("each unreadable declaration is a warning with the header's line: the second `Twice` (line 3), `typedef … (int;` (line 5), the unclosed `struct Open` (line 9, swallowing Good5)",
            reader.Unparsed.Count == 3
            && reader.Unparsed[0] == ("C:/api/mixed.h:3", "struct `Twice` is defined twice: the first definition is kept")
            && reader.Unparsed[1].Where == "C:/api/mixed.h:5" && reader.Unparsed[1].Why.Contains("typedef void (*Broken)(int")
            && reader.Unparsed[2].Where == "C:/api/mixed.h:9" && reader.Unparsed[2].Why.Contains("struct Open"),
            string.Join("; ", reader.Unparsed.Select(u => $"{u.Where}: {u.Why}")));
        Check("the description parses", BindingDescription.Parse(description).Functions.Count == 4);
    }

    /// <summary>A real header, preprocessed by clang: the C runtime's stdlib.h.</summary>
    [Fact]
    public void Stdlib_h_through_clang_becomes_a_description_of_ucrtbase()
    {
        RequireWindows();
        RequireClang("the real header");
        string folder = TestEnvironment.TempFolder("headers");
        string stdlib = Path.Combine(folder, "stdlib_all.h");
        File.WriteAllText(stdlib, "#include <stdlib.h>\n#include <string.h>\n");
        var real = new HeaderReader(longIs64: false);
        string realDescription = real.ReadFile(stdlib, "ucrtbase.dll", "Ucrt");
        var realParsed = BindingDescription.Parse(realDescription);
        Check("stdlib.h + string.h through clang: abs, qsort, strtol, strlen and memcmp come out with the right signatures",
            realDescription.Contains("abs(i32 ") && realDescription.Contains("qsort(void* ") && realDescription.Contains("func*<void*, void*> : i32")
            && realDescription.Contains("strtol(str? ") && realDescription.Contains("strlen(str? ") && realDescription.Contains("memcmp(void* ") && realDescription.Contains(") : u64"),
            $"{realParsed.Functions.Count} functions, {real.Skipped.Count} skipped; e.g. {string.Join(" | ", realParsed.Functions.Take(5).Select(f => f.Name))}");
        var realExports = Exports.Read(System32("ucrtbase.dll"));
        var known = new HashSet<string>(realExports);
        int exported = realParsed.Functions.Count(f => known.Contains(f.Entry));
        Check("most of what it read is exported by ucrtbase.dll", exported > 100 && exported * 10 > realParsed.Functions.Count * 6, $"{exported} of {realParsed.Functions.Count}");
    }

    // ── C structs ──

    [Fact]
    public void C_structs_become_descriptions_with_layout()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            typedef unsigned short WORD;
            typedef unsigned long DWORD;
            #define NAME_LENGTH 4
            typedef struct _SYSTEMTIME { WORD wYear; WORD wMonth; WORD wDayOfWeek; WORD wDay; WORD wHour; WORD wMinute; WORD wSecond; WORD wMilliseconds; } SYSTEMTIME, *PSYSTEMTIME, *LPSYSTEMTIME;
            typedef struct _FILETIME { DWORD dwLowDateTime; DWORD dwHighDateTime; } FILETIME, *PFILETIME;
            struct Node;                                    /* forward: gets its body below */
            typedef struct Node Node;
            struct Node { Node* next; int value, weight; };
            typedef struct { FILETIME created, modified; unsigned char name[NAME_LENGTH]; double score; } Entry;
            typedef struct { int a; union { int b; float c; } u; } WithUnion;   /* opaque: a nested union */
            typedef struct { unsigned flags : 3; } Bits;                        /* opaque: bitfields */
            typedef struct { int big[1000]; } Big;                              /* opaque: too long an array */
            void GetSystemTime(LPSYSTEMTIME lpSystemTime);
            void GetSystemTimeAsFileTime(LPFILETIME lpSystemTimeAsFileTime);
            int FileTimeToSystemTime(const FILETIME* lpFileTime, LPSYSTEMTIME lpSystemTime);
            typedef FILETIME *LPFILETIME;
            Node* First(Node* list);
            Entry Make(void);
            int Walk(WithUnion* w, Bits* b);
            """, "kernel32.dll", "K32");
        Log(description);
        Check("typedef struct with tag and pointer declarators: one struct, pointer typedefs to it",
            description.Contains("struct SYSTEMTIME { u16 wYear; u16 wMonth; u16 wDayOfWeek; u16 wDay; u16 wHour; u16 wMinute; u16 wSecond; u16 wMilliseconds }\n")
            && description.Contains("GetSystemTime(SYSTEMTIME* lpSystemTime)\n") && description.Contains("FileTimeToSystemTime(FILETIME* lpFileTime, SYSTEMTIME* lpSystemTime) : i32\n"));
        Check("a forward-declared struct defined later, pointing to itself, with `int value, weight`",
            description.Contains("struct Node { Node* next; i32 value; i32 weight }\n") && description.Contains("First(Node* list) : Node*\n"));
        Check("nested structs by value, `T name[N]` as N fields with N from a #define, typedef written after its use",
            description.Contains("struct Entry { FILETIME created; FILETIME modified; u8[4] name; f64 score }\n")
            && description.Contains("GetSystemTimeAsFileTime(FILETIME* lpSystemTimeAsFileTime)\n"));
        Check("a nested anonymous union is its own union named after the outer struct; a long array is one inline array; bitfields leave the struct opaque (void* pointers); a struct by value skips the function",
            description.Contains("union WithUnion_u { i32 b; f32 c }\n") && description.Contains("struct WithUnion { i32 a; WithUnion_u u }\n")
            && description.Contains("Walk(WithUnion* w, void* b) : i32\n") && !description.Contains("struct Bits") && description.Contains("struct Big { i32[1000] big }\n")
            && reader.Opaque.Any(o => o.Name == "Bits" && o.Why == "bitfields")
            && reader.Skipped.Any(s => s.Name == "Make" && s.Why.Contains("by value")),
            string.Join("; ", reader.Opaque.Select(o => $"{o.Name}: {o.Why}").Concat(reader.Skipped.Select(s => $"{s.Name}: {s.Why}"))));

        var parsed = BindingDescription.Parse(description);
        Check("the description parses: structs in order, functions over their pointers", parsed.Structs.Count == 7 && parsed.Structs[3].Fields.Count == 4 && parsed.Structs[4].IsUnion && parsed.Functions.Count == 5,
            $"{parsed.Structs.Count} structs, {parsed.Functions.Count} functions");
        string rejected = "";
        try { BindingDescription.Parse("library x.dll\nstruct P { i32 a }\nF(P p)\n"); } catch (BindingException e) { rejected = e.Message; }
        Check("a struct by value as a parameter is refused (it crosses to C by pointer)", rejected.Contains("by pointer"), rejected);
    }

    /// <summary>A real header: windows.h (lean) through clang, for the structs kernel32 fills.</summary>
    [Fact]
    public void Windows_h_through_clang_becomes_a_description_of_kernel32()
    {
        RequireWindows();
        RequireClang("windows.h");
        string folder = TestEnvironment.TempFolder("headers");
        string windows = Path.Combine(folder, "windows_lean.h");
        File.WriteAllText(windows, "#define WIN32_LEAN_AND_MEAN\n#include <windows.h>\n");
        var real = new HeaderReader(longIs64: false);
        var watch = Stopwatch.StartNew();
        string realDescription = real.ReadFile(windows, "kernel32.dll", "Win32");
        watch.Stop();
        var realParsed = BindingDescription.Parse(realDescription);
        Check("windows.h through clang: SYSTEMTIME, FILETIME, POINT, RECT and SYSTEM_INFO come out with layout, GetSystemTime over SYSTEMTIME*",
            realDescription.Contains("struct SYSTEMTIME { u16 wYear;") && realDescription.Contains("struct FILETIME { u32 dwLowDateTime; u32 dwHighDateTime }")
            && realDescription.Contains("struct POINT { i32 x; i32 y }") && realDescription.Contains("struct RECT { i32 left; i32 top; i32 right; i32 bottom }")
            && realDescription.Contains("GetSystemTime(SYSTEMTIME* ") && realDescription.Contains("GetSystemTimeAsFileTime(FILETIME* ")
            && realDescription.Contains("union LARGE_INTEGER {") && realDescription.Contains("QueryPerformanceCounter(LARGE_INTEGER* "),
            $"{realParsed.Structs.Count} structs, {realParsed.Functions.Count} functions, {real.Skipped.Count} skipped, {real.Opaque.Count} opaque, {watch.ElapsedMilliseconds} ms");
        var known = new HashSet<string>(Exports.Read(System32("kernel32.dll")));
        int exported = realParsed.Functions.Count(f => known.Contains(f.Entry));
        Check("hundreds of its functions are kernel32 exports", exported > 300, $"{exported} of {realParsed.Functions.Count}; e.g. {string.Join(" | ", real.Opaque.Take(4).Select(o => $"{o.Name}: {o.Why}"))}");
    }

    // ── C unions ──

    /// <summary>C unions with every member at offset 0, and `#pragma pack`, with the layouts C gives them.</summary>
    [Fact]
    public void C_unions_and_pragma_pack_become_descriptions_with_C_layouts()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            typedef unsigned long DWORD;
            typedef long LONG;
            typedef long long LONGLONG;
            typedef union _LARGE_INTEGER { struct { DWORD LowPart; LONG HighPart; }; struct { DWORD LowPart; LONG HighPart; } u; LONGLONG QuadPart; } LARGE_INTEGER, *PLARGE_INTEGER;
            int QueryPerformanceCounter(LARGE_INTEGER* lpPerformanceCount);
            typedef union { int i; double d; unsigned char b[3]; } Value;
            typedef struct { Value v; short tag; } Tagged;
            union Small { char c; short s; };
            struct Pair { union Small a, b; };
            #pragma pack(push, 1)
            typedef struct { unsigned char tag; unsigned int value; unsigned short code; } Packed;
            #pragma pack(push, 2)
            typedef struct { char c; double d; } Packed2;
            #pragma pack(pop)
            #pragma pack(pop)
            typedef struct { char c; double d; } Natural;
            """, "kernel32.dll", "K32");
        Log(description);
        Check("LARGE_INTEGER: the anonymous struct, the named one and QuadPart, each nested struct defined before the union",
            description.Contains("struct LARGE_INTEGER_anon0 { u32 LowPart; i32 HighPart }\n") && description.Contains("struct LARGE_INTEGER_u { u32 LowPart; i32 HighPart }\n")
            && description.Contains("union LARGE_INTEGER { LARGE_INTEGER_anon0 anon0; LARGE_INTEGER_u u; i64 QuadPart }\n") && description.Contains("QueryPerformanceCounter(LARGE_INTEGER* lpPerformanceCount) : i32\n"));
        Check("a union with an array member, a struct holding a union, `union Small a, b`",
            description.Contains("union Value { i32 i; f64 d; u8[3] b }\n") && description.Contains("struct Tagged { Value v; i16 tag }\n")
            && description.Contains("union Small { i8 c; i16 s }\n") && description.Contains("struct Pair { Small a; Small b }\n"));
        Check("#pragma pack: push 1, a nested push 2, two pops back to natural alignment",
            description.Contains("struct Packed pack 1 { u8 tag; u32 value; u16 code }\n") && description.Contains("struct Packed2 pack 2 { i8 c; f64 d }\n") && description.Contains("struct Natural { i8 c; f64 d }\n"));
        var parsed = BindingDescription.Parse(description);
        Check("layouts as C: LARGE_INTEGER 8/8, Value 8/8, Tagged 16/8, Small 2/2, Pair 4/2, Packed 7/1, Packed2 10/2, Natural 16/8",
            BindingDescription.Layout("Packed", parsed.Structs) == (7, 1) && BindingDescription.Layout("Packed2", parsed.Structs) == (10, 2) && BindingDescription.Layout("Natural", parsed.Structs) == (16, 8) &&
            BindingDescription.Layout("LARGE_INTEGER", parsed.Structs) == (8, 8) && BindingDescription.Layout("Value", parsed.Structs) == (8, 8)
            && BindingDescription.Layout("Tagged", parsed.Structs) == (16, 8) && BindingDescription.Layout("Small", parsed.Structs) == (2, 2) && BindingDescription.Layout("Pair", parsed.Structs) == (4, 2));
    }

    /// <summary>A union under `#pragma pack` is packed like a struct: its alignment drops, and so does that of a struct holding it.</summary>
    [Fact]
    public void Unions_under_pragma_pack_keep_the_packing()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            #pragma pack(push, 1)
            typedef union { unsigned char c; unsigned int v; } PU;
            typedef struct { unsigned char c; PU u; } PS;
            #pragma pack(pop)
            typedef struct { unsigned char c; PU u; } Natural;   /* the packed union keeps its alignment of 1 here */
            typedef union { unsigned char c; unsigned int v; } NU;
            void Use(PS* s, Natural* n, NU* u);
            """, "lib.dll");
        Log(description);
        Check("`union PU pack 1`, the struct under the same pack, the union after the pop without one",
            description.Contains("union PU pack 1 { u8 c; u32 v }\n") && description.Contains("struct PS pack 1 { u8 c; PU u }\n")
            && description.Contains("union NU { u8 c; u32 v }\n") && description.Contains("Use(PS* s, Natural* n, NU* u)\n"));
        var parsed = BindingDescription.Parse(description);
        Check("layouts as C: PU 4/1, PS 5/1, Natural 5/1 (the packed union inside), NU 4/4",
            BindingDescription.Layout("PU", parsed.Structs) == (4, 1) && BindingDescription.Layout("PS", parsed.Structs) == (5, 1)
            && BindingDescription.Layout("Natural", parsed.Structs) == (5, 1) && BindingDescription.Layout("NU", parsed.Structs) == (4, 4),
            $"{BindingDescription.Layout("PU", parsed.Structs)} {BindingDescription.Layout("Natural", parsed.Structs)}");
        Check("the description's own text keeps the union's pack", parsed.ToText().Contains("union PU pack 1 {"), parsed.ToText());
    }

    // ── SDL3 ──

    /// <summary>
    /// SDL3 from its headers: SDL.h through clang (with SDL's include folder), the functions SDL3.dll exports, its structs
    /// and unions with layout, its #defines. Skipped without SDL: LARVA_SDL names a folder with SDL3.dll and include/SDL3
    /// (or with build/SDL3.dll and SDL-main/include, as a CMake build of the source leaves them).
    /// </summary>
    [Fact]
    public void SDL_h_through_clang_becomes_a_description_of_SDL3()
    {
        string root = Environment.GetEnvironmentVariable("LARVA_SDL") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SDL-main");
        string? dll = new[] { Path.Combine(root, "SDL3.dll"), Path.Combine(root, "build", "SDL3.dll") }.FirstOrDefault(File.Exists);
        string? include = new[] { Path.Combine(root, "include"), Path.Combine(root, "SDL-main", "include") }.FirstOrDefault(d => File.Exists(Path.Combine(d, "SDL3", "SDL.h")));
        Assert.SkipWhen(dll is null || include is null || HeaderReader.FindClang() is null, "SDL3 not found (LARVA_SDL: a folder with SDL3.dll and include/SDL3) or no clang");
        var watch = Stopwatch.StartNew();
        var reader = new HeaderReader(longIs64: false);
        string text = reader.ReadFile(Path.Combine(include!, "SDL3", "SDL.h"), "SDL3.dll", "SDL", includeDirectories: [include!]);
        var description = BindingDescription.Parse(text);
        var missing = description.KeepExported(Exports.Read(dll!));
        long readMs = watch.ElapsedMilliseconds;
        var skippedSdl = reader.Skipped.Where(s => s.Name.StartsWith("SDL_", StringComparison.Ordinal)).ToList();
        Check("SDL.h through clang: over a thousand SDL3 exports, its structs and unions with layout, its #defines; only variadic functions and structs by value left out",
            description.Functions.Count > 1000 && description.Structs.Count > 120 && description.Constants.Any(c => c.Name == "SDL_INIT_EVENTS" && c.Value == 0x4000)
            && description.Structs.Any(s => s.Name == "SDL_Event" && s.IsUnion) && !description.Constants.Any(c => c.Name == "_MSC_VER")
            && skippedSdl.All(s => s.Why == "variadic" || s.Why.Contains("by value")) && !reader.Opaque.Any(o => o.Name.StartsWith("SDL_", StringComparison.Ordinal)),
            $"{description.Functions.Count} functions, {description.Structs.Count} structs and unions, {description.Constants.Count} constants, {skippedSdl.Count} SDL functions left out, {missing.Count} declarations not exported, {readMs} ms");
    }

    // ── larva bind ──

    [Fact]
    public void Larva_bind_lists_a_library_s_exports()
    {
        RequireWindows();
        var (listCode, listed) = LarvaCli.Run("bind", "--list", "kernel32.dll");
        Check("larva bind --list kernel32.dll prints its exports", listCode == 0 && listed.Contains("GetTickCount64"), listed.Length > 200 ? listed[..200] : listed);
    }

    [Fact]
    public void Larva_bind_does_not_overwrite_native_libraries()
    {
        RequireWindows();
        string root = TestEnvironment.TempFolder("bind");
        File.WriteAllText(Path.Combine(root, "api.bind"), """
            library kernel32.dll
            GetCurrentProcessId() : u32
            GetTickCount64() : u64
            lstrlenW(String<u16> text) : i32
            """);
        // A Larva library with the native library's own name would be loaded in its place: refused.
        var (clashCode, clashText) = LarvaCli.Run("bind", "kernel32.dll", Path.Combine(root, "api.bind"), "-o", Path.Combine(root, "kernel32.dll"));
        Check("bindings named as the native library (kernel32.dll) are refused", clashCode != 0 && clashText.Contains("native library's name") && !File.Exists(Path.Combine(root, "kernel32.dll")), clashText.Trim());
        // A DLL that is not a Larva library is not overwritten.
        File.WriteAllBytes(Path.Combine(root, "Native.dll"), [(byte)'M', (byte)'Z', 0, 0]);
        var (nativeCode, nativeText) = LarvaCli.Run("bind", "kernel32.dll", Path.Combine(root, "api.bind"), "-o", Path.Combine(root, "Native.dll"));
        Check("a DLL that is not a Larva library is not overwritten", nativeCode != 0 && File.ReadAllBytes(Path.Combine(root, "Native.dll")).Length == 4, nativeText.Trim());
    }

    /// <summary>`larva bind kernel32.dll api.h` writes the description it derived with --describe.</summary>
    [Fact]
    public void Larva_bind_reads_a_C_header_and_writes_the_derived_description()
    {
        RequireWindows();
        string root = TestEnvironment.TempFolder("bind");
        File.WriteAllText(Path.Combine(root, "api.h"), """
            #define K32_TAG 7
            typedef unsigned long DWORD;
            typedef unsigned __int64 ULONGLONG;
            __declspec(dllimport) DWORD __stdcall GetCurrentProcessId(void);
            __declspec(dllimport) ULONGLONG __stdcall GetTickCount64(void);
            __declspec(dllimport) int __stdcall lstrlenW(const wchar_t* lpString);
            """);
        string hLva = Path.Combine(root, "K32.dll"), hBind = Path.Combine(root, "derived.bind");
        var (hCode, hText) = LarvaCli.Run("bind", "kernel32.dll", Path.Combine(root, "api.h"), "-o", hLva, "--describe", hBind, "--no-pp");
        Check("larva bind reads a C header and writes the derived description", hCode == 0 && File.Exists(hLva) && File.Exists(hBind)
            && File.ReadAllText(hBind).Contains("const K32_TAG = 7") && File.ReadAllText(hBind).Contains("lstrlenW(String<u16>? lpString) : i32"), hText.Trim());
    }
}
