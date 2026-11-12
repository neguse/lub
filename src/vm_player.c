// tcs VM の player の library (include/lub/lub_vm.h): tcs vm が作った bytecode (.tcsb) を tcs VM (tcs/tcsvm) で動かす。
// 既製の lub_vm は src/vm_main.c の main を付けたもの。
// frame の骨格は host API (src/host_api.c) が持ち、ここは VM の読み込み、entry の呼び出し、hot reload だけ。
// API は lub-gen vm が作る host の関数 (src/gen/vm_api_gen.c) で、player はゲームを選ばない (stub の全部を登録する)。
//
//   lub_vm <Entry>.tcsb [--entry CLASS] [--overlay CLASS] [--extension LIB] [--fixed-dt S] [--capture PNG] [--capture-frame N] [--digest]
//   lub_vm <Entry>.csproj [...]
//
// .csproj を渡すと、入力 (src/vm_project.h) を集めて tcs vm --watch を背後に張り、保存のたびに差分を当てる。
// 差分を当てた後に entry と overlay の OnReload() を呼び、compile error の保存では OnBuildFailed(string errors) を呼ぶ。
// どちらも log に 1 行出す (`lub: reloaded` / `lub: reload failed: ...`)。
// 差分に知らせ (tcs の reload_notes。生きている instance に入らない初期化子の変更など) があれば、OnReload の後に OnReloadNotes(string notes) を呼ぶ。
// ProjectReference に Lub="Overlay" を付けた参照先の class は、ゲームに重ねる overlay にする。
// overlay の callback は OnInit がゲームの後、OnEvent がゲームの前、OnFrame がゲームの後 (ゲームの上に描く)、OnQuit がゲームの前。
// overlay が Input.Grab で取った種類の event はゲームに渡さない。
// X.ref.cs の隣の native の library は、その関数を VM の host の関数として登録する拡張として読む (include/lub/lub_vm_extension.h)。
//
// entry class は --entry (IL の名前、namespace は '_' で繋ぐ)、無ければ .tcsb の basename。
// OnFrame(float) は必須、OnInit / OnEvent(EventData) / OnQuit は任意。
// hot reload: <Entry>.batch (tcs vm --watch の vm_batch。module と計画) が現れたら当てて消す。
// 当てられない差分 (restart) と <Entry>.tcsb の書き直しは、OnQuit の後に新しい VM を作って OnInit から始める。
// App.Restart も frame の終わりに同じ作り直しをする。
// 作り直すたびに log に 1 行出す (`lub: restarted`)。
// <Entry>.rev (playground が置く build の revision) があれば、読み込みと batch の後に
// `@@tcs_commit {"revision":N,"ok":...}` を出す (playground の commit ACK)。
// 当てた revision 以下の batch (ACK の再送) は当てずに ACK だけ返す。
// `lub export <os> <entry> -o <dir>` は、画面も GPU も使わずに起動して OnInit の後に OnExport を 1 回呼び、
// その OS の player が動かす snapshot を dir に書く (backend_export.c)。
// snapshot の bytecode は <Entry>.tcsb で、player は引数なしで起動すると、実行ファイルの隣の .tcsb を動かす (src/main.c)。
// 誤り (C# の例外に当たる VM の fault) は log に出し、その frame を失敗にして続ける。
// fault の処置は、失敗した callback の class (entry か overlay) の static ErrorAction OnError(string) に聞く (無ければ Log)。
// OnError 自身が失敗するか Log 以外を返したら Abort (終了コード 1 で終わる)。
#include "api_internal.h"
#include "app.h"
#include "host_api.h"
#include <SDL3/SDL.h>
#include <lub/lub_host.h>
#include <lub/lub_vm.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef __EMSCRIPTEN__
#include <emscripten.h>
#endif

#include "gen/vm_api_gen.c"
#include "pass.h"
#include "pick.h"
#include "rewind.h"
#ifndef __EMSCRIPTEN__
#define VM_PROJECT                                                             \
  1 // .csproj から動かす (tcs の子 process を張る。native だけ)
#include "backend.h"
#include "tcs_build.h"
#include "vm_project.h"
#endif

// entry class と overlay の callback の関数の番号 (無ければ -1)
typedef struct Hooks {
  char cls[256];
  int32_t init, frame, event, quit, reload, build_failed, reload_notes, error,
      export_;
} Hooks;

// 結果の文字列 (次の同じ呼び出しまで有効)
typedef struct StrArena {
  char *p;
  size_t len, cap;
} StrArena;

#define MAX_EXTENSIONS 32

// VM 1 つを動かす player の状態
typedef struct Player {
  LubContext *ctx;
  // 生成した binding の状態。VM は lub.vm (無ければ NULL)
  VmLub lub;
  char module[1024];
  char batch[1024];
  char rev[1024];
  int32_t acked; // ACK を返した revision (無ければ -1)
  int64_t module_mtime;
  Hooks game, tool;
  // native の拡張 (include/lub/lub_vm_extension.h)。VM を作るたびに呼ぶ
  LubVmExtensionFn extensions[MAX_EXTENSIONS];
  int nextensions;
  // .csproj から動かすときの入力と tcs vm --watch
  bool project_mode;
  char csproj[1024];
#ifdef VM_PROJECT
  bool exporting; // lub export
  VmProject project;
  TcsPipeline tcs;
  // 次の vm_batch に付ける reload_notes の本文
  char *notes;
  size_t notes_len;
  uint64_t next_scan;
#endif
#ifdef __EMSCRIPTEN__
  bool shutting_down;
#endif
  // 検査の口 (LubDebugHooks) が返す結果の置き場
  StrArena trace_arena;
  size_t *trace_at;
  LubStr *trace_strs;
  int32_t trace_n, trace_cap, trace_at_cap;
  StrArena value_arena;
  size_t *value_at; // site と text の組
  LubTraceValue *values;
  int32_t value_n, value_cap, value_at_cap;
  TvmInspectNode *nodes;
  LubInspectNode *lub_nodes;
  int32_t node_cap, lub_node_cap;
  SDL_Thread *profile_thread;
  SDL_AtomicInt profile_run;
  StrArena profile_arena;
  size_t *profile_at;
  LubProfileLine *profile;
  int32_t profile_n, profile_cap, profile_at_cap;
  LubGcPause *pauses;
  int32_t pause_n, pause_cap;
  // 持ち越しの 1 件の文字列 (site と holder) は arena の中の位置で持ち、全部を足した後に引く
  StrArena kept_arena;
  size_t *kept_at;
  LubKeptObject *kept;
  int32_t kept_n, kept_cap, kept_at_cap;
  // 個体の 3 つの文字列 (type、path、id) は arena の中の位置で持ち、全部を足した後に引く
  StrArena inst_arena;
  size_t *inst_at;
  LubInspectInstance *inst;
  int32_t inst_n, inst_cap, inst_at_cap;
} Player;

// SDL の callback (lub_vm_app_*) が動かす player
static Player g_player = {.acked = -1};

static void call_reload(Player *p);

static bool is_csproj(const char *path) {
  size_t n = strlen(path);
  return n > 7 && !SDL_strcasecmp(path + n - 7, ".csproj");
}

static int64_t file_mtime(const char *path) {
  SDL_PathInfo info;
  return SDL_GetPathInfo(path, &info) ? (int64_t)info.modify_time : -1;
}

// playground が置いた revision (無ければ -1)
static int32_t read_revision(Player *p) {
  size_t size = 0;
  char *text =
      SDL_GetPathInfo(p->rev, NULL) ? SDL_LoadFile(p->rev, &size) : NULL;
  if (!text)
    return -1;
  int32_t rev = (int32_t)strtol(text, NULL, 10);
  SDL_free(text);
  return rev;
}

static void ack(Player *p, int32_t rev, bool ok, const char *error, double ms) {
  if (rev < 0)
    return;
  char safe[256] = {0};
  for (size_t i = 0; error && error[i] && i + 1 < sizeof(safe); i++)
    safe[i] =
        (error[i] == '"' || error[i] == '\\' || (unsigned char)error[i] < 32)
            ? ' '
            : error[i];
  printf(
      "@@tcs_commit {\"revision\":%d,\"ok\":%s,\"commitTimeMs\":%.2f%s%s%s}\n",
      (int)rev, ok ? "true" : "false", ms, error ? ",\"error\":\"" : "",
      error ? safe : "", error ? "\"" : "");
  fflush(stdout);
  if (ok)
    p->acked = rev;
}

static void log_fault(Player *p, const char *what) {
  SDL_Log("lub: error in %s: %s", what, tvm_error(p->lub.vm));
}

static int32_t find_hook(Player *p, const char *cls, const char *method) {
  char name[320];
  SDL_snprintf(name, sizeof(name), "%s.%s", cls, method);
  return tvm_find(p->lub.vm, name);
}

static void bind_hooks(Player *p, Hooks *h) {
  h->init = find_hook(p, h->cls, "OnInit");
  h->frame = find_hook(p, h->cls, "OnFrame");
  h->event = find_hook(p, h->cls, "OnEvent");
  h->quit = find_hook(p, h->cls, "OnQuit");
  h->reload = find_hook(p, h->cls, "OnReload");
  h->build_failed = find_hook(p, h->cls, "OnBuildFailed");
  h->reload_notes = find_hook(p, h->cls, "OnReloadNotes");
  h->error = find_hook(p, h->cls, "OnError");
  h->export_ = find_hook(p, h->cls, "OnExport");
}

static void clear_hooks(Hooks *h) {
  h->init = h->frame = h->event = h->quit = h->reload = h->build_failed =
      h->reload_notes = h->error = h->export_ = -1;
}

// 関数の番号は reload で変わるので、読み込みと reload の後に引き直す。
// 型の offset も同じ時に確かめる。
static bool bind_entry(Player *p) {
  char error[512];
  if (!vm_lub_verify(p->lub.vm, error, sizeof(error))) {
    SDL_Log("lub: the module does not match the host: %s", error);
    return false;
  }
  bind_hooks(p, &p->game);
  if (p->tool.cls[0])
    bind_hooks(p, &p->tool);
  if (p->game.frame < 0) {
    SDL_Log("lub: %s has no static OnFrame(float)", p->game.cls);
    return false;
  }
  return true;
}

// 今 VM で走らせている callback と、その VM (落ちたときに出す)。VM の外では NULL
static const char *g_calling;
static TvmVm *g_calling_vm;

static void crash_site(void *user, const char *file, uint32_t line) {
  (void)user;
  fprintf(stderr, "  at %s:%u\n", file, (unsigned)line);
}

// 落ちたとき (SIGSEGV など) に、走らせていた callback と C# の呼び出しの段を出してから、既定の処置で落ちる。
// signal の中では本来呼べない関数も使うが、どのみち落ちる所なので、出せるだけ出す
static void on_crash(int sig) {
  fprintf(stderr, "lub: crashed (signal %d) %s%s\n", sig,
          g_calling ? "in " : "outside the VM", g_calling ? g_calling : "");
  if (g_calling_vm && g_calling)
    tvm_stack(g_calling_vm, crash_site, NULL);
  fflush(stderr);
  signal(sig, SIG_DFL);
  raise(sig);
}

static bool call_fn(Player *p, int32_t fn, const char *what,
                    const TvmSlot *args, size_t nargs) {
  if (!p->lub.vm || fn < 0)
    return true;
  const char *outer = g_calling;
  TvmVm *outer_vm = g_calling_vm;
  g_calling = what;
  g_calling_vm = p->lub.vm;
  TvmStatus st = tvm_call(p->lub.vm, fn, args, nargs, NULL, 0);
  g_calling = outer;
  g_calling_vm = outer_vm;
  if (st != TVM_OK) {
    log_fault(p, what);
    return false;
  }
  return true;
}

// 失敗した callback の処置を、その class の OnError に聞く (無ければ Log)
static void call_on_error(Player *p, const Hooks *h) {
  if (!p->lub.vm || h->error < 0)
    return;
  // tvm_error は次の呼び出しで書き換わるので、先に VM の string にする
  const char *message = tvm_error(p->lub.vm);
  TvmSlot args[1], rets[1];
  args[0].p = tvm_string_new(p->lub.vm, message, strlen(message));
  rets[0].i = 0;
  int action = LUB_ERROR_ACTION_ABORT;
  if (tvm_call(p->lub.vm, h->error, args, 1, rets, 1) != TVM_OK)
    log_fault(p, "OnError");
  else if (rets[0].i == LUB_ERROR_ACTION_LOG)
    action = LUB_ERROR_ACTION_LOG;
  if (action == LUB_ERROR_ACTION_ABORT)
    lub_app_quit(p->ctx, 1);
}

// overlay の callback の間だけ App.in_overlay を立てる (Input.Grab と、入力を隠す判定が見る)
static bool call_hook(Player *p, const Hooks *h, int32_t fn, const char *what,
                      const TvmSlot *args, size_t nargs) {
  App *app = lub_api_app(p->ctx);
  bool overlay = h == &p->tool;
  if (overlay)
    app->in_overlay = true;
  bool ok = call_fn(p, fn, what, args, nargs);
  if (!ok)
    call_on_error(p, h);
  if (overlay)
    app->in_overlay = false;
  return ok;
}

static void profile_stop(Player *p);

static void free_vm(Player *p) {
  // Rewind の控えは VM の中にあるので、VM より先に捨てる
  if (p->ctx)
    rewind_drop(lub_api_app(p->ctx));
  // 行ごとの負荷の時間を知らせる thread は VM を触るので、VM より先に止める
  profile_stop(p);
  if (p->lub.vm)
    tvm_free(p->lub.vm);
  p->lub.vm = NULL;
  clear_hooks(&p->game);
  clear_hooks(&p->tool);
  App *app = lub_api_app(p->ctx);
  if (app) {
    app->overlay_grab_keyboard = false;
    app->overlay_grab_mouse = false;
  }
}

// 起動と作り直しの OnInit (ゲームの後に overlay)
static void call_init(Player *p) {
  call_hook(p, &p->game, p->game.init, "OnInit", NULL, 0);
  call_hook(p, &p->tool, p->tool.init, "overlay OnInit", NULL, 0);
}

// OnQuit (overlay はゲームを包むので先)
static void call_quit(Player *p) {
  call_hook(p, &p->tool, p->tool.quit, "overlay OnQuit", NULL, 0);
  call_hook(p, &p->game, p->game.quit, "OnQuit", NULL, 0);
}

// native の拡張を読む (失敗は log に出して飛ばす。その関数を使う module は読み込みで止まる)
static void load_extension(Player *p, const char *path) {
  if (p->nextensions >= MAX_EXTENSIONS)
    return;
  SDL_SharedObject *lib = SDL_LoadObject(path);
  LubVmExtensionFn fn =
      lib ? (LubVmExtensionFn)SDL_LoadFunction(lib, LUB_VM_EXTENSION_SYMBOL)
          : NULL;
  if (!fn) {
    SDL_Log("lub: cannot load the native extension %s: %s", path,
            SDL_GetError());
    return;
  }
  SDL_Log("lub: native extension %s", path);
  p->extensions[p->nextensions++] = fn;
}

// .tcsb から VM を作る。
// 失敗したら VM は無いまま (次の書き直しを待つ)。
static bool load_module(Player *p) {
  p->module_mtime = file_mtime(p->module);
  size_t size = 0;
  void *bytes = SDL_LoadFile(p->module, &size);
  if (!bytes) {
    SDL_Log("lub: cannot read %s: %s", p->module, SDL_GetError());
    return false;
  }
  p->lub.vm = tvm_new(NULL);
  bool ok = p->lub.vm && vm_lub_register(&p->lub, p->lub.vm);
  static const LubVmExtensionApi api = {LUB_VM_EXTENSION_VERSION, tvm_host,
                                        tvm_host_fault, tvm_string_new,
                                        tvm_string_data};
  for (int i = 0; ok && i < p->nextensions; i++)
    if (!p->extensions[i](&api, p->lub.vm)) {
      SDL_Log("lub: a native extension failed to register its functions");
      ok = false;
    }
  if (ok && tvm_load(p->lub.vm, bytes, size) != TVM_OK) {
    SDL_Log("lub: cannot load %s: %s", p->module, tvm_error(p->lub.vm));
    ok = false;
  }
  SDL_free(bytes);
  if (ok)
    ok = bind_entry(p);
  if (!ok)
    free_vm(p);
  return ok;
}

// OnQuit (古い VM) の後に新しい VM を OnInit から始める。
// 物理の world はゲームの状態なので捨て、key で宣言する資源は残す。
// 新しい VM を作れたら、OnInit の前に log に 1 行出す (`lub: restarted`)。
static void restart(Player *p, const char *why) {
  SDL_Log("lub: %s, starting the VM again from %s", why, p->module);
  call_quit(p);
  free_vm(p);
  App *app = lub_api_app(p->ctx);
  phys2d_state_shutdown(&app->phys);
  phys2d_state_init(&app->phys);
  phys3d_state_shutdown(&app->phys3);
  phys3d_state_init(&app->phys3);
  uint64_t t0 = SDL_GetTicksNS();
  if (!load_module(p)) {
    ack(p, read_revision(p), false, "the module failed to load", 0);
    return;
  }
  SDL_Log("lub: restarted");
  app->entry_restarting = true;
  call_init(p);
  app->entry_restarting = false;
  ack(p, read_revision(p), true, NULL, (double)(SDL_GetTicksNS() - t0) / 1e6);
}

// frame の境界で hot reload を見る (VM の frame が無いときだけ当てられる)
static void poll_reload(Player *p) {
  size_t size = 0;
  void *batch =
      SDL_GetPathInfo(p->batch, NULL) ? SDL_LoadFile(p->batch, &size) : NULL;
  if (batch) {
    SDL_RemovePath(p->batch);
    int32_t rev = read_revision(p);
    if (rev >= 0 && rev <= p->acked) { // 当てた build の再送
      SDL_free(batch);
      ack(p, rev, true, NULL, 0);
      return;
    }
    uint64_t t0 = SDL_GetTicksNS();
    if (!p->lub.vm) {
      SDL_free(batch);
      restart(p, "a batch arrived without a running VM");
      return;
    }
    TvmStatus st = tvm_apply_batch(p->lub.vm, batch, size);
    SDL_free(batch);
    if (st == TVM_RESTART) {
      SDL_Log("lub: reload needs a restart: %s", tvm_error(p->lub.vm));
      restart(p, "restart required");
      return;
    }
    if (st != TVM_OK) {
      SDL_Log("lub: reload failed: %s", tvm_error(p->lub.vm));
      ack(p, rev, false, tvm_error(p->lub.vm), 0);
      return;
    }
    if (tvm_error(p->lub.vm)[0])
      SDL_Log("lub: reload: %s", tvm_error(p->lub.vm));
    if (!bind_entry(p)) {
      restart(p, "the reloaded module does not bind");
      return;
    }
    call_reload(p);
    ack(p, rev, true, NULL, (double)(SDL_GetTicksNS() - t0) / 1e6);
    return;
  }
  int64_t mtime = file_mtime(p->module);
  if (mtime >= 0 && mtime != p->module_mtime)
    restart(p,
            p->lub.vm ? "the module was rewritten" : "the module was rebuilt");
}

// ---- 検査の口 (LubDebugHooks): --trace の記録、値の木、Rewind の状態の控え ----

static void arena_reset(StrArena *a) { a->len = 0; }

// 足した文字列の先頭の位置 (realloc で動くので、全部足してから指す)
static size_t arena_add(StrArena *a, const char *s, size_t n) {
  if (a->len + n + 1 > a->cap) {
    size_t cap = a->cap ? a->cap * 2 : 4096;
    while (cap < a->len + n + 1)
      cap *= 2;
    char *g = (char *)SDL_realloc(a->p, cap);
    if (!g)
      return (size_t)-1;
    a->p = g;
    a->cap = cap;
  }
  size_t at = a->len;
  memcpy(a->p + at, s, n);
  a->p[at + n] = '\0';
  a->len += n + 1;
  return at;
}

static LubStr arena_str(const StrArena *a, size_t at) {
  if (at == (size_t)-1)
    return (LubStr){"", 0};
  return (LubStr){a->p + at, (int32_t)strlen(a->p + at)};
}

static void *grow(void *p, int32_t *cap, int32_t need, size_t size) {
  if (need <= *cap)
    return p;
  int32_t c = *cap ? *cap : 64;
  while (c < need)
    c *= 2;
  void *g = SDL_realloc(p, (size_t)c * size);
  if (g)
    *cap = c;
  return g ? g : p;
}

static void trace_line(void *user, const char *file, uint32_t line) {
  Player *p = (Player *)user;
  char key[1100];
  int n = SDL_snprintf(key, sizeof key, "%s:%u", file, (unsigned)line);
  p->trace_at = (size_t *)grow(p->trace_at, &p->trace_at_cap, p->trace_n + 1,
                               sizeof(size_t));
  if (p->trace_n < p->trace_at_cap)
    p->trace_at[p->trace_n++] = arena_add(&p->trace_arena, key, (size_t)n);
}

static const char *cstr(LubStr s, char *buf, size_t cap);

// key に覚えた個体 (tvm_inspect_pin) と同じ class の別の個体だけが通った行を除く (key が空なら全部)
static void hook_take_trace(App *app, LubStr key, const LubStr **out,
                            int32_t *count) {
  Player *p = (Player *)app->debug_user;
  arena_reset(&p->trace_arena);
  p->trace_n = 0;
  if (!p->lub.vm)
    return;
  char k[64];
  tvm_trace_take_lines_for(p->lub.vm, cstr(key, k, sizeof k), trace_line, p);
  p->trace_strs =
      (LubStr *)grow(p->trace_strs, &p->trace_cap, p->trace_n, sizeof(LubStr));
  for (int32_t i = 0; i < p->trace_n && i < p->trace_cap; i++)
    p->trace_strs[i] = arena_str(&p->trace_arena, p->trace_at[i]);
  *out = p->trace_strs;
  *count = p->trace_n < p->trace_cap ? p->trace_n : p->trace_cap;
}

static void trace_value(void *user, const char *file, uint32_t line,
                        uint32_t col, const char *label, const char *text) {
  Player *p = (Player *)user;
  char site[1400];
  int n = SDL_snprintf(site, sizeof site, "%s:%u:%u:%s", file, (unsigned)line,
                       (unsigned)col, label);
  p->value_at = (size_t *)grow(p->value_at, &p->value_at_cap,
                               2 * p->value_n + 2, sizeof(size_t));
  if (2 * p->value_n + 2 > p->value_at_cap)
    return;
  p->value_at[2 * p->value_n] = arena_add(&p->value_arena, site, (size_t)n);
  p->value_at[2 * p->value_n + 1] =
      arena_add(&p->value_arena, text, strlen(text));
  p->value_n++;
}

static void hook_take_values(App *app, const LubTraceValue **out,
                             int32_t *count) {
  Player *p = (Player *)app->debug_user;
  arena_reset(&p->value_arena);
  p->value_n = 0;
  if (!p->lub.vm)
    return;
  tvm_trace_take_values(p->lub.vm, trace_value, p);
  p->values = (LubTraceValue *)grow(p->values, &p->value_cap, p->value_n,
                                    sizeof(LubTraceValue));
  int32_t n = p->value_n < p->value_cap ? p->value_n : p->value_cap;
  for (int32_t i = 0; i < n; i++) {
    p->values[i].site = arena_str(&p->value_arena, p->value_at[2 * i]);
    p->values[i].text = arena_str(&p->value_arena, p->value_at[2 * i + 1]);
  }
  *out = p->values;
  *count = n;
}

// LubStr を NUL で終わる文字列に写す (長すぎれば切る)
static const char *cstr(LubStr s, char *buf, size_t cap) {
  size_t n = s.ptr && s.len > 0 ? (size_t)s.len : 0;
  if (n >= cap)
    n = cap - 1;
  if (n)
    memcpy(buf, s.ptr, n);
  buf[n] = '\0';
  return buf;
}

static void hook_inspect(App *app, LubStr path, int32_t offset, int32_t count,
                         const LubInspectNode **out, int32_t *n) {
  Player *p = (Player *)app->debug_user;
  if (!p->lub.vm)
    return;
  if (count > 4096)
    count = 4096;
  p->nodes = (TvmInspectNode *)grow(p->nodes, &p->node_cap, count,
                                    sizeof(TvmInspectNode));
  p->lub_nodes = (LubInspectNode *)grow(p->lub_nodes, &p->lub_node_cap, count,
                                        sizeof(LubInspectNode));
  if (p->node_cap < count || p->lub_node_cap < count)
    return;
  char text[1024];
  int32_t got =
      tvm_inspect(p->lub.vm, p->game.cls, cstr(path, text, sizeof text), offset,
                  count, p->nodes);
  for (int32_t i = 0; i < got; i++) {
    const TvmInspectNode *t = &p->nodes[i];
    LubInspectNode *l = &p->lub_nodes[i];
    l->path = (LubStr){t->path, (int32_t)strlen(t->path)};
    l->name = (LubStr){t->name, (int32_t)strlen(t->name)};
    l->text = (LubStr){t->text, (int32_t)strlen(t->text)};
    l->type =
        t->type ? (LubStr){t->type, (int32_t)strlen(t->type)} : (LubStr){"", 0};
    l->id = (LubStr){t->id, (int32_t)strlen(t->id)};
    l->count = t->count;
    l->cycle = t->cycle;
  }
  *out = p->lub_nodes;
  *n = got;
}

static void hook_inspect_pin(App *app, LubStr key, LubStr path) {
  Player *p = (Player *)app->debug_user;
  char k[64], text[1024];
  if (p->lub.vm)
    tvm_inspect_pin(p->lub.vm, cstr(key, k, sizeof k), p->game.cls,
                    cstr(path, text, sizeof text));
}

static bool hook_inspect_pinned(App *app, LubStr key, LubStr id) {
  Player *p = (Player *)app->debug_user;
  char k[64], i[64];
  return p->lub.vm && tvm_inspect_pinned(p->lub.vm, cstr(key, k, sizeof k),
                                         cstr(id, i, sizeof i));
}

static void *hook_snapshot_take(App *app) {
  Player *p = (Player *)app->debug_user;
  return p->lub.vm ? tvm_snapshot_take(p->lub.vm, p->game.cls) : NULL;
}

static void hook_snapshot_restore(App *app, void *s) {
  Player *p = (Player *)app->debug_user;
  if (p->lub.vm)
    tvm_snapshot_restore(p->lub.vm, (const TvmSnapshot *)s);
}

static void hook_snapshot_free(App *app, void *s) {
  Player *p = (Player *)app->debug_user;
  if (p->lub.vm)
    tvm_snapshot_free(p->lub.vm, (TvmSnapshot *)s);
}

static bool hook_snapshot_number(App *app, void *s, LubStr path, float *out) {
  Player *p = (Player *)app->debug_user;
  char text[1024];
  return p->lub.vm &&
         tvm_snapshot_number(p->lub.vm, (const TvmSnapshot *)s, p->game.cls,
                             cstr(path, text, sizeof text), out);
}

static uint32_t hook_layout_version(App *app) {
  Player *p = (Player *)app->debug_user;
  return p->lub.vm ? tvm_layout_version(p->lub.vm) : 0;
}

// Gfx.Pick の呼び出し元: draw を呼んだ時の VM の呼び出しの並び (内側から 1 行に 1 つ "file:line")
typedef struct SiteBuf {
  char *out;
  size_t cap, len;
} SiteBuf;

// self のある段は、続けて "\t" と self の Id を書く (pick.c が分ける)
static void pick_site(void *user, const char *file, uint32_t line,
                      const char *id) {
  SiteBuf *b = (SiteBuf *)user;
  if (b->len + 1 >= b->cap)
    return;
  int n = SDL_snprintf(b->out + b->len, b->cap - b->len, "%s%s:%u%s%s",
                       b->len ? "\n" : "", file, (unsigned)line,
                       id[0] ? "\t" : "", id);
  if (n > 0)
    b->len = b->len + (size_t)n < b->cap ? b->len + (size_t)n : b->cap - 1;
}

static void vm_pick_sites(void *user, char *out, size_t cap) {
  Player *p = (Player *)user;
  SiteBuf b = {out, cap, 0};
  if (cap)
    out[0] = '\0';
  if (p->lub.vm)
    tvm_stack_self(p->lub.vm, pick_site, &b);
}

// ---- 行ごとの負荷 (tvm_profile_*) ----
// VM は文ごとに時計を読まない (時計は VM の文 1 つより重く、時計の時間が行の時間に化ける)。
// 別の thread が PROFILE_TICK_NS ごとに経った時間を知らせ、VM はそのとき実行していた行に足す。
// 時計 (SDL の ns) は GC が止めた長さを測るのにだけ渡す。

#define PROFILE_TICK_NS 250000

static uint64_t profile_clock(void) { return SDL_GetTicksNS(); }

// VM は thread を止めてから捨てるので、始めに読んだ VM を使い続ける
static int SDLCALL profile_ticker(void *data) {
  Player *p = (Player *)data;
  TvmVm *vm = p->lub.vm;
  uint64_t last = SDL_GetTicksNS();
  while (SDL_GetAtomicInt(&p->profile_run)) {
    SDL_DelayNS(PROFILE_TICK_NS);
    uint64_t now = SDL_GetTicksNS();
    tvm_profile_tick(vm, now - last);
    last = now;
  }
  return 0;
}

static void profile_stop(Player *p) {
  if (!p->profile_thread)
    return;
  SDL_SetAtomicInt(&p->profile_run, 0);
  SDL_WaitThread(p->profile_thread, NULL);
  p->profile_thread = NULL;
}

static void hook_profile_lines(App *app, bool on) {
  Player *p = (Player *)app->debug_user;
  profile_stop(p);
  if (!p->lub.vm)
    return;
  tvm_profile_enable(p->lub.vm, on ? profile_clock : NULL);
  if (!on)
    return;
  // thread を作れない環境 (thread の無い web) では時間を数えず、確保と GC の止まりだけを数える
  SDL_SetAtomicInt(&p->profile_run, 1);
  p->profile_thread = SDL_CreateThread(profile_ticker, "lub_profile", p);
}

static void profile_line(void *user, const char *file, uint32_t line,
                         uint64_t ns, uint64_t bytes, uint32_t allocs) {
  Player *p = (Player *)user;
  char site[1100];
  int n = SDL_snprintf(site, sizeof site, "%s:%u", file, (unsigned)line);
  p->profile_at = (size_t *)grow(p->profile_at, &p->profile_at_cap,
                                 p->profile_n + 1, sizeof(size_t));
  p->profile = (LubProfileLine *)grow(p->profile, &p->profile_cap,
                                      p->profile_n + 1, sizeof(LubProfileLine));
  if (p->profile_n >= p->profile_at_cap || p->profile_n >= p->profile_cap)
    return;
  p->profile_at[p->profile_n] = arena_add(&p->profile_arena, site, (size_t)n);
  LubProfileLine *l = &p->profile[p->profile_n++];
  l->ms = (float)((double)ns / 1e6);
  l->bytes = bytes > INT32_MAX ? INT32_MAX : (int32_t)bytes;
  l->allocs = allocs > INT32_MAX ? INT32_MAX : (int32_t)allocs;
}

static void hook_take_profile(App *app, const LubProfileLine **out,
                              int32_t *count) {
  Player *p = (Player *)app->debug_user;
  arena_reset(&p->profile_arena);
  p->profile_n = 0;
  if (!p->lub.vm)
    return;
  tvm_profile_take_lines(p->lub.vm, profile_line, p);
  // 文字列は arena が全部を持った後に引く (arena は伸びると場所が変わる)
  for (int32_t i = 0; i < p->profile_n; i++)
    p->profile[i].site = arena_str(&p->profile_arena, p->profile_at[i]);
  *out = p->profile;
  *count = p->profile_n;
}

static void gc_pause(void *user, uint64_t ns, size_t live) {
  Player *p = (Player *)user;
  p->pauses = (LubGcPause *)grow(p->pauses, &p->pause_cap, p->pause_n + 1,
                                 sizeof(LubGcPause));
  if (p->pause_n >= p->pause_cap)
    return;
  p->pauses[p->pause_n].ms = (float)((double)ns / 1e6);
  p->pauses[p->pause_n].live_kb = (int32_t)(live / 1024);
  p->pause_n++;
}

static void hook_take_gc_pauses(App *app, const LubGcPause **out,
                                int32_t *count) {
  Player *p = (Player *)app->debug_user;
  p->pause_n = 0;
  if (!p->lub.vm)
    return;
  tvm_profile_take_gc(p->lub.vm, gc_pause, p);
  *out = p->pauses;
  *count = p->pause_n;
}

static void kept_object(void *user, const char *file, uint32_t line,
                        const char *holder, uint32_t count, uint64_t bytes) {
  Player *p = (Player *)user;
  char site[1100];
  int n = SDL_snprintf(site, sizeof site, "%s:%u", file, (unsigned)line);
  p->kept_at = (size_t *)grow(p->kept_at, &p->kept_at_cap, 2 * (p->kept_n + 1),
                              sizeof(size_t));
  p->kept = (LubKeptObject *)grow(p->kept, &p->kept_cap, p->kept_n + 1,
                                  sizeof(LubKeptObject));
  if (2 * (p->kept_n + 1) > p->kept_at_cap || p->kept_n >= p->kept_cap)
    return;
  p->kept_at[2 * p->kept_n] = arena_add(&p->kept_arena, site, (size_t)n);
  p->kept_at[2 * p->kept_n + 1] =
      arena_add(&p->kept_arena, holder, SDL_strlen(holder));
  LubKeptObject *k = &p->kept[p->kept_n++];
  k->count = count > INT32_MAX ? INT32_MAX : (int32_t)count;
  k->bytes = bytes > INT32_MAX ? INT32_MAX : (int32_t)bytes;
}

static void hook_take_kept(App *app, const LubKeptObject **out,
                           int32_t *count) {
  Player *p = (Player *)app->debug_user;
  arena_reset(&p->kept_arena);
  p->kept_n = 0;
  if (!p->lub.vm)
    return;
  tvm_profile_take_kept(p->lub.vm, p->game.cls, kept_object, p);
  for (int32_t i = 0; i < p->kept_n; i++) {
    p->kept[i].site = arena_str(&p->kept_arena, p->kept_at[2 * i]);
    p->kept[i].holder = arena_str(&p->kept_arena, p->kept_at[2 * i + 1]);
  }
  *out = p->kept;
  *count = p->kept_n;
}

static bool hook_profile_site(App *app, char *out, int32_t cap) {
  Player *p = (Player *)app->debug_user;
  const char *file;
  uint32_t line;
  if (!p->lub.vm || !tvm_profile_site(p->lub.vm, &file, &line))
    return false;
  SDL_snprintf(out, (size_t)cap, "%s:%u", file, (unsigned)line);
  return true;
}

static void inspect_instance(void *user, const char *type, const char *path,
                             const char *id) {
  Player *p = (Player *)user;
  p->inst_at = (size_t *)grow(p->inst_at, &p->inst_at_cap, 3 * (p->inst_n + 1),
                              sizeof(size_t));
  p->inst = (LubInspectInstance *)grow(p->inst, &p->inst_cap, p->inst_n + 1,
                                       sizeof(LubInspectInstance));
  if (3 * (p->inst_n + 1) > p->inst_at_cap || p->inst_n >= p->inst_cap)
    return;
  p->inst_at[3 * p->inst_n] = arena_add(&p->inst_arena, type, SDL_strlen(type));
  p->inst_at[3 * p->inst_n + 1] =
      arena_add(&p->inst_arena, path, SDL_strlen(path));
  p->inst_at[3 * p->inst_n + 2] = arena_add(&p->inst_arena, id, SDL_strlen(id));
  p->inst_n++;
}

static void hook_inspect_instances(App *app, int32_t max_depth,
                                   int32_t max_children,
                                   const LubInspectInstance **out,
                                   int32_t *count) {
  Player *p = (Player *)app->debug_user;
  arena_reset(&p->inst_arena);
  p->inst_n = 0;
  if (!p->lub.vm)
    return;
  tvm_inspect_instances(p->lub.vm, p->game.cls, (uint32_t)max_depth,
                        (uint32_t)max_children, inspect_instance, p);
  for (int32_t i = 0; i < p->inst_n; i++) {
    p->inst[i].type = arena_str(&p->inst_arena, p->inst_at[3 * i]);
    p->inst[i].path = arena_str(&p->inst_arena, p->inst_at[3 * i + 1]);
    p->inst[i].id = arena_str(&p->inst_arena, p->inst_at[3 * i + 2]);
  }
  *out = p->inst;
  *count = p->inst_n;
}

static const LubDebugHooks g_debug_hooks = {
    hook_take_trace,        hook_take_values,    hook_inspect,
    hook_inspect_pin,       hook_inspect_pinned, hook_snapshot_take,
    hook_snapshot_restore,  hook_snapshot_free,  hook_snapshot_number,
    hook_layout_version,    hook_profile_lines,  hook_take_profile,
    hook_take_gc_pauses,    hook_take_kept,      hook_profile_site,
    hook_inspect_instances,
};

// overlay が Input.Grab で取っている種類の event か
static bool event_grabbed(const App *app, const LubEventData *e) {
  switch (e->kind) {
  case LUB_EVENT_KIND_KEY_DOWN:
  case LUB_EVENT_KIND_KEY_UP:
    return app->overlay_grab_keyboard;
  case LUB_EVENT_KIND_MOUSE_BUTTON_DOWN:
  case LUB_EVENT_KIND_MOUSE_BUTTON_UP:
  case LUB_EVENT_KIND_MOUSE_MOTION:
  case LUB_EVENT_KIND_MOUSE_WHEEL:
    return app->overlay_grab_mouse;
  default:
    return false;
  }
}

// overlay が先に受け取る。
// overlay がその場で Grab した event もゲームには渡さない
static void on_event(Player *p, const LubEventData *e) {
  if (!p->lub.vm)
    return;
  TvmSlot args[1];
  if (p->tool.event >= 0) {
    args[0].p = vm_lub_event(&p->lub, e);
    call_hook(p, &p->tool, p->tool.event, "overlay OnEvent", args, 1);
  }
  App *app = lub_api_app(p->ctx);
  if (p->game.event < 0 || event_grabbed(app, e) || rewind_paused(app))
    return;
  args[0].p = vm_lub_event(&p->lub, e);
  call_hook(p, &p->game, p->game.event, "OnEvent", args, 1);
}

static bool on_frame(Player *p, float dt) {
  if (!p->lub.vm)
    return true;
  App *app = lub_api_app(p->ctx);
  // Rewind: 記録中は控え、止めていれば止めた frame の状態に戻して、控えた入力と dt で作り直す
  double game_dt = dt;
  rewind_frame_begin(app, &game_dt);
  TvmSlot args[1];
  args[0].f = (float)game_dt;
  bool ok = call_hook(p, &p->game, p->game.frame, "OnFrame", args, 1);
  rewind_frame_end(app);
  args[0].f = dt;
  if (p->tool.frame >= 0 && p->lub.vm) {
    // overlay はゲームの後に描き、ゲームの OnFrame が失敗した frame でも動く。
    // 失敗で開いたままの pass は閉じてから渡す。
    // どちらかが失敗した frame は失敗した frame (resource の sweep までの frame に数えない)
    if (pass_state_in_pass(&app->pass))
      pass_state_end(&app->pass);
    ok =
        call_hook(p, &p->tool, p->tool.frame, "overlay OnFrame", args, 1) && ok;
  }
  if (p->lub.vm)
    tvm_frame_end(p->lub.vm);
  return ok;
}

// 差分を当てた後の OnReload (ゲームの後に overlay)
static void call_reload(Player *p) {
  call_hook(p, &p->game, p->game.reload, "OnReload", NULL, 0);
  call_hook(p, &p->tool, p->tool.reload, "overlay OnReload", NULL, 0);
}

static void call_build_failed(Player *p, const char *errors, size_t len) {
  if (!p->lub.vm)
    return;
  const Hooks *hooks[2] = {&p->game, &p->tool};
  for (int i = 0; i < 2 && p->lub.vm; i++) {
    if (hooks[i]->build_failed < 0)
      continue;
    TvmSlot args[1];
    args[0].p = tvm_string_new(p->lub.vm, errors, len);
    call_hook(p, hooks[i], hooks[i]->build_failed,
              i ? "overlay OnBuildFailed" : "OnBuildFailed", args, 1);
  }
}

static void call_reload_notes(Player *p, const char *notes, size_t len) {
  const Hooks *hooks[2] = {&p->game, &p->tool};
  for (int i = 0; i < 2 && p->lub.vm; i++) {
    if (hooks[i]->reload_notes < 0)
      continue;
    TvmSlot args[1];
    args[0].p = tvm_string_new(p->lub.vm, notes, len);
    call_hook(p, hooks[i], hooks[i]->reload_notes,
              i ? "overlay OnReloadNotes" : "OnReloadNotes", args, 1);
  }
}

#ifdef VM_PROJECT
// ---- .csproj から動かすとき (tcs vm --watch の frame) ----

static int b64_value(char c) {
  if (c >= 'A' && c <= 'Z')
    return c - 'A';
  if (c >= 'a' && c <= 'z')
    return c - 'a' + 26;
  if (c >= '0' && c <= '9')
    return c - '0' + 52;
  if (c == '+')
    return 62;
  if (c == '/')
    return 63;
  return -1;
}

// base64 を戻す (改行と '=' で止まる)。
// 戻した byte 数を返す
static size_t b64_decode(const char *in, size_t len, uint8_t *out) {
  size_t n = 0;
  uint32_t acc = 0;
  int bits = 0;
  for (size_t i = 0; i < len; i++) {
    int v = b64_value(in[i]);
    if (v < 0)
      break;
    acc = (acc << 6) | (uint32_t)v;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out[n++] = (uint8_t)(acc >> bits);
    }
  }
  return n;
}

// notes は同じ差分の reload_notes の本文 (無ければ NULL)。
// 当てて OnReload を呼んだ後に OnReloadNotes に渡す
static void apply_vm_batch(Player *p, const char *text, size_t len,
                           const char *notes, size_t notes_len) {
  uint8_t *batch = (uint8_t *)SDL_malloc(len);
  if (!batch)
    return;
  size_t size = b64_decode(text, len, batch);
  if (!p->lub.vm) {
    SDL_free(batch);
    restart(p, "a batch arrived without a running VM");
    return;
  }
  TvmStatus st = tvm_apply_batch(p->lub.vm, batch, size);
  SDL_free(batch);
  if (st == TVM_RESTART) {
    SDL_Log("lub: reload needs a restart: %s", tvm_error(p->lub.vm));
    restart(p, "restart required");
    return;
  }
  if (st != TVM_OK) {
    SDL_Log("lub: reload failed: %s", tvm_error(p->lub.vm));
    return;
  }
  if (tvm_error(p->lub.vm)[0])
    SDL_Log("lub: reload: %s", tvm_error(p->lub.vm));
  if (!bind_entry(p)) {
    restart(p, "the reloaded module does not bind");
    return;
  }
  SDL_Log("lub: reloaded");
  call_reload(p);
  if (notes)
    call_reload_notes(p, notes, notes_len);
}

// tcs vm --watch が書いた frame を、届いた順に当てる
static void poll_project(Player *p) {
  uint64_t now = SDL_GetTicks();
  if (now >= p->next_scan) {
    vm_project_scan(&p->project);
    p->next_scan = now + 300;
  }
  TcsFrameKind kind;
  char *body = NULL;
  size_t len = 0;
  while (tcs_pipeline_next_frame(&p->tcs, &kind, &body, &len)) {
    if (kind == TCS_FRAME_RELOAD_NOTES) {
      // 続く vm_batch に付ける
      SDL_free(p->notes);
      p->notes = body;
      p->notes_len = len;
      body = NULL;
      continue;
    }
    if (kind == TCS_FRAME_VM_BATCH)
      apply_vm_batch(p, body, len, p->notes, p->notes_len);
    else if (kind == TCS_FRAME_RESTART_REQUIRED) {
      SDL_Log("lub: reload needs a restart:\n%s", body);
      restart(p, "restart required");
    } else if (kind == TCS_FRAME_BUILD_FAILED) {
      // 診断は tcs が stderr に書く
      SDL_Log("lub: reload failed: the build has errors, the running code is "
              "unchanged");
      call_build_failed(p, body, len);
    }
    SDL_free(body);
    body = NULL;
    SDL_free(p->notes);
    p->notes = NULL;
  }
}

// 入力を集めて tcs vm --watch を張り、最初の build (.tcsb が書き直される) を待つ。
// 通らない間は直して保存されるのを待つ。
static bool start_project(Player *p) {
  if (!vm_project_open(&p->project, p->csproj))
    return false;
  SDL_strlcpy(p->game.cls, p->project.entry, sizeof(p->game.cls));
  SDL_strlcpy(p->tool.cls, p->project.overlay, sizeof(p->tool.cls));
  SDL_strlcpy(p->module, p->project.out_tcsb, sizeof(p->module));
  vm_project_scan(&p->project);
  for (int i = 0; i < p->project.nextensions; i++)
    load_extension(p, p->project.extensions[i]);
  const char *args[256];
  int n = vm_project_args(&p->project, args, 256);
  int64_t before = file_mtime(p->module);
  if (!tcs_vm_pipeline_start(&p->tcs, args, n))
    return false;
  SDL_Log("tcs: compiling %s for the tcs VM ...", p->csproj);
  for (;;) {
    int64_t mtime = file_mtime(p->module);
    if (mtime >= 0 && mtime != before)
      break;
    if (!tcs_pipeline_alive(&p->tcs))
      return false;
    TcsFrameKind kind;
    char *body = NULL;
    size_t len = 0;
    while (tcs_pipeline_next_frame(&p->tcs, &kind, &body, &len))
      SDL_free(body); // 最初の build の前の診断は tcs が stderr にも書く
    if (SDL_GetTicks() >= p->next_scan) {
      vm_project_scan(&p->project);
      p->next_scan = SDL_GetTicks() + 300;
    }
    SDL_Delay(50);
  }
  SDL_Log("tcs: %s ready (watching)", p->module);
  return true;
}

#endif

#ifdef VM_PROJECT
static SDL_EnumerationResult SDLCALL found_entry(void *found, const char *dir,
                                                 const char *name) {
  (void)dir;
  (void)name;
  *(bool *)found = true;
  return SDL_ENUM_SUCCESS;
}

// export の <os> と -o <dir> (空でない dir は前の export の残りが混ざるので断る)
static bool begin_export(const char *os, const char *out) {
  if (!os || !out) {
    SDL_Log("usage: lub export <web|linux|windows|macos|ios> <entry> -o <dir>");
    return false;
  }
  bool found = false;
  if (SDL_GetPathInfo(out, NULL))
    SDL_EnumerateDirectory(out, found_entry, &found);
  if (found) {
    SDL_Log("export: %s is not empty", out);
    return false;
  }
  return export_begin(os, out);
}
#endif

// 動かす .tcsb と、その隣の .batch / .rev の path、entry class (NULL なら .tcsb の basename) を決める
static void set_module(Player *p, const char *module, const char *entry) {
  SDL_strlcpy(p->module, module, sizeof(p->module));
  size_t n = strlen(p->module);
  size_t stem = n > 5 && !strcmp(p->module + n - 5, ".tcsb") ? n - 5 : n;
  SDL_snprintf(p->batch, sizeof(p->batch), "%.*s.batch", (int)stem, p->module);
  SDL_snprintf(p->rev, sizeof(p->rev), "%.*s.rev", (int)stem, p->module);
  if (entry)
    SDL_strlcpy(p->game.cls, entry, sizeof(p->game.cls));
  else {
    const char *base = strrchr(p->module, '/');
    base = base ? base + 1 : p->module;
    size_t len = stem - (size_t)(base - p->module);
    SDL_snprintf(p->game.cls, sizeof(p->game.cls), "%.*s", (int)len, base);
  }
}

// ---- App.Use で開く app (LubAppHooks) ----
// 開いた app は自分の Player (VM、binding の状態、reload) を持つ。
// .tcsb は直接動かし、隣の .batch と .tcsb の書き直しで reload する。
// .csproj は tcs vm --watch を張り、最初の build が終わった frame から動かす (build を待って止まらない)。

#ifdef VM_PROJECT
// 開いた app の .csproj の入力を集め、tcs vm --watch を張る。
// ゲームのソースは --trace で変換し、csproj の overlay は呼ばない (開いた側が観察する)。
// module は build が .tcsb を書き直した frame に app_step が読む
static void open_project(Player *p, const char *csproj, const char *entry) {
  p->project_mode = true;
  SDL_strlcpy(p->csproj, csproj, sizeof(p->csproj));
  if (!vm_project_open(&p->project, p->csproj))
    return;
  p->project.trace = true;
  SDL_strlcpy(p->game.cls, entry ? entry : p->project.entry,
              sizeof(p->game.cls));
  SDL_strlcpy(p->module, p->project.out_tcsb, sizeof(p->module));
  vm_project_scan(&p->project);
  for (int i = 0; i < p->project.nextensions; i++)
    load_extension(p, p->project.extensions[i]);
  const char *args[256];
  int n = vm_project_args(&p->project, args, 256);
  // 前の build の .tcsb が残っていても、この build が書き直すまで読まない
  p->module_mtime = file_mtime(p->module);
  if (tcs_vm_pipeline_start(&p->tcs, args, n))
    SDL_Log("tcs: compiling %s for the tcs VM ...", p->csproj);
}
#endif

// module を読み、OnInit を呼ぶ。
// App.Config は backend と window の大きさを変えずに他の項目だけ効かせる
static void start_module(Player *p) {
  if (!load_module(p))
    return;
  App *app = lub_api_app(p->ctx);
  app->entry_restarting = true;
  call_init(p);
  app->entry_restarting = false;
}

static void *app_open(App *owner, App *child, const char *module,
                      const char *entry) {
  Player *from = (Player *)owner->debug_user;
  Player *p = (Player *)SDL_calloc(1, sizeof(Player));
  if (!p)
    return NULL;
  p->acked = -1;
  p->ctx = lub_api_ctx(child);
  if (!vm_lub_init(&p->lub, p->ctx)) {
    SDL_free(p);
    return NULL;
  }
  memcpy(p->extensions, from->extensions, sizeof(p->extensions));
  p->nextensions = from->nextensions;
  child->debug_hooks = &g_debug_hooks;
  child->debug_user = p;
  pick_set_sites(child, vm_pick_sites, p);
  clear_hooks(&p->game);
  clear_hooks(&p->tool);
  if (is_csproj(module)) {
#ifdef VM_PROJECT
    open_project(p, module, entry[0] ? entry : NULL);
#else
    SDL_Log("lub: this player cannot build %s (open the built .tcsb)", module);
#endif
    return p;
  }
  set_module(p, module, entry[0] ? entry : NULL);
  start_module(p);
  return p;
}

static bool app_step(void *player, float dt) {
  Player *p = (Player *)player;
  App *app = lub_api_app(p->ctx);
  // App.Restart は次の frame の始めに作り直す
  if (app->restart_requested) {
    app->restart_requested = false;
    if (p->lub.vm)
      restart(p, "App.Restart was called");
  }
#ifdef VM_PROJECT
  if (p->project_mode) {
    poll_project(p);
    // 最初の build と、読み込めなかった module の次の build は、.tcsb の書き直しで届く
    int64_t mtime = file_mtime(p->module);
    if (!p->lub.vm && mtime >= 0 && mtime != p->module_mtime)
      start_module(p);
  } else
#endif
    poll_reload(p);
  return p->lub.vm && on_frame(p, dt);
}

static void app_close(void *player) {
  Player *p = (Player *)player;
  call_quit(p);
  free_vm(p);
#ifdef VM_PROJECT
  if (p->project_mode) {
    tcs_pipeline_stop(&p->tcs);
    vm_project_close(&p->project);
    SDL_free(p->notes);
  }
#endif
  VmLub *s = &p->lub;
  for (uint32_t i = 0; i < s->nkeys; i++)
    free(s->keys[i]);
  free(s->keys);
  free(s->types);
  free(s->views);
  free(s->temps);
  SDL_free(p->trace_arena.p);
  SDL_free(p->trace_at);
  SDL_free(p->trace_strs);
  SDL_free(p->value_arena.p);
  SDL_free(p->value_at);
  SDL_free(p->values);
  SDL_free(p->nodes);
  SDL_free(p->lub_nodes);
  SDL_free(p->profile_arena.p);
  SDL_free(p->profile_at);
  SDL_free(p->profile);
  SDL_free(p->pauses);
  SDL_free(p->kept_arena.p);
  SDL_free(p->kept_at);
  SDL_free(p->kept);
  SDL_free(p->inst_arena.p);
  SDL_free(p->inst_at);
  SDL_free(p->inst);
  SDL_free(p);
}

static bool app_save(void *player, uint8_t **bytes, size_t *size, char *error,
                     size_t error_size) {
  Player *p = (Player *)player;
  if (!p->lub.vm) {
    SDL_snprintf(error, error_size, "the app has no module loaded");
    return false;
  }
  uint8_t *state;
  size_t n;
  if (tvm_state_save(p->lub.vm, &state, &n) != TVM_OK) {
    SDL_snprintf(error, error_size, "%s", tvm_error(p->lub.vm));
    return false;
  }
  uint8_t *copy = (uint8_t *)malloc(n ? n : 1);
  if (copy)
    memcpy(copy, state, n);
  tvm_state_free(state);
  if (!copy) {
    SDL_snprintf(error, error_size, "out of memory");
    return false;
  }
  *bytes = copy;
  *size = n;
  return true;
}

static bool app_load(void *player, const uint8_t *bytes, size_t size,
                     char *error, size_t error_size) {
  Player *p = (Player *)player;
  if (!p->lub.vm) {
    SDL_snprintf(error, error_size, "the app has no module loaded");
    return false;
  }
  if (tvm_state_load(p->lub.vm, bytes, size) != TVM_OK) {
    SDL_snprintf(error, error_size, "%s", tvm_error(p->lub.vm));
    return false;
  }
  return true;
}

static bool app_event(void *player, const LubEventData *e) {
  Player *p = (Player *)player;
  if (!p->lub.vm || p->game.event < 0)
    return true;
  TvmSlot args[1];
  args[0].p = vm_lub_event(&p->lub, e);
  return call_hook(p, &p->game, p->game.event, "OnEvent", args, 1);
}

static const LubAppHooks g_app_hooks = {app_open,  app_step, app_close,
                                        app_event, app_save, app_load};

static bool parse_args(Player *p, int argc, char **argv, LubHostOpts *opts) {
  const char *module = NULL, *entry = NULL;
  int first = 1;
#ifdef VM_PROJECT
  const char *export_os_name = NULL, *export_out = NULL;
  if (argc > 1 && !strcmp(argv[1], "export")) {
    p->exporting = true;
    first = 2;
  }
#endif
  for (int i = first; i < argc; i++) {
#ifdef VM_PROJECT
    if (p->exporting && !strcmp(argv[i], "-o") && i + 1 < argc) {
      export_out = argv[++i];
      continue;
    }
    if (p->exporting && !export_os_name) {
      export_os_name = argv[i];
      continue;
    }
#endif
    if (!strcmp(argv[i], "--digest"))
      opts->digest = true;
    else if (!strcmp(argv[i], "--readback-fixed-latency"))
      opts->readback_fixed_latency = true;
    else if (!strcmp(argv[i], "--fixed-dt") && i + 1 < argc)
      opts->fixed_dt = strtof(argv[++i], NULL);
    else if (!strcmp(argv[i], "--capture-frame") && i + 1 < argc)
      opts->capture_frame = atoi(argv[++i]);
    else if (!strcmp(argv[i], "--capture") && i + 1 < argc) {
      const char *path = argv[++i];
      opts->capture_path = (LubStr){path, (int32_t)strlen(path)};
      if (!opts->capture_frame)
        opts->capture_frame = 30;
    } else if (!strcmp(argv[i], "--entry") && i + 1 < argc)
      entry = argv[++i];
    else if (!strcmp(argv[i], "--overlay") && i + 1 < argc)
      SDL_strlcpy(p->tool.cls, argv[++i], sizeof(p->tool.cls));
    else if (!strcmp(argv[i], "--extension") && i + 1 < argc)
      load_extension(p, argv[++i]);
    else if (!module)
      module = argv[i];
    else {
      SDL_Log("lub: unknown argument %s", argv[i]);
      return false;
    }
  }
#ifdef VM_PROJECT
  if (p->exporting && (!module || !begin_export(export_os_name, export_out)))
    return false;
#endif
  if (!module) {
    SDL_Log("usage: lub <Entry>.tcsb|<Entry>.csproj [--entry CLASS] "
            "[--fixed-dt S] "
            "[--capture PNG] [--capture-frame N] [--digest]");
    return false;
  }
  if (is_csproj(module)) {
    p->project_mode = true;
    SDL_strlcpy(p->csproj, module, sizeof(p->csproj));
    return true;
  }
  set_module(p, module, entry);
  return true;
}

#ifdef __EMSCRIPTEN__
// player を外す前に page が呼ぶ (src/tcs_host.c の lub_tcs_shutdown と同じ)
EMSCRIPTEN_KEEPALIVE void lub_vm_shutdown(void) {
  Player *p = &g_player;
  if (!p->ctx || p->shutting_down)
    return;
  p->shutting_down = true;
  emscripten_cancel_main_loop();
  call_quit(p);
  free_vm(p);
  lub_host_destroy(p->ctx);
  p->ctx = NULL;
  p->lub.ctx = NULL;
}

// page が変換した C# の compile error を、ゲームと overlay の OnBuildFailed に渡す
EMSCRIPTEN_KEEPALIVE void lub_vm_build_failed(const char *errors) {
  Player *p = &g_player;
  if (p->ctx && errors)
    call_build_failed(p, errors, strlen(errors));
}

EMSCRIPTEN_KEEPALIVE void lub_vm_volume(float volume) {
  Player *p = &g_player;
  if (p->ctx)
    lub_audio_master_volume(p->ctx, volume);
}
#endif

SDL_AppResult lub_vm_app_init(int argc, char **argv,
                              const LubVmConfig *config) {
  Player *p = &g_player;
  SDL_SetHint(SDL_HINT_ASSERT, "abort");
  signal(SIGSEGV, on_crash);
  signal(SIGFPE, on_crash);
  signal(SIGILL, on_crash);
  // 静的に link した拡張は、読み込む拡張より先に登録する
  for (int i = 0;
       config && i < config->nextensions && p->nextensions < MAX_EXTENSIONS;
       i++)
    p->extensions[p->nextensions++] = config->extensions[i];
  LubHostOpts opts = {0};
  if (!parse_args(p, argc, argv, &opts))
    return SDL_APP_FAILURE;
  p->ctx = lub_host_create(&opts);
  if (!p->ctx)
    return SDL_APP_FAILURE;
  if (!vm_lub_init(&p->lub, p->ctx))
    return SDL_APP_FAILURE;
  lub_api_app(p->ctx)->debug_hooks = &g_debug_hooks;
  lub_api_app(p->ctx)->debug_user = p;
  lub_api_app(p->ctx)->app_hooks = &g_app_hooks;
  pick_set_sites(lub_api_app(p->ctx), vm_pick_sites, p);
  clear_hooks(&p->game);
  clear_hooks(&p->tool);
#ifdef VM_PROJECT
  if (p->project_mode && !start_project(p))
    return SDL_APP_FAILURE;
#else
  if (p->project_mode) {
    SDL_Log("lub: this player cannot compile a .csproj");
    return SDL_APP_FAILURE;
  }
#endif
#ifdef VM_PROJECT
  // export は最初の build だけを使う
  if (p->exporting && p->project_mode)
    tcs_pipeline_stop(&p->tcs);
#endif
  uint64_t t0 = SDL_GetTicksNS();
  if (!load_module(p))
    return SDL_APP_FAILURE;
#ifdef VM_PROJECT
  if (p->exporting) {
    char name[320];
    SDL_snprintf(name, sizeof(name), "%s.tcsb", p->game.cls);
    export_copy(p->module, name);
  }
#endif
  call_init(p);
#ifdef VM_PROJECT
  if (p->exporting && lub_host_exit_code(p->ctx) != 0)
    return SDL_APP_FAILURE;
#endif
  ack(p, read_revision(p), true, NULL, (double)(SDL_GetTicksNS() - t0) / 1e6);
  if (lub_host_quit_requested(p->ctx))
    return lub_host_exit_code(p->ctx) == 0 ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
  return lub_host_start(p->ctx) == LUB_OK ? SDL_APP_CONTINUE : SDL_APP_FAILURE;
}

SDL_AppResult lub_vm_app_event(SDL_Event *event) {
  Player *p = &g_player;
  LubEventData translated;
  if (!lub_host_translate_event(lub_api_app(p->ctx), event, &translated))
    return SDL_APP_CONTINUE;
  if (translated.kind == LUB_EVENT_KIND_QUIT)
    return SDL_APP_SUCCESS;
  on_event(p, &translated);
  return SDL_APP_CONTINUE;
}

SDL_AppResult lub_vm_app_iterate(void) {
  Player *p = &g_player;
  if (lub_host_quit_requested(p->ctx))
    return lub_host_exit_code(p->ctx) == 0 ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
#ifdef VM_PROJECT
  if (p->exporting) {
    // OnInit の後、最初の frame の代わりに OnExport を 1 回呼んで終わる
    float dt = 0.0f;
    bool ok = lub_host_frame_begin(p->ctx, &dt);
    if (ok) {
      ok = call_hook(p, &p->game, p->game.export_, "OnExport", NULL, 0);
      lub_host_frame_end(p->ctx, ok);
    }
    ok = ok && !export_failed() && lub_host_exit_code(p->ctx) == 0;
    if (ok && !strcmp(export_os(), "web")) {
      char start[320];
      SDL_snprintf(start, sizeof(start), "%s.tcsb", p->game.cls);
      ok = export_write_web_page(export_out_dir(), p->game.cls, start);
    }
    if (ok)
      SDL_Log("export: wrote %s", export_out_dir());
    else
      SDL_Log("export: failed");
    return ok ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
  }
#endif
#ifdef VM_PROJECT
  if (p->project_mode)
    poll_project(p);
  else
#endif
    poll_reload(p);
  float dt;
  if (lub_host_frame_begin(p->ctx, &dt)) {
    bool ok = on_frame(p, dt);
    lub_host_frame_end(p->ctx, ok);
  }
  // App.Restart は frame の終わりに作り直す (VM の frame の中では作り直せない)
  App *app = lub_api_app(p->ctx);
  if (app->restart_requested) {
    app->restart_requested = false;
    if (p->lub.vm)
      restart(p, "App.Restart was called");
  }
  if (lub_host_quit_requested(p->ctx))
    return lub_host_exit_code(p->ctx) == 0 ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
  return SDL_APP_CONTINUE;
}

void lub_vm_app_quit(SDL_AppResult result) {
#ifdef __EMSCRIPTEN__
  lub_vm_shutdown();
#else
  Player *p = &g_player;
  if (!p->ctx)
    return;
  call_quit(p);
  free_vm(p);
#ifdef VM_PROJECT
  tcs_pipeline_stop(&p->tcs);
  vm_project_close(&p->project);
#endif
  int32_t code = lub_host_exit_code(p->ctx);
  if (result == SDL_APP_FAILURE && !lub_host_quit_requested(p->ctx)) {
    // 起動の失敗など、Quit を経ない失敗も終了状態の file に失敗として残す
    code = 1;
    lub_app_quit(p->ctx, code);
  }
  lub_host_destroy(p->ctx);
  p->ctx = NULL;
  // SDL の main は AppIterate が返した成功 / 失敗で終わる。
  // OnQuit の中の Quit と 1 以外の code はそれに載らないので、ここで返す
  if (code != 0 || result == SDL_APP_FAILURE) {
    SDL_Quit();
    exit(code);
  }
#endif
}
