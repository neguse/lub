/* VM と tcs2c が共有する型記述子 (doc/object-model.md §4、§5)。
   byte 列 (v1) を読み、C の struct の規則で layout を決める。 */
#ifndef TCS_MODEL_H
#define TCS_MODEL_H

#include <stddef.h>
#include <stdint.h>

/* 値の種類 (field / 要素 / 葉) */
enum { TCS_K_I32 = 0, TCS_K_F32 = 1, TCS_K_BOOL = 2, TCS_K_REF = 3, TCS_K_STRUCT = 4 };

/* 型の種類 */
enum {
  TCS_T_CLASS = 1,  /* class と cell */
  TCS_T_STRUCT = 2, /* 値型 (struct / T? / tuple / KeyValuePair) */
  TCS_T_ARRAY = 3,
  TCS_T_STRING = 4,
  TCS_T_LIST = 5,
  TCS_T_DICT = 6,
  TCS_T_FUNC = 7,
  TCS_T_IFACE = 8,
  TCS_T_OBJECT = 9,
  TCS_T_BOX = 10,
};

/* flags */
#define TCS_TYPE_RECORD 1u
#define TCS_TYPE_HAS_ON_RELOAD 2u

#define TCS_NO_TYPE 0xFFFF

typedef struct TcsField {
  const char *name;
  uint8_t kind;  /* TCS_K_* */
  uint16_t type; /* REF: 参照先 (TCS_NO_TYPE は任意)、STRUCT: struct の型 */
  uint32_t offset;
} TcsField;

/* 葉: struct を展開した 1 値 (VM の命令の operand、GC の参照の場所、snapshot の 1 値) */
typedef struct TcsLeaf {
  uint8_t kind;
  uint16_t type;
  uint32_t offset;
} TcsLeaf;

typedef struct TcsInfo {
  uint8_t kind;
  uint16_t type;
} TcsInfo;

typedef struct TcsItable {
  uint16_t selector;
  uint16_t func;
} TcsItable;

typedef struct TcsType {
  uint8_t tkind;
  const char *name;
  uint32_t layout_hash;
  uint8_t flags;
  /* class / struct */
  uint16_t nfields;
  TcsField *fields;
  uint16_t nleaves; /* field を struct まで展開した列 (layout が求める) */
  TcsLeaf *leaves;
  uint32_t size;  /* class: payload の大きさ、struct: 値の大きさ */
  uint32_t align; /* struct の alignment */
  /* 配列 / List / Dictionary の値、box の中 */
  uint8_t elem_kind;
  uint16_t elem_type;
  uint32_t elem_size;
  /* Dictionary の key */
  uint8_t key_kind;
  uint16_t key_type;
  uint32_t dict_entry; /* Dictionary の entry の大きさ (実装が決めて入れる) */
  /* 関数型 */
  uint16_t nparams, nrets;
  TcsInfo *params, *rets;
  /* class: 実装する interface と itable (VM だけが使う) */
  uint16_t nifaces;
  uint16_t *ifaces;
  uint16_t nitable;
  TcsItable *itable;
} TcsType;

/* 読む位置。bad は読み過ぎ・形の誤り */
typedef struct TcsReader {
  const uint8_t *p, *end;
  int bad;
} TcsReader;

uint8_t tcs_rd_u8(TcsReader *r);
uint16_t tcs_rd_u16(TcsReader *r);
uint32_t tcs_rd_u32(TcsReader *r);
/* n 個 × unit byte が残っているか (巨大な確保を避ける) */
int tcs_rd_count_ok(TcsReader *r, uint32_t n, size_t unit);

/* 型の表を読む。strings は名前の string 表。失敗したら message (static) を返し、types は解放してよい形のまま */
const char *tcs_model_read(TcsReader *r, const char *const *strings, uint32_t nstrings, uint32_t *ntypes,
                           TcsType **types);

/* layout (§5): field / 葉の offset、大きさ、要素の大きさ。class の payload は class_prefix byte の後から。
   ref_size は参照の大きさ */
const char *tcs_model_layout(TcsType *types, uint32_t ntypes, uint32_t class_prefix, uint32_t ref_size);

void tcs_model_free(TcsType *types, uint32_t ntypes);

uint32_t tcs_kind_size(uint8_t kind, uint32_t ref_size);

#endif
