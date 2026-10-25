#pragma once
// tcs (TinyC#) pipeline: .cs entry の transpile + watch を lub が駆動する。
// 実装は tcs_build.c。
#include <SDL3/SDL.h>
#include <stdbool.h>

#define TCS_MAX_LUA_DIRS 16

typedef struct TcsPipeline {
  SDL_Process *proc; // tcs --watch (kill at quit)
  bool enabled;
  // tcs の標準出力 (reload chunk の stream) のうち、まだ切り出していない分
  char *buf;
  size_t len, cap;
  // Lub="Overlay" の参照先の basename (= overlay の entry class)。
  // 無ければ空
  char overlay[128];
  // 変換したソースのディレクトリ (entry と読んだ参照先)。
  // VM はここから Lua の module (?.lua) と native の module (?.so) を require で読む
  char lua_dirs[TCS_MAX_LUA_DIRS][512];
  int lua_dir_count;
} TcsPipeline;

// cs_path (.csproj。entry class = basename、入力 = 同 dir 以下の全 *.cs と、印の付いた ProjectReference の参照先の dir 以下の全 *.cs。*.ref.cs は入力にせず --ref で渡す) を transpile して <dir>/.lub/<Base>.lua を生成し、tcs --watch を背後に張る。
// 成功時 out_lua に出力パスを書き true。
// 初回 transpile 完了 (dotnet cold start 込み) まで block する。
// with_overlay が false なら、overlay の参照先からだけ辿れる参照 (TcsProjectRef の tool) は読まない (lub export と --serve。配る物と web に overlay を入れない)。
bool tcs_pipeline_start(TcsPipeline *p, const char *cs_path, char *out_lua,
                        size_t out_lua_sz, bool with_overlay);
typedef enum TcsFrameKind {
  // 本文は実行中の VM に当てる Lua chunk
  TCS_FRAME_RELOAD_CHUNK,
  // chunk では移行できない変更。
  // 本文は改行終端の理由の行で、その build の出力は書き終わっている。
  // 以後の chunk はその出力から起動した VM 向け
  TCS_FRAME_RESTART_REQUIRED,
} TcsFrameKind;

// 以後の rebuild ごとに tcs が書く frame を、届いた順に 1 つ取り出す (block しない)。
// 知らない種類の frame は読み飛ばす。
// *out は SDL_malloc で確保し (NUL 終端)、呼び出し側が SDL_free する。
bool tcs_pipeline_next_frame(TcsPipeline *p, TcsFrameKind *kind, char **out,
                             size_t *out_len);
void tcs_pipeline_stop(TcsPipeline *p);

// csproj ディレクトリ dir の .cs 一覧 (dir からの相対 path、'/' 区切り、昇順)。
// SDK-style csproj の implicit glob (**/*.cs) に倣い、サブディレクトリも辿る。
// bin / obj と、名前が '.' で始まるディレクトリ (.lub を含む) は辿らない。
// *count に個数を書く。
// tcs_free_sources で解放する。
char **tcs_glob_sources(const char *dir, int *count);
void tcs_free_sources(char **sources, int count);

// csproj の ProjectReference のうち、lub がソースとして読む印 (属性 Lub="Source" か Lub="Overlay") の付いたもの。
// path は参照先の .csproj (参照元の csproj の path を基準に解決し、"." と ".." を畳んだもの)。
// overlay は Lub="Overlay" で、参照先の basename の class をゲームに重ねる entry にする。
// tool は overlay の参照先からだけ辿れる参照 (overlay の参照先自身を含む)。
typedef struct TcsProjectRef {
  char path[512];
  bool overlay;
  bool tool;
} TcsProjectRef;

// csproj を読み、印の付いた ProjectReference を out に書く。
// 参照先の csproj の参照も辿り、同じ file は 1 回だけ入れる (読み始めた csproj 自身は入れない)。
// Include が MSBuild の property ($(...)) を含む参照は読めないので log を出して飛ばす。
// 返り値は書いた個数 (max まで)。
// 印の付いた参照先の csproj が無ければ log を出して -1。
int tcs_project_refs(const char *csproj, TcsProjectRef *out, int max);

// a と b が同じ file を指すか (相対 path は cwd から、"." と ".." を畳んで比べる)。
bool tcs_same_path(const char *a, const char *b);

// cs-lib ディレクトリを解決して out に書く。
// 優先順は LUB_CS_LIB 環境変数 (指定したら fallback しない)、cwd の cs-lib、実行ファイルの 1 階層上の cs-lib。
// 見つからなければ false。
bool tcs_resolve_cs_lib(char *out, size_t outsz);
