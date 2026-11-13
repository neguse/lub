#define SDL_MAIN_USE_CALLBACKS 1
#include "path_util.h"
#include "serve.h"
#include "tcs_build.h"
#include "vm_project.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>
#include <lub/lub_vm.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <direct.h>
#define chdir _chdir
#else
#include <unistd.h>
#endif

// lub の実行ファイルの入口。
// ゲームは tcs VM の player (src/vm_player.c) が動かし、ここは起動の形を振り分けるだけ。
// `lub check <entry>.csproj` は窓も VM も作らずに tcs check の結果を終了コードにする。
// `--serve` は窓を作らず、web の tcs VM の player にゲームを配る (src/serve.c)。
static bool g_serve_mode = false;
static ServeState g_serve;
// --serve の入力 (tcs vm --watch の入力の一覧を探し直す)
static VmProject g_serve_project;
static uint64_t g_serve_next_scan;

static bool has_extension(const char *path, const char *ext) {
  size_t n = SDL_strlen(path), m = SDL_strlen(ext);
  return n >= m && SDL_strcasecmp(path + n - m, ext) == 0;
}

// tcs VM の player に渡す entry を決める。
// entry が sample の名前 (samples/<name>/) なら、その中の .csproj にする。
// entry が無ければ、実行ファイルの隣 (app bundle では resource の場所) の .tcsb (lub export の snapshot) をそこを cwd にして動かす。
// *argv を VM の player に渡す引数にする。
// 動かす entry が無ければ false。
static char g_vm_entry[1024];
static char *g_vm_argv[64];
static SDL_EnumerationResult SDLCALL find_tcsb(void *out, const char *dir,
                                               const char *name) {
  (void)dir;
  size_t len = SDL_strlen(name);
  if (len > 5 && SDL_strcasecmp(name + len - 5, ".tcsb") == 0) {
    SDL_strlcpy((char *)out, name, sizeof(g_vm_entry));
    return SDL_ENUM_SUCCESS;
  }
  return SDL_ENUM_CONTINUE;
}
static bool resolve_entry(int *argc, char ***argvp) {
  char **argv = *argvp;
  int n = *argc;
  int first = 1;
  if (n >= 2 && strcmp(argv[1], "export") == 0)
    first = 3;
  int entry = -1;
  for (int i = first; i < n; ++i) {
    // 値を取る option は値ごと飛ばす
    if ((strcmp(argv[i], "--capture") == 0 ||
         strcmp(argv[i], "--capture-frame") == 0 ||
         strcmp(argv[i], "--fixed-dt") == 0 ||
         strcmp(argv[i], "--entry") == 0 ||
         strcmp(argv[i], "--extension") == 0 || strcmp(argv[i], "-o") == 0) &&
        i + 1 < n) {
      i++;
      continue;
    }
    if (argv[i][0] != '-' && entry < 0)
      entry = i;
  }
  if (entry < 0) {
    // entry の無い起動 (配った snapshot): 実行ファイルの隣の .tcsb
    const char *base = SDL_GetBasePath();
    if (first != 1 || !base || n + 2 > (int)SDL_arraysize(g_vm_argv))
      return false;
    g_vm_entry[0] = 0;
    SDL_EnumerateDirectory(base, find_tcsb, g_vm_entry);
    if (!g_vm_entry[0] || chdir(base) != 0)
      return false;
    g_vm_argv[0] = argv[0];
    g_vm_argv[1] = g_vm_entry;
    for (int i = 1; i < n; ++i)
      g_vm_argv[i + 1] = argv[i];
    g_vm_argv[n + 1] = NULL;
    *argvp = g_vm_argv;
    *argc = n + 1;
    return true;
  }
  // sample の名前
  const char *path = argv[entry];
  if (has_extension(path, ".csproj") || has_extension(path, ".tcsb") ||
      strchr(path, '/') || strchr(path, '\\'))
    return true;
  char dir[512];
  SDL_snprintf(dir, sizeof(dir), "samples/%s", path);
  int count = 0;
  char **found = SDL_GlobDirectory(dir, "*.csproj", 0, &count);
  if (found && count > 0) {
    SDL_snprintf(g_vm_entry, sizeof(g_vm_entry), "%s/%s", dir, found[0]);
    argv[entry] = g_vm_entry;
  }
  SDL_free(found);
  return true;
}

static void print_usage(void) {
  SDL_Log("usage: lub <Entry>.csproj|<Entry>.tcsb|<sample> [--entry CLASS] "
          "[--fixed-dt S] [--capture PNG] [--capture-frame N] [--digest]\n"
          "       lub check <Entry>.csproj\n"
          "       lub --serve <Entry>.csproj [--port N]\n"
          "       lub export <web|linux|windows|macos|ios|android> <entry> "
          "-o <dir>");
}

// iOS の app は cwd が "/" で始まる。
// 相対パスは常に bundle 基準にする。
static void enter_bundle(void) {
#ifdef SDL_PLATFORM_IOS
  const char *base = SDL_GetBasePath();
  if (base && chdir(base) != 0)
    SDL_Log("chdir to %s failed", base);
#endif
}

// `lub --serve <entry>.csproj`: 窓も GPU も使わず、tcs vm --watch の出力をブラウザの tcs VM の player に配る。
static SDL_AppResult start_serve(int argc, char *argv[]) {
  if (!SDL_Init(0)) {
    SDL_Log("SDL_Init failed: %s", SDL_GetError());
    return SDL_APP_FAILURE;
  }

  const char *script = NULL;
  const char *wasm_dir = NULL;
  const char *slang_dir = NULL;
  int port = 8080;
  for (int i = 1; i < argc; i++) {
    if (strcmp(argv[i], "--serve") == 0)
      continue;
    if (strcmp(argv[i], "--port") == 0 && i + 1 < argc) {
      port = atoi(argv[++i]);
    } else if (strcmp(argv[i], "--wasm-dir") == 0 && i + 1 < argc) {
      wasm_dir = argv[++i];
    } else if (strcmp(argv[i], "--slang-dir") == 0 && i + 1 < argc) {
      slang_dir = argv[++i];
    } else {
      script = argv[i];
    }
  }
  if (!script || !has_extension(script, ".csproj")) {
    SDL_Log("FATAL: --serve requires a .csproj path");
    return SDL_APP_FAILURE;
  }

  // Auto-detect wasm_dir and slang_dir from executable path if not specified
  const char *base_path = SDL_GetBasePath();
  char auto_wasm[768] = {0};
  char auto_slang[768] = {0};
  if (base_path) {
    // base_path is e.g. "/path/to/lub/build/" — strip trailing slash copy
    char bp_copy[768];
    SDL_strlcpy(bp_copy, base_path, sizeof(bp_copy));
    size_t blen = strlen(bp_copy);
    if (blen > 0 && bp_copy[blen - 1] == '/')
      bp_copy[blen - 1] = '\0';
    char lub_root[768];
    path_dirname(bp_copy, lub_root, sizeof(lub_root));
    if (!wasm_dir) {
      SDL_snprintf(auto_wasm, sizeof(auto_wasm), "%s/build/wasm", lub_root);
      wasm_dir = auto_wasm;
    }
    if (!slang_dir) {
      SDL_snprintf(auto_slang, sizeof(auto_slang), "%s/web/public/slang",
                   lub_root);
      slang_dir = auto_slang;
    }
  }
  if (!wasm_dir) {
    SDL_Log("FATAL: --wasm-dir required (could not auto-detect)");
    return SDL_APP_FAILURE;
  }
  if (!slang_dir) {
    SDL_Log("FATAL: --slang-dir required (could not auto-detect)");
    return SDL_APP_FAILURE;
  }

  // web の tcs VM の player は wasm の dir の vm/
  char vm_wasm[800];
  SDL_snprintf(vm_wasm, sizeof(vm_wasm), "%s/vm", wasm_dir);
  if (!vm_project_open(&g_serve_project, script))
    return SDL_APP_FAILURE;
  vm_project_scan(&g_serve_project);
  const char *args[256];
  int n = vm_project_args(&g_serve_project, args, 256);
  if (!serve_start_vm(&g_serve, script, args, n, vm_wasm, slang_dir, port))
    return SDL_APP_FAILURE;
  g_serve_mode = true;
  return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppInit(void **appstate, int argc, char *argv[]) {
  (void)appstate;

  // SDL の assertion は既定で message box を開いて利用者の選択を待つ。
  // headless (Xvfb / CI / bench) ではクリックされないので process が止まり続ける。
  // dialog を出さず abort させる。
  // 環境変数 SDL_ASSERT があれば hint よりそちらが優先される (SDL_GetHint は env を先に見る) ので、利用者の設定はそのまま効く。
  SDL_SetHint(SDL_HINT_ASSERT, "abort");

  enter_bundle();
  // `lub check <entry>`: 窓も VM も作らず、tcs の結果を終了コードにする
  if (argc > 1 && strcmp(argv[1], "check") == 0) {
    if (argc != 3 || !has_extension(argv[2], ".csproj")) {
      SDL_Log("usage: lub check <entry>.csproj");
      return SDL_APP_FAILURE;
    }
    return tcs_check(argv[2]) ? SDL_APP_SUCCESS : SDL_APP_FAILURE;
  }
  for (int i = 1; i < argc; i++) {
    if (strcmp(argv[i], "--serve") == 0)
      return start_serve(argc, argv);
  }
  if (!resolve_entry(&argc, &argv)) {
    print_usage();
    return SDL_APP_FAILURE;
  }
  return lub_vm_app_init(argc, argv, NULL);
}

SDL_AppResult SDL_AppEvent(void *appstate, SDL_Event *event) {
  (void)appstate;
  if (g_serve_mode)
    return event->type == SDL_EVENT_QUIT ? SDL_APP_SUCCESS : SDL_APP_CONTINUE;
  return lub_vm_app_event(event);
}

SDL_AppResult SDL_AppIterate(void *appstate) {
  (void)appstate;
  if (g_serve_mode) {
    // file を足すか消すと、tcs vm --watch がその一覧から変換し直す
    if (SDL_GetTicks() >= g_serve_next_scan) {
      vm_project_scan(&g_serve_project);
      g_serve_next_scan = SDL_GetTicks() + 300;
    }
    return serve_tick(&g_serve) ? SDL_APP_CONTINUE : SDL_APP_SUCCESS;
  }
  return lub_vm_app_iterate();
}

void SDL_AppQuit(void *appstate, SDL_AppResult result) {
  (void)appstate;
  if (g_serve_mode) {
    serve_stop(&g_serve);
    vm_project_close(&g_serve_project);
    SDL_Quit();
    return;
  }
  lub_vm_app_quit(result);
}
