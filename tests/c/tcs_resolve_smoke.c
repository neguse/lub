// tcs_build の path 解決の smoke。Windows の '\' 区切りは Linux でも
// 文字列として検査できる。リポジトリ root を cwd にして実行する。
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

  if (failures) {
    SDL_Log("tcs_resolve_smoke: %d failure(s)", failures);
    return 1;
  }
  SDL_Log("PASS: tcs_resolve_smoke");
  return 0;
}
