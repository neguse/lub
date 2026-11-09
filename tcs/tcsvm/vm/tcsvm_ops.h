/* 命令の一覧。tcsvm/Emitter/Op.cs の enum と同じ順に並べる (Op.cs はこの一覧から作る)。
   並びの食い違いは、名前の列の FNV-1a (.tcsb の header の abi) で読み込み時に止める。

   形式 (64 bit 固定長、doc/vm-design.md §7.1):
     | op:8 | a:16 | b:16 | c:16 | x:8 |
     即値形: imm32 は bit 24..55 (b と c の位置)
     分岐: 相対 offset は bit 40..63 の符号付き 24 bit (c と x の位置)。次の命令からの数

   operand の書き方: a/b/c は register、#c は即値、@c は link で byte offset に書き換わる葉の番号。
   _I32 / _F32 / _B / _REF の 4 つ組は必ずこの順に並べる (emitter が種類の番号を足して選ぶ) */
#define TVM_OPS(X)                                                             \
  X(NOP)                                                                       \
  X(MOV)        /* a = b */                                                    \
  X(LDK)        /* a = imm32 (i32 / f32 の bit / bool) */                      \
  X(LDNULL)     /* a = null */                                                 \
  X(LDSTR)      /* a = 定数の string #imm */                                    \
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
  X(CADD_I32)   /* checked: 範囲外は fault (C# の OverflowException) */         \
  X(CSUB_I32)                                                                  \
  X(CMUL_I32)                                                                  \
  X(CNEG_I32)                                                                  \
  X(ADD_F32)                                                                   \
  X(SUB_F32)                                                                   \
  X(MUL_F32)                                                                   \
  X(DIV_F32)                                                                   \
  X(NEG_F32)                                                                   \
  X(FMOD_F32)   /* a = fmodf(b, c) (float の %) */                              \
  X(I2F)        /* a = (float)b */                                             \
  X(F2I)        /* a = (int)b、0 方向。NaN と範囲外は fault */                  \
  X(NOT_B)      /* a = !b */                                                   \
  X(AND_B)      /* a = b & c (bool、短絡しない) */                              \
  X(OR_B)                                                                      \
  X(NE_B)       /* a = b != c (bool の ^) */                                    \
  X(NVAL)       /* a (T? の has) が false なら fault (.Value の値なし) */         \
  X(EQ_I32)     /* a = b == c (bool) */                                        \
  X(LT_I32)                                                                    \
  X(LE_I32)                                                                    \
  X(EQ_F32)                                                                    \
  X(LT_F32)                                                                    \
  X(LE_F32)                                                                    \
  X(EQ_B)                                                                      \
  X(EQ_REF)     /* identity */                                                 \
  X(EQ_STR)     /* 内容 (null どうしは等しい) */                                \
  X(CMP_I32)    /* a = b.CompareTo(c) (-1 / 0 / 1) */                          \
  X(CMP_F32)    /* .NET の float.CompareTo (NaN は最小、NaN どうしは 0) */       \
  X(JMP)        /* 無条件 */                                                    \
  X(JT)         /* a が true なら */                                           \
  X(JF)         /* a が false なら */                                          \
  X(JNULL)      /* a が null なら */                                           \
  X(JNN)        /* a が null でなければ */                                     \
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
  X(NEW)        /* a = new 型 #c (class か cell。葉は 0 / null) */             \
  X(NNULL)      /* a が null なら fault (instance method の呼び出しの前) */      \
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
  X(NEWLIST)    /* a = new List 型 #c */                                       \
  X(LIST_LEN)   /* a = b.Count */                                              \
  X(LIST_LD_I32) /* a = b[c] (範囲検査) */                                     \
  X(LIST_LD_F32)                                                               \
  X(LIST_LD_B)                                                                 \
  X(LIST_LD_REF)                                                               \
  X(LIST_ST_I32) /* a[b] = c */                                                \
  X(LIST_ST_F32)                                                               \
  X(LIST_ST_B)                                                                 \
  X(LIST_ST_REF)                                                               \
  X(LIST_ADD_I32) /* a.Add(b) */                                               \
  X(LIST_ADD_F32)                                                              \
  X(LIST_ADD_B)                                                                \
  X(LIST_ADD_REF)                                                              \
  X(LIST_SORT)  /* a.Sort(b) (b は比較の closure) */                           \
  X(LIST_LDF_I32) /* a = b[c].@x (struct の要素の葉) */                          \
  X(LIST_LDF_F32)                                                              \
  X(LIST_LDF_B)                                                                \
  X(LIST_LDF_REF)                                                              \
  X(LIST_STF_I32) /* a[b].@x = c */                                            \
  X(LIST_STF_F32)                                                              \
  X(LIST_STF_B)                                                                \
  X(LIST_STF_REF)                                                              \
  X(LIST_PUSH0) /* a に 0 の要素を 1 つ足す (struct の Add は足してから葉を書く) */ \
  X(LIST_INSERT0) /* a の b の位置に 0 の要素を入れる (b は 0..Count) */          \
  X(LIST_REMOVE_AT) /* a.RemoveAt(b) */                                        \
  X(LIST_CLEAR) /* a.Clear() */                                                \
  X(NEWDICT)    /* a = new Dictionary 型 #c */                                 \
  X(DICT_LEN)   /* a = b.Count */                                              \
  X(DICT_FIND)  /* a = b の key c の entry の番号 (無い key は fault) */          \
  X(DICT_INS)   /* a = b の key c の entry の番号 (無ければ足す。値は 0) */       \
  X(DICT_LDV_I32) /* a = b の entry c の値の葉 @x */                            \
  X(DICT_LDV_F32)                                                              \
  X(DICT_LDV_B)                                                                \
  X(DICT_LDV_REF)                                                              \
  X(DICT_STV_I32) /* a の entry b の値の葉 @x = c */                            \
  X(DICT_STV_F32)                                                              \
  X(DICT_STV_B)                                                                \
  X(DICT_STV_REF)                                                              \
  X(DICT_LDK)   /* a = b の entry c の key */                                  \
  X(DICT_USED)  /* a = b の entry の列の長さ (消した entry を含む。列挙用) */      \
  X(DICT_LIVE)  /* a = b の entry c が生きているか */                            \
  X(DICT_HAS)   /* a = b.ContainsKey(c) */                                     \
  X(DICT_REMOVE) /* a = b.Remove(c) */                                         \
  X(ISTYPE)     /* a = b の実行時の型が #c (null は false) */                    \
  X(CAST)       /* a = (型 #c) b。合わなければ fault (null は通す) */            \
  X(ASCAST)     /* a = b as 型 #c (合わなければ null) */                        \
  X(BOX_I32)    /* a = (object) b (box の型 #c) */                             \
  X(BOX_F32)                                                                   \
  X(BOX_B)                                                                     \
  X(UNBOX_I32)  /* a = (int) b (box の型 #c でなければ fault) */                 \
  X(UNBOX_F32)                                                                 \
  X(UNBOX_B)                                                                   \
  X(EQ_REC)     /* a = b == c (実行時の型の値の等価。record は field ごと) */      \
  X(CLONE)      /* a = b の shallow copy (record の with) */                    \
  X(CHR)        /* a = 1 byte の string (b は 0..255) */                        \
  X(CALLR)      /* runtime の関数 #c を a.. の引数で呼ぶ。戻り値は b.. */         \
  X(CLOSURE)    /* a = closure (関数 #c、捕捉は b.. の register) */            \
  X(LDCAP)      /* a = 捕捉 #c (b は自分の closure) */                         \
  X(CALL)       /* 関数 #c を、a.. の引数で呼ぶ。戻り値は b.. に入る */         \
  X(CALLD)      /* a の closure を、a+1.. の引数で呼ぶ。戻り値は b..、#c は関数型 */ \
  X(CALLI)      /* interface の method #c (selector) を a (receiver).. で呼ぶ。戻り値は b.. */ \
  X(RET)        /* a.. の値を返す */                                           \
  X(RET0)                                                                      \
  X(CONCAT)     /* a = b + c (string。null は空文字列) */                       \
  X(I2S)        /* a = b.ToString() (int) */                                   \
  X(F2S)        /* a = b.ToString() (float、runtime/fmt) */                    \
  X(B2S)        /* a = "True" / "False" */                                     \
  X(PRINT_I32)  /* Console.WriteLine(int) */                                   \
  X(PRINT_F32)  /* Console.WriteLine(float) */                                 \
  X(PRINT_B)    /* Console.WriteLine(bool) */                                  \
  X(PRINT_S)    /* Console.WriteLine(string) */                                \
  X(TRACE)      /* --trace: 行 #imm を通った印 (module の trace の行の表の添字) */ \
  X(TVAL)       /* --trace: 代入の値の葉 #b (a の register) を値の場所 #c に控える */

enum {
#define TVM_OP_ENUM(name) TVM_OP_##name,
  TVM_OPS(TVM_OP_ENUM)
#undef TVM_OP_ENUM
      TVM_OP_COUNT
};

/* register と static と葉の種類 (doc/vm-design.md §6.1) */
enum { TVM_K_I32 = 0, TVM_K_F32 = 1, TVM_K_BOOL = 2, TVM_K_REF = 3 };

/* 型の種類 */
enum {
  TVM_T_CLASS = 1,  /* class と cell (捕捉される変数の箱) */
  TVM_T_STRUCT = 2, /* 配列の要素の layout だけに使う */
  TVM_T_ARRAY = 3,
  TVM_T_STRING = 4,
  TVM_T_LIST = 5,
  TVM_T_DICT = 6,
  TVM_T_FUNC = 7,   /* closure の型 (引数と戻り値の葉の列) */
  TVM_T_IFACE = 8,  /* interface。値は実装する class の object */
  TVM_T_OBJECT = 9, /* object。値は任意の object (値型は box) */
  TVM_T_BOX = 10,   /* object に入った int / float / bool */
};

/* class の flags */
#define TVM_CLASS_RECORD 1

/* 配列の要素型が struct のときの要素の種類 */
#define TVM_K_STRUCT 4

#define TVM_NO_TYPE 0xFFFF
