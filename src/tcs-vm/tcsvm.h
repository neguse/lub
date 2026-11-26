/* tcs VM — a VM that runs bytecode (.tcsb) built from TinyC# IL.
   The design is in docs/tcs/vm-design.md. */
#ifndef TCSVM_H
#define TCSVM_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef union TvmSlot {
  int32_t i;
  float f;
  void *p;
  uint64_t u;
} TvmSlot;

typedef enum TvmStatus {
  TVM_OK = 0,
  TVM_LOAD_ERROR = 1,
  TVM_FAULT = 2,
  TVM_RESTART = 3, /* tvm_apply: a diff that cannot be applied (the reason is in
                      tvm_error). The host goes to the restart path */
} TvmStatus;

typedef struct TvmVm TvmVm;

typedef struct TvmConfig {
  size_t stack_slots; /* 0 = default (1M slots) */
  size_t max_frames;  /* 0 = default (64K) */
  int gc_stress;      /* Nonzero: collects at every allocation point and frame
                         boundary (for testing) */
} TvmConfig;

TvmVm *tvm_new(const TvmConfig *config);
void tvm_free(TvmVm *vm);

/* Loads the bytecode, verifies it, links it, and runs the static initializers.
   On failure returns TVM_LOAD_ERROR or TVM_FAULT, and tvm_error() returns the
   reason. */
TvmStatus tvm_load(TvmVm *vm, const uint8_t *bytes, size_t size);

/* hot reload (docs/tcs/vm-design.md §12, docs/tcs/object-model.md §6): applies
   the module and plan of the new version (made by tcs2vm --reload-from).
   Accepted only at a frame boundary (when there is no VM frame). TVM_LOAD_ERROR
   / TVM_RESTART means nothing was applied (the state stays at the old version;
   the reason is in tvm_error). On TVM_OK, a non-empty tvm_error() is a warning
   about a fault in an initializer or OnReload (the update is not rolled back).
 */
TvmStatus tvm_apply(TvmVm *vm, const uint8_t *module, size_t size,
                    const uint8_t *plan, size_t plan_size);

/* ---- shared heap (docs/tcs/object-model.md §10.2) ----
   Shares one heap and GC with C (tcs2c) modules. Installed by tcs_vm_register
   in the tcs2c --lib --vm-host glue (the host never calls it directly). When
   shared, the tcs2c GC traces VM objects (kind FOREIGN), collection happens
   only at frame boundaries (tcs_lib_gc / tvm_frame_end), and C modules can hold
   VM objects in statics and so on. One VM per process. */
typedef void (*TvmSlotFn)(void **slot);
typedef struct TvmHeap {
  void *(*alloc)(uint32_t kind, size_t size); /* Zero-filled payload */
  void (*enter)(void); /* Enters a VM call (stops collection) */
  void (*leave)(void);
  void (*barrier)(const void *owner); /* A reference was written into owner
                                         (write barrier) */
  void (*frame)(void);                /* Collection at a frame boundary */
  /* Passes the reference locations of all objects and the C module roots to fn
   * (for repointing on reload; called at a boundary) */
  void (*visit_all)(TvmSlotFn fn);
  /* Passes the payloads of old-generation objects in order (it runs after a
   * boundary, so there are no young objects) */
  void (*each)(void (*fn)(void *payload, uint32_t kind, void *ctx), void *ctx);
} TvmHeap;
void tvm_use_heap(TvmVm *vm, const TvmHeap *heap); /* Before tvm_load */
/* Called by the runtime/core GC: the VM roots and the reference locations in VM
 * objects */
void tvm_heap_roots(TvmSlotFn fn);
void tvm_heap_refs(void *payload, TvmSlotFn fn);

/* Applies a vm_batch from the tcs2vm --watch stream (the base64-decoded bytes:
   u32 module length, module, u32 plan length, plan). Returns the same as
   tvm_apply */
TvmStatus tvm_apply_batch(TvmVm *vm, const uint8_t *batch, size_t size);

/* ---- host (docs/tcs/vm-design.md §8.5, §13) ----
   Host functions are registered by name before tvm_load.
   The name is the IL host name (for the --ref stub's `Eng.Gfx.Begin` it is
   "eng.gfx.begin"; for the static value `Gfx.MainTex` it is "eng.gfx.main_tex"
   with no arguments). params is the sequence of terms in C# argument order,
   rets is the return value term ("" for void), and each term is one of:
   - i int (including enum), f float, b bool, s string, o other references
     (host data class objects, List, delegate, etc.)
   - S{type}: struct value (type is the IL type name, spelled as in tvm_type;
     the stub's `Eng.Vec2` is "Eng_Vec2", `Eng.Vec2?` is "Eng_Vec2?", `int?` is
     "int?")
   - &term: out argument, allowed only in params ("&i", "&S{Eng_Vec2}", etc.)
   Fields of an S{type} struct are limited to int / float / bool / enum and
   structs made only of those. If the format is wrong, tvm_host returns 0.
   args[k] is the k-th term of params; for
   i / f / b / s / o it is the value, and for S{type} it is a pointer to a block
   in C layout (docs/tcs/object-model.md §5). The host reads the block as a C
   struct
   (`*(const Vec2 *)args[k].p`). For &term, args[k].p points to where the host
   writes the value (int32_t *, float *, bool * (1 byte), void ** for a
   reference slot, the block for S{type}). out locations start as 0, and when
   the host returns, the VM stores them into the C# out variables. rets holds
   only the return value; for i / f / b / s / o the host writes the value to
   rets[0], and for S{type} it writes to the block at rets[0].p. Blocks and out
   locations are valid only during the call, and stay valid even if the host
   function calls back into the VM with tvm_invoke and host functions are called
   nested. It is a load error if a host function the module uses is missing, if
   an S{type} is not a module struct or has a reference field, or if the call
   shape does not match the registration. On failure, return the return value of
   tvm_host_fault (a C# trace is attached). Inside a host function, no
   collection happens unless it calls back into the VM with tvm_invoke or the
   like (collection happens only at interpreter points and frame boundaries). */
typedef TvmStatus (*TvmHostFn)(TvmVm *vm, TvmSlot *args, TvmSlot *rets,
                               void *user);
int tvm_host(TvmVm *vm, const char *name, const char *params, const char *rets,
             TvmHostFn fn, void *user);
TvmStatus tvm_host_fault(TvmVm *vm, const char *message);

/* The host reads and writes VM objects. An object's payload uses the layout of
   the type descriptor (docs/tcs/object-model.md §5), and field offsets are
   looked up by name (the IL name; "title" for the stub's field `Title`). */
int32_t tvm_field_offset(TvmVm *vm, const char *type,
                         const char *field); /* -1 if missing */
void *tvm_object_new(
    TvmVm *vm,
    const char *type); /* Fields are 0. NULL if the type is missing */
const char *tvm_object_type(const void *object); /* Type name */
void *tvm_string_new(TvmVm *vm, const char *data, size_t len);
const char *tvm_string_data(const void *string, size_t *len);

/* Looks up a type by name (a class by its IL name "Eng_Opts", List as
   "List<Eng_Opts>", arrays as "int[]", and the element types int / float / bool
   are spelled the same way). NULL if missing. The type table is recreated on
   load and reload, so a host that caches types or offsets must look them up
   again when tvm_types_version changes (the version never has the same value
   across VMs, so it also changes when the VM is recreated) */
typedef struct TcsType TvmTypeInfo;
const TvmTypeInfo *tvm_type(TvmVm *vm, const char *name);
uint32_t tvm_types_version(TvmVm *vm);
int32_t tvm_type_field_offset(const TvmTypeInfo *type,
                              const char *field); /* -1 if missing */
void *
tvm_object_new_of(TvmVm *vm,
                  const TvmTypeInfo *type); /* Classes only. Fields are 0 */

/* The host value held by objects of external data classes shared with the host
   (--ref classes) (docs/tcs/object-model.md §3). Located after the payload's
   type_id, aligned to 8 bytes */
#define TVM_HOST_VALUE(object) (*(uint64_t *)((char *)(object) + 8))

/* List and arrays. Elements are laid out from data (int / float are 4 bytes,
   bool is 1 byte, references are pointers, structs are the size of the type).
   tvm_list_new_of / tvm_array_new_of create count zero elements.
   tvm_list_resize changes the count, and added elements are 0. No collection
   happens inside a host function, so it may keep allocating without holding the
   created objects in a root */
void *tvm_list_new_of(TvmVm *vm, const TvmTypeInfo *list_type, int32_t count);
bool tvm_list_resize(TvmVm *vm, void *list, int32_t count);
int32_t tvm_list_count(const void *list);
void *tvm_list_data(const void *list);
void *tvm_array_new_of(TvmVm *vm, const TvmTypeInfo *array_type, int32_t len);
/* An untyped zero-filled byte array (owned by the GC). Placed in a reference
   field of an object, it lives as long as that object lives (a host data class
   lets the GC own host state. VM code does not read it) */
void *tvm_blob_new(TvmVm *vm, size_t size);
int32_t tvm_array_len(const void *array);
void *tvm_array_data(const void *array);

/* handle: the host (a C module) holds a VM object across frames
   (docs/tcs/object-model.md §10). It is a GC root, and when the object is
   recreated by a reload, the handle points to the new object. Numbers start at
   1 (0 is invalid). */
/* The host calls a VM closure (delegate) (X-4). args / rets are the leaves of
 * the function type. Can be called both inside a VM call (from a host function)
 * and outside */
TvmStatus tvm_invoke(TvmVm *vm, void *closure, const TvmSlot *args,
                     size_t nargs, TvmSlot *rets, size_t nrets);

/* Calls an instance method of a VM object by name (X-4: when a VM class
   implements an interface of a C module, the C dispatcher calls it). Looked up
   in the itable by name and argument count. args / rets are the leaves
   excluding self */
TvmStatus tvm_invoke_method(TvmVm *vm, void *object, const char *method,
                            const TvmSlot *args, size_t nargs, TvmSlot *rets,
                            size_t nrets);

/* The VM calls a C module delegate (X-4). sig is the leaf kind characters of
   the arguments, ':', and the return kind character ("i:b", "s:", etc.; the
   same characters as tvm_host). When CALLD meets a C closure (a delegate that
   is not a VM object), it calls the invoke for the sig of the function type */
typedef TvmStatus (*TvmDelegateFn)(void *closure, const TvmSlot *args,
                                   TvmSlot *rets);
int tvm_host_delegate(TvmVm *vm, const char *sig, TvmDelegateFn fn);

/* Types of C modules (shared heap, docs/tcs/object-model.md §10.6). Register
   them before tvm_load (they carry over to the new version on reload).
   - tvm_host_type: attaches the C module's type_id to objects of the VM class
     named name (a --ref class), so that objects of that type created by the C
     module are treated as the same type by VM type tests (`is` / cast). The
     type_ids of VM-only classes are TVM_TYPE_ID_VM or higher (they do not
     overlap with the C module numbers).
   - tvm_host_iface: a --ref interface. VM type tests inspect C objects with
     check.
   - tvm_implements: whether a VM object implements the --ref interface name
     (called by the C module's interface check) */
#define TVM_TYPE_ID_VM 0x40000000u
typedef bool (*TvmIfaceCheck)(void *object);
int tvm_host_type(TvmVm *vm, const char *name, uint32_t type_id);
int tvm_host_iface(TvmVm *vm, const char *name, TvmIfaceCheck check);
bool tvm_implements(TvmVm *vm, const void *object, const char *iface);

typedef uint32_t TvmHandle;
TvmHandle tvm_hold(TvmVm *vm, void *object);
void *tvm_handle_get(TvmVm *vm, TvmHandle h);
void tvm_release(TvmVm *vm, TvmHandle h);

/* Looks up a function by name ("Particles.Main"). -1 if missing. */
int32_t tvm_find(TvmVm *vm, const char *name);
/* The function the module marked as entry (-1 if none). */
int32_t tvm_entry(TvmVm *vm);

/* Arguments are args[0..nargs), and return values are written to
   rets[0..nrets). TVM_LOAD_ERROR if nargs differs from the function's argument
   slot count or nrets is less than the return value slot count. */
TvmStatus tvm_call(TvmVm *vm, int32_t func, const TvmSlot *args, size_t nargs,
                   TvmSlot *rets, size_t nrets);

/* Frame boundary (after the host returns from entry). Collects if the
 * accumulated allocations exceed the threshold. */
void tvm_frame_end(TvmVm *vm);
/* Bytes alive after the last collection */
size_t tvm_gc_live(TvmVm *vm);

/* The message of the last error or fault (including the C# stack trace). */
const char *tvm_error(TvmVm *vm);

/* ---- Inspection (tcsvm_debug.c): --trace records, call stack, value tree,
   state snapshots ---- A module built with tcs vm --trace PREFIX places records
   at the start of statements and after assignments in files under PREFIX. */

/* Passes the lines passed since the previous call, and clears the record (in no
 * particular order) */
typedef void (*TvmTraceLineFn)(void *user, const char *file, uint32_t line);
void tvm_trace_take_lines(TvmVm *vm, TvmTraceLineFn fn, void *user);
/* Same as tvm_trace_take_lines, but skips lines passed only with another object
   of the same type as the object pinned under key (tvm_inspect_pin) as self.
   Lines of class instance methods are recorded as (line, self) pairs. If no
   object is pinned under key, passes all lines */
void tvm_trace_take_lines_for(TvmVm *vm, const char *key, TvmTraceLineFn fn,
                              void *user);
/* Passes the values assigned since the previous call, the last value per
   location as a short string, and clears the record. label is the C# notation
   of the assignment target. Numbers as is, bool as true / false, strings in ""
   cut at 40 bytes, null as null, classes and structs as the type name (with
   {X=1 Y=2} appended if they have 4 or fewer fields that are only numbers and
   bools), List and arrays as [element count], Dictionary as {…} */
typedef void (*TvmTraceValueFn)(void *user, const char *file, uint32_t line,
                                uint32_t col, const char *label,
                                const char *text);
void tvm_trace_take_values(TvmVm *vm, TvmTraceValueFn fn, void *user);
/* From inside a host function, passes the file path and line of each entry in
 * the call stack (from the current function outward) */
typedef void (*TvmFrameFn)(void *user, const char *file, uint32_t line);
void tvm_stack(TvmVm *vm, TvmFrameFn fn, void *user);
/* Ordered like tvm_stack, and also passes the Id of the self object of each
   frame (in the same notation as TvmInspectNode.id). self is set only for
   frames of instance methods of --trace classes, and is "" otherwise */
typedef void (*TvmFrameSelfFn)(void *user, const char *file, uint32_t line,
                               const char *id);
void tvm_stack_self(TvmVm *vm, TvmFrameSelfFn fn, void *user);
/* Classes in --trace files (in the order found) */
uint32_t tvm_traced_class_count(TvmVm *vm);
const char *tvm_traced_class(TvmVm *vm, uint32_t i);

/* A node of the value tree. path is in C# notation (Game.Field.Items[2];
 * Dictionary entries are ["key"] or [3]) */
typedef struct TvmInspectNode {
  char path[512];
  char name[256];
  char text[160];
  const char
      *type;   /* The class name if it is a class instance (NULL otherwise) */
  char id[32]; /* For an object value, the string of the serial number assigned
                  to that object ("" for string and value types). Does not
                  change when a state is loaded */
  int32_t count; /* Number of children */
  bool cycle;    /* Same object as an ancestor (cannot be expanded) */
} TvmInspectNode;
/* Writes count children of the path node, starting at the offset-th, to out,
   and returns the number written. An empty path is the root, whose children are
   the entry class (first) and the --trace classes (in name order). The children
   of a class are its static fields (in declaration order), of instances and
   structs their fields (in name order), of List and arrays the elements, and of
   Dictionary the entries (in key order). null fields and functions are not
   returned. 0 if the path cannot be followed */
int32_t tvm_inspect(TvmVm *vm, const char *entry, const char *path,
                    int32_t offset, int32_t count, TvmInspectNode *out);
/* Rewrites the value of the path node to the value in text (path is a
   tvm_inspect path). Call only at a frame boundary. It can write int (decimal;
   enum values are int too), float, bool (true / false), string (enclosed in ";
   \\ and \" and \n can be used), and set a reference to null (text is null).
   struct, object, List, array, and Dictionary values themselves cannot be
   written. Returns true on success. false if the path cannot be followed, the
   kind is not writable, or text cannot be read as a value; the reason is in
   tvm_error */
bool tvm_inspect_set(TvmVm *vm, const char *entry, const char *path,
                     const char *text);
/* Walks breadth-first from the statics of the entry and --trace classes, and
   passes each class instance to fn once, in the order found (path is a
   tvm_inspect path, id is TvmInspectNode.id. Each object only once, with the
   first path found). Walks down to depth max_depth (static fields are depth 1),
   and for List and arrays only the first max_children elements. Faster than
   walking each node again with tvm_inspect (it does not re-read the path from
   the root and does not build value strings) */
typedef void (*TvmInstanceFn)(void *user, const char *type, const char *path,
                              const char *id);
void tvm_inspect_instances(TvmVm *vm, const char *entry, uint32_t max_depth,
                           uint32_t max_children, TvmInstanceFn fn, void *user);
/* Weakly pins the object of the path node under key (does not prevent
 * collection). Forgets it if the path cannot be followed or is not an object */
void tvm_inspect_pin(TvmVm *vm, const char *key, const char *entry,
                     const char *path);
/* Whether the object pinned under key has not been collected yet and its id
 * (TvmInspectNode.id) is id */
bool tvm_inspect_pinned(TvmVm *vm, const char *key, const char *id);

/* Cost: passing clock (a clock returning ns; measures how long GC pauses)
   starts counting, and NULL stops it. While counting, adds to each --trace line
   its allocations (bytes and count) and the time reported by tvm_profile_tick.
   Time is attributed to the line executing when it is reported (the clock is
   not read, because even reading the TSC directly costs more than one VM
   statement, and reading it per statement would turn clock time into line
   time). Time and allocations inside unmarked functions (functions outside
   --trace and host functions) are attributed to the line that called them. Time
   paused by GC is not added to lines and is counted separately as pauses. Time
   reported outside the outermost call from the host is attributed to no line.
 */
typedef uint64_t (*TvmClockFn)(void);
void tvm_profile_enable(TvmVm *vm, TvmClockFn clock);
/* Reports that ns of time has passed. May be called from another thread
 * (calling it at a fixed interval gives an estimate of per-line time) */
void tvm_profile_tick(TvmVm *vm, uint64_t ns);
/* Passes the lines added to since the previous take, and resets them to 0 (in
 * no particular order; bytes includes the header) */
typedef void (*TvmProfileLineFn)(void *user, const char *file, uint32_t line,
                                 uint64_t ns, uint64_t bytes, uint32_t allocs);
void tvm_profile_take_lines(TvmVm *vm, TvmProfileLineFn fn, void *user);
/* Passes the GC pauses since the previous take, oldest first, and forgets them
 * (up to the last TVM_PROFILE_GCS; live is the bytes alive after the
 * collection) */
#define TVM_PROFILE_GCS 64
typedef void (*TvmProfileGcFn)(void *user, uint64_t ns, size_t live);
void tvm_profile_take_gc(TvmVm *vm, TvmProfileGcFn fn, void *user);
/* Objects kept across frame boundaries: objects created on a --trace line after
   the previous tvm_frame_end and now reachable from the statics of the entry
   and --trace classes are counted per creating line and holding field
   ("type.field"; "class.field" for statics). The holding field is the field of
   a pre-existing object nearest to the root (for objects inside a newly created
   object, the field holding that new object). An object counted once is not
   counted again. bytes includes the header */
typedef void (*TvmProfileKeptFn)(void *user, const char *file, uint32_t line,
                                 const char *holder, uint32_t count,
                                 uint64_t bytes);
void tvm_profile_take_kept(TvmVm *vm, const char *entry, TvmProfileKeptFn fn,
                           void *user);
/* The current --trace line while counting (inside a host function, the line
 * that called it). false if not counting or outside a line */
bool tvm_profile_site(TvmVm *vm, const char **file, uint32_t *line);

/* State snapshot: the statics of the entry and --trace classes, and the
 * contents of the objects reachable from them (strings are not copied) */
typedef struct TvmSnapshot TvmSnapshot;
/* Takes and holds a snapshot (snapshotted objects are not collected). NULL on
 * failure */
TvmSnapshot *tvm_snapshot_take(TvmVm *vm, const char *entry);
/* Writes the snapshotted statics and object contents back to the same statics
 * and the same objects */
void tvm_snapshot_restore(TvmVm *vm, const TvmSnapshot *s);
void tvm_snapshot_free(TvmVm *vm, TvmSnapshot *s);
/* A number that increases every time a reload recreates objects (snapshots
 * taken before that cannot be restored) */
uint32_t tvm_layout_version(TvmVm *vm);
/* The numeric value of the path node in the snapshot (the parent of path is
 * resolved in the current value tree, and that parent's value in the snapshot
 * is read). false if not a number */
bool tvm_snapshot_number(TvmVm *vm, const TvmSnapshot *s, const char *entry,
                         const char *path, float *out);

/* ---- Saving and loading state (tcsvm_state.c, docs/tcs/vm-design.md §13.4)
   ---- Turns all statics and the objects reachable from them into an
   address-independent byte array. It can be loaded into another VM (even in
   another process) whose types have the same shape. Can be called only at a
   frame boundary. Objects held by host handles (tvm_hold) are not written. */
/* Free bytes with tvm_state_free. TVM_LOAD_ERROR on failure (the reason is in
 * tvm_error) */
TvmStatus tvm_state_save(TvmVm *vm, uint8_t **bytes, size_t *size);
void tvm_state_free(uint8_t *bytes);
/* Replaces the statics with the loaded values. Statics not in the current
   module are dropped, and statics not in the state keep their current values.
   If the type shapes or functions do not match, TVM_LOAD_ERROR and the state is
   unchanged (the reason is in tvm_error) */
TvmStatus tvm_state_load(TvmVm *vm, const uint8_t *bytes, size_t size);

#ifdef __cplusplus
}
#endif

#endif
