// tcs_build の path 解決の smoke。
// Windows の '\' 区切りは Linux でも文字列として検査できる。
// リポジトリ root を cwd にして実行する。
#include "../../src/path_util.h"
#include "../../src/tcs_build.h"
#include <SDL3/SDL.h>
#include <string.h>

static int failures;

static void expect_str(const char *what, const char *got, const char *want) {
  if (strcmp(got, want) != 0) {
    SDL_Log("FAIL: %s: got \"%s\", want \"%s\"", what, got, want);
    failures++;
  }
}

static void expect_parent(const char *path, const char *want) {
  char out[256];
  path_parent_dir(path, out, sizeof(out));
  expect_str(path, out, want);
}

int main(void) {
  expect_parent("/a/b/build/", "/a/b");
  expect_parent("/a/b/build", "/a/b");
  expect_parent("D:\\x\\lub\\build-release\\", "D:\\x\\lub");
  expect_parent("D:\\x\\lub\\build-release", "D:\\x\\lub");
  expect_parent("D:/x\\lub/build\\", "D:/x\\lub");
  expect_parent("/a", "/");
  expect_parent("x", ".");

  char out[256];
  SDL_setenv_unsafe("LUB_CS_LIB", "tests/c", 1);
  if (!tcs_resolve_cs_lib(out, sizeof(out))) {
    SDL_Log("FAIL: LUB_CS_LIB=tests/c was not accepted");
    failures++;
  } else {
    expect_str("LUB_CS_LIB", out, "tests/c");
  }
  // 上書き先が無いときは cwd の cs-lib に黙って fallback しない
  SDL_setenv_unsafe("LUB_CS_LIB", "no/such/dir", 1);
  if (tcs_resolve_cs_lib(out, sizeof(out))) {
    SDL_Log("FAIL: LUB_CS_LIB=no/such/dir fell back to \"%s\"", out);
    failures++;
  }
  SDL_unsetenv_unsafe("LUB_CS_LIB");
  if (!tcs_resolve_cs_lib(out, sizeof(out))) {
    SDL_Log("FAIL: cs-lib not found from cwd");
    failures++;
  } else {
    expect_str("cwd cs-lib", out, "cs-lib");
  }

  // セルフホストの compiler: LUB_TCS_SELFHOST_DIR は 4 つのファイルが揃った dir だけを受け、黙って他を探さない
  TcsSelfHost sh;
  SDL_setenv_unsafe("LUB_TCS_SELFHOST_DIR", "no/such/dir", 1);
  if (tcs_resolve_selfhost(&sh)) {
    SDL_Log("FAIL: LUB_TCS_SELFHOST_DIR=no/such/dir was accepted");
    failures++;
  }
  const char *dir = "build-tcs-resolve-smoke";
  SDL_CreateDirectory(dir);
  const char *names[4] = {"tcsc.lua", "compiler.lua", "prelude.lua", "Linq.cs"};
  char path[256];
  for (int i = 0; i < 3; i++) {
    SDL_snprintf(path, sizeof(path), "%s/%s", dir, names[i]);
    SDL_SaveFile(path, "--\n", 3);
  }
  SDL_setenv_unsafe("LUB_TCS_SELFHOST_DIR", dir, 1);
  if (tcs_resolve_selfhost(&sh)) {
    SDL_Log("FAIL: LUB_TCS_SELFHOST_DIR without Linq.cs was accepted");
    failures++;
  }
  SDL_snprintf(path, sizeof(path), "%s/%s", dir, names[3]);
  SDL_SaveFile(path, "//\n", 3);
  if (!tcs_resolve_selfhost(&sh)) {
    SDL_Log("FAIL: LUB_TCS_SELFHOST_DIR with the four files was not accepted");
    failures++;
  } else {
    expect_str("self-host compiler", sh.compiler,
               "build-tcs-resolve-smoke/compiler.lua");
    expect_str("self-host driver", sh.driver,
               "build-tcs-resolve-smoke/tcsc.lua");
  }
  for (int i = 0; i < 4; i++) {
    SDL_snprintf(path, sizeof(path), "%s/%s", dir, names[i]);
    SDL_RemovePath(path);
  }
  SDL_RemovePath(dir);
  SDL_unsetenv_unsafe("LUB_TCS_SELFHOST_DIR");

  if (failures) {
    SDL_Log("tcs_resolve_smoke: %d failure(s)", failures);
    return 1;
  }
  SDL_Log("PASS: tcs_resolve_smoke");
  return 0;
}
