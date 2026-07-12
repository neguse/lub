// tcs (TinyC#) pipeline: .csproj entry を hxml と対称の DX で動かす。
// 起動時に tcs を --watch で spawn し、初回出力 (.lub/<Base>.lua) を待って
// entry にする。以後の .cs 保存は tcs --watch が再変換し、既存の entry mtime
// poll (app.c) が hotswap する。lub 側は子プロセスの lifecycle だけ持つ。
#include "tcs_build.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <string.h>

// haxe_build.c の path helpers を借りる
#include "haxe_build.h"

static bool file_exists(const char *path) {
  SDL_PathInfo info;
  return SDL_GetPathInfo(path, &info);
}

// tcs の起動コマンドを解決する。優先順:
//   1. LUB_TCS 環境変数 (space 区切りの command prefix。quote は未対応)
//   2. <cwd>/third_party/tcs      -> dotnet run --project ... --
//   3. <exe>/../third_party/tcs   -> 同上
// 返り値: argv に詰めた個数 (0 = 解決失敗)。storage には strdup 相当で確保。
static int resolve_tcs_cmd(char storage[][512], int max_args,
                           const char **argv) {
  const char *env = SDL_getenv("LUB_TCS");
  if (env && env[0]) {
    int n = 0;
    char buf[1024];
    SDL_strlcpy(buf, env, sizeof(buf));
    for (char *tok = strtok(buf, " "); tok && n < max_args - 1;
         tok = strtok(NULL, " ")) {
      SDL_strlcpy(storage[n], tok, 512);
      argv[n] = storage[n];
      n++;
    }
    return n;
  }
  const char *roots[2] = {".", NULL};
  char exe_root[768] = "";
  const char *base_path = SDL_GetBasePath();
  if (base_path) {
    SDL_strlcpy(exe_root, base_path, sizeof(exe_root));
    size_t n = SDL_strlen(exe_root);
    while (n > 0 && (exe_root[n - 1] == '/' || exe_root[n - 1] == '\\'))
      exe_root[--n] = '\0';
    char *cut = SDL_strrchr(exe_root, '/');
    if (cut && cut > exe_root)
      *cut = '\0';
    roots[1] = exe_root;
  }
  for (int i = 0; i < 2; i++) {
    if (!roots[i])
      continue;
    char proj[768];
    SDL_snprintf(proj, sizeof(proj),
                 "%s/third_party/tcs/Transpiler/Transpiler.csproj", roots[i]);
    if (!file_exists(proj))
      continue;
    SDL_strlcpy(storage[0], "dotnet", 512);
    SDL_strlcpy(storage[1], "run", 512);
    SDL_strlcpy(storage[2], "--project", 512);
    SDL_strlcpy(storage[3], proj, 512);
    SDL_strlcpy(storage[4], "--", 512);
    for (int k = 0; k < 5; k++)
      argv[k] = storage[k];
    return 5;
  }
  return 0;
}

// cs-lib/ ディレクトリを cwd / exe root から探す。
static bool resolve_cs_lib(char *out, size_t outsz) {
  const char *cands[2] = {"cs-lib", NULL};
  char exe_dir[900] = "";
  const char *base_path = SDL_GetBasePath();
  if (base_path) {
    char root[768];
    SDL_strlcpy(root, base_path, sizeof(root));
    size_t n = SDL_strlen(root);
    while (n > 0 && (root[n - 1] == '/' || root[n - 1] == '\\'))
      root[--n] = '\0';
    char *cut = SDL_strrchr(root, '/');
    if (cut && cut > root)
      *cut = '\0';
    SDL_snprintf(exe_dir, sizeof(exe_dir), "%s/cs-lib", root);
    cands[1] = exe_dir;
  }
  for (int i = 0; i < 2; i++) {
    SDL_PathInfo info;
    if (cands[i] && SDL_GetPathInfo(cands[i], &info) &&
        info.type == SDL_PATHTYPE_DIRECTORY) {
      SDL_strlcpy(out, cands[i], outsz);
      return true;
    }
  }
  return false;
}

static bool str_ends_with(const char *s, const char *suffix) {
  size_t ls = SDL_strlen(s), lf = SDL_strlen(suffix);
  return ls >= lf && SDL_strcmp(s + ls - lf, suffix) == 0;
}

bool tcs_pipeline_start(TcsPipeline *p, const char *cs_path, char *out_lua,
                        size_t out_lua_sz) {
  if (!p || !cs_path)
    return false;
  SDL_zerop(p);

  // entry class = csproj basename、入力 = 同ディレクトリの全 *.cs
  // (SDK-style csproj の implicit glob と同じ範囲)。csproj は MSBuild として
  // 評価しない (IDE の型チェック・補完用の実ファイルで、lub は名前しか
  // 読まない)。
  char base[256];
  path_basename_noext(cs_path, base, sizeof(base));
  char dir[512];
  path_dirname(cs_path, dir, sizeof(dir));
  char lub_dir[640];
  SDL_snprintf(lub_dir, sizeof(lub_dir), "%s/.lub", dir);
  SDL_CreateDirectory(lub_dir);
  SDL_snprintf(out_lua, out_lua_sz, "%s/%s.lua", lub_dir, base);

  char storage[16][512];
  const char *argv[256];
  int n = resolve_tcs_cmd(storage, 16, argv);
  if (n == 0) {
    SDL_Log("tcs not found: set LUB_TCS or init third_party/tcs "
            "(git submodule update --init third_party/tcs)");
    return false;
  }

  char cs_lib[900];
  bool has_cs_lib = resolve_cs_lib(cs_lib, sizeof(cs_lib));
  char stub[960] = "";
  bool has_stub = false;
  if (has_cs_lib) {
    SDL_snprintf(stub, sizeof(stub), "%s/lub_stub.cs", cs_lib);
    has_stub = file_exists(stub);
  }
  if (!has_stub)
    SDL_Log("cs-lib/lub_stub.cs not found; compiling without lub API stub");

  int glob_count = 0;
  char **globbed = SDL_GlobDirectory(dir, "*.cs", 0, &glob_count);
  int inputs = 0;
  if (globbed && glob_count > 0) {
    for (int i = 0; i < glob_count; i++) {
      if (n >= (int)(sizeof(argv) / sizeof(argv[0])) - 16) {
        SDL_Log("tcs argv full: dropped %d sample source(s)", glob_count - i);
        break;
      }
      char full[900];
      SDL_snprintf(full, sizeof(full), "%s/%s", dir, globbed[i]);
      argv[n] = SDL_strdup(full); // process 終了まで生存でよい (leak 許容)
      n++;
      inputs++;
    }
    SDL_free(globbed);
  }
  if (inputs == 0) {
    SDL_Log("no .cs sources next to %s", cs_path);
    return false;
  }

  // cs-lib 実装ソース (lub_stub.cs 以外の全 *.cs) を一律追加する。
  // stub は宣言のみ (--ref) だが、実装モジュールは transpile 対象。
  // input に入れることで tcs --watch の監視対象にもなる (hot reload)。
  if (has_cs_lib) {
    int lib_count = 0;
    char **lib = SDL_GlobDirectory(cs_lib, NULL, 0, &lib_count);
    if (lib) {
      for (int i = 0; i < lib_count; i++) {
        if (n >= (int)(sizeof(argv) / sizeof(argv[0])) - 16) {
          SDL_Log("tcs argv full: dropped remaining cs-lib sources");
          break;
        }
        if (!str_ends_with(lib[i], ".cs"))
          continue;
        const char *base = SDL_strrchr(lib[i], '/');
        base = base ? base + 1 : lib[i];
        if (SDL_strcmp(base, "lub_stub.cs") == 0)
          continue;
        char full[1200];
        SDL_snprintf(full, sizeof(full), "%s/%s", cs_lib, lib[i]);
        argv[n] = SDL_strdup(full); // process 終了まで生存でよい (leak 許容)
        n++;
      }
      SDL_free(lib);
    }
  }

  if (has_stub) {
    argv[n++] = "--ref";
    argv[n++] = stub;
  }
  argv[n++] = "-o";
  argv[n++] = out_lua;
  argv[n++] = "--entry";
  argv[n++] = base;
  argv[n++] = "--no-naming-check";
  argv[n++] = "--watch";
  argv[n] = NULL;

  SDL_PropertiesID props = SDL_CreateProperties();
  SDL_SetPointerProperty(props, SDL_PROP_PROCESS_CREATE_ARGS_POINTER,
                         (void *)argv);
  // stdout/stderr は継承 (transpile エラーが端末に出るように)
  p->proc = SDL_CreateProcessWithProperties(props);
  SDL_DestroyProperties(props);
  if (!p->proc) {
    SDL_Log("tcs spawn failed: %s", SDL_GetError());
    return false;
  }

  // 初回 transpile を待つ (dotnet run の cold start 込みで最大 120s)。
  // 既存出力がある場合も mtime 更新を待つ (stale をロードしない)。
  int64_t before = 0;
  SDL_PathInfo info;
  if (SDL_GetPathInfo(out_lua, &info))
    before = (int64_t)info.modify_time;
  SDL_Log("tcs: transpiling %s ...", cs_path);
  for (int waited = 0; waited < 120000; waited += 100) {
    if (SDL_GetPathInfo(out_lua, &info) && (int64_t)info.modify_time != before)
      break;
    int exit_code = 0;
    if (SDL_WaitProcess(p->proc, false, &exit_code)) {
      SDL_Log("tcs exited before producing output (exit=%d)", exit_code);
      SDL_DestroyProcess(p->proc);
      p->proc = NULL;
      return false;
    }
    SDL_Delay(100);
  }
  if (!SDL_GetPathInfo(out_lua, &info) || (int64_t)info.modify_time == before) {
    SDL_Log("tcs: timeout waiting for %s", out_lua);
    tcs_pipeline_stop(p);
    return false;
  }
  SDL_Log("tcs: %s ready (watching)", out_lua);
  p->enabled = true;
  return true;
}

void tcs_pipeline_stop(TcsPipeline *p) {
  if (!p || !p->proc)
    return;
  SDL_KillProcess(p->proc, false);
  SDL_DestroyProcess(p->proc);
  p->proc = NULL;
  p->enabled = false;
}
