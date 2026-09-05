// 生成した Lua binding (src/gen/lua_api_gen.c) が使う手書きの helper。
// 生成コードは C API (include/lub/lub_api.h) への詰め替えだけを持ち、Lua の
// 値の読み書き、sentinel、view userdata、callback の trampoline の土台、
// 呼び出しの間だけ生きる memory (arena) はここが受け持つ。
#pragma once
#include "lub/lub_api.h"
#include <lauxlib.h>
#include <lua.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

// ------------------------------------------------------------- context

LubContext *lgen_ctx(void);
// luaL_error(lub_last_error())。
int lgen_raise(lua_State *L);

// 呼び出しの間だけ生きる memory。生成した関数は入口で mark、出口で release
// する (入れ子の呼び出しも安全)。
typedef size_t LgenMark;
LgenMark lgen_mark(void);
void lgen_release(LgenMark mark);
void *lgen_alloc(lua_State *L, size_t bytes);

// -------------------------------------------------------------- scalars

LubStr lgen_str_arg(lua_State *L, int idx); // string か Bytes view (必須)
LubStr lgen_str_opt(lua_State *L, int idx); // nil なら len 0
const uint8_t *lgen_bytes_arg(lua_State *L, int idx, int32_t *len,
                              bool required);

// table の field (存在して nil でなければ true。値は push しない)。
bool lgen_has(lua_State *L, int idx, const char *key);
float lgen_num(lua_State *L, int idx, const char *key, float def);
bool lgen_num_opt(lua_State *L, int idx, const char *key, float *out);
int32_t lgen_int(lua_State *L, int idx, const char *key, int32_t def);
bool lgen_int_opt(lua_State *L, int idx, const char *key, int32_t *out);
bool lgen_bool(lua_State *L, int idx, const char *key, bool def);
bool lgen_bool_opt(lua_State *L, int idx, const char *key, bool *out);
LubStr lgen_str(lua_State *L, int idx, const char *key); // 無ければ len 0
// 文字列 enum。名前の表 (NULL 終端) で引く。無ければ has = false。不明な名前は
// error。
int32_t lgen_enum_str(lua_State *L, int idx, const char *key,
                      const char *const *names, const int32_t *values,
                      const char *enum_name, bool *has);
int32_t lgen_enum_str_arg(lua_State *L, int idx, const char *const *names,
                          const int32_t *values, const char *enum_name);
// 64 bit の bit mask (hex 文字列か整数)。
bool lgen_bits_opt(lua_State *L, int idx, const char *key, uint64_t *out);

void lgen_set_num(lua_State *L, const char *key, float v);
void lgen_set_int(lua_State *L, const char *key, int32_t v);
void lgen_set_bool(lua_State *L, const char *key, bool v);
void lgen_set_str(lua_State *L, const char *key, LubStr v);
void lgen_set_bits(lua_State *L, const char *key, uint64_t v);
void lgen_push_str(lua_State *L, LubStr v);
void lgen_push_bits(lua_State *L, uint64_t v);

// --------------------------------------------------------------- arrays

// 数値の配列 (table か view userdata) を arena に写す。nil なら NULL。
const float *lgen_floats_arg(lua_State *L, int idx, int32_t *count,
                             bool required);
const int32_t *lgen_ints_arg(lua_State *L, int idx, int32_t *count,
                             bool required);
const float *lgen_floats(lua_State *L, int idx, const char *key,
                         int32_t *count);
const int32_t *lgen_ints(lua_State *L, int idx, const char *key,
                         int32_t *count);
// 固定長配列。無ければ false (out は 0 埋め)。count には読んだ個数。
bool lgen_floats_fixed(lua_State *L, int idx, const char *key, float *out,
                       int32_t cap, int32_t *count);
bool lgen_ints_fixed(lua_State *L, int idx, const char *key, int32_t *out,
                     int32_t cap, int32_t *count);
// { {a,b,c,d}, ... } を [n][width] に写す。
const float *lgen_float_rows(lua_State *L, int idx, const char *key,
                             int32_t width, int32_t *count);
const LubStr *lgen_strs(lua_State *L, int idx, const char *key, int32_t *count);
const LubHandle *lgen_handles(lua_State *L, int idx, const char *key,
                              const char *kind, int32_t *count);
// record の配列。reader は table (絶対 index) を読む。
typedef void (*LgenReader)(lua_State *L, int idx, void *out);
const void *lgen_records(lua_State *L, int idx, const char *key, size_t size,
                         LgenReader reader, int32_t *count);
const void *lgen_records_arg(lua_State *L, int idx, size_t size,
                             LgenReader reader, int32_t *count, bool required);

// runtime 所有の配列を frame 有効の view userdata として push する。
void lgen_push_float_view(lua_State *L, const float *data, int32_t count);
void lgen_push_int_view(lua_State *L, const int32_t *data, int32_t count);
void lgen_push_float_table(lua_State *L, const float *data, int32_t count);
void lgen_push_int_table(lua_State *L, const int32_t *data, int32_t count);
void lgen_push_str_table(lua_State *L, const LubStr *data, int32_t count);
void lgen_push_bytes_view(lua_State *L, LubView v);

// ------------------------------------------------------------ handles

// sentinel table { __lub_kind, handle, key... } と handle の相互変換。
LubHandle lgen_ref_arg(lua_State *L, int idx, const char *kind, bool required);
LubHandle lgen_ref(lua_State *L, int idx, const char *key, const char *kind);
void lgen_push_ref(lua_State *L, const char *kind, LubHandle h);
// key で宣言した resource の sentinel。parent_idx (0 = 無し) の sentinel の
// key 列を引き継ぎ、自分の key を足す。
void lgen_push_ref_keyed(lua_State *L, const char *kind, LubHandle h,
                         int parent_idx, LubStr key);
void lgen_push_handle_table(lua_State *L, const char *kind,
                            const LubHandle *data, int32_t count);
// key で参照する resource の sentinel ({ __lub_kind, key })。
LubStr lgen_keyed_arg(lua_State *L, int idx, const char *kind);
LubStr lgen_keyed(lua_State *L, int idx, const char *key, const char *kind);
void lgen_push_keyed(lua_State *L, const char *kind, LubStr key);

// ---------------------------------------------------------- callbacks

// Lua の closure を registry に持ち、C の callback (trampoline) から呼ぶ。
// n 個の slot を持つ。runtime が手放すと user_release から
// lgen_callbacks_free。
typedef struct LgenCallbacks {
  lua_State *L;
  int n;
  int refs[8];
  bool logged[8];
  bool transient; // query の visitor (呼び出しの間だけ)。error は呼び出し元へ
  char *error;    // transient で最初の error message
} LgenCallbacks;

LgenCallbacks *lgen_callbacks_new(lua_State *L, int n);
// table idx の field key が function なら slot i に持つ。持ったら true。
bool lgen_callbacks_field(lua_State *L, LgenCallbacks *cb, int i, int idx,
                          const char *key);
// 引数 idx が function なら slot 0 に持つ。nil なら NULL。
LgenCallbacks *lgen_callbacks_arg(lua_State *L, int idx);
void lgen_callbacks_free(void *user);
// slot i の function を push する (無ければ false)。
bool lgen_callbacks_push(LgenCallbacks *cb, int i);
// pcall。持続する callback は error を slot ごとに 1 回だけ log して false、
// visitor (transient) は error を持ち帰って false。
bool lgen_callbacks_call(LgenCallbacks *cb, int i, int nargs, int nresults);
// visitor の error message (無ければ NULL)。
const char *lgen_callbacks_error(LgenCallbacks *cb);

// ------------------------------------------------------------ bindings

// draw / dispatch の自由な table を LubBinding の配列に写す。
const LubBinding *lgen_bindings_arg(lua_State *L, int idx, int32_t *count);

// ------------------------------------------------------------ register

// 生成した binding を lub table に登録する (src/gen/lua_api_gen.c)。
void lub_api_gen_register(lua_State *L);
// 生成物に無い Lua 面 (readback(key) の sentinel、view userdata の metatable、
// Bytes)。lub_api_gen_register の後に呼ぶ。
void lgen_support_register(lua_State *L, LubContext *ctx);
