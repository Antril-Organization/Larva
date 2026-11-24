using System.Diagnostics;
using Larva.Native;

namespace Larva.Tests;

/// <summary>
/// <c>larva polyglot build --lang c</c> end to end: System.Bindings (a library compiled from its sources) as a native
/// library with its header, and a C program that uses it from several threads: handles, text both ways, enums, static
/// and instance methods, properties and fields, errors (an exception, a null handle) and giving handles back.
/// It needs MSVC and a compiled System.Core and System.Bindings (the installer's stage, or an installed larva).
/// </summary>
public sealed class PolyglotCTests : CheckedTest
{
    private static readonly Lazy<string> Folder = new(() => TestEnvironment.TempFolder("polyglot-c"));

    /// <summary>A folder with System.Core.dll and System.Bindings.dll, compiled: the repository's installer stage, else an installed larva.</summary>
    private static string? Libraries()
    {
        string[] candidates =
        [
            Path.Combine(Repository.Root, "artifacts", "installer", "stage"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Larva"),
        ];
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "System.Core.dll")) && File.Exists(Path.Combine(c, "System.Bindings.dll")));
    }

    private const string Host = """
        #include <windows.h>
        #include <stdio.h>
        #include <string.h>
        #include "system_bindings.h"

        static int failures;
        #define CHECK(what, ok) do { if (!(ok)) { printf("FAILED: %s\n", what); failures++; } } while (0)

        static int text_is(char *text, const char *expected) {
            int same = text && strcmp(text, expected) == 0;
            if (!same) printf("  got \"%s\", expected \"%s\"\n", text ? text : "(null)", expected);
            system_bindings_string_free(text);
            return same;
        }

        /* Each thread: types made, read and changed; names shaped (static functions, an enum). */
        static DWORD WINAPI work(void *argument) {
            int n = (int)(intptr_t)argument;
            for (int round = 0; round < 200; round++) {
                system_bindings_error_t error = 0;
                char name[32];
                int length = sprintf(name, "u%d*", n * 1000 + round);
                system_bindings_c_type_t type = system_bindings_c_type_new(name, (size_t)length, "", 0, &error);
                CHECK("a CType is made", type != 0 && error == 0);
                CHECK("IsPointer() of u…*", system_bindings_c_type_is_pointer(type, &error));
                CHECK("Text reads back what was given", text_is(system_bindings_c_type_get_text(type, &error), name));
                system_bindings_c_type_set_text(type, "i32", 3, &error);
                CHECK("Text set, read again", text_is(system_bindings_c_type_get_text(type, &error), "i32"));
                CHECK("IsNumber() of i32", system_bindings_c_type_is_number(type, &error));
                system_bindings_release(type);
                CHECK("Names.Pascal", text_is(system_bindings_names_pascal("window_flags", 12, &error), "WindowFlags"));
                CHECK("Names.Shape with NameCase.Snake", text_is(system_bindings_names_shape("GPUDevice", 9, SYSTEM_BINDINGS_NAME_CASE_SNAKE, &error), "gpu_device"));
                CHECK("no error in a round", error == 0);
            }
            system_bindings_thread_detach();
            return 0;
        }

        int main(void) {
            system_bindings_error_t error = 0;

            /* An exception: the error handle and its message; the result is 0. */
            system_bindings_binding_model_t model = system_bindings_binding_model_read(0, "x", 1, &error);
            CHECK("a null handle where one is needed is an error", model == 0 && error != 0);
            char *message = system_bindings_error_message(error);
            CHECK("its message names the parameter", message && strstr(message, "settings") != NULL);
            if (message) printf("error: %s\n", message);
            system_bindings_string_free(message);
            system_bindings_release(error);
            error = 0;

            /* Without an error pointer, a failure is only a 0 result. */
            CHECK("NULL for the error: still 0", system_bindings_c_type_is_pointer(0, NULL) == false);

            HANDLE threads[3];
            for (int t = 0; t < 3; t++)
                threads[t] = CreateThread(NULL, 0, work, (void *)(intptr_t)(t + 1), 0, NULL);
            work((void *)(intptr_t)0);
            WaitForMultipleObjects(3, threads, TRUE, INFINITE);
            printf("%s %d\n", failures == 0 ? "ok" : "failed", failures);
            return failures != 0;
        }
        """;

    private const string CoreHost = """
        #include <windows.h>
        #include <stdio.h>
        #include <string.h>
        #include "system_core.h"

        static int failures;
        #define CHECK(what, ok) do { if (!(ok)) { printf("FAILED: %s\n", what); failures++; } } while (0)

        static int text_is(char *text, const char *expected) {
            int same = text && strcmp(text, expected) == 0;
            if (!same) printf("  got \"%s\", expected \"%s\"\n", text ? text : "(null)", expected);
            system_core_string_free(text);
            return same;
        }

        static DWORD WINAPI work(void *argument) {
            int n = (int)(intptr_t)argument;
            for (int round = 0; round < 50; round++) {
                system_core_error_t error = 0;

                /* Values: structs in by pointer, out through a pointer. */
                system_time_span_t a, b, sum;
                system_time_span_from_seconds(90 + n, &a, &error);
                system_time_span_from_seconds(30, &b, &error);
                system_time_span_add(&a, &b, &sum, &error);
                CHECK("TimeSpan: 90 + n s + 30 s", system_time_span_get_total_seconds(&sum, &error) == 120.0 + n);
                CHECK("TimeSpan: its Seconds", system_time_span_get_seconds(&sum, &error) == n);

                /* An optional result: whether there is one, and the value. */
                system_date_time_t when;
                CHECK("DateTime.TryParse of a date", system_date_time_try_parse("2026-11-24", 10, &when, &error));
                CHECK("its year", system_date_time_get_year(&when, &error) == 2026);
                CHECK("DateTime.TryParse of nonsense: none", !system_date_time_try_parse("not a date", 10, &when, &error));

                /* Arrays: out as a block and its count; in as a pointer and a count. */
                size_t count = 0;
                uint16_t *units = system_unicode_to_utf16("a\xC3\xB1o", 4, &count, &error);
                CHECK("Unicode.ToUtf16: three units", units && count == 3 && units[0] == 'a' && units[1] == 0xF1 && units[2] == 'o');
                system_core_free(units);
                uint8_t x[] = { 1, 2, 3 }, y[] = { 1, 2, 3 }, z[] = { 1, 2, 4 };
                CHECK("Array.Equals of equal arrays", system_array_equals(x, 3, y, 3, &error));
                CHECK("Array.Equals of different ones", !system_array_equals(x, 3, z, 3, &error));

                /* A list: the library's own, made, filled, written, read back. */
                char path[MAX_PATH];
                sprintf(path, "lines-%d-%d.txt", n, round);
                system_core_list_str_t lines = system_core_list_str_new(&error);
                system_core_list_str_add(lines, "uno", 3, &error);
                system_core_list_str_add(lines, "dos", 3, &error);
                system_core_list_str_add(lines, "tres", 4, &error);
                system_core_list_str_set(lines, 1, "DOS", 3, &error);
                system_core_list_str_remove_at(lines, 0, &error);
                system_io_file_write_all_lines(path, strlen(path), lines, &error);
                system_core_list_str_t read = system_io_file_read_all_lines(path, strlen(path), &error);
                CHECK("the list read back has 2 lines", system_core_list_str_count(read, &error) == 2);
                CHECK("its first", text_is(system_core_list_str_get(read, 0, &error), "DOS"));
                CHECK("its second", text_is(system_core_list_str_get(read, 1, &error), "tres"));
                system_core_list_str_clear(read, &error);
                CHECK("cleared", system_core_list_str_count(read, &error) == 0);
                CHECK("no error in a round", error == 0);
                system_core_release(lines);
                system_core_release(read);
                DeleteFileA(path);

                /* Past the end of a list: an error, not a crash. */
                system_core_list_str_t empty = system_core_list_str_new(NULL);
                char *none = system_core_list_str_get(empty, 5, &error);
                CHECK("reading past the end is an error", none == NULL && error != 0);
                system_core_release(error);
                system_core_release(empty);
            }
            system_core_thread_detach();
            return 0;
        }

        int main(void) {
            HANDLE threads[3];
            for (int t = 0; t < 3; t++)
                threads[t] = CreateThread(NULL, 0, work, (void *)(intptr_t)(t + 1), 0, NULL);
            work((void *)(intptr_t)0);
            WaitForMultipleObjects(3, threads, TRUE, INFINITE);
            printf("%s %d\n", failures == 0 ? "ok" : "failed", failures);
            return failures != 0;
        }
        """;

    /// <summary>
    /// Callbacks from C: Parallel.For calls a C function on the library's threads, Mutex.Run and Thread on the caller's and
    /// another; a callback that fails (callback_error) is the caller's error; one calls the library again; the contexts
    /// are released once the library lets the callbacks go (collections).
    /// </summary>
    private const string CallbackHost = """
        #include <windows.h>
        #include <stdio.h>
        #include <string.h>
        #include "system_core.h"

        static volatile LONG sum, calls, released, inner;
        static int failures;
        #define CHECK(what, ok) do { if (!(ok)) { printf("FAILED: %s\n", what); failures++; } } while (0)

        static void add(void *context, int32_t i, system_core_error_t *error) {
            (void)error;
            InterlockedAdd(&sum, i * (int)(intptr_t)context);
        }

        static void count(void *context, system_core_error_t *error) {
            (void)context; (void)error;
            InterlockedIncrement(&calls);
        }

        static void fails(void *context, system_core_error_t *error) {
            (void)context;
            system_core_callback_error(error, "boom from C", 11);
        }

        /* A callback that calls the library again (from inside the call out). */
        static void nested(void *context, system_core_error_t *error) {
            (void)context;
            system_time_span_t span;
            system_time_span_from_seconds(42, &span, error);
            if (system_time_span_get_seconds(&span, error) == 42)
                InterlockedIncrement(&inner);
        }

        static void release(void *context) { (void)context; InterlockedIncrement(&released); }

        int main(void) {
            system_core_error_t error = 0;
            system_threading_parallel_for(0, 1000, add, (void *)(intptr_t)2, release, &error);
            CHECK("Parallel.For called back on Larva's threads", sum == 999000 && error == 0);

            system_threading_mutex_t mutex = system_threading_mutex_new(&error);
            for (int i = 0; i < 100; i++)
                system_threading_mutex_run(mutex, count, NULL, release, &error);
            CHECK("Mutex.Run called back 100 times", calls == 100 && error == 0);

            system_threading_mutex_run(mutex, fails, NULL, NULL, &error);
            char *message = error ? system_core_error_message(error) : NULL;
            CHECK("a failing callback is the caller's error", message && strstr(message, "boom from C"));
            system_core_string_free(message);
            system_core_release(error);
            error = 0;

            system_threading_mutex_run(mutex, nested, NULL, NULL, &error);
            CHECK("a callback calls the library again", inner == 1 && error == 0);

            for (int i = 0; i < 20; i++) {
                system_threading_thread_t thread = system_threading_thread_new(count, NULL, release, &error);
                system_threading_thread_start(thread, &error);
                system_threading_thread_join(thread, &error);
                system_core_release(thread);
            }
            CHECK("Thread runs the callback on its thread", calls == 120 && error == 0);

            /* The library no longer holds them: collections release their contexts. */
            for (int i = 0; i < 20 && released < 100; i++) {
                system_gc_collect(&error);
                system_time_span_t s;
                system_time_span_from_seconds(i, &s, &error);
            }
            printf("released %ld of 121\n", (long)released);
            CHECK("contexts are released once the library lets the callbacks go", released > 0);
            printf("%s %d\n", failures == 0 ? "ok" : "failed", failures);
            return failures;
        }
        """;

    /// <summary>
    /// System.Core for C: value types as structs (TimeSpan, DateTime), an optional result (TryParse), arrays both ways,
    /// and List&lt;str&gt; as a handle of its own (made in C, given to File.WriteAllLines, and one File.ReadAllLines gives).
    /// </summary>
    [Fact]
    public void Values_arrays_lists_and_optionals_cross_to_C()
    {
        var toolchain = Toolchain.Find();
        Assert.SkipWhen(toolchain is null, Toolchain.Missing);
        string? libraries = Libraries();
        Assert.SkipWhen(libraries is null, "no compiled System.Core (the installer's stage, or an installed larva)");
        string folder = Path.Combine(Folder.Value, "core");
        string core = Path.Combine(libraries!, "System.Core.dll");
        // Task.GetResult() and the property Task.Result meet in C (get_result): the method is left out.
        var (code, log) = Repository.Cli(null, 900_000, "polyglot", "build", core, "--core", core, "--lang", "c", "-o", folder,
            "--exclude", "System.Threading.Tasks.Task.GetResult");
        Check("larva polyglot build System.Core --lang c", code == 0, log.Trim().Length > 2000 ? log.Trim()[^2000..] : log.Trim());
        if (code != 0)
            return;
        folder = Path.Combine(folder, "c");
        string declared = File.ReadAllText(Path.Combine(folder, "system_core.h"));
        Check("the header declares value types as structs, lists as handles, arrays with counts and optionals as bool + result",
            declared.Contains("typedef struct system_time_span\n{\n    int64_t ticks;\n} system_time_span_t;")
            && declared.Contains("typedef uint64_t system_core_list_str_t;")
            && declared.Contains("uint16_t *system_unicode_to_utf16(const char *text, size_t text_length, size_t *result_count, system_core_error_t *error);")
            && declared.Contains("bool system_date_time_try_parse(const char *text, size_t text_length, system_date_time_t *result, system_core_error_t *error);"));
        string source = Path.Combine(folder, "host.c"), exe = Path.Combine(folder, "host.exe");
        File.WriteAllText(source, CoreHost);
        var (compiled, compileLog) = toolchain!.CompileHost(source, exe, [Path.Combine(folder, "system_core.lib")]);
        Check("the C program compiles against system_core.h", compiled, compileLog);
        if (!compiled)
            return;
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = folder };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(120_000);
        if (!exited)
            process.Kill(entireProcessTree: true);
        Check("four threads: values, optionals, arrays and lists hold every check", exited && process.ExitCode == 0 && output.Result.TrimEnd().EndsWith("ok 0"),
            $"exit {(exited ? process.ExitCode : -1)}: {output.Result.Trim()} {errors.Result.Trim()}");

        // Callbacks.
        string callbacks = Path.Combine(folder, "callbacks.c"), callbacksExe = Path.Combine(folder, "callbacks.exe");
        File.WriteAllText(callbacks, CallbackHost);
        (compiled, compileLog) = toolchain.CompileHost(callbacks, callbacksExe, [Path.Combine(folder, "system_core.lib")]);
        Check("the callbacks program compiles", compiled, compileLog);
        if (!compiled)
            return;
        using var run = Process.Start(new ProcessStartInfo(callbacksExe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = folder })!;
        var said = run.StandardOutput.ReadToEndAsync();
        var complained = run.StandardError.ReadToEndAsync();
        bool ended = run.WaitForExit(120_000);
        if (!ended)
            run.Kill(entireProcessTree: true);
        Check("callbacks from C: on the library's threads, failing, calling back in, released", ended && run.ExitCode == 0 && said.Result.TrimEnd().EndsWith("ok 0"),
            $"exit {(ended ? run.ExitCode : -1)}: {said.Result.Trim()} {complained.Result.Trim()}");
    }

    [Fact]
    public void A_library_for_C_is_used_from_C()
    {
        var toolchain = Toolchain.Find();
        Assert.SkipWhen(toolchain is null, Toolchain.Missing);
        string? libraries = Libraries();
        Assert.SkipWhen(libraries is null, "no compiled System.Core and System.Bindings (the installer's stage, or an installed larva)");
        string folder = Path.Combine(Folder.Value, "bindings");
        var (code, log) = Repository.Cli(null, 600_000, "polyglot", "build", Path.Combine(libraries!, "System.Bindings.dll"),
            "--core", Path.Combine(libraries!, "System.Core.dll"), "--lang", "c", "-o", folder);
        Check("larva polyglot build --lang c", code == 0, log.Trim());
        if (code != 0)
            return;
        Check("C only: no C# folder", !Directory.Exists(Path.Combine(folder, "csharp")));
        folder = Path.Combine(folder, "c");
        string header = Path.Combine(folder, "system_bindings.h");
        Check("the header, the library and its import library are written",
            File.Exists(header) && File.Exists(Path.Combine(folder, "system_bindings.dll")) && File.Exists(Path.Combine(folder, "system_bindings.lib")));
        string declared = File.ReadAllText(header);
        Check("the header declares handle types, an enum, text parameters with lengths and the error last",
            declared.Contains("typedef uint64_t system_bindings_c_type_t;") && declared.Contains("SYSTEM_BINDINGS_NAME_CASE_SNAKE = 4,")
            && declared.Contains("system_bindings_c_type_new(const char *text, size_t text_length, const char *typedef_, size_t typedef_length, system_bindings_error_t *error);"));
        Check("the header does not say what language the library was written in", !declared.Contains("Antril", StringComparison.OrdinalIgnoreCase));

        string source = Path.Combine(folder, "host.c"), exe = Path.Combine(folder, "host.exe");
        File.WriteAllText(source, Host);
        var (compiled, compileLog) = toolchain!.CompileHost(source, exe, [Path.Combine(folder, "system_bindings.lib")]);
        Check("the C program compiles against the header and links the import library", compiled, compileLog);
        if (!compiled)
            return;
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = folder };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(120_000);
        if (!exited)
            process.Kill(entireProcessTree: true);
        Check("four threads use the library from C: every check holds", exited && process.ExitCode == 0 && output.Result.TrimEnd().EndsWith("ok 0"),
            $"exit {(exited ? process.ExitCode : -1)}: {output.Result.Trim()} {errors.Result.Trim()}");
    }
}
