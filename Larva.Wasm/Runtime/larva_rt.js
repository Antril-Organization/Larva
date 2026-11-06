// Larva runtime for WebAssembly: loads __LARVA_WASM__ and provides what it imports (the larva_* functions of the native
// runtime): heap cells and gc objects in the module's memory, a non-moving mark-sweep collector that scans the shadow
// stack, the globals and heap cells conservatively, reference counting, arena regions, printing and panics.
// Two code shapes: "uniform" (Larva's own back end: 32-bit memory, every function takes (args, ret)) and "c64" (LLVM:
// 64-bit memory, the C calling convention, pointers as BigInt).
// `node NAME.js [args]` runs the program; a page does `import { run } from "./NAME.js"`.
// Environment (Node): LARVA_GC_STRESS=1 collects at every allocation, LARVA_GC_THRESHOLD=n collects every n allocations.

const GC_MODE = "__LARVA_GC__";
const ABI = "__LARVA_ABI__";

class LarvaPanic extends Error {}

// A float's bits as a BigInt (how floats travel to and from the runtime), and back.
const floatView = new DataView(new ArrayBuffer(8));
const toBits = value => { floatView.setFloat64(0, value); return floatView.getBigInt64(0); };
const fromBits = bits => { floatView.setBigInt64(0, BigInt.asIntN(64, BigInt(bits))); return floatView.getFloat64(0); };

/**
 * Runs the program: options.args (strings), options.write(text) for output, options.error(text) for errors,
 * options.readLine() for input (a string, or null at the end; Node reads stdin, a page asks with prompt()).
 */
export async function run(options = {}) {
    const node = typeof process !== "undefined" && process.versions?.node;
    const write = options.write ?? (text => process.stdout.write(text));
    const error = options.error ?? (node ? text => process.stderr.write(text) : write);
    const env = node ? process.env : {};
    const stress = options.stress ?? (env.LARVA_GC_STRESS && env.LARVA_GC_STRESS !== "0");
    const threshold = options.threshold ?? (Number(env.LARVA_GC_THRESHOLD) || 0);
    const c64 = ABI === "c64";
    const N = c64 ? Number : x => x;           // an address or size from the module
    const P = c64 ? BigInt : x => x;           // an address to the module

    let output = "";
    const flush = () => { if (output) { write(output); output = ""; } };

    // ── Input and the command line: read by the program one UTF-16 unit at a time ──
    const fs = node && !options.readLine ? await import("node:fs") : null;
    const decoder = new TextDecoder();
    const readLine = options.readLine ?? (fs ? () => {
        const collected = [], one = new Uint8Array(1);
        for (;;) {
            let n;
            try { n = fs.readSync(0, one, 0, 1, null); } catch (e) { if (e.code === "EAGAIN") continue; n = 0; }
            if (n === 0) return collected.length ? decoder.decode(new Uint8Array(collected)).replace(/\r$/, "") : null;
            if (one[0] === 10) return decoder.decode(new Uint8Array(collected)).replace(/\r$/, "");
            collected.push(one[0]);
        }
    } : () => globalThis.prompt?.() ?? null);
    let line = null, formatted = "";
    // System.IO: Node's file system (a page has none: reading fails, nothing is written).
    const files = node ? await import("node:fs") : null;
    const shell = node ? await import("node:child_process") : null;
    let fileBuffer = new Uint8Array(0), folderEntries = [], environmentValue = null;
    const positions = new Map(); // stream handle → position
    const attempt = action => { try { action(); return 0n; } catch { return -1n; } };
    const words = (options.args ?? (node ? process.argv.slice(2) : [])).map(String);
    const unitOf = (text, at) => { at = Number(at); return text !== null && at >= 0 && at < text.length ? BigInt(text.charCodeAt(at)) : 0n; };
    const print = text => { output += text; if (output.length > 8192) flush(); };

    let memory, table, exp, dv, bytes, words32;
    let threadsMade = 1n;
    const refresh = () => { dv = new DataView(memory.buffer); bytes = new Uint8Array(memory.buffer); words32 = new Uint32Array(memory.buffer); };

    // ── Heap: 16-byte blocks from a bump pointer, reused by size. What the runtime knows of each block is in typed arrays
    // indexed by its address / 16: its kind (below), size, reference count and region; finalizers in a map (rare). ──
    const LIVE = 1, GC = 2, MARK = 4, LISTED = 8; // LISTED: the block's address is in `objects` (maybe from an earlier object)
    let base = 0, top = 0;
    let kinds = new Uint8Array(0), sizes = new Uint32Array(0), counts = new Int32Array(0), owners = new Int32Array(0);
    const finalizers = new Map(); // gc object → finalizer's table index
    const freeSmall = [], freeLarge = new Map(); // free blocks by size in 16-byte units (small: under 256)
    let objects = new Uint32Array(4096), objectCount = 0; // the gc objects (and blocks that were one: dropped at the sweep)
    const cells = new Set(); // the other blocks: roots for the collector
    const pins = new Map();
    const regions = [];
    // A collection after `budget` bytes of gc objects (as many as survived the last one, at least 4 MB), or, with a
    // threshold given, after that many allocations.
    const MIN_BUDGET = 4 << 20;
    let since = 0, sinceBytes = 0, budget = MIN_BUDGET, liveBytes = 0, finalizing = 0, collections = 0;

    // ── The heap compiled code shares (LLVM: larva_wasm_heap, see wasm_heap.ll): it allocates small gc objects itself, from
    // free lists by size and a bump region, and logs them; everything here reads the log first (`takeLog`). Without it
    // (Larva's own back end) the free lists are JavaScript arrays and every allocation comes here. ──
    let shared = -1; // its address
    const CHUNK = 256 << 10;
    const read64 = at => words32[at >>> 2] + words32[(at >>> 2) + 1] * 4294967296;
    const write64 = (at, value) => { words32[at >>> 2] = value >>> 0; words32[(at >>> 2) + 1] = Math.floor(value / 4294967296); };
    const listPop = n => {
        if (shared < 0) { const list = freeSmall[n]; return list !== undefined && list.length ? list.pop() : 0; }
        const head = shared + 40 + 8 * n, at = read64(head);
        if (at) write64(head, read64(at));
        return at;
    };
    const listPush = (n, at) => {
        if (shared < 0) { (freeSmall[n] ??= []).push(at); return; }
        const head = shared + 40 + 8 * n;
        write64(at, read64(head));
        write64(head, at);
    };
    // What compiled code may allocate before coming here: nothing while a collection is due at every allocation or after
    // a count of them, or while a region is open (its objects belong to it).
    const share = () => {
        if (shared < 0) return;
        const open = !tracing ? Number.MAX_SAFE_INTEGER : stress || threshold || regions.length ? 0 : budget;
        write64(shared + 16, sinceBytes);
        write64(shared + 24, open);
    };
    const takeLog = () => {
        if (shared < 0) return;
        const count = read64(shared + 32);
        if (count === 0) return;
        for (let j = 0, entry = shared + 2088; j < count; j++, entry += 16) register(read64(entry), read64(entry + 8));
        // Cleared: the log is in the globals the collector scans, where old addresses would keep objects alive.
        words32.fill(0, (shared + 2088) >>> 2, (shared + 2088 + 16 * count) >>> 2);
        write64(shared + 32, 0);
        sinceBytes = read64(shared + 16);
    };
    // A new bump region for compiled code when little is left of the old one (whose rest goes to the free lists).
    const refill = () => {
        let cursor = read64(shared), limit = read64(shared + 8);
        if (limit - cursor >= 4096) return;
        while (cursor < limit) {
            const n = Math.min(255, (limit - cursor) >>> 4);
            sizes[cursor >>> 4] = 16 * n;
            listPush(n, cursor);
            cursor += 16 * n;
        }
        const at = top;
        top += CHUNK;
        grow();
        write64(shared, at);
        write64(shared + 8, at + CHUNK);
    };

    const growFacts = need => {
        const n = Math.max(need, kinds.length * 2, 1 << 16);
        const grown = (Kind, old) => { const a = new Kind(n); a.set(old); return a; };
        kinds = grown(Uint8Array, kinds); sizes = grown(Uint32Array, sizes); counts = grown(Int32Array, counts); owners = grown(Int32Array, owners);
    };
    const block = size => {
        const n = (size + 15) >>> 4 || 1;
        let at = 0;
        if (n < 256) at = listPop(n);
        else { const list = freeLarge.get(n); if (list !== undefined && list.length) at = list.pop(); }
        if (at) {
            if (n <= 8) for (let w = at >>> 2, end = w + 4 * n; w < end; w++) words32[w] = 0;
            else bytes.fill(0, at, at + 16 * n);
            return at;
        }
        at = top;
        top += 16 * n;
        grow();
        return at;
    };
    // Memory and the facts up to `top`.
    function grow() {
        if (top > memory.buffer.byteLength) {
            const pages = Math.ceil((top - memory.buffer.byteLength) / 65536) + 16;
            memory.grow(c64 ? BigInt(pages) : pages);
            refresh();
        }
        if (top >>> 4 > kinds.length) growFacts(top >>> 4);
    }
    // Frees a block (an object's address stays in `objects`, marked LISTED, until the next sweep drops it).
    const release = at => {
        const i = at >>> 4, n = (sizes[i] + 15) >>> 4 || 1;
        kinds[i] &= LISTED;
        if (finalizers.size) finalizers.delete(at);
        if (n < 256) { listPush(n, at); return; }
        let list = freeLarge.get(n);
        if (list === undefined) freeLarge.set(n, list = []);
        list.push(at);
    };
    const alloc = size => {
        const at = block(size), i = at >>> 4;
        kinds[i] = LIVE | (kinds[i] & LISTED);
        sizes[i] = size || 1;
        cells.add(at);
        return at;
    };
    const tracing = GC_MODE === "default";
    const due = () => stress || (threshold ? since >= threshold : sinceBytes >= budget);
    const poll = () => { if (tracing && finalizing === 0 && due()) collect(); };
    const gcAlloc = size => {
        takeLog();
        since++;
        sinceBytes += size;
        if (tracing && finalizing === 0 && due()) collect();
        const at = block(size);
        register(at, size);
        owners[at >>> 4] = regions.length ? regions[regions.length - 1] + 1 : 0;
        if (shared >= 0) { refill(); share(); }
        return at;
    };
    // A new gc object's facts (outside regions).
    function register(at, size) {
        const i = at >>> 4;
        const listed = kinds[i] & LISTED;
        kinds[i] = LIVE | GC | LISTED;
        sizes[i] = size || 1;
        counts[i] = 1;
        owners[i] = 0;
        if (!listed) {
            if (objectCount === objects.length) { const grown = new Uint32Array(objects.length * 2); grown.set(objects); objects = grown; }
            objects[objectCount++] = at;
        }
    }
    // Whether `at` is a live gc object's address.
    const isObject = at => at >= base && at < top && (at & 15) === 0 && (kinds[at >>> 4] & (LIVE | GC)) === (LIVE | GC);

    // The exception in flight (a finalizer's own is dropped).
    const exceptionAt = () => c64 ? Number(exp.larva_exception.value) : 0;
    const pending = () => c64 ? dv.getBigUint64(exceptionAt(), true) !== 0n : exp.exception.value !== 0;
    const pendingType = () => c64 ? dv.getBigInt64(Number(exp.larva_exception_type.value), true) : exp.exception_type.value;
    const clearPending = () => { if (c64) dv.setBigUint64(exceptionAt(), 0n, true); else exp.exception.value = 0; };

    const callFinalizer = (index, payload) => {
        if (c64) {
            try { table.get(typeof table.length === "bigint" ? BigInt(index) : index)(BigInt(payload)); } finally { clearPending(); }
            return;
        }
        const sp = exp.sp.value - 16;
        dv.setBigUint64(sp, BigInt(payload), true);
        exp.sp.value = sp;
        try { table.get(index)(sp, sp + 8); } finally { exp.sp.value = sp + 16; clearPending(); }
    };
    const finalize = at => {
        const index = finalizers.get(at);
        if (!index || !isObject(at)) return;
        finalizers.delete(at);
        finalizing++;
        try { callFinalizer(index, at); } finally { finalizing--; }
    };

    // ── Collection: conservative roots (any 4-byte word that is a live object's address) ──
    const marks = [];
    const consider = word => {
        if (word >= base && word < top && (word & 15) === 0) {
            const i = word >>> 4;
            if ((kinds[i] & (LIVE | GC | MARK)) === (LIVE | GC)) { kinds[i] |= MARK; marks.push(word); }
        }
    };
    const scan = (start, size) => {
        const from = base, to = top, end = (start + size) >>> 2;
        for (let p = start >>> 2; p < end; p++) {
            const word = words32[p];
            if (word >= from && word < to && (word & 15) === 0) {
                const i = word >>> 4;
                if ((kinds[i] & (LIVE | GC | MARK)) === (LIVE | GC)) { kinds[i] |= MARK; marks.push(word); }
            }
        }
    };
    const drain = () => { while (marks.length) { const at = marks.pop(); scan(at, sizes[at >>> 4]); } };
    const roots = () => c64
        ? [[Number(exp.__stack_pointer.value), Number(exp.__stack_high.value)], [Number(exp.__stack_high.value), Number(exp.__data_end.value)]]
        : [[exp.sp.value, exp.stack_top.value], [exp.globals_start.value, exp.globals_end.value]];
    function collect() {
        takeLog();
        collections++;
        for (const [from, to] of roots()) scan(from, to - from);
        for (const at of cells) scan(at, sizes[at >>> 4]);
        for (const at of pins.keys()) consider(at);
        drain();
        // Unreachable objects with a finalizer survive this time (with what they reach); their finalizers run after.
        const dying = [];
        for (const at of finalizers.keys())
            if ((kinds[at >>> 4] & (LIVE | GC | MARK)) === (LIVE | GC) && owners[at >>> 4] === 0) dying.push(at);
        dying.forEach(consider);
        drain();
        // The sweep: unmarked objects outside regions are freed; the list keeps the others.
        let kept = 0;
        liveBytes = 0;
        for (let j = 0; j < objectCount; j++) {
            const at = objects[j], i = at >>> 4, kind = kinds[i];
            if ((kind & (LIVE | GC)) !== (LIVE | GC)) { kinds[i] = kind & ~LISTED; continue; }
            if (kind & MARK) kinds[i] = kind & ~MARK;
            else if (owners[i] === 0) { release(at); kinds[i] = 0; continue; }
            objects[kept++] = at;
            liveBytes += sizes[i];
        }
        objectCount = kept;
        since = 0;
        sinceBytes = 0;
        budget = Math.max(MIN_BUDGET, liveBytes);
        share();
        for (const at of dying) finalize(at);
    }

    const freeRegion = region => {
        takeLog();
        const dying = [];
        for (let j = 0; j < objectCount; j++) { const at = objects[j]; if (isObject(at) && owners[at >>> 4] === region + 1) dying.push(at); }
        dying.forEach(at => finalize(at));
        dying.forEach(at => isObject(at) && release(at));
    };

    // ── Printing ──
    const scratch = new DataView(new ArrayBuffer(8));
    const general = (x, digits) => {
        if (Number.isNaN(x)) return "nan";
        if (x === Infinity) return "inf";
        if (x === -Infinity) return "-inf";
        if (x === 0) return Object.is(x, -0) ? "-0" : "0";
        const [mantissa, e] = x.toExponential(digits - 1).split("e");
        const exponent = Number(e);
        if (exponent < -4 || exponent >= digits) {
            const m = mantissa.includes(".") ? mantissa.replace(/\.?0+$/, "") : mantissa;
            return `${m}e${exponent < 0 ? "-" : "+"}${String(Math.abs(exponent)).padStart(2, "0")}`;
        }
        const fixed = x.toFixed(Math.max(0, digits - 1 - exponent));
        return fixed.includes(".") ? fixed.replace(/\.?0+$/, "") : fixed;
    };
    const bitsToFloat = bits => { scratch.setBigInt64(0, bits, true); return scratch.getFloat64(0, true); };
    const text = at => {
        const length = Number(dv.getBigInt64(at, true));
        let s = "";
        for (let i = 0; i < length; i += 4096)
            s += String.fromCharCode(...new Uint16Array(memory.buffer.slice(at + 8 + 2 * i, at + 8 + 2 * Math.min(length, i + 4096))));
        return s;
    };


    // ── Floats in software (SoftFloat.cs, bit for bit: to nearest, ties to even; canonical quiet NaN): f128 and f256 for
    // the LLVM build (`larva_float_*`, values as 64-bit limbs in memory, lowest first), and the f16 conversions LLVM calls
    // by compiler-rt's names (a half travels as its 16 bits in an i32). Classes: 0 zero, 1 finite, 2 infinity, 3 NaN. ──
    const formats = {};
    const fmt = bits => formats[bits] || (formats[bits] = (() => {
        const [e, fr] = { 16: [5, 10], 32: [8, 23], 64: [11, 52], 128: [15, 112], 256: [19, 236] }[bits];
        const bias = (1 << (e - 1)) - 1, sign = 1n << BigInt(bits - 1), top = BigInt((1 << e) - 1) << BigInt(fr);
        return { bits, fr, bias, min: 1 - bias, max: bias, emask: (1 << e) - 1, sign, fmask: (1n << BigInt(fr)) - 1n, mask: (1n << BigInt(bits)) - 1n,
                 nan: top | (1n << BigInt(fr - 1)), inf: negative => (negative ? sign : 0n) | top, zero: negative => negative ? sign : 0n };
    })());
    const bitLength = n => n === 0n ? 0 : n.toString(2).length;
    const unpack = (bits, f) => {
        bits &= f.mask;
        const negative = (bits & f.sign) !== 0n;
        const e = Number((bits >> BigInt(f.fr)) & BigInt(f.emask));
        const fraction = bits & f.fmask;
        if (e === f.emask) return { negative, cls: fraction === 0n ? 2 : 3, sig: 0n, exp: 0 };
        if (e === 0) return fraction === 0n ? { negative, cls: 0, sig: 0n, exp: 0 } : { negative, cls: 1, sig: fraction, exp: f.min - f.fr };
        return { negative, cls: 1, sig: fraction | (1n << BigInt(f.fr)), exp: e - f.bias - f.fr };
    };
    // magnitude × 2^exponent rounded into the format (sticky: nonzero bits below the magnitude's lowest one).
    const pack = (negative, magnitude, exponent, sticky, f) => {
        if (magnitude === 0n) return f.zero(negative);
        const msb = bitLength(magnitude) - 1;
        let e = exponent + msb, shift = msb - f.fr;
        if (e < f.min) { shift += f.min - e; e = f.min; }
        if (shift <= 0) magnitude <<= BigInt(-shift);
        else {
            const s = BigInt(shift);
            const round = ((magnitude >> (s - 1n)) & 1n) !== 0n;
            const below = sticky || (magnitude & ((1n << (s - 1n)) - 1n)) !== 0n;
            magnitude >>= s;
            if (round && (below || (magnitude & 1n) !== 0n)) {
                magnitude += 1n;
                if (bitLength(magnitude) > f.fr + 1) { magnitude >>= 1n; e++; }
            }
        }
        if (e > f.max) return f.inf(negative);
        const sign = negative ? f.sign : 0n;
        if ((magnitude >> BigInt(f.fr)) === 0n) return sign | magnitude;
        return sign | (BigInt(e + f.bias) << BigInt(f.fr)) | (magnitude & f.fmask);
    };
    const repack = (x, f) => x.cls === 3 ? f.nan : x.cls === 2 ? f.inf(x.negative) : x.cls === 0 ? f.zero(x.negative) : pack(x.negative, x.sig, x.exp, false, f);
    const fadd = (x, y, f) => {
        if (x.cls === 3 || y.cls === 3) return f.nan;
        if (x.cls === 2) return y.cls === 2 && x.negative !== y.negative ? f.nan : f.inf(x.negative);
        if (y.cls === 2) return f.inf(y.negative);
        if (x.cls === 0) return y.cls === 0 ? f.zero(x.negative && y.negative) : repack(y, f);
        if (y.cls === 0) return repack(x, f);
        if (x.exp < y.exp) [x, y] = [y, x];
        const sx = x.sig << 64n;
        let sy = y.sig << 64n, sticky = false;
        const d = x.exp - y.exp;
        if (d > 0) {
            if (d >= bitLength(sy)) { sticky = sy !== 0n; sy = 0n; }
            else { sticky = (sy & ((1n << BigInt(d)) - 1n)) !== 0n; sy >>= BigInt(d); }
        }
        const exponent = x.exp - 64;
        if (x.negative === y.negative) return pack(x.negative, sx + sy, exponent, sticky, f);
        if (sx >= sy) { const m = sticky ? sx - sy - 1n : sx - sy; return m === 0n && !sticky ? f.zero(false) : pack(x.negative, m, exponent, sticky, f); }
        return pack(y.negative, sy - sx, exponent, sticky, f);
    };
    const fsub = (x, y, f) => fadd(x, { negative: !y.negative, cls: y.cls, sig: y.sig, exp: y.exp }, f);
    const fmul = (x, y, f) => {
        if (x.cls === 3 || y.cls === 3) return f.nan;
        const negative = x.negative !== y.negative;
        if (x.cls === 2 || y.cls === 2) return x.cls === 0 || y.cls === 0 ? f.nan : f.inf(negative);
        if (x.cls === 0 || y.cls === 0) return f.zero(negative);
        return pack(negative, x.sig * y.sig, x.exp + y.exp, false, f);
    };
    const fdiv = (x, y, f) => {
        if (x.cls === 3 || y.cls === 3) return f.nan;
        const negative = x.negative !== y.negative;
        if (x.cls === 2) return y.cls === 2 ? f.nan : f.inf(negative);
        if (y.cls === 2) return f.zero(negative);
        if (y.cls === 0) return x.cls === 0 ? f.nan : f.inf(negative);
        if (x.cls === 0) return f.zero(negative);
        const extra = 2 * f.fr + 4, dividend = x.sig << BigInt(extra);
        return pack(negative, dividend / y.sig, x.exp - y.exp - extra, dividend % y.sig !== 0n, f);
    };
    const frem = (x, y, f) => {
        if (x.cls === 3 || y.cls === 3 || x.cls === 2 || y.cls === 0) return f.nan;
        if (y.cls === 2 || x.cls === 0) return repack(x, f);
        const e = Math.min(x.exp, y.exp);
        const r = (x.sig << BigInt(x.exp - e)) % (y.sig << BigInt(y.exp - e));
        return r === 0n ? f.zero(x.negative) : pack(x.negative, r, e, false, f);
    };
    // -1, 0 or 1; null when either is NaN. +0 and -0 are equal.
    const fcompare = (x, y) => {
        if (x.cls === 3 || y.cls === 3) return null;
        if (x.cls === 0 && y.cls === 0) return 0;
        if (x.cls === 0) return y.negative ? 1 : -1;
        if (y.cls === 0) return x.negative ? -1 : 1;
        if (x.negative !== y.negative) return x.negative ? -1 : 1;
        let m;
        if (x.cls === 2 || y.cls === 2) m = x.cls === y.cls ? 0 : x.cls === 2 ? 1 : -1;
        else {
            const ex = x.exp + bitLength(x.sig), ey = y.exp + bitLength(y.sig);
            if (ex !== ey) m = ex < ey ? -1 : 1;
            else {
                let sx = x.sig, sy = y.sig;
                if (x.exp > y.exp) sx <<= BigInt(x.exp - y.exp); else sy <<= BigInt(y.exp - x.exp);
                m = sx < sy ? -1 : sx > sy ? 1 : 0;
            }
        }
        return x.negative ? -m : m;
    };
    const fromInteger = (v, f) => v === 0n ? f.zero(false) : pack(v < 0n, v < 0n ? -v : v, 0, false, f);
    const toInteger = (bits, f) => {
        const x = unpack(bits, f);
        if (x.cls !== 1) return 0n;
        const whole = x.exp >= 0 ? x.sig << BigInt(x.exp) : x.sig >> BigInt(-x.exp);
        return x.negative ? -whole : whole;
    };
    const limbsRead = (at, limbs, signed) => {
        at = N(at);
        let v = 0n;
        for (let i = limbs - 1; i >= 0; i--) v = (v << 64n) | dv.getBigUint64(at + 8 * i, true);
        return signed ? BigInt.asIntN(64 * limbs, v) : v;
    };
    const limbsWrite = (at, limbs, v) => {
        at = N(at);
        v = BigInt.asUintN(64 * limbs, v);
        for (let i = 0; i < limbs; i++) dv.setBigUint64(at + 8 * i, BigInt.asUintN(64, v >> BigInt(64 * i)), true);
    };
    const wideBinary = op => (a, b, result, limbs) => {
        const f = fmt(64 * limbs);
        limbsWrite(result, limbs, op(unpack(limbsRead(a, limbs, false), f), unpack(limbsRead(b, limbs, false), f), f));
    };
    // A type to larva_float_conv: its width (12 bits), 0x1000 when signed, 0x2000 for a float. Integers up to 64 bits travel
    // in their canonical 64-bit form (sign-extended when signed).
    const floatConvert = (source, target, from, to) => {
        const fromBits = from & 0xFFF, toBits = to & 0xFFF, fromSigned = (from & 0x1000) !== 0, toSigned = (to & 0x1000) !== 0;
        const fromLimbs = Math.max(1, fromBits >> 6), toLimbs = Math.max(1, toBits >> 6);
        if (from & 0x2000) {
            const f = fmt(fromBits), bits = limbsRead(source, fromLimbs, false) & f.mask;
            if (to & 0x2000) { limbsWrite(target, toLimbs, repack(unpack(bits, f), fmt(toBits))); return; }
            const wrapped = toSigned ? BigInt.asIntN(toBits, toInteger(bits, f)) : BigInt.asUintN(toBits, toInteger(bits, f));
            if (toBits >= 64) limbsWrite(target, toLimbs, wrapped);
            else dv.setBigUint64(N(target), BigInt.asUintN(64, wrapped), true);
            return;
        }
        let value = limbsRead(source, fromLimbs, fromSigned);
        if (fromBits < 64) value = fromSigned ? BigInt.asIntN(fromBits, value) : BigInt.asUintN(fromBits, value);
        limbsWrite(target, toLimbs, fromInteger(value, fmt(toBits)));
    };
    const halfView = new DataView(new ArrayBuffer(8));
    const toHalf = (bits, from) => Number(repack(unpack(bits, fmt(from)), fmt(16)));
    const fromHalf = (half, to) => repack(unpack(BigInt(half & 0xFFFF), fmt(16)), fmt(to));
    const softFloat = {
        larva_float_add: wideBinary(fadd), larva_float_sub: wideBinary(fsub), larva_float_mul: wideBinary(fmul),
        larva_float_div: wideBinary(fdiv), larva_float_rem: wideBinary(frem),
        // 1 when the condition (0 ==, 1 !=, 2 <, 3 <=, 4 >, 5 >=) holds; NaN makes every one false but !=.
        larva_float_cmp: (a, b, limbs, condition) => {
            const f = fmt(64 * limbs);
            const order = fcompare(unpack(limbsRead(a, limbs, false), f), unpack(limbsRead(b, limbs, false), f));
            if (order === null) return condition === 1 ? 1 : 0;
            return [order === 0, order !== 0, order < 0, order <= 0, order > 0, order >= 0][condition] ? 1 : 0;
        },
        larva_float_conv: floatConvert,
        __truncdfhf2: x => { halfView.setFloat64(0, x, true); return toHalf(halfView.getBigUint64(0, true), 64); },
        __truncsfhf2: x => { halfView.setFloat32(0, x, true); return toHalf(BigInt(halfView.getUint32(0, true)), 32); },
        __extendhfsf2: half => { halfView.setUint32(0, Number(fromHalf(half, 32)), true); return halfView.getFloat32(0, true); },
        __extendhfdf2: half => { halfView.setBigUint64(0, fromHalf(half, 64), true); return halfView.getFloat64(0, true); },
    };
    softFloat.__gnu_f2h_ieee = softFloat.__truncsfhf2;
    softFloat.__gnu_h2f_ieee = softFloat.__extendhfsf2;

    const wide128 = (low, high) => (BigInt.asUintN(64, high) << 64n) | BigInt.asUintN(64, low);
    const write128 = (result, value) => {
        value = BigInt.asUintN(128, value);
        const at = N(result);
        dv.setBigUint64(at, BigInt.asUintN(64, value), true);
        dv.setBigUint64(at + 8, value >> 64n, true);
    };

    const panics = { 1: (a, b) => `index ${a} out of bounds for length ${b}`, 2: () => "division by zero", 3: () => "unreachable code",
        4: a => `invalid array length ${a}`, 5: () => "null reference", 6: () => "stack overflow" };

    const imports = {
        env: {
            ...softFloat,
            larva_alloc: size => P(alloc(N(size))),
            larva_exception_cell: size => P(alloc(N(size))),
            larva_free: at => { at = N(at); if (cells.delete(at)) release(at); },
            larva_global: (slot, size) => {
                slot = N(slot);
                if (dv.getBigUint64(slot, true) === 0n) dv.setBigUint64(slot, BigInt(alloc(N(size))), true);
                return dv.getBigUint64(slot, true);
            },
            larva_gc_new: size => P(gcAlloc(N(size))),
            larva_gc_new_in: size => P(gcAlloc(N(size))),
            larva_gc_domain: () => 0n,
            // No sockets in WebAssembly (yet): every network call fails.
            larva_net_connect: () => -1n,
            larva_net_listen: () => -1n,
            larva_net_accept: () => -1n,
            larva_net_port: () => -1n,
            // No processes in WebAssembly.
            larva_proc_start: () => -1n,
            larva_proc_wait: () => -1n,
            // A typed object or array (the LLVM back end): the type map's first words are its size and element stride.
            larva_gc_alloc: (type, size) => P(gcAlloc(N(size))),
            // An object ending with an array in place (flags bit 8) keeps the length in the word before the elements.
            larva_gc_array: (type, length) => {
                const t = N(type), n = Number(length), header = dv.getUint32(t, true);
                const at = gcAlloc(header + Math.max(n, 0) * dv.getUint32(t + 4, true));
                dv.setBigInt64(at + (dv.getUint32(t + 8, true) & 8 ? header - 8 : 0), BigInt(length), true);
                return P(at);
            },
            larva_raw_alloc: size => P(alloc(N(size))),
            larva_js_gc_alloc: (type, size) => P(gcAlloc(N(size))),
            larva_js_gc_array: (type, length) => {
                const t = N(type), n = Number(length), header = dv.getUint32(t, true);
                const at = gcAlloc(header + Math.max(n, 0) * dv.getUint32(t + 4, true));
                dv.setBigInt64(at + (dv.getUint32(t + 8, true) & 8 ? header - 8 : 0), BigInt(length), true);
                return P(at);
            },
            larva_js_array_new: (length, header, stride, managed) => {
                const size = N(header) + Math.max(Number(length), 0) * N(stride);
                const at = managed & 1 ? gcAlloc(size) : alloc(size);
                dv.setBigInt64(at + (managed & 4 ? N(header) - 8 : 0), BigInt(length), true);
                return P(at);
            },
            larva_gc_retain: at => { takeLog(); at = N(at); if (isObject(at)) counts[at >>> 4]++; },
            larva_gc_release: at => {
                takeLog();
                at = N(at);
                if (!isObject(at) || --counts[at >>> 4] > 0) return;
                if (finalizers.has(at)) { counts[at >>> 4] = 1; finalize(at); } else release(at);
            },
            larva_gc_finalizer: (at, fn) => { takeLog(); at = N(at); if (isObject(at)) { if (N(fn)) finalizers.set(at, N(fn)); else finalizers.delete(at); } },
            larva_gc_wb: () => {},
            larva_poll: poll,
            larva_array_new: (length, header, stride, managed) => {
                const size = N(header) + Number(length) * N(stride);
                const at = managed & 1 ? gcAlloc(size) : alloc(size);
                dv.setBigInt64(at + (managed & 4 ? N(header) - 8 : 0), BigInt(length), true);
                return P(at);
            },
            larva_pin: at => { at = N(at); pins.set(at, (pins.get(at) ?? 0) + 1); },
            larva_unpin: at => { at = N(at); const n = pins.get(at); if (n > 1) pins.set(at, n - 1); else pins.delete(at); },
            larva_region_enter: region => { takeLog(); regions.push(region); share(); },
            larva_region_exit: region => { if (regions[regions.length - 1] === region) regions.pop(); freeRegion(region); share(); },
            larva_region_depth: () => regions.length,
            larva_region_unwind: depth => { while (regions.length > depth) freeRegion(regions.pop()); share(); },
            larva_print_i64: value => print(String(value)),
            larva_print_u64: value => print(String(BigInt.asUintN(64, value))),
            larva_print_f64: bits => print(general(bitsToFloat(bits), 15)),
            larva_print_bool: value => print(value ? "true" : "false"),
            larva_print_str: at => print(text(N(at))),
            larva_print_line: () => print("\n"),
            larva_print_utf8: at => { at = N(at); print(new TextDecoder().decode(new Uint8Array(memory.buffer.slice(at + 8, at + 8 + Number(dv.getBigInt64(at, true)))))); },
            larva_read_line: () => { flush(); line = readLine(); return line === null ? -1n : BigInt(line.length); },
            larva_read_unit: index => unitOf(line, index),
            larva_args_count: () => BigInt(words.length),
            larva_args_length: index => BigInt(words[Number(index)]?.length ?? 0),
            larva_args_unit: (index, at) => unitOf(words[Number(index)] ?? null, at),
            larva_format_f64: bits => { formatted = general(bitsToFloat(bits), 15); return BigInt(formatted.length); },
            larva_format_unit: index => unitOf(formatted, index),
            // System.GC: a collection now (not inside a finalizer), how many so far, and the gc objects alive.
            larva_gc_request: () => { if (tracing && finalizing === 0) collect(); },
            larva_gc_collections: () => BigInt(collections),
            larva_gc_objects: () => { takeLog(); let n = 0; for (let j = 0; j < objectCount; j++) if (isObject(objects[j])) n++; return BigInt(n); },
            larva_gc_custom: () => {},
            larva_mulhi_u64: (a, b) => BigInt.asIntN(64, (BigInt.asUintN(64, a) * BigInt.asUintN(64, b)) >> 64n),
            // LLVM's 128-bit arithmetic (compiler-rt): each operand a (low, high) pair, the result written at `result`.
            __multi3: (result, aLow, aHigh, bLow, bHigh) => write128(result, wide128(aLow, aHigh) * wide128(bLow, bHigh)),
            __udivti3: (result, aLow, aHigh, bLow, bHigh) => write128(result, wide128(aLow, aHigh) / wide128(bLow, bHigh)),
            __umodti3: (result, aLow, aHigh, bLow, bHigh) => write128(result, wide128(aLow, aHigh) % wide128(bLow, bHigh)),
            __divti3: (result, aLow, aHigh, bLow, bHigh) => write128(result, BigInt.asIntN(128, wide128(aLow, aHigh)) / BigInt.asIntN(128, wide128(bLow, bHigh))),
            __modti3: (result, aLow, aHigh, bLow, bHigh) => write128(result, BigInt.asIntN(128, wide128(aLow, aHigh)) % BigInt.asIntN(128, wide128(bLow, bHigh))),
            // A monotonic clock, and waiting (blocking: WebAssembly code here runs to completion).
            larva_time_now: () => BigInt(Math.floor(performance.now())),
            // Threads: WebAssembly here runs on one thread, so a thread's function runs at once, to the end; locks are always
            // free and a condition's wait times out at once. Atomics are plain reads and writes.
            larva_thread_start: (code, env) => { const f = table.get(typeof table.length === "bigint" ? BigInt(code) : code); f(env); return ++threadsMade; },
            larva_thread_join: () => 1n,
            larva_thread_id: () => 1n,
            larva_thread_yield: () => {},
            larva_cpu_count: () => 1n,
            larva_mutex_new: () => 1n,
            larva_mutex_lock: () => {},
            larva_mutex_trylock: () => 1n,
            larva_mutex_unlock: () => {},
            larva_mutex_free: () => {},
            larva_cond_new: () => 1n,
            larva_cond_wait: () => 0n,
            larva_cond_signal: () => {},
            larva_cond_broadcast: () => {},
            larva_cond_free: () => {},
            larva_atomic_load_i64: at => dv.getBigInt64(N(at), true),
            larva_atomic_store_i64: (at, v) => dv.setBigInt64(N(at), v, true),
            larva_atomic_add_i64: (at, v) => { const b = dv.getBigInt64(N(at), true); dv.setBigInt64(N(at), BigInt.asIntN(64, b + v), true); return b; },
            larva_atomic_xchg_i64: (at, v) => { const b = dv.getBigInt64(N(at), true); dv.setBigInt64(N(at), v, true); return b; },
            larva_atomic_cas_i64: (at, e, v) => { const b = dv.getBigInt64(N(at), true); if (b === e) dv.setBigInt64(N(at), v, true); return b; },
            larva_atomic_load_i32: at => dv.getInt32(N(at), true),
            larva_atomic_store_i32: (at, v) => dv.setInt32(N(at), v, true),
            larva_atomic_add_i32: (at, v) => { const b = dv.getInt32(N(at), true); dv.setInt32(N(at), (b + v) | 0, true); return b; },
            larva_atomic_xchg_i32: (at, v) => { const b = dv.getInt32(N(at), true); dv.setInt32(N(at), v, true); return b; },
            larva_atomic_cas_i32: (at, e, v) => { const b = dv.getInt32(N(at), true); if (b === e) dv.setInt32(N(at), v, true); return b; },
            // The calendar clock (100 ns ticks since 1970 UTC), the local offset in seconds at a moment, nanoseconds.
            larva_time_utc: () => BigInt(Date.now()) * 10000n,
            larva_time_offset: seconds => BigInt(-new Date(Number(seconds) * 1000).getTimezoneOffset() * 60),
            larva_time_precise: () => BigInt(Math.round(performance.now() * 1e6)),
            larva_time_sleep: ms => { const until = performance.now() + Number(ms); while (performance.now() < until) { } },
            // Streams: node file descriptors (0, 1, 2 are the standard ones); each handle keeps its position.
            larva_stream_open: (path, mode) => {
                mode = Number(mode);
                const name = text(N(path));
                const exists = files.existsSync(name);
                if ((mode & 32) && exists || !exists && !(mode & 4)) return -1n;
                const read = mode & 1, write = mode & 2;
                const flags = (mode & 16) ? (read ? "a+" : "a") : (mode & 8) || !exists ? (read ? "w+" : "w") : (write ? "r+" : "r");
                try { const fd = files.openSync(name, flags); positions.set(fd, (mode & 16) ? files.fstatSync(fd).size : 0); return BigInt(fd); }
                catch { return -1n; }
            },
            larva_stream_close: handle => { const fd = Number(handle); if (fd < 3) return 0n; positions.delete(fd); return attempt(() => files.closeSync(fd)); },
            larva_stream_read: (handle, at, offset, count) => {
                at = N(at);
                const fd = Number(handle), start = Number(offset), wanted = Number(count), length = Number(dv.getBigInt64(at, true));
                if (start < 0 || wanted < 0 || start + wanted > length) return -1n;
                try {
                    const buffer = new Uint8Array(wanted);
                    const read = fd < 3 ? files.readSync(fd, buffer, 0, wanted, null) : files.readSync(fd, buffer, 0, wanted, positions.get(fd));
                    if (fd >= 3) positions.set(fd, positions.get(fd) + read);
                    bytes.set(buffer.subarray(0, read), at + 8 + start);
                    return BigInt(read);
                } catch { return -1n; }
            },
            larva_stream_write: (handle, at, offset, count) => {
                at = N(at);
                const fd = Number(handle), start = Number(offset), wanted = Number(count), length = Number(dv.getBigInt64(at, true));
                if (start < 0 || wanted < 0 || start + wanted > length) return -1n;
                try {
                    const data = bytes.slice(at + 8 + start, at + 8 + start + wanted);
                    const written = fd < 3 ? files.writeSync(fd, data) : files.writeSync(fd, data, 0, wanted, positions.get(fd));
                    if (fd >= 3) positions.set(fd, positions.get(fd) + written);
                    return BigInt(written);
                } catch { return -1n; }
            },
            larva_stream_seek: (handle, offset, origin) => {
                const fd = Number(handle);
                if (fd < 3 || !positions.has(fd)) return -1n;
                const base = Number(origin) === 0 ? 0 : Number(origin) === 1 ? positions.get(fd) : files.fstatSync(fd).size;
                const at = base + Number(offset);
                if (at < 0) return -1n;
                positions.set(fd, at);
                return BigInt(at);
            },
            larva_stream_length: handle => { const fd = Number(handle); if (fd < 3) return -1n; try { return BigInt(files.fstatSync(fd).size); } catch { return -1n; } },
            larva_stream_flush: handle => Number(handle) < 3 || positions.has(Number(handle)) ? 0n : -1n,
            // System.Math: floats as their bits (BigInt).
            larva_math_sqrt: x => toBits(Math.sqrt(fromBits(x))),
            larva_math_sin: x => toBits(Math.sin(fromBits(x))),
            larva_math_cos: x => toBits(Math.cos(fromBits(x))),
            larva_math_tan: x => toBits(Math.tan(fromBits(x))),
            larva_math_asin: x => toBits(Math.asin(fromBits(x))),
            larva_math_acos: x => toBits(Math.acos(fromBits(x))),
            larva_math_atan: x => toBits(Math.atan(fromBits(x))),
            larva_math_exp: x => toBits(Math.exp(fromBits(x))),
            larva_math_log: x => toBits(Math.log(fromBits(x))),
            larva_math_log10: x => toBits(Math.log10(fromBits(x))),
            larva_math_floor: x => toBits(Math.floor(fromBits(x))),
            larva_math_ceil: x => toBits(Math.ceil(fromBits(x))),
            larva_math_atan2: (x, y) => toBits(Math.atan2(fromBits(x), fromBits(y))),
            larva_math_pow: (x, y) => toBits(Math.pow(fromBits(x), fromBits(y))),
            larva_f64_bits: bits => bits,
            larva_f64_from_bits: bits => bits,
            // No C in WebAssembly: interop stops the program.
            larva_mem_alloc: () => { throw new LarvaPanic("larva panic: native memory is not available in WebAssembly"); },
            larva_mem_free: () => { throw new LarvaPanic("larva panic: native memory is not available in WebAssembly"); },
            larva_mem_copy: (target, source, count) => { target = N(target); source = N(source); count = Number(count); if (count > 0) bytes.copyWithin(target, source, source + count); },
            larva_mem_cmp: (left, right, count) => { left = N(left); right = N(right); count = Number(count); for (let i = 0; i < count; i++) { const d = bytes[left + i] - bytes[right + i]; if (d !== 0) return BigInt(d); } return 0n; },
            larva_native_symbol: () => { throw new LarvaPanic("larva panic: C functions cannot be called from WebAssembly"); },
            larva_file_read: at => {
                try { fileBuffer = files.readFileSync(text(N(at))); return BigInt(fileBuffer.length); }
                catch { fileBuffer = new Uint8Array(0); return -1n; }
            },
            larva_file_take: at => {
                at = N(at);
                const length = Math.min(Number(dv.getBigInt64(at, true)), fileBuffer.length);
                bytes.set(fileBuffer.subarray(0, length), at + 8);
            },
            larva_file_write: (path, at, append) => {
                at = N(at);
                const data = bytes.slice(at + 8, at + 8 + Number(dv.getBigInt64(at, true)));
                return attempt(() => (append ? files.appendFileSync : files.writeFileSync)(text(N(path)), data));
            },
            larva_file_kind: at => {
                try { const s = files.statSync(text(N(at))); return s.isDirectory() ? 2n : 1n; } catch { return 0n; }
            },
            larva_file_delete: at => attempt(() => {
                const name = text(N(at));
                if (files.statSync(name).isDirectory()) files.rmdirSync(name); else files.unlinkSync(name);
            }),
            larva_file_time: at => { try { return BigInt(Math.floor(files.statSync(text(N(at))).mtimeMs)); } catch { return -1n; } },
            larva_dir_create: at => attempt(() => files.mkdirSync(text(N(at)), { recursive: true })),
            larva_dir_list: at => {
                try { folderEntries = files.readdirSync(text(N(at))).sort(); return BigInt(folderEntries.length); }
                catch { folderEntries = []; return -1n; }
            },
            larva_dir_length: index => BigInt(folderEntries[Number(index)]?.length ?? 0),
            larva_dir_unit: (index, at) => unitOf(folderEntries[Number(index)] ?? null, at),
            larva_env_get: at => { environmentValue = env[text(N(at))] ?? null; return environmentValue === null ? -1n : BigInt(environmentValue.length); },
            larva_env_unit: index => unitOf(environmentValue, index),
            larva_parse_f64: at => { const view = new DataView(new ArrayBuffer(8)); view.setFloat64(0, Number(text(N(at)))); return view.getBigInt64(0); },
            larva_process_run: at => {
                flush();
                if (!shell) return -1n;
                const result = shell.spawnSync(text(N(at)), { shell: true, stdio: "inherit" });
                return result.status === null ? -1n : BigInt(result.status);
            },
            larva_panic: (code, a, b) => { throw new LarvaPanic(`larva panic: ${(panics[code] ?? (() => `trap ${code}`))(a, b)}`); },
        },
    };

    let source;
    const url = new URL("__LARVA_WASM__", import.meta.url);
    if (options.module) source = options.module;
    else if (node) source = await (await import("node:fs/promises")).readFile(url);
    else source = await (await fetch(url)).arrayBuffer();
    const { instance } = await WebAssembly.instantiate(source, imports);
    exp = instance.exports;
    memory = exp.memory;
    table = c64 ? exp.__indirect_function_table : exp.table;
    refresh();
    base = top = ((c64 ? Number(exp.__heap_base.value) : exp.stack_top.value) + 15) & ~15;
    if (c64 && exp.larva_wasm_heap) { shared = Number(exp.larva_wasm_heap.value); refill(); share(); }

    // The entry: up to four integer, bool or float arguments; its result is printed (like a native executable).
    const ARGS = 16, RESULT = 48;
    let info;
    if (c64) info = Number(exp.larva_entry_info());
    else { exp.larva_entry_info(ARGS, RESULT); info = Number(dv.getBigInt64(RESULT, true)); }
    const arity = info & 0xFF, kind = (info >> 8) & 0xFF, textArgs = (info >> 16) & 1;
    const args = textArgs ? [] : words;
    if (args.length !== arity) {
        error(`usage: program${arity ? " <arguments>" : ""} (${arity} integer argument${arity === 1 ? "" : "s"})\n`);
        return { exitCode: 2 };
    }
    const values = [0n, 0n, 0n, 0n];
    args.forEach((a, i) => { values[i] = a === "true" || a === "True" ? 1n : a === "false" || a === "False" ? 0n : BigInt(a); });
    let result;
    try {
        if (c64) {
            result = (kind === 4 ? exp.larva_entry_f64 : exp.larva_entry)(...values);
        } else {
            values.forEach((v, i) => dv.setBigInt64(ARGS + 8 * i, v, true));
            (kind === 4 ? exp.larva_entry_f64 : exp.larva_entry)(ARGS, RESULT);
            result = kind === 4 ? dv.getFloat64(RESULT, true) : dv.getBigInt64(RESULT, true);
        }
    } catch (e) {
        flush();
        if (e instanceof LarvaPanic) { error(e.message + "\n"); return { exitCode: 101, error: e.message }; }
        throw e;
    }
    if (pending()) {
        flush();
        const message = `larva: unhandled exception (type id ${pendingType()})`;
        error(message + "\n");
        return { exitCode: 1, error: message };
    }
    const shown = { 1: () => String(result), 2: () => String(BigInt.asUintN(64, result)), 3: () => (result ? "True" : "False"),
        4: () => general(result, 17) }[kind];
    if (shown) print(shown() + "\n");
    flush();
    return { exitCode: kind === 5 ? Number(BigInt.asIntN(32, BigInt(result))) : 0 }; // a program's result is its exit code
}

// Run directly by Node: `node NAME.js [args]`.
if (typeof process !== "undefined" && process.versions?.node && process.argv[1]
    && import.meta.url.endsWith("/" + process.argv[1].replace(/\\/g, "/").split("/").pop())) {
    run().then(r => { process.exitCode = r.exitCode; }, e => { console.error(e); process.exitCode = 70; });
}
