// vm_project (csproj の Lub="Source" の ProjectReference と入力の一覧) の smoke。
// path の正規化と XML の属性の読み取りは static なので、vm_project.c をそのまま取り込んで呼ぶ。
#include "../../src/vm_project.c"
#include <stdlib.h>
#include <string.h>

static int failures;

static void expect_str(const char *what, const char *got, const char *want) {
  if (SDL_strcmp(got, want) != 0) {
    SDL_Log("FAIL: %s: got \"%s\", want \"%s\"", what, got, want);
    failures++;
  }
}

static int cmp_desc(const void *a, const void *b) {
  return strcmp(*(char *const *)b, *(char *const *)a);
}

// SDL に再帰削除は無い。
// 降順に並べると子が親より先に来る
static void remove_tree(const char *root) {
  int n = 0;
  char **all = SDL_GlobDirectory(root, NULL, 0, &n);
  if (all) {
    qsort(all, (size_t)n, sizeof(all[0]), cmp_desc);
    for (int i = 0; i < n; i++) {
      char path[1024];
      SDL_snprintf(path, sizeof(path), "%s/%s", root, all[i]);
      SDL_RemovePath(path);
    }
    SDL_free(all);
  }
  SDL_RemovePath(root);
}

static void write_file(const char *root, const char *rel, const char *text) {
  char path[1024];
  SDL_snprintf(path, sizeof(path), "%s/%s", root, rel);
  for (char *slash = strchr(path + 1, '/'); slash;
       slash = strchr(slash + 1, '/')) {
    *slash = '\0';
    SDL_CreateDirectory(path);
    *slash = '/';
  }
  if (!SDL_SaveFile(path, text, SDL_strlen(text))) {
    SDL_Log("FAIL: cannot create %s: %s", path, SDL_GetError());
    failures++;
  }
}

static int count_lines_ending(const char *text, const char *suffix) {
  int n = 0;
  size_t ls = SDL_strlen(suffix);
  for (const char *line = text; line && *line;) {
    const char *nl = SDL_strchr(line, '\n');
    size_t len = nl ? (size_t)(nl - line) : SDL_strlen(line);
    if (len >= ls && SDL_strncmp(line + len - ls, suffix, ls) == 0)
      n++;
    line = nl ? nl + 1 : NULL;
  }
  return n;
}

static void test_normalize(void) {
  char out[512];
  path_normalize("\\\\server\\share\\x\\..\\Lib.csproj", out, sizeof(out));
  expect_str("UNC", out, "//server/share/Lib.csproj");
  path_normalize("//server/share/./Lib.csproj", out, sizeof(out));
  expect_str("UNC with /", out, "//server/share/Lib.csproj");
  path_normalize("/a//b/../c", out, sizeof(out));
  expect_str("absolute", out, "/a/c");
  path_normalize("///a", out, sizeof(out));
  expect_str("three slashes", out, "/a");
  path_normalize("a/./b/../../..", out, sizeof(out));
  expect_str("relative", out, "..");
}

static void test_xml_attr(void) {
  char out[256];
  const char *tag = " Include=\"../A&amp;B/&#x41;&#66;&lt;&unknown;.csproj\"\n"
                    "  Lub\r\n=\n'Source' ";
  size_t len = SDL_strlen(tag);
  if (!xml_attr(tag, len, "Include", out, sizeof(out)))
    out[0] = '\0';
  expect_str("Include", out, "../A&B/AB<&unknown;.csproj");
  if (!xml_attr(tag, len, "Lub", out, sizeof(out)))
    out[0] = '\0';
  expect_str("Lub", out, "Source");
  const char *utf8 = " Include=\"&#x3042;&#12354;\"";
  if (!xml_attr(utf8, SDL_strlen(utf8), "Include", out, sizeof(out)))
    out[0] = '\0';
  expect_str("UTF-8", out, "\xE3\x81\x82\xE3\x81\x82");
}

int main(void) {
  test_normalize();
  test_xml_attr();

  char root[512];
  SDL_snprintf(root, sizeof(root), "%svm_project_fixture", SDL_GetBasePath());
  remove_tree(root);
  // ゲームのディレクトリの下の project を絶対 path で参照しても、同じ file は一覧に 1 回だけ載る
  char game_csproj[1400];
  SDL_snprintf(
      game_csproj, sizeof(game_csproj),
      "<Project>\n  <ProjectReference Include=\"%s/game/lib/Lib.csproj\" "
      "Lub=\"Source\" />\n</Project>\n",
      root);
  write_file(root, "game/Game.csproj", game_csproj);
  write_file(root, "game/Game.cs", "");
  write_file(root, "game/lib/Lib.csproj", "<Project />\n");
  write_file(root, "game/lib/Lib.cs", "");
  // 文字参照を含む path と、'=' の前後の改行
  write_file(root, "other/Other.csproj",
             "<Project>\n  <ProjectReference Include=\"../A&amp;B/Lib.csproj\" "
             "Lub=\n\"Source\" />\n</Project>\n");
  write_file(root, "other/Other.cs", "");
  write_file(root, "A&B/Lib.csproj", "<Project />\n");
  write_file(root, "A&B/AB.cs", "");

  char path[1024];
  VmProject proj;
  SDL_snprintf(path, sizeof(path), "%s/game/Game.csproj", root);
  if (!vm_project_open(&proj, path)) {
    SDL_Log("FAIL: open %s", path);
    failures++;
  }
  vm_project_scan(&proj);
  if (proj.ndirs != 2) {
    SDL_Log("FAIL: game: %d dir(s), want 2", proj.ndirs);
    failures++;
  }
  int n = count_lines_ending(proj.list_text, "/game/lib/Lib.cs");
  if (n != 1) {
    SDL_Log("FAIL: game/lib/Lib.cs listed %d time(s), want 1\n%s", n,
            proj.list_text);
    failures++;
  }
  vm_project_close(&proj);

  SDL_snprintf(path, sizeof(path), "%s/other/Other.csproj", root);
  if (!vm_project_open(&proj, path)) {
    SDL_Log("FAIL: open %s", path);
    failures++;
  }
  vm_project_scan(&proj);
  if (proj.ndirs != 2 || !str_ends_with(proj.dirs[1], "/A&B")) {
    SDL_Log("FAIL: other: %d dir(s), dirs[1] \"%s\", want .../A&B", proj.ndirs,
            proj.ndirs > 1 ? proj.dirs[1] : "");
    failures++;
  }
  if (count_lines_ending(proj.list_text, "/A&B/AB.cs") != 1) {
    SDL_Log("FAIL: A&B/AB.cs is not listed once\n%s", proj.list_text);
    failures++;
  }
  vm_project_close(&proj);

  remove_tree(root);
  if (failures) {
    SDL_Log("vm_project_smoke: %d failure(s)", failures);
    return 1;
  }
  SDL_Log("PASS: vm_project_smoke");
  return 0;
}
