// tcs_glob_sources の smoke。csproj ディレクトリの implicit glob (**/*.cs、
// bin / obj / 隠しディレクトリを除く) を、実ファイルの fixture で検査する。
#include "../../src/tcs_build.h"
#include <SDL3/SDL.h>
#include <stdlib.h>
#include <string.h>

static int failures;

static int cmp_desc(const void *a, const void *b) {
  return strcmp(*(char *const *)b, *(char *const *)a);
}

// SDL に再帰削除は無い。降順に並べると子が親より先に来る
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

static void touch(const char *root, const char *rel) {
  char path[512];
  SDL_snprintf(path, sizeof(path), "%s/%s", root, rel);
  char *slash = strrchr(path, '/');
  *slash = '\0';
  SDL_CreateDirectory(path);
  *slash = '/';
  if (!SDL_SaveFile(path, "", 0)) {
    SDL_Log("FAIL: cannot create %s: %s", path, SDL_GetError());
    failures++;
  }
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

  remove_tree(root);
  if (failures) {
    SDL_Log("tcs_sources_smoke: %d failure(s)", failures);
    return 1;
  }
  SDL_Log("PASS: tcs_sources_smoke");
  return 0;
}
