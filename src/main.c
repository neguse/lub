#define SDL_MAIN_USE_CALLBACKS 1
#include "tcs_build.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>
#include <lub/lub_vm.h>
#include <stdbool.h>
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
  if (!resolve_entry(&argc, &argv)) {
    print_usage();
    return SDL_APP_FAILURE;
  }
  return lub_vm_app_init(argc, argv, NULL);
}

SDL_AppResult SDL_AppEvent(void *appstate, SDL_Event *event) {
  (void)appstate;
  return lub_vm_app_event(event);
}

SDL_AppResult SDL_AppIterate(void *appstate) {
  (void)appstate;
  return lub_vm_app_iterate();
}

void SDL_AppQuit(void *appstate, SDL_AppResult result) {
  (void)appstate;
  lub_vm_app_quit(result);
}
