/* tcs VM — TinyC# の IL から作った bytecode (.tcsb) を実行する VM。
   設計は doc/vm-design.md。V1 の核に V2 の一部 (string / List / Dictionary / closure /
   instance method) を足した版 (同書 §17)。 */
#ifndef TCSVM_H
#define TCSVM_H

#include <stddef.h>
#include <stdbool.h>
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
  TVM_RESTART = 3, /* tvm_apply: 当てられない差分 (理由は tvm_error)。host は restart の導線へ */
} TvmStatus;

typedef struct TvmVm TvmVm;

typedef struct TvmConfig {
  size_t stack_slots; /* 0 は既定 (1M slot) */
  size_t max_frames;  /* 0 は既定 (64K) */
  int gc_stress;      /* 0 以外: 確保の地点とフレーム境界ごとに回収する (検査用) */
} TvmConfig;

TvmVm *tvm_new(const TvmConfig *config);
void tvm_free(TvmVm *vm);

/* bytecode を読み込み、検査し、link して static initializer を走らせる。
   失敗したら TVM_LOAD_ERROR か TVM_FAULT を返し、tvm_error() が理由を返す。 */
TvmStatus tvm_load(TvmVm *vm, const uint8_t *bytes, size_t size);

/* hot reload (doc/vm-design.md §12、doc/object-model.md §6): 新版の module と計画 (tcs2vm --reload-from が作る)
   を当てる。フレーム境界 (VM の frame が無いとき) でだけ受け付ける。
   TVM_LOAD_ERROR / TVM_RESTART は当てていない (状態は旧版のまま。理由は tvm_error)。
   TVM_OK のとき tvm_error() が空でなければ、initializer か OnReload の fault の警告 (反映は巻き戻さない)。 */
TvmStatus tvm_apply(TvmVm *vm, const uint8_t *module, size_t size, const uint8_t *plan, size_t plan_size);

/* ---- shared heap (doc/object-model.md §10.2) ----
   C (tcs2c) の module と 1 つの heap と GC を共有する。tcs2c --lib --vm-host の glue の tcs_vm_register が入れる
   (host が直接呼ぶことは無い)。共有すると、VM の object は tcs2c の GC が辿り (kind FOREIGN)、回収は frame の境界
   (tcs_lib_gc / tvm_frame_end) だけで起き、C の module が VM の object を static などに持てる。1 process に 1 つの VM。 */
typedef void (*TvmSlotFn)(void **slot);
typedef struct TvmHeap {
  void *(*alloc)(uint32_t kind, size_t size); /* 0 で埋めた payload */
  void (*enter)(void);                        /* VM の呼び出しに入る (回収を止める) */
  void (*leave)(void);
  void (*barrier)(const void *owner);         /* owner に参照を書いた (write barrier) */
  void (*frame)(void);                        /* frame の境界の回収 */
  /* 全部の object の参照の場所と C の module の root を fn に渡す (reload の張り替え。境界で呼ぶ) */
  void (*visit_all)(TvmSlotFn fn);
  /* 旧世代の object の payload を順に渡す (境界の後なので若い object は無い) */
  void (*each)(void (*fn)(void *payload, uint32_t kind, void *ctx), void *ctx);
} TvmHeap;
void tvm_use_heap(TvmVm *vm, const TvmHeap *heap); /* tvm_load の前 */
/* runtime/core の GC が呼ぶ: VM の root と、VM の object の参照の場所 */
void tvm_heap_roots(TvmSlotFn fn);
void tvm_heap_refs(void *payload, TvmSlotFn fn);

/* tcs2vm --watch の stream の vm_batch (base64 を戻した byte 列: u32 の module の長さ、module、u32 の計画の長さ、
   計画) を当てる。戻り値は tvm_apply と同じ */
TvmStatus tvm_apply_batch(TvmVm *vm, const uint8_t *batch, size_t size);

/* ---- host (doc/vm-design.md §8.5、§13) ----
   host の関数は tvm_load の前に名前で登録する。名前は IL の host の名前 (--ref の stub の `Eng.Gfx.Begin` なら
   "eng.gfx.begin"、static の値 `Gfx.MainTex` は引数の無い "eng.gfx.main_tex")。params / rets は葉の種類の列:
   i int (enum を含む)、f float、b bool、s string、o ほかの参照 (host の data class の object、List など)。
   struct の値は葉に展開する。module が使う host の関数が無いか、呼び出しの形が合わなければ読み込みエラー。
   host の関数は args の値を読み、rets に書く。失敗は tvm_host_fault の戻り値を返す (C# の trace が付く)。
   host の関数の中では回収は起きない (回収は interpreter の地点とフレーム境界だけ)。 */
typedef TvmStatus (*TvmHostFn)(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user);
int tvm_host(TvmVm *vm, const char *name, const char *params, const char *rets, TvmHostFn fn, void *user);
TvmStatus tvm_host_fault(TvmVm *vm, const char *message);

/* host が VM の object を読み書きする。object の payload は型記述子の layout (doc/object-model.md §5) で、
   field の offset は名前で引く (IL の名前。stub の field `Title` なら "title")。 */
int32_t tvm_field_offset(TvmVm *vm, const char *type, const char *field); /* 無ければ -1 */
void *tvm_object_new(TvmVm *vm, const char *type);                        /* field は 0。型が無ければ NULL */
const char *tvm_object_type(const void *object);                           /* 型の名前 */
void *tvm_string_new(TvmVm *vm, const char *data, size_t len);
const char *tvm_string_data(const void *string, size_t *len);

/* 型を名前で引く (class は IL の名前 "Eng_Opts"、List は "List<Eng_Opts>"、配列は "int[]"、要素の int / float / bool も同じ綴り)。
   無ければ NULL。型の表は読み込みと reload で作り直すので、host が型や offset を cache するなら tvm_types_version が
   変わったら引き直す (版は VM をまたいで同じ値にならないので、VM を作り直したときも変わる) */
typedef struct TcsType TvmTypeInfo;
const TvmTypeInfo *tvm_type(TvmVm *vm, const char *name);
uint32_t tvm_types_version(TvmVm *vm);
int32_t tvm_type_field_offset(const TvmTypeInfo *type, const char *field); /* 無ければ -1 */
void *tvm_object_new_of(TvmVm *vm, const TvmTypeInfo *type);              /* class だけ。field は 0 */

/* host と共有する外部 data class (--ref の class) の object が持つ host の値 (doc/object-model.md §3)。
   payload の type_id の後、8 byte に揃えた位置 */
#define TVM_HOST_VALUE(object) (*(uint64_t *)((char *)(object) + 8))

/* List と配列。要素は data から並ぶ (int / float は 4 byte、bool は 1 byte、参照は pointer、struct は型の大きさ)。
   tvm_list_new_of / tvm_array_new_of は count 個の 0 の要素を持つ。tvm_list_resize は数を変え、増えた要素は 0。
   host の関数の中では回収が起きないので、作った object を root に持たずに続けて確保してよい */
void *tvm_list_new_of(TvmVm *vm, const TvmTypeInfo *list_type, int32_t count);
bool tvm_list_resize(TvmVm *vm, void *list, int32_t count);
int32_t tvm_list_count(const void *list);
void *tvm_list_data(const void *list);
void *tvm_array_new_of(TvmVm *vm, const TvmTypeInfo *array_type, int32_t len);
/* 型を持たない 0 で埋めた byte 列 (GC が持つ)。object の参照の field に置けば、その object が生きている間生きる
   (host の data class が host の state を GC に持たせる。VM の code からは読まない) */
void *tvm_blob_new(TvmVm *vm, size_t size);
int32_t tvm_array_len(const void *array);
void *tvm_array_data(const void *array);

/* handle: host (C の module) が frame を跨いで VM の object を持つ (doc/object-model.md §10)。GC の root になり、
   reload で object が作り直されたら handle は新しい object を指す。番号は 1 から (0 は無効)。 */
/* VM の closure (delegate) を host が呼ぶ (X-4)。args / rets は関数型の葉。VM の呼び出しの中 (host の関数から) でも外でも呼べる */
TvmStatus tvm_invoke(TvmVm *vm, void *closure, const TvmSlot *args, size_t nargs, TvmSlot *rets, size_t nrets);

/* VM の object の instance method を名前で呼ぶ (X-4: C の module の interface を VM の class が実装したとき、C の
   dispatcher が呼ぶ)。itable から名前と引数の数で引く。args / rets は self を除いた葉 */
TvmStatus tvm_invoke_method(TvmVm *vm, void *object, const char *method, const TvmSlot *args, size_t nargs,
                            TvmSlot *rets, size_t nrets);

/* C の module の delegate を VM が呼ぶ (X-4)。sig は引数の葉の種類の文字、':'、戻り値の種類の文字 ("i:b"、"s:" など。
   tvm_host と同じ文字)。CALLD が C の closure (VM の object でない delegate) に会うと、関数型の sig の invoke を呼ぶ */
typedef TvmStatus (*TvmDelegateFn)(void *closure, const TvmSlot *args, TvmSlot *rets);
int tvm_host_delegate(TvmVm *vm, const char *sig, TvmDelegateFn fn);

/* C の module の型 (shared heap、doc/object-model.md §10.6)。tvm_load の前に登録する (reload の新版にも写る)。
   tvm_host_type: VM の class の名前 name (--ref の class) の object に C の module の type_id を付け、C の module が作った
   その型の object を VM の型の test (`is` / cast) で同じ型として扱う。VM だけの class の type_id は TVM_TYPE_ID_VM 以上
   (C の module の番号と重ならない)。
   tvm_host_iface: --ref の interface。VM の型の test は C の object を check で調べる。
   tvm_implements: VM の object が --ref の interface name を実装するか (C の module の interface の判定が呼ぶ) */
#define TVM_TYPE_ID_VM 0x40000000u
typedef bool (*TvmIfaceCheck)(void *object);
int tvm_host_type(TvmVm *vm, const char *name, uint32_t type_id);
int tvm_host_iface(TvmVm *vm, const char *name, TvmIfaceCheck check);
bool tvm_implements(TvmVm *vm, const void *object, const char *iface);

typedef uint32_t TvmHandle;
TvmHandle tvm_hold(TvmVm *vm, void *object);
void *tvm_handle_get(TvmVm *vm, TvmHandle h);
void tvm_release(TvmVm *vm, TvmHandle h);

/* 関数を名前で引く ("Particles.Main")。無ければ -1。 */
int32_t tvm_find(TvmVm *vm, const char *name);
/* module が entry と記した関数 (無ければ -1)。 */
int32_t tvm_entry(TvmVm *vm);

/* 引数は args[0..nargs)、戻り値は rets[0..nrets) に書く。
   nargs が関数の引数の slot 数と違うか、nrets が戻り値の slot 数より小さければ TVM_LOAD_ERROR。 */
TvmStatus tvm_call(TvmVm *vm, int32_t func, const TvmSlot *args, size_t nargs, TvmSlot *rets,
                   size_t nrets);

/* フレーム境界 (host が entry から戻った後)。溜まった確保が閾値を超えていれば回収する。 */
void tvm_frame_end(TvmVm *vm);
/* 直前の回収の後に生きていた byte 数 */
size_t tvm_gc_live(TvmVm *vm);

/* 最後のエラーか fault の message (C# の stack trace を含む)。 */
const char *tvm_error(TvmVm *vm);

/* ---- 検査 (tcsvm_debug.c): --trace の記録、呼び出しの並び、値の木、状態の控え ----
   tcs vm --trace PREFIX で作った module は、PREFIX の下のファイルの文の頭と代入の後に記録を置く。 */

/* 前に呼んでから通った行を渡し、記録を空にする (順番は決まっていない) */
typedef void (*TvmTraceLineFn)(void *user, const char *file, uint32_t line);
void tvm_trace_take_lines(TvmVm *vm, TvmTraceLineFn fn, void *user);
/* tvm_trace_take_lines と同じだが、key で覚えた object (tvm_inspect_pin) と同じ型の別の object を self にして通っただけの行は渡さない。
   class の instance の method の行は (行, self) の組で記録してある。key で覚えた object が無ければ全部を渡す */
void tvm_trace_take_lines_for(TvmVm *vm, const char *key, TvmTraceLineFn fn, void *user);
/* 前に呼んでから代入した値を、場所ごとに最後の値を短い文字にして渡し、記録を空にする。
   label は代入先の C# の書き方。数はそのまま、bool は true / false、文字列は "" で囲み 40 byte で切る、null は null、
   class と struct は型の名前 (field が 4 つ以下で数と bool だけなら {X=1 Y=2} を付ける)、List と配列は [要素の数]、
   Dictionary は {…} */
typedef void (*TvmTraceValueFn)(void *user, const char *file, uint32_t line, uint32_t col, const char *label,
                                const char *text);
void tvm_trace_take_values(TvmVm *vm, TvmTraceValueFn fn, void *user);
/* host の関数の中から、呼び出しの並び (今の関数から外へ) のファイルの path と行を渡す */
typedef void (*TvmFrameFn)(void *user, const char *file, uint32_t line);
void tvm_stack(TvmVm *vm, TvmFrameFn fn, void *user);
/* tvm_stack と同じに並べ、段ごとに self の object の Id (TvmInspectNode.id と同じ書き方) も渡す。
   self は --trace の class の instance の method の段だけで、それ以外は "" */
typedef void (*TvmFrameSelfFn)(void *user, const char *file, uint32_t line, const char *id);
void tvm_stack_self(TvmVm *vm, TvmFrameSelfFn fn, void *user);
/* --trace のファイルの class (見つけた順) */
uint32_t tvm_traced_class_count(TvmVm *vm);
const char *tvm_traced_class(TvmVm *vm, uint32_t i);

/* 値の木の節。path は C# の書き方 (Game.Field.Items[2]、Dictionary の項目は ["key"] か [3]) */
typedef struct TvmInspectNode {
  char path[512];
  char name[256];
  char text[160];
  const char *type; /* class の instance ならその class の名前 (それ以外は NULL) */
  char id[32];      /* object の値なら object の番地の文字列 (string と値型は "") */
  int32_t count;    /* 子の数 */
  bool cycle;       /* 祖先と同じ object (開けない) */
} TvmInspectNode;
/* path の節の子のうち offset 番目から count 個を out に書き、書いた数を返す。
   path が空なら根で、子は entry の class (先頭) と --trace の class (名前の順)。
   class の子は static field (宣言の順)、instance と struct の子は field (名前の順)、List と配列は要素、
   Dictionary は項目 (key の順)。null の field と関数は返さない。path が辿れなければ 0 */
int32_t tvm_inspect(TvmVm *vm, const char *entry, const char *path, int32_t offset, int32_t count,
                    TvmInspectNode *out);
/* entry と --trace の class の static から幅優先で辿り、class の instance を見つけた順に 1 度ずつ fn に渡す
   (path は tvm_inspect の path、id は TvmInspectNode.id。同じ object は最初に見つけた path で 1 度だけ)。
   深さ max_depth まで (static field が 1)、List と配列は先頭の max_children 個まで辿る。
   節ごとに tvm_inspect で辿り直すより速い (path を根から読み直さず、値の文字を作らない) */
typedef void (*TvmInstanceFn)(void *user, const char *type, const char *path, const char *id);
void tvm_inspect_instances(TvmVm *vm, const char *entry, uint32_t max_depth, uint32_t max_children, TvmInstanceFn fn,
                           void *user);
/* path の節の object を key で弱く覚える (回収は止めない)。辿れないか object でなければ忘れる */
void tvm_inspect_pin(TvmVm *vm, const char *key, const char *entry, const char *path);
/* key で覚えた object がまだ回収されておらず、その id (TvmInspectNode.id) が id か */
bool tvm_inspect_pinned(TvmVm *vm, const char *key, const char *id);

/* 負荷: clock (ns を返す時計。GC が止めた長さを測る) を渡すと数え始め、NULL で止める。
   数えている間、--trace の行ごとに、確保 (byte と回数) と、tvm_profile_tick で知らされた時間を足す。
   時間は、知らされたときに実行していた行に付く (時計は TSC を直接読んでも VM の文 1 つより重く、文ごとに読むと時計の時間が行の時間に化けるので、時計は読まない)。
   印の無い関数 (--trace の外の関数と host の関数) の中の時間と確保は、それを呼んだ行に付く。
   GC が止めた時間は行に足さず、止まりとして別に数える。host から呼んだ一番外の呼び出しの外で知らされた時間は、どの行にも付けない。 */
typedef uint64_t (*TvmClockFn)(void);
void tvm_profile_enable(TvmVm *vm, TvmClockFn clock);
/* ns だけ時間が経ったことを知らせる。別の thread から呼んでよい (一定の間隔で呼ぶと、行ごとの時間の見積もりになる) */
void tvm_profile_tick(TvmVm *vm, uint64_t ns);
/* 前に取り出してから足した行を渡し、0 に戻す (順番は決まっていない。bytes は header を含む) */
typedef void (*TvmProfileLineFn)(void *user, const char *file, uint32_t line, uint64_t ns, uint64_t bytes, uint32_t allocs);
void tvm_profile_take_lines(TvmVm *vm, TvmProfileLineFn fn, void *user);
/* 前に取り出してから GC が止めた回を古い順に渡して忘れる (直近 TVM_PROFILE_GCS 回まで。live は回収の後に生きている byte) */
#define TVM_PROFILE_GCS 64
typedef void (*TvmProfileGcFn)(void *user, uint64_t ns, size_t live);
void tvm_profile_take_gc(TvmVm *vm, TvmProfileGcFn fn, void *user);
/* frame の境目をまたいで持ち越す物: 前の tvm_frame_end の後に --trace の行で作られ、今 entry と --trace の class の static
   から辿れる物を、作った行とそれを持つ field ("型.field"。static は "class.field") ごとに数える。
   持つ field は、根から一番近い、前からある物の field (新しく作った物の中の物は、その新しい物を持つ field)。
   一度数えた物は次から数えない。bytes は header を含む */
typedef void (*TvmProfileKeptFn)(void *user, const char *file, uint32_t line, const char *holder, uint32_t count,
                                 uint64_t bytes);
void tvm_profile_take_kept(TvmVm *vm, const char *entry, TvmProfileKeptFn fn, void *user);
/* 数えている間の今の --trace の行 (host の関数の中では、それを呼んだ行)。数えていないか行の外なら false */
bool tvm_profile_site(TvmVm *vm, const char **file, uint32_t *line);

/* 状態の控え: entry と --trace の class の static と、そこから辿れる object の中身 (string は写さない) */
typedef struct TvmSnapshot TvmSnapshot;
/* 控えて持つ (控えた object は回収しない)。失敗したら NULL */
TvmSnapshot *tvm_snapshot_take(TvmVm *vm, const char *entry);
/* 控えた static と object の中身を、同じ static と同じ object に書き戻す */
void tvm_snapshot_restore(TvmVm *vm, const TvmSnapshot *s);
void tvm_snapshot_free(TvmVm *vm, TvmSnapshot *s);
/* reload が object を作り直すたびに増える数 (それより前の控えは戻せない) */
uint32_t tvm_layout_version(TvmVm *vm);
/* 控えの中の、path の節の数の値 (path の親は今の値の木で決め、控えの中のその親の値を読む)。数でなければ false */
bool tvm_snapshot_number(TvmVm *vm, const TvmSnapshot *s, const char *entry, const char *path, float *out);

#ifdef __cplusplus
}
#endif

#endif
