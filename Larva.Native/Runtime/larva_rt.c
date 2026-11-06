/*
 * Larva native runtime for ahead-of-time executables: Windows x64 (MSVC or clang-cl), Linux and macOS (gcc or clang).
 *
 * The same C ABI as the in-process runtime the JITs use (Larva.Native.NativeRuntime): heap cells, gc objects with a
 * header before the payload (reference count, size, arena region, mark bit), arrays, regions, pins, polls and panics.
 *
 * Tracing collection is generational. Young objects are made in a nursery (bump allocation in zeroed blocks, inline in
 * compiled code) and a minor collection copies the reachable ones out to the old generation, updating the references to
 * them: every object carries its type (a map of where it holds handles, emitted by the back end), so its fields are known
 * precisely. The stack, registers, heap cells and globals are scanned conservatively (any word that points into an
 * object); a young object such a word points at is pinned instead of copied, and its block stays. The old generation is
 * a non-moving mark-sweep (size-class chunks and large blocks), marked precisely through the types; objects made without
 * a type are scanned word by word and never move. Heap cells (owned memory: arrays, boxes) are traced like objects: one
 * stays while a root, a live object or a live cell holds its address, and is freed otherwise.
 *
 * Exceptions are Windows SEH (Larva code raises them with RaiseException); the entry point wraps the program in a
 * __try/__except that reports a Larva exception nobody caught.
 *
 * On Linux and macOS (POSIX) code is compiled by LLVM with exceptions as a flag (larva_exception, set by a throw and
 * checked after every call) and without stack maps: the slots that may hold handles stay in memory, so the collector
 * scans the thread's stack conservatively (after spilling the callee-saved registers), like heap cells.
 *
 * A collection is due once as many objects were allocated as there were alive after the last one (at least 4096), and as
 * many bytes: the work of collecting (marking what lives) stays proportional to the work of allocating, however large the
 * heap grows. Arrays of numbers are marked without being scanned.
 *
 * Threads (System.Threading) run in parallel. While more than one runs, each allocates young objects in a nursery block
 * of its own (through the runtime: compiled code's inline path is off) and the runtime's shared state is locked. A
 * collection stops the world: the thread that starts it waits until every other one is stopped at a safepoint (every
 * loop of a program that starts threads polls) or blocked in the runtime (sleeping, joining, waiting for a lock or a
 * condition, reading), each having saved its registers and stack pointer, and scans every stack.
 *
 * Environment: LARVA_GC_STRESS=1 collects at every safepoint, LARVA_GC_THRESHOLD=n sets the allocations between
 * collections, LARVA_STATS=1 prints counters to stderr at exit, LARVA_GC_QUARANTINE=1 poisons collected objects and never
 * reuses their memory (a use after collection then reads the poison every run), LARVA_GC_NOSMALL=1 puts every object on the
 * general path (no small-object chunks), to tell their bugs apart.
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <math.h>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <psapi.h>
#include <intrin.h>
#include <shellapi.h>
#include <malloc.h>
#define larva_exit(code) ExitProcess(code)
#define larva_aligned_alloc(size, align) _aligned_malloc(size, align)
#define larva_aligned_free(block) _aligned_free(block)
#else
#include <pthread.h>
#include <sched.h>
#include <setjmp.h>
#include <unistd.h>
#include <sys/mman.h>
#define larva_exit(code) (fflush(stdout), fflush(stderr), _Exit(code))
static void *larva_aligned_alloc(size_t size, size_t align) {
    void *block = NULL;
    return posix_memalign(&block, align < sizeof(void *) ? sizeof(void *) : align, size) == 0 ? block : NULL;
}
#define larva_aligned_free(block) free(block)
#define _strtoi64 strtoll
#endif

/*
 * COLD: code that runs once or rarely (start-up, the command line, files and folders, numbers as text, crash reports,
 * starting threads) is compiled for size between #pragma optimize("s", on) and ("", on); the collector, allocation,
 * polls, barriers, copies and writes stay compiled for speed. (MSVC; other compilers ignore the pragma.)
 */
#ifndef _MSC_VER
#pragma GCC diagnostic ignored "-Wunknown-pragmas"
#endif

/* The shared runtime (larva_rt.dll) and POSIX keep the diagnostics (see "Diagnostics" below); Windows programs have them
   when built with LARVA_DIAGNOSTICS=1. */
#if (defined(LARVA_SHARED_RUNTIME) || !defined(_WIN32)) && !defined(LARVA_DIAGNOSTICS)
#define LARVA_DIAGNOSTICS 1
#endif

/*
 * ── Without the C runtime (Windows executables and DLLs) ──────────────────────────────────────────────────────────
 *
 * Programs link no C library: only kernel32 and ntdll (which also gives memcmp, strlen, wcslen, __chkstk and
 * __C_specific_handler, the SEH handler compiled code uses). memcpy, memmove and memset are here (ntdll's are slower). What the runtime used from the C
 * library is here, under the same names (macros), on Windows' own services:
 *
 * - memory: the process heap (HeapAlloc);
 * - stdin, stdout, stderr and files: handles with a buffer of their own (WriteFile, ReadFile), stdout and stderr in text
 *   mode as the C library had them ("\n" written as "\r\n", "\r\n" read as "\n"), a console written at once;
 * - printf and friends for what the runtime prints (integers, strings, pointers, %g and %f of doubles); doubles to text
 *   and back (strtod) exactly, correctly rounded (ties to even), as the C library did;
 * - the transcendental math functions (sin, exp, pow…) from ucrtbase.dll, loaded the first time one is used: the same
 *   results as before and as .NET (which uses them too); sqrt, floor and ceil here;
 * - the entry point (the command line split as the C library did, in UTF-8), thread-local storage's directory, _fltused.
 *
 * LARVA_SHARED_RUNTIME (larva_rt.dll, shared by Larva DLLs) keeps the C library, and so does LARVA_WITH_CRT (to compare).
 */
#if defined(_WIN32) && !defined(LARVA_SHARED_RUNTIME) && !defined(LARVA_WITH_CRT)
#define LARVA_NO_CRT 1

#pragma function(memcpy, memset, memmove)
#pragma function(sin, cos, tan, asin, acos, atan, atan2, exp, log, log10, pow, fmod, floor, ceil, sqrt, sqrtf, floorf, ceilf)

int _fltused = 0x9875;

/* Thread-local storage (__declspec(thread)): the directory the loader reads, as the C library's tlssup made it. */
ULONG _tls_index = 0;
#pragma data_seg(".tls")
char _tls_start = 0;
#pragma data_seg(".tls$ZZZ")
char _tls_end = 0;
#pragma data_seg()
#pragma section(".CRT$XLA", long, read)
#pragma section(".CRT$XLZ", long, read)
__declspec(allocate(".CRT$XLA")) PIMAGE_TLS_CALLBACK __xl_a = 0;
__declspec(allocate(".CRT$XLZ")) PIMAGE_TLS_CALLBACK __xl_z = 0;
#pragma section(".rdata$T", long, read)
__declspec(allocate(".rdata$T")) const IMAGE_TLS_DIRECTORY64 _tls_used = {
    (ULONGLONG)&_tls_start, (ULONGLONG)&_tls_end, (ULONGLONG)&_tls_index, (ULONGLONG)(&__xl_a + 1), 0, 0,
};

#pragma optimize("s", on) /* cold: compiled for size (see COLD) */
/* ── Memory ── */

static HANDLE rt_heap;

static HANDLE rt_process_heap(void) {
    if (!rt_heap)
        rt_heap = GetProcessHeap();
    return rt_heap;
}

static void *rt_malloc(size_t size) { return HeapAlloc(rt_process_heap(), 0, size ? size : 1); }
static void *rt_calloc(size_t count, size_t size) { return HeapAlloc(rt_process_heap(), HEAP_ZERO_MEMORY, count * size ? count * size : 1); }
static void rt_free(void *block) {
    if (block)
        HeapFree(rt_process_heap(), 0, block);
}
static void *rt_realloc(void *block, size_t size) {
    if (!block)
        return rt_malloc(size);
    if (!size) {
        rt_free(block);
        return NULL;
    }
    return HeapReAlloc(rt_process_heap(), 0, block, size);
}

/* Aligned blocks: the heap's block is kept just before the aligned address. */
static void *rt_aligned_malloc(size_t size, size_t align) {
    if (align < sizeof(void *))
        align = sizeof(void *);
    uint8_t *raw = (uint8_t *)rt_malloc(size + align + sizeof(void *));
    if (!raw)
        return NULL;
    uintptr_t aligned = ((uintptr_t)raw + sizeof(void *) + align - 1) & ~(uintptr_t)(align - 1);
    ((void **)aligned)[-1] = raw;
    return (void *)aligned;
}
static void rt_aligned_free(void *block) {
    if (block)
        rt_free(((void **)block)[-1]);
}

static char *rt_strcpy(char *to, const char *from) {
    char *at = to;
    while ((*at++ = *from++) != 0) {
    }
    return to;
}

static wchar_t *rt_wcscpy(wchar_t *to, const wchar_t *from) {
    wchar_t *at = to;
    while ((*at++ = *from++) != 0) {
    }
    return to;
}

static int rt_wcscmp(const wchar_t *a, const wchar_t *b) {
    while (*a && *a == *b) {
        a++;
        b++;
    }
    return (int)*a - (int)*b;
}


static int rt_strcmp(const char *a, const char *b) {
    while (*a && *a == *b) {
        a++;
        b++;
    }
    return (int)(unsigned char)*a - (int)(unsigned char)*b;
}

/* qsort: a heap sort (the runtime sorts addresses and names: no recursion, no extra memory). */
static void rt_swap(uint8_t *a, uint8_t *b, size_t size) {
    for (size_t i = 0; i < size; i++) {
        uint8_t t = a[i];
        a[i] = b[i];
        b[i] = t;
    }
}
static void rt_sift(uint8_t *base, size_t root, size_t count, size_t size, int (*compare)(const void *, const void *)) {
    for (;;) {
        size_t child = 2 * root + 1;
        if (child >= count)
            return;
        if (child + 1 < count && compare(base + child * size, base + (child + 1) * size) < 0)
            child++;
        if (compare(base + root * size, base + child * size) >= 0)
            return;
        rt_swap(base + root * size, base + child * size, size);
        root = child;
    }
}
static void rt_qsort(void *items, size_t count, size_t size, int (*compare)(const void *, const void *)) {
    uint8_t *base = (uint8_t *)items;
    if (count < 2)
        return;
    for (size_t i = count / 2; i > 0; i--)
        rt_sift(base, i - 1, count, size, compare);
    for (size_t end = count - 1; end > 0; end--) {
        rt_swap(base, base + end * size, size);
        rt_sift(base, 0, end, size, compare);
    }
}

static int64_t rt_strtoi64(const char *text, char **end, int base) {
    (void)base;
    while (*text == ' ' || *text == '\t')
        text++;
    int negative = *text == '-';
    if (*text == '-' || *text == '+')
        text++;
    uint64_t value = 0;
    while (*text >= '0' && *text <= '9')
        value = value * 10 + (uint64_t)(*text++ - '0');
    if (end)
        *end = (char *)text;
    return negative ? -(int64_t)value : (int64_t)value;
}

static int64_t rt_atoll(const char *text) { return rt_strtoi64(text, NULL, 10); }

/* getenv: the value in one of a few buffers kept around (callers read it right away). */
static char *rt_getenv(const char *name) {
    static char *kept[8];
    static LONG next;
    DWORD size = GetEnvironmentVariableA(name, NULL, 0);
    if (!size)
        return NULL;
    char *value = (char *)rt_malloc(size);
    if (!value || GetEnvironmentVariableA(name, value, size) >= size) {
        rt_free(value);
        return NULL;
    }
    LONG slot = (InterlockedIncrement(&next) - 1) & 7;
    char *old = (char *)InterlockedExchangePointer((void **)&kept[slot], value);
    rt_free(old);
    return value;
}

static int64_t rt_time(int64_t *out) {
    FILETIME now;
    GetSystemTimeAsFileTime(&now);
    int64_t seconds = (int64_t)((((uint64_t)now.dwHighDateTime << 32 | now.dwLowDateTime) - 116444736000000000ULL) / 10000000ULL);
    if (out)
        *out = seconds;
    return seconds;
}

/* clock(): milliseconds since the process started, as the C library gives it on Windows. */
static LARGE_INTEGER rt_started;
static long rt_clock(void) {
    LARGE_INTEGER now, frequency;
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&frequency);
    if (!rt_started.QuadPart)
        rt_started = now;
    return (long)((now.QuadPart - rt_started.QuadPart) * 1000 / frequency.QuadPart);
}

#pragma optimize("", on)
/* ── Memory copies ── */
/*
 * memcpy, memmove and memset (compiled code and the runtime call them; ntdll's are slower than the C library's: a third of
 * the speed for large blocks). SSE2, unaligned 16-byte moves: up to 64 bytes with moves that overlap at the ends, then 64
 * a step (the destination aligned first), and past 2 MB stores that bypass the cache (the block will not fit in it).
 */
#define RT_STREAM_FROM (2u << 20)

/* Whether the processor has AVX (and the system saves its registers): then the loops move 32 bytes at a time. 0 unknown. */
static int rt_avx;

static int rt_has_avx(void) {
    if (rt_avx == 0) {
        int info[4];
        __cpuid(info, 1);
        int ok = (info[2] & (1 << 27)) && (info[2] & (1 << 28)); /* OSXSAVE and AVX */
        if (ok)
            ok = (_xgetbv(0) & 6) == 6; /* the system saves XMM and YMM */
        rt_avx = ok ? 1 : -1;
    }
    return rt_avx > 0;
}

static void rt_copy_forward(uint8_t *d, const uint8_t *s, size_t n) {
    if (n <= 16) {
        if (n >= 8) {
            uint64_t a = *(const uint64_t *)s, b = *(const uint64_t *)(s + n - 8);
            *(uint64_t *)d = a;
            *(uint64_t *)(d + n - 8) = b;
        } else if (n >= 4) {
            uint32_t a = *(const uint32_t *)s, b = *(const uint32_t *)(s + n - 4);
            *(uint32_t *)d = a;
            *(uint32_t *)(d + n - 4) = b;
        } else if (n) {
            uint8_t a = s[0], b = s[n / 2], c = s[n - 1];
            d[0] = a;
            d[n / 2] = b;
            d[n - 1] = c;
        }
        return;
    }
    if (n <= 64) {
        __m128i a = _mm_loadu_si128((const __m128i *)s);
        __m128i b = _mm_loadu_si128((const __m128i *)(s + 16));
        __m128i c = _mm_loadu_si128((const __m128i *)(s + n - 32));
        __m128i e = _mm_loadu_si128((const __m128i *)(s + n - 16));
        if (n <= 32) {
            _mm_storeu_si128((__m128i *)d, a);
            _mm_storeu_si128((__m128i *)(d + n - 16), e);
        } else {
            _mm_storeu_si128((__m128i *)d, a);
            _mm_storeu_si128((__m128i *)(d + 16), b);
            _mm_storeu_si128((__m128i *)(d + n - 32), c);
            _mm_storeu_si128((__m128i *)(d + n - 16), e);
        }
        return;
    }
    /* The last 64 bytes first (loaded before anything is written), then 64 a step from an aligned destination. */
    __m128i t0 = _mm_loadu_si128((const __m128i *)(s + n - 64));
    __m128i t1 = _mm_loadu_si128((const __m128i *)(s + n - 48));
    __m128i t2 = _mm_loadu_si128((const __m128i *)(s + n - 32));
    __m128i t3 = _mm_loadu_si128((const __m128i *)(s + n - 16));
    uint8_t *tail = d + n - 64;
    __m128i head = _mm_loadu_si128((const __m128i *)s);
    size_t skip = 16 - ((uintptr_t)d & 15);
    _mm_storeu_si128((__m128i *)d, head);
    d += skip;
    s += skip;
    n -= skip;
    if (rt_has_avx() && n < RT_STREAM_FROM) {
        for (; n > 64; n -= 64, d += 64, s += 64) {
            __m256i a = _mm256_loadu_si256((const __m256i *)s);
            __m256i b = _mm256_loadu_si256((const __m256i *)(s + 32));
            _mm256_storeu_si256((__m256i *)d, a);
            _mm256_storeu_si256((__m256i *)(d + 32), b);
        }
        _mm256_zeroupper();
    } else if (n >= RT_STREAM_FROM) {
        for (; n > 64; n -= 64, d += 64, s += 64) {
            _mm_stream_si128((__m128i *)d, _mm_loadu_si128((const __m128i *)s));
            _mm_stream_si128((__m128i *)(d + 16), _mm_loadu_si128((const __m128i *)(s + 16)));
            _mm_stream_si128((__m128i *)(d + 32), _mm_loadu_si128((const __m128i *)(s + 32)));
            _mm_stream_si128((__m128i *)(d + 48), _mm_loadu_si128((const __m128i *)(s + 48)));
        }
        _mm_sfence();
    } else {
        for (; n > 64; n -= 64, d += 64, s += 64) {
            __m128i a = _mm_loadu_si128((const __m128i *)s);
            __m128i b = _mm_loadu_si128((const __m128i *)(s + 16));
            __m128i c = _mm_loadu_si128((const __m128i *)(s + 32));
            __m128i e = _mm_loadu_si128((const __m128i *)(s + 48));
            _mm_store_si128((__m128i *)d, a);
            _mm_store_si128((__m128i *)(d + 16), b);
            _mm_store_si128((__m128i *)(d + 32), c);
            _mm_store_si128((__m128i *)(d + 48), e);
        }
    }
    _mm_storeu_si128((__m128i *)tail, t0);
    _mm_storeu_si128((__m128i *)(tail + 16), t1);
    _mm_storeu_si128((__m128i *)(tail + 32), t2);
    _mm_storeu_si128((__m128i *)(tail + 48), t3);
}

/*
 * Overlapping blocks: 16 bytes a step, each loaded before it is stored, in the direction that reads every byte before it is
 * written over (up when the destination is below the source, down when above), then the rest a byte at a time.
 */
static void rt_move_overlapping(uint8_t *d, const uint8_t *s, size_t n) {
    if (d < s) {
        size_t i = 0;
        for (; i + 16 <= n; i += 16)
            _mm_storeu_si128((__m128i *)(d + i), _mm_loadu_si128((const __m128i *)(s + i)));
        for (; i < n; i++)
            d[i] = s[i];
    } else {
        size_t i = n;
        for (; i >= 16; i -= 16)
            _mm_storeu_si128((__m128i *)(d + i - 16), _mm_loadu_si128((const __m128i *)(s + i - 16)));
        while (i > 0) {
            i--;
            d[i] = s[i];
        }
    }
}

void *__cdecl memcpy(void *to, const void *from, size_t count) {
    rt_copy_forward((uint8_t *)to, (const uint8_t *)from, count);
    return to;
}

void *__cdecl memmove(void *to, const void *from, size_t count) {
    uint8_t *d = (uint8_t *)to;
    const uint8_t *s = (const uint8_t *)from;
    if (d < s + count && s < d + count && d != s)
        rt_move_overlapping(d, s, count);
    else
        rt_copy_forward(d, s, count);
    return to;
}

void *__cdecl memset(void *to, int value, size_t count) {
    uint8_t *d = (uint8_t *)to;
    uint64_t byte = (uint8_t)value * 0x0101010101010101ULL;
    if (count <= 16) {
        if (count >= 8) {
            *(uint64_t *)d = byte;
            *(uint64_t *)(d + count - 8) = byte;
        } else if (count >= 4) {
            *(uint32_t *)d = (uint32_t)byte;
            *(uint32_t *)(d + count - 4) = (uint32_t)byte;
        } else if (count) {
            d[0] = d[count / 2] = d[count - 1] = (uint8_t)value;
        }
        return to;
    }
    __m128i v = _mm_set1_epi8((char)value);
    if (count <= 64) {
        _mm_storeu_si128((__m128i *)d, v);
        _mm_storeu_si128((__m128i *)(d + count - 16), v);
        if (count > 32) {
            _mm_storeu_si128((__m128i *)(d + 16), v);
            _mm_storeu_si128((__m128i *)(d + count - 32), v);
        }
        return to;
    }
    uint8_t *end = d + count;
    _mm_storeu_si128((__m128i *)d, v);
    uint8_t *at = (uint8_t *)(((uintptr_t)d + 16) & ~(uintptr_t)15);
    if (rt_has_avx() && count < RT_STREAM_FROM) {
        __m256i w = _mm256_set1_epi8((char)value);
        for (; at + 64 <= end; at += 64) {
            _mm256_store_si256((__m256i *)at, w);
            _mm256_store_si256((__m256i *)(at + 32), w);
        }
        _mm256_zeroupper();
    } else if (count >= RT_STREAM_FROM) {
        for (; at + 64 <= end; at += 64) {
            _mm_stream_si128((__m128i *)at, v);
            _mm_stream_si128((__m128i *)(at + 16), v);
            _mm_stream_si128((__m128i *)(at + 32), v);
            _mm_stream_si128((__m128i *)(at + 48), v);
        }
        _mm_sfence();
    } else {
        for (; at + 64 <= end; at += 64) {
            _mm_store_si128((__m128i *)at, v);
            _mm_store_si128((__m128i *)(at + 16), v);
            _mm_store_si128((__m128i *)(at + 32), v);
            _mm_store_si128((__m128i *)(at + 48), v);
        }
    }
    _mm_storeu_si128((__m128i *)(end - 64), v);
    _mm_storeu_si128((__m128i *)(end - 48), v);
    _mm_storeu_si128((__m128i *)(end - 32), v);
    _mm_storeu_si128((__m128i *)(end - 16), v);
    return to;
}

/* ── Files ── */

#define RT_READ 1
#define RT_WRITE 2
#define RT_TEXT 4
#define RT_CONSOLE 8

typedef struct RtFile {
    HANDLE handle;
    int flags;
    int error;
    SRWLOCK lock;
    uint8_t *buffer;
    size_t capacity;
    size_t start, end;   /* read: what is buffered and not taken yet */
    size_t pending;      /* write: what waits to be written */
    int peeked;          /* text mode: a byte read ahead after '\r' (-1: none) */
} RtFile;

static RtFile rt_standard[3];
static volatile LONG rt_standard_ready;

static void rt_standard_open(void) {
    static SRWLOCK once = SRWLOCK_INIT;
    AcquireSRWLockExclusive(&once);
    if (!rt_standard_ready) {
        DWORD ids[3] = { STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE };
        for (int i = 0; i < 3; i++) {
            RtFile *file = &rt_standard[i];
            InitializeSRWLock(&file->lock);
            file->handle = GetStdHandle(ids[i]);
            file->flags = (i == 0 ? RT_READ : RT_WRITE) | RT_TEXT;
            DWORD mode;
            if (file->handle && file->handle != INVALID_HANDLE_VALUE && GetConsoleMode(file->handle, &mode))
                file->flags |= RT_CONSOLE;
            file->peeked = -1;
        }
        rt_standard_ready = 1;
    }
    ReleaseSRWLockExclusive(&once);
}

static RtFile *rt_std(int which) {
    if (!rt_standard_ready)
        rt_standard_open();
    return &rt_standard[which];
}

#undef stdin
#undef stdout
#undef stderr
#define stdin rt_std(0)
#define stdout rt_std(1)
#define stderr rt_std(2)
#define FILE RtFile

static int rt_write_all(RtFile *file, const uint8_t *bytes, size_t count) {
    while (count > 0) {
        DWORD wrote = 0;
        DWORD chunk = count > 0x40000000 ? 0x40000000 : (DWORD)count;
        if (!WriteFile(file->handle, bytes, chunk, &wrote, NULL) || wrote == 0) {
            file->error = 1;
            return -1;
        }
        bytes += wrote;
        count -= wrote;
    }
    return 0;
}

static int rt_flush_unlocked(RtFile *file) {
    if (file->pending == 0)
        return 0;
    size_t count = file->pending;
    file->pending = 0;
    return rt_write_all(file, file->buffer, count);
}

/* Before writing: what was read ahead is given back (the position goes back to where reading stopped). */
static void rt_drop_read(RtFile *file) {
    if (file->end > file->start && !(file->flags & RT_CONSOLE)) {
        LARGE_INTEGER back;
        back.QuadPart = -(int64_t)(file->end - file->start);
        SetFilePointerEx(file->handle, back, NULL, FILE_CURRENT);
    }
    file->start = file->end = 0;
}

static int rt_buffer(RtFile *file) {
    if (file->buffer)
        return 1;
    file->capacity = 65536;
    file->buffer = (uint8_t *)rt_malloc(file->capacity);
    return file->buffer != NULL;
}

static void rt_put_unlocked(RtFile *file, const uint8_t *bytes, size_t count) {
    if (!(file->flags & RT_WRITE)) {
        file->error = 1;
        return;
    }
    rt_drop_read(file);
    if (!rt_buffer(file)) {
        rt_write_all(file, bytes, count);
        return;
    }
    if (file->flags & RT_TEXT) {
        for (size_t i = 0; i < count; i++) {
            if (file->pending + 2 > file->capacity)
                rt_flush_unlocked(file);
            if (bytes[i] == '\n')
                file->buffer[file->pending++] = '\r';
            file->buffer[file->pending++] = bytes[i];
        }
    } else if (count >= file->capacity) {
        rt_flush_unlocked(file);
        rt_write_all(file, bytes, count);
    } else {
        if (file->pending + count > file->capacity)
            rt_flush_unlocked(file);
        memcpy(file->buffer + file->pending, bytes, count);
        file->pending += count;
    }
}

/* A console is written at once (what a person reads appears as it is printed); stderr too. */
static void rt_after_write(RtFile *file) {
    if ((file->flags & RT_CONSOLE) || file == &rt_standard[2])
        rt_flush_unlocked(file);
}

static size_t rt_fwrite(const void *bytes, size_t size, size_t count, RtFile *file) {
    if (!file)
        return 0;
    AcquireSRWLockExclusive(&file->lock);
    rt_put_unlocked(file, (const uint8_t *)bytes, size * count);
    rt_after_write(file);
    int failed = file->error;
    ReleaseSRWLockExclusive(&file->lock);
    return failed ? 0 : count;
}

static int rt_fputs(const char *text, RtFile *file) { return rt_fwrite(text, 1, strlen(text), file) ? 0 : -1; }

static int rt_putchar(int c) {
    uint8_t byte = (uint8_t)c;
    return rt_fwrite(&byte, 1, 1, stdout) ? c : -1;
}

static int rt_fputc(int c, RtFile *file) {
    uint8_t byte = (uint8_t)c;
    return rt_fwrite(&byte, 1, 1, file) ? c : -1;
}

static int rt_puts(const char *text) {
    RtFile *out = stdout;
    AcquireSRWLockExclusive(&out->lock);
    rt_put_unlocked(out, (const uint8_t *)text, strlen(text));
    rt_put_unlocked(out, (const uint8_t *)"\n", 1);
    rt_after_write(out);
    ReleaseSRWLockExclusive(&out->lock);
    return 0;
}

static int rt_fflush(RtFile *file) {
    if (!file) {
        if (rt_standard_ready) {
            rt_fflush(&rt_standard[1]);
            rt_fflush(&rt_standard[2]);
        }
        return 0;
    }
    AcquireSRWLockExclusive(&file->lock);
    int result = rt_flush_unlocked(file);
    ReleaseSRWLockExclusive(&file->lock);
    return result;
}

static int rt_fill(RtFile *file) {
    if (!rt_buffer(file))
        return 0;
    DWORD read = 0;
    if (!ReadFile(file->handle, file->buffer, (DWORD)file->capacity, &read, NULL)) {
        if (GetLastError() != ERROR_BROKEN_PIPE)
            file->error = 1;
        read = 0;
    }
    file->start = 0;
    file->end = read;
    return read > 0;
}

/* One byte (text mode: "\r\n" is "\n"), or -1 at the end. */
static int rt_getc_unlocked(RtFile *file) {
    int c;
    if (file->peeked >= 0) {
        c = file->peeked;
        file->peeked = -1;
        return c;
    }
    if (file->start == file->end && !rt_fill(file))
        return -1;
    c = file->buffer[file->start++];
    if (c == '\r' && (file->flags & RT_TEXT)) {
        int next = file->start < file->end || rt_fill(file) ? file->buffer[file->start++] : -1;
        if (next == '\n')
            return '\n';
        file->peeked = next;
    }
    return c;
}

static size_t rt_fread(void *bytes, size_t size, size_t count, RtFile *file) {
    if (!file || !(file->flags & RT_READ))
        return 0;
    AcquireSRWLockExclusive(&file->lock);
    rt_flush_unlocked(file);
    uint8_t *to = (uint8_t *)bytes;
    size_t wanted = size * count, got = 0;
    if (file->flags & RT_TEXT) {
        while (got < wanted) {
            int c = rt_getc_unlocked(file);
            if (c < 0)
                break;
            to[got++] = (uint8_t)c;
        }
    } else {
        while (got < wanted) {
            if (file->start < file->end) {
                size_t take = file->end - file->start < wanted - got ? file->end - file->start : wanted - got;
                memcpy(to + got, file->buffer + file->start, take);
                file->start += take;
                got += take;
            } else if (wanted - got >= 65536) {
                DWORD read = 0;
                DWORD chunk = wanted - got > 0x40000000 ? 0x40000000 : (DWORD)(wanted - got);
                if (!ReadFile(file->handle, to + got, chunk, &read, NULL) || read == 0)
                    break;
                got += read;
            } else if (!rt_fill(file)) {
                break;
            }
        }
    }
    ReleaseSRWLockExclusive(&file->lock);
    return size ? got / size : 0;
}

static int rt_getchar(void) {
    RtFile *in = stdin;
    if (rt_standard_ready)
        rt_fflush(&rt_standard[1]);
    AcquireSRWLockExclusive(&in->lock);
    int c = rt_getc_unlocked(in);
    ReleaseSRWLockExclusive(&in->lock);
    return c;
}

#pragma optimize("s", on) /* cold: compiled for size (see COLD) */
static RtFile *rt_wfopen(const wchar_t *name, const wchar_t *mode) {
    int read = mode[0] == L'r' || mode[1] == L'+' || mode[2] == L'+';
    int write = mode[0] != L'r' || mode[1] == L'+' || mode[2] == L'+';
    DWORD access = (read ? GENERIC_READ : 0) | (mode[0] == L'a' ? FILE_APPEND_DATA : write ? GENERIC_WRITE : 0);
    DWORD disposition = mode[0] == L'r' ? OPEN_EXISTING : mode[0] == L'w' ? CREATE_ALWAYS : OPEN_ALWAYS;
    HANDLE handle = CreateFileW(name, access, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, disposition, FILE_ATTRIBUTE_NORMAL, NULL);
    if (handle == INVALID_HANDLE_VALUE)
        return NULL;
    RtFile *file = (RtFile *)rt_calloc(1, sizeof(RtFile));
    if (!file) {
        CloseHandle(handle);
        return NULL;
    }
    InitializeSRWLock(&file->lock);
    file->handle = handle;
    file->flags = (read ? RT_READ : 0) | (write ? RT_WRITE : 0);
    file->peeked = -1;
    return file;
}

static int rt_fclose(RtFile *file) {
    if (!file)
        return -1;
    int result = rt_flush_unlocked(file);
    if (!CloseHandle(file->handle))
        result = -1;
    rt_free(file->buffer);
    rt_free(file);
    return result;
}

static int64_t rt_ftelli64(RtFile *file) {
    LARGE_INTEGER zero, at;
    zero.QuadPart = 0;
    if (!SetFilePointerEx(file->handle, zero, &at, FILE_CURRENT))
        return -1;
    return at.QuadPart + (int64_t)file->pending - (int64_t)(file->end - file->start);
}

static int rt_fseeki64(RtFile *file, int64_t offset, int origin) {
    AcquireSRWLockExclusive(&file->lock);
    if (origin == SEEK_CUR)
        offset += rt_ftelli64(file);
    rt_flush_unlocked(file);
    file->start = file->end = 0;
    file->peeked = -1;
    LARGE_INTEGER to;
    to.QuadPart = offset;
    BOOL moved = SetFilePointerEx(file->handle, to, NULL, origin == SEEK_END ? FILE_END : FILE_BEGIN);
    ReleaseSRWLockExclusive(&file->lock);
    return moved ? 0 : -1;
}

static int rt_fseek(RtFile *file, long offset, int origin) { return rt_fseeki64(file, offset, origin); }

static int rt_ferror(RtFile *file) { return file->error; }

/* _wsystem: the line through cmd.exe /c, waited for: its exit code. */
static int rt_wsystem(const wchar_t *line) {
    static const wchar_t prefix[] = L"cmd.exe /c ";
    size_t length = wcslen(line);
    wchar_t *command = (wchar_t *)rt_malloc((length + 16) * sizeof(wchar_t));
    if (!command)
        return -1;
    rt_wcscpy(command, prefix);
    rt_wcscpy(command + (sizeof prefix / sizeof(wchar_t) - 1), line);
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    memset(&startup, 0, sizeof startup);
    startup.cb = sizeof startup;
    int code = -1;
    if (CreateProcessW(NULL, command, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &process)) {
        WaitForSingleObject(process.hProcess, INFINITE);
        DWORD exit;
        if (GetExitCodeProcess(process.hProcess, &exit))
            code = (int)exit;
        CloseHandle(process.hProcess);
        CloseHandle(process.hThread);
    }
    rt_free(command);
    return code;
}

#pragma optimize("", on)
/* ── Numbers as text ── */

/* An unsigned big integer, 32 bits a word, least significant first (enough for 800 digits times 2^1075, or 10^1130). */
typedef struct {
    uint32_t w[160];
    int n;
} RtBig;

static void big_set(RtBig *b, uint64_t value) {
    b->n = 0;
    while (value) {
        b->w[b->n++] = (uint32_t)value;
        value >>= 32;
    }
}

static void big_mul_small(RtBig *b, uint32_t factor) {
    uint64_t carry = 0;
    for (int i = 0; i < b->n; i++) {
        uint64_t t = (uint64_t)b->w[i] * factor + carry;
        b->w[i] = (uint32_t)t;
        carry = t >> 32;
    }
    if (carry)
        b->w[b->n++] = (uint32_t)carry;
}

static void big_add_small(RtBig *b, uint32_t value) {
    uint64_t carry = value;
    for (int i = 0; i < b->n && carry; i++) {
        uint64_t t = (uint64_t)b->w[i] + carry;
        b->w[i] = (uint32_t)t;
        carry = t >> 32;
    }
    if (carry)
        b->w[b->n++] = (uint32_t)carry;
}

static void big_mul_pow10(RtBig *b, int count) {
    static const uint32_t powers[10] = { 1, 10, 100, 1000, 10000, 100000, 1000000, 10000000, 100000000, 1000000000 };
    while (count >= 9) {
        big_mul_small(b, 1000000000u);
        count -= 9;
    }
    if (count > 0)
        big_mul_small(b, powers[count]);
}

static void big_shift_left(RtBig *b, int bits) {
    if (b->n == 0 || bits <= 0)
        return;
    int words = bits / 32, rest = bits % 32;
    if (rest) {
        uint32_t carry = 0;
        for (int i = 0; i < b->n; i++) {
            uint32_t w = b->w[i];
            b->w[i] = w << rest | carry;
            carry = w >> (32 - rest);
        }
        if (carry)
            b->w[b->n++] = carry;
    }
    if (words) {
        for (int i = b->n - 1; i >= 0; i--)
            b->w[i + words] = b->w[i];
        for (int i = 0; i < words; i++)
            b->w[i] = 0;
        b->n += words;
    }
}

static int big_compare(const RtBig *a, const RtBig *b) {
    if (a->n != b->n)
        return a->n < b->n ? -1 : 1;
    for (int i = a->n - 1; i >= 0; i--)
        if (a->w[i] != b->w[i])
            return a->w[i] < b->w[i] ? -1 : 1;
    return 0;
}

/* a -= b (a >= b). */
static void big_sub(RtBig *a, const RtBig *b) {
    int64_t borrow = 0;
    for (int i = 0; i < a->n; i++) {
        int64_t t = (int64_t)a->w[i] - (i < b->n ? b->w[i] : 0) - borrow;
        borrow = t < 0;
        a->w[i] = (uint32_t)(t + (borrow << 32));
    }
    while (a->n > 0 && a->w[a->n - 1] == 0)
        a->n--;
}

/*
 * The first `count` significant decimal digits of v (> 0, finite), correctly rounded (ties to even), and the power of
 * ten of the first one: v ≈ 0.d1d2… × 10^(exponent + 1). The exact value is m × 2^e: digits come from R / S scaled so that
 * 1 <= R / S < 10, one per step (Steele and White's method, with big integers).
 */
static void rt_digits(double v, int count, char *digits, int *exponent) {
    uint64_t bits;
    memcpy(&bits, &v, 8);
    int biased = (int)(bits >> 52 & 0x7FF);
    uint64_t m = bits & ((1ULL << 52) - 1);
    if (biased)
        m |= 1ULL << 52;
    else
        biased = 1;
    int e = biased - 1075;
    RtBig r, s, scaled;
    big_set(&r, m);
    big_set(&s, 1);
    if (e > 0)
        big_shift_left(&r, e);
    else
        big_shift_left(&s, -e);
    unsigned long top;
    _BitScanReverse64(&top, m);
    int msb = (int)top + e;
    int k = msb >= 0 ? (int)(((int64_t)msb * 78913) >> 18) : -(int)(((int64_t)-msb * 78913 + (1 << 18) - 1) >> 18);
    if (k >= 0)
        big_mul_pow10(&s, k);
    else
        big_mul_pow10(&r, -k);
    if (big_compare(&r, &s) < 0) {
        big_mul_small(&r, 10);
        k--;
    }
    for (;;) {
        scaled = s;
        big_mul_small(&scaled, 10);
        if (big_compare(&r, &scaled) < 0)
            break;
        s = scaled;
        k++;
    }
    *exponent = k;
    for (int i = 0; i < count; i++) {
        int d = 0;
        while (big_compare(&r, &s) >= 0) {
            big_sub(&r, &s);
            d++;
        }
        digits[i] = (char)('0' + d);
        big_mul_small(&r, 10);
    }
    /* r is the rest times 10: against 5 × s, half a unit of the last digit. */
    scaled = s;
    big_mul_small(&scaled, 5);
    int c = big_compare(&r, &scaled);
    if (c > 0 || (c == 0 && (digits[count - 1] - '0') % 2 == 1)) {
        int i = count - 1;
        while (i >= 0 && digits[i] == '9')
            digits[i--] = '0';
        if (i >= 0) {
            digits[i]++;
        } else {
            digits[0] = '1';
            (*exponent)++;
        }
    }
}

/* "nan", "inf" and "-inf" as the C library wrote them; 0 when v is a number. */
static int rt_special(char *out, double v) {
    if (v != v) {
        uint64_t bits;
        memcpy(&bits, &v, 8);
        rt_strcpy(out, bits >> 63 ? "-nan(ind)" : "nan");
        return (int)strlen(out);
    }
    if (v > 1.7976931348623157e308 || v < -1.7976931348623157e308) {
        rt_strcpy(out, v > 0 ? "inf" : "-inf");
        return (int)strlen(out);
    }
    return 0;
}

/* printf's %.<precision>g. */
static int rt_format_g(char *out, double v, int precision) {
    int n = rt_special(out, v);
    if (n)
        return n;
    char *at = out;
    uint64_t bits;
    memcpy(&bits, &v, 8);
    if (bits >> 63) {
        *at++ = '-';
        v = -v;
    }
    if (precision <= 0)
        precision = 1;
    if (precision > 40)
        precision = 40;
    if (v == 0) {
        *at++ = '0';
        *at = 0;
        return (int)(at - out);
    }
    char digits[41];
    int x;
    rt_digits(v, precision, digits, &x);
    int last = precision;
    while (last > 1 && digits[last - 1] == '0')
        last--;
    if (x < -4 || x >= precision) {
        *at++ = digits[0];
        if (last > 1) {
            *at++ = '.';
            for (int i = 1; i < last; i++)
                *at++ = digits[i];
        }
        *at++ = 'e';
        *at++ = x < 0 ? '-' : '+';
        int ex = x < 0 ? -x : x;
        char exponent[8];
        int en = 0;
        do {
            exponent[en++] = (char)('0' + ex % 10);
            ex /= 10;
        } while (ex);
        while (en < 2)
            exponent[en++] = '0';
        while (en)
            *at++ = exponent[--en];
    } else if (x >= 0) {
        for (int i = 0; i <= x; i++)
            *at++ = i < last ? digits[i] : '0';
        if (last > x + 1) {
            *at++ = '.';
            for (int i = x + 1; i < last; i++)
                *at++ = digits[i];
        }
    } else {
        *at++ = '0';
        *at++ = '.';
        for (int i = -1; i > x; i--)
            *at++ = '0';
        for (int i = 0; i < last; i++)
            *at++ = digits[i];
    }
    *at = 0;
    return (int)(at - out);
}

/* printf's %.<decimals>f. */
static int rt_format_f(char *out, double v, int decimals) {
    int n = rt_special(out, v);
    if (n)
        return n;
    char *at = out;
    if (v < 0) {
        *at++ = '-';
        v = -v;
    }
    if (decimals > 30)
        decimals = 30;
    char digits[400];
    int x = 0, count = 0;
    if (v != 0) {
        /* The exponent from 20 digits (rounding to fewer could carry into the next power of ten). */
        rt_digits(v, 20, digits, &x);
        count = x + 1 + decimals;
        if (count == 0) {
            /* Below the last decimal: it rounds to one unit of it when above half of one (ties to even: 0). */
            int up = digits[0] > '5';
            for (int i = 1; i < 20 && digits[0] == '5' && !up; i++)
                up = digits[i] != '0';
            if (up) {
                *at++ = '0';
                if (decimals) {
                    *at++ = '.';
                    for (int i = 1; i < decimals; i++)
                        *at++ = '0';
                }
                *at++ = '1';
                *at = 0;
                return (int)(at - out);
            }
        }
        if (count > 0 && count < 390)
            rt_digits(v, count, digits, &x);
        if (count > 0 && x + 1 + decimals > count) /* rounding carried into a new digit */
            count++, digits[count - 1] = '0';
    }
    if (count <= 0) {
        *at++ = '0';
        if (decimals) {
            *at++ = '.';
            for (int i = 0; i < decimals; i++)
                *at++ = '0';
        }
        *at = 0;
        return (int)(at - out);
    }
    if (x < 0) {
        *at++ = '0';
    } else {
        for (int i = 0; i <= x; i++)
            *at++ = digits[i];
    }
    if (decimals) {
        *at++ = '.';
        for (int i = 0; i < decimals; i++) {
            int index = x + 1 + i;
            *at++ = index < 0 ? '0' : digits[index];
        }
    }
    *at = 0;
    return (int)(at - out);
}

#pragma optimize("s", on) /* cold: compiled for size (see COLD) */
/* What the runtime's printf calls take: %d %u %x %lld %llu %llx %zu %s %ls %c %p, a width with 0 padding, %.Ng and %.Nf. */
static int rt_vformat(char *out, size_t capacity, const char *format, va_list arguments) {
    size_t length = 0;
#define RT_PUT(ch) do { if (length + 1 < capacity) out[length] = (ch); length++; } while (0)
    for (const char *f = format; *f; f++) {
        if (*f != '%') {
            RT_PUT(*f);
            continue;
        }
        f++;
        int zero = 0, width = 0, precision = -1, longs = 0, size = 0;
        if (*f == '%') {
            RT_PUT('%');
            continue;
        }
        while (*f == '0' || *f == '-' || *f == '+' || *f == ' ') {
            if (*f == '0')
                zero = 1;
            f++;
        }
        while (*f >= '0' && *f <= '9')
            width = width * 10 + (*f++ - '0');
        if (*f == '.') {
            f++;
            precision = 0;
            while (*f >= '0' && *f <= '9')
                precision = precision * 10 + (*f++ - '0');
        }
        while (*f == 'l' || *f == 'z' || *f == 'h') {
            if (*f == 'l')
                longs++;
            if (*f == 'z')
                size = 1;
            f++;
        }
        char text[512];
        int n = 0;
        const char *piece = text;
        switch (*f) {
        case 'd':
        case 'i':
        case 'u':
        case 'x':
        case 'X':
        case 'p': {
            uint64_t value;
            int negative = 0;
            if (*f == 'p')
                value = (uint64_t)(uintptr_t)va_arg(arguments, void *);
            else if (longs >= 2 || size)
                value = va_arg(arguments, uint64_t);
            else if (*f == 'd' || *f == 'i')
                value = (uint64_t)(int64_t)va_arg(arguments, int);
            else
                value = va_arg(arguments, unsigned int);
            if ((*f == 'd' || *f == 'i') && (int64_t)value < 0) {
                negative = 1;
                value = (uint64_t)-(int64_t)value;
            }
            int base = *f == 'x' || *f == 'X' || *f == 'p' ? 16 : 10;
            const char *letters = *f == 'X' ? "0123456789ABCDEF" : "0123456789abcdef";
            char reversed[24];
            int rn = 0;
            do {
                reversed[rn++] = letters[value % (uint64_t)base];
                value /= (uint64_t)base;
            } while (value);
            if (*f == 'p')
                while (rn < 16)
                    reversed[rn++] = '0';
            if (negative)
                text[n++] = '-';
            while (zero && n + rn < width)
                text[n++] = '0';
            while (rn)
                text[n++] = reversed[--rn];
            break;
        }
        case 'c':
            text[n++] = (char)va_arg(arguments, int);
            break;
        case 's':
            if (longs) {
                const wchar_t *wide = va_arg(arguments, const wchar_t *);
                n = wide ? WideCharToMultiByte(CP_ACP, 0, wide, -1, text, sizeof text, NULL, NULL) - 1 : 0;
                if (n < 0)
                    n = 0;
            } else {
                piece = va_arg(arguments, const char *);
                if (!piece)
                    piece = "(null)";
                n = (int)strlen(piece);
            }
            break;
        case 'g':
        case 'G':
            n = rt_format_g(text, va_arg(arguments, double), precision < 0 ? 6 : precision);
            break;
        case 'f':
        case 'F':
            n = rt_format_f(text, va_arg(arguments, double), precision < 0 ? 6 : precision);
            break;
        default:
            RT_PUT('%');
            RT_PUT(*f);
            continue;
        }
        for (int pad = n; pad < width; pad++)
            RT_PUT(' ');
        for (int i = 0; i < n; i++)
            RT_PUT(piece[i]);
    }
    if (capacity)
        out[length < capacity ? length : capacity - 1] = 0;
    return (int)length;
#undef RT_PUT
}

static int rt_snprintf(char *out, size_t capacity, const char *format, ...) {
    va_list arguments;
    va_start(arguments, format);
    int n = rt_vformat(out, capacity, format, arguments);
    va_end(arguments);
    return n;
}

static int rt_vfprintf(RtFile *file, const char *format, va_list arguments) {
    char line[1024];
    va_list copy;
    va_copy(copy, arguments);
    int n = rt_vformat(line, sizeof line, format, arguments);
    if (n < (int)sizeof line) {
        rt_fwrite(line, 1, (size_t)n, file);
    } else {
        char *large = (char *)rt_malloc((size_t)n + 1);
        if (large) {
            rt_vformat(large, (size_t)n + 1, format, copy);
            rt_fwrite(large, 1, (size_t)n, file);
            rt_free(large);
        }
    }
    va_end(copy);
    return n;
}

static int rt_fprintf(RtFile *file, const char *format, ...) {
    va_list arguments;
    va_start(arguments, format);
    int n = rt_vfprintf(file, format, arguments);
    va_end(arguments);
    return n;
}

static int rt_printf(const char *format, ...) {
    va_list arguments;
    va_start(arguments, format);
    int n = rt_vfprintf(stdout, format, arguments);
    va_end(arguments);
    return n;
}

/* ── Text to a double, correctly rounded ── */

static double rt_from_parts(uint64_t mantissa, int exponent2) {
    /* mantissa × 2^exponent2, mantissa < 2^53 (a normal or subnormal double, exactly). */
    uint64_t bits;
    if (mantissa >= (1ULL << 52)) {
        int biased = exponent2 + 1075;
        bits = (uint64_t)biased << 52 | (mantissa & ((1ULL << 52) - 1));
    } else {
        bits = mantissa;
    }
    double value;
    memcpy(&value, &bits, 8);
    return value;
}

static double rt_strtod(const char *text, char **end) {
    const char *at = text;
    while (*at == ' ' || *at == '\t' || *at == '\n' || *at == '\r')
        at++;
    int negative = 0;
    if (*at == '+' || *at == '-')
        negative = *at++ == '-';
    double sign = negative ? -1.0 : 1.0;
    char lower[9];
    for (int i = 0; i < 8; i++)
        lower[i] = (char)(at[i] >= 'A' && at[i] <= 'Z' ? at[i] + 32 : at[i]);
    lower[8] = 0;
    if (lower[0] == 'i' && lower[1] == 'n' && lower[2] == 'f') {
        int full = lower[3] == 'i' && lower[4] == 'n' && lower[5] == 'i' && lower[6] == 't' && lower[7] == 'y';
        if (end)
            *end = (char *)(at + (full ? 8 : 3));
        uint64_t infinity = 0x7FF0000000000000ULL;
        double v;
        memcpy(&v, &infinity, 8);
        return sign * v;
    }
    if (lower[0] == 'n' && lower[1] == 'a' && lower[2] == 'n') {
        if (end)
            *end = (char *)(at + 3);
        uint64_t nan = 0x7FF8000000000000ULL;
        double v;
        memcpy(&v, &nan, 8);
        return v;
    }
    /* Significant digits (at most 800; past them only whether any is not zero): value = 0.d1d2… × 10^decimal. */
    char digits[800];
    int count = 0, any = 0, sticky = 0, point = 0;
    int64_t decimal = 0;
    for (;; at++) {
        if (*at == '.') {
            if (point)
                break;
            point = 1;
            continue;
        }
        if (*at < '0' || *at > '9')
            break;
        any = 1;
        if (*at == '0' && count == 0) {
            if (point)
                decimal--;
            continue;
        }
        if (count < (int)sizeof digits)
            digits[count++] = *at;
        else if (*at != '0')
            sticky = 1;
        if (!point)
            decimal++;
    }
    if (!any) {
        if (end)
            *end = (char *)text;
        return 0;
    }
    int64_t exponent10 = 0;
    if (*at == 'e' || *at == 'E') {
        const char *e = at + 1;
        int eneg = 0;
        if (*e == '+' || *e == '-')
            eneg = *e++ == '-';
        if (*e >= '0' && *e <= '9') {
            int64_t value = 0;
            while (*e >= '0' && *e <= '9') {
                if (value < 100000)
                    value = value * 10 + (*e - '0');
                e++;
            }
            exponent10 = eneg ? -value : value;
            at = e;
        }
    }
    if (end)
        *end = (char *)at;
    while (count > 0 && digits[count - 1] == '0' && !sticky)
        count--;
    if (count == 0)
        return sign * 0.0;
    decimal += exponent10;
    if (decimal > 310)
        return sign * rt_from_parts(0, 0) + sign * 1e308 * 10.0;
    if (decimal < -330)
        return sign * 0.0;
    /* As an integer D × 10^(decimal - count). */
    int64_t scale = decimal - count;
    if (count <= 19 && !sticky) {
        uint64_t d = 0;
        for (int i = 0; i < count; i++)
            d = d * 10 + (uint64_t)(digits[i] - '0');
        static const double exact[23] = { 1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
            1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22 };
        if (d < (1ULL << 53) && scale >= -22 && scale <= 22)
            return sign * (scale >= 0 ? (double)d * exact[scale] : (double)d / exact[-scale]);
    }
    /* A first guess, then corrected by comparing against midpoints exactly. */
    double guess = 0;
    for (int i = 0; i < count && i < 19; i++)
        guess = guess * 10 + (digits[i] - '0');
    int64_t guess_scale = scale + (count > 19 ? count - 19 : 0);
    /* Scaled by 10^guess_scale in steps of at most 10^200 (no step overflows; the guess is within a few units). */
    for (int64_t left = guess_scale; left != 0 && guess != 0;) {
        int64_t step = left > 200 ? 200 : left < -200 ? -200 : left;
        double p = 1, base = 10;
        for (int64_t k = step < 0 ? -step : step; k; k >>= 1) {
            if (k & 1)
                p *= base;
            base *= base;
        }
        guess = step > 0 ? guess * p : guess / p;
        left -= step;
    }
    RtBig exact_value;
    exact_value.n = 0;
    for (int i = 0; i < count; i++) {
        big_mul_small(&exact_value, 10);
        if (exact_value.n == 0)
            big_set(&exact_value, (uint64_t)(digits[i] - '0'));
        else
            big_add_small(&exact_value, (uint32_t)(digits[i] - '0'));
    }
    for (int step = 0; step < 200; step++) {
        uint64_t bits;
        memcpy(&bits, &guess, 8);
        if ((bits & 0x7FF0000000000000ULL) == 0x7FF0000000000000ULL) {
            bits = 0x7FEFFFFFFFFFFFFFULL; /* past the largest: start from it */
            memcpy(&guess, &bits, 8);
        }
        int biased = (int)(bits >> 52 & 0x7FF);
        uint64_t m = bits & ((1ULL << 52) - 1);
        if (biased)
            m |= 1ULL << 52;
        else
            biased = 1;
        int e = biased - 1075;
        /* Compare D × 10^scale with the midpoints (2m ± 1) × 2^(e - 1). */
        RtBig left = exact_value, up, down;
        big_set(&up, 2 * m + 1);
        big_set(&down, m ? 2 * m - 1 : 0);
        if (scale >= 0)
            big_mul_pow10(&left, (int)scale);
        else {
            big_mul_pow10(&up, (int)-scale);
            big_mul_pow10(&down, (int)-scale);
        }
        if (e - 1 >= 0) {
            big_shift_left(&up, e - 1);
            big_shift_left(&down, e - 1);
        } else {
            big_shift_left(&left, 1 - e);
        }
        int above = big_compare(&left, &up);
        if (above > 0 || (above == 0 && sticky)) {
            if (bits == 0x7FEFFFFFFFFFFFFFULL)
                return sign * 1e308 * 10.0;
            bits++;
            memcpy(&guess, &bits, 8);
            continue;
        }
        if (above == 0) {
            if (m & 1) {
                bits++;
                memcpy(&guess, &bits, 8);
            }
            break;
        }
        int below = m ? big_compare(&left, &down) : 1;
        if (below < 0) {
            bits--;
            memcpy(&guess, &bits, 8);
            continue;
        }
        if (below == 0 && !sticky && (m & 1)) {
            bits--;
            memcpy(&guess, &bits, 8);
        }
        break;
    }
    (void)rt_from_parts;
    return sign * guess;
}

#pragma optimize("", on)
/* ── Math ── */

static HMODULE rt_ucrt;

static FARPROC rt_math(const char *name) {
    if (!rt_ucrt)
        rt_ucrt = LoadLibraryA("ucrtbase.dll");
    FARPROC found = rt_ucrt ? GetProcAddress(rt_ucrt, name) : NULL;
    if (!found) {
        rt_fprintf(stderr, "larva: %s is not in ucrtbase.dll\n", name);
        ExitProcess(102);
    }
    return found;
}

#define RT_MATH1(name)                                                         \
    static double(__cdecl *rt_##name)(double);                                 \
    double __cdecl name(double x) {                                            \
        if (!rt_##name)                                                        \
            rt_##name = (double(__cdecl *)(double))rt_math(#name);              \
        return rt_##name(x);                                                   \
    }
#define RT_MATH2(name)                                                         \
    static double(__cdecl *rt_##name)(double, double);                         \
    double __cdecl name(double x, double y) {                                  \
        if (!rt_##name)                                                        \
            rt_##name = (double(__cdecl *)(double, double))rt_math(#name);      \
        return rt_##name(x, y);                                                \
    }
RT_MATH1(sin)
RT_MATH1(cos)
RT_MATH1(tan)
RT_MATH1(asin)
RT_MATH1(acos)
RT_MATH1(atan)
RT_MATH1(exp)
RT_MATH1(log)
RT_MATH1(log10)
RT_MATH2(atan2)
RT_MATH2(pow)
RT_MATH2(fmod)

double __cdecl sqrt(double x) { return _mm_cvtsd_f64(_mm_sqrt_sd(_mm_set_sd(x), _mm_set_sd(x))); }

double __cdecl floor(double x) {
    if (!(x == x) || x >= 4503599627370496.0 || x <= -4503599627370496.0 || x == 0)
        return x;
    double t = (double)(int64_t)x;
    if (t > x)
        t -= 1.0;
    return t;
}

double __cdecl ceil(double x) {
    if (!(x == x) || x >= 4503599627370496.0 || x <= -4503599627370496.0 || x == 0)
        return x;
    double t = (double)(int64_t)x;
    if (t < x)
        t += 1.0;
    if (t == 0 && x < 0)
        return -0.0;
    return t;
}

/* The f32 forms: LLVM calls them for `(f32)floor((f64)x)` and the like (Math.Floor<f32>). Exact through f64. */
float __cdecl sqrtf(float x) { return _mm_cvtss_f32(_mm_sqrt_ss(_mm_set_ss(x))); }
float __cdecl floorf(float x) { return (float)floor((double)x); }
float __cdecl ceilf(float x) { return (float)ceil((double)x); }

/* ── Exit ── */

static void rt_exit(int code) {
    rt_fflush(NULL);
    ExitProcess((UINT)code);
}

static void rt_abort(void) { rt_exit(3); }

/* The names the runtime uses (the C library's) mean the functions above from here on. */
#define malloc rt_malloc
#define calloc rt_calloc
#define realloc rt_realloc
#define free rt_free
#define _aligned_malloc rt_aligned_malloc
#define _aligned_free rt_aligned_free
#define strcpy rt_strcpy
#define strcmp rt_strcmp
#define wcscpy rt_wcscpy
#define wcscmp rt_wcscmp
#define qsort rt_qsort
#define _strtoi64 rt_strtoi64
#define atoll rt_atoll
#define getenv rt_getenv
#define time(out) rt_time(out)
#define clock rt_clock
#define fwrite rt_fwrite
#define fputs rt_fputs
#define putchar rt_putchar
#define fputc rt_fputc
#define puts rt_puts
#define fflush rt_fflush
#define fread rt_fread
#define getchar rt_getchar
#define _wfopen rt_wfopen
#define fclose rt_fclose
#define _ftelli64 rt_ftelli64
#define _fseeki64 rt_fseeki64
#define fseek rt_fseek
#define ferror rt_ferror
#define _wsystem rt_wsystem
#define snprintf rt_snprintf
#define fprintf rt_fprintf
#define printf rt_printf
#define strtod rt_strtod
#define abort rt_abort
#define _Exit rt_exit
#undef larva_exit
#define larva_exit(code) rt_exit(code)
#endif

/* Built as larva_rt.dll / liblarva_rt.so (LARVA_SHARED_RUNTIME) the runtime is shared by a program and its Larva libraries. */
#if defined(LARVA_SHARED_RUNTIME) && !defined(_WIN32)
#define LARVA_API __attribute__((visibility("default")))
#elif defined(LARVA_SHARED_RUNTIME)
#define LARVA_API __declspec(dllexport)
/* What compiled Larva code needs from a C library, so programs and Larva DLLs link none: SEH and memory copies. */
#pragma comment(linker, "/EXPORT:__C_specific_handler")
#pragma comment(linker, "/EXPORT:memcpy")
#pragma comment(linker, "/EXPORT:memmove")
#pragma comment(linker, "/EXPORT:memset")
#pragma comment(linker, "/EXPORT:memcmp")
#else
#define LARVA_API
#endif

#define LARVA_EXCEPTION 0xE04C5641u
#define GC_HEADER 16

typedef enum {
    KIND_HEAP = 1, KIND_GC = 2,
    KIND_ROOT = 3,     /* a global's cell: always scanned, never collected */
    KIND_PAYLOAD = 4,  /* an exception in flight: scanned, kept until its handler frees it */
} Kind;

/*
 * A gc object's type, as compiled code describes it (a constant the back end emits per concrete type): its payload's
 * size, and where it holds handles (`gc` references) and owned heap cells (`box`). An array's descriptor is its element's:
 * `size` is the array header (the length and padding), `stride` the element's size, the offsets within each element.
 * Objects the runtime makes for code that gives no type are CONSERVATIVE: every word may be a handle (they never move).
 */
typedef struct LarvaType {
    uint32_t size;
    uint32_t stride;
    uint32_t flags;
    uint16_t handles;     /* how many handle offsets, first in `offsets` */
    uint16_t cells;       /* how many box offsets, after them */
    uint32_t offsets[];
} LarvaType;

#define TYPE_ARRAY 1
#define TYPE_CONSERVATIVE 2
#define TYPE_LEAF 4
/* An object whose last field is an array kept in place: `size` reaches the elements (the length is the 8 bytes before),
   `stride` is theirs; the handles are the fields' (the elements hold none). */
#define TYPE_TRAILING 8

/*
 * The 16-byte header before every gc object's payload: its type, a reference count (reference-counting mode), and four
 * bytes compiled code knows: mark, flags, domain and age (the byte just before the payload, which the write barrier reads).
 */
typedef struct {
    const LarvaType *type;
    int32_t refcount;
    int8_t mark;
    uint8_t flags;
    int8_t domain;    /* the GC domain the object belongs to (0: the program's; bits 8..15 of its type's flags); SLOT_FREE for a free slot */
    uint8_t age;      /* AGE_YOUNG (in the nursery), AGE_OLD or AGE_REMEMBERED */
} GcHeader;

/* A large object (made by the runtime, outside the chunks and the nursery): its block starts with this, then the header. */
#define FLAG_LARGE 1
/* Listed in `carded` (its dirty cards are scanned at the next minor collection). */
#define FLAG_CARDED 2
#define FLAG_FINALIZER 4   /* has an entry in `finalizers` */
#define FLAG_REGION 8      /* belongs to an open arena region (an entry in `region_of`) */
#define FLAG_PINNED 16     /* in the nursery and pointed at ambiguously: stays where it is */
#define FLAG_FORWARDED 32  /* copied out of the nursery: the type word is the copy (bit 0 set), the count the payload size */
#define FLAG_CUSTOM 64     /* the memory came from the program's own collector */
#define DOMAIN_UNCOUNTED 0x40  /* in the domain byte: retain and release skip the object (a domain without counting, in a mixed program) */

typedef struct {
    uint8_t *cards;   /* one byte per CARD bytes of payload, set when an old one is written (NULL until then) */
    int64_t size;     /* payload bytes */
} LargeInfo;

#define AGE_YOUNG 0
#define AGE_OLD 1
#define AGE_REMEMBERED 2
#define CARD 512

/*
 * What compiled code reads to allocate without calling the runtime: when `fast` is set, a new object of `total` bytes
 * (header included, 16-byte aligned) takes [cursor, cursor + total) of the nursery while that is within `limit`; the
 * nursery's memory is already zero. Anything else calls larva_gc_alloc / larva_gc_array. Reached through
 * __imp_larva_heap; the layout is fixed (compiled code knows these offsets).
 */
typedef struct {
    int32_t fast;
    int32_t unused;
    char *cursor;
    char *limit;
} LarvaHeap;

LARVA_API LarvaHeap larva_heap;
#if !defined(LARVA_SHARED_RUNTIME) || !defined(_WIN32)
LARVA_API LarvaHeap *__imp_larva_heap = &larva_heap;
#endif

/* ── Threads: their records and the runtime's locks ─────────────────── */

#ifdef _WIN32
#define THREAD_LOCAL __declspec(thread)
#else
#define THREAD_LOCAL _Thread_local
#endif

#define THREAD_RUNNING 0
#define THREAD_STOPPED 1   /* at a safepoint, or blocked in the runtime: its registers and stack pointer are saved */
#define THREAD_DONE 2

typedef struct LarvaThread {
    struct LarvaThread *next;
    int64_t id;
    volatile int32_t state;
    volatile int32_t finished;   /* its function returned (or threw) */
    uintptr_t stack_high;        /* the top of its stack */
    uintptr_t saved_sp;          /* where its stack's words start while it is stopped */
    uintptr_t registers[16];     /* its general registers when it stopped */
    LarvaHeap heap;              /* its nursery block, while several threads run */
    size_t block;
    void (*code)(void *);
    void *env;                   /* what `code` takes: a root while the thread is alive */
    void **pins;                 /* the objects its functions pinned (larva_pin), most recent last */
    size_t pin_count, pin_capacity;
#ifdef _WIN32
    HANDLE os;
#else
    pthread_t os;
#endif
} LarvaThread;

static THREAD_LOCAL LarvaThread *this_thread;
static LarvaThread main_record = { NULL, 1, THREAD_RUNNING, 0, 0, 0, {0}, {0, 0, NULL, NULL}, (size_t)-1, NULL, NULL, NULL, 0, 0 };
static LarvaThread *thread_list;          /* the threads that have not finished (the main one first) */
static volatile int threading;            /* a thread was started: the runtime's shared state is locked from then on */
static volatile int64_t threads_alive = 1;
static int64_t next_thread_id = 1;
static int shared_heap = 1;               /* one thread runs: it allocates through larva_heap (inline in compiled code) */
static volatile int stop_world;           /* a collection waits for every other thread to stop */
static int running_count;                 /* threads running (not stopped, not done), the collector apart */

#ifdef _WIN32
static CRITICAL_SECTION rt_lock, world_lock;
static CONDITION_VARIABLE world_stopped, world_resumed;
static void lock_init(CRITICAL_SECTION *lock) { InitializeCriticalSectionAndSpinCount(lock, 4000); }
#define LOCK(l) EnterCriticalSection(&(l))
#define UNLOCK(l) LeaveCriticalSection(&(l))
#define WAIT(c, l) SleepConditionVariableCS(&(c), &(l), INFINITE)
#define WAKE_ALL(c) WakeAllConditionVariable(&(c))
#else
static pthread_mutex_t rt_lock, world_lock;
static pthread_cond_t world_stopped = PTHREAD_COND_INITIALIZER, world_resumed = PTHREAD_COND_INITIALIZER;
static void lock_init(pthread_mutex_t *lock) {
    pthread_mutexattr_t attributes;
    pthread_mutexattr_init(&attributes);
    pthread_mutexattr_settype(&attributes, PTHREAD_MUTEX_RECURSIVE);
    pthread_mutex_init(lock, &attributes);
    pthread_mutexattr_destroy(&attributes);
}
#define LOCK(l) pthread_mutex_lock(&(l))
#define UNLOCK(l) pthread_mutex_unlock(&(l))
#define WAIT(c, l) pthread_cond_wait(&(c), &(l))
#define WAKE_ALL(c) pthread_cond_broadcast(&(c))
#endif

/* The runtime's shared state (the allocator, its tables): locked once threads exist. Re-entrant. */
static int rt_enter(void) {
    if (!threading)
        return 0;
    LOCK(rt_lock);
    return 1;
}

static void rt_leave(int locked) {
    if (locked)
        UNLOCK(rt_lock);
}

/*
 * Saves the calling thread's registers and stack pointer in its record: what a collection scans while it is stopped. It
 * is a macro, so the capture happens in the frame that then waits: that frame and those above it stay as they are while
 * the thread is stopped, and a value a callee-saved register held is in the saved registers or in one of those frames.
 */
#ifdef _WIN32
#define SAVE_REGISTERS(thread) do { \
        CONTEXT context_; \
        RtlCaptureContext(&context_); \
        DWORD64 values_[16] = {context_.Rax, context_.Rbx, context_.Rcx, context_.Rdx, context_.Rsi, context_.Rdi, context_.Rbp, \
            context_.R8, context_.R9, context_.R10, context_.R11, context_.R12, context_.R13, context_.R14, context_.R15, context_.Rsp}; \
        for (int r_ = 0; r_ < 16; r_++) \
            (thread)->registers[r_] = (uintptr_t)values_[r_]; \
        (thread)->saved_sp = (uintptr_t)context_.Rsp; \
    } while (0)
#else
#define SAVE_REGISTERS(thread) do { \
        jmp_buf registers_; \
        setjmp(registers_); \
        memset((thread)->registers, 0, sizeof (thread)->registers); \
        memcpy((thread)->registers, &registers_, sizeof registers_ < sizeof (thread)->registers ? sizeof registers_ : sizeof (thread)->registers); \
        volatile uintptr_t here_ = (uintptr_t)&here_; \
        (thread)->saved_sp = here_ < (uintptr_t)&registers_ ? here_ : (uintptr_t)&registers_; \
    } while (0)
#endif

static LarvaThread *current_record(void) {
    LarvaThread *me = this_thread;
    return me ? me : &main_record;
}

/* The calling thread stops (for a collection, or while it waits in the runtime): its registers were saved. */
static void become_stopped(LarvaThread *me) {
    LOCK(world_lock);
    me->state = THREAD_STOPPED;
    running_count--;
    WAKE_ALL(world_stopped);
    UNLOCK(world_lock);
}

/* It runs again, once no collection is going on. */
static void become_running(LarvaThread *me) {
    LOCK(world_lock);
    while (stop_world)
        WAIT(world_resumed, world_lock);
    me->state = THREAD_RUNNING;
    running_count++;
    UNLOCK(world_lock);
}

/* A wait in the runtime (sleeping, joining, a lock, a condition, input): a collection need not wait for this thread. In the
   function that waits (see SAVE_REGISTERS); BLOCKING_LEAVE() after the wait. */
#define BLOCKING_ENTER() do { \
        if (threading) { \
            LarvaThread *blocked_ = current_record(); \
            SAVE_REGISTERS(blocked_); \
            become_stopped(blocked_); \
        } \
    } while (0)

static void blocking_leave(void) {
    if (threading)
        become_running(current_record());
}

/* Stopped at a safepoint until the collection another thread runs is over (in the poll's own frame). */
#define STOP_HERE() do { \
        LarvaThread *stopped_ = current_record(); \
        SAVE_REGISTERS(stopped_); \
        become_stopped(stopped_); \
        become_running(stopped_); \
    } while (0)

/* ── Pointer table (open addressing) ─────────────────────────────── */

typedef struct {
    uintptr_t key;    /* 0 = empty, 1 = deleted */
    int64_t value;
    uint8_t kind;
    uint8_t mark;     /* a heap cell reached by the collector */
} Entry;

typedef struct {
    Entry *entries;
    size_t capacity, count, used;
} Table;

static size_t hash_pointer(uintptr_t key) {
    key ^= key >> 33;
    key *= 0xff51afd7ed558ccdULL;
    key ^= key >> 33;
    return (size_t)key;
}

static Entry *table_find(Table *t, uintptr_t key) {
    /* 0 marks an empty entry and 1 a removed one: neither is an address (a stack slot holding 1 would match a removed entry). */
    if (t->capacity == 0 || key <= 1)
        return NULL;
    size_t i = hash_pointer(key) & (t->capacity - 1);
    for (;;) {
        Entry *e = &t->entries[i];
        if (e->key == 0)
            return NULL;
        if (e->key == key)
            return e;
        i = (i + 1) & (t->capacity - 1);
    }
}

static void table_put(Table *t, uintptr_t key, int64_t value, uint8_t kind);

static void table_grow(Table *t) {
    Entry *old = t->entries;
    size_t old_capacity = t->capacity;
    /* Sized for what is live (removed entries are dropped by the rehash), at most a quarter full: churn (many objects made
       and freed) rehashes in place instead of growing the table forever. */
    size_t capacity = 1024;
    while (capacity < (t->count + 1) * 4)
        capacity *= 2;
    t->capacity = capacity;
    t->entries = (Entry *)calloc(t->capacity, sizeof(Entry));
    if (!t->entries) {
        fputs("larva: out of memory\n", stderr);
        larva_exit(101);
    }
    t->count = t->used = 0;
    for (size_t i = 0; i < old_capacity; i++)
        if (old[i].key > 1)
            table_put(t, old[i].key, old[i].value, old[i].kind);
    free(old);
}

static void table_put(Table *t, uintptr_t key, int64_t value, uint8_t kind) {
    if ((t->used + 1) * 2 > t->capacity)
        table_grow(t);
    size_t i = hash_pointer(key) & (t->capacity - 1);
    Entry *tombstone = NULL;
    for (;;) {
        Entry *e = &t->entries[i];
        if (e->key == key) {
            e->value = value;
            e->kind = kind;
            e->mark = 0;
            return;
        }
        if (e->key == 1 && !tombstone)
            tombstone = e;
        if (e->key == 0) {
            if (tombstone)
                e = tombstone;
            else
                t->used++;
            e->key = key;
            e->value = value;
            e->kind = kind;
            e->mark = 0;
            t->count++;
            return;
        }
        i = (i + 1) & (t->capacity - 1);
    }
}

static int table_remove(Table *t, uintptr_t key) {
    Entry *e = table_find(t, key);
    if (!e)
        return 0;
    e->key = 1;
    t->count--;
    return 1;
}

/* ── State ───────────────────────────────────────────────────────── */

static Table live;          /* payload / cell → size, kind (heap cells, large gc objects) */
static Table frames;        /* function address → index into root_sets */
static Table finalizers;    /* gc payload → its finalizer */
static Table region_of;     /* gc payload → its arena region + 1 */
static int32_t *regions;
static size_t region_count, region_capacity;

typedef struct { uint16_t reg; int32_t offset; int32_t size; } Root;
typedef struct { Root *roots; size_t count; } RootSet;
static RootSet *root_sets;
static size_t root_set_count;
static int stack_maps_loaded;

static int64_t allocations, frees, errors, collections, collected, since_collection, finalized, collected_cells;
static int finalizing;  /* finalizers running: collections wait */
static int64_t threshold = 4096;
/*
 * Diagnostics (LARVA_GC_STRESS, _VERIFY, _QUARANTINE, _NOSMALL, _GENERATIONS, _THRESHOLD, _NURSERY, LARVA_STATS) are in
 * the runtime only when it is built with LARVA_DIAGNOSTICS (LARVA_DIAGNOSTICS=1 when a program is built). Otherwise these
 * settings are constants and their code is gone: smaller programs, and nothing to test on the way.
 */
#ifdef LARVA_DIAGNOSTICS
static int fixed_threshold;  /* LARVA_GC_THRESHOLD: no growing with the heap */
static int stress;
static int quarantine;  /* collected objects are poisoned and kept out of the allocator */
#else
enum { fixed_threshold = 0, stress = 0, quarantine = 0 };
#endif
static int collect_requested;  /* GC.Collect(): the next safepoint collects */
static int64_t gc_live;        /* gc objects alive outside the nursery */
static int64_t bytes_since;    /* bytes allocated outside the nursery since the last collection */
static int64_t live_bytes;     /* bytes alive after the last collection */
#ifdef LARVA_DIAGNOSTICS
static int stats_enabled;      /* LARVA_STATS: count what the nursery made (it is not counted otherwise) */
#else
enum { stats_enabled = 0 };
#endif

static int64_t old_bytes, old_objects;           /* old since the last major collection (its survivors and promotions) */
static int64_t major_bytes = (int64_t)64 << 20, major_objects = 1 << 20;
static int major_due;
static int64_t minors, majors;
static int64_t minor_ns, major_ns;   /* time spent collecting (LARVA_STATS) */
static int64_t barrier_calls, poll_calls;
static int64_t promoted_total, pinned_total;

static int64_t now_ns(void) {
#ifdef _WIN32
    static LARGE_INTEGER frequency;
    LARGE_INTEGER counter;
    if (!frequency.QuadPart)
        QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&counter);
    return (int64_t)((double)counter.QuadPart * 1e9 / (double)frequency.QuadPart);
#else
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000000000 + now.tv_nsec;
#endif
}
static void **remembered;
static size_t remembered_count, remembered_capacity;
static void **carded;
static size_t carded_count, carded_capacity;

/* The program's own collector (`gc: custom`, installed by System.GC.Install): where objects' memory comes from and
   goes back to, and when to collect. Its functions are Antril code: while one runs, objects it allocates take memory
   from the runtime and no collection starts. Memory freed during a collection goes back once the collection is done. */
static void *(*custom_allocate)(uint64_t);
static void (*custom_release)(void *);
static int32_t (*custom_poll)(int64_t, int64_t);
static int in_custom;
static int collecting;
static int64_t custom_asked = -1;  /* since_collection when the collector last said no */
static void **custom_pending;
static size_t custom_pending_count, custom_pending_capacity;

/* Safepoints in compiled code read this flag and call larva_poll only when it is set: a collection may be due (enough
   allocated, GC.Collect, stress, or a custom collector to ask). Code reaches it through __imp_larva_poll_pending, the
   pointer an import from larva_rt.dll uses; a static runtime (and a POSIX shared one) defines that pointer itself. */
LARVA_API volatile int32_t larva_poll_pending;
#if !defined(LARVA_SHARED_RUNTIME) || !defined(_WIN32)
LARVA_API volatile int32_t *__imp_larva_poll_pending = &larva_poll_pending;
#endif

static int generational(void);
static int nursery_due(void);

static int allocation_due(void) {
    if (fixed_threshold)
        return since_collection >= threshold;
    if (!generational())
        return since_collection >= threshold || bytes_since >= ((int64_t)32 << 20) && bytes_since >= live_bytes * 2;
    return nursery_due() || bytes_since >= ((int64_t)32 << 20) || major_due;
}

static void update_poll_pending(void) {
    larva_poll_pending = stop_world || stress || collect_requested || custom_poll != NULL || allocation_due();
}

static void *fail_alloc(void) {
    fputs("larva: out of memory\n", stderr);
    larva_exit(101);
    return NULL;
}

/* ── Heap cells ──────────────────────────────────────────────────── */

static void *alloc_unlocked(int64_t size, int64_t align);

LARVA_API void *larva_alloc(int64_t size, int64_t align) {
    int locked = rt_enter();
    void *block = alloc_unlocked(size, align);
    rt_leave(locked);
    return block;
}

static void *alloc_unlocked(int64_t size, int64_t align) {
    size_t bytes = size > 0 ? (size_t)size : 1;
    size_t alignment = align > 8 ? (size_t)align : 8;
    void *block = larva_aligned_alloc(bytes, alignment);
    if (!block)
        return fail_alloc();
    memset(block, 0, bytes);
    table_put(&live, (uintptr_t)block, (int64_t)bytes, KIND_HEAP);
    allocations++;
    bytes_since += (int64_t)bytes;
    return block;
}

/*
 * Raw memory (`alloc v`, a `*mut T` freed by the program): vtables, interface tables, objects when no collector manages
 * them. A root (what it holds stays alive) that no collection frees, however it is reached: the program may keep the
 * pointer anywhere (a plain global, a field the collector does not follow). It does not count toward the next collection.
 */
LARVA_API void *larva_raw_alloc(int64_t size, int64_t align) {
    size_t bytes = size > 0 ? (size_t)size : 1;
    size_t alignment = align > 8 ? (size_t)align : 8;
    void *block = larva_aligned_alloc(bytes, alignment);
    if (!block)
        return fail_alloc();
    memset(block, 0, bytes);
    int locked = rt_enter();
    table_put(&live, (uintptr_t)block, (int64_t)bytes, KIND_PAYLOAD);
    allocations++;
    rt_leave(locked);
    return block;
}

/* The cell a thrown value travels in: a root until the handler that catches it frees it (nothing else holds it then). */
LARVA_API void *larva_exception_cell(int64_t size, int64_t align) {
    int locked = rt_enter();
    void *cell = alloc_unlocked(size, align);
    Entry *e = table_find(&live, (uintptr_t)cell);
    if (e)
        e->kind = KIND_PAYLOAD;
    rt_leave(locked);
    return cell;
}

static void free_unlocked(void *pointer);

LARVA_API void larva_free(void *pointer) {
    int locked = rt_enter();
    free_unlocked(pointer);
    rt_leave(locked);
}

static void free_unlocked(void *pointer) {
    if (!pointer)
        return;
    Entry *e = table_find(&live, (uintptr_t)pointer);
    if (!e || (e->kind != KIND_HEAP && e->kind != KIND_PAYLOAD)) {
        errors++;
        return;
    }
    table_remove(&live, (uintptr_t)pointer);
    larva_aligned_free(pointer);
    frees++;
}

/* ── Types the runtime makes (code that gives none) ───────────────── */

/* A conservative type of `size` payload bytes, or an array one of header `size` and `stride` (leaf: holds no handles). */
static const LarvaType *untyped(uint32_t size, uint32_t stride, int array, int leaf, int domain) {
    static Table made;  /* key: (size, stride, array, leaf, domain) packed; value: the type */
    uintptr_t key = ((uintptr_t)size << 34) ^ ((uintptr_t)stride << 3) ^ (uintptr_t)(array ? 2 : 0) ^ (uintptr_t)(array == 2 ? 1u << 30 : 0) ^ (uintptr_t)(leaf ? 4 : 0)
        ^ ((uintptr_t)(domain & 0xFF) << 23) ^ 1u << 31;
    Entry *e = table_find(&made, key);
    if (e)
        return (const LarvaType *)(intptr_t)e->value;
    LarvaType *type = (LarvaType *)calloc(1, sizeof(LarvaType));
    if (!type)
        return (const LarvaType *)fail_alloc();
    type->size = size;
    type->stride = stride;
    /* array 2: an object ending with an array in place (TYPE_TRAILING). */
    type->flags = (array == 2 ? TYPE_TRAILING : array ? TYPE_ARRAY : 0) | (leaf ? TYPE_LEAF : TYPE_CONSERVATIVE) | ((uint32_t)(domain & 0xFF) << 8);
    table_put(&made, key, (int64_t)(intptr_t)type, 0);
    return type;
}

/* The payload bytes of an object (an array's from its length). */
static int64_t payload_bytes(const GcHeader *header) {
    /* A forwarded object's first word is its copy (an array's length is gone): its size is kept in the count. */
    if (header->flags & FLAG_FORWARDED)
        return header->refcount;
    const LarvaType *type = header->type;
    if (type->flags & (TYPE_ARRAY | TYPE_TRAILING)) {
        int64_t length = *(const int64_t *)((const char *)header + GC_HEADER + ((type->flags & TYPE_TRAILING) ? type->size - 8 : 0));
        return (int64_t)type->size + (length > 0 ? length : 0) * (int64_t)type->stride;
    }
    return type->size;
}

/* Header and payload, 16-byte aligned, at least one word of payload (where a forwarded object keeps its copy). */
static int64_t total_bytes(int64_t payload) {
    if (payload < 8)
        payload = 8;
    return GC_HEADER + ((payload + 15) & ~(int64_t)15);
}

/* ── Small objects (the old generation) ────────────────────────── */

/* Old gc objects of up to SMALL_MAX bytes (header included) live in chunks of CHUNK bytes, aligned to CHUNK, each chunk
   for one size class. A slot comes from its class's free list, else from the bump pointer of the class's newest chunk;
   the header says whether a slot holds an object (`custom` is SLOT_FREE when it does not). The collector finds a chunk
   from an address by masking it, and the slot from the offset. Larger objects, and memory from the program's own
   collector, take the general path (the `live` table). */
#define CHUNK ((size_t)1 << 18)
#define CHUNK_DATA 64
#define ARENA_CHUNKS 64
#define SMALL_MAX 2048
#define SLOT_FREE ((int8_t)-1)
#define CLASS_COUNT 23

typedef struct Chunk {
    uint32_t block;       /* slot size in bytes, header included */
    uint32_t count;       /* slots in the chunk */
    uint32_t bump;        /* slots handed out so far (the others were never used) */
    uint32_t size_class;
    char *free;           /* its free slots, linked through their first word */
    struct Chunk *next_partial;
    uint32_t listed;      /* in its class's list of chunks with free slots */
} Chunk;

static const uint32_t class_blocks[CLASS_COUNT] = {
    32, 48, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 640, 768, 896, 1024, 1280, 1536, 1792, 2048};
static uint8_t class_of_units[SMALL_MAX / 16 + 1];  /* (header + size) in 16-byte units, rounded up → size class */
static int classes_ready;
static Chunk *current_chunk[CLASS_COUNT];
/* Free slots are kept by chunk: a class lists its chunks that have some, and whoever needs slots takes one chunk's list
   whole (the collector's threads too, without walking it under their lock). */
static Chunk *partial_chunks[CLASS_COUNT];
static char *fill_list[CLASS_COUNT];   /* the free slots the program's own old allocations take from */
static uint8_t *chunk_map;                          /* one byte per chunk from small_low: 1 a chunk */
static size_t chunk_map_size;
static Chunk **chunks;
static size_t chunk_count, chunk_capacity;
static char *arena_next, *arena_end;
static uintptr_t small_low = UINTPTR_MAX, small_high;  /* the addresses chunks lie in: other words are not small objects */
/* Whether any object ever had an arena region or a finalizer: collections skip looking for them until one does. */
static int regions_used, finalizers_used;
#ifdef LARVA_DIAGNOSTICS
static int small_disabled;  /* LARVA_GC_NOSMALL=1: every object takes the general path (diagnosis) */
#else
enum { small_disabled = 0 };
#endif

static void init_classes(void) {
    size_t c = 0;
    for (size_t units = 0; units <= SMALL_MAX / 16; units++) {
        while (class_blocks[c] < units * 16)
            c++;
        class_of_units[units] = (uint8_t)c;
    }
    classes_ready = 1;
}

static char *chunk_slot(Chunk *chunk, size_t index) { return (char *)chunk + CHUNK_DATA + index * chunk->block; }

static Chunk *new_chunk(uint32_t size_class) {
    if (arena_next == arena_end) {
        char *arena = (char *)larva_aligned_alloc(CHUNK * ARENA_CHUNKS, CHUNK);
        if (!arena)
            return (Chunk *)fail_alloc();
        arena_next = arena;
        arena_end = arena + CHUNK * ARENA_CHUNKS;
        uintptr_t old_low = small_low;
        if ((uintptr_t)arena < small_low)
            small_low = (uintptr_t)arena;
        if ((uintptr_t)arena_end > small_high)
            small_high = (uintptr_t)arena_end;
        size_t size = (small_high - small_low) / CHUNK;
        uint8_t *map = (uint8_t *)calloc(size, 1);
        if (!map)
            return (Chunk *)fail_alloc();
        for (size_t c = 0; c < chunk_count; c++)
            map[((uintptr_t)chunks[c] - small_low) / CHUNK] = chunk_map[((uintptr_t)chunks[c] - old_low) / CHUNK];
        free(chunk_map);
        chunk_map = map;
        chunk_map_size = size;
    }
    Chunk *chunk = (Chunk *)arena_next;
    arena_next += CHUNK;
    chunk->block = class_blocks[size_class];
    chunk->count = (uint32_t)((CHUNK - CHUNK_DATA) / chunk->block);
    chunk->bump = 0;
    chunk->size_class = size_class;
    chunk->free = NULL;
    chunk->next_partial = NULL;
    chunk->listed = 0;
    chunk_map[((uintptr_t)chunk - small_low) / CHUNK] = 1;
    if (chunk_count == chunk_capacity) {
        chunk_capacity = chunk_capacity ? chunk_capacity * 2 : 64;
        chunks = (Chunk **)realloc(chunks, chunk_capacity * sizeof(Chunk *));
        if (!chunks)
            return (Chunk *)fail_alloc();
    }
    chunks[chunk_count++] = chunk;
    return chunk;
}

/* The header of the small object whose payload is at `word`, or NULL when it is not one. */
static GcHeader *small_object(const void *word) {
    uintptr_t address = (uintptr_t)word;
    if (address < small_low || address >= small_high)
        return NULL;
    uintptr_t base = address & ~(uintptr_t)(CHUNK - 1);
    if (!chunk_map[(base - small_low) / CHUNK])
        return NULL;
    Chunk *chunk = (Chunk *)base;
    uintptr_t offset = address - base;
    if (offset < CHUNK_DATA + GC_HEADER)
        return NULL;
    offset -= CHUNK_DATA + GC_HEADER;
    if (offset % chunk->block != 0 || offset / chunk->block >= chunk->bump)
        return NULL;
    GcHeader *header = (GcHeader *)(address - GC_HEADER);
    return header->domain == SLOT_FREE ? NULL : header;
}

/* The free slots of one chunk of the class, taken whole (NULL: no chunk has any). */
static char *take_free_list(uint32_t size_class) {
    Chunk *chunk = partial_chunks[size_class];
    if (!chunk)
        return NULL;
    partial_chunks[size_class] = chunk->next_partial;
    chunk->listed = 0;
    char *list = chunk->free;
    chunk->free = NULL;
    return list;
}

/* An old slot of `total` bytes (header included), zeroed. */
static char *small_allocate(int64_t total) {
    if (!classes_ready)
        init_classes();
    uint32_t size_class = class_of_units[(total + 15) / 16];
    char *slot = fill_list[size_class];
    if (!slot)
        slot = take_free_list(size_class);
    if (slot) {
        fill_list[size_class] = *(char **)slot;
    } else {
        Chunk *chunk = current_chunk[size_class];
        if (!chunk || chunk->bump == chunk->count)
            chunk = current_chunk[size_class] = new_chunk(size_class);
        slot = chunk_slot(chunk, chunk->bump++);
    }
    memset(slot, 0, (size_t)class_blocks[size_class]);
    return slot;
}

/* Gives a small object's slot back to its class (kept out of use under quarantine). */
static void small_free(GcHeader *header) {
    Chunk *chunk = (Chunk *)((uintptr_t)header & ~(uintptr_t)(CHUNK - 1));
    header->domain = SLOT_FREE;
    if (quarantine)
        return;
    *(char **)header = chunk->free;
    chunk->free = (char *)header;
    if (!chunk->listed) {
        chunk->listed = 1;
        chunk->next_partial = partial_chunks[chunk->size_class];
        partial_chunks[chunk->size_class] = chunk;
    }
}

/* ── The nursery ─────────────────────────────────────────────────── */

/*
 * Young objects are made in the nursery: blocks of NURSERY_BLOCK bytes in one reserved range of address space, whose memory
 * is zero before use. Compiled code takes [cursor, cursor + total) of the current block; when it runs out, the runtime
 * gives it the next free block. A minor collection copies the reachable young objects out (to the old generation), except
 * those a word of the stack, a register, a heap cell or a conservative object points at: those are pinned, and their
 * whole block stays (RETAINED) as part of the old generation until a major collection finds nothing alive in it. The
 * other blocks are zeroed and free again. Objects larger than NURSERY_OBJECT_MAX, objects with a finalizer or in an arena
 * region, and objects without a type (CONSERVATIVE) are made old directly.
 */
#define NURSERY_BLOCK ((size_t)64 << 10)
#define NURSERY_RESERVE ((size_t)1 << 30)
#define NURSERY_OBJECT_MAX 2048

enum { BLOCK_UNUSED, BLOCK_FREE, BLOCK_ACTIVE, BLOCK_RETAINED };

typedef struct {
    uint8_t state;
    uint8_t pinned;       /* holds a pinned object (this collection) */
    uint32_t used;        /* bytes of objects from the block's start */
    uint32_t *starts;     /* object offsets, made when an ambiguous word points in (this collection) */
    uint32_t start_count;
    uint32_t starts_used; /* `used` when they were made (a block still filling makes them again) */
    uint32_t dirty;       /* bytes to zero before it is used again (zeroed then, while they go into the cache) */
} NurseryBlock;

static char *nursery_low, *nursery_high;
static NurseryBlock *nursery_blocks;
static size_t nursery_block_count, nursery_used_blocks;   /* blocks in the range; blocks ever handed out */
static size_t *free_blocks;
static size_t free_block_count;
static size_t *active_blocks;      /* the blocks handed out since the last minor collection */
static size_t active_count;
static size_t current_block = (size_t)-1;
/* Blocks between minor collections, adapted to what survives: from 8 MB, doubled while most of it survives (up to 64 MB),
   halved only after a long run of collections where almost nothing did. A nursery that shrinks at the first quiet
   collection promotes what would have died soon after (a tree being built), and the old generation pays for it. */
#define NURSERY_MIN 128
#define NURSERY_MAX 1024
#define NURSERY_QUIET_RUN 16
static size_t nursery_target = NURSERY_MIN;
static int nursery_quiet;  /* minor collections in a row where almost nothing survived */
static int nursery_disabled;        /* LARVA_GC_NOSMALL, quarantine, or the reservation failed */
static int64_t retained_blocks;

static void nursery_init(void) {
    if (nursery_low || nursery_disabled)
        return;
#ifdef _WIN32
    nursery_low = (char *)VirtualAlloc(NULL, NURSERY_RESERVE, MEM_RESERVE, PAGE_NOACCESS);
#else
    void *range = mmap(NULL, NURSERY_RESERVE, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    nursery_low = range == MAP_FAILED ? NULL : (char *)range;
#endif
    if (!nursery_low) {
        nursery_disabled = 1;
        return;
    }
    nursery_high = nursery_low + NURSERY_RESERVE;
    nursery_block_count = NURSERY_RESERVE / NURSERY_BLOCK;
    nursery_blocks = (NurseryBlock *)calloc(nursery_block_count, sizeof(NurseryBlock));
    free_blocks = (size_t *)malloc(nursery_block_count * sizeof(size_t));
    active_blocks = (size_t *)malloc(nursery_block_count * sizeof(size_t));
    if (!nursery_blocks || !free_blocks || !active_blocks)
        fail_alloc();
}

static int in_nursery(const void *word) { return (const char *)word >= nursery_low && (const char *)word < nursery_high; }

static char *block_start(size_t block) { return nursery_low + block * NURSERY_BLOCK; }

/* Closes the block a heap was filling (how much of it holds objects). */
static void close_block(LarvaHeap *heap, size_t *current) {
    if (*current != (size_t)-1 && heap->cursor)
        nursery_blocks[*current].used = (uint32_t)(heap->cursor - block_start(*current));
    *current = (size_t)-1;
    heap->cursor = heap->limit = NULL;
}

/* Closes the block compiled code was filling, and every thread's. */
static void close_current_block(void) {
    close_block(&larva_heap, &current_block);
    for (LarvaThread *thread = thread_list; thread; thread = thread->next)
        close_block(&thread->heap, &thread->block);
}

/* A zeroed block for young objects (0 when the reservation is used up), for a heap. */
static int open_block_for(LarvaHeap *heap, size_t *current) {
    size_t block;
    if (free_block_count > 0) {
        block = free_blocks[--free_block_count];
    } else if (nursery_used_blocks < nursery_block_count) {
        block = nursery_used_blocks++;
#ifdef _WIN32
        if (!VirtualAlloc(block_start(block), NURSERY_BLOCK, MEM_COMMIT, PAGE_READWRITE))
            return 0;
#endif
    } else {
        return 0;
    }
    if (nursery_blocks[block].dirty) {
        memset(block_start(block), 0, nursery_blocks[block].dirty);
        nursery_blocks[block].dirty = 0;
    }
    nursery_blocks[block].state = BLOCK_ACTIVE;
    nursery_blocks[block].used = 0;
    nursery_blocks[block].pinned = 0;
    active_blocks[active_count++] = block;
    *current = block;
    heap->cursor = block_start(block);
    heap->limit = block_start(block) + NURSERY_BLOCK;
    return 1;
}

/* Due once the blocks handed out reach the target: with threads, a target per running thread (each fills its own blocks), up to 16. */
static int nursery_due(void) {
    size_t threads = threads_alive < 16 ? (size_t)threads_alive : 16;
    return active_count >= nursery_target * (threads > 0 ? threads : 1);
}

/* ── gc objects ──────────────────────────────────────────────────── */

static GcHeader *header_of(void *payload) { return (GcHeader *)((char *)payload - GC_HEADER); }

static LargeInfo *large_info(GcHeader *header) { return (LargeInfo *)((char *)header - sizeof(LargeInfo)); }

/* An old object: a chunk slot, or a large block (its info first), or memory from the program's collector. */
static void *old_allocate(const LarvaType *type, int64_t payload) {
    int64_t total = total_bytes(payload);
    int custom = custom_allocate && !in_custom;
    int small = !custom && total <= SMALL_MAX && !small_disabled;
    char *block;
    GcHeader *header;
    if (small) {
        block = small_allocate(total);
        header = (GcHeader *)block;
    } else if (custom) {
        in_custom++;
        block = (char *)custom_allocate((uint64_t)total);
        in_custom--;
        if (!block)
            return fail_alloc();
        memset(block, 0, (size_t)total);
        header = (GcHeader *)block;
        header->flags |= FLAG_CUSTOM;
    } else {
        block = (char *)larva_aligned_alloc((size_t)total + sizeof(LargeInfo), 16);
        if (!block)
            return fail_alloc();
        memset(block, 0, (size_t)total + sizeof(LargeInfo));
        header = (GcHeader *)(block + sizeof(LargeInfo));
        header->flags = FLAG_LARGE;
        ((LargeInfo *)block)->size = payload;
        bytes_since += total;
    }
    header->type = type;
    header->refcount = 1;
    header->domain = (int8_t)(type->flags >> 8);
    header->age = AGE_OLD;
    void *object = (char *)header + GC_HEADER;
    if (!small)
        table_put(&live, (uintptr_t)object, payload, KIND_GC);
    gc_live++;
    allocations++;
    since_collection++;
    return object;
}

static int nursery_ok;   /* young objects may be made (update_fast's conditions, threads apart) */

/* A new object of `type` (`payload` bytes): young in the nursery when it can be, else old. */
static void *gc_allocate(const LarvaType *type, int64_t payload) {
    if (payload < 0)
        payload = 0;
    int64_t total = total_bytes(payload);
    void *object;
    if (nursery_ok && !(type->flags & TYPE_CONSERVATIVE) && total <= NURSERY_OBJECT_MAX) {
        /* One thread: larva_heap (the block compiled code fills too); several: the thread's own block. */
        LarvaThread *me = shared_heap ? NULL : this_thread;
        LarvaHeap *heap = me ? &me->heap : &larva_heap;
        int fits = heap->cursor && heap->cursor + total <= heap->limit;
        if (!fits) {
            int locked = rt_enter();
            size_t *current = me ? &me->block : &current_block;
            close_block(heap, current);
            fits = open_block_for(heap, current);
            rt_leave(locked);
        }
        if (fits) {
            GcHeader *header = (GcHeader *)heap->cursor;
            heap->cursor += total;
            header->type = type;
            header->refcount = 1;
            header->domain = (int8_t)(type->flags >> 8);
            object = (char *)header + GC_HEADER;
            if (nursery_due())
                larva_poll_pending = 1;
            return object;
        }
    }
    int locked = rt_enter();
    object = old_allocate(type, payload);
    if (region_count > 0) {
        GcHeader *header = header_of(object);
        header->flags |= FLAG_REGION;
        table_put(&region_of, (uintptr_t)object, regions[region_count - 1] + 1, 0);
        regions_used = 1;
    }
    rt_leave(locked);
    if (allocation_due())
        larva_poll_pending = 1;
    return object;
}

static void release_custom(void *block) {
    if (!custom_release)
        return;  /* the collector was removed: its memory stays where it is */
    in_custom++;
    custom_release(block);
    in_custom--;
}

/* Frees an old object (a chunk slot or a large block; never one in the nursery). */
static void gc_free(void *payload) {
    gc_live--;
    GcHeader *header = header_of(payload);
    if (header->flags & FLAG_FINALIZER)
        table_remove(&finalizers, (uintptr_t)payload);
    if (header->flags & FLAG_REGION)
        table_remove(&region_of, (uintptr_t)payload);
    if (small_object(payload)) {
        small_free(header);
        frees++;
        return;
    }
    table_remove(&live, (uintptr_t)payload);
    if (header->flags & FLAG_LARGE) {
        free(large_info(header)->cards);
        larva_aligned_free(large_info(header));
    } else if (!collecting) {
        release_custom(header);
    } else {
        if (custom_pending_count == custom_pending_capacity) {
            custom_pending_capacity = custom_pending_capacity ? custom_pending_capacity * 2 : 64;
            custom_pending = (void **)realloc(custom_pending, custom_pending_capacity * sizeof(void *));
            if (!custom_pending)
                fail_alloc();
        }
        custom_pending[custom_pending_count++] = header;
    }
    frees++;
}

/* The object a nursery address is in (ACTIVE or RETAINED block), or NULL. */
static void *nursery_object(uintptr_t address);

static int is_gc(void *payload) {
    if (in_nursery(payload))
        return nursery_object((uintptr_t)payload) == payload;
    if (small_object(payload))
        return 1;
    Entry *e = table_find(&live, (uintptr_t)payload);
    return e && e->kind == KIND_GC;
}

/* An object of a type compiled code describes (the slow path of the inline allocation, and every allocation it does not inline). */
LARVA_API void *larva_gc_alloc(const LarvaType *type, int64_t size) { return gc_allocate(type, size); }

/* An array of `length` elements of a type compiled code describes (`type` is the element's map, `size` the array header). */
LARVA_API void *larva_gc_array(const LarvaType *type, int64_t length) {
    char *array = (char *)gc_allocate(type, (int64_t)type->size + (length > 0 ? length : 0) * (int64_t)type->stride);
    *(int64_t *)(array + ((type->flags & TYPE_TRAILING) ? type->size - 8 : 0)) = length;
    return array;
}

/* An object compiled code gives no type for: its words are scanned conservatively and it is never moved. */
LARVA_API void *larva_gc_new(int64_t size, int64_t align) {
    (void)align;
    return gc_allocate(untyped((uint32_t)(size > 0 ? size : 0), 0, 0, 0, 0), size);
}

/* The same, for code of GC domain `domain` (the object's header says so). */
LARVA_API void *larva_gc_new_in(int64_t size, int64_t align, int64_t domain) {
    (void)align;
    return gc_allocate(untyped((uint32_t)(size > 0 ? size : 0), 0, 0, 0, (int)domain), size);
}

/* The GC domain of a gc object (its header's byte). */
LARVA_API int64_t larva_gc_domain(void *handle) {
    return handle ? (int64_t)(header_of(handle)->domain & 0x3F) : 0;
}

/* What retain and release check: a nursery address is taken as the handle it is (finding its object walks the block),
   an old one is looked up. */
static int counted_handle(void *payload) {
    return in_nursery(payload) || is_gc(payload);
}

LARVA_API void larva_gc_retain(void *handle) {
    if (!handle)
        return;
    if (!counted_handle(handle)) {
        errors++;
        return;
    }
    if (header_of(handle)->domain & DOMAIN_UNCOUNTED)
        return;
    header_of(handle)->refcount++;
}

static void push_pointer(void ***list, size_t *count, size_t *capacity, void *item) {
    if (*count == *capacity) {
        *capacity = *capacity ? *capacity * 2 : 256;
        *list = (void **)realloc(*list, *capacity * sizeof(void *));
        if (!*list)
            fail_alloc();
    }
    (*list)[(*count)++] = item;
}

static void remember(void *payload) { push_pointer(&remembered, &remembered_count, &remembered_capacity, payload); }

/* Young objects with a finalizer: a minor collection keeps the dead ones (and runs their finalizers). */
static void **young_finalizable;
static size_t young_finalizable_count, young_finalizable_capacity;

/* Runs an object's finalizer, once (its entry is removed first). */
static void finalize(void *payload) {
    Entry *e = table_find(&finalizers, (uintptr_t)payload);
    if (!e)
        return;
    void (*finalizer)(void *) = (void (*)(void *))(intptr_t)e->value;
    table_remove(&finalizers, (uintptr_t)payload);
    header_of(payload)->flags &= (uint8_t)~FLAG_FINALIZER;
    finalized++;
    finalizing++;
    finalizer(payload);
    finalizing--;
}

LARVA_API void larva_gc_finalizer(void *handle, void *finalizer) {
    if (handle && is_gc(handle)) {
        GcHeader *header = header_of(handle);
        table_put(&finalizers, (uintptr_t)handle, (int64_t)(intptr_t)finalizer, 0);
        header->flags |= FLAG_FINALIZER;
        finalizers_used = 1;
        if (header->age == AGE_YOUNG)
            push_pointer(&young_finalizable, &young_finalizable_count, &young_finalizable_capacity, handle);
    }
}

static void each_slot(void *payload, void (*visit)(void **slot), void (*ambiguous)(const uint8_t *, int64_t), void (*cell)(uintptr_t));

/* Objects whose count reached 0, still to free: a death releases what the object holds, which may die too (a list, so a
   long chain is not a deep recursion). */
/* Some code of the program counts references (entry info bit 18): a thread's environment was retained for it. */
static int counted_program;

static void **rc_dying;
static size_t rc_dying_count, rc_dying_capacity;
static int rc_draining;

static void rc_drop_slot(void **slot) {
    void *child = *slot;
    if (!child)
        return;
    GcHeader *header = header_of(child);
    if (header->domain & DOMAIN_UNCOUNTED)  /* also a free slot (SLOT_FREE) */
        return;
    if (--header->refcount == 0)
        push_pointer(&rc_dying, &rc_dying_count, &rc_dying_capacity, child);
}

/* An object's count reached 0: it dies, and the handles it holds lose an owner. */
LARVA_API void larva_rc_dead(void *handle) {
    push_pointer(&rc_dying, &rc_dying_count, &rc_dying_capacity, handle);
    if (rc_draining)
        return;
    rc_draining = 1;
    while (rc_dying_count > 0) {
        void *object = rc_dying[--rc_dying_count];
        GcHeader *header = header_of(object);
        if (in_nursery(object))
            continue;  /* young (a program that also traces): the next minor collection takes it */
        if (header->flags & FLAG_FINALIZER) {
            /* The finalizer borrows the object (native code passes handles at +0): one reference for the call, given up
               after it; the object dies then unless the finalizer kept it. */
            header->refcount = 1;
            finalize(object);
            if (--header->refcount == 0)
                push_pointer(&rc_dying, &rc_dying_count, &rc_dying_capacity, object);
            continue;
        }
        each_slot(object, rc_drop_slot, NULL, NULL);
        gc_free(object);
    }
    rc_draining = 0;
}

LARVA_API void larva_gc_release(void *handle) {
    if (!handle)
        return;
    if (!counted_handle(handle)) {
        errors++;
        return;
    }
    GcHeader *header = header_of(handle);
    if (header->domain & DOMAIN_UNCOUNTED)
        return;
    if (--header->refcount == 0)
        larva_rc_dead(handle);
}

/* The cell of a global that holds handles: allocated on first use, as a heap cell (so the collector scans it). */
LARVA_API void *larva_global(void **slot, int64_t size) {
    if (!*slot) {
        int locked = rt_enter();
        if (!*slot) {
            void *cell = alloc_unlocked(size, 16);
            Entry *e = table_find(&live, (uintptr_t)cell);
            if (e)
                e->kind = KIND_ROOT;
            *slot = cell;
        }
        rt_leave(locked);
    }
    return *slot;
}

/* A zeroed array with its length in the first 8 bytes; elements start at `header` (code that gives no element type). */
/* `managed`: 1 a gc array, 2 its elements hold no references, 4 an object ending with the array in place (`header` reaches
   the elements, the length is the word before them); bits 8..15 the domain. */
LARVA_API void *larva_array_new(int64_t length, int64_t header, int64_t stride, int32_t managed) {
    char *array;
    if (managed & 1)
        array = (char *)gc_allocate(untyped((uint32_t)header, (uint32_t)stride, (managed & 4) ? 2 : 1, (managed & 3) == 3, (managed >> 8) & 0xFF),
            header + (length > 0 ? length : 0) * stride);
    else
        array = (char *)larva_alloc(header + (length > 0 ? length : 0) * stride, header < 16 ? header : 16);
    *(int64_t *)(array + ((managed & 4) ? header - 8 : 0)) = length;
    return array;
}

/*
 * The write barrier, after `size` bytes were stored at `slot` inside the gc object `owner`: compiled code calls it only when
 * the owner is old (its age byte is AGE_OLD). An object is remembered whole (scanned at the next minor collection); a large
 * one marks the cards written, so a big array is not scanned whole for one store.
 */
static void gc_wb_unlocked(void *owner, void *slot, int64_t size);

#ifdef _WIN32
static int age_old_to_remembered(GcHeader *header) {
    return _InterlockedCompareExchange8((volatile char *)&header->age, AGE_REMEMBERED, AGE_OLD) == AGE_OLD;
}
#else
static int age_old_to_remembered(GcHeader *header) {
    uint8_t expected = AGE_OLD;
    return __atomic_compare_exchange_n(&header->age, &expected, AGE_REMEMBERED, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
}
#endif

/* With threads the barrier takes the runtime's lock only the first time an object becomes remembered (or carded): the
   age moves 1 → 2 by an atomic compare-exchange, and a card's byte is set by whoever writes it. */
LARVA_API void larva_gc_wb(void *owner, void *slot, int64_t size) {
    if (!threading) {
        gc_wb_unlocked(owner, slot, size);
        return;
    }
    barrier_calls++;
    if (!owner)
        return;
    GcHeader *header = header_of(owner);
    if (header->domain == SLOT_FREE)
        return;
    if (!(header->flags & FLAG_LARGE)) {
        if (header->age == AGE_OLD && age_old_to_remembered(header)) {
            int locked = rt_enter();
            remember(owner);
            rt_leave(locked);
        }
        return;
    }
    if (header->age != AGE_OLD)
        return;
    LargeInfo *info = large_info(header);
    size_t count = ((size_t)info->size + CARD - 1) / CARD;
    if (!info->cards) {
        int locked = rt_enter();
        if (!info->cards) {
            uint8_t *cards = (uint8_t *)calloc(count ? count : 1, 1);
            if (!cards)
                fail_alloc();
            info->cards = cards;
        }
        rt_leave(locked);
    }
    int64_t from = (char *)slot - (char *)owner, to = from + (size > 0 ? size : 8) - 1;
    for (int64_t card = from / CARD; card <= to / CARD; card++)
        if (card >= 0 && (size_t)card < count)
            info->cards[card] = 1;
    if (!(header->flags & FLAG_CARDED)) {
        int locked = rt_enter();
        if (!(header->flags & FLAG_CARDED)) {
            header->flags |= FLAG_CARDED;
            push_pointer(&carded, &carded_count, &carded_capacity, owner);
        }
        rt_leave(locked);
    }
}

static void gc_wb_unlocked(void *owner, void *slot, int64_t size) {
    barrier_calls++;
    if (!owner)
        return;
    GcHeader *header = header_of(owner);
    if (header->age != AGE_OLD || header->domain == SLOT_FREE)
        return;
    if (!(header->flags & FLAG_LARGE)) {
        header->age = AGE_REMEMBERED;
        remember(owner);
        return;
    }
    LargeInfo *info = large_info(header);
    size_t count = ((size_t)info->size + CARD - 1) / CARD;
    if (!info->cards) {
        info->cards = (uint8_t *)calloc(count ? count : 1, 1);
        if (!info->cards)
            fail_alloc();
    }
    int64_t from = (char *)slot - (char *)owner, to = from + (size > 0 ? size : 8) - 1;
    for (int64_t card = from / CARD; card <= to / CARD; card++)
        if (card >= 0 && (size_t)card < count)
            info->cards[card] = 1;
    if (!(header->flags & FLAG_CARDED)) {
        header->flags |= FLAG_CARDED;
        push_pointer(&carded, &carded_count, &carded_capacity, owner);
    }
}

/*
 * Pins: a function that borrows inside a gc object pins it until it returns. They are taken and given back by the same
 * thread, most recent first, so each thread keeps them on a stack of its own (no lock); collections read every thread's.
 */
LARVA_API void larva_pin(void *handle) {
    LarvaThread *me = this_thread ? this_thread : &main_record;
    if (me->pin_count == me->pin_capacity) {
        size_t capacity = me->pin_capacity ? me->pin_capacity * 2 : 16;
        void **grown = (void **)realloc(me->pins, capacity * sizeof(void *));
        if (!grown)
            fail_alloc();
        me->pins = grown;
        me->pin_capacity = capacity;
    }
    me->pins[me->pin_count++] = handle;
}

LARVA_API void larva_unpin(void *handle) {
    if (!handle)
        return;
    LarvaThread *me = this_thread ? this_thread : &main_record;
    for (size_t i = me->pin_count; i > 0; i--) {
        if (me->pins[i - 1] == handle) {
            memmove(&me->pins[i - 1], &me->pins[i], (me->pin_count - i) * sizeof(void *));
            me->pin_count--;
            return;
        }
    }
}

/* Every thread's pins (the main thread's record holds its own even before threads exist). */
static void scan_pins(void (*visit)(uintptr_t)) {
    for (size_t i = 0; i < main_record.pin_count; i++)
        visit((uintptr_t)main_record.pins[i]);
    for (LarvaThread *thread = thread_list; thread; thread = thread->next) {
        if (thread == &main_record)
            continue;
        for (size_t i = 0; i < thread->pin_count; i++)
            visit((uintptr_t)thread->pins[i]);
    }
}

/* ── Arena regions ───────────────────────────────────────────────── */

/* Closes a region: finalizers first (everything in it is still there for them), then frees. */
static void free_region(int32_t region) {
    size_t count = 0, capacity = 0;
    void **dying = NULL;
    for (size_t i = 0; i < region_of.capacity; i++) {
        Entry *e = &region_of.entries[i];
        if (e->key > 1 && e->value == region + 1) {
            if (count == capacity) {
                capacity = capacity ? capacity * 2 : 64;
                dying = (void **)realloc(dying, capacity * sizeof(void *));
                if (!dying)
                    fail_alloc();
            }
            dying[count++] = (void *)e->key;
        }
    }
    for (size_t i = 0; i < count; i++)
        if (is_gc(dying[i]))
            finalize(dying[i]);
    for (size_t i = 0; i < count; i++)
        if (is_gc(dying[i]))
            gc_free(dying[i]);
    free(dying);
}

/* Whether compiled code may allocate inline (in the nursery): not inside arena regions, with the program's own collector,
   a fixed threshold, quarantine, or without generations. */
static void update_fast(void) {
    if (!classes_ready)
        init_classes();
    nursery_init();
    int fast = !nursery_disabled && !quarantine && !small_disabled && !fixed_threshold && region_count == 0 && !custom_allocate
        && !custom_poll && generational();
    if (!fast)
        close_current_block();
    nursery_ok = fast;
    larva_heap.fast = fast && shared_heap;
}

LARVA_API void larva_region_enter(int32_t region) {
    if (region_count == region_capacity) {
        region_capacity = region_capacity ? region_capacity * 2 : 16;
        regions = (int32_t *)realloc(regions, region_capacity * sizeof(int32_t));
        if (!regions)
            fail_alloc();
    }
    regions[region_count++] = region;
    update_fast();
}

LARVA_API void larva_region_exit(int32_t region) {
    if (region_count == 0 || regions[region_count - 1] != region)
        errors++;
    else
        region_count--;
    update_fast();
    free_region(region);
}

LARVA_API int32_t larva_region_depth(void) { return (int32_t)region_count; }

/* An exception leaves a frame: the regions it opened (above `depth`) close. */
LARVA_API void larva_region_unwind(int32_t depth) {
    while ((int32_t)region_count > depth)
        free_region(regions[--region_count]);
    update_fast();
}

#pragma optimize("s", on) /* cold: compiled for size (see COLD) */
/* ── Stack maps ──────────────────────────────────────────────────── */

static const uint8_t *align8(const uint8_t *p) { return (const uint8_t *)(((uintptr_t)p + 7) & ~(uintptr_t)7); }

/* Parses consecutive version-3 tables (one per object file) from the image's stack map section. */
static void parse_stack_maps(const uint8_t *p, const uint8_t *end) {
    while (p + 16 <= end) {
        if (*p != 3) {          /* padding between the tables of different objects */
            p++;
            continue;
        }
        uint32_t functions = *(const uint32_t *)(p + 4);
        uint32_t constant_count = *(const uint32_t *)(p + 8);
        const uint8_t *function_table = p + 16;
        const uint64_t *constants = (const uint64_t *)(function_table + 24 * (size_t)functions);
        const uint8_t *record = (const uint8_t *)(constants + constant_count);
        for (uint32_t f = 0; f < functions; f++) {
            uint64_t address = *(const uint64_t *)(function_table + 24 * (size_t)f);
            uint64_t records = *(const uint64_t *)(function_table + 24 * (size_t)f + 16);
            size_t capacity = 0, count = 0;
            Root *roots = NULL;
            for (uint64_t r = 0; r < records; r++) {
                uint16_t locations = *(const uint16_t *)(record + 14);
                const uint8_t *location = record + 16;
                for (uint16_t l = 0; l + 1 < locations; l += 2, location += 24) {
                    uint8_t kind = location[0], size_kind = location[12];
                    int32_t size_value = *(const int32_t *)(location + 20);
                    int64_t size = size_kind == 5 ? (int64_t)constants[size_value] : size_value;
                    if (kind != 2)
                        continue;
                    if (count == capacity) {
                        capacity = capacity ? capacity * 2 : 4;
                        roots = (Root *)realloc(roots, capacity * sizeof(Root));
                        if (!roots)
                            fail_alloc();
                    }
                    roots[count].reg = *(const uint16_t *)(location + 4);
                    roots[count].offset = *(const int32_t *)(location + 8);
                    roots[count].size = (int32_t)size;
                    count++;
                }
                record = align8(record + 16 + 12 * (size_t)locations);
                uint16_t live_outs = *(const uint16_t *)(record + 2);
                record = align8(record + 4 + 4 * (size_t)live_outs);
            }
            root_sets = (RootSet *)realloc(root_sets, (root_set_count + 1) * sizeof(RootSet));
            if (!root_sets)
                fail_alloc();
            root_sets[root_set_count].roots = roots;
            root_sets[root_set_count].count = count;
            table_put(&frames, (uintptr_t)address, (int64_t)root_set_count, 0);
            root_set_count++;
        }
        p = record;
    }
}

#ifdef _WIN32
/* Reads the stack map section of one image (linkers truncate section names to 8 characters: ".llvm_st"). */
static void load_image_stack_maps(uint8_t *base) {
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(base + dos->e_lfanew);
    IMAGE_SECTION_HEADER *section = IMAGE_FIRST_SECTION(nt);
    for (WORD s = 0; s < nt->FileHeader.NumberOfSections; s++, section++) {
        if (memcmp(section->Name, ".llvm_st", 8) == 0) {
            const uint8_t *start = base + section->VirtualAddress;
            parse_stack_maps(start, start + section->Misc.VirtualSize);
        }
    }
}

/* The program and every Larva DLL it loaded: each image is read once (images loaded later are read when they appear). */
static HMODULE seen_images[256];
static DWORD seen_image_count;

static void load_stack_maps(void) {
    stack_maps_loaded = 1;
    HMODULE images[256];
    DWORD bytes = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), images, sizeof images, &bytes)) {
        if (seen_image_count == 0) {
            seen_images[seen_image_count++] = GetModuleHandleW(NULL);
            load_image_stack_maps((uint8_t *)seen_images[0]);
        }
        return;
    }
    for (DWORD i = 0; i < bytes / sizeof(HMODULE) && i < 256; i++) {
        DWORD k = 0;
        while (k < seen_image_count && seen_images[k] != images[i])
            k++;
        if (k < seen_image_count || seen_image_count == 256)
            continue;
        seen_images[seen_image_count++] = images[i];
        load_image_stack_maps((uint8_t *)images[i]);
    }
}

#else
static void load_stack_maps(void) { stack_maps_loaded = 1; }
#endif

#pragma optimize("", on)
/* ── Finding objects from ambiguous words ────────────────────────── */

/* Large gc objects sorted by address (rebuilt when one was made or freed): what a pointer into one belongs to. */
static uintptr_t *large_starts, *large_ends;
static size_t large_count, large_capacity;
static size_t large_indexed_live = (size_t)-1;

static int compare_addresses(const void *a, const void *b) {
    uintptr_t x = *(const uintptr_t *)a, y = *(const uintptr_t *)b;
    return x < y ? -1 : x > y;
}

static void index_large(void) {
    large_count = 0;
    for (size_t i = 0; i < live.capacity; i++) {
        Entry *e = &live.entries[i];
        if (e->key <= 1 || e->kind != KIND_GC)
            continue;
        if (large_count == large_capacity) {
            large_capacity = large_capacity ? large_capacity * 2 : 64;
            large_starts = (uintptr_t *)realloc(large_starts, large_capacity * sizeof(uintptr_t));
            large_ends = (uintptr_t *)realloc(large_ends, large_capacity * sizeof(uintptr_t));
            if (!large_starts || !large_ends)
                fail_alloc();
        }
        large_starts[large_count++] = e->key;
    }
    qsort(large_starts, large_count, sizeof(uintptr_t), compare_addresses);
    for (size_t i = 0; i < large_count; i++)
        large_ends[i] = large_starts[i] + (uintptr_t)payload_bytes(header_of((void *)large_starts[i]));
}

/* The object starts of a nursery block, by walking it (once per collection). */
static void block_starts(NurseryBlock *block, size_t index) {
    if (block->starts)
        return;
    size_t capacity = 64, count = 0;
    uint32_t *starts = (uint32_t *)malloc(capacity * sizeof(uint32_t));
    char *base = block_start(index);
    for (uint32_t at = 0; at < block->used;) {
        GcHeader *header = (GcHeader *)(base + at);
        if (!header->type)
            break;
        if (count == capacity) {
            capacity *= 2;
            starts = (uint32_t *)realloc(starts, capacity * sizeof(uint32_t));
        }
        starts[count++] = at;
        at += (uint32_t)total_bytes(payload_bytes(header));
    }
    block->starts = starts;
    block->start_count = (uint32_t)count;
    block->starts_used = block->used;
}

static size_t *walked_blocks;
static size_t walked_count, walked_capacity;

static void *nursery_object(uintptr_t address) {
    if (!in_nursery((void *)address))
        return NULL;
    size_t index = (size_t)((char *)address - nursery_low) / NURSERY_BLOCK;
    NurseryBlock *block = &nursery_blocks[index];
    if (block->state != BLOCK_ACTIVE && block->state != BLOCK_RETAINED)
        return NULL;
    uint32_t offset = (uint32_t)(address - (uintptr_t)block_start(index));
    if (index == current_block && larva_heap.cursor)
        block->used = (uint32_t)(larva_heap.cursor - block_start(index));
    if (offset >= block->used)
        return NULL;
    if (block->starts && block->starts_used != block->used) {
        free(block->starts);
        block->starts = NULL;
    }
    if (!block->starts) {
        block_starts(block, index);
        if (walked_count == walked_capacity) {
            walked_capacity = walked_capacity ? walked_capacity * 2 : 64;
            walked_blocks = (size_t *)realloc(walked_blocks, walked_capacity * sizeof(size_t));
        }
        walked_blocks[walked_count++] = index;
    }
    /* The last start at or before the offset. */
    uint32_t low = 0, high = block->start_count;
    if (high == 0)
        return NULL;
    while (high - low > 1) {
        uint32_t middle = (low + high) / 2;
        if (block->starts[middle] <= offset)
            low = middle;
        else
            high = middle;
    }
    uint32_t start = block->starts[low];
    if (start > offset)
        return NULL;
    GcHeader *header = (GcHeader *)(block_start(index) + start);
    if (offset >= start + total_bytes(payload_bytes(header)))
        return NULL;
    /* In a retained block only the pinned survivors are objects (the rest was copied out, or is dead). */
    if (block->state == BLOCK_RETAINED && (header->age == AGE_YOUNG || header->flags & FLAG_FORWARDED))
        return NULL;
    return (char *)header + GC_HEADER;
}

static void forget_walks(void) {
    for (size_t i = 0; i < walked_count; i++) {
        free(nursery_blocks[walked_blocks[i]].starts);
        nursery_blocks[walked_blocks[i]].starts = NULL;
    }
    walked_count = 0;
}

/* ── Major collection: marking ──────────────────────────────────── */

static void **mark_stack;
static size_t mark_count, mark_capacity;
static void **cell_stack;
static size_t cell_count, cell_capacity;

static void mark_object(GcHeader *header) {
    if (header->mark)
        return;
    header->mark = 1;
    push_pointer(&mark_stack, &mark_count, &mark_capacity, (char *)header + GC_HEADER);
}

/* A word that may be an object's or a heap cell's address (or point inside one): what it reaches stays. */
static void consider_ambiguous(uintptr_t address) {
    if (address < 4096)
        return;
    if (in_nursery((void *)address)) {
        void *object = nursery_object(address);
        if (object)
            mark_object(header_of(object));
        return;
    }
    if (address >= small_low && address < small_high) {
        uintptr_t base = address & ~(uintptr_t)(CHUNK - 1);
        if (chunk_map[(base - small_low) / CHUNK]) {
            Chunk *chunk = (Chunk *)base;
            uintptr_t offset = address - base;
            if (offset < CHUNK_DATA)
                return;
            size_t slot = (offset - CHUNK_DATA) / chunk->block;
            if (slot >= chunk->bump)
                return;
            GcHeader *header = (GcHeader *)chunk_slot(chunk, slot);
            if (header->domain != SLOT_FREE)
                mark_object(header);
            return;
        }
    }
    if (large_count > 0 && address >= large_starts[0] - GC_HEADER) {
        size_t low = 0, high = large_count;
        while (high - low > 1) {
            size_t middle = (low + high) / 2;
            if (large_starts[middle] - GC_HEADER <= address)
                low = middle;
            else
                high = middle;
        }
        if (address <= large_ends[low]) {
            mark_object(header_of((void *)large_starts[low]));
            return;
        }
    }
    /* Heap cells, by their address (and custom memory, which is in the live table too). */
    Entry *e = table_find(&live, address);
    if (!e)
        return;
    if (e->kind == KIND_HEAP) {
        if (!e->mark) {
            e->mark = 1;
            push_pointer(&cell_stack, &cell_count, &cell_capacity, (void *)address);
        }
    } else if (e->kind == KIND_GC) {
        mark_object(header_of((void *)address));
    }
}

static void scan_ambiguous(const uint8_t *start, int64_t size) {
    for (int64_t offset = 0; offset + 8 <= size; offset += 8)
        consider_ambiguous(*(const uintptr_t *)(start + offset));
}

/* Calls `visit` on each handle slot of an object, by its type (conservative objects: `ambiguous` on every word). */
static void each_slot(void *payload, void (*visit)(void **slot), void (*ambiguous)(const uint8_t *, int64_t), void (*cell)(uintptr_t)) {
    GcHeader *header = header_of(payload);
    const LarvaType *type = header->type;
    if (type->flags & TYPE_LEAF)
        return;
    if (type->flags & TYPE_CONSERVATIVE) {
        if (ambiguous)
            ambiguous((const uint8_t *)payload, payload_bytes(header));
        return;
    }
    char *base = (char *)payload;
    if (type->flags & TYPE_ARRAY) {
        int64_t length = *(int64_t *)payload;
        char *element = base + type->size;
        for (int64_t i = 0; i < length; i++, element += type->stride) {
            for (uint32_t k = 0; k < type->handles; k++)
                visit((void **)(element + type->offsets[k]));
            if (cell)
                for (uint32_t k = 0; k < type->cells; k++)
                    cell(*(uintptr_t *)(element + type->offsets[type->handles + k]));
        }
        return;
    }
    for (uint32_t k = 0; k < type->handles; k++)
        visit((void **)(base + type->offsets[k]));
    if (cell)
        for (uint32_t k = 0; k < type->cells; k++)
            cell(*(uintptr_t *)(base + type->offsets[type->handles + k]));
}

static void mark_slot(void **slot) {
    void *target = *slot;
    if (target)
        mark_object(header_of(target));
}

/* Scans what the mark stacks hold (objects and heap cells), and what they lead to. */
static void drain_marks(void) {
    while (mark_count > 0 || cell_count > 0) {
        while (mark_count > 0)
            each_slot(mark_stack[--mark_count], mark_slot, scan_ambiguous, consider_ambiguous);
        while (cell_count > 0) {
            void *cell = cell_stack[--cell_count];
            Entry *e = table_find(&live, (uintptr_t)cell);
            if (e)
                scan_ambiguous((const uint8_t *)cell, e->value);
        }
    }
}

#ifdef _WIN32
/*
 * The thread's registers and its stack, every word, from here to the top: compiled code keeps handles in registers and
 * stack slots as it likes; what they point into is pinned (a minor collection) or marked (a major one).
 */
static __declspec(noinline) void scan_stack(void (*visit)(uintptr_t)) {
    CONTEXT context;
    ULONG_PTR low, high;
    GetCurrentThreadStackLimits(&low, &high);
    RtlCaptureContext(&context);
    DWORD64 registers[] = {context.Rax, context.Rbx, context.Rcx, context.Rdx, context.Rsi, context.Rdi, context.Rbp,
        context.R8, context.R9, context.R10, context.R11, context.R12, context.R13, context.R14, context.R15};
    for (size_t r = 0; r < sizeof registers / sizeof registers[0]; r++)
        visit((uintptr_t)registers[r]);
    uintptr_t from = (uintptr_t)context.Rsp & ~(uintptr_t)7;
    if (from < low)
        from = low;
    for (uintptr_t at = from; at + 8 <= high; at += 8)
        visit(*(const uintptr_t *)at);
}

#else
/* The top (highest address) of the main thread's stack: where the conservative scan ends. */
static uintptr_t stack_top(void) {
    if (this_thread && this_thread->stack_high)
        return this_thread->stack_high;
    static uintptr_t top;
    if (!top) {
#ifdef __APPLE__
        top = (uintptr_t)pthread_get_stackaddr_np(pthread_self());
#else
        pthread_attr_t attributes;
        void *address;
        size_t size;
        pthread_getattr_np(pthread_self(), &attributes);
        pthread_attr_getstack(&attributes, &address, &size);
        pthread_attr_destroy(&attributes);
        top = (uintptr_t)address + size;
#endif
    }
    return top;
}

/* Every word between here and the top of the stack, after the callee-saved registers are spilled into this frame. */
static __attribute__((noinline)) void scan_stack(void (*visit)(uintptr_t)) {
    __builtin_unwind_init();
    volatile uintptr_t here = (uintptr_t)&here;
    uintptr_t low = here & ~(uintptr_t)7;
    for (uintptr_t at = low; at + 8 <= stack_top(); at += 8)
        visit(*(const uintptr_t *)at);
}
#endif

/* The stacks of the threads stopped for a collection (their saved registers, and their stack from where it stopped), and
   what each thread runs: the collecting thread's own stack is scan_stack's. */
static void scan_threads(void (*visit)(uintptr_t)) {
    for (LarvaThread *thread = thread_list; thread; thread = thread->next) {
        visit((uintptr_t)thread->env);
        /* A thread that has not run yet has no stack to scan (saved_sp is 0 until it first stops). */
        if (thread == this_thread || thread->state != THREAD_STOPPED || thread->saved_sp == 0)
            continue;
        for (size_t r = 0; r < 16; r++)
            visit(thread->registers[r]);
        for (uintptr_t at = thread->saved_sp & ~(uintptr_t)7; at + 8 <= thread->stack_high; at += 8)
            visit(*(const uintptr_t *)at);
    }
}

static void scan_stacks(void (*visit)(uintptr_t)) {
    scan_stack(visit);
    if (threading)
        scan_threads(visit);
}

/* ── Minor collection: copying the nursery out ──────────────────── */

static int64_t minor_cards_ns, minor_roots_ns, minor_copy_ns, minor_blocks_ns, cards_scanned, slots_evacuated, remembered_scanned;
static void **copy_queue;
static size_t copy_count, copy_capacity;
static int64_t promoted, promoted_bytes;

/* An ambiguous word during a minor collection: a young object it points into is pinned (it cannot move). */
static void pin_ambiguous(uintptr_t address) {
    if (!in_nursery((void *)address))
        return;
    size_t index = (size_t)((char *)address - nursery_low) / NURSERY_BLOCK;
    if (nursery_blocks[index].state != BLOCK_ACTIVE)
        return;
    void *object = nursery_object(address);
    if (!object)
        return;
    GcHeader *header = header_of(object);
    if (header->flags & FLAG_PINNED)
        return;
    header->flags |= FLAG_PINNED;
    nursery_blocks[index].pinned = 1;
    pinned_total++;
    push_pointer(&copy_queue, &copy_count, &copy_capacity, object);
}

static void pin_words(const uint8_t *start, int64_t size) {
    for (int64_t offset = 0; offset + 8 <= size; offset += 8)
        pin_ambiguous(*(const uintptr_t *)(start + offset));
}

/*
 * Copying in parallel. A minor collection copies with several threads: the collector and helpers parked between
 * collections (half the processors, up to GC_WORKERS_DEFAULT). Each has a stack of copies (and old objects) to scan and a
 * cache of old slots per size class; a stack that grows gives half to a shared list the idle ones take from. Two threads
 * may reach the same young object: each copies it, and a compare-exchange on the header's type word decides whose copy it
 * becomes (the word then holds the copy, tagged with bit 0, and the count the payload size); the other gives its slot
 * back. The payload stays as it was, so a thread that read the type before the exchange still reads the right length.
 */
#define GC_WORKERS_MAX 8
#define GC_WORKERS_DEFAULT 4    /* more stop paying: the copy waits on memory, and they share it */
#define LOCAL_SLOTS 32
#define SHARE_AT 2       /* a stack this deep gives half away while some thread is idle */
#define TAKE_AT_MOST 16

/* Each on cache lines of its own: the threads update theirs at every object. */
#ifdef _WIN32
#define GC_LINE_ALIGNED __declspec(align(128))
#else
#define GC_LINE_ALIGNED __attribute__((aligned(128)))
#endif
typedef struct GC_LINE_ALIGNED {
    void **stack;
    size_t count, capacity;
    char *slots[CLASS_COUNT];      /* old slots taken for promotions (free: SLOT_FREE), linked through their first word */
    int64_t promoted, promoted_bytes, live, evacuated;
} GcWorker;

static GcWorker gc_workers[GC_WORKERS_MAX];
static THREAD_LOCAL GcWorker *gc_worker;
static int gc_worker_count;        /* threads that copy, the collector included (0 until the first minor collection) */
#ifdef _WIN32
static CRITICAL_SECTION gc_share_lock, gc_alloc_lock, gc_pool_lock;
static CONDITION_VARIABLE gc_pool_start, gc_pool_done;
static long cas_add(volatile long *at, long n) { return _InterlockedExchangeAdd(at, n) + n; }
static int64_t cas_word(volatile int64_t *at, int64_t expected, int64_t value) { return _InterlockedCompareExchange64(at, value, expected); }
#define GC_PAUSE() _mm_pause()
#else
static pthread_mutex_t gc_share_lock, gc_alloc_lock, gc_pool_lock;
static pthread_cond_t gc_pool_start = PTHREAD_COND_INITIALIZER, gc_pool_done = PTHREAD_COND_INITIALIZER;
static long cas_add(volatile long *at, long n) { return __atomic_add_fetch(at, n, __ATOMIC_SEQ_CST); }
static int64_t cas_word(volatile int64_t *at, int64_t expected, int64_t value) {
    __atomic_compare_exchange_n(at, &expected, value, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    return expected;
}
#define GC_PAUSE() __builtin_ia32_pause()
#endif
/* The collector copies alone at first; once it has copied WAKE_AFTER objects in a collection the helpers join (a small
   collection does not pay for waking them). */
#define WAKE_AFTER 2048
static volatile int copy_threads;  /* threads copying now */
static int copy_parallel, helpers_awake;
static void **volatile shared_work;
static volatile size_t shared_count;
static size_t shared_capacity;
static volatile long idle_workers;
static int pool_generation, pool_pending;
#ifdef LARVA_DIAGNOSTICS
static int gc_threads_setting;     /* LARVA_GC_THREADS: how many threads copy (1: the collector alone) */
#else
enum { gc_threads_setting = 0 };
#endif

/* An old slot of `size_class` for a promotion: from the thread's cache, refilled from the free list or the newest chunk. */
static char *worker_slot(GcWorker *w, uint32_t size_class) {
    char *slot = w->slots[size_class];
    if (!slot) {
        /* One chunk's free slots, or slots never used yet: taken under the lock, linked outside it. */
        LOCK(gc_alloc_lock);
        slot = take_free_list(size_class);
        Chunk *chunk = NULL;
        uint32_t first = 0, count = 0;
        if (!slot) {
            chunk = current_chunk[size_class];
            if (!chunk || chunk->bump == chunk->count)
                chunk = current_chunk[size_class] = new_chunk(size_class);
            first = chunk->bump;
            count = chunk->count - first < LOCAL_SLOTS ? chunk->count - first : LOCAL_SLOTS;
            chunk->bump += count;
        }
        UNLOCK(gc_alloc_lock);
        for (uint32_t i = 0; i < count; i++) {
            char *taken = chunk_slot(chunk, first + count - 1 - i);
            ((GcHeader *)taken)->domain = SLOT_FREE;
            *(char **)taken = slot;
            slot = taken;
        }
    }
    w->slots[size_class] = *(char **)slot;
    return slot;
}

/* An old home for a young object being promoted: its header written, its payload for the copy to fill. */
static void *worker_promote(GcWorker *w, const LarvaType *type, int64_t payload) {
    int64_t total = total_bytes(payload);
    if (total > SMALL_MAX || small_disabled || custom_allocate) {
        LOCK(gc_alloc_lock);
        void *made = old_allocate(type, payload);
        UNLOCK(gc_alloc_lock);
        return made;
    }
    if (!classes_ready)
        init_classes();
    char *slot = worker_slot(w, class_of_units[(total + 15) / 16]);
    GcHeader *header = (GcHeader *)slot;
    memset(header, 0, GC_HEADER);
    header->type = type;
    header->age = AGE_OLD;
    w->live++;
    return slot + GC_HEADER;
}

/* A copy another thread's copy beat: its slot goes back. */
static void worker_unpromote(GcWorker *w, void *copy, int64_t payload) {
    int64_t total = total_bytes(payload);
    if (total > SMALL_MAX || small_disabled || custom_allocate) {
        LOCK(gc_alloc_lock);
        gc_free(copy);
        UNLOCK(gc_alloc_lock);
        return;
    }
    GcHeader *header = header_of(copy);
    uint32_t size_class = class_of_units[(total + 15) / 16];
    header->domain = SLOT_FREE;
    *(char **)header = w->slots[size_class];
    w->slots[size_class] = (char *)header;
    w->live--;
}

/* The payload size of a young object of `type` (read once: the header may be changing). */
static int64_t young_payload(const LarvaType *type, const GcHeader *header) {
    if (type->flags & (TYPE_ARRAY | TYPE_TRAILING)) {
        int64_t length = *(const int64_t *)((const char *)header + GC_HEADER + ((type->flags & TYPE_TRAILING) ? type->size - 8 : 0));
        return (int64_t)type->size + (length > 0 ? length : 0) * (int64_t)type->stride;
    }
    return type->size;
}

static void share_work(GcWorker *w);

static void wake_helpers(void) {
    if (helpers_awake || !copy_parallel || gc_worker_count <= 1)
        return;
    helpers_awake = 1;
    idle_workers = 0;
    copy_threads = gc_worker_count;
    LOCK(gc_pool_lock);
    pool_pending = gc_worker_count - 1;
    pool_generation++;
    WAKE_ALL(gc_pool_start);
    UNLOCK(gc_pool_lock);
}

/* Something for this thread to scan: a copy, an old object, or (bit 0) a large object's dirty cards. */
static void push_work(GcWorker *w, void *item) {
    push_pointer(&w->stack, &w->count, &w->capacity, item);
    if (copy_threads > 1 && w->count >= SHARE_AT && idle_workers > 0 && shared_count == 0)
        share_work(w);
}

/* Half of a thread's stack (the oldest entries: the larger parts of what is left) to the shared list. */
static void share_work(GcWorker *w) {
    LOCK(gc_share_lock);
    size_t half = w->count / 2;
    for (size_t i = 0; i < half; i++)
        push_pointer((void ***)&shared_work, (size_t *)&shared_count, &shared_capacity, w->stack[i]);
    memmove(w->stack, w->stack + half, (w->count - half) * sizeof(void *));
    w->count -= half;
    UNLOCK(gc_share_lock);
}

static int take_shared(GcWorker *w) {
    if (shared_count == 0)
        return 0;
    LOCK(gc_share_lock);
    size_t take = shared_count < TAKE_AT_MOST ? shared_count : TAKE_AT_MOST;
    for (size_t i = 0; i < take; i++)
        push_pointer(&w->stack, &w->count, &w->capacity, shared_work[--shared_count]);
    UNLOCK(gc_share_lock);
    return take > 0;
}

/* A slot that holds a handle: a young object it names is copied out (once), and the slot updated. */
static void evacuate(void **slot) {
    GcWorker *w = gc_worker;
    w->evacuated++;
    void *target = *slot;
    if (!in_nursery(target))
        return;
    size_t index = (size_t)((char *)target - nursery_low) / NURSERY_BLOCK;
    if (nursery_blocks[index].state != BLOCK_ACTIVE)
        return;
    GcHeader *header = header_of(target);
    volatile int64_t *word = (volatile int64_t *)&header->type;
    int64_t seen = *word;
    if (seen & 1) {
        *slot = (void *)(seen & ~(int64_t)1);
        return;
    }
    if (header->flags & FLAG_PINNED)
        return;
    const LarvaType *type = (const LarvaType *)seen;
    int64_t payload = young_payload(type, header);
    void *copy = worker_promote(w, type, payload);
    GcHeader *moved = header_of(copy);
    memcpy(copy, target, (size_t)(payload < 8 ? 8 : payload));
    moved->refcount = header->refcount;
    moved->flags |= header->flags & FLAG_FINALIZER;
    int64_t before = cas_word(word, seen, (int64_t)copy | 1);
    if (before != seen) {
        worker_unpromote(w, copy, payload);
        *slot = (void *)(before & ~(int64_t)1);
        return;
    }
    header->refcount = (int32_t)payload;
    header->flags |= FLAG_FORWARDED;
    if (header->flags & FLAG_FINALIZER) {
        LOCK(gc_alloc_lock);
        Entry *e = table_find(&finalizers, (uintptr_t)target);
        if (e) {
            int64_t finalizer = e->value;
            table_remove(&finalizers, (uintptr_t)target);
            table_put(&finalizers, (uintptr_t)copy, finalizer, 0);
        }
        UNLOCK(gc_alloc_lock);
    }
    *slot = copy;
    w->promoted++;
    w->promoted_bytes += payload;
    if (w->promoted == WAKE_AFTER && w == &gc_workers[0])
        wake_helpers();
    if (!(type->flags & TYPE_LEAF))
        push_work(w, copy);  /* (a copy without handles has nothing to scan) */
}

static void evacuate_from(void *payload) { each_slot(payload, evacuate, NULL, NULL); }

/* A run of a large object's cards (the threads scan a big array's cards in parts). */
typedef struct {
    void *owner;
    size_t first, end;
} CardRun;
#define CARDS_PER_RUN 64
static CardRun *card_runs;
static size_t card_run_count, card_run_capacity;

/* The handle slots of a large object's dirty cards in [first, end). */
static void evacuate_cards(void *owner, size_t first_card, size_t end_card) {
    GcHeader *header = header_of(owner);
    LargeInfo *info = large_info(header);
    if (!info->cards)
        return;
    const LarvaType *type = header->type;
    size_t count = ((size_t)info->size + CARD - 1) / CARD;
    if (end_card > count)
        end_card = count;
    for (size_t card = first_card; card < end_card; card++) {
        if (!info->cards[card])
            continue;
        info->cards[card] = 0;
        gc_worker->evacuated++;
        int64_t from = (int64_t)card * CARD, to = from + CARD < info->size ? from + CARD : info->size;
        if (type->flags & TYPE_LEAF)
            continue;
        if (type->flags & TYPE_CONSERVATIVE)
            continue;  /* pinned through in the first pass */
        char *base = (char *)owner;
        if (type->flags & TYPE_ARRAY) {
            int64_t length = *(int64_t *)owner;
            int64_t first = from > (int64_t)type->size ? (from - (int64_t)type->size) / type->stride : 0;
            for (int64_t i = first; i < length && (int64_t)type->size + i * type->stride < to; i++)
                for (uint32_t k = 0; k < type->handles; k++)
                    evacuate((void **)(base + type->size + i * type->stride + type->offsets[k]));
        } else {
            for (uint32_t k = 0; k < type->handles; k++)
                if (type->offsets[k] >= from && type->offsets[k] < to)
                    evacuate((void **)(base + type->offsets[k]));
        }
    }
}

static void scan_work(void *item) {
    if ((uintptr_t)item & 1) {
        CardRun *run = &card_runs[(uintptr_t)item >> 1];
        evacuate_cards(run->owner, run->first, run->end);
    } else {
        evacuate_from(item);
    }
}

/* Scans until no thread has anything left: its own stack, then the shared list; idle, it waits for the others. */
static void copy_work(GcWorker *w) {
    for (;;) {
        while (w->count > 0)
            scan_work(w->stack[--w->count]);
        if (take_shared(w))
            continue;
        if (copy_threads <= 1)
            return;
        cas_add(&idle_workers, 1);
        for (;;) {
            if (shared_count > 0) {
                cas_add(&idle_workers, -1);
                if (take_shared(w))
                    break;
                cas_add(&idle_workers, 1);
                continue;
            }
            if (idle_workers >= copy_threads)
                return;
            GC_PAUSE();
        }
    }
}

static void gc_helper_loop(GcWorker *w) {
    gc_worker = w;
    int seen = 0;
    for (;;) {
        LOCK(gc_pool_lock);
        while (pool_generation == seen)
            WAIT(gc_pool_start, gc_pool_lock);
        seen = pool_generation;
        UNLOCK(gc_pool_lock);
        copy_work(w);
        LOCK(gc_pool_lock);
        if (--pool_pending == 0)
            WAKE_ALL(gc_pool_done);
        UNLOCK(gc_pool_lock);
    }
}

#ifdef _WIN32
static DWORD WINAPI gc_helper(void *argument) { gc_helper_loop((GcWorker *)argument); return 0; }
#else
static void *gc_helper(void *argument) { gc_helper_loop((GcWorker *)argument); return NULL; }
#endif

/* The copying threads, made at the first minor collection: half the processors (hyperthreads share a core's cache). */
static void start_gc_workers(void) {
    if (gc_worker_count)
        return;
    lock_init(&gc_share_lock);
    lock_init(&gc_alloc_lock);
    lock_init(&gc_pool_lock);
#ifdef _WIN32
    SYSTEM_INFO system;
    GetSystemInfo(&system);
    int processors = (int)system.dwNumberOfProcessors;
#else
    int processors = (int)sysconf(_SC_NPROCESSORS_ONLN);
#endif
    int wanted = gc_threads_setting > 0 ? gc_threads_setting : processors / 2;
    if (gc_threads_setting <= 0 && wanted > GC_WORKERS_DEFAULT)
        wanted = GC_WORKERS_DEFAULT;
    if (wanted < 1)
        wanted = 1;
    if (wanted > GC_WORKERS_MAX)
        wanted = GC_WORKERS_MAX;
    gc_worker_count = 1;
    for (int i = 1; i < wanted; i++) {
#ifdef _WIN32
        HANDLE thread = CreateThread(NULL, 0, gc_helper, &gc_workers[i], 0, NULL);
        if (!thread)
            break;
        CloseHandle(thread);
#else
        pthread_t thread;
        if (pthread_create(&thread, NULL, gc_helper, &gc_workers[i]) != 0)
            break;
        pthread_detach(thread);
#endif
        gc_worker_count++;
    }
}

/*
 * Copies what the stacks of the threads hold now (and what it reaches) with every copying thread; then the counts of
 * each go into the collector's. `parallel` 0: the collector alone (the few objects kept for their finalizers).
 */
static void copy_all(int parallel) {
    GcWorker *main = &gc_workers[0];
    gc_worker = main;
    copy_parallel = parallel;
    helpers_awake = 0;
    copy_threads = 1;
    copy_work(main);
    if (helpers_awake) {
        LOCK(gc_pool_lock);
        while (pool_pending > 0)
            WAIT(gc_pool_done, gc_pool_lock);
        UNLOCK(gc_pool_lock);
    }
    copy_threads = 1;
    for (int i = 0; i < gc_worker_count; i++) {
        GcWorker *w = &gc_workers[i];
        promoted += w->promoted;
        promoted_bytes += w->promoted_bytes;
        gc_live += w->live;
        slots_evacuated += w->evacuated;
        w->promoted = w->promoted_bytes = w->live = w->evacuated = 0;
    }
}

/* The slots the threads took and did not use stay theirs for the next collection (free, SLOT_FREE in the header):
   giving them back would walk them. */
static void return_worker_slots(void) {}

/* The conservative words of a large object's dirty cards (first pass: what they point at stays). */
static void pin_cards(void *owner) {
    GcHeader *header = header_of(owner);
    LargeInfo *info = large_info(header);
    if (!info->cards || !(header->type->flags & TYPE_CONSERVATIVE))
        return;
    size_t count = ((size_t)info->size + CARD - 1) / CARD;
    for (size_t card = 0; card < count; card++) {
        if (!info->cards[card])
            continue;
        int64_t from = (int64_t)card * CARD, length = info->size - from < CARD ? info->size - from : CARD;
        pin_words((const uint8_t *)owner + from, length);
    }
}

static void **run_after;
static size_t run_after_count, run_after_capacity;

/*
 * A minor collection. First every ambiguous root (the stack and registers, heap cells and the cells of globals, pins, and
 * the old conservative objects remembered or with dirty cards) pins the young objects it points into; then the remembered
 * old objects, the dirty cards and the pinned objects are scanned by their types, copying every young object they reach
 * out of the nursery (and what those reach). Dead young objects with a finalizer are kept the same way, their finalizers
 * run after. Blocks without pinned objects are zeroed and free; the others stay, with their pinned objects, as old.
 */
static void collect_minor(int walk_stack) {
    collecting++;
    close_current_block();
    promoted = promoted_bytes = 0;
    int64_t phase = stats_enabled ? now_ns() : 0;
    if (walk_stack)
        scan_stacks(pin_ambiguous);
    for (size_t i = 0; i < live.capacity; i++) {
        Entry *e = &live.entries[i];
        if (e->key > 1 && (e->kind == KIND_ROOT || e->kind == KIND_PAYLOAD || e->kind == KIND_HEAP))
            pin_words((const uint8_t *)e->key, e->value);
    }
    scan_pins(pin_ambiguous);
    for (size_t i = 0; i < remembered_count; i++) {
        GcHeader *header = header_of(remembered[i]);
        if (header->type->flags & TYPE_CONSERVATIVE)
            pin_words((const uint8_t *)remembered[i], payload_bytes(header));
    }
    for (size_t i = 0; i < carded_count; i++)
        pin_cards(carded[i]);
    if (stats_enabled) {
        int64_t at = now_ns();
        minor_roots_ns += at - phase;
        phase = at;
    }
    /* Pinned objects first (the queue holds them now), then everything the old generation points at: dealt among the
       collector's stack (the helpers take from it once they join). */
    start_gc_workers();
    GcWorker *first = &gc_workers[0];
    for (size_t i = 0; i < copy_count; i++)
        push_pointer(&first->stack, &first->count, &first->capacity, copy_queue[i]);
    copy_count = 0;
    for (size_t i = 0; i < remembered_count; i++) {
        GcHeader *header = header_of(remembered[i]);
        if (header->age != AGE_REMEMBERED)
            continue;
        header->age = AGE_OLD;
        remembered_scanned++;
        if (!(header->type->flags & TYPE_CONSERVATIVE))
            push_pointer(&first->stack, &first->count, &first->capacity, remembered[i]);
    }
    /* A large object's dirty cards in runs (an index into card_runs, tagged with bit 0): parts the threads share. */
    card_run_count = 0;
    for (size_t i = 0; i < carded_count; i++) {
        GcHeader *header = header_of(carded[i]);
        header->flags &= (uint8_t)~FLAG_CARDED;
        LargeInfo *info = large_info(header);
        if (!info->cards)
            continue;
        size_t cards = ((size_t)info->size + CARD - 1) / CARD;
        for (size_t at = 0; at < cards; at += CARDS_PER_RUN) {
            if (card_run_count == card_run_capacity) {
                card_run_capacity = card_run_capacity ? card_run_capacity * 2 : 256;
                card_runs = (CardRun *)realloc(card_runs, card_run_capacity * sizeof(CardRun));
                if (!card_runs)
                    fail_alloc();
            }
            card_runs[card_run_count].owner = carded[i];
            card_runs[card_run_count].first = at;
            card_runs[card_run_count].end = at + CARDS_PER_RUN;
            push_pointer(&first->stack, &first->count, &first->capacity, (void *)(card_run_count << 1 | 1));
            card_run_count++;
        }
    }
    if (stats_enabled)
        minor_cards_ns += now_ns() - phase;
    copy_all(1);
    for (;;) {
        /* Young objects with a finalizer that nothing reached: kept (with what they reach), their finalizers run after. */
        int kept = 0;
        size_t still = 0;
        for (size_t i = 0; i < young_finalizable_count; i++) {
            void *object = young_finalizable[i];
            GcHeader *header = header_of(object);
            if (header->flags & FLAG_FORWARDED)
                continue;  /* reached: old now (its finalizer entry moved with it) */
            if (header->flags & FLAG_PINNED) {
                young_finalizable[still++] = object;
                continue;
            }
            void *slot = object;
            gc_worker = &gc_workers[0];
            evacuate(&slot);
            push_pointer(&run_after, &run_after_count, &run_after_capacity, slot);
            kept = 1;
        }
        young_finalizable_count = still;
        if (!kept)
            break;
        copy_all(0);
    }
    return_worker_slots();
    if (stats_enabled) {
        int64_t at = now_ns();
        minor_copy_ns += at - phase;
        phase = at;
    }
    /* The blocks: zeroed and free, or kept with their pinned objects (old from now on). */
    int64_t young_bytes = 0;
    for (size_t i = 0; i < active_count; i++) {
        size_t index = active_blocks[i];
        NurseryBlock *block = &nursery_blocks[index];
        char *base = block_start(index);
        young_bytes += block->used;
        if (stats_enabled) {
            for (uint32_t at = 0; at < block->used;) {
                GcHeader *header = (GcHeader *)(base + at);
                allocations++;
                if (!(header->flags & (FLAG_FORWARDED | FLAG_PINNED)))
                    collected++, frees++;
                at += (uint32_t)total_bytes(payload_bytes(header));
            }
        }
        if (block->pinned) {
            block->state = BLOCK_RETAINED;
            retained_blocks++;
            for (uint32_t at = 0; at < block->used;) {
                GcHeader *header = (GcHeader *)(base + at);
                if (header->flags & FLAG_PINNED) {
                    header->flags &= (uint8_t)~FLAG_PINNED;
                    header->age = AGE_OLD;
                    gc_live++;
                }
                at += (uint32_t)total_bytes(payload_bytes(header));
            }
        } else {
            block->dirty = block->used;
            block->state = BLOCK_FREE;
            block->used = 0;
            free_blocks[free_block_count++] = index;
        }
        block->pinned = 0;
    }
    active_count = 0;
    forget_walks();
    if (stats_enabled)
        minor_blocks_ns += now_ns() - phase;
    remembered_count = 0;
    carded_count = 0;
    /* Most of the nursery survived: a larger one collects less often; almost nothing did for long: back toward the smallest
       (in the caches). */
    if (young_bytes > 0) {
        if (promoted_bytes * 2 > young_bytes) {
            nursery_quiet = 0;
            if (nursery_target < NURSERY_MAX)
                nursery_target *= 2;
        } else if (promoted_bytes * 64 < young_bytes) {
            if (++nursery_quiet >= NURSERY_QUIET_RUN && nursery_target > NURSERY_MIN) {
                nursery_target /= 2;
                nursery_quiet = 0;
            }
        } else {
            nursery_quiet = 0;
        }
    }
    collections++;
    minors++;
    since_collection = 0;
    bytes_since = 0;
    promoted_total += promoted;
    old_bytes += promoted_bytes;
    old_objects += promoted;
    if (old_bytes > major_bytes || old_objects > major_objects)
        major_due = 1;
    collecting--;
}

/* ── Major collection ───────────────────────────────────────────── */

/*
 * A major collection: first a minor one (the nursery is empty after it, but for retained blocks), then a mark-sweep of the
 * old generation: chunk slots, large objects, retained blocks (freed once nothing in them is alive) and heap cells.
 */
static void collect_major(int walk_stack) {
    /* Young objects are copied out (or pinned) first, whatever the mode now: marking knows only the old generation. */
    if (active_count > 0 || current_block != (size_t)-1)
        collect_minor(walk_stack);
    collecting++;
    close_current_block();
    load_stack_maps();
    index_large();
    if (walk_stack)
        scan_stacks(consider_ambiguous);
    /* Roots besides the stack: the cells of globals, in-flight exceptions, objects an open region owns, pins. */
    for (size_t i = 0; i < live.capacity; i++) {
        Entry *e = &live.entries[i];
        if (e->key <= 1)
            continue;
        if (e->kind == KIND_ROOT || e->kind == KIND_PAYLOAD)
            scan_ambiguous((const uint8_t *)e->key, e->value);
    }
    for (size_t i = 0; regions_used && i < region_of.capacity; i++)
        if (region_of.entries[i].key > 1)
            consider_ambiguous(region_of.entries[i].key);
    scan_pins(consider_ambiguous);
    drain_marks();
    /* Unreachable objects with a finalizer survive this collection (with what they reach): their finalizers run after
       the sweep, and a later collection frees them once they are unreachable again. */
    size_t pending = 0, pending_capacity = 0;
    void **to_finalize = NULL;
    for (size_t i = 0; finalizers_used && i < finalizers.capacity; i++) {
        Entry *e = &finalizers.entries[i];
        if (e->key <= 1)
            continue;
        GcHeader *header = header_of((void *)e->key);
        if (!header->mark && !(header->flags & FLAG_REGION)) {
            if (pending == pending_capacity) {
                pending_capacity = pending_capacity ? pending_capacity * 2 : 64;
                to_finalize = (void **)realloc(to_finalize, pending_capacity * sizeof(void *));
                if (!to_finalize)
                    fail_alloc();
            }
            to_finalize[pending++] = (void *)e->key;
        }
    }
    for (size_t i = 0; i < pending; i++)
        mark_object(header_of(to_finalize[i]));
    drain_marks();
    /* Sweep (objects in an open arena region belong to it, not to the collector). */
    int64_t survived = 0;
    for (size_t i = 0; i < live.capacity; i++) {
        Entry *e = &live.entries[i];
        if (e->key > 1 && e->kind == KIND_HEAP) {
            /* A cell nothing reaches: its owner is gone. */
            if (e->mark) {
                e->mark = 0;
                survived += e->value;
            } else {
                void *cell = (void *)e->key;
                if (stress || quarantine)
                    memset(cell, 0xDD, (size_t)e->value);
                table_remove(&live, (uintptr_t)cell);
                if (!quarantine)
                    larva_aligned_free(cell);
                frees++;
                collected_cells++;
            }
            continue;
        }
        if (e->key <= 1 || e->kind != KIND_GC)
            continue;
        GcHeader *header = header_of((void *)e->key);
        if (header->mark) {
            header->mark = 0;
            header->age = AGE_OLD;
            header->flags &= (uint8_t)~FLAG_CARDED;
            survived += e->value;
        } else if (!(header->flags & FLAG_REGION)) {
            if (stress || quarantine)
                memset((void *)e->key, 0xDD, (size_t)e->value);
            if (quarantine) {
                table_remove(&live, (uintptr_t)e->key);
                gc_live--;
                frees++;
            } else {
                gc_free((void *)e->key);
            }
            collected++;
        }
    }
    for (size_t c = 0; c < chunk_count; c++) {
        for (uint32_t s = 0; s < chunks[c]->bump; s++) {
            GcHeader *header = (GcHeader *)chunk_slot(chunks[c], s);
            if (header->domain == SLOT_FREE)
                continue;
            if (header->mark) {
                header->mark = 0;
                header->age = AGE_OLD;
                survived += chunks[c]->block;
            } else if (!(header->flags & FLAG_REGION)) {
                if (stress || quarantine)
                    memset((char *)header + GC_HEADER, 0xDD, (size_t)chunks[c]->block - GC_HEADER);
                gc_free((char *)header + GC_HEADER);
                collected++;
            }
        }
    }
    /* Retained nursery blocks: kept while an object in them lives (its unmarked neighbours stay as they are). */
    for (size_t index = 0; index < nursery_used_blocks; index++) {
        NurseryBlock *block = &nursery_blocks[index];
        if (block->state != BLOCK_RETAINED)
            continue;
        char *base = block_start(index);
        int alive = 0;
        for (uint32_t at = 0; at < block->used;) {
            GcHeader *header = (GcHeader *)(base + at);
            if (header->mark) {
                header->mark = 0;
                alive = 1;
                survived += payload_bytes(header);
            } else if (header->age == AGE_OLD && !(header->flags & FLAG_FORWARDED)) {
                /* A pinned object that died. */
                header->age = AGE_YOUNG;
                gc_live--;
                collected++;
                frees++;
            }
            at += (uint32_t)total_bytes(payload_bytes(header));
        }
        if (!alive) {
            block->dirty = block->used;
            block->state = BLOCK_FREE;
            block->used = 0;
            free_blocks[free_block_count++] = index;
            retained_blocks--;
        }
    }
    forget_walks();
    collections++;
    majors++;
    since_collection = 0;
    bytes_since = 0;
    live_bytes = survived;
    /* Every survivor is old; the cards and remembered objects start over. */
    for (size_t i = 0; i < live.capacity; i++) {
        Entry *e = &live.entries[i];
        if (e->key > 1 && e->kind == KIND_GC) {
            GcHeader *header = header_of((void *)e->key);
            if ((header->flags & FLAG_LARGE) && large_info(header)->cards)
                memset(large_info(header)->cards, 0, ((size_t)large_info(header)->size + CARD - 1) / CARD);
        }
    }
    /* Remembered and carded objects go back to plain old ones: the next store into them passes the barrier again (a major
       collection without a minor one before it, when the nursery was empty, would otherwise leave them unseen). */
    for (size_t i = 0; i < remembered_count; i++) {
        GcHeader *header = header_of(remembered[i]);
        if (header->age == AGE_REMEMBERED)
            header->age = AGE_OLD;
    }
    for (size_t i = 0; i < carded_count; i++)
        header_of(carded[i])->flags &= (uint8_t)~FLAG_CARDED;
    remembered_count = 0;
    carded_count = 0;
    old_bytes = survived;
    old_objects = gc_live;
    /* The next major collection waits until the old generation doubles: its work per allocation stays bounded. */
    major_bytes = survived * 2 > ((int64_t)64 << 20) ? survived * 2 : ((int64_t)64 << 20);
    major_objects = gc_live * 2 > (1 << 20) ? gc_live * 2 : (1 << 20);
    major_due = 0;
    /* Without generations: the next collection waits for as many allocations as there are live objects. */
    if (!fixed_threshold)
        threshold = gc_live * 2 > 4096 ? gc_live * 2 : 4096;
    collecting--;
    custom_asked = -1;
    if (collecting == 0) {
        for (size_t i = 0; i < custom_pending_count; i++)
            release_custom(custom_pending[i]);
        custom_pending_count = 0;
    }
    for (size_t i = 0; i < pending; i++)
        if (is_gc(to_finalize[i]))
            finalize(to_finalize[i]);
    free(to_finalize);
}

/* Whether minor collections may run: not while the program's own collector, arena regions, a fixed threshold or quarantine
   change how objects live and die. */
#ifdef LARVA_DIAGNOSTICS
static int generations_off;  /* LARVA_GC_GENERATIONS=0: every collection is major (to compare) */
#else
enum { generations_off = 0 };
#endif

static int generational(void) {
    return !generations_off && !fixed_threshold && !quarantine && !custom_poll && !custom_allocate && !regions_used && !in_custom;
}

/* LARVA_GC_VERIFY: after every collection, no old object may point at a young or a dead object. */
#ifdef LARVA_DIAGNOSTICS
static int verify;
#else
enum { verify = 0 };
#endif
static void *verify_owner;
static int verify_major;

static void verify_slot(void **slot) {
    void *target = *slot;
    if (!target || !in_nursery(target))
        return;
    size_t index = (size_t)((char *)target - nursery_low) / NURSERY_BLOCK;
    GcHeader *header = header_of(target);
    if (nursery_blocks[index].state == BLOCK_RETAINED && header->age != AGE_YOUNG && !(header->flags & FLAG_FORWARDED))
        return;
    fprintf(stderr, "larva verify (%s): object %p (type size %u flags %u) slot +%lld -> %p in block %zu state %d (age %d flags %d)\n",
        verify_major ? "major" : "minor", verify_owner, header_of(verify_owner)->type->size, header_of(verify_owner)->type->flags,
        (long long)((char *)slot - (char *)verify_owner), target, index, nursery_blocks[index].state, header->age, header->flags);
    {
        GcHeader *owner = header_of(verify_owner);
        fprintf(stderr, "  owner: age %d flags %d in nursery %d, handles at", owner->age, owner->flags, in_nursery(verify_owner));
        for (uint16_t h = 0; h < owner->type->handles; h++)
            fprintf(stderr, " +%u", owner->type->offsets[h]);
        fprintf(stderr, "; collections %lld (minor %lld), threads alive %lld\n", (long long)collections, (long long)minors, (long long)threads_alive);
    }
    abort();
}

static void verify_object(void *object) {
    verify_owner = object;
    each_slot(object, verify_slot, NULL, NULL);
}

static void verify_heap(int major) {
    verify_major = major;
    for (size_t c = 0; c < chunk_count; c++)
        for (uint32_t s = 0; s < chunks[c]->bump; s++) {
            GcHeader *header = (GcHeader *)chunk_slot(chunks[c], s);
            if (header->domain != SLOT_FREE && header->type)
                verify_object((char *)header + GC_HEADER);
        }
    for (size_t i = 0; i < live.capacity; i++) {
        Entry *e = &live.entries[i];
        if (e->key > 1 && e->kind == KIND_GC)
            verify_object((void *)e->key);
    }
    for (size_t index = 0; index < nursery_used_blocks; index++) {
        NurseryBlock *block = &nursery_blocks[index];
        if (block->state != BLOCK_RETAINED)
            continue;
        for (uint32_t at = 0; at < block->used;) {
            GcHeader *header = (GcHeader *)(block_start(index) + at);
            if (header->age != AGE_YOUNG && !(header->flags & FLAG_FORWARDED))
                verify_object((char *)header + GC_HEADER);
            at += (uint32_t)total_bytes(payload_bytes(header));
        }
    }
}

/* A collection: minor while generations apply and the old generation has room, else major. */
static void collect(int walk_stack) {
    int64_t start = now_ns();
    if (walk_stack && generational() && !nursery_disabled && !major_due && !collect_requested) {
        collect_minor(1);
        minor_ns += now_ns() - start;
        if (verify)
            verify_heap(0);
    } else {
        collect_major(walk_stack);
        major_ns += now_ns() - start;
        if (verify)
            verify_heap(1);
    }
    update_fast();
}

/* The finalizers of what the last collection found unreachable (outside the collection, other threads running). */
static void run_finalizers(void) {
    while (run_after_count > 0) {
        void *object = run_after[--run_after_count];
        finalize(object);
    }
}

static void threaded_poll(void);
static int collection_due(void);
static void run_finalizers(void);

/* Safepoint: collects when enough was allocated since the last collection (always under stress). */
LARVA_API void larva_poll(void) {
    if (threading) {
        threaded_poll();
        return;
    }
    poll_calls++;
    if (finalizing != 0 || in_custom)
        return;
    if (collection_due()) {
        collect(1);
        collect_requested = 0;
        run_finalizers();
    }
    update_poll_pending();
}

/* Whether this safepoint collects (asking the program's collector if it has one). */
static int collection_due(void) {
    int due;
    if (stress || collect_requested) {
        due = 1;
        /* Under stress, one collection in eight is major (the others test the generations). */
        static int64_t stressed;
        if (stress && !collect_requested && ++stressed % 8 == 0)
            major_due = 1;
    } else if (custom_poll) {
        /* The program's collector decides, asked again only once something was allocated since its last answer. */
        due = 0;
        if (since_collection != custom_asked) {
            in_custom++;
            due = custom_poll(since_collection, gc_live) != 0;
            in_custom--;
            custom_asked = due ? -1 : since_collection;
        }
    } else {
        due = allocation_due();
    }
    return due;
}

/* ── The collector (System.GC) ─────────────────────────────────────── */

LARVA_API void larva_gc_request(void) {
    collect_requested = 1;
    larva_poll_pending = 1;
}

LARVA_API int64_t larva_gc_collections(void) { return collections; }

/* Objects alive or not collected yet: the old ones, and those in the nursery (walked to count them). */
LARVA_API int64_t larva_gc_objects(void) {
    int64_t young = 0;
    if (current_block != (size_t)-1 && larva_heap.cursor)
        nursery_blocks[current_block].used = (uint32_t)(larva_heap.cursor - block_start(current_block));
    for (size_t i = 0; i < active_count; i++) {
        NurseryBlock *block = &nursery_blocks[active_blocks[i]];
        char *base = block_start(active_blocks[i]);
        for (uint32_t at = 0; at < block->used; young++)
            at += (uint32_t)total_bytes(payload_bytes((GcHeader *)(base + at)));
    }
    return gc_live + young;
}

/* System.GC.Install: the program's own collector (null functions put the runtime's back; objects keep their memory's
   owner, so earlier objects still go back where they came from). */
LARVA_API void larva_gc_custom(void *allocate, void *release, void *poll) {
    custom_allocate = (void *(*)(uint64_t))allocate;
    custom_release = (void (*)(void *))release;
    custom_poll = (int32_t (*)(int64_t, int64_t))poll;
    custom_asked = -1;
    update_fast();
    update_poll_pending();
}

#pragma optimize("s", on) /* cold: compiled for size (see COLD) */
/* ── Interop (System.Runtime.InteropServices) ──────────────────────── */

/* Native memory for C (Marshal.AllocHGlobal): zeroed, not managed by the collector. */
LARVA_API void *larva_mem_alloc(int64_t size) {
    void *block = calloc(1, size > 0 ? (size_t)size : 1);
    return block ? block : fail_alloc();
}

LARVA_API void larva_mem_free(void *block) { free(block); }

/* `count` bytes from `source` to `target` (the ranges may overlap). */
LARVA_API void larva_mem_copy(void *target, const void *source, int64_t count) {
    if (count > 0)
        memmove(target, source, (size_t)count);
}

/* The first `count` bytes of `left` and `right` compared as unsigned values: < 0, 0 or > 0. */
LARVA_API int64_t larva_mem_cmp(const void *left, const void *right, int64_t count) {
    return count > 0 ? (int64_t)memcmp(left, right, (size_t)count) : 0;
}

#ifndef _WIN32
#include <dlfcn.h>
#endif

/* The address of C function `name` in library `library` (both as a str: length, then UTF-16 units); stops the
   program when either is missing. */
LARVA_API int64_t larva_native_symbol(const int64_t *library, const int64_t *name) {
    char entry[512];
    const uint16_t *units = (const uint16_t *)(name + 1);
    int64_t length = name[0] < 511 ? name[0] : 511;
    for (int64_t i = 0; i < length; i++)
        entry[i] = (char)units[i];
    entry[length] = 0;
#ifdef _WIN32
    wchar_t file[1024];
    int64_t size = library[0] < 1023 ? library[0] : 1023;
    memcpy(file, library + 1, (size_t)size * 2);
    file[size] = 0;
    HMODULE module = GetModuleHandleW(file);
    if (!module)
        module = LoadLibraryW(file);
    if (!module) {
        fprintf(stderr, "larva: cannot load `%ls` (for `%s`): put it next to the program or on the PATH (a project copies it there with `lib: path/%ls` in its DEPS chunk)\n", file, entry, file);
        larva_exit(102);
    }
    void *address = (void *)GetProcAddress(module, entry);
    if (!address) {
        fprintf(stderr, "larva: `%s` not found in `%ls` (another version of the library?)\n", entry, file);
        larva_exit(102);
    }
#else
    char file[1024];
    int64_t size = library[0] < 1023 ? library[0] : 1023;
    const uint16_t *fileUnits = (const uint16_t *)(library + 1);
    for (int64_t i = 0; i < size; i++)
        file[i] = (char)fileUnits[i];
    file[size] = 0;
    void *module = dlopen(file, RTLD_NOW);
    if (!module) {
        fprintf(stderr, "larva: cannot load `%s` (for `%s`): %s (a project copies it next to the program with `lib: path/%s` in its DEPS chunk)\n", file, entry, dlerror(), file);
        larva_exit(102);
    }
    void *address = dlsym(module, entry);
    if (!address) {
        fprintf(stderr, "larva: `%s` not found in `%s` (another version of the library?)\n", entry, file);
        larva_exit(102);
    }
#endif
    return (int64_t)(intptr_t)address;
}

#pragma optimize("", on)
/* ── Output (the print intrinsics) ─────────────────────────────────── */

LARVA_API void larva_print_i64(int64_t value) { printf("%lld", (long long)value); }

LARVA_API void larva_print_u64(uint64_t value) { printf("%llu", (unsigned long long)value); }

/* The bits of an f64; up to 15 significant digits, as the VM prints. */
LARVA_API void larva_print_f64(int64_t bits) {
    double value;
    memcpy(&value, &bits, sizeof value);
    if (value != value)
        fputs("nan", stdout);
    else if (value > 1.7976931348623157e308)
        fputs("inf", stdout);
    else if (value < -1.7976931348623157e308)
        fputs("-inf", stdout);
    else
        printf("%.15g", value);
}

LARVA_API void larva_print_bool(int32_t value) { fputs(value ? "true" : "false", stdout); }

/* A str: its length (8 bytes), then UTF-16 code units, written as UTF-8. */
LARVA_API void larva_print_str(const int64_t *text) {
    const uint16_t *units = (const uint16_t *)(text + 1);
    for (int64_t i = 0; i < text[0]; i++) {
        uint32_t c = units[i];
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < text[0] && units[i + 1] >= 0xDC00 && units[i + 1] < 0xE000)
            c = 0x10000 + ((c - 0xD800) << 10) + (units[++i] - 0xDC00);
        if (c < 0x80) {
            putchar((int)c);
        } else if (c < 0x800) {
            putchar((int)(0xC0 | c >> 6));
            putchar((int)(0x80 | (c & 0x3F)));
        } else if (c < 0x10000) {
            putchar((int)(0xE0 | c >> 12));
            putchar((int)(0x80 | (c >> 6 & 0x3F)));
            putchar((int)(0x80 | (c & 0x3F)));
        } else {
            putchar((int)(0xF0 | c >> 18));
            putchar((int)(0x80 | (c >> 12 & 0x3F)));
            putchar((int)(0x80 | (c >> 6 & 0x3F)));
            putchar((int)(0x80 | (c & 0x3F)));
        }
    }
}

LARVA_API void larva_print_line(void) { putchar('\n'); }

/* Text in UTF-8: its length (8 bytes), then the bytes, written as they are. */
LARVA_API void larva_print_utf8(const int64_t *text) { fwrite(text + 1, 1, (size_t)text[0], stdout); }

#pragma optimize("s", on) /* cold: compiled for size (see COLD) */
/* ── Input and the command line ─────────────────────────────────── */

/* Text reaches the program as UTF-16 code units, read one at a time: the runtime never allocates the program's strings. */
typedef struct { uint16_t *units; int64_t length, capacity; } Units;

static void units_add(Units *text, uint16_t unit) {
    if (text->length == text->capacity) {
        text->capacity = text->capacity ? text->capacity * 2 : 64;
        text->units = (uint16_t *)realloc(text->units, (size_t)text->capacity * sizeof(uint16_t));
        if (!text->units) {
            fputs("larva: out of memory\n", stderr);
            larva_exit(1);
        }
    }
    text->units[text->length++] = unit;
}

/* Appends UTF-8 bytes as UTF-16 (a bad byte becomes U+FFFD). */
static void units_utf8(Units *text, const unsigned char *bytes, size_t count) {
    for (size_t i = 0; i < count;) {
        uint32_t c = bytes[i];
        int extra = c < 0x80 ? 0 : (c & 0xE0) == 0xC0 ? 1 : (c & 0xF0) == 0xE0 ? 2 : (c & 0xF8) == 0xF0 ? 3 : -1;
        if (extra < 0 || (extra > 0 && i + (size_t)extra >= count)) {
            units_add(text, 0xFFFD);
            i++;
            continue;
        }
        c = extra == 0 ? c : c & (0x3F >> extra);
        for (int k = 1; k <= extra; k++)
            c = c << 6 | (bytes[i + k] & 0x3F);
        i += (size_t)extra + 1;
        if (c >= 0x10000) {
            c -= 0x10000;
            units_add(text, (uint16_t)(0xD800 | c >> 10));
            units_add(text, (uint16_t)(0xDC00 | (c & 0x3FF)));
        } else
            units_add(text, (uint16_t)c);
    }
}

static THREAD_LOCAL Units input_line;
static Units *arguments_text;
static int64_t argument_count;

/* Reads a line (without its line break) into the runtime's buffer: its length in code units, or -1 at the end of input. */
LARVA_API int64_t larva_read_line(void) {
    fflush(stdout);
    input_line.length = 0;
#ifdef _WIN32
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    DWORD mode;
    if (GetConsoleMode(in, &mode)) {
        /* A console gives UTF-16 itself. */
        int any = 0;
        for (;;) {
            wchar_t unit;
            DWORD read = 0;
            if (!ReadConsoleW(in, &unit, 1, &read, NULL) || read == 0)
                return any ? input_line.length : -1;
            any = 1;
            if (unit == L'\n')
                break;
            if (unit == 0x1A && input_line.length == 0)
                return -1; /* Ctrl+Z */
            units_add(&input_line, (uint16_t)unit);
        }
        if (input_line.length > 0 && input_line.units[input_line.length - 1] == '\r')
            input_line.length--;
        return input_line.length;
    }
#endif
    static unsigned char *bytes;
    static size_t capacity;
    size_t count = 0;
    int c, any = 0;
    while ((c = getchar()) != EOF) {
        any = 1;
        if (c == '\n')
            break;
        if (count == capacity) {
            capacity = capacity ? capacity * 2 : 128;
            bytes = (unsigned char *)realloc(bytes, capacity);
            if (!bytes) {
                fputs("larva: out of memory\n", stderr);
                larva_exit(1);
            }
        }
        bytes[count++] = (unsigned char)c;
    }
    if (!any)
        return -1;
    if (count > 0 && bytes[count - 1] == '\r')
        count--;
    units_utf8(&input_line, bytes, count);
    return input_line.length;
}

LARVA_API int64_t larva_read_unit(int64_t index) {
    return index >= 0 && index < input_line.length ? input_line.units[index] : 0;
}

/* A float as text (what larva_print_f64 prints), read back a unit at a time. */
static THREAD_LOCAL char formatted[64];
static THREAD_LOCAL int64_t formatted_length;

LARVA_API int64_t larva_format_f64(int64_t bits) {
    double value;
    memcpy(&value, &bits, sizeof value);
    if (value != value)
        strcpy(formatted, "nan");
    else if (value > 1.7976931348623157e308)
        strcpy(formatted, "inf");
    else if (value < -1.7976931348623157e308)
        strcpy(formatted, "-inf");
    else
        snprintf(formatted, sizeof formatted, "%.15g", value);
    formatted_length = (int64_t)strlen(formatted);
    return formatted_length;
}

LARVA_API int64_t larva_format_unit(int64_t index) {
    return index >= 0 && index < formatted_length ? (unsigned char)formatted[index] : 0;
}

LARVA_API int64_t larva_args_count(void) { return argument_count; }

LARVA_API int64_t larva_args_length(int64_t index) {
    return index >= 0 && index < argument_count ? arguments_text[index].length : 0;
}

LARVA_API int64_t larva_args_unit(int64_t index, int64_t at) {
    return index >= 0 && index < argument_count && at >= 0 && at < arguments_text[index].length ? arguments_text[index].units[at] : 0;
}

/* ── System.IO: files, folders, the environment, processes ──────── */

#ifndef _WIN32
#include <sys/stat.h>
#include <sys/wait.h>
#include <dirent.h>
#include <errno.h>
#include <unistd.h>
#endif

static THREAD_LOCAL unsigned char *file_buffer;
static THREAD_LOCAL int64_t file_size;
static THREAD_LOCAL Units *folder_entries;
static THREAD_LOCAL int64_t folder_count;
static THREAD_LOCAL Units environment_value;
static THREAD_LOCAL int environment_set;

#ifdef _WIN32
/* A str (its length, then UTF-16 units) as a NUL-terminated wide string: Windows paths are UTF-16. */
static wchar_t *io_text(const int64_t *text) {
    wchar_t *wide = (wchar_t *)malloc(((size_t)text[0] + 1) * sizeof(wchar_t));
    if (!wide)
        return NULL;
    memcpy(wide, text + 1, (size_t)text[0] * sizeof(wchar_t));
    wide[text[0]] = 0;
    return wide;
}
#else
/* A str as NUL-terminated UTF-8 (what POSIX paths and the environment use). */
static char *io_text(const int64_t *text) {
    const uint16_t *units = (const uint16_t *)(text + 1);
    char *bytes = (char *)malloc((size_t)text[0] * 3 + 1), *out = bytes;
    if (!bytes)
        return NULL;
    for (int64_t i = 0; i < text[0]; i++) {
        uint32_t c = units[i];
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < text[0] && units[i + 1] >= 0xDC00 && units[i + 1] < 0xE000)
            c = 0x10000 + ((c - 0xD800) << 10) + (units[++i] - 0xDC00);
        if (c < 0x80) {
            *out++ = (char)c;
        } else if (c < 0x800) {
            *out++ = (char)(0xC0 | c >> 6);
            *out++ = (char)(0x80 | (c & 0x3F));
        } else if (c < 0x10000) {
            *out++ = (char)(0xE0 | c >> 12);
            *out++ = (char)(0x80 | (c >> 6 & 0x3F));
            *out++ = (char)(0x80 | (c & 0x3F));
        } else {
            *out++ = (char)(0xF0 | c >> 18);
            *out++ = (char)(0x80 | (c >> 12 & 0x3F));
            *out++ = (char)(0x80 | (c >> 6 & 0x3F));
            *out++ = (char)(0x80 | (c & 0x3F));
        }
    }
    *out = 0;
    return bytes;
}
#endif

/* Reads the whole file into the runtime's buffer: its size, or -1. */
LARVA_API int64_t larva_file_read(const int64_t *path) {
    file_size = 0;
#ifdef _WIN32
    wchar_t *name = io_text(path);
    FILE *file = name ? _wfopen(name, L"rb") : NULL;
#else
    char *name = io_text(path);
    FILE *file = name ? fopen(name, "rb") : NULL;
#endif
    free(name);
    if (!file)
        return -1;
    /* The size first, then one read into a buffer that large (it doubles if the file grows meanwhile). */
    size_t capacity = 65536;
#ifdef _WIN32
    if (_fseeki64(file, 0, SEEK_END) == 0) {
        int64_t end = _ftelli64(file);
        if (end > 0)
            capacity = (size_t)end + 1;
        _fseeki64(file, 0, SEEK_SET);
    }
#else
    if (fseeko(file, 0, SEEK_END) == 0) {
        off_t end = ftello(file);
        if (end > 0)
            capacity = (size_t)end + 1;
        fseeko(file, 0, SEEK_SET);
    }
#endif
    unsigned char *buffer = (unsigned char *)malloc(capacity);
    if (!buffer) {
        fclose(file);
        return -1;
    }
    size_t size = 0, read;
    while ((read = fread(buffer + size, 1, capacity - size, file)) > 0) {
        size += read;
        if (size == capacity) {
            unsigned char *grown = (unsigned char *)realloc(buffer, capacity * 2);
            if (!grown) {
                free(buffer);
                fclose(file);
                return -1;
            }
            buffer = grown;
            capacity *= 2;
        }
    }
    fclose(file);
    free(file_buffer);
    file_buffer = buffer;
    file_size = (int64_t)size;
    return file_size;
}

/* Copies the file read last into a u8[] (its length, then the bytes). */
LARVA_API void larva_file_take(int64_t *bytes) {
    int64_t count = bytes[0] < file_size ? bytes[0] : file_size;
    if (count > 0)
        memcpy(bytes + 1, file_buffer, (size_t)count);
}

/* Writes a u8[] as the file's content (or at its end): 0, or -1. */
LARVA_API int64_t larva_file_write(const int64_t *path, const int64_t *bytes, int64_t append) {
#ifdef _WIN32
    wchar_t *name = io_text(path);
    FILE *file = name ? _wfopen(name, append ? L"ab" : L"wb") : NULL;
#else
    char *name = io_text(path);
    FILE *file = name ? fopen(name, append ? "ab" : "wb") : NULL;
#endif
    free(name);
    if (!file)
        return -1;
    size_t written = bytes[0] > 0 ? fwrite(bytes + 1, 1, (size_t)bytes[0], file) : 0;
    int closed = fclose(file);
    return written == (size_t)bytes[0] && closed == 0 ? 0 : -1;
}

/* 0: nothing there, 1: a file, 2: a folder. */
LARVA_API int64_t larva_file_kind(const int64_t *path) {
#ifdef _WIN32
    wchar_t *name = io_text(path);
    DWORD attributes = name ? GetFileAttributesW(name) : INVALID_FILE_ATTRIBUTES;
    free(name);
    if (attributes == INVALID_FILE_ATTRIBUTES)
        return 0;
    return attributes & FILE_ATTRIBUTE_DIRECTORY ? 2 : 1;
#else
    char *name = io_text(path);
    struct stat info;
    int found = name && stat(name, &info) == 0;
    free(name);
    return !found ? 0 : S_ISDIR(info.st_mode) ? 2 : 1;
#endif
}

/* Deletes a file, or an empty folder: 0, or -1. */
LARVA_API int64_t larva_file_delete(const int64_t *path) {
#ifdef _WIN32
    wchar_t *name = io_text(path);
    if (!name)
        return -1;
    DWORD attributes = GetFileAttributesW(name);
    BOOL done = attributes != INVALID_FILE_ATTRIBUTES
        && (attributes & FILE_ATTRIBUTE_DIRECTORY ? RemoveDirectoryW(name) : DeleteFileW(name));
    free(name);
    return done ? 0 : -1;
#else
    char *name = io_text(path);
    int failed = !name || remove(name) != 0;
    free(name);
    return failed ? -1 : 0;
#endif
}

/* When it was last written: milliseconds since 1970-01-01 UTC, or -1. */
LARVA_API int64_t larva_file_time(const int64_t *path) {
#ifdef _WIN32
    wchar_t *name = io_text(path);
    WIN32_FILE_ATTRIBUTE_DATA data;
    BOOL found = name && GetFileAttributesExW(name, GetFileExInfoStandard, &data);
    free(name);
    if (!found)
        return -1;
    int64_t ticks = (int64_t)(((uint64_t)data.ftLastWriteTime.dwHighDateTime << 32) | data.ftLastWriteTime.dwLowDateTime);
    return (ticks - 116444736000000000LL) / 10000;
#else
    char *name = io_text(path);
    struct stat info;
    int found = name && stat(name, &info) == 0;
    free(name);
    return found ? (int64_t)info.st_mtime * 1000 : -1;
#endif
}

/* Makes a folder and the missing folders above it: 0 when it exists after, -1 otherwise. */
LARVA_API int64_t larva_dir_create(const int64_t *path) {
#ifdef _WIN32
    wchar_t *name = io_text(path);
    if (!name)
        return -1;
    for (wchar_t *at = name + 1; *at; at++) {
        if ((*at == L'\\' || *at == L'/') && at[-1] != L':') {
            wchar_t kept = *at;
            *at = 0;
            CreateDirectoryW(name, NULL);
            *at = kept;
        }
    }
    CreateDirectoryW(name, NULL);
    DWORD attributes = GetFileAttributesW(name);
    free(name);
    return attributes != INVALID_FILE_ATTRIBUTES && attributes & FILE_ATTRIBUTE_DIRECTORY ? 0 : -1;
#else
    char *name = io_text(path);
    if (!name)
        return -1;
    for (char *at = name + 1; *at; at++) {
        if (*at == '/') {
            *at = 0;
            mkdir(name, 0777);
            *at = '/';
        }
    }
    mkdir(name, 0777);
    struct stat info;
    int made = stat(name, &info) == 0 && S_ISDIR(info.st_mode);
    free(name);
    return made ? 0 : -1;
#endif
}

static int compare_entries(const void *a, const void *b) {
    const Units *x = (const Units *)a, *y = (const Units *)b;
    int64_t shorter = x->length < y->length ? x->length : y->length;
    for (int64_t i = 0; i < shorter; i++) {
        if (x->units[i] != y->units[i])
            return x->units[i] < y->units[i] ? -1 : 1;
    }
    return x->length < y->length ? -1 : x->length > y->length ? 1 : 0;
}

static Units *next_entry(void) {
    static int64_t capacity;
    if (folder_count == capacity) {
        capacity = capacity ? capacity * 2 : 32;
        folder_entries = (Units *)realloc(folder_entries, (size_t)capacity * sizeof(Units));
        if (!folder_entries) {
            fputs("larva: out of memory\n", stderr);
            larva_exit(1);
        }
    }
    Units *entry = &folder_entries[folder_count++];
    entry->units = NULL;
    entry->length = entry->capacity = 0;
    return entry;
}

/* Lists a folder's entries (names, sorted) into the runtime: how many, or -1. */
LARVA_API int64_t larva_dir_list(const int64_t *path) {
    for (int64_t i = 0; i < folder_count; i++)
        free(folder_entries[i].units);
    folder_count = 0;
#ifdef _WIN32
    wchar_t *name = io_text(path);
    if (!name)
        return -1;
    size_t length = wcslen(name);
    wchar_t *pattern = (wchar_t *)malloc((length + 3) * sizeof(wchar_t));
    memcpy(pattern, name, length * sizeof(wchar_t));
    wcscpy(pattern + length, length && (name[length - 1] == L'\\' || name[length - 1] == L'/') ? L"*" : L"\\*");
    free(name);
    WIN32_FIND_DATAW found;
    HANDLE search = FindFirstFileW(pattern, &found);
    free(pattern);
    if (search == INVALID_HANDLE_VALUE)
        return -1;
    do {
        if (wcscmp(found.cFileName, L".") == 0 || wcscmp(found.cFileName, L"..") == 0)
            continue;
        Units *entry = next_entry();
        for (wchar_t *c = found.cFileName; *c; c++)
            units_add(entry, (uint16_t)*c);
    } while (FindNextFileW(search, &found));
    FindClose(search);
#else
    char *name = io_text(path);
    DIR *folder = name ? opendir(name) : NULL;
    free(name);
    if (!folder)
        return -1;
    struct dirent *found;
    while ((found = readdir(folder)) != NULL) {
        if (strcmp(found->d_name, ".") == 0 || strcmp(found->d_name, "..") == 0)
            continue;
        units_utf8(next_entry(), (const unsigned char *)found->d_name, strlen(found->d_name));
    }
    closedir(folder);
#endif
    qsort(folder_entries, (size_t)folder_count, sizeof(Units), compare_entries);
    return folder_count;
}

LARVA_API int64_t larva_dir_length(int64_t index) {
    return index >= 0 && index < folder_count ? folder_entries[index].length : 0;
}

LARVA_API int64_t larva_dir_unit(int64_t index, int64_t at) {
    return index >= 0 && index < folder_count && at >= 0 && at < folder_entries[index].length ? folder_entries[index].units[at] : 0;
}

/* An environment variable into the runtime: its length in code units, or -1 when it is not set. */
LARVA_API int64_t larva_env_get(const int64_t *variable) {
    environment_value.length = 0;
    environment_set = 0;
#ifdef _WIN32
    wchar_t *name = io_text(variable);
    DWORD size = name ? GetEnvironmentVariableW(name, NULL, 0) : 0;
    if (size > 0) {
        wchar_t *value = (wchar_t *)malloc(size * sizeof(wchar_t));
        DWORD length = value ? GetEnvironmentVariableW(name, value, size) : 0;
        for (DWORD i = 0; i < length; i++)
            units_add(&environment_value, (uint16_t)value[i]);
        free(value);
        environment_set = 1;
    }
    free(name);
#else
    char *name = io_text(variable);
    const char *value = name ? getenv(name) : NULL;
    if (value) {
        units_utf8(&environment_value, (const unsigned char *)value, strlen(value));
        environment_set = 1;
    }
    free(name);
#endif
    return environment_set ? environment_value.length : -1;
}

LARVA_API int64_t larva_env_unit(int64_t index) {
    return index >= 0 && index < environment_value.length ? environment_value.units[index] : 0;
}

/* Text (ASCII digits, sign, point, exponent: the library checks it) to a float, correctly rounded: its bits. */
LARVA_API int64_t larva_parse_f64(const int64_t *text) {
    char digits[512];
    int64_t length = text[0] < 511 ? text[0] : 511;
    const uint16_t *units = (const uint16_t *)(text + 1);
    for (int64_t i = 0; i < length; i++)
        digits[i] = units[i] < 128 ? (char)units[i] : '?';
    digits[length] = 0;
    char *end;
    double value = strtod(digits, &end);
    if (end == digits || *end != 0) {
        uint64_t nan = 0x7FF8000000000000ULL;
        memcpy(&value, &nan, sizeof value);
    }
    int64_t bits;
    memcpy(&bits, &value, sizeof bits);
    return bits;
}

#pragma optimize("", on)
/* ── Streams (System.IO) ───────────────────────────────────────────── */

/* Open files by handle: 0, 1 and 2 are standard input, output and error. A slot is NULL once closed. */
static FILE **stream_table;
static int64_t stream_count, stream_capacity;

/* ── TCP (System.Net) ───────────────────────────────────────────────
 * Sockets share the stream handles: a socket handle has SOCKET_TAG set and indexes `sock_table`; stream.read/write/close
 * see the tag and use the socket calls. */
#define SOCKET_TAG ((int64_t)1 << 40)
#ifdef _WIN32
typedef SOCKET rt_socket;
#define RT_BAD_SOCKET INVALID_SOCKET
#define rt_closesocket closesocket
#else
#include <sys/socket.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <arpa/inet.h>
#include <netdb.h>
#include <unistd.h>
#include <sys/wait.h>
typedef int rt_socket;
#define RT_BAD_SOCKET (-1)
#define rt_closesocket close
#endif
static rt_socket *sock_table;
static int64_t sock_count, sock_capacity;
static int net_ready;

static void net_init(void) {
#ifdef _WIN32
    if (!net_ready) {
        WSADATA data;
        WSAStartup(MAKEWORD(2, 2), &data);
        net_ready = 1;
    }
#else
    net_ready = 1;
#endif
}

static int64_t sock_add(rt_socket s) {
    if (sock_count >= sock_capacity) {
        sock_capacity = sock_capacity ? sock_capacity * 2 : 16;
        rt_socket *grown = (rt_socket *)realloc(sock_table, (size_t)sock_capacity * sizeof(rt_socket));
        if (!grown) {
            rt_closesocket(s);
            return -1;
        }
        sock_table = grown;
    }
    sock_table[sock_count] = s;
    return SOCKET_TAG | sock_count++;
}

static rt_socket sock_of(int64_t handle) {
    if (!(handle & SOCKET_TAG))
        return RT_BAD_SOCKET;
    int64_t i = handle & ~SOCKET_TAG;
    return i >= 0 && i < sock_count ? sock_table[i] : RT_BAD_SOCKET;
}

/* Decimal digits of `value` into `text` (at least 12 wide), as wide or narrow characters. */
static void port_text(int64_t value, char *text) {
    char digits[24];
    int n = 0;
    if (value <= 0) { text[0] = '0'; text[1] = 0; return; }
    while (value > 0 && n < 23) { digits[n++] = (char)('0' + value % 10); value /= 10; }
    for (int i = 0; i < n; i++) text[i] = digits[n - 1 - i];
    text[n] = 0;
}

LARVA_API int64_t larva_net_connect(const int64_t *host, int64_t port) {
    net_init();
    char service[24];
    port_text(port, service);
    rt_socket s = RT_BAD_SOCKET;
#ifdef _WIN32
    wchar_t *name = io_text(host);
    if (!name)
        return -1;
    wchar_t wservice[24];
    for (int i = 0; i < 24; i++) { wservice[i] = (wchar_t)service[i]; if (!service[i]) break; }
    ADDRINFOW hints, *found = NULL;
    memset(&hints, 0, sizeof hints);
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    if (GetAddrInfoW(name, wservice, &hints, &found) != 0) { free(name); return -1; }
    BLOCKING_ENTER();
    for (ADDRINFOW *a = found; a; a = a->ai_next) {
        s = socket(a->ai_family, a->ai_socktype, a->ai_protocol);
        if (s == RT_BAD_SOCKET) continue;
        if (connect(s, a->ai_addr, (int)a->ai_addrlen) == 0) break;
        rt_closesocket(s);
        s = RT_BAD_SOCKET;
    }
    blocking_leave();
    FreeAddrInfoW(found);
    free(name);
#else
    char *name = io_text(host);
    if (!name)
        return -1;
    struct addrinfo hints, *found = NULL;
    memset(&hints, 0, sizeof hints);
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    if (getaddrinfo(name, service, &hints, &found) != 0) { free(name); return -1; }
    BLOCKING_ENTER();
    for (struct addrinfo *a = found; a; a = a->ai_next) {
        s = socket(a->ai_family, a->ai_socktype, a->ai_protocol);
        if (s == RT_BAD_SOCKET) continue;
        if (connect(s, a->ai_addr, a->ai_addrlen) == 0) break;
        rt_closesocket(s);
        s = RT_BAD_SOCKET;
    }
    blocking_leave();
    freeaddrinfo(found);
    free(name);
#endif
    if (s == RT_BAD_SOCKET)
        return -1;
    int one = 1;
    setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char *)&one, sizeof one);
    return sock_add(s);
}

LARVA_API int64_t larva_net_listen(int64_t port) {
    net_init();
    rt_socket s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == RT_BAD_SOCKET)
        return -1;
#ifndef _WIN32
    int one = 1;
    setsockopt(s, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
#endif
    struct sockaddr_in address;
    memset(&address, 0, sizeof address);
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_ANY);
    address.sin_port = htons((unsigned short)(port < 0 ? 0 : port));
    if (bind(s, (struct sockaddr *)&address, sizeof address) != 0 || listen(s, 16) != 0) {
        rt_closesocket(s);
        return -1;
    }
    return sock_add(s);
}

LARVA_API int64_t larva_net_accept(int64_t listener) {
    rt_socket s = sock_of(listener);
    if (s == RT_BAD_SOCKET)
        return -1;
    BLOCKING_ENTER();
    rt_socket c = accept(s, NULL, NULL);
    blocking_leave();
    if (c == RT_BAD_SOCKET)
        return -1;
    int one = 1;
    setsockopt(c, IPPROTO_TCP, TCP_NODELAY, (const char *)&one, sizeof one);
    return sock_add(c);
}

LARVA_API int64_t larva_net_port(int64_t handle) {
    rt_socket s = sock_of(handle);
    if (s == RT_BAD_SOCKET)
        return -1;
    struct sockaddr_in address;
#ifdef _WIN32
    int size = sizeof address;
#else
    socklen_t size = sizeof address;
#endif
    if (getsockname(s, (struct sockaddr *)&address, &size) != 0)
        return -1;
    return ntohs(address.sin_port);
}

/* ── Processes (System.Diagnostics.Process) ─────────────────────────
 * A started program's handle has PROC_TAG set and indexes `proc_table`: the process and the read end of its standard
 * output, which stream.read reads; proc.wait waits and closes both. */
#define PROC_TAG ((int64_t)1 << 41)
typedef struct {
#ifdef _WIN32
    HANDLE process;
    HANDLE output;
#else
    int process;   /* the pid */
    int output;    /* the pipe's read end */
#endif
    int open;
} RtProcess;
static RtProcess *proc_table;
static int64_t proc_count, proc_capacity;

static RtProcess *proc_of(int64_t handle) {
    if (!(handle & PROC_TAG))
        return NULL;
    int64_t i = handle & ~PROC_TAG;
    return i >= 0 && i < proc_count && proc_table[i].open ? &proc_table[i] : NULL;
}

static int64_t proc_add(RtProcess record) {
    if (proc_count >= proc_capacity) {
        proc_capacity = proc_capacity ? proc_capacity * 2 : 8;
        RtProcess *grown = (RtProcess *)realloc(proc_table, (size_t)proc_capacity * sizeof(RtProcess));
        if (!grown)
            return -1;
        proc_table = grown;
    }
    record.open = 1;
    proc_table[proc_count] = record;
    return PROC_TAG | proc_count++;
}

LARVA_API int64_t larva_proc_start(const int64_t *command, const int64_t *arguments) {
    RtProcess record;
    memset(&record, 0, sizeof record);
#ifdef _WIN32
    wchar_t *program = io_text(command), *args = io_text(arguments);
    if (!program || !args) { free(program); free(args); return -1; }
    /* The command line: "program" arguments. */
    size_t pn = wcslen(program), an = wcslen(args);
    wchar_t *line = (wchar_t *)malloc((pn + an + 4) * sizeof(wchar_t));
    if (!line) { free(program); free(args); return -1; }
    line[0] = L'"';
    memcpy(line + 1, program, pn * sizeof(wchar_t));
    line[pn + 1] = L'"';
    line[pn + 2] = an ? L' ' : 0;
    if (an) { memcpy(line + pn + 3, args, an * sizeof(wchar_t)); line[pn + 3 + an] = 0; }
    SECURITY_ATTRIBUTES inherit = { sizeof(SECURITY_ATTRIBUTES), NULL, TRUE };
    HANDLE read_end = NULL, write_end = NULL;
    if (!CreatePipe(&read_end, &write_end, &inherit, 0)) { free(program); free(args); free(line); return -1; }
    SetHandleInformation(read_end, HANDLE_FLAG_INHERIT, 0);
    STARTUPINFOW startup;
    memset(&startup, 0, sizeof startup);
    startup.cb = sizeof startup;
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.hStdOutput = write_end;
    startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    PROCESS_INFORMATION info;
    memset(&info, 0, sizeof info);
    fflush(stdout);
    BOOL started = CreateProcessW(NULL, line, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &info);
    CloseHandle(write_end);
    free(program); free(args); free(line);
    if (!started) { CloseHandle(read_end); return -1; }
    CloseHandle(info.hThread);
    record.process = info.hProcess;
    record.output = read_end;
#else
    char *program = io_text(command), *args = io_text(arguments);
    if (!program || !args) { free(program); free(args); return -1; }
    size_t pn = strlen(program), an = strlen(args);
    char *line = (char *)malloc(pn + an + 2);
    if (!line) { free(program); free(args); return -1; }
    memcpy(line, program, pn);
    line[pn] = an ? ' ' : 0;
    if (an) { memcpy(line + pn + 1, args, an); line[pn + 1 + an] = 0; }
    int fds[2];
    if (pipe(fds) != 0) { free(program); free(args); free(line); return -1; }
    fflush(stdout);
    pid_t pid = fork();
    if (pid < 0) { close(fds[0]); close(fds[1]); free(program); free(args); free(line); return -1; }
    if (pid == 0) {
        dup2(fds[1], 1);
        close(fds[0]);
        close(fds[1]);
        execl("/bin/sh", "sh", "-c", line, (char *)NULL);
        _exit(127);
    }
    close(fds[1]);
    free(program); free(args); free(line);
    record.process = pid;
    record.output = fds[0];
#endif
    return proc_add(record);
}

static int64_t proc_read(RtProcess *p, int64_t *bytes, int64_t offset, int64_t count) {
    if (offset < 0 || count < 0 || offset + count > bytes[0])
        return -1;
    if (count == 0)
        return 0;
    int64_t got;
    BLOCKING_ENTER();
#ifdef _WIN32
    DWORD read = 0;
    if (ReadFile(p->output, (char *)(bytes + 1) + offset, (DWORD)(count > 0x7FFFFFFF ? 0x7FFFFFFF : count), &read, NULL))
        got = read;
    else
        got = GetLastError() == ERROR_BROKEN_PIPE ? 0 : -1;
#else
    got = read(p->output, (char *)(bytes + 1) + offset, (size_t)count);
#endif
    blocking_leave();
    return got;
}

static int64_t proc_close(int64_t handle) {
    RtProcess *p = proc_of(handle);
    if (!p)
        return -1;
#ifdef _WIN32
    CloseHandle(p->output);
    CloseHandle(p->process);
#else
    close(p->output);
#endif
    p->open = 0;
    return 0;
}

/* Waits for the program to end: its exit code (-1 when unknown); the handle is closed. */
LARVA_API int64_t larva_proc_wait(int64_t handle) {
    RtProcess *p = proc_of(handle);
    if (!p)
        return -1;
    int64_t code = -1;
    BLOCKING_ENTER();
#ifdef _WIN32
    if (WaitForSingleObject(p->process, INFINITE) == WAIT_OBJECT_0) {
        DWORD exit_code = 0;
        if (GetExitCodeProcess(p->process, &exit_code))
            code = (int32_t)exit_code;
    }
#else
    int status = 0;
    if (waitpid(p->process, &status, 0) == p->process)
        code = WIFEXITED(status) ? WEXITSTATUS(status) : -1;
#endif
    blocking_leave();
    proc_close(handle);
    return code;
}

static FILE *stream_of(int64_t handle) {
    if (handle == 0) return stdin;
    if (handle == 1) return stdout;
    if (handle == 2) return stderr;
    return handle >= 3 && handle < stream_count ? stream_table[handle] : NULL;
}

/* Mode bits: 1 read, 2 write, 4 create, 8 truncate, 16 append, 32 only a new file. A handle, or -1. */
LARVA_API int64_t larva_stream_open(const int64_t *path, int64_t mode) {
    int read = (mode & 1) != 0, write = (mode & 2) != 0;
#ifdef _WIN32
    wchar_t *name = io_text(path);
    if (!name)
        return -1;
    int exists = GetFileAttributesW(name) != INVALID_FILE_ATTRIBUTES;
#else
    char *name = io_text(path);
    if (!name)
        return -1;
    FILE *probe = fopen(name, "rb");
    int exists = probe != NULL;
    if (probe)
        fclose(probe);
#endif
    if (((mode & 32) && exists) || (!exists && !(mode & 4))) {
        free(name);
        return -1;
    }
    const char *how = (mode & 16) ? (read ? "a+b" : "ab")
        : ((mode & 8) || !exists) ? (read ? "w+b" : "wb")
        : (write ? "r+b" : "rb");
#ifdef _WIN32
    wchar_t wide[4] = { 0 };
    for (int i = 0; i < 3 && how[i]; i++)
        wide[i] = (wchar_t)how[i];
    FILE *file = _wfopen(name, wide);
#else
    FILE *file = fopen(name, how);
#endif
    free(name);
    if (!file)
        return -1;
    if (stream_count < 3)
        stream_count = 3;
    if (stream_count >= stream_capacity) {
        stream_capacity = stream_capacity ? stream_capacity * 2 : 16;
        FILE **grown = (FILE **)realloc(stream_table, (size_t)stream_capacity * sizeof(FILE *));
        if (!grown) {
            fclose(file);
            return -1;
        }
        stream_table = grown;
    }
    stream_table[stream_count] = file;
    return stream_count++;
}

LARVA_API int64_t larva_stream_close(int64_t handle) {
    if (handle & PROC_TAG)
        return proc_close(handle);
    if (handle & SOCKET_TAG) {
        rt_socket s = sock_of(handle);
        if (s == RT_BAD_SOCKET)
            return -1;
        sock_table[handle & ~SOCKET_TAG] = RT_BAD_SOCKET;
        return rt_closesocket(s) == 0 ? 0 : -1;
    }
    if (handle < 3)
        return 0;
    FILE *file = stream_of(handle);
    if (!file)
        return -1;
    stream_table[handle] = NULL;
    return fclose(file) == 0 ? 0 : -1;
}

/* Reads up to `count` bytes into bytes[offset..]: how many (0 at the end), or -1. */
LARVA_API int64_t larva_stream_read(int64_t handle, int64_t *bytes, int64_t offset, int64_t count) {
    if (handle & PROC_TAG) {
        RtProcess *p = proc_of(handle);
        return p ? proc_read(p, bytes, offset, count) : -1;
    }
    if (handle & SOCKET_TAG) {
        rt_socket s = sock_of(handle);
        if (s == RT_BAD_SOCKET || offset < 0 || count < 0 || offset + count > bytes[0])
            return -1;
        if (count == 0)
            return 0;
        BLOCKING_ENTER();
        int64_t got = recv(s, (char *)(bytes + 1) + offset, (int)(count > 0x7FFFFFFF ? 0x7FFFFFFF : count), 0);
        blocking_leave();
        return got < 0 ? -1 : got;
    }
    FILE *file = stream_of(handle);
    if (!file || offset < 0 || count < 0 || offset + count > bytes[0])
        return -1;
    if (handle == 0)
        fflush(stdout);
    else
        fseek(file, 0, SEEK_CUR); /* a read may follow a write */
    size_t read = fread((uint8_t *)(bytes + 1) + offset, 1, (size_t)count, file);
    return read == 0 && ferror(file) ? -1 : (int64_t)read;
}

/* Writes bytes[offset..offset+count]: how many, or -1. */
LARVA_API int64_t larva_stream_write(int64_t handle, const int64_t *bytes, int64_t offset, int64_t count) {
    if (handle & PROC_TAG)
        return -1;
    if (handle & SOCKET_TAG) {
        rt_socket s = sock_of(handle);
        if (s == RT_BAD_SOCKET || offset < 0 || count < 0 || offset + count > bytes[0])
            return -1;
        int64_t done = 0;
        BLOCKING_ENTER();
        while (done < count) {
            int64_t sent = send(s, (const char *)(bytes + 1) + offset + done, (int)(count - done > 0x7FFFFFFF ? 0x7FFFFFFF : count - done), 0);
            if (sent <= 0) {
                done = -1;
                break;
            }
            done += sent;
        }
        blocking_leave();
        return done;
    }
    FILE *file = stream_of(handle);
    if (!file || handle == 0 || offset < 0 || count < 0 || offset + count > bytes[0])
        return -1;
    if (handle >= 3)
        fseek(file, 0, SEEK_CUR); /* a write may follow a read */
    size_t written = fwrite((const uint8_t *)(bytes + 1) + offset, 1, (size_t)count, file);
    return written == (size_t)count ? (int64_t)written : -1;
}

/* Moves to `offset` from the start (0), the current position (1) or the end (2): the new position, or -1. */
LARVA_API int64_t larva_stream_seek(int64_t handle, int64_t offset, int64_t origin) {
    if (handle & (SOCKET_TAG | PROC_TAG))
        return -1;
    FILE *file = handle >= 3 ? stream_of(handle) : NULL;
    if (!file)
        return -1;
    int whence = origin == 0 ? SEEK_SET : origin == 1 ? SEEK_CUR : SEEK_END;
#ifdef _WIN32
    if (_fseeki64(file, offset, whence) != 0)
        return -1;
    return _ftelli64(file);
#else
    if (fseeko(file, (off_t)offset, whence) != 0)
        return -1;
    return (int64_t)ftello(file);
#endif
}

LARVA_API int64_t larva_stream_length(int64_t handle) {
    if (handle & (SOCKET_TAG | PROC_TAG))
        return -1;
    FILE *file = handle >= 3 ? stream_of(handle) : NULL;
    if (!file)
        return -1;
    fflush(file);
#ifdef _WIN32
    int64_t at = _ftelli64(file);
    _fseeki64(file, 0, SEEK_END);
    int64_t end = _ftelli64(file);
    _fseeki64(file, at, SEEK_SET);
#else
    int64_t at = (int64_t)ftello(file);
    fseeko(file, 0, SEEK_END);
    int64_t end = (int64_t)ftello(file);
    fseeko(file, (off_t)at, SEEK_SET);
#endif
    return end;
}

LARVA_API int64_t larva_stream_flush(int64_t handle) {
    if (handle & (SOCKET_TAG | PROC_TAG))
        return 0;
    FILE *file = stream_of(handle);
    return file && fflush(file) == 0 ? 0 : -1;
}

/* ── Floating point (System.Math): floats travel as their bits ──────────────────────── */

static double from_bits(int64_t bits) {
    double value;
    memcpy(&value, &bits, sizeof value);
    return value;
}

static int64_t to_bits(double value) {
    int64_t bits;
    memcpy(&bits, &value, sizeof bits);
    return bits;
}

LARVA_API int64_t larva_math_sqrt(int64_t x) { return to_bits(sqrt(from_bits(x))); }
LARVA_API int64_t larva_math_sin(int64_t x) { return to_bits(sin(from_bits(x))); }
LARVA_API int64_t larva_math_cos(int64_t x) { return to_bits(cos(from_bits(x))); }
LARVA_API int64_t larva_math_tan(int64_t x) { return to_bits(tan(from_bits(x))); }
LARVA_API int64_t larva_math_asin(int64_t x) { return to_bits(asin(from_bits(x))); }
LARVA_API int64_t larva_math_acos(int64_t x) { return to_bits(acos(from_bits(x))); }
LARVA_API int64_t larva_math_atan(int64_t x) { return to_bits(atan(from_bits(x))); }
LARVA_API int64_t larva_math_exp(int64_t x) { return to_bits(exp(from_bits(x))); }
LARVA_API int64_t larva_math_log(int64_t x) { return to_bits(log(from_bits(x))); }
LARVA_API int64_t larva_math_log10(int64_t x) { return to_bits(log10(from_bits(x))); }
LARVA_API int64_t larva_math_floor(int64_t x) { return to_bits(floor(from_bits(x))); }
LARVA_API int64_t larva_math_ceil(int64_t x) { return to_bits(ceil(from_bits(x))); }
LARVA_API int64_t larva_math_atan2(int64_t x, int64_t y) { return to_bits(atan2(from_bits(x), from_bits(y))); }
LARVA_API int64_t larva_math_pow(int64_t x, int64_t y) { return to_bits(pow(from_bits(x), from_bits(y))); }
LARVA_API int64_t larva_f64_bits(int64_t bits) { return bits; }
LARVA_API int64_t larva_f64_from_bits(int64_t bits) { return bits; }

/* ── Time (System.Threading.Tasks) ──────────────────────────────────── */

/* A monotonic clock in milliseconds, from an arbitrary start. */
LARVA_API int64_t larva_time_now(void) {
#ifdef _WIN32
    LARGE_INTEGER counter, frequency;
    QueryPerformanceCounter(&counter);
    QueryPerformanceFrequency(&frequency);
    return (int64_t)(counter.QuadPart / (frequency.QuadPart / 1000));
#else
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
#endif
}

/* The calendar clock: now in 100-nanosecond ticks since 1970-01-01 UTC. */
LARVA_API int64_t larva_time_utc(void) {
#ifdef _WIN32
    FILETIME now;
    GetSystemTimePreciseAsFileTime(&now);
    return (int64_t)(((uint64_t)now.dwHighDateTime << 32) | now.dwLowDateTime) - 116444736000000000LL;
#else
    struct timespec now;
    clock_gettime(CLOCK_REALTIME, &now);
    return (int64_t)now.tv_sec * 10000000 + now.tv_nsec / 100;
#endif
}

/* The local time zone's offset from UTC in seconds (east positive) at a moment, in seconds since 1970 UTC. */
LARVA_API int64_t larva_time_offset(int64_t seconds) {
#ifdef _WIN32
    /* Windows dates start in 1601 and end in 30827: moments outside use the nearest end's rules. */
    if (seconds < -11644473600LL + 86400)
        seconds = -11644473600LL + 86400;
    if (seconds > 253402300799LL)
        seconds = 253402300799LL;
    int64_t ticks = seconds * 10000000 + 116444736000000000LL;
    FILETIME utc, local;
    SYSTEMTIME u, l;
    utc.dwLowDateTime = (DWORD)ticks;
    utc.dwHighDateTime = (DWORD)((uint64_t)ticks >> 32);
    if (!FileTimeToSystemTime(&utc, &u) || !SystemTimeToTzSpecificLocalTime(NULL, &u, &l) || !SystemTimeToFileTime(&l, &local))
        return 0;
    int64_t localTicks = (int64_t)(((uint64_t)local.dwHighDateTime << 32) | local.dwLowDateTime);
    return (localTicks - ticks) / 10000000;
#else
    time_t moment = (time_t)seconds;
    struct tm local;
    if (!localtime_r(&moment, &local))
        return 0;
    return (int64_t)local.tm_gmtoff;
#endif
}

/* A monotonic clock in nanoseconds, from an arbitrary start. */
LARVA_API int64_t larva_time_precise(void) {
#ifdef _WIN32
    static int64_t frequency;
    LARGE_INTEGER counter;
    if (frequency == 0) {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        frequency = f.QuadPart;
    }
    QueryPerformanceCounter(&counter);
    int64_t c = counter.QuadPart;
    return c / frequency * 1000000000LL + c % frequency * 1000000000LL / frequency;
#else
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000000000LL + now.tv_nsec;
#endif
}


LARVA_API void larva_time_sleep(int64_t milliseconds) {
    if (milliseconds <= 0)
        return;
    fflush(stdout);
    BLOCKING_ENTER();
#ifdef _WIN32
    Sleep((DWORD)milliseconds);
#else
    struct timespec wait = { (time_t)(milliseconds / 1000), (long)(milliseconds % 1000) * 1000000 };
    nanosleep(&wait, NULL);
#endif
    blocking_leave();
}


/* ── Threads (System.Threading) ────────────────────────────────────── */

/* Moves the only running thread's block back to larva_heap (inline allocation again), or a thread's out of it. */
static void adopt_shared_heap(void) {
    LarvaThread *me = current_record();
    if (shared_heap || threads_alive != 1)
        return;
    close_block(&larva_heap, &current_block);
    larva_heap.cursor = me->heap.cursor;
    larva_heap.limit = me->heap.limit;
    current_block = me->block;
    me->heap.cursor = me->heap.limit = NULL;
    me->block = (size_t)-1;
    shared_heap = 1;
    update_fast();
}

static void leave_shared_heap(LarvaThread *me) {
    if (!shared_heap)
        return;
    me->heap.cursor = larva_heap.cursor;
    me->heap.limit = larva_heap.limit;
    me->block = current_block;
    larva_heap.cursor = larva_heap.limit = NULL;
    current_block = (size_t)-1;
    shared_heap = 0;
    larva_heap.fast = 0;
}

/*
 * A safepoint with threads: stops for another thread's collection, or runs one with every other thread stopped. Locks are
 * taken in one order everywhere: the runtime's (rt_lock) before the world's, never the other way round.
 */
#ifdef _WIN32
static __declspec(noinline) void threaded_poll(void) {
#else
static __attribute__((noinline)) void threaded_poll(void) {
#endif
    poll_calls++;
    if (stop_world) {
        STOP_HERE();
        return;
    }
    if (finalizing != 0 || in_custom)
        return;
    if (!collection_due()) {
        if (threads_alive == 1 && !shared_heap) {
            int locked = rt_enter();
            adopt_shared_heap();
            rt_leave(locked);
        }
        update_poll_pending();
        return;
    }
    LOCK(world_lock);
    if (stop_world) {
        UNLOCK(world_lock);
        STOP_HERE();
        return;
    }
    /* This thread collects: every other one stops first (it does not count itself while it waits). */
    stop_world = 1;
    larva_poll_pending = 1;
    running_count--;
    while (running_count > 0)
        WAIT(world_stopped, world_lock);
    UNLOCK(world_lock);
    int locked = rt_enter();
    collect(1);
    collect_requested = 0;
    if (threads_alive == 1)
        adopt_shared_heap();
    rt_leave(locked);
    LOCK(world_lock);
    stop_world = 0;
    running_count++;
    update_poll_pending();
    WAKE_ALL(world_resumed);
    UNLOCK(world_lock);
    run_finalizers();
}

/* The first thread: locks made, the main thread's record listed. */
static void threads_init(void) {
    lock_init(&rt_lock);
    lock_init(&world_lock);
#ifdef _WIN32
    InitializeConditionVariable(&world_stopped);
    InitializeConditionVariable(&world_resumed);
    ULONG_PTR low, high;
    GetCurrentThreadStackLimits(&low, &high);
    main_record.stack_high = (uintptr_t)high;
#else
    main_record.stack_high = stack_top();
#endif
    this_thread = &main_record;
    thread_list = &main_record;
    running_count = 1;
    threading = 1;
}

#ifdef _WIN32
/*
 * Throwing (Windows x64). RaiseException enters the kernel before the dispatcher even looks for a handler, which costs
 * most of a throw. larva_raise does the first pass itself instead: it walks the frames with the unwind tables, and where
 * a frame is handled by __C_specific_handler (Larva code, the runtime's own __try) it reads the scope table and asks the
 * filters, as that handler would. The first filter that takes the exception gets it through RtlUnwindEx, which runs the
 * cleanups of the frames in between and lands in the handler, as after RaiseException. A frame with another handler, a
 * filter that asks to continue, or no handler at all: RaiseException does it all as before.
 */
typedef struct {
    ULONG count;
    struct { ULONG begin, end, handler, target; } entries[1];
} RtScopeTable;

typedef LONG (*RtFilter)(EXCEPTION_POINTERS *pointers, void *frame);

LARVA_API void larva_raise(DWORD code, DWORD flags, DWORD count, const ULONG_PTR *information) {
    EXCEPTION_RECORD record;
    memset(&record, 0, sizeof record);
    record.ExceptionCode = code;
    record.ExceptionFlags = flags;
    record.NumberParameters = count > EXCEPTION_MAXIMUM_PARAMETERS ? EXCEPTION_MAXIMUM_PARAMETERS : count;
    for (DWORD i = 0; i < record.NumberParameters; i++)
        record.ExceptionInformation[i] = information[i];
    record.ExceptionAddress = _ReturnAddress();
    CONTEXT context, walk;
    RtlCaptureContext(&context);
    walk = context;
    for (int depth = 0; depth < 100000; depth++) {
        DWORD64 pc = walk.Rip, base = 0;
        if (!pc)
            break;
        PRUNTIME_FUNCTION entry = RtlLookupFunctionEntry(pc, &base, NULL);
        if (!entry) {
            /* A leaf function: its return address is at the top of the stack. */
            walk.Rip = *(DWORD64 *)walk.Rsp;
            walk.Rsp += 8;
            continue;
        }
        void *data = NULL;
        DWORD64 frame = 0;
        PEXCEPTION_ROUTINE handler = RtlVirtualUnwind(UNW_FLAG_EHANDLER, base, pc, entry, &walk, &data, &frame, NULL);
        if (!handler)
            continue;
        if (handler != (PEXCEPTION_ROUTINE)__C_specific_handler)
            break;
        const RtScopeTable *scopes = (const RtScopeTable *)data;
        DWORD64 offset = pc - base;
        for (ULONG s = 0; s < scopes->count; s++) {
            if (offset < scopes->entries[s].begin || offset >= scopes->entries[s].end || scopes->entries[s].target == 0)
                continue;
            LONG verdict;
            if (scopes->entries[s].handler == EXCEPTION_EXECUTE_HANDLER) {
                verdict = EXCEPTION_EXECUTE_HANDLER;
            } else {
                EXCEPTION_POINTERS pointers = { &record, &context };
                verdict = ((RtFilter)(base + scopes->entries[s].handler))(&pointers, (void *)frame);
            }
            if (verdict < 0)
                goto raise;
            if (verdict > 0)
                RtlUnwindEx((void *)frame, (void *)(base + scopes->entries[s].target), &record, (void *)(ULONG_PTR)code, &context, NULL);
        }
    }
raise:
    RaiseException(code, flags, count, information);
}

static int thread_filter(EXCEPTION_POINTERS *pointers) {
    EXCEPTION_RECORD *record = pointers->ExceptionRecord;
    if (record->ExceptionCode != LARVA_EXCEPTION || record->NumberParameters < 2)
        return EXCEPTION_CONTINUE_SEARCH;
    return EXCEPTION_EXECUTE_HANDLER;
}

static int run_thread_code(LarvaThread *me) {
    __try {
        me->code(me->env);
        return 0;
    } __except (thread_filter(GetExceptionInformation())) {
        return 1;
    }
}
#else
extern void *larva_exception;

static int run_thread_code(LarvaThread *me) {
    me->code(me->env);
    return larva_exception != NULL;
}
#endif

#ifdef _WIN32
static DWORD WINAPI thread_main(void *argument) {
#else
static void *thread_main(void *argument) {
#endif
    LarvaThread *me = (LarvaThread *)argument;
    this_thread = me;
#ifdef _WIN32
    ULONG_PTR low, high;
    GetCurrentThreadStackLimits(&low, &high);
    me->stack_high = (uintptr_t)high;
#else
    {
        pthread_attr_t attributes;
        void *address;
        size_t size;
        pthread_getattr_np(pthread_self(), &attributes);
        pthread_attr_getstack(&attributes, &address, &size);
        pthread_attr_destroy(&attributes);
        me->stack_high = (uintptr_t)address + size;
    }
#endif
    /* Counted as stopped until now (its record was listed by the thread that made it): it runs once no collection is on. */
    become_running(me);
    int failed = run_thread_code(me);
    /* The thread's code borrowed its environment (+0): the reference taken for it at the start ends here. */
    if (counted_program && me->env)
        larva_gc_release(me->env);
    if (failed) {
        fflush(stdout);
        fprintf(stderr, "larva: unhandled exception in thread %lld\n", (long long)me->id);
        fflush(stderr);
        larva_exit(1);
    }
    /* Done: its block closes and it leaves the list (a collection no longer waits for it or scans it). */
    int locked = rt_enter();
    close_block(&me->heap, &me->block);
    LOCK(world_lock);
    for (LarvaThread **at = &thread_list; *at; at = &(*at)->next) {
        if (*at == me) {
            *at = me->next;
            break;
        }
    }
    me->state = THREAD_DONE;
    me->env = NULL;
    me->pin_count = 0;
    running_count--;
    threads_alive--;
    me->finished = 1;
    WAKE_ALL(world_stopped);
    UNLOCK(world_lock);
    rt_leave(locked);
#ifdef _WIN32
    return 0;
#else
    return NULL;
#endif
}

/* Runs code(env) on a new thread: its handle (for thread.join). */
#pragma optimize("s", on) /* cold: compiled for size (see COLD) */
LARVA_API int64_t larva_thread_start(void (*code)(void *), void *env) {
    if (!threading)
        threads_init();
    /* A cache line (or more) of its own: each thread writes its heap cursor on every allocation. */
    size_t record = (sizeof(LarvaThread) + 127) & ~(size_t)127;
    LarvaThread *thread = (LarvaThread *)larva_aligned_alloc(record, 128);
    if (!thread)
        fail_alloc();
    memset(thread, 0, record);
    thread->code = code;
    thread->env = env;
    thread->block = (size_t)-1;
    thread->state = THREAD_STOPPED;  /* stopped (nothing to scan yet) until it starts running */
    thread->saved_sp = thread->stack_high = 0;
    int locked = rt_enter();
    LOCK(world_lock);
    thread->id = ++next_thread_id;
    thread->next = thread_list->next;
    thread_list->next = thread;
    threads_alive++;
    leave_shared_heap(current_record());
    UNLOCK(world_lock);
    rt_leave(locked);
#ifdef _WIN32
    thread->os = CreateThread(NULL, 0, thread_main, thread, 0, NULL);
    int made = thread->os != NULL;
#else
    int made = pthread_create(&thread->os, NULL, thread_main, thread) == 0;
#endif
    if (!made) {
        fputs("larva: cannot start a thread\n", stderr);
        larva_exit(101);
    }
    return (int64_t)(uintptr_t)thread;
}

/* Waits for a thread to finish (`milliseconds`, -1: as long as it takes): 1 when it has (its handle is freed), 0 if not. */
LARVA_API int64_t larva_thread_join(int64_t handle, int64_t milliseconds) {
    LarvaThread *thread = (LarvaThread *)(uintptr_t)handle;
    if (!thread)
        return 1;
    fflush(stdout);
    BLOCKING_ENTER();
#ifdef _WIN32
    int done = WaitForSingleObject(thread->os, milliseconds < 0 ? INFINITE : (DWORD)milliseconds) == WAIT_OBJECT_0;
    if (done)
        CloseHandle(thread->os);
#else
    int done = 1;
    if (milliseconds < 0) {
        pthread_join(thread->os, NULL);
    } else {
        int64_t until = now_ns() + milliseconds * 1000000;
        while (!thread->finished && now_ns() < until) {
            struct timespec wait = { 0, 1000000 };
            nanosleep(&wait, NULL);
        }
        done = thread->finished;
        if (done)
            pthread_join(thread->os, NULL);
    }
#endif
    blocking_leave();
    if (done) {
        free(thread->pins);
        larva_aligned_free(thread);
    }
    return done;
}

/* The current thread's number: 1 for the first. */
LARVA_API int64_t larva_thread_id(void) {
    LarvaThread *me = this_thread;
    return me ? me->id : 1;
}

LARVA_API void larva_thread_yield(void) {
#ifdef _WIN32
    SwitchToThread();
#else
    sched_yield();
#endif
}

/* How many processors the program can run on. */
LARVA_API int64_t larva_cpu_count(void) {
#ifdef _WIN32
    SYSTEM_INFO info;
    GetSystemInfo(&info);
    return (int64_t)info.dwNumberOfProcessors;
#else
    long count = sysconf(_SC_NPROCESSORS_ONLN);
    return count > 0 ? count : 1;
#endif
}

#pragma optimize("", on)
/* ── Locks and conditions ─────────────────────────────────────────── */

#ifdef _WIN32
LARVA_API int64_t larva_mutex_new(void) {
    SRWLOCK *lock = (SRWLOCK *)calloc(1, sizeof(SRWLOCK));
    if (!lock)
        fail_alloc();
    InitializeSRWLock(lock);
    return (int64_t)(uintptr_t)lock;
}

LARVA_API void larva_mutex_lock(int64_t handle) {
    SRWLOCK *lock = (SRWLOCK *)(uintptr_t)handle;
    if (TryAcquireSRWLockExclusive(lock))
        return;
    /* Spin a little before blocking: most holds are short. */
    for (int spin = 0; spin < 64; spin++) {
        YieldProcessor();
        if (TryAcquireSRWLockExclusive(lock))
            return;
    }
    BLOCKING_ENTER();
    AcquireSRWLockExclusive(lock);
    blocking_leave();
}

LARVA_API int64_t larva_mutex_trylock(int64_t handle) { return TryAcquireSRWLockExclusive((SRWLOCK *)(uintptr_t)handle) ? 1 : 0; }
LARVA_API void larva_mutex_unlock(int64_t handle) { ReleaseSRWLockExclusive((SRWLOCK *)(uintptr_t)handle); }
LARVA_API void larva_mutex_free(int64_t handle) { free((void *)(uintptr_t)handle); }

LARVA_API int64_t larva_cond_new(void) {
    CONDITION_VARIABLE *condition = (CONDITION_VARIABLE *)calloc(1, sizeof(CONDITION_VARIABLE));
    if (!condition)
        fail_alloc();
    InitializeConditionVariable(condition);
    return (int64_t)(uintptr_t)condition;
}

LARVA_API int64_t larva_cond_wait(int64_t condition, int64_t lock, int64_t milliseconds) {
    BLOCKING_ENTER();
    BOOL woken = SleepConditionVariableSRW((CONDITION_VARIABLE *)(uintptr_t)condition, (SRWLOCK *)(uintptr_t)lock,
        milliseconds < 0 ? INFINITE : (DWORD)milliseconds, 0);
    blocking_leave();
    return woken ? 1 : 0;
}

LARVA_API void larva_cond_signal(int64_t condition) { WakeConditionVariable((CONDITION_VARIABLE *)(uintptr_t)condition); }
LARVA_API void larva_cond_broadcast(int64_t condition) { WakeAllConditionVariable((CONDITION_VARIABLE *)(uintptr_t)condition); }
LARVA_API void larva_cond_free(int64_t condition) { free((void *)(uintptr_t)condition); }
#else
LARVA_API int64_t larva_mutex_new(void) {
    pthread_mutex_t *lock = (pthread_mutex_t *)calloc(1, sizeof(pthread_mutex_t));
    if (!lock)
        fail_alloc();
    pthread_mutex_init(lock, NULL);
    return (int64_t)(uintptr_t)lock;
}

LARVA_API void larva_mutex_lock(int64_t handle) {
    pthread_mutex_t *lock = (pthread_mutex_t *)(uintptr_t)handle;
    if (pthread_mutex_trylock(lock) == 0)
        return;
    BLOCKING_ENTER();
    pthread_mutex_lock(lock);
    blocking_leave();
}

LARVA_API int64_t larva_mutex_trylock(int64_t handle) { return pthread_mutex_trylock((pthread_mutex_t *)(uintptr_t)handle) == 0; }
LARVA_API void larva_mutex_unlock(int64_t handle) { pthread_mutex_unlock((pthread_mutex_t *)(uintptr_t)handle); }
LARVA_API void larva_mutex_free(int64_t handle) {
    pthread_mutex_destroy((pthread_mutex_t *)(uintptr_t)handle);
    free((void *)(uintptr_t)handle);
}

LARVA_API int64_t larva_cond_new(void) {
    pthread_cond_t *condition = (pthread_cond_t *)calloc(1, sizeof(pthread_cond_t));
    if (!condition)
        fail_alloc();
    pthread_cond_init(condition, NULL);
    return (int64_t)(uintptr_t)condition;
}

LARVA_API int64_t larva_cond_wait(int64_t condition, int64_t lock, int64_t milliseconds) {
    pthread_cond_t *c = (pthread_cond_t *)(uintptr_t)condition;
    pthread_mutex_t *m = (pthread_mutex_t *)(uintptr_t)lock;
    BLOCKING_ENTER();
    int result;
    if (milliseconds < 0) {
        result = pthread_cond_wait(c, m);
    } else {
        struct timespec until;
        clock_gettime(CLOCK_REALTIME, &until);
        until.tv_sec += milliseconds / 1000;
        until.tv_nsec += (milliseconds % 1000) * 1000000;
        if (until.tv_nsec >= 1000000000) {
            until.tv_sec++;
            until.tv_nsec -= 1000000000;
        }
        result = pthread_cond_timedwait(c, m, &until);
    }
    blocking_leave();
    return result == 0;
}

LARVA_API void larva_cond_signal(int64_t condition) { pthread_cond_signal((pthread_cond_t *)(uintptr_t)condition); }
LARVA_API void larva_cond_broadcast(int64_t condition) { pthread_cond_broadcast((pthread_cond_t *)(uintptr_t)condition); }
LARVA_API void larva_cond_free(int64_t condition) {
    pthread_cond_destroy((pthread_cond_t *)(uintptr_t)condition);
    free((void *)(uintptr_t)condition);
}
#endif

/* ── Atomics (the x64 back end calls these; LLVM makes the instructions itself) ── */

#ifdef _WIN32
LARVA_API int64_t larva_atomic_load_i64(volatile int64_t *at) { return InterlockedCompareExchange64(at, 0, 0); }
LARVA_API void larva_atomic_store_i64(volatile int64_t *at, int64_t value) { InterlockedExchange64(at, value); }
LARVA_API int64_t larva_atomic_add_i64(volatile int64_t *at, int64_t value) { return InterlockedExchangeAdd64(at, value); }
LARVA_API int64_t larva_atomic_xchg_i64(volatile int64_t *at, int64_t value) { return InterlockedExchange64(at, value); }
LARVA_API int64_t larva_atomic_cas_i64(volatile int64_t *at, int64_t expected, int64_t value) { return InterlockedCompareExchange64(at, value, expected); }
LARVA_API int32_t larva_atomic_load_i32(volatile long *at) { return InterlockedCompareExchange(at, 0, 0); }
LARVA_API void larva_atomic_store_i32(volatile long *at, int32_t value) { InterlockedExchange(at, value); }
LARVA_API int32_t larva_atomic_add_i32(volatile long *at, int32_t value) { return InterlockedExchangeAdd(at, value); }
LARVA_API int32_t larva_atomic_xchg_i32(volatile long *at, int32_t value) { return InterlockedExchange(at, value); }
LARVA_API int32_t larva_atomic_cas_i32(volatile long *at, int32_t expected, int32_t value) { return InterlockedCompareExchange(at, value, expected); }
#else
LARVA_API int64_t larva_atomic_load_i64(int64_t *at) { return __atomic_load_n(at, __ATOMIC_SEQ_CST); }
LARVA_API void larva_atomic_store_i64(int64_t *at, int64_t value) { __atomic_store_n(at, value, __ATOMIC_SEQ_CST); }
LARVA_API int64_t larva_atomic_add_i64(int64_t *at, int64_t value) { return __atomic_fetch_add(at, value, __ATOMIC_SEQ_CST); }
LARVA_API int64_t larva_atomic_xchg_i64(int64_t *at, int64_t value) { return __atomic_exchange_n(at, value, __ATOMIC_SEQ_CST); }
LARVA_API int64_t larva_atomic_cas_i64(int64_t *at, int64_t expected, int64_t value) {
    __atomic_compare_exchange_n(at, &expected, value, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    return expected;
}
LARVA_API int32_t larva_atomic_load_i32(int32_t *at) { return __atomic_load_n(at, __ATOMIC_SEQ_CST); }
LARVA_API void larva_atomic_store_i32(int32_t *at, int32_t value) { __atomic_store_n(at, value, __ATOMIC_SEQ_CST); }
LARVA_API int32_t larva_atomic_add_i32(int32_t *at, int32_t value) { return __atomic_fetch_add(at, value, __ATOMIC_SEQ_CST); }
LARVA_API int32_t larva_atomic_xchg_i32(int32_t *at, int32_t value) { return __atomic_exchange_n(at, value, __ATOMIC_SEQ_CST); }
LARVA_API int32_t larva_atomic_cas_i32(int32_t *at, int32_t expected, int32_t value) {
    __atomic_compare_exchange_n(at, &expected, value, 0, __ATOMIC_SEQ_CST, __ATOMIC_SEQ_CST);
    return expected;
}
#endif

/* Runs a command line through the system's shell (with this program's console): its exit code, or -1. */
LARVA_API int64_t larva_process_run(const int64_t *command) {
    fflush(stdout);
    fflush(stderr);
#ifdef _WIN32
    wchar_t *line = io_text(command);
    int code = line ? _wsystem(line) : -1;
    free(line);
    return code;
#else
    char *line = io_text(command);
    int status = line ? system(line) : -1;
    free(line);
    return status == -1 ? -1 : WIFEXITED(status) ? WEXITSTATUS(status) : -1;
#endif
}

/* Keeps the command line (without the program's name, UTF-8) for args.*. */
#pragma optimize("s", on) /* cold: compiled for size (see COLD) */
static void keep_arguments(int argc, char **argv) {
    argument_count = argc > 1 ? argc - 1 : 0;
    arguments_text = (Units *)calloc((size_t)argument_count + 1, sizeof(Units));
    for (int64_t i = 0; i < argument_count; i++)
        units_utf8(&arguments_text[i], (const unsigned char *)argv[i + 1], strlen(argv[i + 1]));
}

#pragma optimize("", on)
/* ── Soft-float routines (Windows) ───────────────────────────────── */

#ifdef _WIN32
/*
 * What LLVM calls for f16 and f128 on x86-64 (compiler-rt's names and contracts); MSVC's libraries do not have them, and
 * POSIX toolchains link libgcc or compiler-rt. A half travels in the low 16 bits of an XMM register, an f128 by reference.
 */
static float half_to_float(uint16_t h) {
    uint32_t sign = (uint32_t)(h & 0x8000) << 16, exponent = (h >> 10) & 0x1F, mantissa = h & 0x3FF, bits;
    if (exponent == 0) {
        if (mantissa == 0) {
            bits = sign;
        } else {
            /* Subnormal: normalize it. */
            exponent = 127 - 15 + 1;
            while (!(mantissa & 0x400)) {
                mantissa <<= 1;
                exponent--;
            }
            bits = sign | exponent << 23 | (mantissa & 0x3FF) << 13;
        }
    } else if (exponent == 31) {
        bits = sign | 0x7F800000 | mantissa << 13;
    } else {
        bits = sign | (exponent + 112) << 23 | mantissa << 13;
    }
    float value;
    memcpy(&value, &bits, sizeof value);
    return value;
}

/* Rounds to nearest, ties to even. */
static uint16_t float_to_half(float value) {
    uint32_t x;
    memcpy(&x, &value, sizeof x);
    uint32_t sign = (x >> 16) & 0x8000, mantissa = x & 0x7FFFFF;
    int32_t biased = (int32_t)((x >> 23) & 0xFF);
    if (biased == 0xFF)
        return (uint16_t)(sign | 0x7C00 | (mantissa ? 0x200 : 0));
    int32_t exponent = biased - 127 + 15;
    if (exponent >= 31)
        return (uint16_t)(sign | 0x7C00);
    if (exponent <= 0) {
        if (exponent < -10)
            return (uint16_t)sign;
        mantissa |= 0x800000;
        uint32_t shift = (uint32_t)(14 - exponent), half = mantissa >> shift, rest = mantissa & ((1u << shift) - 1), middle = 1u << (shift - 1);
        if (rest > middle || (rest == middle && (half & 1)))
            half++;
        return (uint16_t)(sign | half);
    }
    uint32_t half = sign | (uint32_t)exponent << 10 | mantissa >> 13, rest = mantissa & 0x1FFF;
    if (rest > 0x1000 || (rest == 0x1000 && (half & 1)))
        half++;
    return (uint16_t)half;
}

static uint16_t half_in(float reg) {
    uint32_t bits;
    memcpy(&bits, &reg, sizeof bits);
    return (uint16_t)bits;
}

static float half_out(uint16_t half) {
    uint32_t bits = half;
    float reg;
    memcpy(&reg, &bits, sizeof reg);
    return reg;
}

LARVA_API float __extendhfsf2(float reg) { return half_to_float(half_in(reg)); }
LARVA_API double __extendhfdf2(float reg) { return half_to_float(half_in(reg)); }
LARVA_API float __truncsfhf2(float value) { return half_out(float_to_half(value)); }
LARVA_API float __truncdfhf2(double value) { return half_out(float_to_half((float)value)); }

/* An f128 as two 64-bit halves, low first. */
static int quad_nan(const uint64_t *q) {
    uint64_t high = q[1] & 0x7FFFFFFFFFFFFFFFull;
    return high > 0x7FFF000000000000ull || (high == 0x7FFF000000000000ull && q[0] != 0);
}

static int quad_compare(const uint64_t *a, const uint64_t *b, int unordered) {
    if (quad_nan(a) || quad_nan(b))
        return unordered;
    uint64_t magnitude_a = a[1] & 0x7FFFFFFFFFFFFFFFull, magnitude_b = b[1] & 0x7FFFFFFFFFFFFFFFull;
    if (magnitude_a == 0 && a[0] == 0 && magnitude_b == 0 && b[0] == 0)
        return 0; /* +0 == -0 */
    int negative_a = (int)(a[1] >> 63), negative_b = (int)(b[1] >> 63);
    if (negative_a != negative_b)
        return negative_a ? -1 : 1;
    int by_magnitude = magnitude_a != magnitude_b ? (magnitude_a < magnitude_b ? -1 : 1) : a[0] != b[0] ? (a[0] < b[0] ? -1 : 1) : 0;
    return negative_a ? -by_magnitude : by_magnitude;
}

/* `<`, `<=`, `==`, `!=` are false (1) on unordered values; `>`, `>=` are false (-1). */
LARVA_API int __eqtf2(const uint64_t *a, const uint64_t *b) { return quad_compare(a, b, 1); }
LARVA_API int __netf2(const uint64_t *a, const uint64_t *b) { return quad_compare(a, b, 1); }
LARVA_API int __lttf2(const uint64_t *a, const uint64_t *b) { return quad_compare(a, b, 1); }
LARVA_API int __letf2(const uint64_t *a, const uint64_t *b) { return quad_compare(a, b, 1); }
LARVA_API int __cmptf2(const uint64_t *a, const uint64_t *b) { return quad_compare(a, b, 1); }
LARVA_API int __gttf2(const uint64_t *a, const uint64_t *b) { return quad_compare(a, b, -1); }
LARVA_API int __getf2(const uint64_t *a, const uint64_t *b) { return quad_compare(a, b, -1); }
LARVA_API int __unordtf2(const uint64_t *a, const uint64_t *b) { return quad_nan(a) || quad_nan(b); }

/* 128-bit integers <-> f64 (compiler-rt's names): the integer argument travels by reference, a 128-bit result comes
   back in XMM0. Rounded to nearest, ties to even, as a conversion instruction would. */
static double u128_to_double(uint64_t low, uint64_t high) {
    if (high == 0)
        return (double)low; /* MSVC's u64 -> f64 rounds correctly */
    unsigned long top_bit;
    _BitScanReverse64(&top_bit, high);
    int length = 64 + (int)top_bit + 1; /* 65 ... 128 */
    int shift = length - 64;            /* 1 ... 64 */
    uint64_t top = shift == 64 ? high : (high << (64 - shift)) | (low >> shift);
    uint64_t lost = shift == 64 ? low : low & ((1ull << shift) - 1);
    if (lost)
        top |= 1; /* sticky: below the 53 bits an f64 keeps, so it only breaks ties */
    uint64_t scale_bits = (uint64_t)(1023 + shift) << 52;
    double scale;
    memcpy(&scale, &scale_bits, sizeof scale);
    return (double)top * scale;
}

LARVA_API double __floatuntidf(const uint64_t *x) { return u128_to_double(x[0], x[1]); }

LARVA_API double __floattidf(const uint64_t *x) {
    if (!(x[1] >> 63))
        return u128_to_double(x[0], x[1]);
    uint64_t low = ~x[0] + 1, high = ~x[1] + (low == 0);
    return -u128_to_double(low, high);
}

/* |x| truncated toward zero, as 128 bits (x below 2^128). */
static void double_to_u128(double x, uint64_t *low, uint64_t *high) {
    if (x < 18446744073709551616.0) {
        *low = x < 9223372036854775808.0 ? (uint64_t)(int64_t)x : (uint64_t)(int64_t)(x - 9223372036854775808.0) + 0x8000000000000000ull;
        *high = 0;
        return;
    }
    uint64_t bits;
    memcpy(&bits, &x, sizeof bits);
    int exponent = (int)((bits >> 52) & 0x7FF) - 1075; /* 12 ... */
    uint64_t mantissa = (bits & 0xFFFFFFFFFFFFFull) | 0x10000000000000ull;
    if (exponent >= 76) { /* beyond 128 bits */
        *low = ~0ull;
        *high = ~0ull;
        return;
    }
    *low = exponent >= 64 ? 0 : mantissa << exponent;
    *high = exponent >= 64 ? mantissa << (exponent - 64) : mantissa >> (64 - exponent);
}

LARVA_API __m128i __fixunsdfti(double x) {
    uint64_t low = 0, high = 0;
    if (x > 0)
        double_to_u128(x, &low, &high);
    return _mm_set_epi64x((int64_t)high, (int64_t)low);
}

LARVA_API __m128i __fixdfti(double x) {
    uint64_t low = 0, high = 0;
    double_to_u128(x < 0 ? -x : x, &low, &high);
    if (x < 0) {
        low = ~low + 1;
        high = ~high + (low == 0);
    }
    return _mm_set_epi64x((int64_t)high, (int64_t)low);
}
#endif

/* ── Panics ──────────────────────────────────────────────────────── */

/* ── Wide integers (128 and 256 bits) for the direct x86-64 back end: 64-bit limbs, lowest first ── */

LARVA_API void larva_panic(int32_t code, int64_t a, int64_t b);

static int wide_is_negative(const uint64_t *x, int limbs) { return (x[limbs - 1] >> 63) != 0; }

static void wide_negate(uint64_t *x, int limbs) {
    uint64_t carry = 1;
    for (int i = 0; i < limbs; i++) {
        uint64_t v = ~x[i] + carry;
        carry = (carry && v == 0) ? 1 : 0;
        x[i] = v;
    }
}

static int wide_ucompare(const uint64_t *a, const uint64_t *b, int limbs) {
    for (int i = limbs - 1; i >= 0; i--) {
        if (a[i] != b[i])
            return a[i] < b[i] ? -1 : 1;
    }
    return 0;
}

static void wide_usub(uint64_t *a, const uint64_t *b, int limbs) {
    uint64_t borrow = 0;
    for (int i = 0; i < limbs; i++) {
        uint64_t d = a[i] - b[i] - borrow;
        borrow = (a[i] < b[i] || (a[i] == b[i] && borrow)) ? 1 : 0;
        a[i] = d;
    }
}

static void wide_shl1(uint64_t *x, int limbs) {
    for (int i = limbs - 1; i > 0; i--)
        x[i] = (x[i] << 1) | (x[i - 1] >> 63);
    x[0] <<= 1;
}

static int wide_is_zero(const uint64_t *x, int limbs) {
    for (int i = 0; i < limbs; i++)
        if (x[i]) return 0;
    return 1;
}

/* Shift-subtract long division of unsigned limbs: q = a / b, r = a % b (either may be null). */
static void wide_udivrem(const uint64_t *a, const uint64_t *b, uint64_t *q, uint64_t *r, int limbs) {
    uint64_t rem[4] = {0, 0, 0, 0};
    uint64_t quo[4] = {0, 0, 0, 0};
    for (int bit = 64 * limbs - 1; bit >= 0; bit--) {
        wide_shl1(rem, limbs);
        rem[0] |= (a[bit / 64] >> (bit % 64)) & 1;
        if (wide_ucompare(rem, b, limbs) >= 0) {
            wide_usub(rem, b, limbs);
            quo[bit / 64] |= (uint64_t)1 << (bit % 64);
        }
    }
    if (q) memcpy(q, quo, 8 * limbs);
    if (r) memcpy(r, rem, 8 * limbs);
}

static void wide_divide(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs, int is_signed, int remainder) {
    if (wide_is_zero(b, limbs)) {
        larva_panic(2, 0, 0);
        return;
    }
    uint64_t na[4], nb[4];
    memcpy(na, a, 8 * limbs);
    memcpy(nb, b, 8 * limbs);
    int negate_a = is_signed && wide_is_negative(na, limbs);
    int negate_b = is_signed && wide_is_negative(nb, limbs);
    if (negate_a) wide_negate(na, limbs);
    if (negate_b) wide_negate(nb, limbs);
    if (remainder) {
        wide_udivrem(na, nb, NULL, result, limbs);
        if (negate_a) wide_negate(result, limbs); /* the remainder takes the dividend's sign */
    } else {
        wide_udivrem(na, nb, result, NULL, limbs);
        if (negate_a != negate_b) wide_negate(result, limbs);
    }
}

LARVA_API void larva_wide_udiv(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) { wide_divide(a, b, result, limbs, 0, 0); }
LARVA_API void larva_wide_urem(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) { wide_divide(a, b, result, limbs, 0, 1); }
LARVA_API void larva_wide_sdiv(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) { wide_divide(a, b, result, limbs, 1, 0); }
LARVA_API void larva_wide_srem(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) { wide_divide(a, b, result, limbs, 1, 1); }

/* The shift amount is the low limb of b, masked to the width (as x86 masks its shifts). */
LARVA_API void larva_wide_shl(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) {
    int amount = (int)(b[0] & (uint64_t)(64 * limbs - 1));
    int whole = amount / 64, part = amount % 64;
    for (int i = limbs - 1; i >= 0; i--) {
        uint64_t v = 0;
        if (i - whole >= 0) {
            v = a[i - whole] << part;
            if (part && i - whole - 1 >= 0)
                v |= a[i - whole - 1] >> (64 - part);
        }
        result[i] = v;
    }
}

static void wide_shr(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs, uint64_t fill) {
    int amount = (int)(b[0] & (uint64_t)(64 * limbs - 1));
    int whole = amount / 64, part = amount % 64;
    for (int i = 0; i < limbs; i++) {
        uint64_t v;
        if (i + whole < limbs) {
            v = a[i + whole] >> part;
            if (part)
                v |= (i + whole + 1 < limbs ? a[i + whole + 1] : fill) << (64 - part);
        } else {
            v = fill;
        }
        result[i] = v;
    }
}

LARVA_API void larva_wide_lshr(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) { wide_shr(a, b, result, limbs, 0); }
LARVA_API void larva_wide_ashr(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) {
    wide_shr(a, b, result, limbs, wide_is_negative(a, limbs) ? ~(uint64_t)0 : 0);
}

/* ── Software floats for the direct x86-64 back end: f128 and f256 arithmetic and comparisons, and conversions between
 * any float and any integer or float width (what Larva.Native.NativeRuntime computes for the JIT and the VM computes with
 * SoftFloat, in C#: the same results, bit for bit). Values are 64-bit limbs, lowest first; a narrower float or integer
 * sits in the low bytes of an 8-byte slot (integers in their canonical 64-bit form, extended by signedness). Every result
 * is rounded to nearest, ties to even; NaNs come out as the canonical quiet NaN. ── */

#define FL_WORDS 13 /* 832 bits of working precision: an f256 division needs 3·236 + 5 */

typedef struct { int bits, exponent_bits, fraction_bits, bias, limbs; } fl_format;

static fl_format fl_format_of(int bits) {
    fl_format f;
    f.bits = bits;
    f.exponent_bits = bits == 16 ? 5 : bits == 32 ? 8 : bits == 64 ? 11 : bits == 128 ? 15 : 19;
    f.fraction_bits = bits - 1 - f.exponent_bits;
    f.bias = (1 << (f.exponent_bits - 1)) - 1;
    f.limbs = bits < 64 ? 1 : bits / 64;
    return f;
}

typedef struct { uint64_t w[FL_WORDS]; } fl_big;

static void fl_clear(fl_big *x) {
    int i;
    for (i = 0; i < FL_WORDS; i++) x->w[i] = 0;
}

static int fl_is_zero(const fl_big *x) {
    int i;
    for (i = 0; i < FL_WORDS; i++) if (x->w[i]) return 0;
    return 1;
}

/* The index of the highest set bit; -1 for zero. */
static int fl_msb(const fl_big *x) {
    int i, b;
    for (i = FL_WORDS - 1; i >= 0; i--) {
        if (x->w[i]) {
            for (b = 63; !((x->w[i] >> b) & 1); b--) {}
            return 64 * i + b;
        }
    }
    return -1;
}

static int fl_bit(const fl_big *x, int i) { return i < 0 || i >= 64 * FL_WORDS ? 0 : (int)((x->w[i / 64] >> (i % 64)) & 1); }
static void fl_set_bit(fl_big *x, int i) { x->w[i / 64] |= (uint64_t)1 << (i % 64); }
static void fl_clear_bit(fl_big *x, int i) { x->w[i / 64] &= ~((uint64_t)1 << (i % 64)); }

static void fl_shl(fl_big *x, int n) {
    int words, bits, i;
    if (n <= 0) return;
    if (n >= 64 * FL_WORDS) { fl_clear(x); return; }
    words = n / 64; bits = n % 64;
    for (i = FL_WORDS - 1; i >= 0; i--) {
        uint64_t v = i - words >= 0 ? x->w[i - words] << bits : 0;
        if (bits && i - words - 1 >= 0) v |= x->w[i - words - 1] >> (64 - bits);
        x->w[i] = v;
    }
}

/* Shifts right; says whether a set bit was shifted out (the sticky bit). */
static int fl_shr(fl_big *x, int n) {
    int words, bits, i, sticky = 0;
    if (n <= 0) return 0;
    if (n >= 64 * FL_WORDS) { sticky = !fl_is_zero(x); fl_clear(x); return sticky; }
    words = n / 64; bits = n % 64;
    for (i = 0; i < words; i++) if (x->w[i]) sticky = 1;
    if (bits && (x->w[words] & (((uint64_t)1 << bits) - 1))) sticky = 1;
    for (i = 0; i < FL_WORDS; i++) {
        uint64_t v = i + words < FL_WORDS ? x->w[i + words] >> bits : 0;
        if (bits && i + words + 1 < FL_WORDS) v |= x->w[i + words + 1] << (64 - bits);
        x->w[i] = v;
    }
    return sticky;
}

static int fl_cmp(const fl_big *a, const fl_big *b) {
    int i;
    for (i = FL_WORDS - 1; i >= 0; i--) if (a->w[i] != b->w[i]) return a->w[i] < b->w[i] ? -1 : 1;
    return 0;
}

static void fl_add(fl_big *r, const fl_big *a, const fl_big *b) {
    uint64_t carry = 0;
    int i;
    for (i = 0; i < FL_WORDS; i++) {
        uint64_t s = a->w[i] + b->w[i], c = s < a->w[i], t = s + carry;
        c |= t < s;
        r->w[i] = t;
        carry = c;
    }
}

/* a - b for a >= b. */
static void fl_sub(fl_big *r, const fl_big *a, const fl_big *b) {
    uint64_t borrow = 0;
    int i;
    for (i = 0; i < FL_WORDS; i++) {
        uint64_t d = a->w[i] - b->w[i], bo = a->w[i] < b->w[i], t = d - borrow;
        bo |= d < borrow;
        r->w[i] = t;
        borrow = bo;
    }
}

static void fl_add_one(fl_big *x) {
    int i;
    for (i = 0; i < FL_WORDS; i++) if (++x->w[i]) return;
}

static void fl_sub_one(fl_big *x) {
    int i;
    for (i = 0; i < FL_WORDS; i++) if (x->w[i]--) return;
}

static void fl_mul64(uint64_t a, uint64_t b, uint64_t *lo, uint64_t *hi) {
    uint64_t a0 = (uint32_t)a, a1 = a >> 32, b0 = (uint32_t)b, b1 = b >> 32;
    uint64_t p00 = a0 * b0, p01 = a0 * b1, p10 = a1 * b0, p11 = a1 * b1;
    uint64_t mid = (p00 >> 32) + (uint32_t)p01 + (uint32_t)p10;
    *lo = (mid << 32) | (uint32_t)p00;
    *hi = p11 + (p01 >> 32) + (p10 >> 32) + (mid >> 32);
}

/* r = a × b (r apart from a and b); what passes the top word is lost (the callers keep their products inside it). */
static void fl_mul(fl_big *r, const fl_big *a, const fl_big *b) {
    int i, j, k;
    fl_clear(r);
    for (i = 0; i < FL_WORDS; i++) {
        if (!a->w[i]) continue;
        for (j = 0; i + j < FL_WORDS; j++) {
            uint64_t lo, hi, carry;
            if (!b->w[j]) continue;
            fl_mul64(a->w[i], b->w[j], &lo, &hi);
            carry = (r->w[i + j] += lo) < lo;
            for (k = i + j + 1; k < FL_WORDS && (hi || carry); k++) {
                uint64_t before = r->w[k];
                r->w[k] += hi + carry;
                carry = r->w[k] < before || (hi + carry < hi);
                hi = 0;
            }
        }
    }
}

/* q = a / b, r = a % b (b nonzero), bit by bit. */
static void fl_divrem(fl_big *q, fl_big *r, const fl_big *a, const fl_big *b) {
    int i;
    fl_clear(q);
    fl_clear(r);
    for (i = fl_msb(a); i >= 0; i--) {
        fl_shl(r, 1);
        r->w[0] |= (uint64_t)fl_bit(a, i);
        if (fl_cmp(r, b) >= 0) {
            fl_sub(r, r, b);
            fl_set_bit(q, i);
        }
    }
}

/* A number taken apart: sig × 2^exponent for a finite one (the significand holds the hidden bit). */
typedef struct { int negative, cls, exponent; fl_big sig; } fl_value; /* cls: 0 zero, 1 finite, 2 infinity, 3 NaN */

static void fl_unpack(fl_value *v, const uint64_t *bits, fl_format f) {
    int i, e = 0, word = f.fraction_bits / 64, bit = f.fraction_bits % 64;
    fl_big raw;
    fl_clear(&raw);
    for (i = 0; i < f.limbs; i++) raw.w[i] = bits[i];
    if (f.bits < 64) raw.w[0] &= ((uint64_t)1 << f.bits) - 1;
    v->negative = fl_bit(&raw, f.bits - 1);
    for (i = 0; i < f.exponent_bits; i++) e |= fl_bit(&raw, f.fraction_bits + i) << i;
    v->sig = raw;
    v->sig.w[word] &= bit ? ((uint64_t)1 << bit) - 1 : 0;
    for (i = word + 1; i < FL_WORDS; i++) v->sig.w[i] = 0;
    v->exponent = 0;
    if (e == (1 << f.exponent_bits) - 1) { v->cls = fl_is_zero(&v->sig) ? 2 : 3; return; }
    if (e == 0) {
        if (fl_is_zero(&v->sig)) { v->cls = 0; return; }
        v->cls = 1;
        v->exponent = 1 - f.bias - f.fraction_bits;
        return;
    }
    fl_set_bit(&v->sig, f.fraction_bits);
    v->cls = 1;
    v->exponent = e - f.bias - f.fraction_bits;
}

static void fl_out(uint64_t *out, fl_format f, const fl_big *x) {
    int i;
    for (i = 0; i < f.limbs; i++) out[i] = x->w[i];
}

/* A zero (cls 0), an infinity (2) or the canonical quiet NaN (3). */
static void fl_special(uint64_t *out, fl_format f, int negative, int cls) {
    fl_big x;
    int i;
    fl_clear(&x);
    if (cls == 2 || cls == 3) for (i = 0; i < f.exponent_bits; i++) fl_set_bit(&x, f.fraction_bits + i);
    if (cls == 3) fl_set_bit(&x, f.fraction_bits - 1);
    if (negative && cls != 3) fl_set_bit(&x, f.bits - 1);
    fl_out(out, f, &x);
}

/* mag × 2^exponent rounded to nearest even into the format (sticky: nonzero bits below mag's lowest one), with overflow
 * to infinity and gradual underflow. */
static void fl_pack(uint64_t *out, fl_format f, int negative, fl_big *mag, int exponent, int sticky) {
    int msb = fl_msb(mag), e, shift, i, field;
    if (msb < 0) { fl_special(out, f, negative, 0); return; }
    e = exponent + msb;
    shift = msb - f.fraction_bits;
    if (e < 1 - f.bias) { shift += (1 - f.bias) - e; e = 1 - f.bias; }
    if (shift <= 0) {
        fl_shl(mag, -shift);
    } else {
        int below = fl_shr(mag, shift - 1) | sticky;
        int round = (int)(mag->w[0] & 1);
        fl_shr(mag, 1);
        if (round && (below || (mag->w[0] & 1))) {
            fl_add_one(mag);
            if (fl_bit(mag, f.fraction_bits + 1)) { fl_shr(mag, 1); e++; }
        }
    }
    if (e > f.bias) { fl_special(out, f, negative, 2); return; }
    if (fl_bit(mag, f.fraction_bits)) {
        fl_clear_bit(mag, f.fraction_bits);
        field = e + f.bias;
        for (i = 0; i < f.exponent_bits; i++) if ((field >> i) & 1) fl_set_bit(mag, f.fraction_bits + i);
    }
    if (negative) fl_set_bit(mag, f.bits - 1);
    fl_out(out, f, mag);
}

static void fl_repack(uint64_t *out, fl_format f, fl_value *x) {
    if (x->cls != 1) fl_special(out, f, x->negative, x->cls);
    else fl_pack(out, f, x->negative, &x->sig, x->exponent, 0);
}

static void fl_add_values(uint64_t *out, fl_format f, fl_value *x, fl_value *y) {
    fl_big sx, sy, r;
    int d, sticky, exponent;
    if (x->cls == 3 || y->cls == 3) { fl_special(out, f, 0, 3); return; }
    if (x->cls == 2) {
        if (y->cls == 2 && x->negative != y->negative) fl_special(out, f, 0, 3);
        else fl_special(out, f, x->negative, 2);
        return;
    }
    if (y->cls == 2) { fl_special(out, f, y->negative, 2); return; }
    if (x->cls == 0) {
        if (y->cls == 0) fl_special(out, f, x->negative && y->negative, 0);
        else fl_repack(out, f, y);
        return;
    }
    if (y->cls == 0) { fl_repack(out, f, x); return; }
    if (x->exponent < y->exponent) { fl_value *t = x; x = y; y = t; }
    /* Guard bits below both, so the smaller one's dropped bits only reach the result as a sticky bit. */
    sx = x->sig; sy = y->sig;
    fl_shl(&sx, 64);
    fl_shl(&sy, 64);
    d = x->exponent - y->exponent;
    sticky = fl_shr(&sy, d);
    exponent = x->exponent - 64;
    if (x->negative == y->negative) {
        fl_add(&r, &sx, &sy);
        fl_pack(out, f, x->negative, &r, exponent, sticky);
        return;
    }
    if (fl_cmp(&sx, &sy) >= 0) {
        /* The dropped bits of the smaller one make the difference a little less than sx - sy. */
        fl_sub(&r, &sx, &sy);
        if (sticky) fl_sub_one(&r);
        if (fl_is_zero(&r) && !sticky) fl_special(out, f, 0, 0);
        else fl_pack(out, f, x->negative, &r, exponent, sticky);
        return;
    }
    fl_sub(&r, &sy, &sx);
    fl_pack(out, f, y->negative, &r, exponent, sticky);
}

static void fl_mul_values(uint64_t *out, fl_format f, fl_value *x, fl_value *y) {
    fl_big r;
    int negative = x->negative != y->negative;
    if (x->cls == 3 || y->cls == 3) { fl_special(out, f, 0, 3); return; }
    if (x->cls == 2 || y->cls == 2) { fl_special(out, f, negative, x->cls == 0 || y->cls == 0 ? 3 : 2); return; }
    if (x->cls == 0 || y->cls == 0) { fl_special(out, f, negative, 0); return; }
    fl_mul(&r, &x->sig, &y->sig);
    fl_pack(out, f, negative, &r, x->exponent + y->exponent, 0);
}

static void fl_div_values(uint64_t *out, fl_format f, fl_value *x, fl_value *y) {
    fl_big num, q, r;
    int negative = x->negative != y->negative, extra = 2 * f.fraction_bits + 4;
    if (x->cls == 3 || y->cls == 3) { fl_special(out, f, 0, 3); return; }
    if (x->cls == 2) { fl_special(out, f, negative, y->cls == 2 ? 3 : 2); return; }
    if (y->cls == 2) { fl_special(out, f, negative, 0); return; }
    if (y->cls == 0) { fl_special(out, f, negative, x->cls == 0 ? 3 : 2); return; }
    if (x->cls == 0) { fl_special(out, f, negative, 0); return; }
    /* Enough quotient bits whatever the significands' widths (a subnormal's may be tiny): the rest is sticky. */
    num = x->sig;
    fl_shl(&num, extra);
    fl_divrem(&q, &r, &num, &y->sig);
    fl_pack(out, f, negative, &q, x->exponent - y->exponent - extra, !fl_is_zero(&r));
}

/* The remainder of a truncating division (fmod): exact, with the sign of the dividend. */
static void fl_rem_values(uint64_t *out, fl_format f, fl_value *x, fl_value *y) {
    fl_big q, r, b;
    int e;
    if (x->cls == 3 || y->cls == 3 || x->cls == 2 || y->cls == 0) { fl_special(out, f, 0, 3); return; }
    if (y->cls == 2 || x->cls == 0) { fl_repack(out, f, x); return; }
    b = y->sig;
    if (x->exponent >= y->exponent) {
        int n = x->exponent - y->exponent;
        fl_divrem(&q, &r, &x->sig, &b);
        while (n-- > 0) {
            fl_shl(&r, 1);
            if (fl_cmp(&r, &b) >= 0) fl_sub(&r, &r, &b);
        }
        e = y->exponent;
    } else {
        int d = y->exponent - x->exponent;
        if (d > f.fraction_bits + 2) { fl_repack(out, f, x); return; } /* |x| < |y| */
        fl_shl(&b, d);
        fl_divrem(&q, &r, &x->sig, &b);
        e = x->exponent;
    }
    if (fl_is_zero(&r)) fl_special(out, f, x->negative, 0);
    else fl_pack(out, f, x->negative, &r, e, 0);
}

/* -1, 0 or 1 by magnitude (finite or infinite values). */
static int fl_compare_magnitudes(const fl_value *x, const fl_value *y) {
    fl_big sx, sy;
    int ex, ey;
    if (x->cls == 2 || y->cls == 2) return x->cls == y->cls ? 0 : x->cls == 2 ? 1 : -1;
    ex = x->exponent + fl_msb(&x->sig) + 1;
    ey = y->exponent + fl_msb(&y->sig) + 1;
    if (ex != ey) return ex < ey ? -1 : 1;
    sx = x->sig; sy = y->sig;
    if (x->exponent > y->exponent) fl_shl(&sx, x->exponent - y->exponent);
    else fl_shl(&sy, y->exponent - x->exponent);
    return fl_cmp(&sx, &sy);
}

/* -1, 0 or 1; 2 when unordered (a NaN). +0 and -0 are equal. */
static int fl_compare(const fl_value *x, const fl_value *y) {
    int by_magnitude;
    if (x->cls == 3 || y->cls == 3) return 2;
    if (x->cls == 0 && y->cls == 0) return 0;
    if (x->cls == 0) return y->negative ? 1 : -1;
    if (y->cls == 0) return x->negative ? -1 : 1;
    if (x->negative != y->negative) return x->negative ? -1 : 1;
    by_magnitude = fl_compare_magnitudes(x, y);
    return x->negative ? -by_magnitude : by_magnitude;
}

LARVA_API void larva_float_add(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) {
    fl_format f = fl_format_of(64 * limbs);
    fl_value x, y;
    fl_unpack(&x, a, f);
    fl_unpack(&y, b, f);
    fl_add_values(result, f, &x, &y);
}

LARVA_API void larva_float_mul(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) {
    fl_format f = fl_format_of(64 * limbs);
    fl_value x, y;
    fl_unpack(&x, a, f);
    fl_unpack(&y, b, f);
    fl_mul_values(result, f, &x, &y);
}

LARVA_API void larva_float_div(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) {
    fl_format f = fl_format_of(64 * limbs);
    fl_value x, y;
    fl_unpack(&x, a, f);
    fl_unpack(&y, b, f);
    fl_div_values(result, f, &x, &y);
}

LARVA_API void larva_float_rem(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) {
    fl_format f = fl_format_of(64 * limbs);
    fl_value x, y;
    fl_unpack(&x, a, f);
    fl_unpack(&y, b, f);
    fl_rem_values(result, f, &x, &y);
}

LARVA_API void larva_float_sub(const uint64_t *a, const uint64_t *b, uint64_t *result, int limbs) {
    fl_format f = fl_format_of(64 * limbs);
    fl_value x, y;
    fl_unpack(&x, a, f);
    fl_unpack(&y, b, f);
    y.negative = !y.negative;
    fl_add_values(result, f, &x, &y);
}

/* 1 when the condition (0 ==, 1 !=, 2 <, 3 <=, 4 >, 5 >=) holds; a NaN makes every one false but !=. */
LARVA_API int32_t larva_float_cmp(const uint64_t *a, const uint64_t *b, int limbs, int condition) {
    fl_format f = fl_format_of(64 * limbs);
    fl_value x, y;
    int order;
    fl_unpack(&x, a, f);
    fl_unpack(&y, b, f);
    order = fl_compare(&x, &y);
    switch (condition) {
    case 0: return order == 0;
    case 1: return order != 0;
    case 2: return order == -1;
    case 3: return order == -1 || order == 0;
    case 4: return order == 1;
    default: return order == 1 || order == 0;
    }
}

/* Converts the value at `src` (type `from`) into `dst` (type `to`): a type is its width (12 bits), with 0x1000 for
 * signed and 0x2000 for float. A float to an integer truncates and wraps to the width (NaN and the infinities give 0). */
LARVA_API void larva_float_conv(const uint64_t *src, uint64_t *dst, int from, int to) {
    int from_bits = from & 0xFFF, to_bits = to & 0xFFF;
    int from_float = (from & 0x2000) != 0, to_float = (to & 0x2000) != 0;
    int from_signed = (from & 0x1000) != 0, to_signed = (to & 0x1000) != 0;
    int from_limbs = from_bits < 64 ? 1 : from_bits / 64, to_limbs = to_bits < 64 ? 1 : to_bits / 64;
    fl_big mag;
    int negative = 0, i;
    fl_clear(&mag);
    if (from_float) {
        fl_value v;
        fl_unpack(&v, src, fl_format_of(from_bits));
        if (to_float) { fl_repack(dst, fl_format_of(to_bits), &v); return; }
        if (v.cls == 1) {
            mag = v.sig;
            negative = v.negative;
            if (v.exponent >= 0) {
                if (v.exponent >= to_bits) fl_clear(&mag);
                else fl_shl(&mag, v.exponent);
            } else {
                fl_shr(&mag, -v.exponent);
            }
        }
        /* Wrapped to the width, in two's complement; then the canonical form of a narrow integer. */
        for (i = to_limbs; i < FL_WORDS; i++) mag.w[i] = 0;
        if (to_bits < 64) mag.w[0] &= ((uint64_t)1 << to_bits) - 1;
        if (negative && !fl_is_zero(&mag)) {
            for (i = 0; i < to_limbs; i++) mag.w[i] = ~mag.w[i];
            for (i = 0; i < to_limbs; i++) if (++mag.w[i]) break;
            if (to_bits < 64) mag.w[0] &= ((uint64_t)1 << to_bits) - 1;
        }
        if (to_bits < 64 && to_signed && ((mag.w[0] >> (to_bits - 1)) & 1)) mag.w[0] |= ~(uint64_t)0 << to_bits;
        for (i = 0; i < to_limbs; i++) dst[i] = mag.w[i];
        return;
    }
    for (i = 0; i < from_limbs; i++) mag.w[i] = src[i];
    if (from_bits < 64) {
        uint64_t mask = ((uint64_t)1 << from_bits) - 1;
        if (from_signed && ((mag.w[0] >> (from_bits - 1)) & 1)) mag.w[0] |= ~mask;
        else mag.w[0] &= mask;
    }
    if (from_signed && (mag.w[from_limbs - 1] >> 63)) {
        negative = 1;
        for (i = 0; i < from_limbs; i++) mag.w[i] = ~mag.w[i];
        for (i = 0; i < from_limbs; i++) if (++mag.w[i]) break;
    }
    fl_pack(dst, fl_format_of(to_bits), negative, &mag, 0, 0);
}

LARVA_API void larva_panic(int32_t code, int64_t a, int64_t b) {
    fflush(stdout);
    switch (code) {
    case 1: fprintf(stderr, "larva panic: index %lld out of bounds for length %lld\n", (long long)a, (long long)b); break;
    case 2: fputs("larva panic: division by zero\n", stderr); break;
    case 4: fprintf(stderr, "larva panic: invalid array length %lld\n", (long long)a); break;
    case 5: fputs("larva panic: null reference\n", stderr); break;
    default: fprintf(stderr, "larva panic: trap %d\n", code); break;
    }
    fflush(stderr);
    larva_exit(101);
}

#pragma optimize("s", on) /* cold: compiled for size (see COLD) */
/* ── Entry point ─────────────────────────────────────────────────── */

typedef int64_t (*EntryFn)(int64_t, int64_t, int64_t, int64_t);
typedef double (*EntryF64Fn)(int64_t, int64_t, int64_t, int64_t);
typedef int64_t (*EntryInfoFn)(void);

static int64_t uncaught_type;

#ifdef _WIN32
static int larva_filter(EXCEPTION_POINTERS *pointers) {
    EXCEPTION_RECORD *record = pointers->ExceptionRecord;
    if (record->ExceptionCode != LARVA_EXCEPTION || record->NumberParameters < 2)
        return EXCEPTION_CONTINUE_SEARCH;
    uncaught_type = (int64_t)record->ExceptionInformation[1];
    return EXCEPTION_EXECUTE_HANDLER;
}

static void print_stats(void) {
    int64_t live_heap = 0, live_gc = 0;
    for (size_t i = 0; i < live.capacity; i++) {
        if (live.entries[i].key > 1) {
            if (live.entries[i].kind == KIND_HEAP || live.entries[i].kind == KIND_PAYLOAD)
                live_heap++;
            else
                live_gc++;
        }
    }
    for (size_t c = 0; c < chunk_count; c++)
        for (uint32_t s = 0; s < chunks[c]->bump; s++)
            if (((GcHeader *)chunk_slot(chunks[c], s))->domain != SLOT_FREE)
                live_gc++;
    for (size_t index = 0; index < nursery_used_blocks; index++) {
        NurseryBlock *block = &nursery_blocks[index];
        if (block->state != BLOCK_RETAINED)
            continue;
        for (uint32_t at = 0; at < block->used;) {
            GcHeader *header = (GcHeader *)(block_start(index) + at);
            if (header->age != AGE_YOUNG && !(header->flags & FLAG_FORWARDED))
                live_gc++;
            at += (uint32_t)total_bytes(payload_bytes(header));
        }
    }
    fprintf(stderr, "larva stats: allocations %lld, frees %lld, live heap %lld, live gc %lld, collections %lld (%lld minor), collected %lld, errors %lld\n",
        (long long)allocations, (long long)frees, (long long)live_heap, (long long)live_gc, (long long)collections, (long long)minors,
        (long long)collected, (long long)errors);
    fprintf(stderr, "larva stats: minor collections %.1f ms, major %.1f ms, barrier calls %lld, promoted %lld, pinned %lld, retained blocks %lld\n",
        minor_ns / 1e6, major_ns / 1e6, (long long)barrier_calls, (long long)promoted_total, (long long)pinned_total, (long long)retained_blocks);
    fprintf(stderr, "larva stats: minor phases: roots %.1f ms, copy %.1f ms, blocks %.1f ms; live table %zu\n",
        minor_roots_ns / 1e6, minor_copy_ns / 1e6, minor_blocks_ns / 1e6, live.capacity);
    fprintf(stderr, "larva stats: remembered+cards part of copy %.1f ms\n", minor_cards_ns / 1e6);
    fprintf(stderr, "larva stats: safepoint calls %lld\n", (long long)poll_calls);
    fprintf(stderr, "larva stats: cards %lld, slots %lld, remembered %lld\n", (long long)cards_scanned, (long long)slots_evacuated, (long long)remembered_scanned);
}

static int run(EntryFn entry, EntryF64Fn entry_f64, int64_t *arguments, int kind, int64_t *result, double *real) {
    __try {
        if (kind == 4)
            *real = entry_f64(arguments[0], arguments[1], arguments[2], arguments[3]);
        else
            *result = entry(arguments[0], arguments[1], arguments[2], arguments[3]);
        return 0;
    } __except (larva_filter(GetExceptionInformation())) {
        return 1;
    }
}
#else
/* The exception in flight, set by compiled code (which also defines these, weakly). */
LARVA_API void *larva_exception;
LARVA_API int64_t larva_exception_type;

static int run(EntryFn entry, EntryF64Fn entry_f64, int64_t *arguments, int kind, int64_t *result, double *real) {
    if (kind == 4)
        *real = entry_f64(arguments[0], arguments[1], arguments[2], arguments[3]);
    else
        *result = entry(arguments[0], arguments[1], arguments[2], arguments[3]);
    if (!larva_exception)
        return 0;
    uncaught_type = larva_exception_type;
    return 1;
}
#endif

#ifdef _WIN32
/* A crash the program did not catch: where it happened and the frames above it (as image+offset, which the linker map
   that LARVA_MAP=1 asks for turns into function names). */
static LONG WINAPI report_crash(EXCEPTION_POINTERS *info) {
    fflush(stdout);
    EXCEPTION_RECORD *record = info->ExceptionRecord;
    fprintf(stderr, "larva crash: exception 0x%08lx", (unsigned long)record->ExceptionCode);
    if (record->ExceptionCode == EXCEPTION_ACCESS_VIOLATION && record->NumberParameters >= 2)
        fprintf(stderr, " (%s 0x%llx)", record->ExceptionInformation[0] ? "writing" : "reading", (unsigned long long)record->ExceptionInformation[1]);
    fputc('\n', stderr);
    CONTEXT context = *info->ContextRecord;
    fprintf(stderr, "  rax %llx rbx %llx rcx %llx rdx %llx rsi %llx rdi %llx\n  r8 %llx r9 %llx r12 %llx r13 %llx r14 %llx r15 %llx\n",
        (unsigned long long)context.Rax, (unsigned long long)context.Rbx, (unsigned long long)context.Rcx, (unsigned long long)context.Rdx,
        (unsigned long long)context.Rsi, (unsigned long long)context.Rdi, (unsigned long long)context.R8, (unsigned long long)context.R9,
        (unsigned long long)context.R12, (unsigned long long)context.R13, (unsigned long long)context.R14, (unsigned long long)context.R15);
    for (int frame = 0; frame < 24 && context.Rip != 0; frame++) {
        DWORD64 image_base = 0;
        PRUNTIME_FUNCTION function = RtlLookupFunctionEntry(context.Rip, &image_base, NULL);
        char name[MAX_PATH] = "?";
        if (image_base) {
            GetModuleFileNameA((HMODULE)image_base, name, sizeof name);
            const char *slash = strrchr(name, '\\');
            if (slash)
                memmove(name, slash + 1, strlen(slash));
        }
        fprintf(stderr, "  at %s+0x%llx\n", name, (unsigned long long)(context.Rip - image_base));
        if (!function)
            break;
        PVOID handler_data;
        DWORD64 establisher;
        RtlVirtualUnwind(UNW_FLAG_NHANDLER, image_base, context.Rip, function, &context, &handler_data, &establisher, NULL);
    }
    fflush(stderr);
    return EXCEPTION_CONTINUE_SEARCH;
}
#endif

/* The runtime's settings, from the environment (once, before any Larva code runs). */
static void runtime_settings(void) {
#ifdef _WIN32
    SetUnhandledExceptionFilter(report_crash);
#endif
#ifdef LARVA_DIAGNOSTICS
    const char *setting = getenv("LARVA_GC_STRESS");
    stress = setting && *setting && strcmp(setting, "0") != 0;
    setting = getenv("LARVA_GC_NOSMALL");
    small_disabled = setting && *setting && strcmp(setting, "0") != 0;
    setting = getenv("LARVA_GC_QUARANTINE");
    quarantine = setting && *setting && strcmp(setting, "0") != 0;
    setting = getenv("LARVA_GC_THRESHOLD");
    if (setting && atoll(setting) > 0)
    {
        threshold = atoll(setting);
        fixed_threshold = 1;
    }
    setting = getenv("LARVA_GC_VERIFY");
    verify = setting && *setting && strcmp(setting, "0") != 0;
    setting = getenv("LARVA_GC_GENERATIONS");
    generations_off = setting && strcmp(setting, "0") == 0;
    setting = getenv("LARVA_GC_NURSERY");
    if (setting && atoll(setting) > 0)
        nursery_target = (size_t)atoll(setting);
    setting = getenv("LARVA_STATS");
    stats_enabled = setting && *setting && strcmp(setting, "0") != 0;
    setting = getenv("LARVA_GC_THREADS");
    if (setting && atoll(setting) > 0)
        gc_threads_setting = (int)atoll(setting);
#endif
    nursery_disabled = small_disabled || quarantine;
    update_fast();
    update_poll_pending();
}

#if defined(_WIN32) && !defined(LARVA_SHARED_RUNTIME)
/*
 * A native library built from Larva code (functions with `[Export]`): the runtime reads its settings when the library
 * is loaded. An executable never calls this (its entry is main).
 */
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    (void)instance;
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
        runtime_settings();
    return TRUE;
}
#endif

/* Runs the program: arguments from the command line, the result printed, settings from the environment. */
static int program_main(int argc, char **argv, EntryFn entry, EntryF64Fn entry_f64, EntryInfoFn entry_info) {
    runtime_settings();
    main_record.os = 0;
    int64_t info = entry_info();
    int arity = (int)(info & 0xFF), kind = (int)(info >> 8 & 0xFF), text = (int)(info >> 16 & 1);
    int counting_only = (int)(info >> 17 & 1);
    counted_program = (int)(info >> 18 & 1);
    if (counting_only) {
        /* Nothing traces (reference counting alone): no nursery, every object is old and freed when its count reaches 0. */
        nursery_disabled = 1;
        update_fast();
    }
    keep_arguments(argc, argv);
    if (!text && argc - 1 != arity) {
        fprintf(stderr, "usage: %s%s (%d integer argument%s)\n", argv[0], arity ? " <arguments>" : "", arity, arity == 1 ? "" : "s");
        return 2;
    }
    int64_t arguments[4] = {0, 0, 0, 0};
    for (int i = 0; i < arity; i++) {
        const char *text = argv[i + 1];
        if (strcmp(text, "true") == 0 || strcmp(text, "True") == 0)
            arguments[i] = 1;
        else if (strcmp(text, "false") == 0 || strcmp(text, "False") == 0)
            arguments[i] = 0;
        else
            arguments[i] = _strtoi64(text, NULL, 10);
    }

    int64_t result = 0;
    double real = 0;
    if (run(entry, entry_f64, arguments, kind, &result, &real) != 0) {
        fflush(stdout);
        fprintf(stderr, "larva: unhandled exception (type id %lld)\n", (long long)uncaught_type);
        return 1;
    }
    switch (kind) {
    case 1: printf("%lld\n", (long long)result); break;
    case 2: printf("%llu\n", (unsigned long long)result); break;
    case 3: puts(result ? "True" : "False"); break;
    case 4: printf("%.17g\n", real); break;
    default: break;
    }
    fflush(stdout);
    if (stats_enabled) {
        if (!counting_only)
            collect(0);  /* (counting alone: what is still live is what the counts kept, leaks included) */
        print_stats();
    }
    return kind == 5 ? (int)result : 0; /* a program's result is its exit code */
}

#if defined(LARVA_SHARED_RUNTIME) && !defined(_WIN32)

/* A program linked against liblarva_rt.so starts here: its main hands over the command line and its entry wrappers. */
LARVA_API int larva_run_main_args(int argc, char **argv, EntryFn entry, EntryF64Fn entry_f64, EntryInfoFn entry_info) {
    return program_main(argc, argv, entry, entry_f64, entry_info);
}

#elif defined(LARVA_SHARED_RUNTIME)

/*
 * A program linked against larva_rt.dll starts here (its own entry only calls this): the command line comes from
 * Windows, since the runtime's C library does not parse it for a DLL.
 */
LARVA_API int larva_run_main(EntryFn entry, EntryF64Fn entry_f64, EntryInfoFn entry_info) {
    int argc = 0;
    LPWSTR *wide = CommandLineToArgvW(GetCommandLineW(), &argc);
    char **argv = (char **)calloc((size_t)argc + 1, sizeof(char *));
    if (!wide || !argv)
        fail_alloc();
    for (int i = 0; i < argc; i++) {
        int length = WideCharToMultiByte(CP_UTF8, 0, wide[i], -1, NULL, 0, NULL, NULL);
        argv[i] = (char *)malloc((size_t)length);
        if (!argv[i])
            fail_alloc();
        WideCharToMultiByte(CP_UTF8, 0, wide[i], -1, argv[i], length, NULL, NULL);
    }
    LocalFree(wide);
    return program_main(argc, argv, entry, entry_f64, entry_info);
}

#else

/* Generated by the compiler for the program's entry function (arguments and result widened to 64 bits). */
extern int64_t larva_entry(int64_t a0, int64_t a1, int64_t a2, int64_t a3);
extern double larva_entry_f64(int64_t a0, int64_t a1, int64_t a2, int64_t a3);
/* arity | result kind << 8 (0 unit, 1 signed, 2 unsigned, 3 bool, 4 float) | argument kinds << 16 (2 bits each). */
extern int64_t larva_entry_info(void);

int main(int argc, char **argv) {
    return program_main(argc, argv, larva_entry, larva_entry_f64, larva_entry_info);
}

#endif

#ifdef LARVA_NO_CRT
/*
 * The executable's entry point (no C library starts it): the command line split as the C library split it (spaces apart
 * outside quotes; 2n backslashes and a quote: n backslashes and a quote that opens or closes; 2n+1: n and a literal quote;
 * "" inside quotes: a literal quote), each argument in UTF-8, then main; what it returns is the exit code.
 */
static char *rt_utf8(const wchar_t *text, int length) {
    int size = WideCharToMultiByte(CP_UTF8, 0, text, length, NULL, 0, NULL, NULL);
    char *bytes = (char *)rt_malloc((size_t)size + 1);
    if (!bytes)
        fail_alloc();
    WideCharToMultiByte(CP_UTF8, 0, text, length, bytes, size, NULL, NULL);
    bytes[size] = 0;
    return bytes;
}

static char **rt_arguments(int *count) {
    const wchar_t *line = GetCommandLineW();
    size_t length = wcslen(line);
    char **arguments = (char **)rt_calloc(length / 2 + 2, sizeof(char *));
    wchar_t *word = (wchar_t *)rt_malloc((length + 1) * sizeof(wchar_t));
    if (!arguments || !word)
        fail_alloc();
    int n = 0;
    const wchar_t *at = line;
    /* The program's name: up to the closing quote, or the first space (no escapes). */
    int w = 0;
    if (*at == L'"') {
        at++;
        while (*at && *at != L'"')
            word[w++] = *at++;
        if (*at)
            at++;
    } else {
        while (*at && *at != L' ' && *at != L'\t')
            word[w++] = *at++;
    }
    arguments[n++] = rt_utf8(word, w);
    for (;;) {
        while (*at == L' ' || *at == L'\t')
            at++;
        if (!*at)
            break;
        w = 0;
        int quoted = 0;
        while (*at && (quoted || (*at != L' ' && *at != L'\t'))) {
            if (*at == L'\\') {
                int slashes = 0;
                while (*at == L'\\') {
                    slashes++;
                    at++;
                }
                if (*at == L'"') {
                    for (int i = 0; i < slashes / 2; i++)
                        word[w++] = L'\\';
                    if (slashes % 2) {
                        word[w++] = L'"';
                        at++;
                    }
                } else {
                    for (int i = 0; i < slashes; i++)
                        word[w++] = L'\\';
                }
            } else if (*at == L'"') {
                if (quoted && at[1] == L'"') {
                    word[w++] = L'"';
                    at += 2;
                } else {
                    quoted = !quoted;
                    at++;
                }
            } else {
                word[w++] = *at++;
            }
        }
        arguments[n++] = rt_utf8(word, w);
    }
    rt_free(word);
    *count = n;
    return arguments;
}

void __stdcall larva_crt_start(void) {
    int count;
    char **arguments = rt_arguments(&count);
    rt_exit(main(count, arguments));
}
#endif
#pragma optimize("", on)
