/* tcs2c の GC を、本物の runtime のまま有界に全列挙する harness。
   GcEnumerationTests が tcs2c --lib の出力 (Node / G の harness program) の
   末尾に連結して 1 つの translation unit としてコンパイルする。

   - runtime の malloc / calloc / realloc / free は -D で h_* に向け、bump 確保の
     arena に置く (free は毒値で埋めるだけ)。arena の使用部分 + static 変数を
     memcpy すれば GC の状態をそのまま snapshot / restore できる
   - mutator の op (new / root 代入 / field store / List / Dict / frame 境界) を
     深さ制限 DFS で列挙する。root から到達する構造 (object の flags と dirty
     登録、List の長さ、Dict の chain 順を含む) が同じ状態は 1 回だけ見る
   - store は emit された関数 (tcs_m_G_*) を呼ぶので、バリアの置き方は emitter と
     runtime のものがそのまま検査対象になる。harness が足すのは op の選び方だけ
   - frame 境界ごとに shadow graph (op を鏡写しした抽象 heap) と実 heap を root
     から lockstep に歩き、到達可能な slot が shadow と同じ object を指し
     (NoDangling / NoLostObject)、旧世代の list が到達可能な object とちょうど
     一致する (OldGenSound) ことを確かめる。違反は最短の op 列 (witness) を
     印字して exit 1 */

#ifndef TCS_GC_STRESS
#error "GcEnumeration needs -DTCS_GC_STRESS=1 (every boundary collects the old generation)"
#endif
#ifndef H_DEPTH
#define H_DEPTH 8
#endif
#ifndef H_MAX_NODES
#define H_MAX_NODES 3           /* 到達可能な Node の上限 (new の適用条件) */
#endif
#ifndef H_LIST_MAX
#define H_LIST_MAX 2
#endif
#ifndef H_VISITED_BITS
#define H_VISITED_BITS 22
#endif
#define H_ARENA_BYTES ((size_t)1 << 20)
#define H_DICT_KEYS 2
#define H_GROW_BASE 100
#define H_GROW_KEYS 33          /* 33 個目の put で bucket が 16 → 32 に倍化する */
#define H_MAX_IDS (H_DEPTH + 1)
#define H_KEY_MAX 16384
#define H_EXPECT_MAX 1024

#include <signal.h>

/* ---- arena ---- */
typedef struct HBlock { size_t size; size_t pad; } HBlock;
static _Alignas(max_align_t) unsigned char h_arena[H_ARENA_BYTES];
static size_t h_arena_used;

static void
h_die(const char *message)
{
    fprintf(stderr, "harness: %s\n", message);
    exit(2);
}

void *
h_malloc(size_t size)
{
    size_t need = (size + 15) & ~(size_t)15;
    HBlock *block;
    if (h_arena_used + sizeof(*block) + need > H_ARENA_BYTES) h_die("arena exhausted");
    block = (HBlock *)(h_arena + h_arena_used);
    block->size = size;
    h_arena_used += sizeof(*block) + need;
    memset(block + 1, 0xA5, need);
    return block + 1;
}

void *
h_calloc(size_t count, size_t size)
{
    void *pointer = h_malloc(count * size);
    memset(pointer, 0, count * size);
    return pointer;
}

void
h_free(void *pointer)
{
    HBlock *block;
    if (pointer == NULL) return;
    block = (HBlock *)pointer - 1;
    memset(pointer, 0xA5, block->size);
}

void *
h_realloc(void *pointer, size_t size)
{
    void *grown = h_malloc(size);
    if (pointer != NULL) {
        size_t old = ((HBlock *)pointer - 1)->size;
        memcpy(grown, pointer, old < size ? old : size);
        h_free(pointer);
    }
    return grown;
}

/* ---- shadow graph ---- */
enum { SL_S, SL_H, SL_L0, SL_L1, SL_COUNT, SL_NONE = SL_COUNT };
static const char *const h_slot_name[SL_COUNT + 1] = { "S", "H", "L0", "L1", "null" };

typedef struct HNode {
    int a;
    int has_list;
    int list_len;
    int list_items[H_LIST_MAX];
    int has_ilist;              /* List<int>: 要素 layout が無く、再確保のバリアだけが効く経路 */
    int ilist_len;              /* 要素 i の値は i + 1 */
    int has_dict;
    int dict_has[H_DICT_KEYS];
    int dict_val[H_DICT_KEYS];
    int grown;
    int serial;
    Tcs_Node *real;             /* frame 中は確定。境界では check が root から引き直す */
} HNode;

typedef struct HShadow {
    int count;
    int serial;
    int slot[SL_COUNT];
    HNode nodes[H_MAX_IDS];
} HShadow;

static HShadow h_sh;
static void *h_held;            /* host の hold slot (SL_H の実体) */

static Tcs_Node *
h_real(int id)
{
    return id < 0 ? NULL : h_sh.nodes[id].real;
}

/* ---- ops ---- */
enum {
    OP_NEW, OP_ROOT, OP_STORE, OP_LOAD, OP_NEWLIST, OP_LISTADD, OP_LISTREMOVE,
    OP_NEWILIST, OP_ILISTADD, OP_NEWDICT, OP_DICTPUT, OP_DICTREMOVE, OP_DICTGROW,
    OP_FRAME
};

typedef struct HOp { int kind; int x; int y; int k; } HOp;
static HOp h_ops[256];
static int h_op_count;

static void
h_op(int kind, int x, int y, int k)
{
    HOp op = { kind, x, y, k };
    h_ops[h_op_count++] = op;
}

static void
h_build_ops(void)
{
    int x, y, k;
    h_op(OP_FRAME, 0, 0, 0);
    for (k = SL_L0; k <= SL_L1; k++) h_op(OP_NEW, k, 0, 0);
    for (x = SL_S; x <= SL_H; x++)
        for (y = 0; y <= SL_NONE; y++) if (y != x) h_op(OP_ROOT, x, y, 0);
    for (x = 0; x < SL_COUNT; x++)
        for (y = 0; y <= SL_NONE; y++) h_op(OP_STORE, x, y, 0);
    for (k = SL_L0; k <= SL_L1; k++)
        for (x = 0; x < SL_COUNT; x++) h_op(OP_LOAD, k, x, 0);
    for (x = 0; x < SL_COUNT; x++) h_op(OP_NEWLIST, x, 0, 0);
    for (x = 0; x < SL_COUNT; x++)
        for (y = 0; y <= SL_NONE; y++) h_op(OP_LISTADD, x, y, 0);
    for (x = 0; x < SL_COUNT; x++) h_op(OP_LISTREMOVE, x, 0, 0);
    for (x = 0; x < SL_COUNT; x++) h_op(OP_NEWILIST, x, 0, 0);
    for (x = 0; x < SL_COUNT; x++) h_op(OP_ILISTADD, x, 0, 0);
    for (x = 0; x < SL_COUNT; x++) h_op(OP_NEWDICT, x, 0, 0);
    for (x = 0; x < SL_COUNT; x++)
        for (k = 0; k < H_DICT_KEYS; k++)
            for (y = 0; y <= SL_NONE; y++) h_op(OP_DICTPUT, x, y, k);
    for (x = 0; x < SL_COUNT; x++)
        for (k = 0; k < H_DICT_KEYS; k++) h_op(OP_DICTREMOVE, x, 0, k);
    for (x = 0; x < SL_COUNT; x++) h_op(OP_DICTGROW, x, 0, 0);
}

static void
h_describe(const HOp *op, char *out, size_t size)
{
    const char *x = h_slot_name[op->x];
    const char *y = h_slot_name[op->y];
    switch (op->kind) {
    case OP_NEW: snprintf(out, size, "%s = new", x); break;
    case OP_ROOT: snprintf(out, size, "%s = %s", x, y); break;
    case OP_STORE: snprintf(out, size, "%s.A = %s", x, y); break;
    case OP_LOAD: snprintf(out, size, "%s = %s.A", x, y); break;
    case OP_NEWLIST: snprintf(out, size, "%s.L = new", x); break;
    case OP_LISTADD: snprintf(out, size, "%s.L.Add(%s)", x, y); break;
    case OP_LISTREMOVE: snprintf(out, size, "%s.L.RemoveAt(0)", x); break;
    case OP_NEWILIST: snprintf(out, size, "%s.I = new", x); break;
    case OP_ILISTADD: snprintf(out, size, "%s.I.Add(n)", x); break;
    case OP_NEWDICT: snprintf(out, size, "%s.D = new", x); break;
    case OP_DICTPUT: snprintf(out, size, "%s.D[%d] = %s", x, op->k, y); break;
    case OP_DICTREMOVE: snprintf(out, size, "%s.D.Remove(%d)", x, op->k); break;
    case OP_DICTGROW: snprintf(out, size, "%s.D grow (%d puts)", x, H_GROW_KEYS); break;
    default: snprintf(out, size, "frame"); break;
    }
}

/* ---- 探索の状態 ---- */
static const HOp *h_path[H_DEPTH];
static int h_path_len;
static size_t h_states, h_frames;
static size_t h_cov_old_young;      /* 旧 object の slot へ若い object を store */
static size_t h_cov_grow_old;       /* 旧 node を持つ Dict の rehash */
static size_t h_cov_remove_hazard;  /* 旧 owner の link が若い node を指すようになる remove */

static void
h_fail(const char *reason, int id)
{
    int i;
    char text[64];
    printf("violation: %s", reason);
    if (id >= 0) printf(" (node %d)", id);
    printf("\nwitness:");
    for (i = 0; i < h_path_len; i++) {
        h_describe(h_path[i], text, sizeof(text));
        printf("%s %s", i == 0 ? "" : ";", text);
    }
    printf("\n");
    exit(1);
}

static int
h_mark_reach(int id, int *seen)
{
    int i, n = 0;
    HNode *node;
    if (id < 0 || seen[id]) return 0;
    seen[id] = 1;
    node = &h_sh.nodes[id];
    n = 1 + h_mark_reach(node->a, seen);
    for (i = 0; i < node->list_len; i++) n += h_mark_reach(node->list_items[i], seen);
    for (i = 0; i < H_DICT_KEYS; i++)
        if (node->dict_has[i]) n += h_mark_reach(node->dict_val[i], seen);
    return n;
}

static int
h_reachable_nodes(void)
{
    int seen[H_MAX_IDS] = { 0 };
    int i, n = 0;
    for (i = 0; i < SL_COUNT; i++) n += h_mark_reach(h_sh.slot[i], seen);
    return n;
}

static int
h_slot_value(int slot)
{
    return slot == SL_NONE ? -1 : h_sh.slot[slot];
}

static int
h_applicable(const HOp *op)
{
    int x = op->x == SL_NONE ? -1 : h_sh.slot[op->x];
    int y = h_slot_value(op->y);
    HNode *node = x < 0 ? NULL : &h_sh.nodes[x];
    switch (op->kind) {
    case OP_NEW:
        return h_sh.count < H_MAX_IDS && h_reachable_nodes() < H_MAX_NODES;
    case OP_ROOT:
        return x != y;
    case OP_STORE:
        return node != NULL && node->a != y;
    case OP_LOAD:
        return y >= 0 && h_sh.slot[op->x] != h_sh.nodes[y].a;
    case OP_NEWLIST:
        return node != NULL && !node->has_list;
    case OP_LISTADD:
        return node != NULL && node->has_list && node->list_len < H_LIST_MAX;
    case OP_LISTREMOVE:
        return node != NULL && node->list_len > 0;
    case OP_NEWILIST:
        return node != NULL && !node->has_ilist;
    case OP_ILISTADD:
        return node != NULL && node->has_ilist && node->ilist_len < H_LIST_MAX;
    case OP_NEWDICT:
        return node != NULL && !node->has_dict;
    case OP_DICTPUT:
        return node != NULL && node->has_dict
            && !(node->dict_has[op->k] && node->dict_val[op->k] == y);
    case OP_DICTREMOVE:
        return node != NULL && node->has_dict && node->dict_has[op->k];
    case OP_DICTGROW:
        return node != NULL && node->has_dict && !node->grown;
    default:
        return 1;
    }
}

static int
h_is_old(const void *pointer)
{
    return pointer != NULL && (TCS_GC_HEADER(pointer)->flags & TCS_GC_OLD) != 0;
}

/* remove が書き換える link の owner (直前の node か Dict 自身) が旧世代で、
   新しく指す node が若いなら、バリア無しの store が若い object を旧 object
   の slot に置いた形。tcs_dict_remove にバリアは無いので、この形が境界で
   壊れないことが列挙で確かめたい性質 */
static void
h_cover_remove(TcsDict *dict, int32_t key)
{
    TcsDictNode *node, *prev = NULL;
    const void *owner = dict;
    if (dict->bucket_count == 0) return;
    node = dict->buckets[tcs_dict_hash(dict, key, NULL) % dict->bucket_count];
    for (; node != NULL; prev = node, node = node->next) {
        if (node->key_i != key) continue;
        if (prev != NULL) owner = prev;
        if (h_is_old(owner) && node->next != NULL && !h_is_old(node->next))
            h_cov_remove_hazard++;
        return;
    }
}

static void
h_apply(const HOp *op)
{
    int x = op->x == SL_NONE ? -1 : h_sh.slot[op->x];
    int y = h_slot_value(op->y);
    HNode *node = x < 0 ? NULL : &h_sh.nodes[x];
    Tcs_Node *real = h_real(x);
    Tcs_Node *value = h_real(y);
    int i;
    switch (op->kind) {
    case OP_NEW: {
        int id = h_sh.count++;
        HNode *fresh = &h_sh.nodes[id];
        memset(fresh, 0, sizeof(*fresh));
        fresh->a = -1;
        for (i = 0; i < H_DICT_KEYS; i++) fresh->dict_val[i] = -1;
        fresh->serial = ++h_sh.serial;
        fresh->real = tcs_m_G_New(fresh->serial);
        h_sh.slot[op->x] = id;
        break;
    }
    case OP_ROOT:
        if (op->x == SL_S) tcs_s_G_S = value;
        else h_held = value;
        h_sh.slot[op->x] = y;
        break;
    case OP_STORE:
        if (h_is_old(real) && value != NULL && !h_is_old(value)) h_cov_old_young++;
        tcs_m_G_SetA(real, value);
        node->a = y;
        break;
    case OP_LOAD: {
        Tcs_Node *loaded = tcs_m_G_GetA(value);
        if (loaded != h_real(h_sh.nodes[y].a)) h_fail("load read a pointer the shadow does not know", y);
        h_sh.slot[op->x] = h_sh.nodes[y].a;
        break;
    }
    case OP_NEWLIST:
        tcs_m_G_NewList(real);
        node->has_list = 1;
        node->list_len = 0;
        break;
    case OP_LISTADD:
        if (h_is_old(real->f_L) && value != NULL && !h_is_old(value)) h_cov_old_young++;
        tcs_m_G_ListAdd(real, value);
        node->list_items[node->list_len++] = y;
        break;
    case OP_LISTREMOVE:
        tcs_m_G_ListRemoveFirst(real);
        node->list_len--;
        for (i = 0; i < node->list_len; i++) node->list_items[i] = node->list_items[i + 1];
        break;
    case OP_NEWILIST:
        tcs_m_G_NewIntList(real);
        node->has_ilist = 1;
        node->ilist_len = 0;
        break;
    case OP_ILISTADD:
        tcs_m_G_IntListAdd(real, node->ilist_len + 1);
        node->ilist_len++;
        break;
    case OP_NEWDICT:
        tcs_m_G_NewDict(real);
        node->has_dict = 1;
        break;
    case OP_DICTPUT:
        if (h_is_old(real->f_D) && value != NULL && !h_is_old(value)) h_cov_old_young++;
        tcs_m_G_DictPut(real, op->k, value);
        node->dict_has[op->k] = 1;
        node->dict_val[op->k] = y;
        break;
    case OP_DICTREMOVE:
        h_cover_remove(real->f_D, op->k);
        tcs_m_G_DictRemove(real, op->k);
        node->dict_has[op->k] = 0;
        node->dict_val[op->k] = -1;
        break;
    case OP_DICTGROW:
        if (h_is_old(real->f_D)) h_cov_grow_old++;
        tcs_m_G_DictGrow(real);
        node->grown = 1;
        break;
    default:
        /* 境界: C# のスタックは空なので local は捨てる */
        h_sh.slot[SL_L0] = h_sh.slot[SL_L1] = -1;
        tcs_lib_gc();
        h_frames++;
        break;
    }
}

/* ---- 境界の検査: shadow と実 heap を root から lockstep に歩く ---- */
static TcsGcHeader *h_expect[H_EXPECT_MAX];
static size_t h_expect_count;

static void
h_expect_object(const void *payload, int kind, const char *what, int id)
{
    TcsGcHeader *hdr;
    size_t i;
    /* 毒値や解放済み領域の pointer は header を読む前に止める (crash でなく witness を出す) */
    if ((const unsigned char *)payload < h_arena + sizeof(HBlock) + sizeof(*hdr)
        || (const unsigned char *)payload >= h_arena + h_arena_used)
        h_fail("reachable slot points outside the heap", id);
    hdr = TCS_GC_HEADER(payload);
    if (hdr->kind != kind) h_fail(what, id);
    if ((hdr->flags & TCS_GC_OLD) == 0) h_fail("reachable object is not in the old generation", id);
    if (hdr->flags & (TCS_GC_DIRTY | TCS_GC_FORWARD | TCS_GC_MARK))
        h_fail("reachable object keeps a transient flag after the boundary", id);
    if (tcs_gc_nursery != NULL && (unsigned char *)hdr >= tcs_gc_nursery->data
        && (unsigned char *)hdr < tcs_gc_nursery->data + tcs_gc_nursery->capacity)
        h_fail("reachable object lives in the nursery after the boundary", id);
    for (i = 0; i < h_expect_count; i++)
        if (h_expect[i] == hdr) h_fail("two reachable objects share one address", id);
    if (h_expect_count == H_EXPECT_MAX) h_die("expect table full");
    h_expect[h_expect_count++] = hdr;
}

static void
h_check_visit(int id, Tcs_Node *pointer)
{
    HNode *node;
    int i;
    if (id < 0) {
        if (pointer != NULL) h_fail("slot that should be null points somewhere", -1);
        return;
    }
    if (pointer == NULL) h_fail("reachable object was lost (slot is null)", id);
    node = &h_sh.nodes[id];
    if (node->real != NULL) {
        if (node->real != pointer) h_fail("one shadow object reached at two addresses", id);
        return;
    }
    node->real = pointer;
    h_expect_object(pointer, TCS_KIND_OBJECT, "reachable node has the wrong kind", id);
    if (TCS_GC_HEADER(pointer)->layout != &tcs_layout_C_Node)
        h_fail("reachable node has the wrong layout", id);
    if (pointer->f_V != node->serial) h_fail("node payload changed (stale or overwritten)", id);
    h_check_visit(node->a, pointer->f_A);

    if (!node->has_list) {
        if (pointer->f_L != NULL) h_fail("list slot should be null", id);
    } else {
        TcsList *list = pointer->f_L;
        if (list == NULL) h_fail("list was lost", id);
        h_expect_object(list, TCS_KIND_LIST, "list has the wrong kind", id);
        if (list->length != (size_t)node->list_len) h_fail("list length changed", id);
        if (list->data != NULL) h_expect_object(list->data, TCS_KIND_RAW, "list buffer kind", id);
        else if (list->length != 0) h_fail("list buffer was lost", id);
        for (i = 0; i < node->list_len; i++)
            h_check_visit(node->list_items[i], ((Tcs_Node **)list->data)[i]);
    }

    if (!node->has_ilist) {
        if (pointer->f_I != NULL) h_fail("int list slot should be null", id);
    } else {
        TcsList *list = pointer->f_I;
        if (list == NULL) h_fail("int list was lost", id);
        h_expect_object(list, TCS_KIND_LIST, "int list has the wrong kind", id);
        if (list->length != (size_t)node->ilist_len) h_fail("int list length changed", id);
        if (list->data != NULL) h_expect_object(list->data, TCS_KIND_RAW, "int list buffer kind", id);
        else if (list->length != 0) h_fail("int list buffer was lost", id);
        for (i = 0; i < node->ilist_len; i++)
            if (((int32_t *)list->data)[i] != i + 1) h_fail("int list element changed", id);
    }

    if (!node->has_dict) {
        if (pointer->f_D != NULL) h_fail("dict slot should be null", id);
    } else {
        TcsDict *dict = pointer->f_D;
        size_t b, chain = 0;
        int32_t expected = node->grown ? H_GROW_KEYS : 0;
        if (dict == NULL) h_fail("dict was lost", id);
        h_expect_object(dict, TCS_KIND_DICT, "dict has the wrong kind", id);
        if (dict->buckets != NULL)
            h_expect_object(dict->buckets, TCS_KIND_RAW, "dict bucket buffer kind", id);
        for (b = 0; b < dict->bucket_count; b++) {
            TcsDictNode *entry;
            for (entry = dict->buckets[b]; entry != NULL; entry = entry->next) {
                h_expect_object(entry, TCS_KIND_DICT_NODE, "dict node has the wrong kind", id);
                chain++;
            }
        }
        for (i = 0; i < H_DICT_KEYS; i++) expected += node->dict_has[i];
        if (dict->count != expected || chain != (size_t)expected) h_fail("dict count changed", id);
        for (i = 0; i < H_DICT_KEYS; i++) {
            TcsDictNode *entry = tcs_dict_find(dict, i, NULL);
            if ((entry != NULL) != node->dict_has[i]) h_fail("dict key presence changed", id);
            if (entry != NULL) h_check_visit(node->dict_val[i], *(Tcs_Node **)entry->value);
        }
        for (i = 0; node->grown && i < H_GROW_KEYS; i++) {
            TcsDictNode *entry = tcs_dict_find(dict, H_GROW_BASE + i, NULL);
            if (entry == NULL || *(Tcs_Node **)entry->value != NULL)
                h_fail("dict filler entry changed", id);
        }
    }
}

static void
h_check(void)
{
    TcsGcHeader *hdr;
    size_t i, listed = 0;
    for (i = 0; i < (size_t)h_sh.count; i++) h_sh.nodes[i].real = NULL;
    h_expect_count = 0;
    h_check_visit(h_sh.slot[SL_S], tcs_s_G_S);
    h_check_visit(h_sh.slot[SL_H], h_held);
    for (hdr = tcs_gc_objects; hdr != NULL; hdr = hdr->next) {
        for (i = 0; i < h_expect_count; i++) if (h_expect[i] == hdr) break;
        if (i == h_expect_count) h_fail("old generation keeps an unreachable object", -1);
        listed++;
    }
    if (listed != h_expect_count) h_fail("old generation lost a reachable object", -1);
    if (tcs_gc_object_count != listed) h_fail("old generation count is off", -1);
    if (tcs_gc_nursery_objects != 0 || (tcs_gc_nursery != NULL && tcs_gc_nursery_chunks != 1))
        h_fail("nursery was not reset at the boundary", -1);
    if (tcs_gc_dirty_count != 0) h_fail("remembered set was not drained", -1);
}

/* ---- 状態の key (root からの到達構造 + GC が次に見る flags) ---- */
static unsigned char h_key[H_KEY_MAX];
static size_t h_key_len;
static int h_label[H_MAX_IDS];
static int h_next_label;

static void
h_key_byte(unsigned value)
{
    if (h_key_len == H_KEY_MAX) h_die("key buffer full");
    h_key[h_key_len++] = (unsigned char)value;
}

static void
h_key_u32(uint32_t value)
{
    h_key_byte(value); h_key_byte(value >> 8); h_key_byte(value >> 16); h_key_byte(value >> 24);
}

static void
h_key_header(const void *payload)
{
    TcsGcHeader *hdr;
    size_t i;
    if (payload == NULL) { h_key_byte(0xFD); return; }
    hdr = TCS_GC_HEADER(payload);
    h_key_byte(hdr->flags);
    for (i = 0; i < tcs_gc_dirty_count; i++) if (tcs_gc_dirty[i] == hdr) break;
    h_key_byte(i < tcs_gc_dirty_count);
}

static void
h_key_visit(int id)
{
    HNode *node;
    int i;
    if (id < 0) { h_key_byte(0xFF); return; }
    if (h_label[id] >= 0) { h_key_byte(0xFE); h_key_byte(h_label[id]); return; }
    h_label[id] = h_next_label++;
    h_key_byte(h_label[id]);
    node = &h_sh.nodes[id];
    h_key_header(node->real);
    h_key_visit(node->a);
    h_key_byte(node->has_list);
    if (node->has_list) {
        TcsList *list = node->real->f_L;
        h_key_header(list);
        h_key_header(list->data);
        h_key_u32((uint32_t)list->capacity);
        h_key_byte(node->list_len);
        for (i = 0; i < node->list_len; i++) h_key_visit(node->list_items[i]);
    }
    h_key_byte(node->has_ilist);
    if (node->has_ilist) {
        TcsList *list = node->real->f_I;
        h_key_header(list);
        h_key_header(list->data);
        h_key_u32((uint32_t)list->capacity);
        h_key_byte(node->ilist_len);
    }
    h_key_byte(node->has_dict);
    if (node->has_dict) {
        TcsDict *dict = node->real->f_D;
        size_t b;
        h_key_header(dict);
        h_key_header(dict->buckets);
        h_key_u32((uint32_t)dict->bucket_count);
        for (b = 0; b < dict->bucket_count; b++) {
            TcsDictNode *entry;
            for (entry = dict->buckets[b]; entry != NULL; entry = entry->next) {
                h_key_u32((uint32_t)entry->key_i);
                h_key_header(entry);
            }
            h_key_byte(0xFC);
        }
        for (i = 0; i < H_DICT_KEYS; i++)
            h_key_visit(node->dict_has[i] ? node->dict_val[i] : -1);
    }
}

static void
h_key_build(void)
{
    int i;
    h_key_len = 0;
    h_next_label = 0;
    for (i = 0; i < H_MAX_IDS; i++) h_label[i] = -1;
    for (i = 0; i < SL_COUNT; i++) h_key_visit(h_sh.slot[i]);
}

typedef struct HVisited { uint64_t h1, h2; unsigned char remaining; } HVisited;
static HVisited h_visited[(size_t)1 << H_VISITED_BITS];
static size_t h_visited_count;

static uint64_t
h_fnv(uint64_t hash, uint64_t prime)
{
    size_t i;
    for (i = 0; i < h_key_len; i++) hash = (hash ^ h_key[i]) * prime;
    return hash;
}

/* 同じ状態を残り深さ以上で既に見ていれば 1 */
static int
h_seen(int remaining)
{
    uint64_t h1 = h_fnv(UINT64_C(0xcbf29ce484222325), UINT64_C(0x100000001b3));
    uint64_t h2 = h_fnv(UINT64_C(0x9e3779b97f4a7c15), UINT64_C(0x1000193));
    size_t mask = ((size_t)1 << H_VISITED_BITS) - 1;
    size_t at = (size_t)h1 & mask;
    for (;;) {
        HVisited *entry = &h_visited[at];
        if (entry->remaining == 0) {
            if (h_visited_count * 10 > ((size_t)1 << H_VISITED_BITS) * 7) h_die("visited table full");
            entry->h1 = h1;
            entry->h2 = h2;
            entry->remaining = (unsigned char)(remaining + 1);
            h_visited_count++;
            return 0;
        }
        if (entry->h1 == h1 && entry->h2 == h2) {
            if (entry->remaining >= (unsigned)remaining + 1) return 1;
            entry->remaining = (unsigned char)(remaining + 1);
            return 0;
        }
        at = (at + 1) & mask;
    }
}

/* ---- snapshot / restore ---- */
typedef struct HSnap {
    size_t arena_used;
    TcsChunk *nursery, *nursery_cur;
    size_t nursery_bytes, nursery_chunks;
    TcsGcHeader *objects;
    size_t object_count, live_bytes, allocated_since, threshold, collections, frames;
    size_t promoted_bytes, old_bytes, nursery_objects;
    unsigned call_depth;
    int collecting;
    TcsGcHeader **mark_stack;
    size_t mark_count, mark_capacity;
    TcsGcHeader **dirty;
    size_t dirty_count, dirty_capacity;
    void ***holds;
    size_t hold_count, hold_capacity;
    Tcs_Node *s;
    void *held;
    HShadow shadow;
    unsigned char arena[H_ARENA_BYTES];
} HSnap;

static HSnap h_snaps[H_DEPTH + 1];

#define H_SAVE(snap, field, var) ((snap)->field = (var))
#define H_LOAD(snap, field, var) ((var) = (snap)->field)
#define H_STATICS(ACT, snap) \
    ACT(snap, nursery, tcs_gc_nursery); ACT(snap, nursery_cur, tcs_gc_nursery_cur); \
    ACT(snap, nursery_bytes, tcs_gc_nursery_bytes); ACT(snap, nursery_chunks, tcs_gc_nursery_chunks); \
    ACT(snap, objects, tcs_gc_objects); ACT(snap, object_count, tcs_gc_object_count); \
    ACT(snap, live_bytes, tcs_gc_live_bytes); ACT(snap, allocated_since, tcs_gc_allocated_since); \
    ACT(snap, threshold, tcs_gc_threshold); ACT(snap, collections, tcs_gc_collections); \
    ACT(snap, frames, tcs_gc_frames); ACT(snap, promoted_bytes, tcs_gc_promoted_bytes); \
    ACT(snap, old_bytes, tcs_gc_old_bytes); ACT(snap, nursery_objects, tcs_gc_nursery_objects); \
    ACT(snap, call_depth, tcs_gc_call_depth); ACT(snap, collecting, tcs_gc_collecting); \
    ACT(snap, mark_stack, tcs_gc_mark_stack); ACT(snap, mark_count, tcs_gc_mark_count); \
    ACT(snap, mark_capacity, tcs_gc_mark_capacity); ACT(snap, dirty, tcs_gc_dirty); \
    ACT(snap, dirty_count, tcs_gc_dirty_count); ACT(snap, dirty_capacity, tcs_gc_dirty_capacity); \
    ACT(snap, holds, tcs_gc_holds); ACT(snap, hold_count, tcs_gc_hold_count); \
    ACT(snap, hold_capacity, tcs_gc_hold_capacity); ACT(snap, s, tcs_s_G_S); \
    ACT(snap, held, h_held); ACT(snap, arena_used, h_arena_used)

static void
h_snapshot(HSnap *snap)
{
    H_STATICS(H_SAVE, snap);
    snap->shadow = h_sh;
    memcpy(snap->arena, h_arena, h_arena_used);
}

static void
h_restore(const HSnap *snap)
{
    H_STATICS(H_LOAD, snap);
    h_sh = snap->shadow;
    memcpy(h_arena, snap->arena, snap->arena_used);
}

/* ---- DFS (反復深化: 違反は最短の witness で出る) ---- */
static void
h_dfs(int remaining)
{
    HSnap *snap = &h_snaps[h_path_len];
    int i;
    if (remaining == 0) return;
    h_snapshot(snap);
    for (i = 0; i < h_op_count; i++) {
        const HOp *op = &h_ops[i];
        if (!h_applicable(op)) continue;
        h_path[h_path_len++] = op;
        h_apply(op);
        if (op->kind == OP_FRAME) h_check();
        h_key_build();
        if (!h_seen(remaining - 1)) {
            h_states++;
            h_dfs(remaining - 1);
        }
        h_path_len--;
        h_restore(snap);
    }
}

/* runtime 自身が壊れた pointer を踏んで落ちても witness を出す */
static void
h_on_signal(int sig)
{
    char reason[48];
    snprintf(reason, sizeof(reason), "runtime crashed (signal %d)", sig);
    h_fail(reason, -1);
}

int
main(void)
{
    int depth;
    signal(SIGSEGV, h_on_signal);
    signal(SIGILL, h_on_signal);
    signal(SIGFPE, h_on_signal);
    h_build_ops();
    tcs_lib_init();
    tcs_lib_hold(&h_held);
    h_sh.count = 0;
    h_sh.serial = 0;
    for (depth = 0; depth < SL_COUNT; depth++) h_sh.slot[depth] = -1;
    for (depth = 1; depth <= H_DEPTH; depth++) {
        memset(h_visited, 0, sizeof(h_visited));
        h_visited_count = 0;
        h_dfs(depth);
    }
    printf("ok depth=%d ops=%d states=%zu frames=%zu old_young_stores=%zu grow_old=%zu remove_hazard=%zu\n",
        H_DEPTH, h_op_count, h_states, h_frames, h_cov_old_young, h_cov_grow_old,
        h_cov_remove_hazard);
    return 0;
}
