// tcs VM の player の library (include/lub/lub_vm.h): tcs vm が作った bytecode (.tcsb) を tcs VM (third_party/tcs/tcsvm) で動かす。
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

static LubContext *g_ctx;
static char g_module[1024];
static char g_batch[1024];
static char g_rev[1024];
static int32_t g_acked = -1; // ACK を返した revision
static int64_t g_module_mtime;
// entry class と overlay の callback の関数の番号 (無ければ -1)
typedef struct Hooks {
  char cls[256];
  int32_t init, frame, event, quit, reload, build_failed, reload_notes, error,
      export_;
} Hooks;
static Hooks g_game, g_tool;
// native の拡張 (include/lub/lub_vm_extension.h)。VM を作るたびに呼ぶ
#define MAX_EXTENSIONS 32
static LubVmExtensionFn g_extensions[MAX_EXTENSIONS];
static int g_nextensions;
static void call_reload(void);
// .csproj から動かすときの入力と tcs vm --watch
static bool g_project_mode;
static char g_csproj[1024];
#ifdef VM_PROJECT
static bool g_export; // lub export
static VmProject g_project;
static TcsPipeline g_tcs;
// 次の vm_batch に付ける reload_notes の本文
static char *g_notes;
static size_t g_notes_len;
static uint64_t g_next_scan;
#endif
#ifdef __EMSCRIPTEN__
static bool g_shutting_down;
#endif

static int64_t file_mtime(const char *path) {
  SDL_PathInfo info;
  return SDL_GetPathInfo(path, &info) ? (int64_t)info.modify_time : -1;
}

// playground が置いた revision (無ければ -1)
static int32_t read_revision(void) {
  size_t size = 0;
  char *text = SDL_GetPathInfo(g_rev, NULL) ? SDL_LoadFile(g_rev, &size) : NULL;
  if (!text)
    return -1;
  int32_t rev = (int32_t)strtol(text, NULL, 10);
  SDL_free(text);
  return rev;
}

static void ack(int32_t rev, bool ok, const char *error, double ms) {
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
    g_acked = rev;
}

static void log_fault(const char *what) {
  SDL_Log("lub: error in %s: %s", what, tvm_error(vm_lub_vm));
}

static int32_t find_hook(const char *cls, const char *method) {
  char name[320];
  SDL_snprintf(name, sizeof(name), "%s.%s", cls, method);
  return tvm_find(vm_lub_vm, name);
}

static void bind_hooks(Hooks *h) {
  h->init = find_hook(h->cls, "OnInit");
  h->frame = find_hook(h->cls, "OnFrame");
  h->event = find_hook(h->cls, "OnEvent");
  h->quit = find_hook(h->cls, "OnQuit");
  h->reload = find_hook(h->cls, "OnReload");
  h->build_failed = find_hook(h->cls, "OnBuildFailed");
  h->reload_notes = find_hook(h->cls, "OnReloadNotes");
  h->error = find_hook(h->cls, "OnError");
  h->export_ = find_hook(h->cls, "OnExport");
}

static void clear_hooks(Hooks *h) {
  h->init = h->frame = h->event = h->quit = h->reload = h->build_failed =
      h->reload_notes = h->error = h->export_ = -1;
}

// 関数の番号は reload で変わるので、読み込みと reload の後に引き直す。
// 型の offset も同じ時に確かめる。
static bool bind_entry(void) {
  char error[512];
  if (!vm_lub_verify(vm_lub_vm, error, sizeof(error))) {
    SDL_Log("lub: the module does not match the host: %s", error);
    return false;
  }
  bind_hooks(&g_game);
  if (g_tool.cls[0])
    bind_hooks(&g_tool);
  if (g_game.frame < 0) {
    SDL_Log("lub: %s has no static OnFrame(float)", g_game.cls);
    return false;
  }
  return true;
}

// 今 VM で走らせている callback (落ちたときに出す)。VM の外では NULL
static const char *g_calling;

static void crash_site(void *user, const char *file, uint32_t line) {
  (void)user;
  fprintf(stderr, "  at %s:%u\n", file, (unsigned)line);
}

// 落ちたとき (SIGSEGV など) に、走らせていた callback と C# の呼び出しの段を出してから、既定の処置で落ちる。
// signal の中では本来呼べない関数も使うが、どのみち落ちる所なので、出せるだけ出す
static void on_crash(int sig) {
  fprintf(stderr, "lub: crashed (signal %d) %s%s\n", sig,
          g_calling ? "in " : "outside the VM", g_calling ? g_calling : "");
  if (vm_lub_vm && g_calling)
    tvm_stack(vm_lub_vm, crash_site, NULL);
  fflush(stderr);
  signal(sig, SIG_DFL);
  raise(sig);
}

static bool call_fn(int32_t fn, const char *what, const TvmSlot *args,
                    size_t nargs) {
  if (!vm_lub_vm || fn < 0)
    return true;
  const char *outer = g_calling;
  g_calling = what;
  TvmStatus st = tvm_call(vm_lub_vm, fn, args, nargs, NULL, 0);
  g_calling = outer;
  if (st != TVM_OK) {
    log_fault(what);
    return false;
  }
  return true;
}

// 失敗した callback の処置を、その class の OnError に聞く (無ければ Log)
static void call_on_error(const Hooks *h) {
  if (!vm_lub_vm || h->error < 0)
    return;
  // tvm_error は次の呼び出しで書き換わるので、先に VM の string にする
  const char *message = tvm_error(vm_lub_vm);
  TvmSlot args[1], rets[1];
  args[0].p = tvm_string_new(vm_lub_vm, message, strlen(message));
  rets[0].i = 0;
  int action = LUB_ERROR_ACTION_ABORT;
  if (tvm_call(vm_lub_vm, h->error, args, 1, rets, 1) != TVM_OK)
    log_fault("OnError");
  else if (rets[0].i == LUB_ERROR_ACTION_LOG)
    action = LUB_ERROR_ACTION_LOG;
  if (action == LUB_ERROR_ACTION_ABORT)
    lub_app_quit(g_ctx, 1);
}

// overlay の callback の間だけ App.in_overlay を立てる (Input.Grab と、入力を隠す判定が見る)
static bool call_hook(const Hooks *h, int32_t fn, const char *what,
                      const TvmSlot *args, size_t nargs) {
  App *app = lub_api_app(g_ctx);
  bool overlay = h == &g_tool;
  if (overlay)
    app->in_overlay = true;
  bool ok = call_fn(fn, what, args, nargs);
  if (!ok)
    call_on_error(h);
  if (overlay)
    app->in_overlay = false;
  return ok;
}

static void profile_stop(void);

static void free_vm(void) {
  // Rewind の控えは VM の中にあるので、VM より先に捨てる
  if (g_ctx)
    rewind_drop(lub_api_app(g_ctx));
  // 行ごとの負荷の時間を知らせる thread は VM を触るので、VM より先に止める
  profile_stop();
  if (vm_lub_vm)
    tvm_free(vm_lub_vm);
  vm_lub_vm = NULL;
  clear_hooks(&g_game);
  clear_hooks(&g_tool);
  App *app = lub_api_app(g_ctx);
  if (app) {
    app->overlay_grab_keyboard = false;
    app->overlay_grab_mouse = false;
  }
}

// 起動と作り直しの OnInit (ゲームの後に overlay)
static void call_init(void) {
  call_hook(&g_game, g_game.init, "OnInit", NULL, 0);
  call_hook(&g_tool, g_tool.init, "overlay OnInit", NULL, 0);
}

// OnQuit (overlay はゲームを包むので先)
static void call_quit(void) {
  call_hook(&g_tool, g_tool.quit, "overlay OnQuit", NULL, 0);
  call_hook(&g_game, g_game.quit, "OnQuit", NULL, 0);
}

// native の拡張を読む (失敗は log に出して飛ばす。その関数を使う module は読み込みで止まる)
static void load_extension(const char *path) {
  if (g_nextensions >= MAX_EXTENSIONS)
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
  g_extensions[g_nextensions++] = fn;
}

// .tcsb から VM を作る。
// 失敗したら VM は無いまま (次の書き直しを待つ)。
static bool load_module(void) {
  g_module_mtime = file_mtime(g_module);
  size_t size = 0;
  void *bytes = SDL_LoadFile(g_module, &size);
  if (!bytes) {
    SDL_Log("lub: cannot read %s: %s", g_module, SDL_GetError());
    return false;
  }
  vm_lub_vm = tvm_new(NULL);
  bool ok = vm_lub_vm && vm_lub_register(vm_lub_vm);
  static const LubVmExtensionApi api = {LUB_VM_EXTENSION_VERSION, tvm_host,
                                        tvm_host_fault, tvm_string_new,
                                        tvm_string_data};
  for (int i = 0; ok && i < g_nextensions; i++)
    if (!g_extensions[i](&api, vm_lub_vm)) {
      SDL_Log("lub: a native extension failed to register its functions");
      ok = false;
    }
  if (ok && tvm_load(vm_lub_vm, bytes, size) != TVM_OK) {
    SDL_Log("lub: cannot load %s: %s", g_module, tvm_error(vm_lub_vm));
    ok = false;
  }
  SDL_free(bytes);
  if (ok)
    ok = bind_entry();
  if (!ok)
    free_vm();
  return ok;
}

// OnQuit (古い VM) の後に新しい VM を OnInit から始める。
// 物理の world はゲームの状態なので捨て、key で宣言する資源は残す。
// 新しい VM を作れたら、OnInit の前に log に 1 行出す (`lub: restarted`)。
static void restart(const char *why) {
  SDL_Log("lub: %s, starting the VM again from %s", why, g_module);
  call_quit();
  free_vm();
  App *app = lub_api_app(g_ctx);
  phys2d_state_shutdown(&app->phys);
  phys2d_state_init(&app->phys);
  phys3d_state_shutdown(&app->phys3);
  phys3d_state_init(&app->phys3);
  uint64_t t0 = SDL_GetTicksNS();
  if (!load_module()) {
    ack(read_revision(), false, "the module failed to load", 0);
    return;
  }
  SDL_Log("lub: restarted");
  app->entry_restarting = true;
  call_init();
  app->entry_restarting = false;
  ack(read_revision(), true, NULL, (double)(SDL_GetTicksNS() - t0) / 1e6);
}

// frame の境界で hot reload を見る (VM の frame が無いときだけ当てられる)
static void poll_reload(void) {
  size_t size = 0;
  void *batch =
      SDL_GetPathInfo(g_batch, NULL) ? SDL_LoadFile(g_batch, &size) : NULL;
  if (batch) {
    SDL_RemovePath(g_batch);
    int32_t rev = read_revision();
    if (rev >= 0 && rev <= g_acked) { // 当てた build の再送
      SDL_free(batch);
      ack(rev, true, NULL, 0);
      return;
    }
    uint64_t t0 = SDL_GetTicksNS();
    if (!vm_lub_vm) {
      SDL_free(batch);
      restart("a batch arrived without a running VM");
      return;
    }
    TvmStatus st = tvm_apply_batch(vm_lub_vm, batch, size);
    SDL_free(batch);
    if (st == TVM_RESTART) {
      SDL_Log("lub: reload needs a restart: %s", tvm_error(vm_lub_vm));
      restart("restart required");
      return;
    }
    if (st != TVM_OK) {
      SDL_Log("lub: reload failed: %s", tvm_error(vm_lub_vm));
      ack(rev, false, tvm_error(vm_lub_vm), 0);
      return;
    }
    if (tvm_error(vm_lub_vm)[0])
      SDL_Log("lub: reload: %s", tvm_error(vm_lub_vm));
    if (!bind_entry()) {
      restart("the reloaded module does not bind");
      return;
    }
    call_reload();
    ack(rev, true, NULL, (double)(SDL_GetTicksNS() - t0) / 1e6);
    return;
  }
  int64_t mtime = file_mtime(g_module);
  if (mtime >= 0 && mtime != g_module_mtime)
    restart(vm_lub_vm ? "the module was rewritten" : "the module was rebuilt");
}

// ---- 検査の口 (LubDebugHooks): --trace の記録、値の木、Rewind の状態の控え ----

// 結果の文字列 (次の同じ呼び出しまで有効)
typedef struct StrArena {
  char *p;
  size_t len, cap;
} StrArena;

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

static StrArena g_trace_arena;
static size_t *g_trace_at;
static LubStr *g_trace_strs;
static int32_t g_trace_n, g_trace_cap, g_trace_at_cap;

static void trace_line(void *user, const char *file, uint32_t line) {
  (void)user;
  char key[1100];
  int n = SDL_snprintf(key, sizeof key, "%s:%u", file, (unsigned)line);
  g_trace_at = (size_t *)grow(g_trace_at, &g_trace_at_cap, g_trace_n + 1,
                              sizeof(size_t));
  if (g_trace_n < g_trace_at_cap)
    g_trace_at[g_trace_n++] = arena_add(&g_trace_arena, key, (size_t)n);
}

static const char *cstr(LubStr s, char *buf, size_t cap);

// key に覚えた個体 (tvm_inspect_pin) と同じ class の別の個体だけが通った行を除く (key が空なら全部)
static void hook_take_trace(App *app, LubStr key, const LubStr **out,
                            int32_t *count) {
  (void)app;
  arena_reset(&g_trace_arena);
  g_trace_n = 0;
  if (!vm_lub_vm)
    return;
  char k[64];
  tvm_trace_take_lines_for(vm_lub_vm, cstr(key, k, sizeof k), trace_line, NULL);
  g_trace_strs =
      (LubStr *)grow(g_trace_strs, &g_trace_cap, g_trace_n, sizeof(LubStr));
  for (int32_t i = 0; i < g_trace_n && i < g_trace_cap; i++)
    g_trace_strs[i] = arena_str(&g_trace_arena, g_trace_at[i]);
  *out = g_trace_strs;
  *count = g_trace_n < g_trace_cap ? g_trace_n : g_trace_cap;
}

static StrArena g_value_arena;
static size_t *g_value_at; // site と text の組
static LubTraceValue *g_values;
static int32_t g_value_n, g_value_cap, g_value_at_cap;

static void trace_value(void *user, const char *file, uint32_t line,
                        uint32_t col, const char *label, const char *text) {
  (void)user;
  char site[1400];
  int n = SDL_snprintf(site, sizeof site, "%s:%u:%u:%s", file, (unsigned)line,
                       (unsigned)col, label);
  g_value_at = (size_t *)grow(g_value_at, &g_value_at_cap, 2 * g_value_n + 2,
                              sizeof(size_t));
  if (2 * g_value_n + 2 > g_value_at_cap)
    return;
  g_value_at[2 * g_value_n] = arena_add(&g_value_arena, site, (size_t)n);
  g_value_at[2 * g_value_n + 1] = arena_add(&g_value_arena, text, strlen(text));
  g_value_n++;
}

static void hook_take_values(App *app, const LubTraceValue **out,
                             int32_t *count) {
  (void)app;
  arena_reset(&g_value_arena);
  g_value_n = 0;
  if (!vm_lub_vm)
    return;
  tvm_trace_take_values(vm_lub_vm, trace_value, NULL);
  g_values = (LubTraceValue *)grow(g_values, &g_value_cap, g_value_n,
                                   sizeof(LubTraceValue));
  int32_t n = g_value_n < g_value_cap ? g_value_n : g_value_cap;
  for (int32_t i = 0; i < n; i++) {
    g_values[i].site = arena_str(&g_value_arena, g_value_at[2 * i]);
    g_values[i].text = arena_str(&g_value_arena, g_value_at[2 * i + 1]);
  }
  *out = g_values;
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

static TvmInspectNode *g_nodes;
static LubInspectNode *g_lub_nodes;
static int32_t g_node_cap, g_lub_node_cap;

static void hook_inspect(App *app, LubStr path, int32_t offset, int32_t count,
                         const LubInspectNode **out, int32_t *n) {
  (void)app;
  if (!vm_lub_vm)
    return;
  if (count > 4096)
    count = 4096;
  g_nodes = (TvmInspectNode *)grow(g_nodes, &g_node_cap, count,
                                   sizeof(TvmInspectNode));
  g_lub_nodes = (LubInspectNode *)grow(g_lub_nodes, &g_lub_node_cap, count,
                                       sizeof(LubInspectNode));
  if (g_node_cap < count || g_lub_node_cap < count)
    return;
  char p[1024];
  int32_t got = tvm_inspect(vm_lub_vm, g_game.cls, cstr(path, p, sizeof p),
                            offset, count, g_nodes);
  for (int32_t i = 0; i < got; i++) {
    const TvmInspectNode *t = &g_nodes[i];
    LubInspectNode *l = &g_lub_nodes[i];
    l->path = (LubStr){t->path, (int32_t)strlen(t->path)};
    l->name = (LubStr){t->name, (int32_t)strlen(t->name)};
    l->text = (LubStr){t->text, (int32_t)strlen(t->text)};
    l->type =
        t->type ? (LubStr){t->type, (int32_t)strlen(t->type)} : (LubStr){"", 0};
    l->id = (LubStr){t->id, (int32_t)strlen(t->id)};
    l->count = t->count;
    l->cycle = t->cycle;
  }
  *out = g_lub_nodes;
  *n = got;
}

static void hook_inspect_pin(App *app, LubStr key, LubStr path) {
  (void)app;
  char k[64], p[1024];
  if (vm_lub_vm)
    tvm_inspect_pin(vm_lub_vm, cstr(key, k, sizeof k), g_game.cls,
                    cstr(path, p, sizeof p));
}

static bool hook_inspect_pinned(App *app, LubStr key, LubStr id) {
  (void)app;
  char k[64], i[64];
  return vm_lub_vm && tvm_inspect_pinned(vm_lub_vm, cstr(key, k, sizeof k),
                                         cstr(id, i, sizeof i));
}

static void *hook_snapshot_take(App *app) {
  (void)app;
  return vm_lub_vm ? tvm_snapshot_take(vm_lub_vm, g_game.cls) : NULL;
}

static void hook_snapshot_restore(App *app, void *s) {
  (void)app;
  if (vm_lub_vm)
    tvm_snapshot_restore(vm_lub_vm, (const TvmSnapshot *)s);
}

static void hook_snapshot_free(App *app, void *s) {
  (void)app;
  if (vm_lub_vm)
    tvm_snapshot_free(vm_lub_vm, (TvmSnapshot *)s);
}

static bool hook_snapshot_number(App *app, void *s, LubStr path, float *out) {
  (void)app;
  char p[1024];
  return vm_lub_vm &&
         tvm_snapshot_number(vm_lub_vm, (const TvmSnapshot *)s, g_game.cls,
                             cstr(path, p, sizeof p), out);
}

static uint32_t hook_layout_version(App *app) {
  (void)app;
  return vm_lub_vm ? tvm_layout_version(vm_lub_vm) : 0;
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
  (void)user;
  SiteBuf b = {out, cap, 0};
  if (cap)
    out[0] = '\0';
  if (vm_lub_vm)
    tvm_stack_self(vm_lub_vm, pick_site, &b);
}

// ---- 行ごとの負荷 (tvm_profile_*) ----
// VM は文ごとに時計を読まない (時計は VM の文 1 つより重く、時計の時間が行の時間に化ける)。
// 別の thread が PROFILE_TICK_NS ごとに経った時間を知らせ、VM はそのとき実行していた行に足す。
// 時計 (SDL の ns) は GC が止めた長さを測るのにだけ渡す。

#define PROFILE_TICK_NS 250000

static uint64_t profile_clock(void) { return SDL_GetTicksNS(); }

static SDL_Thread *g_profile_thread;
static SDL_AtomicInt g_profile_run;

static int SDLCALL profile_ticker(void *data) {
  TvmVm *vm = (TvmVm *)data;
  uint64_t last = SDL_GetTicksNS();
  while (SDL_GetAtomicInt(&g_profile_run)) {
    SDL_DelayNS(PROFILE_TICK_NS);
    uint64_t now = SDL_GetTicksNS();
    tvm_profile_tick(vm, now - last);
    last = now;
  }
  return 0;
}

static void profile_stop(void) {
  if (!g_profile_thread)
    return;
  SDL_SetAtomicInt(&g_profile_run, 0);
  SDL_WaitThread(g_profile_thread, NULL);
  g_profile_thread = NULL;
}

static void hook_profile_lines(App *app, bool on) {
  (void)app;
  profile_stop();
  if (!vm_lub_vm)
    return;
  tvm_profile_enable(vm_lub_vm, on ? profile_clock : NULL);
  if (!on)
    return;
  // thread を作れない環境 (thread の無い web) では時間を数えず、確保と GC の止まりだけを数える
  SDL_SetAtomicInt(&g_profile_run, 1);
  g_profile_thread = SDL_CreateThread(profile_ticker, "lub_profile", vm_lub_vm);
}

static StrArena g_profile_arena;
static size_t *g_profile_at;
static LubProfileLine *g_profile;
static int32_t g_profile_n, g_profile_cap, g_profile_at_cap;

static void profile_line(void *user, const char *file, uint32_t line,
                         uint64_t ns, uint64_t bytes, uint32_t allocs) {
  (void)user;
  char site[1100];
  int n = SDL_snprintf(site, sizeof site, "%s:%u", file, (unsigned)line);
  g_profile_at = (size_t *)grow(g_profile_at, &g_profile_at_cap,
                                g_profile_n + 1, sizeof(size_t));
  g_profile = (LubProfileLine *)grow(g_profile, &g_profile_cap, g_profile_n + 1,
                                     sizeof(LubProfileLine));
  if (g_profile_n >= g_profile_at_cap || g_profile_n >= g_profile_cap)
    return;
  g_profile_at[g_profile_n] = arena_add(&g_profile_arena, site, (size_t)n);
  LubProfileLine *p = &g_profile[g_profile_n++];
  p->ms = (float)((double)ns / 1e6);
  p->bytes = bytes > INT32_MAX ? INT32_MAX : (int32_t)bytes;
  p->allocs = allocs > INT32_MAX ? INT32_MAX : (int32_t)allocs;
}

static void hook_take_profile(App *app, const LubProfileLine **out,
                              int32_t *count) {
  (void)app;
  arena_reset(&g_profile_arena);
  g_profile_n = 0;
  if (!vm_lub_vm)
    return;
  tvm_profile_take_lines(vm_lub_vm, profile_line, NULL);
  // 文字列は arena が全部を持った後に引く (arena は伸びると場所が変わる)
  for (int32_t i = 0; i < g_profile_n; i++)
    g_profile[i].site = arena_str(&g_profile_arena, g_profile_at[i]);
  *out = g_profile;
  *count = g_profile_n;
}

static LubGcPause *g_pauses;
static int32_t g_pause_n, g_pause_cap;

static void gc_pause(void *user, uint64_t ns, size_t live) {
  (void)user;
  g_pauses = (LubGcPause *)grow(g_pauses, &g_pause_cap, g_pause_n + 1,
                                sizeof(LubGcPause));
  if (g_pause_n >= g_pause_cap)
    return;
  g_pauses[g_pause_n].ms = (float)((double)ns / 1e6);
  g_pauses[g_pause_n].live_kb = (int32_t)(live / 1024);
  g_pause_n++;
}

static void hook_take_gc_pauses(App *app, const LubGcPause **out,
                                int32_t *count) {
  (void)app;
  g_pause_n = 0;
  if (!vm_lub_vm)
    return;
  tvm_profile_take_gc(vm_lub_vm, gc_pause, NULL);
  *out = g_pauses;
  *count = g_pause_n;
}

// 持ち越しの 1 件の文字列 (site と holder) は arena の中の位置で持ち、全部を足した後に引く
static StrArena g_kept_arena;
static size_t *g_kept_at;
static LubKeptObject *g_kept;
static int32_t g_kept_n, g_kept_cap, g_kept_at_cap;

static void kept_object(void *user, const char *file, uint32_t line,
                        const char *holder, uint32_t count, uint64_t bytes) {
  (void)user;
  char site[1100];
  int n = SDL_snprintf(site, sizeof site, "%s:%u", file, (unsigned)line);
  g_kept_at = (size_t *)grow(g_kept_at, &g_kept_at_cap, 2 * (g_kept_n + 1),
                             sizeof(size_t));
  g_kept = (LubKeptObject *)grow(g_kept, &g_kept_cap, g_kept_n + 1,
                                 sizeof(LubKeptObject));
  if (2 * (g_kept_n + 1) > g_kept_at_cap || g_kept_n >= g_kept_cap)
    return;
  g_kept_at[2 * g_kept_n] = arena_add(&g_kept_arena, site, (size_t)n);
  g_kept_at[2 * g_kept_n + 1] =
      arena_add(&g_kept_arena, holder, SDL_strlen(holder));
  LubKeptObject *k = &g_kept[g_kept_n++];
  k->count = count > INT32_MAX ? INT32_MAX : (int32_t)count;
  k->bytes = bytes > INT32_MAX ? INT32_MAX : (int32_t)bytes;
}

static void hook_take_kept(App *app, const LubKeptObject **out,
                           int32_t *count) {
  (void)app;
  arena_reset(&g_kept_arena);
  g_kept_n = 0;
  if (!vm_lub_vm)
    return;
  tvm_profile_take_kept(vm_lub_vm, g_game.cls, kept_object, NULL);
  for (int32_t i = 0; i < g_kept_n; i++) {
    g_kept[i].site = arena_str(&g_kept_arena, g_kept_at[2 * i]);
    g_kept[i].holder = arena_str(&g_kept_arena, g_kept_at[2 * i + 1]);
  }
  *out = g_kept;
  *count = g_kept_n;
}

static bool hook_profile_site(App *app, char *out, int32_t cap) {
  (void)app;
  const char *file;
  uint32_t line;
  if (!vm_lub_vm || !tvm_profile_site(vm_lub_vm, &file, &line))
    return false;
  SDL_snprintf(out, (size_t)cap, "%s:%u", file, (unsigned)line);
  return true;
}

// 個体の 3 つの文字列 (type、path、id) は arena の中の位置で持ち、全部を足した後に引く
static StrArena g_inst_arena;
static size_t *g_inst_at;
static LubInspectInstance *g_inst;
static int32_t g_inst_n, g_inst_cap, g_inst_at_cap;

static void inspect_instance(void *user, const char *type, const char *path,
                             const char *id) {
  (void)user;
  g_inst_at = (size_t *)grow(g_inst_at, &g_inst_at_cap, 3 * (g_inst_n + 1),
                             sizeof(size_t));
  g_inst = (LubInspectInstance *)grow(g_inst, &g_inst_cap, g_inst_n + 1,
                                      sizeof(LubInspectInstance));
  if (3 * (g_inst_n + 1) > g_inst_at_cap || g_inst_n >= g_inst_cap)
    return;
  g_inst_at[3 * g_inst_n] = arena_add(&g_inst_arena, type, SDL_strlen(type));
  g_inst_at[3 * g_inst_n + 1] =
      arena_add(&g_inst_arena, path, SDL_strlen(path));
  g_inst_at[3 * g_inst_n + 2] = arena_add(&g_inst_arena, id, SDL_strlen(id));
  g_inst_n++;
}

static void hook_inspect_instances(App *app, int32_t max_depth,
                                   int32_t max_children,
                                   const LubInspectInstance **out,
                                   int32_t *count) {
  (void)app;
  arena_reset(&g_inst_arena);
  g_inst_n = 0;
  if (!vm_lub_vm)
    return;
  tvm_inspect_instances(vm_lub_vm, g_game.cls, (uint32_t)max_depth,
                        (uint32_t)max_children, inspect_instance, NULL);
  for (int32_t i = 0; i < g_inst_n; i++) {
    g_inst[i].type = arena_str(&g_inst_arena, g_inst_at[3 * i]);
    g_inst[i].path = arena_str(&g_inst_arena, g_inst_at[3 * i + 1]);
    g_inst[i].id = arena_str(&g_inst_arena, g_inst_at[3 * i + 2]);
  }
  *out = g_inst;
  *count = g_inst_n;
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
static void on_event(const LubEventData *e) {
  if (!vm_lub_vm)
    return;
  TvmSlot args[1];
  if (g_tool.event >= 0) {
    args[0].p = vm_lub_event(e);
    call_hook(&g_tool, g_tool.event, "overlay OnEvent", args, 1);
  }
  App *app = lub_api_app(g_ctx);
  if (g_game.event < 0 || event_grabbed(app, e) || rewind_paused(app))
    return;
  args[0].p = vm_lub_event(e);
  call_hook(&g_game, g_game.event, "OnEvent", args, 1);
}

static bool on_frame(float dt) {
  if (!vm_lub_vm)
    return true;
  App *app = lub_api_app(g_ctx);
  // Rewind: 記録中は控え、止めていれば止めた frame の状態に戻して、控えた入力と dt で作り直す
  double game_dt = dt;
  rewind_frame_begin(app, &game_dt);
  TvmSlot args[1];
  args[0].f = (float)game_dt;
  bool ok = call_hook(&g_game, g_game.frame, "OnFrame", args, 1);
  rewind_frame_end(app);
  args[0].f = dt;
  if (g_tool.frame >= 0 && vm_lub_vm) {
    // overlay はゲームの後に描き、ゲームの OnFrame が失敗した frame でも動く。
    // 失敗で開いたままの pass は閉じてから渡す。
    // どちらかが失敗した frame は失敗した frame (resource の sweep までの frame に数えない)
    if (pass_state_in_pass(&app->pass))
      pass_state_end(&app->pass);
    ok = call_hook(&g_tool, g_tool.frame, "overlay OnFrame", args, 1) && ok;
  }
  if (vm_lub_vm)
    tvm_frame_end(vm_lub_vm);
  return ok;
}

// 差分を当てた後の OnReload (ゲームの後に overlay)
static void call_reload(void) {
  call_hook(&g_game, g_game.reload, "OnReload", NULL, 0);
  call_hook(&g_tool, g_tool.reload, "overlay OnReload", NULL, 0);
}

static void call_build_failed(const char *errors, size_t len) {
  if (!vm_lub_vm)
    return;
  const Hooks *hooks[2] = {&g_game, &g_tool};
  for (int i = 0; i < 2 && vm_lub_vm; i++) {
    if (hooks[i]->build_failed < 0)
      continue;
    TvmSlot args[1];
    args[0].p = tvm_string_new(vm_lub_vm, errors, len);
    call_hook(hooks[i], hooks[i]->build_failed,
              i ? "overlay OnBuildFailed" : "OnBuildFailed", args, 1);
  }
}

static void call_reload_notes(const char *notes, size_t len) {
  const Hooks *hooks[2] = {&g_game, &g_tool};
  for (int i = 0; i < 2 && vm_lub_vm; i++) {
    if (hooks[i]->reload_notes < 0)
      continue;
    TvmSlot args[1];
    args[0].p = tvm_string_new(vm_lub_vm, notes, len);
    call_hook(hooks[i], hooks[i]->reload_notes,
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
static void apply_vm_batch(const char *text, size_t len, const char *notes,
                           size_t notes_len) {
  uint8_t *batch = (uint8_t *)SDL_malloc(len);
  if (!batch)
    return;
  size_t size = b64_decode(text, len, batch);
  if (!vm_lub_vm) {
    SDL_free(batch);
    restart("a batch arrived without a running VM");
    return;
  }
  TvmStatus st = tvm_apply_batch(vm_lub_vm, batch, size);
  SDL_free(batch);
  if (st == TVM_RESTART) {
    SDL_Log("lub: reload needs a restart: %s", tvm_error(vm_lub_vm));
    restart("restart required");
    return;
  }
  if (st != TVM_OK) {
    SDL_Log("lub: reload failed: %s", tvm_error(vm_lub_vm));
    return;
  }
  if (tvm_error(vm_lub_vm)[0])
    SDL_Log("lub: reload: %s", tvm_error(vm_lub_vm));
  if (!bind_entry()) {
    restart("the reloaded module does not bind");
    return;
  }
  SDL_Log("lub: reloaded");
  call_reload();
  if (notes)
    call_reload_notes(notes, notes_len);
}

// tcs vm --watch が書いた frame を、届いた順に当てる
static void poll_project(void) {
  uint64_t now = SDL_GetTicks();
  if (now >= g_next_scan) {
    vm_project_scan(&g_project);
    g_next_scan = now + 300;
  }
  TcsFrameKind kind;
  char *body = NULL;
  size_t len = 0;
  while (tcs_pipeline_next_frame(&g_tcs, &kind, &body, &len)) {
    if (kind == TCS_FRAME_RELOAD_NOTES) {
      // 続く vm_batch に付ける
      SDL_free(g_notes);
      g_notes = body;
      g_notes_len = len;
      body = NULL;
      continue;
    }
    if (kind == TCS_FRAME_VM_BATCH)
      apply_vm_batch(body, len, g_notes, g_notes_len);
    else if (kind == TCS_FRAME_RESTART_REQUIRED) {
      SDL_Log("lub: reload needs a restart:\n%s", body);
      restart("restart required");
    } else if (kind == TCS_FRAME_BUILD_FAILED) {
      // 診断は tcs が stderr に書く
      SDL_Log("lub: reload failed: the build has errors, the running code is "
              "unchanged");
      call_build_failed(body, len);
    }
    SDL_free(body);
    body = NULL;
    SDL_free(g_notes);
    g_notes = NULL;
  }
}

// 入力を集めて tcs vm --watch を張り、最初の build (.tcsb が書き直される) を待つ。
// 通らない間は直して保存されるのを待つ。
static bool start_project(void) {
  if (!vm_project_open(&g_project, g_csproj))
    return false;
  SDL_strlcpy(g_game.cls, g_project.entry, sizeof(g_game.cls));
  SDL_strlcpy(g_tool.cls, g_project.overlay, sizeof(g_tool.cls));
  SDL_strlcpy(g_module, g_project.out_tcsb, sizeof(g_module));
  vm_project_scan(&g_project);
  for (int i = 0; i < g_project.nextensions; i++)
    load_extension(g_project.extensions[i]);
  const char *args[256];
  int n = vm_project_args(&g_project, args, 256);
  int64_t before = file_mtime(g_module);
  if (!tcs_vm_pipeline_start(&g_tcs, args, n))
    return false;
  SDL_Log("tcs: compiling %s for the tcs VM ...", g_csproj);
  for (;;) {
    int64_t mtime = file_mtime(g_module);
    if (mtime >= 0 && mtime != before)
      break;
    if (!tcs_pipeline_alive(&g_tcs))
      return false;
    TcsFrameKind kind;
    char *body = NULL;
    size_t len = 0;
    while (tcs_pipeline_next_frame(&g_tcs, &kind, &body, &len))
      SDL_free(body); // 最初の build の前の診断は tcs が stderr にも書く
    if (SDL_GetTicks() >= g_next_scan) {
      vm_project_scan(&g_project);
      g_next_scan = SDL_GetTicks() + 300;
    }
    SDL_Delay(50);
  }
  SDL_Log("tcs: %s ready (watching)", g_module);
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

static bool parse_args(int argc, char **argv, LubHostOpts *opts) {
  const char *module = NULL, *entry = NULL;
  int first = 1;
#ifdef VM_PROJECT
  const char *export_os_name = NULL, *export_out = NULL;
  if (argc > 1 && !strcmp(argv[1], "export")) {
    g_export = true;
    first = 2;
  }
#endif
  for (int i = first; i < argc; i++) {
#ifdef VM_PROJECT
    if (g_export && !strcmp(argv[i], "-o") && i + 1 < argc) {
      export_out = argv[++i];
      continue;
    }
    if (g_export && !export_os_name) {
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
      SDL_strlcpy(g_tool.cls, argv[++i], sizeof(g_tool.cls));
    else if (!strcmp(argv[i], "--extension") && i + 1 < argc)
      load_extension(argv[++i]);
    else if (!module)
      module = argv[i];
    else {
      SDL_Log("lub: unknown argument %s", argv[i]);
      return false;
    }
  }
#ifdef VM_PROJECT
  if (g_export && (!module || !begin_export(export_os_name, export_out)))
    return false;
#endif
  if (!module) {
    SDL_Log("usage: lub <Entry>.tcsb|<Entry>.csproj [--entry CLASS] "
            "[--fixed-dt S] "
            "[--capture PNG] [--capture-frame N] [--digest]");
    return false;
  }
  size_t mlen = strlen(module);
  if (mlen > 7 && !SDL_strcasecmp(module + mlen - 7, ".csproj")) {
    g_project_mode = true;
    SDL_strlcpy(g_csproj, module, sizeof(g_csproj));
    return true;
  }
  SDL_strlcpy(g_module, module, sizeof(g_module));
  size_t n = strlen(g_module);
  size_t stem = n > 5 && !strcmp(g_module + n - 5, ".tcsb") ? n - 5 : n;
  SDL_snprintf(g_batch, sizeof(g_batch), "%.*s.batch", (int)stem, g_module);
  SDL_snprintf(g_rev, sizeof(g_rev), "%.*s.rev", (int)stem, g_module);
  if (entry)
    SDL_strlcpy(g_game.cls, entry, sizeof(g_game.cls));
  else {
    const char *base = strrchr(g_module, '/');
    base = base ? base + 1 : g_module;
    size_t len = stem - (size_t)(base - g_module);
    SDL_snprintf(g_game.cls, sizeof(g_game.cls), "%.*s", (int)len, base);
  }
  return true;
}

#ifdef __EMSCRIPTEN__
// player を外す前に page が呼ぶ (src/tcs_host.c の lub_tcs_shutdown と同じ)
EMSCRIPTEN_KEEPALIVE void lub_vm_shutdown(void) {
  if (!g_ctx || g_shutting_down)
    return;
  g_shutting_down = true;
  emscripten_cancel_main_loop();
  call_quit();
  free_vm();
  lub_host_destroy(g_ctx);
  g_ctx = NULL;
  vm_lub_context = NULL;
}

// page が変換した C# の compile error を、ゲームと overlay の OnBuildFailed に渡す
EMSCRIPTEN_KEEPALIVE void lub_vm_build_failed(const char *errors) {
  if (g_ctx && errors)
    call_build_failed(errors, strlen(errors));
}

EMSCRIPTEN_KEEPALIVE void lub_vm_volume(float volume) {
  if (g_ctx)
    lub_audio_master_volume(g_ctx, volume);
}
#endif

SDL_AppResult lub_vm_app_init(int argc, char **argv,
                              const LubVmConfig *config) {
  SDL_SetHint(SDL_HINT_ASSERT, "abort");
  signal(SIGSEGV, on_crash);
  signal(SIGFPE, on_crash);
  signal(SIGILL, on_crash);
  // 静的に link した拡張は、読み込む拡張より先に登録する
  for (int i = 0;
       config && i < config->nextensions && g_nextensions < MAX_EXTENSIONS; i++)
    g_extensions[g_nextensions++] = config->extensions[i];
  LubHostOpts opts = {0};
  if (!parse_args(argc, argv, &opts))
    return SDL_APP_FAILURE;
  g_ctx = lub_host_create(&opts);
  if (!g_ctx)
    return SDL_APP_FAILURE;
  vm_lub_context = g_ctx;
  lub_api_app(g_ctx)->debug_hooks = &g_debug_hooks;
  pick_set_sites(lub_api_app(g_ctx), vm_pick_sites, NULL);
  clear_hooks(&g_game);
  clear_hooks(&g_tool);
#ifdef VM_PROJECT
  if (g_project_mode && !start_project())
    return SDL_APP_FAILURE;
#else
  if (g_project_mode) {
    SDL_Log("lub: this player cannot compile a .csproj");
    return SDL_APP_FAILURE;
  }
#endif
#ifdef VM_PROJECT
  // export は最初の build だけを使う
  if (g_export && g_project_mode)
    tcs_pipeline_stop(&g_tcs);
#endif
  uint64_t t0 = SDL_GetTicksNS();
  if (!load_module())
    return SDL_APP_FAILURE;
#ifdef VM_PROJECT
  if (g_export) {
    char name[320];
    SDL_snprintf(name, sizeof(name), "%s.tcsb", g_game.cls);
    export_copy(g_module, name);
  }
#endif
  call_init();
#ifdef VM_PROJECT
  if (g_export && lub_host_exit_code(g_ctx) != 0)
    return SDL_APP_FAILURE;
#endif
  ack(read_revision(), true, NULL, (double)(SDL_GetTicksNS() - t0) / 1e6);
  if (lub_host_quit_requested(g_ctx))
    return lub_host_exit_code(g_ctx) == 0 ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
  return lub_host_start(g_ctx) == LUB_OK ? SDL_APP_CONTINUE : SDL_APP_FAILURE;
}

SDL_AppResult lub_vm_app_event(SDL_Event *event) {
  LubEventData translated;
  if (!lub_host_translate_event(lub_api_app(g_ctx), event, &translated))
    return SDL_APP_CONTINUE;
  if (translated.kind == LUB_EVENT_KIND_QUIT)
    return SDL_APP_SUCCESS;
  on_event(&translated);
  return SDL_APP_CONTINUE;
}

SDL_AppResult lub_vm_app_iterate(void) {
  if (lub_host_quit_requested(g_ctx))
    return lub_host_exit_code(g_ctx) == 0 ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
#ifdef VM_PROJECT
  if (g_export) {
    // OnInit の後、最初の frame の代わりに OnExport を 1 回呼んで終わる
    float dt = 0.0f;
    bool ok = lub_host_frame_begin(g_ctx, &dt);
    if (ok) {
      ok = call_hook(&g_game, g_game.export_, "OnExport", NULL, 0);
      lub_host_frame_end(g_ctx, ok);
    }
    ok = ok && !export_failed() && lub_host_exit_code(g_ctx) == 0;
    if (ok && !strcmp(export_os(), "web")) {
      char start[320];
      SDL_snprintf(start, sizeof(start), "%s.tcsb", g_game.cls);
      ok = export_write_web_page(export_out_dir(), g_game.cls, start);
    }
    if (ok)
      SDL_Log("export: wrote %s", export_out_dir());
    else
      SDL_Log("export: failed");
    return ok ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
  }
#endif
#ifdef VM_PROJECT
  if (g_project_mode)
    poll_project();
  else
#endif
    poll_reload();
  float dt;
  if (lub_host_frame_begin(g_ctx, &dt)) {
    bool ok = on_frame(dt);
    lub_host_frame_end(g_ctx, ok);
  }
  // App.Restart は frame の終わりに作り直す (VM の frame の中では作り直せない)
  App *app = lub_api_app(g_ctx);
  if (app->restart_requested) {
    app->restart_requested = false;
    if (vm_lub_vm)
      restart("App.Restart was called");
  }
  if (lub_host_quit_requested(g_ctx))
    return lub_host_exit_code(g_ctx) == 0 ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
  return SDL_APP_CONTINUE;
}

void lub_vm_app_quit(SDL_AppResult result) {
#ifdef __EMSCRIPTEN__
  lub_vm_shutdown();
#else
  if (!g_ctx)
    return;
  call_quit();
  free_vm();
#ifdef VM_PROJECT
  tcs_pipeline_stop(&g_tcs);
  vm_project_close(&g_project);
#endif
  int32_t code = lub_host_exit_code(g_ctx);
  if (result == SDL_APP_FAILURE && !lub_host_quit_requested(g_ctx)) {
    // 起動の失敗など、Quit を経ない失敗も終了状態の file に失敗として残す
    code = 1;
    lub_app_quit(g_ctx, code);
  }
  lub_host_destroy(g_ctx);
  g_ctx = NULL;
  // SDL の main は AppIterate が返した成功 / 失敗で終わる。
  // OnQuit の中の Quit と 1 以外の code はそれに載らないので、ここで返す
  if (code != 0 || result == SDL_APP_FAILURE) {
    SDL_Quit();
    exit(code);
  }
#endif
}
