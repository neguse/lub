#pragma once
// tcs VM の player (src/vm_host.c) が .csproj から動かすときの、入力の集め方。
// lub は csproj を MSBuild として評価しない。
// 読むのは basename (entry class) と、属性 Lub="Source" / Lub="Overlay" を付けた ProjectReference だけ。
// 入力は csproj のディレクトリと参照先のディレクトリの下の全 *.cs と、cs-lib の実装。
// *.ref.cs は変換せず、tcs の --ref (型だけの宣言) で渡す。
#include <SDL3/SDL.h>
#include <stdbool.h>

#define VM_PROJECT_MAX_DIRS 32

typedef struct VmProject {
  char entry[256];        // entry class (csproj の basename)
  char overlay[256];      // overlay の class (Lub="Overlay" の参照先の basename。無ければ "")
  char out_tcsb[640];     // <dir>/.lub/<Entry>.tcsb (tcs vm が書く)
  char list_path[640];    // <dir>/.lub/<Entry>.inputs (入力の一覧。tcs vm --inputs-from)
  char stub[960];         // cs-lib/lub_stub.cs ("" なら無し)
  char cs_lib[900];       // cs-lib ("" なら無し)
  char dirs[VM_PROJECT_MAX_DIRS][512]; // ソースを探すディレクトリ (entry が先頭)
  int ndirs;
  char *list_text;        // 最後に書いた一覧
  char *refs_text;        // 最後に見つけた *.ref.cs の一覧 (改行区切り)
  char *refs_split;       // vm_project_args が区切った refs_text の写し
} VmProject;

// csproj を読み、参照を辿る。
// 印の付いた参照先の csproj が無ければ log を出して false。
bool vm_project_open(VmProject *proj, const char *csproj);
// ディレクトリを探し直し、一覧が変わっていれば書き直して true。
bool vm_project_scan(VmProject *proj);
// tcs vm に渡す引数 (--inputs-from、--ref、-o、--export) を args に詰め、個数を返す。
// 文字列は proj が持つ (次の vm_project_args まで有効)。
int vm_project_args(VmProject *proj, const char **args, int max);
void vm_project_close(VmProject *proj);
