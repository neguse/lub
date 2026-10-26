// tcs (TinyC#) pipeline: .csproj entry の transpile + watch を lub が駆動する。
// 起動時に tcs を --watch で spawn し、初回出力 (.lub/<Base>.lua) を待って entry にする。
// 以後の .cs 保存は tcs --watch が再変換し、実行中の VM へ当てる reload chunk (または VM の作り直しを求める restart_required) を標準出力へ順に書く。
// lub 側は子プロセスの lifecycle と、その stream からの frame の切り出しを持つ (当てるのは main.c / app.c、serve は SSE で送る)。
#include "tcs_build.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <string.h>

#include "path_util.h"

static bool file_exists(const char *path) {
  SDL_PathInfo info;
  return SDL_GetPathInfo(path, &info);
}

// tcs の起動コマンドを解決する。
// 優先順:
//   1. LUB_TCS 環境変数 (space 区切りの command prefix。quote は未対応)
//   2. <exe>/tcs/Transpiler       -> NativeAOT の tcs (release の一式)
//   3. <cwd>/third_party/tcs      -> dotnet run --project ... --
//   4. <exe>/../third_party/tcs   -> 同上
// 返り値: argv に詰めた個数 (0 = 解決失敗)。
// storage には strdup 相当で確保。
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
#ifdef _WIN32
    SDL_snprintf(storage[0], 512, "%stcs\\Transpiler.exe", base_path);
#else
    SDL_snprintf(storage[0], 512, "%stcs/Transpiler", base_path);
#endif
    if (file_exists(storage[0])) {
      argv[0] = storage[0];
      return 1;
    }
    path_parent_dir(base_path, exe_root, sizeof(exe_root));
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

bool tcs_resolve_cs_lib(char *out, size_t outsz) {
  const char *env = SDL_getenv("LUB_CS_LIB");
  if (env && env[0]) {
    SDL_PathInfo info;
    if (SDL_GetPathInfo(env, &info) && info.type == SDL_PATHTYPE_DIRECTORY) {
      SDL_strlcpy(out, env, outsz);
      return true;
    }
    SDL_Log("LUB_CS_LIB is not a directory: %s", env);
    return false;
  }
  const char *cands[2] = {"cs-lib", NULL};
  char exe_dir[900] = "";
  const char *base_path = SDL_GetBasePath();
  if (base_path) {
    char root[768];
    path_parent_dir(base_path, root, sizeof(root));
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
    // dotnet の DefaultItemExcludes (bin / obj / 隠しディレクトリ)。
    // bin / obj は入れ子の project の生成物 (obj/*.AssemblyInfo.cs) を拾わないよう深さを問わず除く
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

// 区切りを '/' に揃え、"." と ".." の段を畳む。
// 先頭の ".." は畳めないので残す。
static void path_normalize(const char *in, char *out, size_t outsz) {
  char buf[1024];
  SDL_strlcpy(buf, in, sizeof(buf));
  for (char *c = buf; *c; c++)
    if (*c == '\\')
      *c = '/';
  bool absolute = buf[0] == '/';
  const char *seg[128];
  int n = 0;
  char *save = NULL;
  for (char *tok = SDL_strtok_r(buf, "/", &save); tok;
       tok = SDL_strtok_r(NULL, "/", &save)) {
    if (SDL_strcmp(tok, ".") == 0)
      continue;
    if (SDL_strcmp(tok, "..") == 0 && n > 0 && SDL_strcmp(seg[n - 1], "..")) {
      n--;
      continue;
    }
    if (n < 128)
      seg[n++] = tok;
  }
  size_t len = 0;
  out[0] = '\0';
  if (absolute)
    len = SDL_strlcpy(out, "/", outsz);
  for (int i = 0; i < n && len < outsz; i++)
    len += SDL_snprintf(out + len, outsz - len, "%s%s", i ? "/" : "", seg[i]);
  if (n == 0 && !absolute)
    SDL_strlcpy(out, ".", outsz);
}

static bool path_is_absolute(const char *p) {
  return p[0] == '/' || p[0] == '\\' || (p[0] && p[1] == ':');
}

// 相対 path は cwd に繋いでから畳む (同じ file を書き方によらず比べるため)
static void path_absolute(const char *in, char *out, size_t outsz) {
  char joined[1400];
  char *cwd = path_is_absolute(in) ? NULL : SDL_GetCurrentDirectory();
  SDL_snprintf(joined, sizeof(joined), "%s%s", cwd ? cwd : "", in);
  SDL_free(cwd);
  path_normalize(joined, out, outsz);
}

bool tcs_same_path(const char *a, const char *b) {
  char fa[1024], fb[1024];
  path_absolute(a, fa, sizeof(fa));
  path_absolute(b, fb, sizeof(fb));
  return SDL_strcmp(fa, fb) == 0;
}

// tag (要素名の直後から '>' の手前まで) から属性 name の値を out に書く。
static bool xml_attr(const char *tag, size_t len, const char *name, char *out,
                     size_t outsz) {
  size_t name_len = SDL_strlen(name);
  for (size_t i = 1; i + name_len < len; i++) {
    char before = tag[i - 1];
    if ((before != ' ' && before != '\t' && before != '\r' && before != '\n') ||
        SDL_strncmp(tag + i, name, name_len) != 0)
      continue;
    size_t j = i + name_len;
    while (j < len && (tag[j] == ' ' || tag[j] == '\t'))
      j++;
    if (j >= len || tag[j] != '=')
      continue;
    j++;
    while (j < len && (tag[j] == ' ' || tag[j] == '\t'))
      j++;
    if (j >= len || (tag[j] != '"' && tag[j] != '\''))
      continue;
    char quote = tag[j++];
    size_t start = j;
    while (j < len && tag[j] != quote)
      j++;
    if (j >= len || j - start >= outsz)
      return false;
    SDL_memcpy(out, tag + start, j - start);
    out[j - start] = '\0';
    return true;
  }
  return false;
}

// via_tool は、csproj を overlay の参照先から辿ってきたか
static void collect_refs(const char *csproj, const char *root,
                         TcsProjectRef *out, int max, int *n, int depth,
                         bool via_tool, bool *missing) {
  if (depth > 16) // 参照の循環で止まらなくなるのを避ける
    return;
  size_t len = 0;
  char *text = (char *)SDL_LoadFile(csproj, &len);
  if (!text)
    return;
  char dir[512];
  path_dirname(csproj, dir, sizeof(dir));
  static const char TAG[] = "<ProjectReference";
  const char *p = text;
  while ((p = SDL_strstr(p, TAG)) != NULL) {
    // comment (<!-- ... -->) の中の要素は読まない
    const char *comment = SDL_strstr(text, "<!--");
    bool commented = false;
    while (comment && comment < p) {
      const char *end = SDL_strstr(comment + 4, "-->");
      if (!end || end > p) {
        commented = true;
        p = end ? end + 3 : text + len;
        break;
      }
      comment = SDL_strstr(end + 3, "<!--");
    }
    if (commented)
      continue;
    p += sizeof(TAG) - 1;
    const char *close = SDL_strchr(p, '>');
    if (!close)
      break;
    size_t tag_len = (size_t)(close - p);
    char role[32], include[512];
    bool source = false, overlay = false;
    if (xml_attr(p, tag_len, "Lub", role, sizeof(role))) {
      source = SDL_strcasecmp(role, "Source") == 0;
      overlay = SDL_strcasecmp(role, "Overlay") == 0;
      if (!source && !overlay)
        SDL_Log("%s: unknown Lub=\"%s\" on a ProjectReference (use Source or "
                "Overlay)",
                csproj, role);
    }
    if ((source || overlay) &&
        xml_attr(p, tag_len, "Include", include, sizeof(include))) {
      if (SDL_strstr(include, "$(")) {
        SDL_Log("%s: skipping ProjectReference %s (lub does not expand "
                "MSBuild properties)",
                csproj, include);
      } else {
        char joined[1100], norm[512];
        if (path_is_absolute(include))
          SDL_strlcpy(joined, include, sizeof(joined));
        else
          SDL_snprintf(joined, sizeof(joined), "%s/%s", dir, include);
        path_normalize(joined, norm, sizeof(norm));
        bool tool = via_tool || overlay;
        bool seen = tcs_same_path(root, norm);
        TcsProjectRef *prev = NULL;
        for (int i = 0; i < *n && !seen && !prev; i++)
          if (tcs_same_path(out[i].path, norm))
            prev = &out[i];
        if (prev) {
          prev->overlay = prev->overlay || overlay;
          // 先に overlay の側から辿っていても、ゲームから辿れるならその参照ごと配る物に入る
          if (prev->tool && !tool) {
            prev->tool = false;
            collect_refs(norm, root, out, max, n, depth + 1, false, missing);
          }
        } else if (!seen && !file_exists(norm)) {
          SDL_Log("%s: ProjectReference %s does not exist (%s)", csproj,
                  include, norm);
          *missing = true;
        } else if (!seen && *n < max) {
          SDL_strlcpy(out[*n].path, norm, sizeof(out[*n].path));
          out[*n].overlay = overlay;
          out[*n].tool = tool;
          (*n)++;
          collect_refs(norm, root, out, max, n, depth + 1, tool, missing);
        }
      }
    }
    p = close;
  }
  SDL_free(text);
}

int tcs_project_refs(const char *csproj, TcsProjectRef *out, int max) {
  int n = 0;
  bool missing = false;
  collect_refs(csproj, csproj, out, max, &n, 0, false, &missing);
  return missing ? -1 : n;
}

#define TCS_MAX_ARGS 1024
#define TCS_MAX_REFS 32

// argv に source の path を足す。
// 同じ file (path を畳んで比べる) は 1 回だけ入れる。
static bool add_source(const char **argv, int *n, const char *path) {
  char norm[1024];
  path_normalize(path, norm, sizeof(norm));
  for (int i = 0; i < *n; i++)
    if (tcs_same_path(argv[i], norm))
      return false;
  if (*n >= TCS_MAX_ARGS - 16) {
    SDL_Log("tcs argv full: dropped %s", norm);
    return false;
  }
  argv[(*n)++] = SDL_strdup(norm); // process 終了まで生存でよい (leak 許容)
  return true;
}

#define TCS_MAX_REF_SOURCES 64

// *.ref.cs は型だけの stub (Lua の module や native の module が置く global の型) で、変換せずに --ref で渡す
static char *g_ref_sources[TCS_MAX_REF_SOURCES];
static int g_ref_source_count;

static void add_ref_source(const char *path) {
  char norm[1024];
  path_normalize(path, norm, sizeof(norm));
  for (int i = 0; i < g_ref_source_count; i++)
    if (tcs_same_path(g_ref_sources[i], norm))
      return;
  if (g_ref_source_count >= TCS_MAX_REF_SOURCES) {
    SDL_Log("too many *.ref.cs: dropped %s", norm);
    return;
  }
  g_ref_sources[g_ref_source_count++] = SDL_strdup(norm);
}

static void add_lua_dir(TcsPipeline *p, const char *dir) {
  for (int i = 0; i < p->lua_dir_count; i++)
    if (tcs_same_path(p->lua_dirs[i], dir))
      return;
  if (p->lua_dir_count < TCS_MAX_LUA_DIRS)
    SDL_strlcpy(p->lua_dirs[p->lua_dir_count++], dir, sizeof(p->lua_dirs[0]));
}

// dir 以下の全 *.cs を足し (入っているものは足さない)、見つけた個数を返す。
// *.ref.cs は入力にせず --ref に回し、dir は Lua の module を探す場所として覚える
static int add_dir_sources(TcsPipeline *p, const char **argv, int *n,
                           const char *dir) {
  int count = 0;
  char **globbed = tcs_glob_sources(dir, &count);
  for (int i = 0; i < count; i++) {
    char full[1400];
    SDL_snprintf(full, sizeof(full), "%s/%s", dir, globbed[i]);
    if (str_ends_with(globbed[i], ".ref.cs"))
      add_ref_source(full);
    else
      add_source(argv, n, full);
  }
  tcs_free_sources(globbed, count);
  add_lua_dir(p, dir);
  return count;
}

bool tcs_pipeline_start(TcsPipeline *p, const char *cs_path, char *out_lua,
                        size_t out_lua_sz, bool with_overlay) {
  if (!p || !cs_path)
    return false;
  SDL_zerop(p);

  // entry class = csproj basename、入力 = 同ディレクトリ以下の全 *.cs (SDK-style csproj の implicit glob に倣う。tcs_glob_sources)。
  // csproj は MSBuild として評価しない (IDE の型チェック・補完用の実ファイル)。
  // lub が読むのは名前と、Lub="Source" の印を付けた ProjectReference だけ (参照先の dir 以下の全 *.cs も入力にする)。
  char base[256];
  path_basename_noext(cs_path, base, sizeof(base));
  char dir[512];
  path_dirname(cs_path, dir, sizeof(dir));
  char lub_dir[640];
  SDL_snprintf(lub_dir, sizeof(lub_dir), "%s/.lub", dir);
  SDL_CreateDirectory(lub_dir);
  SDL_snprintf(out_lua, out_lua_sz, "%s/%s.lua", lub_dir, base);

  char storage[16][512];
  const char *argv[TCS_MAX_ARGS];
  int n = resolve_tcs_cmd(storage, 16, argv);
  if (n == 0) {
    SDL_Log("tcs not found: set LUB_TCS or init third_party/tcs "
            "(git submodule update --init third_party/tcs)");
    return false;
  }

  char cs_lib[900];
  bool has_cs_lib = tcs_resolve_cs_lib(cs_lib, sizeof(cs_lib));
  char stub[960] = "";
  bool has_stub = false;
  if (has_cs_lib) {
    SDL_snprintf(stub, sizeof(stub), "%s/lub_stub.cs", cs_lib);
    has_stub = file_exists(stub);
  }
  if (!has_stub)
    SDL_Log("cs-lib/lub_stub.cs not found (set LUB_CS_LIB to the cs-lib "
            "directory); compiling without lub API stub");

  // ゲームのソース (entry と Lub="Source" の参照先) の argv の位置
  static bool game_source[TCS_MAX_ARGS];
  memset(game_source, 0, sizeof(game_source));
  int first = n;
  if (add_dir_sources(p, argv, &n, dir) == 0) {
    SDL_Log("no .cs sources under %s", dir);
    return false;
  }
  for (int i = first; i < n; i++)
    game_source[i] = true;
  TcsProjectRef refs[TCS_MAX_REFS];
  int ref_count = tcs_project_refs(cs_path, refs, TCS_MAX_REFS);
  if (ref_count < 0)
    return false;
  for (int i = 0; i < ref_count; i++) {
    if (refs[i].tool && !with_overlay)
      continue;
    char ref_dir[512];
    path_dirname(refs[i].path, ref_dir, sizeof(ref_dir));
    first = n;
    if (add_dir_sources(p, argv, &n, ref_dir) == 0)
      SDL_Log("%s: no .cs sources under %s", cs_path, ref_dir);
    if (!refs[i].tool)
      for (int k = first; k < n; k++)
        game_source[k] = true;
    if (!refs[i].overlay || !with_overlay)
      continue;
    if (p->overlay[0])
      SDL_Log("%s: only one overlay is used, ignoring %s", cs_path,
              refs[i].path);
    else
      path_basename_noext(refs[i].path, p->overlay, sizeof(p->overlay));
  }
  // overlay があるときは、ゲームのソースの実行した行を記録させる (Sys.TakeTrace)。
  // tcs の --trace は path の前置きで選ぶので、file ごとに自分の path を渡す (entry が cwd の直下でも他と混ざらない)
  if (p->overlay[0]) {
    int sources_end = n;
    for (int i = 0; i < sources_end; i++) {
      if (!game_source[i])
        continue;
      if (n >= TCS_MAX_ARGS - 16) {
        SDL_Log("tcs argv full: %s and later sources are not traced", argv[i]);
        break;
      }
      argv[n++] = "--trace";
      argv[n++] = argv[i];
    }
  }

  // cs-lib 実装ソース (lub_stub.cs 以外の全 *.cs) を一律追加する。
  // stub は宣言のみ (--ref) だが、実装モジュールは transpile 対象。
  // input に入れることで tcs --watch の監視対象にもなる (hot reload)。
  if (has_cs_lib) {
    int lib_count = 0;
    char **lib = SDL_GlobDirectory(cs_lib, NULL, 0, &lib_count);
    if (lib) {
      for (int i = 0; i < lib_count; i++) {
        if (!str_ends_with(lib[i], ".cs"))
          continue;
        const char *base = SDL_strrchr(lib[i], '/');
        base = base ? base + 1 : lib[i];
        if (SDL_strcmp(base, "lub_stub.cs") == 0)
          continue;
        char full[1200];
        SDL_snprintf(full, sizeof(full), "%s/%s", cs_lib, lib[i]);
        add_source(argv, &n, full);
      }
      SDL_free(lib);
    }
  }

  if (has_stub) {
    argv[n++] = "--ref";
    argv[n++] = stub;
  }
  for (int i = 0; i < g_ref_source_count && n < TCS_MAX_ARGS - 10; i++) {
    argv[n++] = "--ref";
    argv[n++] = g_ref_sources[i];
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
  // stdout は reload chunk の stream として受ける。
  // stderr (tcs のログと transpile エラー) は端末に出るよう継承する
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

// stream の形式: `@@tcs_<kind> <本文の byte 数>\n` の行と本文の組の列
static const char FRAME_PREFIX[] = "@@tcs_";

// buf[0..line) (改行を除く) が frame の見出しなら kind と本文の byte 数を返す
static bool parse_frame_header(const char *buf, size_t line, const char **kind,
                               size_t *kind_len, size_t *body) {
  size_t prefix = sizeof(FRAME_PREFIX) - 1;
  if (line <= prefix || memcmp(buf, FRAME_PREFIX, prefix) != 0)
    return false;
  const char *space = (const char *)memchr(buf + prefix, ' ', line - prefix);
  if (!space || space == buf + prefix)
    return false;
  const char *digits = space + 1, *end = buf + line;
  if (digits == end || end - digits > 12)
    return false;
  *body = 0;
  for (const char *c = digits; c < end; c++) {
    if (*c < '0' || *c > '9')
      return false;
    *body = *body * 10 + (size_t)(*c - '0');
  }
  *kind = buf + prefix;
  *kind_len = (size_t)(space - *kind);
  return true;
}

static bool kind_is(const char *kind, size_t kind_len, const char *name) {
  return kind_len == SDL_strlen(name) && memcmp(kind, name, kind_len) == 0;
}

bool tcs_pipeline_next_frame(TcsPipeline *p, TcsFrameKind *kind, char **out,
                             size_t *out_len) {
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
    const char *name;
    size_t name_len, body;
    if (!parse_frame_header(p->buf, head - 1, &name, &name_len, &body)) {
      // frame の見出しでない行 (想定外の出力) は捨てて先へ進む
      SDL_Log("tcs: ignoring stdout line: %.*s", (int)(head - 1), p->buf);
      memmove(p->buf, p->buf + head, p->len - head);
      p->len -= head;
      continue;
    }
    if (p->len < head + body)
      return false;
    bool known = true;
    if (kind_is(name, name_len, "reload_chunk"))
      *kind = TCS_FRAME_RELOAD_CHUNK;
    else if (kind_is(name, name_len, "restart_required"))
      *kind = TCS_FRAME_RESTART_REQUIRED;
    else if (kind_is(name, name_len, "build_failed"))
      *kind = TCS_FRAME_BUILD_FAILED;
    else
      known = false;
    if (!known) {
      // 新しい tcs が足した種類。
      // 本文ごと読み飛ばす
      SDL_Log("tcs: skipping unknown frame %.*s (%zu bytes)", (int)name_len,
              name, body);
      memmove(p->buf, p->buf + head + body, p->len - head - body);
      p->len -= head + body;
      continue;
    }
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
