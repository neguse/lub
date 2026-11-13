/* jamo (かな漢字変換) の session API を、tcs VM の player (lub_vm) の native の拡張として出す (src/Jamo.ref.cs の関数)。
 * lub の include/lub/lub_vm_extension.h の形で、2 つの使い方がある。
 * lider の player (native/CMakeLists.txt、native と web) は静的に link する (入口の名前は lub_vm_extension_jamo)。
 * lub の既製の lub_vm は、scripts/build-native.sh が作る src/Jamo.so を読む。
 * web (emscripten) は thread を使わないので、Preload は何もせず、Open がその場で辞書を読む。
 * engine と session は process にひとつ。IME の状態 (かな入力、変換、候補、文節の伸び縮み、学習) は jamo の session が持ち、ここは渡すだけ。
 * 文節と候補の番号、preedit の中の位置は 0 から。 */
#include <lub/lub_vm_extension.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>

#include <jamo.h>

static const LubVmExtensionApi *api;
static jamo_engine_t *engine;
static jamo_session_t *session;

/* 辞書の読み込み (mozc.dict で数秒) を裏の thread で先に始める。
 * thread は preload_engine にだけ書き、Open が待ってから読む */
static pthread_t preload_thread;
static int preload_started;
static jamo_engine_t *preload_engine;
static char *preload_dict;

static void *preload_main(void *arg) {
  (void)arg;
  preload_engine = jamo_engine_open(preload_dict);
  return NULL;
}

/* VM の string を NUL で終わる文字列に写す (呼ぶ側が free) */
static char *dup_arg(const void *s) {
  size_t len = 0;
  const char *data = s ? api->string_data(s, &len) : NULL;
  if (!data)
    return NULL;
  char *out = (char *)malloc(len + 1);
  if (out) {
    memcpy(out, data, len);
    out[len] = '\0';
  }
  return out;
}

/* jamo の返す文字列 (次の呼び出しまで有効) を、bool と out の string にする */
// 戻り値は s があるか、out の引数 (string の slot を指す) は s の string
static void out_str(TvmVm *vm, const char *s, TvmSlot *rets, TvmSlot *out) {
  rets[0].i = s != NULL;
  *(void **)out->p = s ? api->string_new(vm, s, strlen(s)) : NULL;
}

#define FN(name)                                                               \
  static TvmStatus name(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user)
#define UNUSED (void)vm, (void)args, (void)user

FN(f_available) {
  UNUSED;
  rets[0].i = 1;
  return TVM_OK;
}

FN(f_preload) {
  UNUSED;
  if (engine || preload_started) {
    rets[0].i = 1;
    return TVM_OK;
  }
  preload_dict = dup_arg(args[0].p);
  preload_started = preload_dict && pthread_create(&preload_thread, NULL,
                                                   preload_main, NULL) == 0;
  if (!preload_started) {
    free(preload_dict);
    preload_dict = NULL;
  }
  rets[0].i = preload_started;
  return TVM_OK;
}

FN(f_open) {
  UNUSED;
  if (!engine && preload_started) {
    pthread_join(preload_thread, NULL); /* 読んでいる途中ならここで待つ */
    preload_started = 0;
    engine = preload_engine;
    free(preload_dict);
    preload_dict = NULL;
  }
  if (!engine) {
    char *path = dup_arg(args[0].p);
    if (path)
      engine = jamo_engine_open(path);
    free(path);
  }
  if (engine && !session)
    session = jamo_session_new(engine);
  rets[0].i = session != NULL;
  return TVM_OK;
}

FN(f_load_user_data) {
  UNUSED;
  char *path = session ? dup_arg(args[0].p) : NULL;
  rets[0].i = path && jamo_engine_load_user_data(engine, path) == JAMO_OK;
  free(path);
  return TVM_OK;
}

FN(f_save_user_data) {
  UNUSED;
  char *path = session ? dup_arg(args[0].p) : NULL;
  rets[0].i = path && jamo_engine_save_user_data(engine, path) == JAMO_OK;
  free(path);
  return TVM_OK;
}

FN(f_converting) {
  UNUSED;
  rets[0].i = session && jamo_session_state(session) == JAMO_STATE_CONVERTING;
  return TVM_OK;
}

FN(f_input) {
  UNUSED;
  char *text = session ? dup_arg(args[0].p) : NULL;
  rets[0].i = text && jamo_session_input(session, text) == JAMO_OK;
  free(text);
  return TVM_OK;
}

FN(f_backspace) {
  UNUSED;
  rets[0].i = session && jamo_session_backspace(session) == 1;
  return TVM_OK;
}

FN(f_delete_forward) {
  UNUSED;
  rets[0].i = session && jamo_session_delete_forward(session) == 1;
  return TVM_OK;
}

FN(f_cursor) {
  UNUSED;
  int32_t c = session ? jamo_session_cursor(session) : 0;
  rets[0].i = c < 0 ? 0 : c;
  return TVM_OK;
}

FN(f_move_cursor) {
  UNUSED;
  rets[0].i = session && jamo_session_move_cursor(session, args[0].i) == 1;
  return TVM_OK;
}

FN(f_set_cursor) {
  UNUSED;
  rets[0].i = session &&
              jamo_session_set_cursor(
                  session, args[0].i < 0 ? 0 : (uint32_t)args[0].i) == JAMO_OK;
  return TVM_OK;
}

FN(f_is_alpha) {
  UNUSED;
  rets[0].i = session && jamo_session_is_alpha(session) == 1;
  return TVM_OK;
}

FN(f_set_alpha) {
  UNUSED;
  rets[0].i =
      session && jamo_session_set_alpha(session, args[0].i != 0) == JAMO_OK;
  return TVM_OK;
}

FN(f_transform) {
  UNUSED;
  static const int32_t kinds[] = {
      JAMO_TRANSFORM_HIRAGANA, JAMO_TRANSFORM_KATAKANA,
      JAMO_TRANSFORM_HALF_ALPHA, JAMO_TRANSFORM_WIDE_ALPHA};
  int32_t k = args[0].i;
  rets[0].i = session && k >= 0 && k <= 3 &&
              jamo_session_transform(session, kinds[k]) == JAMO_OK;
  return TVM_OK;
}

FN(f_preedit) {
  UNUSED;
  out_str(vm, session ? jamo_session_preedit(session) : NULL, rets, &args[0]);
  return TVM_OK;
}

FN(f_focused_range) {
  UNUSED;
  uint32_t from = 0, len = 0;
  if (session)
    jamo_session_focused_range(session, &from, &len);
  *(int32_t *)args[0].p = (int32_t)from;
  *(int32_t *)args[1].p = (int32_t)len;
  return TVM_OK;
}

FN(f_convert) {
  UNUSED;
  rets[0].i =
      session && jamo_session_convert(session, (uint32_t)args[0].i) == JAMO_OK;
  return TVM_OK;
}

FN(f_clause_count) {
  UNUSED;
  rets[0].i = session ? (int32_t)jamo_session_clause_count(session) : 0;
  return TVM_OK;
}

FN(f_clause_yomi) {
  UNUSED;
  out_str(vm,
          session && args[0].i >= 0
              ? jamo_session_clause_yomi(session, (uint32_t)args[0].i)
              : NULL,
          rets, &args[1]);
  return TVM_OK;
}

FN(f_candidate_count) {
  UNUSED;
  int32_t n = session && args[0].i >= 0
                  ? jamo_session_candidate_count(session, (uint32_t)args[0].i)
                  : 0;
  rets[0].i = n < 0 ? 0 : n;
  return TVM_OK;
}

FN(f_candidate) {
  UNUSED;
  out_str(vm,
          session && args[0].i >= 0 && args[1].i >= 0
              ? jamo_session_candidate(session, (uint32_t)args[0].i,
                                       (uint32_t)args[1].i)
              : NULL,
          rets, &args[2]);
  return TVM_OK;
}

FN(f_selected) {
  UNUSED;
  int32_t sel = session && args[0].i >= 0
                    ? jamo_session_selected(session, (uint32_t)args[0].i)
                    : -1;
  rets[0].i = sel < 0 ? -1 : sel;
  return TVM_OK;
}

FN(f_select) {
  UNUSED;
  rets[0].i = session && args[0].i >= 0 && args[1].i >= 0 &&
              jamo_session_select(session, (uint32_t)args[0].i,
                                  (uint32_t)args[1].i) == JAMO_OK;
  return TVM_OK;
}

FN(f_focus) {
  UNUSED;
  int32_t f = session ? jamo_session_focus(session) : -1;
  rets[0].i = f < 0 ? -1 : f;
  return TVM_OK;
}

FN(f_set_focus) {
  UNUSED;
  rets[0].i = session && args[0].i >= 0 &&
              jamo_session_set_focus(session, (uint32_t)args[0].i) == JAMO_OK;
  return TVM_OK;
}

FN(f_resize) {
  UNUSED;
  rets[0].i = session && jamo_session_resize(session, args[0].i,
                                             (uint32_t)args[1].i) == JAMO_OK;
  return TVM_OK;
}

FN(f_revert) {
  UNUSED;
  rets[0].i = session && jamo_session_revert(session) == JAMO_OK;
  return TVM_OK;
}

FN(f_commit) {
  UNUSED;
  out_str(vm, session ? jamo_session_commit(session) : NULL, rets, &args[0]);
  return TVM_OK;
}

FN(f_commit_prefix) {
  UNUSED;
  out_str(vm, session ? jamo_session_commit_prefix(session) : NULL, rets,
          &args[0]);
  return TVM_OK;
}

FN(f_reset) {
  UNUSED;
  (void)rets;
  if (session)
    jamo_session_reset(session);
  return TVM_OK;
}

typedef struct Entry {
  const char *name, *params, *rets;
  TvmHostFn fn;
} Entry;

static const Entry entries[] = {
    {"jamo.available", "", "b", f_available},
    {"jamo.preload", "s", "b", f_preload},
    {"jamo.open", "s", "b", f_open},
    {"jamo.load_user_data", "s", "b", f_load_user_data},
    {"jamo.save_user_data", "s", "b", f_save_user_data},
    {"jamo.converting", "", "b", f_converting},
    {"jamo.input", "s", "b", f_input},
    {"jamo.backspace", "", "b", f_backspace},
    {"jamo.delete_forward", "", "b", f_delete_forward},
    {"jamo.cursor", "", "i", f_cursor},
    {"jamo.move_cursor", "i", "b", f_move_cursor},
    {"jamo.set_cursor", "i", "b", f_set_cursor},
    {"jamo.is_alpha", "", "b", f_is_alpha},
    {"jamo.set_alpha", "b", "b", f_set_alpha},
    {"jamo.transform", "i", "b", f_transform},
    {"jamo.preedit", "&s", "b", f_preedit},
    {"jamo.focused_range", "&i&i", "", f_focused_range},
    {"jamo.convert", "i", "b", f_convert},
    {"jamo.clause_count", "", "i", f_clause_count},
    {"jamo.clause_yomi", "i&s", "b", f_clause_yomi},
    {"jamo.candidate_count", "i", "i", f_candidate_count},
    {"jamo.candidate", "ii&s", "b", f_candidate},
    {"jamo.selected", "i", "i", f_selected},
    {"jamo.select", "ii", "b", f_select},
    {"jamo.focus", "", "i", f_focus},
    {"jamo.set_focus", "i", "b", f_set_focus},
    {"jamo.resize", "ii", "b", f_resize},
    {"jamo.revert", "", "b", f_revert},
    {"jamo.commit", "&s", "b", f_commit},
    {"jamo.commit_prefix", "&s", "b", f_commit_prefix},
    {"jamo.reset", "", "", f_reset},
};

LUB_VM_EXTENSION_EXPORT bool lub_vm_extension(const LubVmExtensionApi *a,
                                              TvmVm *vm) {
  if (a->version != LUB_VM_EXTENSION_VERSION)
    return false;
  api = a;
  for (size_t i = 0; i < sizeof entries / sizeof entries[0]; i++)
    if (a->host(vm, entries[i].name, entries[i].params, entries[i].rets,
                entries[i].fn, NULL) == 0)
      return false;
  return true;
}
