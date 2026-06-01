#ifndef LUB_HAXE_WATCH_H
#define LUB_HAXE_WATCH_H

#include "haxe_build.h"
#include <SDL3/SDL_stdinc.h>
#include <stdbool.h>

typedef struct HaxeWatchEntry {
  char path[768];
  Sint64 mtime_ns;
} HaxeWatchEntry;

typedef struct HaxeWatch {
  HaxeWatchEntry *entries;
  int count;
  int cap;
  Sint64 last_change_ns; // debounce 用
  bool pending_rebuild;
} HaxeWatch;

// hxml + meta から watch root を確定し、recursive に *.hx を拾う。
// hxml 自体も watch 対象 (entry[0] に格納される)。
bool haxe_watch_init(HaxeWatch *w, const char *hxml_path, const HxmlMeta *meta);

void haxe_watch_shutdown(HaxeWatch *w);

// 毎フレーム呼ぶ。mtime に変化があり debounce window (50ms) を抜けたら
// true を返す (= rebuild すべき)。true を返したあとは内部状態の
// pending_rebuild を false に戻す。
// hxml 自体の変更が検知された場合は *meta_dirty=true。caller は meta を
// 再 parse する責務を負う。
bool haxe_watch_tick(HaxeWatch *w, bool *meta_dirty);

#endif
