// lubx.jamo (cs-lib/lub_stub.cs の namespace Lubx): かな漢字変換の jamo の session API。
// engine と session は process に 1 つ。
// IME の状態 (かな入力、変換、候補、文節の伸び縮み、学習) は jamo の session が持ち、ここは渡すだけ。
// jamo と build しない lub (LUB_JAMO が無い。web を含む) では、どれも何もしない。
#include "api_internal.h"

#ifdef LUB_JAMO
#include <jamo.h>
#include <stdlib.h>
#include <string.h>

static jamo_engine_t *engine;
static jamo_session_t *session;

// 辞書の読み込み (mozc.dict で数秒) を裏の thread で先に始める。
// thread は preload_engine にだけ書き、Open が待ってから読む。
static SDL_Thread *preload_thread;
static jamo_engine_t *preload_engine;
static char *preload_dict;

static int SDLCALL preload_main(void *arg) {
  (void)arg;
  preload_engine = jamo_engine_open(preload_dict);
  return 0;
}

// LubStr は NUL で終わらないので、jamo に渡す前に写す
static char *dup_str(LubStr s) {
  char *out = (char *)malloc((size_t)s.len + 1);
  if (!out)
    return NULL;
  memcpy(out, s.ptr, (size_t)s.len);
  out[s.len] = '\0';
  return out;
}

// jamo が返す文字列は次の呼び出しまで有効 (binding はすぐ写す)
static bool out_str(const char *s, LubStr *out) {
  if (!s) {
    *out = (LubStr){0};
    return false;
  }
  *out = (LubStr){s, (int32_t)strlen(s)};
  return true;
}

bool lubx_jamo_available(LubContext *ctx) {
  (void)ctx;
  return true;
}

bool lubx_jamo_preload(LubContext *ctx, LubStr dict) {
  (void)ctx;
  if (engine || preload_thread)
    return true;
  preload_dict = dup_str(dict);
  if (preload_dict)
    preload_thread = SDL_CreateThread(preload_main, "jamo-preload", NULL);
  if (!preload_thread) {
    free(preload_dict);
    preload_dict = NULL;
  }
  return preload_thread != NULL;
}

bool lubx_jamo_open(LubContext *ctx, LubStr dict) {
  (void)ctx;
  if (!engine && preload_thread) {
    SDL_WaitThread(preload_thread, NULL); // 読んでいる途中ならここで待つ
    preload_thread = NULL;
    engine = preload_engine;
    free(preload_dict);
    preload_dict = NULL;
  }
  if (!engine) {
    char *path = dup_str(dict);
    if (path)
      engine = jamo_engine_open(path);
    free(path);
  }
  if (engine && !session)
    session = jamo_session_new(engine);
  return session != NULL;
}

bool lubx_jamo_load_user_data(LubContext *ctx, LubStr path) {
  (void)ctx;
  char *p = session ? dup_str(path) : NULL;
  bool ok = p && jamo_engine_load_user_data(engine, p) == JAMO_OK;
  free(p);
  return ok;
}

bool lubx_jamo_save_user_data(LubContext *ctx, LubStr path) {
  (void)ctx;
  char *p = session ? dup_str(path) : NULL;
  bool ok = p && jamo_engine_save_user_data(engine, p) == JAMO_OK;
  free(p);
  return ok;
}

bool lubx_jamo_converting(LubContext *ctx) {
  (void)ctx;
  return session && jamo_session_state(session) == JAMO_STATE_CONVERTING;
}

bool lubx_jamo_input(LubContext *ctx, LubStr text) {
  (void)ctx;
  char *t = session ? dup_str(text) : NULL;
  bool ok = t && jamo_session_input(session, t) == JAMO_OK;
  free(t);
  return ok;
}

bool lubx_jamo_backspace(LubContext *ctx) {
  (void)ctx;
  return session && jamo_session_backspace(session) == 1;
}

bool lubx_jamo_delete_forward(LubContext *ctx) {
  (void)ctx;
  return session && jamo_session_delete_forward(session) == 1;
}

int32_t lubx_jamo_cursor(LubContext *ctx) {
  (void)ctx;
  int32_t c = session ? jamo_session_cursor(session) : 0;
  return c < 0 ? 0 : c;
}

bool lubx_jamo_move_cursor(LubContext *ctx, int32_t delta) {
  (void)ctx;
  return session && jamo_session_move_cursor(session, delta) == 1;
}

bool lubx_jamo_set_cursor(LubContext *ctx, int32_t pos) {
  (void)ctx;
  return session && jamo_session_set_cursor(
                        session, pos < 0 ? 0 : (uint32_t)pos) == JAMO_OK;
}

bool lubx_jamo_is_alpha(LubContext *ctx) {
  (void)ctx;
  return session && jamo_session_is_alpha(session) == 1;
}

bool lubx_jamo_set_alpha(LubContext *ctx, bool on) {
  (void)ctx;
  return session && jamo_session_set_alpha(session, on) == JAMO_OK;
}

bool lubx_jamo_transform(LubContext *ctx, int32_t kind) {
  (void)ctx;
  static const int32_t kinds[] = {
      JAMO_TRANSFORM_HIRAGANA, JAMO_TRANSFORM_KATAKANA,
      JAMO_TRANSFORM_HALF_ALPHA, JAMO_TRANSFORM_WIDE_ALPHA};
  if (!session || kind < 0 || kind > 3)
    return false;
  return jamo_session_transform(session, kinds[kind]) == JAMO_OK;
}

bool lubx_jamo_preedit(LubContext *ctx, LubStr *text) {
  (void)ctx;
  return out_str(session ? jamo_session_preedit(session) : NULL, text);
}

void lubx_jamo_focused_range(LubContext *ctx, int32_t *from, int32_t *length) {
  (void)ctx;
  uint32_t f = 0, n = 0;
  if (session)
    jamo_session_focused_range(session, &f, &n);
  *from = (int32_t)f;
  *length = (int32_t)n;
}

bool lubx_jamo_convert(LubContext *ctx, int32_t candidates) {
  (void)ctx;
  return session && jamo_session_convert(session, (uint32_t)candidates) ==
                        JAMO_OK;
}

int32_t lubx_jamo_clause_count(LubContext *ctx) {
  (void)ctx;
  return session ? (int32_t)jamo_session_clause_count(session) : 0;
}

bool lubx_jamo_clause_yomi(LubContext *ctx, int32_t clause, LubStr *text) {
  (void)ctx;
  return out_str(session && clause >= 0
                     ? jamo_session_clause_yomi(session, (uint32_t)clause)
                     : NULL,
                 text);
}

int32_t lubx_jamo_candidate_count(LubContext *ctx, int32_t clause) {
  (void)ctx;
  int32_t n = session && clause >= 0
                  ? jamo_session_candidate_count(session, (uint32_t)clause)
                  : 0;
  return n < 0 ? 0 : n;
}

bool lubx_jamo_candidate(LubContext *ctx, int32_t clause, int32_t index,
                         LubStr *text) {
  (void)ctx;
  return out_str(session && clause >= 0 && index >= 0
                     ? jamo_session_candidate(session, (uint32_t)clause,
                                              (uint32_t)index)
                     : NULL,
                 text);
}

int32_t lubx_jamo_selected(LubContext *ctx, int32_t clause) {
  (void)ctx;
  int32_t sel = session && clause >= 0
                    ? jamo_session_selected(session, (uint32_t)clause)
                    : -1;
  return sel < 0 ? -1 : sel;
}

bool lubx_jamo_select(LubContext *ctx, int32_t clause, int32_t index) {
  (void)ctx;
  return session && clause >= 0 && index >= 0 &&
         jamo_session_select(session, (uint32_t)clause, (uint32_t)index) ==
             JAMO_OK;
}

int32_t lubx_jamo_focus(LubContext *ctx) {
  (void)ctx;
  int32_t f = session ? jamo_session_focus(session) : -1;
  return f < 0 ? -1 : f;
}

bool lubx_jamo_set_focus(LubContext *ctx, int32_t clause) {
  (void)ctx;
  return session && clause >= 0 &&
         jamo_session_set_focus(session, (uint32_t)clause) == JAMO_OK;
}

bool lubx_jamo_resize(LubContext *ctx, int32_t delta, int32_t candidates) {
  (void)ctx;
  return session && jamo_session_resize(session, delta,
                                        (uint32_t)candidates) == JAMO_OK;
}

bool lubx_jamo_revert(LubContext *ctx) {
  (void)ctx;
  return session && jamo_session_revert(session) == JAMO_OK;
}

bool lubx_jamo_commit(LubContext *ctx, LubStr *text) {
  (void)ctx;
  return out_str(session ? jamo_session_commit(session) : NULL, text);
}

bool lubx_jamo_commit_prefix(LubContext *ctx, LubStr *text) {
  (void)ctx;
  return out_str(session ? jamo_session_commit_prefix(session) : NULL, text);
}

void lubx_jamo_reset(LubContext *ctx) {
  (void)ctx;
  if (session)
    jamo_session_reset(session);
}

#else

bool lubx_jamo_available(LubContext *ctx) {
  (void)ctx;
  return false;
}
bool lubx_jamo_preload(LubContext *ctx, LubStr dict) {
  (void)ctx;
  (void)dict;
  return false;
}
bool lubx_jamo_open(LubContext *ctx, LubStr dict) {
  (void)ctx;
  (void)dict;
  return false;
}
bool lubx_jamo_load_user_data(LubContext *ctx, LubStr path) {
  (void)ctx;
  (void)path;
  return false;
}
bool lubx_jamo_save_user_data(LubContext *ctx, LubStr path) {
  (void)ctx;
  (void)path;
  return false;
}
bool lubx_jamo_converting(LubContext *ctx) {
  (void)ctx;
  return false;
}
bool lubx_jamo_input(LubContext *ctx, LubStr text) {
  (void)ctx;
  (void)text;
  return false;
}
bool lubx_jamo_backspace(LubContext *ctx) {
  (void)ctx;
  return false;
}
bool lubx_jamo_delete_forward(LubContext *ctx) {
  (void)ctx;
  return false;
}
int32_t lubx_jamo_cursor(LubContext *ctx) {
  (void)ctx;
  return 0;
}
bool lubx_jamo_move_cursor(LubContext *ctx, int32_t delta) {
  (void)ctx;
  (void)delta;
  return false;
}
bool lubx_jamo_set_cursor(LubContext *ctx, int32_t pos) {
  (void)ctx;
  (void)pos;
  return false;
}
bool lubx_jamo_is_alpha(LubContext *ctx) {
  (void)ctx;
  return false;
}
bool lubx_jamo_set_alpha(LubContext *ctx, bool on) {
  (void)ctx;
  (void)on;
  return false;
}
bool lubx_jamo_transform(LubContext *ctx, int32_t kind) {
  (void)ctx;
  (void)kind;
  return false;
}
bool lubx_jamo_preedit(LubContext *ctx, LubStr *text) {
  (void)ctx;
  *text = (LubStr){0};
  return false;
}
void lubx_jamo_focused_range(LubContext *ctx, int32_t *from, int32_t *length) {
  (void)ctx;
  *from = 0;
  *length = 0;
}
bool lubx_jamo_convert(LubContext *ctx, int32_t candidates) {
  (void)ctx;
  (void)candidates;
  return false;
}
int32_t lubx_jamo_clause_count(LubContext *ctx) {
  (void)ctx;
  return 0;
}
bool lubx_jamo_clause_yomi(LubContext *ctx, int32_t clause, LubStr *text) {
  (void)ctx;
  (void)clause;
  *text = (LubStr){0};
  return false;
}
int32_t lubx_jamo_candidate_count(LubContext *ctx, int32_t clause) {
  (void)ctx;
  (void)clause;
  return 0;
}
bool lubx_jamo_candidate(LubContext *ctx, int32_t clause, int32_t index,
                         LubStr *text) {
  (void)ctx;
  (void)clause;
  (void)index;
  *text = (LubStr){0};
  return false;
}
int32_t lubx_jamo_selected(LubContext *ctx, int32_t clause) {
  (void)ctx;
  (void)clause;
  return -1;
}
bool lubx_jamo_select(LubContext *ctx, int32_t clause, int32_t index) {
  (void)ctx;
  (void)clause;
  (void)index;
  return false;
}
int32_t lubx_jamo_focus(LubContext *ctx) {
  (void)ctx;
  return -1;
}
bool lubx_jamo_set_focus(LubContext *ctx, int32_t clause) {
  (void)ctx;
  (void)clause;
  return false;
}
bool lubx_jamo_resize(LubContext *ctx, int32_t delta, int32_t candidates) {
  (void)ctx;
  (void)delta;
  (void)candidates;
  return false;
}
bool lubx_jamo_revert(LubContext *ctx) {
  (void)ctx;
  return false;
}
bool lubx_jamo_commit(LubContext *ctx, LubStr *text) {
  (void)ctx;
  *text = (LubStr){0};
  return false;
}
bool lubx_jamo_commit_prefix(LubContext *ctx, LubStr *text) {
  (void)ctx;
  *text = (LubStr){0};
  return false;
}
void lubx_jamo_reset(LubContext *ctx) { (void)ctx; }

#endif
