// tcs_glob_sources と tcs_project_refs と tcs_same_path の smoke。
// csproj ディレクトリの implicit glob (**/*.cs、bin / obj / 隠しディレクトリを除く) と、Lub="Source" の印を付けた ProjectReference の解決を、実ファイルの fixture で検査する。
#include "../../src/tcs_build.h"
#include <SDL3/SDL.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <direct.h>
#define chdir _chdir
#else
#include <unistd.h>
#endif

static int failures;

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
      char path[512];
      SDL_snprintf(path, sizeof(path), "%s/%s", root, all[i]);
      SDL_RemovePath(path);
    }
    SDL_free(all);
  }
  SDL_RemovePath(root);
}

static void write_file(const char *root, const char *rel, const char *text) {
  char path[512];
  SDL_snprintf(path, sizeof(path), "%s/%s", root, rel);
  char *slash = strrchr(path, '/');
  *slash = '\0';
  SDL_CreateDirectory(path);
  *slash = '/';
  if (!SDL_SaveFile(path, text, strlen(text))) {
    SDL_Log("FAIL: cannot create %s: %s", path, SDL_GetError());
    failures++;
  }
}

static void touch(const char *root, const char *rel) {
  write_file(root, rel, "");
}

static void check_refs(const char *root) {
  // 印の無い参照、comment の中の参照、property を含む参照、二度目の参照、読み始めた csproj への参照は入らない。
  // 参照先の参照は辿る
  write_file(
      root, "refs/game/Game.csproj",
      "<Project>\n"
      "  <!-- <ProjectReference Include=\"../commented/C.csproj\" "
      "Lub=\"Source\" /> -->\n"
      "  <ItemGroup>\n"
      "    <ProjectReference Include=\"../lib/Lib.csproj\" "
      "Lub=\"Source\" />\n"
      "    <ProjectReference Include=\"..\\plain\\Plain.csproj\" />\n"
      "    <ProjectReference Include=\"$(Root)/x/X.csproj\" "
      "Lub=\"Source\" />\n"
      "    <ProjectReference\n"
      "        Lub='source'\n"
      "        Include='./../lib/../lib2/Lib2.csproj'></ProjectReference>\n"
      "  </ItemGroup>\n"
      "</Project>\n");
  write_file(
      root, "refs/lib/Lib.csproj",
      "<Project><ItemGroup>"
      "<ProjectReference Include=\"../lib3/Lib3.csproj\" Lub=\"Source\"/>"
      "<ProjectReference Include=\"../lib2/Lib2.csproj\" Lub=\"Source\"/>"
      "<ProjectReference Include=\"../game/Game.csproj\" Lub=\"Source\"/>"
      "</ItemGroup></Project>");
  write_file(root, "refs/lib2/Lib2.csproj", "<Project />");
  write_file(root, "refs/lib3/Lib3.csproj", "<Project />");
  char game[600];
  SDL_snprintf(game, sizeof(game), "%s/refs/game/Game.csproj", root);
  TcsProjectRef refs[8];
  int n = tcs_project_refs(game, refs, 8);
  const char *want[] = {"refs/lib/Lib.csproj", "refs/lib3/Lib3.csproj",
                        "refs/lib2/Lib2.csproj"};
  int nwant = (int)(sizeof(want) / sizeof(want[0]));
  if (n != nwant) {
    SDL_Log("FAIL: %d reference(s), want %d", n, nwant);
    failures++;
  }
  for (int i = 0; i < n; i++)
    SDL_Log("reference: %s", refs[i].path);
  for (int i = 0; i < n && i < nwant; i++) {
    char full[600];
    SDL_snprintf(full, sizeof(full), "%s/%s", root, want[i]);
    if (strcmp(refs[i].path, full) != 0) {
      SDL_Log("FAIL: [%d] got \"%s\", want \"%s\"", i, refs[i].path, full);
      failures++;
    }
  }
  SDL_snprintf(game, sizeof(game), "%s/refs/missing/Missing.csproj", root);
  if (tcs_project_refs(game, refs, 8) != 0) {
    SDL_Log("FAIL: a missing csproj has references");
    failures++;
  }
}

// 相対 path で起動したとき (lub Game.csproj)。
// 参照の側から書いた読み始めの csproj (../game/Game.csproj) も、読み始めの csproj と同じ file として入れない
static void check_refs_relative(const char *root) {
  char *cwd = SDL_GetCurrentDirectory();
  char dir[600];
  SDL_snprintf(dir, sizeof(dir), "%s/refs/game", root);
  if (chdir(dir) != 0) {
    SDL_Log("FAIL: cannot enter %s", dir);
    failures++;
    SDL_free(cwd);
    return;
  }
  TcsProjectRef refs[8];
  int n = tcs_project_refs("Game.csproj", refs, 8);
  const char *want[] = {"../lib/Lib.csproj", "../lib3/Lib3.csproj",
                        "../lib2/Lib2.csproj"};
  int nwant = (int)(sizeof(want) / sizeof(want[0]));
  if (n != nwant) {
    SDL_Log("FAIL: relative start: %d reference(s), want %d", n, nwant);
    failures++;
  }
  for (int i = 0; i < n && i < nwant; i++)
    if (strcmp(refs[i].path, want[i]) != 0) {
      SDL_Log("FAIL: relative start: [%d] got \"%s\", want \"%s\"", i,
              refs[i].path, want[i]);
      failures++;
    }
  char here[700];
  SDL_snprintf(here, sizeof(here), "%s/refs/game/Foo.cs", root);
  if (!tcs_same_path("Foo.cs", "../game/Foo.cs") ||
      !tcs_same_path("Foo.cs", here) ||
      !tcs_same_path("./a/../Foo.cs", "Foo.cs")) {
    SDL_Log("FAIL: the same file written two ways is not the same path");
    failures++;
  }
  if (tcs_same_path("Foo.cs", "../lib/Foo.cs")) {
    SDL_Log("FAIL: files in different directories are the same path");
    failures++;
  }
  if (chdir(cwd) != 0)
    failures++;
  SDL_free(cwd);
}

// 印の付いた参照先の csproj が無いときは、無いと分かるように -1
static void check_missing_ref(const char *root) {
  write_file(
      root, "refs/bad/Bad.csproj",
      "<Project><ItemGroup>"
      "<ProjectReference Include=\"../nope/Nope.csproj\" Lub=\"Source\"/>"
      "</ItemGroup></Project>");
  char bad[600];
  SDL_snprintf(bad, sizeof(bad), "%s/refs/bad/Bad.csproj", root);
  TcsProjectRef refs[8];
  int n = tcs_project_refs(bad, refs, 8);
  if (n != -1) {
    SDL_Log("FAIL: a missing referenced csproj gave %d, want -1", n);
    failures++;
  }
}

static const TcsProjectRef *find_ref(const TcsProjectRef *refs, int n,
                                     const char *root, const char *rel) {
  char full[600];
  SDL_snprintf(full, sizeof(full), "%s/%s", root, rel);
  for (int i = 0; i < n; i++)
    if (strcmp(refs[i].path, full) == 0)
      return &refs[i];
  SDL_Log("FAIL: %s is not a reference", rel);
  failures++;
  return NULL;
}

static void expect_ref(const TcsProjectRef *refs, int n, const char *root,
                       const char *rel, bool overlay, bool tool) {
  const TcsProjectRef *r = find_ref(refs, n, root, rel);
  if (r && (r->overlay != overlay || r->tool != tool)) {
    SDL_Log("FAIL: %s overlay=%d tool=%d, want overlay=%d tool=%d", rel,
            r->overlay, r->tool, overlay, tool);
    failures++;
  }
}

// overlay の参照先から辿る参照は、ゲームからも辿れなければ tool (配る物に入れない)。
// 先に Source として辿った csproj も、Lub="Overlay" の参照があれば overlay
static void check_overlay_refs(const char *root) {
  write_file(
      root, "ov/game/Game.csproj",
      "<Project><ItemGroup>"
      "<ProjectReference Include=\"../tool/Tool.csproj\" Lub=\"Overlay\"/>"
      "<ProjectReference Include=\"../shared/Shared.csproj\" Lub=\"Source\"/>"
      "</ItemGroup></Project>");
  write_file(
      root, "ov/tool/Tool.csproj",
      "<Project><ItemGroup>"
      "<ProjectReference Include=\"../ui/Ui.csproj\" Lub=\"Source\"/>"
      "<ProjectReference Include=\"../shared/Shared.csproj\" Lub=\"Source\"/>"
      "</ItemGroup></Project>");
  write_file(
      root, "ov/ui/Ui.csproj",
      "<Project><ItemGroup>"
      "<ProjectReference Include=\"../uicore/UiCore.csproj\" Lub=\"Source\"/>"
      "</ItemGroup></Project>");
  write_file(root, "ov/uicore/UiCore.csproj", "<Project />");
  write_file(root, "ov/shared/Shared.csproj", "<Project />");
  char game[600];
  SDL_snprintf(game, sizeof(game), "%s/ov/game/Game.csproj", root);
  TcsProjectRef refs[8];
  int n = tcs_project_refs(game, refs, 8);
  expect_ref(refs, n, root, "ov/tool/Tool.csproj", true, true);
  expect_ref(refs, n, root, "ov/ui/Ui.csproj", false, true);
  expect_ref(refs, n, root, "ov/uicore/UiCore.csproj", false, true);
  expect_ref(refs, n, root, "ov/shared/Shared.csproj", false, false);

  // ゲームが Source で参照する Lib を、後から Lib の参照 (Ui2) が辿る順。
  // Ui2 は overlay の参照先からだけ辿るので tool、Lib はゲームから辿るので tool でない。
  // Tool2 は先に Source として辿っても overlay
  write_file(
      root, "ov2/game/Game.csproj",
      "<Project><ItemGroup>"
      "<ProjectReference Include=\"../tool/Tool2.csproj\" Lub=\"Source\"/>"
      "<ProjectReference Include=\"../ui/Ui2.csproj\" Lub=\"Overlay\"/>"
      "</ItemGroup></Project>");
  write_file(root, "ov2/tool/Tool2.csproj", "<Project />");
  write_file(
      root, "ov2/ui/Ui2.csproj",
      "<Project><ItemGroup>"
      "<ProjectReference Include=\"../tool/Tool2.csproj\" Lub=\"Overlay\"/>"
      "</ItemGroup></Project>");
  SDL_snprintf(game, sizeof(game), "%s/ov2/game/Game.csproj", root);
  n = tcs_project_refs(game, refs, 8);
  expect_ref(refs, n, root, "ov2/tool/Tool2.csproj", true, false);
  expect_ref(refs, n, root, "ov2/ui/Ui2.csproj", true, true);

  // overlay の参照先から先に辿った csproj も、後でゲームから辿ればその参照ごと tool でない
  write_file(
      root, "ov3/game/Game.csproj",
      "<Project><ItemGroup>"
      "<ProjectReference Include=\"../tool/Tool.csproj\" Lub=\"Overlay\"/>"
      "<ProjectReference Include=\"../lib/Lib.csproj\" Lub=\"Source\"/>"
      "</ItemGroup></Project>");
  write_file(root, "ov3/tool/Tool.csproj",
             "<Project><ItemGroup>"
             "<ProjectReference Include=\"../lib/Lib.csproj\" Lub=\"Source\"/>"
             "</ItemGroup></Project>");
  write_file(
      root, "ov3/lib/Lib.csproj",
      "<Project><ItemGroup>"
      "<ProjectReference Include=\"../core/Core.csproj\" Lub=\"Source\"/>"
      "</ItemGroup></Project>");
  write_file(root, "ov3/core/Core.csproj", "<Project />");
  SDL_snprintf(game, sizeof(game), "%s/ov3/game/Game.csproj", root);
  n = tcs_project_refs(game, refs, 8);
  expect_ref(refs, n, root, "ov3/tool/Tool.csproj", true, true);
  expect_ref(refs, n, root, "ov3/lib/Lib.csproj", false, false);
  expect_ref(refs, n, root, "ov3/core/Core.csproj", false, false);
}

int main(void) {
  char root[512];
  SDL_snprintf(root, sizeof(root), "%stcs_sources_fixture", SDL_GetBasePath());
  remove_tree(root);

  // 入る
  touch(root, "Game.cs");
  touch(root, "sub/A.cs");
  touch(root, "sub/deep/B.cs");
  touch(root, "Z.cs");
  // 入らない: 拡張子違い、bin / obj (どの深さでも)、隠しディレクトリ
  touch(root, "Game.csproj");
  touch(root, "sub/notes.txt");
  touch(root, "bin/Debug/X.cs");
  touch(root, "obj/Debug/Y.cs");
  touch(root, "sub/obj/Debug/W.cs");
  touch(root, ".lub/V.cs");
  touch(root, "sub/.hidden/H.cs");

  int count = 0;
  char **got = tcs_glob_sources(root, &count);
  const char *want[] = {"Game.cs", "Z.cs", "sub/A.cs", "sub/deep/B.cs"};
  int nwant = (int)(sizeof(want) / sizeof(want[0]));
  if (count != nwant) {
    SDL_Log("FAIL: %d source(s), want %d", count, nwant);
    failures++;
  }
  for (int i = 0; i < count; i++)
    SDL_Log("source: %s", got[i]);
  for (int i = 0; i < count && i < nwant; i++) {
    if (strcmp(got[i], want[i]) != 0) {
      SDL_Log("FAIL: [%d] got \"%s\", want \"%s\"", i, got[i], want[i]);
      failures++;
    }
  }
  tcs_free_sources(got, count);

  char missing[600];
  SDL_snprintf(missing, sizeof(missing), "%s/no_such_dir", root);
  got = tcs_glob_sources(missing, &count);
  if (count != 0) {
    SDL_Log("FAIL: missing dir returned %d source(s)", count);
    failures++;
  }
  tcs_free_sources(got, count);

  check_refs(root);
  check_refs_relative(root);
  check_missing_ref(root);
  check_overlay_refs(root);

  remove_tree(root);
  if (failures) {
    SDL_Log("tcs_sources_smoke: %d failure(s)", failures);
    return 1;
  }
  SDL_Log("PASS: tcs_sources_smoke");
  return 0;
}
