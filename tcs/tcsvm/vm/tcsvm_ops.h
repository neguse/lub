/* 命令の一覧。tcsvm/Emitter/Op.cs の enum と同じ順に並べる。
   並びの食い違いは、名前の列の FNV-1a (.tcsb の header の abi) で読み込み時に止める。

   形式 (64 bit 固定長、doc/vm-design.md §7.1):
     | op:8 | a:16 | b:16 | c:16 | x:8 |
     即値形: imm32 は bit 24..55 (b と c の位置)
     分岐: 相対 offset は bit 40..63 の符号付き 24 bit (c と x の位置)。次の命令からの数

   operand の書き方: a/b/c は register、#c は即値、@c は link で byte offset に書き換わる field の番号 */
#define TVM_OPS(X)                                                             \
  X(NOP)                                                                       \
  X(MOV)        /* a = b */                                                    \
  X(LDK)        /* a = imm32 (i32 / f32 の bit / bool) */                      \
  X(LDNULL)     /* a = null */                                                 \
  X(ADD_I32)    /* a = b + c (wrap) */                                         \
  X(SUB_I32)                                                                   \
  X(MUL_I32)                                                                   \
  X(DIV_I32)    /* 0 方向。0 除算と INT_MIN / -1 は fault */                    \
  X(REM_I32)                                                                   \
  X(AND_I32)                                                                   \
  X(OR_I32)                                                                    \
  X(XOR_I32)                                                                   \
  X(SHL_I32)    /* count & 31 */                                               \
  X(SHR_I32)    /* 算術 */                                                     \
  X(NEG_I32)    /* a = -b */                                                   \
  X(BNOT_I32)   /* a = ~b */                                                   \
  X(ADDI_I32)   /* a = b + #c (c は符号付き 16 bit) */                          \
  X(ADD_F32)                                                                   \
  X(SUB_F32)                                                                   \
  X(MUL_F32)                                                                   \
  X(DIV_F32)                                                                   \
  X(NEG_F32)                                                                   \
  X(I2F)        /* a = (float)b */                                             \
  X(F2I)        /* a = (int)b、0 方向。NaN と範囲外は fault */                  \
  X(NOT_B)      /* a = !b */                                                   \
  X(EQ_I32)     /* a = b == c (bool) */                                        \
  X(LT_I32)                                                                    \
  X(LE_I32)                                                                    \
  X(EQ_F32)                                                                    \
  X(LT_F32)                                                                    \
  X(LE_F32)                                                                    \
  X(EQ_B)                                                                      \
  X(EQ_REF)                                                                    \
  X(JMP)        /* 無条件 */                                                    \
  X(JT)         /* a が true なら */                                           \
  X(JF)         /* a が false なら */                                          \
  X(JT_LT_I32)  /* a < b なら */                                               \
  X(JF_LT_I32)  /* !(a < b) なら */                                            \
  X(JT_LE_I32)                                                                 \
  X(JF_LE_I32)                                                                 \
  X(JT_EQ_I32)                                                                 \
  X(JF_EQ_I32)                                                                 \
  X(JT_LT_F32)                                                                 \
  X(JF_LT_F32)                                                                 \
  X(JT_LE_F32)                                                                 \
  X(JF_LE_F32)                                                                 \
  X(JT_EQ_F32)                                                                 \
  X(JF_EQ_F32)                                                                 \
  X(LDS)        /* a = static[#imm] */                                         \
  X(STS)        /* static[#imm] = a */                                         \
  X(NEW)        /* a = new 型 #c (field は 0 / null) */                         \
  X(LDF_I32)    /* a = b.@c */                                                 \
  X(LDF_F32)                                                                   \
  X(LDF_B)                                                                     \
  X(LDF_REF)                                                                   \
  X(STF_I32)    /* a.@c = b */                                                 \
  X(STF_F32)                                                                   \
  X(STF_B)                                                                     \
  X(STF_REF)                                                                   \
  X(NEWARR)     /* a = new 配列型 #c [長さ b] */                                \
  X(ARR_LEN)    /* a = b.Length */                                             \
  X(LDE_I32)    /* a = b[c] (範囲検査) */                                       \
  X(LDE_F32)                                                                   \
  X(LDE_B)                                                                     \
  X(LDE_REF)                                                                   \
  X(STE_I32)    /* a[b] = c */                                                 \
  X(STE_F32)                                                                   \
  X(STE_B)                                                                     \
  X(STE_REF)                                                                   \
  X(LDEF_I32)   /* a = b[c].@x (struct の要素の葉) */                           \
  X(LDEF_F32)                                                                  \
  X(LDEF_B)                                                                    \
  X(LDEF_REF)                                                                  \
  X(STEF_I32)   /* a[b].@x = c */                                              \
  X(STEF_F32)                                                                  \
  X(STEF_B)                                                                    \
  X(STEF_REF)                                                                  \
  X(CALL)       /* 関数 #c を、a.. の引数で呼ぶ。戻り値は b.. に入る */         \
  X(RET)        /* a.. の値を返す */                                           \
  X(RET0)                                                                      \
  X(PRINT_I32)  /* Console.WriteLine(int) */                                   \
  X(PRINT_F32)  /* Console.WriteLine(float) (runtime/fmt) */                   \
  X(PRINT_B)    /* Console.WriteLine(bool) */

enum {
#define TVM_OP_ENUM(name) TVM_OP_##name,
  TVM_OPS(TVM_OP_ENUM)
#undef TVM_OP_ENUM
      TVM_OP_COUNT
};

/* register と static と葉の種類 (doc/vm-design.md §6.1) */
enum { TVM_K_I32 = 0, TVM_K_F32 = 1, TVM_K_BOOL = 2, TVM_K_REF = 3 };

/* 型の種類 */
enum { TVM_T_CLASS = 1, TVM_T_STRUCT = 2, TVM_T_ARRAY = 3 };

/* 配列の要素型が struct のときの要素の種類 */
#define TVM_K_STRUCT 4

#define TVM_NO_TYPE 0xFFFF
