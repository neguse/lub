#ifndef LUB_HAXE_BUILD_H
#define LUB_HAXE_BUILD_H

#include "haxe_server.h"
#include <stdbool.h>
#include <stddef.h>

typedef struct HaxeBuildResult {
  bool ok;
  char log[4096]; // haxe stdout+stderr (last ~4 KB)
} HaxeBuildResult;

// hxml から `-cp <path>` (もしくは `--class-path <path>`、複数可) と
// `-main <ClassName>` を取り出す。`#` はコメント。空行はスキップ。
typedef struct HxmlMeta {
  char main_class[128];
  char cp_paths[8][512]; // 最大 8 個までの -cp
  int cp_count;
} HxmlMeta;

bool hxml_parse(const char *hxml_path, HxmlMeta *out);

// hxml を build して `<dir>/.lub/<basename>.lua` に atomic write する。
// `<basename>` は hxml の path basename (拡張子抜き)。
// server->port を `--connect` に渡す。
HaxeBuildResult haxe_build_run(const HaxeServer *server, const char *hxml_path,
                               const HxmlMeta *meta);

// path helpers — `/` と `\` 両方を許容。Task 23 の main.c でも使用するので
// header から expose する。out が NUL-terminated になることを保証する。
const char *path_basename_noext(const char *path, char *out, size_t outsz);
void path_dirname(const char *path, char *out, size_t outsz);

#endif
