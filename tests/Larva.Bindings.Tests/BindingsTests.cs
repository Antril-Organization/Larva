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
        Check("a buffer kernel32 writes into (LPSTR) is a u8* parameter, a name it reads (LPCSTR) a str?",
            realDescription.Contains(" hModule, u8* lpFilename, u32 nSize) : u32\n") && realDescription.Contains("GetModuleHandleA(str? lpModuleName) : ")
            && realDescription.Contains("GetEnvironmentVariableA(str? lpName, u8* lpBuffer, u32 nSize) : u32\n"));
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

    /// <summary>`const char*` is an input string; a writable `char*` is a buffer C fills (bytes), never a string copied in and lost.</summary>
    [Fact]
    public void Char_buffers_are_byte_pointers_and_const_chars_are_strings()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            typedef char CHAR;
            typedef CHAR* LPSTR;
            typedef const CHAR* LPCSTR;
            typedef const char CCHAR;
            typedef unsigned long DWORD;
            DWORD GetModuleFileNameA(void* hModule, LPSTR lpFilename, DWORD nSize);
            void* GetModuleHandleA(LPCSTR lpModuleName);
            int Fill(char* buffer, int n);
            int FillW(wchar_t* buffer, int n);
            int Read(const char* text);
            int ReadW(const wchar_t* text);
            int ReadAfter(char const* text);
            int ReadAlias(CCHAR* text);
            int ConstPointer(const LPSTR buffer);
            int FillPointer(char* const buffer);
            char* Dup(const char* s);
            int Many(char** list);
            """, "kernel32.dll", "K32");
        Log(description);
        Check("a writable char* (also through LPSTR, also a const pointer to it) is a u8* buffer; a wchar_t* a u16* one",
            description.Contains("GetModuleFileNameA(void* hModule, u8* lpFilename, u32 nSize) : u32\n") && description.Contains("Fill(u8* buffer, i32 n) : i32\n")
            && description.Contains("FillW(u16* buffer, i32 n) : i32\n") && description.Contains("ConstPointer(u8* buffer) : i32\n") && description.Contains("FillPointer(u8* buffer) : i32\n"));
        Check("a const char* (written before or after, through LPCSTR, through a const typedef) is a str?; a const wchar_t* a String<u16>?",
            description.Contains("GetModuleHandleA(str? lpModuleName) : void*\n") && description.Contains("Read(str? text) : i32\n") && description.Contains("ReadW(String<u16>? text) : i32\n")
            && description.Contains("ReadAfter(str? text) : i32\n") && description.Contains("ReadAlias(str? text) : i32\n"));
        Check("results and deeper pointers stay pointers to bytes", description.Contains("Dup(str? s) : i8*\n") && description.Contains("Many(i8** list) : i32\n"));
        var withoutTypedefs = new HeaderReader(longIs64: false).Read("int A(LPSTR s); int B(LPCSTR s); int C(LPWSTR s); int D(LPCWSTR s);", "kernel32.dll", "K32");
        Check("Windows' string names without their typedefs: the const ones strings, the others buffers",
            withoutTypedefs.Contains("A(u8* s) : i32\n") && withoutTypedefs.Contains("B(str? s) : i32\n") && withoutTypedefs.Contains("C(u16* s) : i32\n") && withoutTypedefs.Contains("D(String<u16>? s) : i32\n"), withoutTypedefs);
    }

    /// <summary>`__attribute__((packed))` packs; `aligned(n)` and `__declspec(align(n))`, which the description cannot carry, make the struct opaque with the reason.</summary>
    [Fact]
    public void Packed_and_aligned_attributes_are_honoured_or_refused_with_a_reason()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            struct __attribute__((packed)) P1 { unsigned char tag; unsigned int value; };
            typedef struct { unsigned char tag; unsigned int value; } __attribute__((packed)) P2;
            typedef struct { unsigned char tag; unsigned int value; } __attribute__((__packed__, deprecated)) P3;
            typedef struct { unsigned char tag; unsigned int value; } __attribute__((aligned(16))) A1;
            struct __declspec(align(16)) A2 { unsigned char tag; unsigned int value; };
            typedef struct { unsigned char tag; unsigned int value __attribute__((aligned(8))); } F1;
            typedef struct { unsigned char tag; struct { unsigned char a; unsigned int b; } __attribute__((packed)) inner; } Outer;
            #pragma pack(push, 4)
            struct __attribute__((packed)) P4 { unsigned char tag; unsigned int value; };
            #pragma pack(pop)
            typedef struct { unsigned char tag; unsigned int value; } N1;
            __attribute__((noreturn)) void Stop(void);
            int Use(struct P1* a, P2* b, P3* c, A1* d, struct A2* e, F1* f, Outer* g, struct P4* h, N1* i);
            """, "mylib.dll", "My");
        Log(description);
        Check("packed on the tag, after the body, among other attributes, inside a #pragma pack, on a nested struct: pack 1",
            description.Contains("struct P1 pack 1 { u8 tag; u32 value }\n") && description.Contains("struct P2 pack 1 { u8 tag; u32 value }\n") && description.Contains("struct P3 pack 1 { u8 tag; u32 value }\n")
            && description.Contains("struct P4 pack 1 { u8 tag; u32 value }\n") && description.Contains("struct Outer_inner pack 1 { u8 a; u32 b }\n") && description.Contains("struct Outer { u8 tag; Outer_inner inner }\n")
            && description.Contains("struct N1 { u8 tag; u32 value }\n"));
        Check("aligned(16), __declspec(align(16)) and an attribute on a field: opaque with the reason, not a wrong layout",
            !description.Contains("struct A1") && !description.Contains("struct A2") && !description.Contains("struct F1")
            && reader.Opaque.Any(o => o.Name == "A1" && o.Why.Contains("aligned(16)")) && reader.Opaque.Any(o => o.Name == "A2" && o.Why.Contains("aligned(16)"))
            && reader.Opaque.Any(o => o.Name == "F1" && o.Why.Contains("field") && o.Why.Contains("aligned(8)")),
            string.Join("; ", reader.Opaque.Select(o => $"{o.Name}: {o.Why}")));
        Check("the functions: pointers to the packed structs, void* to the opaque ones, an attribute on a prototype dropped",
            description.Contains("Use(P1* a, P2* b, P3* c, void* d, void* e, void* f, Outer* g, P4* h, N1* i) : i32\n") && description.Contains("Stop()\n"));
        var parsed = BindingDescription.Parse(description);
        Check("layouts: the packed structs 5/1, the natural one 8/4",
            BindingDescription.Layout("P1", parsed.Structs) == (5, 1) && BindingDescription.Layout("P4", parsed.Structs) == (5, 1) && BindingDescription.Layout("N1", parsed.Structs) == (8, 4));
    }

    /// <summary>Enum members and #defines with constant expressions: evaluated, and the numbering continues from the result; what cannot be evaluated is skipped with the reason.</summary>
    [Fact]
    public void Enum_members_continue_from_an_evaluated_expression()
    {
        var reader = new HeaderReader(longIs64: false);
        string description = reader.Read("""
            #define BASE 10
            #define SHIFTED (1 << 4)
            #define BOTH (BASE + SHIFTED)
            #define MASK ~0u
            #define CAST ((unsigned long)0x10)
            #define LETTER 'A'
            #define PICK (BASE > 5 ? 1 : 2)
            #define TEXT "no"
            #define REAL 1.5
            enum Flags { A = 1 << 1, B, C, D = BASE + 2, E, F = (A | D) * 2, G, H = sizeof(int), I, J = 40, K };
            typedef struct { int items[BASE + 2]; } Sized;
            """, "mylib.dll", "My");
        Log(description);
        Check("#defines with expressions over earlier constants, casts, char literals and ?: are constants; strings and floats are not",
            description.Contains("const SHIFTED = 16\n") && description.Contains("const BOTH = 26\n") && description.Contains("const MASK = -1\n") && description.Contains("const CAST = 16\n")
            && description.Contains("const LETTER = 65\n") && description.Contains("const PICK = 1\n") && !description.Contains("TEXT") && !description.Contains("REAL"));
        Check("enum members after an expression continue from its value",
            description.Contains("const A = 2\n") && description.Contains("const B = 3\n") && description.Contains("const C = 4\n") && description.Contains("const D = 12\n")
            && description.Contains("const E = 13\n") && description.Contains("const F = 28\n") && description.Contains("const G = 29\n") && description.Contains("const J = 40\n") && description.Contains("const K = 41\n"));
        Check("a member that cannot be evaluated (sizeof) and the ones numbered after it are skipped with the reason, never given a value",
            !description.Contains("const H = ") && !description.Contains("const I = ")
            && reader.Skipped.Any(s => s.Name == "H" && s.Why.Contains("sizeof(int)")) && reader.Skipped.Any(s => s.Name == "I" && s.Why.Contains("after `H`")),
            string.Join("; ", reader.Skipped.Select(s => $"{s.Name}: {s.Why}")));
        Check("an array length from an expression", description.Contains("struct Sized { i32[12] items }\n"));
        var members = reader.Enums.Single().Members;
        Check("the enum's members: the evaluated ones, in order", members.Count == 9 && members[0] == ("A", 2) && members[6] == ("G", 29) && members[8] == ("K", 41), string.Join(", ", members));
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
