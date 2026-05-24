#ifndef LUB_HAXE_PIPELINE_H
#define LUB_HAXE_PIPELINE_H

#include <stdbool.h>
#include "haxe_server.h"
#include "haxe_build.h"
#include "haxe_watch.h"

// haxe --wait + build + watch を 1 つの lifecycle で扱う合成 layer。
// app.c の haxe_enabled 経路はこれだけを触る。
typedef struct HaxePipeline {
    HaxeServer server;
    HxmlMeta   meta;
    HaxeWatch  watch;
    char       hxml_path[768];
    bool       enabled;
} HaxePipeline;

// server を起動 -> hxml parse -> 初回 build -> watch を init。
// いずれか失敗したら server を確実に停止して false を返す。
bool haxe_pipeline_start(HaxePipeline *p, const char *hxml_path);

// watch を shutdown -> server を停止。enabled でなければ何もしない。
void haxe_pipeline_stop(HaxePipeline *p);

// 毎フレーム呼ぶ。watch tick が rebuild トリガーを返したら
// haxe_build_run を回す。meta_dirty (hxml 自体が変わった) なら
// meta を再 parse + watch を作り直してから build する。
// rebuild が走って成功した場合のみ true。
bool haxe_pipeline_tick(HaxePipeline *p);

#endif
