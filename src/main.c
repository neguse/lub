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

// Entry point of the lub executable.
// Games are run by the tcs VM player (src/vm_player.c); this code only
// dispatches on the kind of launch. `lub check <entry>.csproj` creates neither
// a window nor a VM and turns the tcs check result into the exit code.

static bool has_extension(const char *path, const char *ext) {
  size_t n = SDL_strlen(path), m = SDL_strlen(ext);
  return n >= m && SDL_strcasecmp(path + n - m, ext) == 0;
}

// Decide the entry to pass to the tcs VM player.
// If entry is a sample name (samples/<name>/), use the .csproj inside it.
// If there is no entry, run the .tcsb (a lub export snapshot) next to the
// executable (the resource location in an app bundle), with that directory as
// cwd. Set *argv to the arguments to pass to the VM player. false if there is
// no entry to run.
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
    // Skip options that take a value together with their value
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
    // Launch without an entry (a distributed snapshot): the .tcsb next to the
    // executable
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
  // Sample name
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

// iOS apps start with cwd "/".
// Relative paths are always resolved against the bundle.
static void enter_bundle(void) {
#ifdef SDL_PLATFORM_IOS
  const char *base = SDL_GetBasePath();
  if (base && chdir(base) != 0)
    SDL_Log("chdir to %s failed", base);
#endif
}

SDL_AppResult SDL_AppInit(void **appstate, int argc, char *argv[]) {
  (void)appstate;

  // By default, SDL assertions open a message box and wait for the user's
  // choice. In headless runs (Xvfb / CI / bench) nobody clicks, so the process
  // stays stuck. Abort without showing a dialog. If the SDL_ASSERT environment
  // variable is set, it takes precedence over the hint (SDL_GetHint checks env
  // first), so the user's setting still applies.
  SDL_SetHint(SDL_HINT_ASSERT, "abort");

  enter_bundle();
  // `lub check <entry>`: create neither a window nor a VM, and turn the tcs
  // result into the exit code
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
