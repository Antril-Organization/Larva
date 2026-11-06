; Allocation in WebAssembly without leaving it: gc objects come from free lists by size and from a bump region, both
; in linear memory and both kept by the JavaScript runtime (larva_rt.js), which reads `log` (each new object's address
; and size) before it looks at the heap. Anything else (a collection due, a full log, a large object, an empty region)
; goes to JavaScript: larva_js_gc_alloc / larva_js_gc_array / larva_js_array_new.
;
; larva_wasm_heap (all i64): 0 cursor, 8 limit (the bump region), 16 since (bytes since the last collection),
; 24 budget (since >= budget: JavaScript decides; 0 sends every allocation there), 32 count (entries in log),
; 40 free[256] (heads by size in 16-byte units; a free block's first word is the next one), 2088 log[4096 x 2].

@larva_wasm_heap = global { i64, i64, i64, i64, i64, [256 x i64], [8192 x i64] } zeroinitializer, align 16

declare ptr @larva_js_gc_alloc(ptr, i64)
declare ptr @larva_js_gc_array(ptr, i64)
declare ptr @larva_js_array_new(i64, i64, i64, i32)
declare void @llvm.memset.p0.i64(ptr, i8, i64, i1)
declare i64 @llvm.umax.i64(i64, i64)

; A zeroed block of `size` bytes, logged as a new gc object, or null when JavaScript must do it.
define internal ptr @larva_wasm_fast(i64 %size) alwaysinline nounwind {
entry:
  %rounded = add i64 %size, 15
  %units = lshr i64 %rounded, 4
  %n = call i64 @llvm.umax.i64(i64 %units, i64 1)
  %since.old = load i64, ptr getelementptr inbounds (i8, ptr @larva_wasm_heap, i64 16), align 8
  %since = add i64 %since.old, %size
  %budget = load i64, ptr getelementptr inbounds (i8, ptr @larva_wasm_heap, i64 24), align 8
  %within = icmp ult i64 %since, %budget
  br i1 %within, label %logged, label %slow

logged:
  %count = load i64, ptr getelementptr inbounds (i8, ptr @larva_wasm_heap, i64 32), align 8
  ; count < 4096 and size + 15 < 4096 (n < 256) at once
  %both = or i64 %count, %rounded
  %small = icmp ult i64 %both, 4096
  br i1 %small, label %list, label %slow

list:
  %head.at = getelementptr inbounds [256 x i64], ptr getelementptr inbounds (i8, ptr @larva_wasm_heap, i64 40), i64 0, i64 %n
  %head = load i64, ptr %head.at, align 8
  %empty = icmp eq i64 %head, 0
  br i1 %empty, label %bump, label %reuse

reuse:
  %block = inttoptr i64 %head to ptr
  %next = load i64, ptr %block, align 8
  store i64 %next, ptr %head.at, align 8
  %bytes = shl i64 %n, 4
  call void @llvm.memset.p0.i64(ptr align 16 %block, i8 0, i64 %bytes, i1 false)
  br label %done

bump:
  %cursor = load i64, ptr @larva_wasm_heap, align 8
  %bytes2 = shl i64 %n, 4
  %end = add i64 %cursor, %bytes2
  %limit = load i64, ptr getelementptr inbounds (i8, ptr @larva_wasm_heap, i64 8), align 8
  %over = icmp ugt i64 %end, %limit
  br i1 %over, label %slow, label %bumped

bumped:
  store i64 %end, ptr @larva_wasm_heap, align 8
  br label %done

done:
  %at = phi i64 [ %head, %reuse ], [ %cursor, %bumped ]
  store i64 %since, ptr getelementptr inbounds (i8, ptr @larva_wasm_heap, i64 16), align 8
  %log.offset = shl i64 %count, 4
  %log.at = getelementptr inbounds i8, ptr getelementptr inbounds (i8, ptr @larva_wasm_heap, i64 2088), i64 %log.offset
  store i64 %at, ptr %log.at, align 8
  %log.size = getelementptr inbounds i8, ptr %log.at, i64 8
  store i64 %size, ptr %log.size, align 8
  %count.next = add i64 %count, 1
  store i64 %count.next, ptr getelementptr inbounds (i8, ptr @larva_wasm_heap, i64 32), align 8
  %result = inttoptr i64 %at to ptr
  ret ptr %result

slow:
  ret ptr null
}

define ptr @larva_gc_alloc(ptr %type, i64 %size) nounwind {
entry:
  %at = call ptr @larva_wasm_fast(i64 %size)
  %got = icmp ne ptr %at, null
  br i1 %got, label %fast, label %slow
fast:
  ret ptr %at
slow:
  %js = call ptr @larva_js_gc_alloc(ptr %type, i64 %size)
  ret ptr %js
}

; The type map's first two words: the header's size and the element's stride.
define ptr @larva_gc_array(ptr %type, i64 %length) nounwind {
entry:
  %fits = icmp ult i64 %length, 1048576
  br i1 %fits, label %sized, label %slow
sized:
  %header32 = load i32, ptr %type, align 4
  %stride.at = getelementptr inbounds i8, ptr %type, i64 4
  %stride32 = load i32, ptr %stride.at, align 4
  %header = zext i32 %header32 to i64
  %stride = zext i32 %stride32 to i64
  %body = mul i64 %length, %stride
  %size = add i64 %header, %body
  %at = call ptr @larva_wasm_fast(i64 %size)
  %got = icmp ne ptr %at, null
  br i1 %got, label %fast, label %slow
fast:
  ; An object ending with an array in place (flags bit 8): the length is the word before the elements.
  %flags.at = getelementptr inbounds i8, ptr %type, i64 8
  %flags = load i32, ptr %flags.at, align 4
  %trailing.bit = and i32 %flags, 8
  %trailing = icmp ne i32 %trailing.bit, 0
  %before = sub i64 %header, 8
  %length.offset = select i1 %trailing, i64 %before, i64 0
  %length.at = getelementptr inbounds i8, ptr %at, i64 %length.offset
  store i64 %length, ptr %length.at, align 8
  ret ptr %at
slow:
  %js = call ptr @larva_js_gc_array(ptr %type, i64 %length)
  ret ptr %js
}

; An array compiled code gives no type for (`managed` bit 0: a gc object; otherwise JavaScript's).
define ptr @larva_array_new(i64 %length, i64 %header, i64 %stride, i32 %managed) nounwind {
entry:
  %gc = and i32 %managed, 1
  %is.gc = icmp ne i32 %gc, 0
  %fits = icmp ult i64 %length, 1048576
  %both = and i1 %is.gc, %fits
  br i1 %both, label %sized, label %slow
sized:
  %body = mul i64 %length, %stride
  %size = add i64 %header, %body
  %at = call ptr @larva_wasm_fast(i64 %size)
  %got = icmp ne ptr %at, null
  br i1 %got, label %fast, label %slow
fast:
  ; `managed` bit 2: an object ending with the array in place.
  %trailing.bit = and i32 %managed, 4
  %trailing = icmp ne i32 %trailing.bit, 0
  %before = sub i64 %header, 8
  %length.offset = select i1 %trailing, i64 %before, i64 0
  %length.at = getelementptr inbounds i8, ptr %at, i64 %length.offset
  store i64 %length, ptr %length.at, align 8
  ret ptr %at
slow:
  %js = call ptr @larva_js_array_new(i64 %length, i64 %header, i64 %stride, i32 %managed)
  ret ptr %js
}
