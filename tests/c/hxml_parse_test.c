#include "../../src/haxe_build.h"
#include <SDL3/SDL.h>
#include <assert.h>
#include <stdio.h>
#include <string.h>

int main(void) {
  // 一時 hxml
  // を作る。`-cp`、`--class-path`、`-main`、コメント、空行を混在させて
  // 全部の経路を 1 ファイルで通す。
  const char *hxml_path = "/tmp/lub_hxml_parse_test.hxml";
  FILE *f = fopen(hxml_path, "w");
  assert(f);
  fprintf(f, "# leading comment\n"
             "\n"
             "-cp samples\n"
             "-lib lub\n"
             "  # indented comment\n"
             "-main Triangle01\n"
             "--class-path haxe-extra\n");
  fclose(f);

  HxmlMeta m;
  bool ok = hxml_parse(hxml_path, &m);
  assert(ok);
  assert(strcmp(m.main_class, "Triangle01") == 0);
  assert(m.cp_count == 2);
  assert(strcmp(m.cp_paths[0], "samples") == 0);
  assert(strcmp(m.cp_paths[1], "haxe-extra") == 0);

  // missing -main → false
  const char *bad_path = "/tmp/lub_hxml_parse_test_bad.hxml";
  FILE *fb = fopen(bad_path, "w");
  assert(fb);
  fprintf(fb, "-cp foo\n");
  fclose(fb);
  HxmlMeta mb;
  bool bad = hxml_parse(bad_path, &mb);
  assert(!bad);

  printf("OK\n");
  return 0;
}
