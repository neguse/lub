// tcs VM の player: tcs vm が作った bytecode (.tcsb) を tcs VM (third_party/tcs/tcsvm) で動かす。
// frame の骨格は host API (src/host_api.c) が持ち、ここは VM の読み込み、entry の呼び出し、hot reload だけ。
// API は lub-gen vm が作る host の関数 (src/gen/vm_api_gen.c) で、player はゲームを選ばない (stub の全部を登録する)。
//
//   lub_vm <Entry>.tcsb [--entry CLASS] [--fixed-dt S] [--capture PNG] [--capture-frame N] [--digest]
//
// entry class は --entry (IL の名前、namespace は '_' で繋ぐ)、無ければ .tcsb の basename。
// OnFrame(float) は必須、OnInit / OnEvent(EventData) / OnQuit は任意。
// hot reload: <Entry>.batch (tcs vm --watch の vm_batch。module と計画) が現れたら当てて消す。
// 当てられない差分 (restart) と <Entry>.tcsb の書き直しは、OnQuit の後に新しい VM を作って OnInit から始める。
// <Entry>.rev (playground が置く build の revision) があれば、読み込みと batch の後に
// `@@tcs_commit {"revision":N,"ok":...}` を出す (Lua の player の commit ACK と同じ形)。
// 当てた revision 以下の batch (ACK の再送) は当てずに ACK だけ返す。
// 誤り (C# の例外に当たる VM の fault) は log に出し、その frame を失敗にして続ける。
#define SDL_MAIN_USE_CALLBACKS 1
#include "api_internal.h"
#include "app.h"
#include "host_api.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>
#include <lub/lub_host.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef __EMSCRIPTEN__
#include <emscripten.h>
#endif

#include "gen/vm_api_gen.c"

static LubContext *g_ctx;
static char g_module[1024];
static char g_batch[1024];
static char g_rev[1024];
static int32_t g_acked = -1; // ACK を返した revision
static char g_entry[256];
static int64_t g_module_mtime;
static int32_t g_on_init = -1, g_on_frame = -1, g_on_event = -1, g_on_quit = -1;
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

static int32_t find_hook(const char *method) {
  char name[320];
  SDL_snprintf(name, sizeof(name), "%s.%s", g_entry, method);
  return tvm_find(vm_lub_vm, name);
}

// 関数の番号は reload で変わるので、読み込みと reload の後に引き直す。
// 型の offset も同じ時に確かめる。
static bool bind_entry(void) {
  char error[512];
  if (!vm_lub_verify(vm_lub_vm, error, sizeof(error))) {
    SDL_Log("lub: the module does not match the host: %s", error);
    return false;
  }
  g_on_init = find_hook("OnInit");
  g_on_frame = find_hook("OnFrame");
  g_on_event = find_hook("OnEvent");
  g_on_quit = find_hook("OnQuit");
  if (g_on_frame < 0) {
    SDL_Log("lub: %s has no static OnFrame(float)", g_entry);
    return false;
  }
  return true;
}

static bool call_hook(int32_t fn, const char *what, const TvmSlot *args,
                      size_t nargs) {
  if (!vm_lub_vm || fn < 0)
    return true;
  if (tvm_call(vm_lub_vm, fn, args, nargs, NULL, 0) != TVM_OK) {
    log_fault(what);
    return false;
  }
  return true;
}

static void free_vm(void) {
  if (vm_lub_vm)
    tvm_free(vm_lub_vm);
  vm_lub_vm = NULL;
  g_on_init = g_on_frame = g_on_event = g_on_quit = -1;
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
// 物理の world はゲームの状態なので捨て、key で宣言する資源は残す (Lua の player の restart と同じ)。
static void restart(const char *why) {
  SDL_Log("lub: %s, starting the VM again from %s", why, g_module);
  call_hook(g_on_quit, "OnQuit", NULL, 0);
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
  app->entry_restarting = true;
  call_hook(g_on_init, "OnInit", NULL, 0);
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
    ack(rev, true, NULL, (double)(SDL_GetTicksNS() - t0) / 1e6);
    return;
  }
  int64_t mtime = file_mtime(g_module);
  if (mtime >= 0 && mtime != g_module_mtime)
    restart(vm_lub_vm ? "the module was rewritten" : "the module was rebuilt");
}

static void on_event(const LubEventData *e) {
  if (!vm_lub_vm || g_on_event < 0)
    return;
  TvmSlot args[1];
  args[0].p = vm_lub_event(e);
  call_hook(g_on_event, "OnEvent", args, 1);
}

static bool on_frame(float dt) {
  if (!vm_lub_vm)
    return true;
  TvmSlot args[1];
  args[0].f = dt;
  bool ok = call_hook(g_on_frame, "OnFrame", args, 1);
  tvm_frame_end(vm_lub_vm);
  return ok;
}

static bool parse_args(int argc, char **argv, LubHostOpts *opts) {
  const char *module = NULL, *entry = NULL;
  for (int i = 1; i < argc; i++) {
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
    else if (!module)
      module = argv[i];
    else {
      SDL_Log("lub: unknown argument %s", argv[i]);
      return false;
    }
  }
  if (!module) {
    SDL_Log("usage: lub_vm <Entry>.tcsb [--entry CLASS] [--fixed-dt S] "
            "[--capture PNG] [--capture-frame N] [--digest]");
    return false;
  }
  SDL_strlcpy(g_module, module, sizeof(g_module));
  size_t n = strlen(g_module);
  size_t stem = n > 5 && !strcmp(g_module + n - 5, ".tcsb") ? n - 5 : n;
  SDL_snprintf(g_batch, sizeof(g_batch), "%.*s.batch", (int)stem, g_module);
  SDL_snprintf(g_rev, sizeof(g_rev), "%.*s.rev", (int)stem, g_module);
  if (entry)
    SDL_strlcpy(g_entry, entry, sizeof(g_entry));
  else {
    const char *base = strrchr(g_module, '/');
    base = base ? base + 1 : g_module;
    size_t len = stem - (size_t)(base - g_module);
    SDL_snprintf(g_entry, sizeof(g_entry), "%.*s", (int)len, base);
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
  call_hook(g_on_quit, "OnQuit", NULL, 0);
  free_vm();
  lub_host_destroy(g_ctx);
  g_ctx = NULL;
  vm_lub_context = NULL;
}

EMSCRIPTEN_KEEPALIVE void lub_vm_volume(float volume) {
  if (g_ctx)
    lub_audio_master_volume(g_ctx, volume);
}
#endif

SDL_AppResult SDL_AppInit(void **appstate, int argc, char **argv) {
  (void)appstate;
  SDL_SetHint(SDL_HINT_ASSERT, "abort");
  LubHostOpts opts = {0};
  if (!parse_args(argc, argv, &opts))
    return SDL_APP_FAILURE;
  g_ctx = lub_host_create(&opts);
  if (!g_ctx)
    return SDL_APP_FAILURE;
  vm_lub_context = g_ctx;
  uint64_t t0 = SDL_GetTicksNS();
  if (!load_module())
    return SDL_APP_FAILURE;
  call_hook(g_on_init, "OnInit", NULL, 0);
  ack(read_revision(), true, NULL, (double)(SDL_GetTicksNS() - t0) / 1e6);
  if (lub_host_quit_requested(g_ctx))
    return lub_host_exit_code(g_ctx) == 0 ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
  return lub_host_start(g_ctx) == LUB_OK ? SDL_APP_CONTINUE : SDL_APP_FAILURE;
}

SDL_AppResult SDL_AppEvent(void *appstate, SDL_Event *event) {
  (void)appstate;
  LubEventData translated;
  if (!lub_host_translate_event(lub_api_app(g_ctx), event, &translated))
    return SDL_APP_CONTINUE;
  if (translated.kind == LUB_EVENT_KIND_QUIT)
    return SDL_APP_SUCCESS;
  on_event(&translated);
  return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppIterate(void *appstate) {
  (void)appstate;
  if (lub_host_quit_requested(g_ctx))
    return lub_host_exit_code(g_ctx) == 0 ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
  poll_reload();
  float dt;
  if (lub_host_frame_begin(g_ctx, &dt)) {
    bool ok = on_frame(dt);
    lub_host_frame_end(g_ctx, ok);
  }
  if (lub_host_quit_requested(g_ctx))
    return lub_host_exit_code(g_ctx) == 0 ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
  return SDL_APP_CONTINUE;
}

void SDL_AppQuit(void *appstate, SDL_AppResult result) {
  (void)appstate;
  (void)result;
#ifdef __EMSCRIPTEN__
  lub_vm_shutdown();
#else
  if (!g_ctx)
    return;
  call_hook(g_on_quit, "OnQuit", NULL, 0);
  free_vm();
  lub_host_destroy(g_ctx);
  g_ctx = NULL;
#endif
}
