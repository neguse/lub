// tcs (TinyC#) pipeline: .csproj entry の transpile + watch を lub が駆動する。
// 起動時に tcs を --watch で spawn し、初回出力 (.lub/<Base>.lua) を待って
// entry にする。以後の .cs 保存は tcs --watch が再変換し、実行中の VM へ当てる
// reload chunk を標準出力へ順に書く。lub 側は子プロセスの lifecycle と、その
// stream からの chunk の切り出しを持つ (当てるのは app.c)。
#include "tcs_build.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <string.h>

#include "path_util.h"

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

typedef struct SourceList {
  char **v;
  int n, cap;
} SourceList;

typedef struct SourceWalk {
  SourceList *list;
  const char *abs; // 走査中ディレクトリ (絶対 / cwd 基準)
  const char *rel; // dir からの相対 ("" = dir 自身)
  int depth;
} SourceWalk;

static void walk_sources(SourceList *list, const char *abs, const char *rel,
                         int depth);

static SDL_EnumerationResult source_entry(void *userdata, const char *dirname,
                                          const char *fname) {
  (void)dirname;
  SourceWalk *w = (SourceWalk *)userdata;
  char abs[1024], rel[1024];
  SDL_snprintf(abs, sizeof(abs), "%s/%s", w->abs, fname);
  if (w->rel[0])
    SDL_snprintf(rel, sizeof(rel), "%s/%s", w->rel, fname);
  else
    SDL_strlcpy(rel, fname, sizeof(rel));
  SDL_PathInfo info;
  if (!SDL_GetPathInfo(abs, &info))
    return SDL_ENUM_CONTINUE;
  if (info.type == SDL_PATHTYPE_DIRECTORY) {
    // dotnet の DefaultItemExcludes (bin / obj / 隠しディレクトリ)。bin / obj
    // は入れ子の project の生成物 (obj/*.AssemblyInfo.cs) を拾わないよう
    // 深さを問わず除く
    if (fname[0] != '.' && SDL_strcmp(fname, "bin") != 0 &&
        SDL_strcmp(fname, "obj") != 0)
      walk_sources(w->list, abs, rel, w->depth + 1);
  } else if (info.type == SDL_PATHTYPE_FILE && str_ends_with(fname, ".cs")) {
    if (w->list->n == w->list->cap) {
      int cap = w->list->cap ? w->list->cap * 2 : 16;
      char **grown = (char **)SDL_realloc(w->list->v, cap * sizeof(char *));
      if (!grown)
        return SDL_ENUM_FAILURE;
      w->list->v = grown;
      w->list->cap = cap;
    }
    w->list->v[w->list->n++] = SDL_strdup(rel);
  }
  return SDL_ENUM_CONTINUE;
}

static void walk_sources(SourceList *list, const char *abs, const char *rel,
                         int depth) {
  if (depth > 32) // symlink の循環で止まらなくなるのを避ける
    return;
  SourceWalk w = {list, abs, rel, depth};
  SDL_EnumerateDirectory(abs, source_entry, &w);
}

static int cmp_str(const void *a, const void *b) {
  return SDL_strcmp(*(char *const *)a, *(char *const *)b);
}

char **tcs_glob_sources(const char *dir, int *count) {
  SourceList list = {0};
  walk_sources(&list, dir, "", 0);
  if (list.n > 1)
    SDL_qsort(list.v, (size_t)list.n, sizeof(char *), cmp_str);
  *count = list.n;
  return list.v;
}

void tcs_free_sources(char **sources, int count) {
  for (int i = 0; i < count; i++)
    SDL_free(sources[i]);
  SDL_free(sources);
}

bool tcs_pipeline_start(TcsPipeline *p, const char *cs_path, char *out_lua,
                        size_t out_lua_sz) {
  if (!p || !cs_path)
    return false;
  SDL_zerop(p);

  // entry class = csproj basename、入力 = 同ディレクトリ以下の全 *.cs
  // (SDK-style csproj の implicit glob に倣う。tcs_glob_sources)。csproj は
  // MSBuild として評価しない (IDE の型チェック・補完用の実ファイルで、lub は
  // 名前しか読まない)。
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
  char **globbed = tcs_glob_sources(dir, &glob_count);
  int inputs = 0;
  for (int i = 0; i < glob_count; i++) {
    if (n >= (int)(sizeof(argv) / sizeof(argv[0])) - 16) {
      SDL_Log("tcs argv full: dropped %d sample source(s)", glob_count - i);
      break;
    }
    char full[1400];
    SDL_snprintf(full, sizeof(full), "%s/%s", dir, globbed[i]);
    argv[n] = SDL_strdup(full); // process 終了まで生存でよい (leak 許容)
    n++;
    inputs++;
  }
  tcs_free_sources(globbed, glob_count);
  if (inputs == 0) {
    SDL_Log("no .cs sources under %s", dir);
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
  argv[n++] = "--watch";
  argv[n++] = "--reload-chunks";
  argv[n] = NULL;

  SDL_PropertiesID props = SDL_CreateProperties();
  SDL_SetPointerProperty(props, SDL_PROP_PROCESS_CREATE_ARGS_POINTER,
                         (void *)argv);
  // stdout は reload chunk の stream として受ける。stderr (tcs のログと
  // transpile エラー) は端末に出るよう継承する
  SDL_SetNumberProperty(props, SDL_PROP_PROCESS_CREATE_STDOUT_NUMBER,
                        SDL_PROCESS_STDIO_APP);
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

// stream の形式: `@@tcs_reload_chunk <本文の byte 数>\n` の行と本文の組の列
static const char RELOAD_CHUNK_HEADER[] = "@@tcs_reload_chunk ";

bool tcs_pipeline_next_chunk(TcsPipeline *p, char **out, size_t *out_len) {
  if (!p || !p->proc)
    return false;
  SDL_IOStream *io = (SDL_IOStream *)SDL_GetPointerProperty(
      SDL_GetProcessProperties(p->proc), SDL_PROP_PROCESS_STDOUT_POINTER, NULL);
  char tmp[16384];
  size_t got;
  while (io && (got = SDL_ReadIO(io, tmp, sizeof(tmp))) > 0) {
    if (p->len + got > p->cap) {
      size_t cap = p->cap ? p->cap : sizeof(tmp);
      while (cap < p->len + got)
        cap *= 2;
      char *grown = (char *)SDL_realloc(p->buf, cap);
      if (!grown) {
        SDL_Log("tcs: reload chunk stream dropped: out of memory");
        return false;
      }
      p->buf = grown;
      p->cap = cap;
    }
    memcpy(p->buf + p->len, tmp, got);
    p->len += got;
  }

  while (p->len > 0) {
    char *nl = (char *)memchr(p->buf, '\n', p->len);
    if (!nl)
      return false;
    size_t head = (size_t)(nl - p->buf) + 1;
    size_t header_len = sizeof(RELOAD_CHUNK_HEADER) - 1;
    size_t body = 0;
    bool ok = head > header_len &&
              memcmp(p->buf, RELOAD_CHUNK_HEADER, header_len) == 0;
    for (size_t i = header_len; ok && i < head - 1; i++) {
      if (p->buf[i] < '0' || p->buf[i] > '9')
        ok = false;
      else
        body = body * 10 + (size_t)(p->buf[i] - '0');
    }
    if (!ok) {
      // chunk の見出しでない行 (想定外の出力) は捨てて先へ進む
      SDL_Log("tcs: ignoring stdout line: %.*s", (int)(head - 1), p->buf);
      memmove(p->buf, p->buf + head, p->len - head);
      p->len -= head;
      continue;
    }
    if (p->len < head + body)
      return false;
    char *chunk = (char *)SDL_malloc(body + 1);
    if (!chunk)
      return false;
    memcpy(chunk, p->buf + head, body);
    chunk[body] = '\0';
    memmove(p->buf, p->buf + head + body, p->len - head - body);
    p->len -= head + body;
    *out = chunk;
    *out_len = body;
    return true;
  }
  return false;
}

void tcs_pipeline_stop(TcsPipeline *p) {
  if (!p || !p->proc)
    return;
  SDL_KillProcess(p->proc, false);
  SDL_DestroyProcess(p->proc);
  p->proc = NULL;
  p->enabled = false;
  SDL_free(p->buf);
  p->buf = NULL;
  p->len = p->cap = 0;
}
