/* Type descriptors shared by the VM and tcs2c (docs/tcs/object-model.md §4,
   §5). Reads a byte sequence (v1) and determines the layout with C struct
   rules. */
#ifndef TCS_MODEL_H
#define TCS_MODEL_H

#include <stddef.h>
#include <stdint.h>

/* tcs2c embeds runtime/model into the generated C and makes it static with
 * TCS_MODEL_STATIC */
#ifdef TCS_MODEL_STATIC
#define TCS_MODEL_API static
#else
#define TCS_MODEL_API
#endif

/* Kinds of values (field / element / leaf) */
enum {
  TCS_K_I32 = 0,
  TCS_K_F32 = 1,
  TCS_K_BOOL = 2,
  TCS_K_REF = 3,
  TCS_K_STRUCT = 4
};

/* Kinds of types */
enum {
  TCS_T_CLASS = 1,  /* class and cell */
  TCS_T_STRUCT = 2, /* value types (struct / T? / tuple / KeyValuePair) */
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
/* External data class shared with the host: after the class prefix, a uint64_t
 * host_value aligned to 8 bytes (docs/tcs/object-model.md §3) */
#define TCS_TYPE_HOST_VALUE 4u

#define TCS_NO_TYPE 0xFFFF

typedef struct TcsField {
  const char *name;
  uint8_t kind;  /* TCS_K_* */
  uint16_t type; /* REF: the referenced type (TCS_NO_TYPE means any), STRUCT:
                    the struct type */
  uint32_t offset;
} TcsField;

/* Leaf: one value of an expanded struct (an operand of a VM instruction, a GC
 * reference location, one value of a snapshot) */
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
  uint16_t nleaves; /* fields with structs expanded (computed by layout) */
  TcsLeaf *leaves;
  uint32_t size;  /* class: payload size, struct: value size */
  uint32_t align; /* struct alignment */
  /* values of arrays / List / Dictionary, contents of a box */
  uint8_t elem_kind;
  uint16_t elem_type;
  uint32_t elem_size;
  /* Dictionary key */
  uint8_t key_kind;
  uint16_t key_type;
  uint32_t dict_entry; /* size of a Dictionary entry (decided and set by the
                          implementation) */
  /* function types */
  uint16_t nparams, nrets;
  TcsInfo *params, *rets;
  /* class: implemented interfaces and itable (used only by the VM) */
  uint16_t nifaces;
  uint16_t *ifaces;
  uint16_t nitable;
  TcsItable *itable;
} TcsType;

/* Read position. bad means reading too far or a malformed shape */
typedef struct TcsReader {
  const uint8_t *p, *end;
  int bad;
} TcsReader;

TCS_MODEL_API uint8_t tcs_rd_u8(TcsReader *r);
TCS_MODEL_API uint16_t tcs_rd_u16(TcsReader *r);
TCS_MODEL_API uint32_t tcs_rd_u32(TcsReader *r);
/* Whether n × unit bytes remain (avoids huge allocations) */
TCS_MODEL_API int tcs_rd_count_ok(TcsReader *r, uint32_t n, size_t unit);

/* Reads the type table. strings is the string table of names. On failure
 * returns a message (static) and leaves types in a state that can be freed */
TCS_MODEL_API const char *tcs_model_read(TcsReader *r,
                                         const char *const *strings,
                                         uint32_t nstrings, uint32_t *ntypes,
                                         TcsType **types);

/* layout (§5): offsets of fields / leaves, sizes, element sizes. A class
   payload starts after class_prefix bytes. ref_size is the size of a reference
 */
TCS_MODEL_API const char *tcs_model_layout(TcsType *types, uint32_t ntypes,
                                           uint32_t class_prefix,
                                           uint32_t ref_size);

TCS_MODEL_API void tcs_model_free(TcsType *types, uint32_t ntypes);

TCS_MODEL_API uint32_t tcs_kind_size(uint8_t kind, uint32_t ref_size);

/* String table (u32 count, then a u32 length and bytes for each). strings is
 * one allocation, freed with tcs_model_free_strings */
TCS_MODEL_API const char *tcs_model_read_strings(TcsReader *r, char ***strings,
                                                 uint32_t *n);
TCS_MODEL_API void tcs_model_free_strings(char **strings, uint32_t n);

#endif
