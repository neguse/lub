/* ---- Common shape of heap objects. Every heap object places its payload right
   after TcsGcHeader (GC is in the CRuntime.Gc section). String literals are
   static objects (TCS_GC_STATIC) and are not on the heap list ---- */
typedef struct TcsLayout {
  uint32_t size;           /* byte count of one value */
  uint32_t count;          /* number of pointer slots */
  const uint32_t *offsets; /* byte offset of each pointer slot */
} TcsLayout;

enum {
  TCS_KIND_RAW = 0,   /* contains no pointers (string / buffer) */
  TCS_KIND_OBJECT,    /* value of one layout (class instance / cell) */
  TCS_KIND_ARRAY,     /* TcsArray + inline elements (layout = element) */
  TCS_KIND_LIST,      /* TcsList (layout = element) */
  TCS_KIND_DICT,      /* TcsDict (layout = value) */
  TCS_KIND_DICT_NODE, /* TcsDictNode (layout = value) */
  TCS_KIND_CLOSURE,   /* TcsClosure (cells are OBJECT) */
  /* tcs VM object (doc/object-model.md §10.2). The layout is in the VM's type
     descriptor, and the reference locations are traced by tcs_gc_foreign_refs
     (a VM function) */
  TCS_KIND_FOREIGN = 0x40
};
/* flags: MARK = liveness mark of the old-generation mark-sweep, STATIC = static
   object (literal), OLD = old generation (individually malloc'ed), DIRTY =
   registered in the remembered set, FORWARD = promoted young object (next is
   the promotion target) */
enum {
  TCS_GC_MARK = 1u,
  TCS_GC_STATIC = 2u,
  TCS_GC_OLD = 4u,
  TCS_GC_DIRTY = 8u,
  TCS_GC_FORWARD = 16u
};

typedef struct TcsGcHeader {
  _Alignas(max_align_t) struct TcsGcHeader *next;
  const TcsLayout *layout;
  size_t size; /* payload bytes */
  uint16_t kind;
  uint16_t flags;
  uint32_t type_id; /* runtime type tag (cast check from object) */
} TcsGcHeader;
/* Reserved type_id: classes are 1..N (DFS range), structural types are
 * 0x80000000+n */
#define TCS_TYPE_STRING UINT32_C(0xfffffff1)
#define TCS_TYPE_BOX_I32 UINT32_C(0xfffffff2)
#define TCS_TYPE_BOX_F32 UINT32_C(0xfffffff3)
#define TCS_TYPE_BOX_BOOL UINT32_C(0xfffffff4)
#define TCS_GC_PAYLOAD(hdr)                                                    \
  ((void *)((unsigned char *)(hdr) + sizeof(TcsGcHeader)))
#define TCS_GC_HEADER(ptr)                                                     \
  ((TcsGcHeader *)((unsigned char *)(ptr) - sizeof(TcsGcHeader)))

typedef struct TcsString {
  size_t length;
  unsigned char data[];
} TcsString;

typedef struct TcsArray {
  size_t length;
  size_t element_size;
  _Alignas(max_align_t) unsigned char data[];
} TcsArray;

typedef struct TcsList {
  size_t length;
  size_t capacity;
  size_t element_size;
  void *data; /* RAW heap buffer (the list traces the elements) */
} TcsList;

/* Queue: the space before head is free (cleared with 0) */
typedef struct TcsQueue {
  TcsList *items;
  size_t head;
} TcsQueue;

/* HashSet: same structure as .NET's HashSet, with the same enumeration order.
   items holds the element per slot, buckets maps hash → slot + 1 (0 = empty),
   nexts is the chain (0 or more = next slot, -1 = end, -2 or less = free slot,
   holding the free chain as -3 - next). key_kind is the element kind (0 = i32,
   1 = string, 2 = host handle, 3 = class identity); the hash for 3 is the
   address, so if a GC boundary is crossed with young objects inside, buckets
   are rebuilt */
typedef struct TcsHashSet {
  TcsList *items;
  int32_t *buckets;
  int32_t *nexts;
  size_t bucket_count;
  int32_t key_kind;
  int32_t free_head;
  int32_t free_count;
  bool has_young;
  size_t frame;
} TcsHashSet;

static uint32_t tcs_digest;

/* Nullable<T> (il-spec §13): has = has a value */
typedef struct TcsOptI32 {
  bool has;
  int32_t v;
} TcsOptI32;
typedef struct TcsOptF32 {
  bool has;
  float v;
} TcsOptF32;
typedef struct TcsOptBool {
  bool has;
  bool v;
} TcsOptBool;

/* closure: fnptr + array of captured variable cells. Capture is per variable
   (il-spec §7) — captured locals are boxed into heap cells */
typedef struct TcsClosure {
  void *fn;
  void *cells[];
} TcsClosure;

/* Dictionary: chained hash (the bucket count doubles with count). Keys are
   i32 / string / host handle (key_is_string == 2: the pointer of an external
   data class is placed in key_s and identity is host_value; an object whose
   host_value is 0 uses pointer identity), and the value is value_size bytes at
   the end of the node (struct values allowed). Enumeration is in insertion
   order (support-matrix §18): nodes are lined up in the order they were added
   to order, removed nodes become NULL holes, and holes are compacted when
   growing. version increases on a new key or Clear, making additions during
   foreach a fault. */
typedef struct TcsDictNode {
  struct TcsDictNode *next;
  int32_t key_i;
  TcsString *key_s;
  size_t order_index;
  _Alignas(max_align_t) unsigned char value[];
} TcsDictNode;

typedef struct TcsDict {
  struct TcsDictNode **buckets; /* RAW heap buffer */
  size_t bucket_count;
  int32_t count;
  int32_t key_is_string;
  size_t value_size;
  struct TcsDictNode *
      *order; /* RAW heap buffer (insertion order; holes are NULL) */
  size_t order_len;
  size_t order_cap;
  uint32_t version;
} TcsDict;

static _Noreturn void tcs_fault(const char *kind);
static void *tcs_nonnull(void *object);
static bool tcs_string_equal(TcsString *left, TcsString *right);
static void *tcs_gc_alloc(uint32_t kind, const TcsLayout *layout, size_t size);
static void *tcs_alloc(size_t size); /* RAW (no pointers) */
static void tcs_gc_mark_statics(
    void); /* defined by the generated code (old-generation roots) */
static void tcs_gc_forward_statics(
    void); /* defined by the generated code (promotion at the boundary) */
static void tcs_wb(const void *owner); /* write barrier */
static void *tcs_gc_promote(void *pointer);
static void tcs_gc_forward_value(void *value, const TcsLayout *layout);

static int tcs_type_in_range(uint32_t type_id, uint32_t first, uint32_t last) {
  return type_id >= first && type_id <= last;
}

/* Explicit downcast: null passes through, fault if the type does not match. The
   runtime type is the tag in the GC header (readable even for non-classes such
   as boxes / arrays) */
static void *tcs_cast(void *object, uint32_t first, uint32_t last) {
  if (object != NULL &&
      !tcs_type_in_range(TCS_GC_HEADER(object)->type_id, first, last))
    tcs_fault("invalid-cast");
  return object;
}

/* object: references as is (structural types are tagged when boxed), scalars
 * are boxed */
typedef struct TcsBox {
  union {
    int32_t i;
    float f;
    bool b;
  } value;
} TcsBox;

static void *tcs_typed(void *object, uint32_t type_id) {
  if (object != NULL && TCS_GC_HEADER(object)->type_id == 0)
    TCS_GC_HEADER(object)->type_id = type_id;
  return object;
}

static TcsBox *tcs_box_new(uint32_t type_id) {
  TcsBox *box = tcs_alloc(sizeof(*box));
  TCS_GC_HEADER(box)->type_id = type_id;
  return box;
}

static void *tcs_box_i32(int32_t value) {
  TcsBox *box = tcs_box_new(TCS_TYPE_BOX_I32);
  box->value.i = value;
  return box;
}

static void *tcs_box_f32(float value) {
  TcsBox *box = tcs_box_new(TCS_TYPE_BOX_F32);
  box->value.f = value;
  return box;
}

static void *tcs_box_bool(bool value) {
  TcsBox *box = tcs_box_new(TCS_TYPE_BOX_BOOL);
  box->value.b = value;
  return box;
}

/* unbox only accepts a box of the matching type, as in C# (a boxed int cannot
   be unboxed to float) */
static TcsBox *tcs_unbox(void *object, uint32_t type_id) {
  tcs_nonnull(object);
  if (TCS_GC_HEADER(object)->type_id != type_id)
    tcs_fault("invalid-cast");
  return object;
}

static void *tcs_interface_cast(void *object, bool (*matches)(void *)) {
  if (object != NULL && !matches(object))
    tcs_fault("invalid-cast");
  return object;
}

/* Receiver for faults that the host installs (tcs_lib_on_fault of --lib).
   If it does not return and instead longjmps out to the host frame, the process
   continues. If it returns, the exit below ends it */
static void (*tcs_fault_handler)(const char *kind);

static _Noreturn void tcs_fault(const char *kind) {
  fprintf(stderr, "TinyC# fault: %s\n", kind);
  if (tcs_fault_handler != NULL)
    tcs_fault_handler(kind);
  exit(EXIT_FAILURE);
}

static void *tcs_nonnull(void *object) {
  if (object == NULL)
    tcs_fault("null");
  return object;
}

static TcsString *tcs_string_new(const unsigned char *data, size_t length) {
  TcsString *string;
  if (length > SIZE_MAX - sizeof(*string))
    tcs_fault("allocation-overflow");
  string = tcs_alloc(sizeof(*string) + length);
  TCS_GC_HEADER(string)->type_id = TCS_TYPE_STRING;
  string->length = length;
  if (length != 0 && data != NULL)
    memcpy(string->data, data, length);
  return string;
}

static TcsString *tcs_string_concat(TcsString *left, TcsString *right) {
  TcsString *result;
  tcs_nonnull(left);
  tcs_nonnull(right);
  if (right->length > SIZE_MAX - left->length)
    tcs_fault("string-length-overflow");
  result = tcs_string_new(NULL, left->length + right->length);
  if (left->length != 0)
    memcpy(result->data, left->data, left->length);
  if (right->length != 0)
    memcpy(result->data + left->length, right->data, right->length);
  return result;
}

static bool tcs_string_equal(TcsString *left, TcsString *right) {
  if (left == NULL || right == NULL)
    return left == right;
  return left->length == right->length &&
         (left->length == 0 ||
          memcmp(left->data, right->data, left->length) == 0);
}

static uint32_t tcs_f32_bits(float value) {
  uint32_t bits;
  memcpy(&bits, &value, sizeof(bits));
  return bits;
}

static TcsString *tcs_string_i32(int32_t value) {
  char buffer[32];
  int written = snprintf(buffer, sizeof(buffer), "%" PRId32, value);
  if (written < 0 || (size_t)written >= sizeof(buffer))
    tcs_fault("integer-format");
  return tcs_string_new((const unsigned char *)buffer, (size_t)written);
}

static TcsString *tcs_string_f32(float value) {
  char buffer[TCS_FORMAT_F32_MAX];
  size_t length = tcs_format_f32(value, buffer);
  return tcs_string_new((const unsigned char *)buffer, length);
}

/* Interpolation float formats G / E / F / N. Writes to the heap only when the
 * precision is large */
static TcsString *tcs_string_f32_spec(float value, char spec,
                                      int32_t precision) {
  char small[TCS_FORMAT_F32_SPEC_MAX('G', 0)];
  size_t size = TCS_FORMAT_F32_SPEC_MAX(spec, precision);
  char *buffer = size <= sizeof small ? small : malloc(size);
  size_t length;
  TcsString *result;
  if (buffer == NULL)
    tcs_fault("allocation");
  length = tcs_format_f32_spec(value, spec, precision, buffer);
  result = tcs_string_new((const unsigned char *)buffer, length);
  if (buffer != small)
    free(buffer);
  return result;
}

/* Interpolation int formats G / E / F / N / D / X. Writes to the heap only when
 * the precision is large */
static TcsString *tcs_string_i32_spec(int32_t value, char spec,
                                      int32_t precision) {
  char small[TCS_FORMAT_I32_SPEC_MAX(0)];
  size_t size = TCS_FORMAT_I32_SPEC_MAX(precision);
  char *buffer = size <= sizeof small ? small : malloc(size);
  size_t length;
  TcsString *result;
  if (buffer == NULL)
    tcs_fault("allocation");
  length = tcs_format_i32_spec(value, spec, precision, buffer);
  result = tcs_string_new((const unsigned char *)buffer, length);
  if (buffer != small)
    free(buffer);
  return result;
}

static TcsString *tcs_string_bool(bool value) {
  static const unsigned char true_text[] = "True";
  static const unsigned char false_text[] = "False";
  return value ? tcs_string_new(true_text, 4) : tcs_string_new(false_text, 5);
}

static TcsString *tcs_string_tostring(TcsString *value) {
  /* A null string is an empty string (same as .NET interpolation and Console)
   */
  static const unsigned char empty_text[] = "";
  return value == NULL ? tcs_string_new(empty_text, 0) : value;
}

static void tcs_print_newline(void) { fputc('\n', stdout); }

static void tcs_print_i32(int32_t value) { printf("%" PRId32 "\n", value); }

static void tcs_print_f32(float value) {
  char buffer[TCS_FORMAT_F32_MAX];
  size_t length = tcs_format_f32(value, buffer);
  fwrite(buffer, 1, length, stdout);
  fputc('\n', stdout);
}

static void tcs_print_bool(bool value) {
  fputs(value ? "true\n" : "false\n", stdout);
}

static void tcs_print_string(TcsString *value) {
  /* A null string is an empty line */
  if (value != NULL && value->length != 0)
    fwrite(value->data, 1, value->length, stdout);
  fputc('\n', stdout);
}

static float tcs_f32(uint32_t bits) {
  float value;
  memcpy(&value, &bits, sizeof(value));
  return value;
}

static int32_t tcs_idiv(int32_t left, int32_t right) {
  if (right == 0)
    tcs_fault("divide-by-zero");
  if (left == INT32_MIN && right == -1)
    tcs_fault("divide-overflow");
  return left / right;
}

static int32_t tcs_irem(int32_t left, int32_t right) {
  if (right == 0)
    tcs_fault("divide-by-zero");
  if (left == INT32_MIN && right == -1)
    tcs_fault("divide-overflow");
  return left % right;
}

/* i32 + - * and unary - of checked(expr) (il-spec §5). Computes in 64 bits and
 * checks the range */
static int32_t tcs_checked_narrow(int64_t value) {
  if (value > INT32_MAX || value < INT32_MIN)
    tcs_fault("arithmetic-overflow");
  return (int32_t)value;
}

static int32_t tcs_checked_add(int32_t left, int32_t right) {
  return tcs_checked_narrow((int64_t)left + right);
}

static int32_t tcs_checked_sub(int32_t left, int32_t right) {
  return tcs_checked_narrow((int64_t)left - right);
}

static int32_t tcs_checked_mul(int32_t left, int32_t right) {
  return tcs_checked_narrow((int64_t)left * right);
}

static int32_t tcs_checked_neg(int32_t value) {
  return tcs_checked_narrow(-(int64_t)value);
}

static int32_t tcs_shl(int32_t value, int32_t count) {
  return (int32_t)((uint32_t)value << ((uint32_t)count & UINT32_C(31)));
}

static int32_t tcs_shr(int32_t value, int32_t count) {
  uint32_t shift = (uint32_t)count & UINT32_C(31);
  uint32_t bits = (uint32_t)value;
  if (shift == 0)
    return value;
  if (value >= 0)
    return (int32_t)(bits >> shift);
  return (int32_t)((bits >> shift) | (~UINT32_C(0) << (32 - shift)));
}

static void tcs_digest_float(float value) {
  uint32_t bits = tcs_f32_bits(value);
  unsigned int shift;
  for (shift = 0; shift < 32; shift += 8) {
    tcs_digest ^= (bits >> shift) & UINT32_C(0xff);
    tcs_digest *= UINT32_C(16777619);
  }
}
