/* List of instructions. Keep the same order as the enum in tcsvm/Emitter/Op.cs
   (Op.cs is generated from this list). An order mismatch is stopped at load
   time by the FNV-1a of the name list (abi in the .tcsb header).

   Format (64-bit fixed length, doc/vm-design.md §7.1):
   - | op:8 | a:16 | b:16 | c:16 | x:8 |
   - Immediate form: imm32 is in bits 24..55 (the positions of b and c)
   - Branch: the relative offset is a signed 24-bit value in bits 40..63 (the
     positions of c and x). Counted from the next instruction

   Operand notation: a/b/c are registers, #c is an immediate, @c is a leaf index
   that link rewrites to a byte offset. The _I32 / _F32 / _B / _REF quadruples
   must always be in this order (the emitter selects one by adding the kind
   number) */
#define TVM_OPS(X)                                                             \
  X(NOP)                                                                       \
  X(MOV)     /* a = b */                                                       \
  X(LDK)     /* a = imm32 (i32 / f32 bits / bool) */                           \
  X(LDNULL)  /* a = null */                                                    \
  X(LDSTR)   /* a = constant string #imm */                                    \
  X(ADD_I32) /* a = b + c (wrap) */                                            \
  X(SUB_I32)                                                                   \
  X(MUL_I32)                                                                   \
  X(DIV_I32) /* Toward 0. Division by 0 and INT_MIN / -1 fault */              \
  X(REM_I32)                                                                   \
  X(AND_I32)                                                                   \
  X(OR_I32)                                                                    \
  X(XOR_I32)                                                                   \
  X(SHL_I32)  /* count & 31 */                                                 \
  X(SHR_I32)  /* Arithmetic */                                                 \
  X(NEG_I32)  /* a = -b */                                                     \
  X(BNOT_I32) /* a = ~b */                                                     \
  X(ADDI_I32) /* a = b + #c (c is a signed 16-bit value) */                    \
  X(CADD_I32) /* checked: out of range faults (C# OverflowException) */        \
  X(CSUB_I32)                                                                  \
  X(CMUL_I32)                                                                  \
  X(CNEG_I32)                                                                  \
  X(ADD_F32)                                                                   \
  X(SUB_F32)                                                                   \
  X(MUL_F32)                                                                   \
  X(DIV_F32)                                                                   \
  X(NEG_F32)                                                                   \
  X(FMOD_F32) /* a = fmodf(b, c) (float %) */                                  \
  X(I2F)      /* a = (float)b */                                               \
  X(F2I)      /* a = (int)b, toward 0. NaN and out of range fault */           \
  X(NOT_B)    /* a = !b */                                                     \
  X(AND_B)    /* a = b & c (bool, not short-circuit) */                        \
  X(OR_B)                                                                      \
  X(NE_B)   /* a = b != c (bool ^) */                                          \
  X(NVAL)   /* Faults if a (has of T?) is false (.Value with no value) */      \
  X(EQ_I32) /* a = b == c (bool) */                                            \
  X(LT_I32)                                                                    \
  X(LE_I32)                                                                    \
  X(EQ_F32)                                                                    \
  X(LT_F32)                                                                    \
  X(LE_F32)                                                                    \
  X(EQ_B)                                                                      \
  X(EQ_REF)  /* identity */                                                    \
  X(EQ_STR)  /* Contents (two nulls are equal) */                              \
  X(CMP_I32) /* a = b.CompareTo(c) (-1 / 0 / 1) */                             \
  X(CMP_F32) /* .NET float.CompareTo (NaN is the smallest, two NaNs give 0) */ \
  X(JMP)     /* Unconditional */                                               \
  X(JT)      /* If a is true */                                                \
  X(JF)      /* If a is false */                                               \
  X(JNULL)   /* If a is null */                                                \
  X(JNN)     /* If a is not null */                                            \
  X(JT_LT_I32) /* If a < b */                                                  \
  X(JF_LT_I32) /* If !(a < b) */                                               \
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
  X(LDS)     /* a = static[#imm] */                                            \
  X(STS)     /* static[#imm] = a */                                            \
  X(NEW)     /* a = new type #c (class or cell; leaves are 0 / null) */        \
  X(NNULL)   /* Faults if a is null (before an instance method call) */        \
  X(LDF_I32) /* a = b.@c */                                                    \
  X(LDF_F32)                                                                   \
  X(LDF_B)                                                                     \
  X(LDF_REF)                                                                   \
  X(STF_I32) /* a.@c = b */                                                    \
  X(STF_F32)                                                                   \
  X(STF_B)                                                                     \
  X(STF_REF)                                                                   \
  X(NEWARR)  /* a = new array type #c [length b] */                            \
  X(ARR_LEN) /* a = b.Length */                                                \
  X(LDE_I32) /* a = b[c] (bounds-checked) */                                   \
  X(LDE_F32)                                                                   \
  X(LDE_B)                                                                     \
  X(LDE_REF)                                                                   \
  X(STE_I32) /* a[b] = c */                                                    \
  X(STE_F32)                                                                   \
  X(STE_B)                                                                     \
  X(STE_REF)                                                                   \
  X(LDEF_I32) /* a = b[c].@x (leaf of a struct element) */                     \
  X(LDEF_F32)                                                                  \
  X(LDEF_B)                                                                    \
  X(LDEF_REF)                                                                  \
  X(STEF_I32) /* a[b].@x = c */                                                \
  X(STEF_F32)                                                                  \
  X(STEF_B)                                                                    \
  X(STEF_REF)                                                                  \
  X(NEWLIST)     /* a = new List type #c */                                    \
  X(LIST_LEN)    /* a = b.Count */                                             \
  X(LIST_LD_I32) /* a = b[c] (bounds-checked) */                               \
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
  X(LIST_SORT)    /* a.Sort(b) (b is the comparison closure) */                \
  X(LIST_LDF_I32) /* a = b[c].@x (leaf of a struct element) */                 \
  X(LIST_LDF_F32)                                                              \
  X(LIST_LDF_B)                                                                \
  X(LIST_LDF_REF)                                                              \
  X(LIST_STF_I32) /* a[b].@x = c */                                            \
  X(LIST_STF_F32)                                                              \
  X(LIST_STF_B)                                                                \
  X(LIST_STF_REF)                                                              \
  X(LIST_PUSH0)   /* Adds one zero element to a (a struct Add adds first, then \
                     writes the leaves) */                                     \
  X(LIST_INSERT0) /* Inserts a zero element into a at position b (b is         \
                     0..Count) */                                              \
  X(LIST_REMOVE_AT) /* a.RemoveAt(b) */                                        \
  X(LIST_CLEAR)     /* a.Clear() */                                            \
  X(NEWDICT)        /* a = new Dictionary type #c */                           \
  X(DICT_LEN)       /* a = b.Count */                                          \
  X(DICT_FIND) /* a = index of the entry for key c in b (a missing key faults) \
                */                                                             \
  X(DICT_INS)  /* a = index of the entry for key c in b (added if missing; the \
                  value is 0) */                                               \
  X(DICT_LDV_I32) /* a = value leaf @x of entry c in b */                      \
  X(DICT_LDV_F32)                                                              \
  X(DICT_LDV_B)                                                                \
  X(DICT_LDV_REF)                                                              \
  X(DICT_STV_I32) /* value leaf @x of entry b in a = c */                      \
  X(DICT_STV_F32)                                                              \
  X(DICT_STV_B)                                                                \
  X(DICT_STV_REF)                                                              \
  X(DICT_LDK)    /* a = key of entry c in b */                                 \
  X(DICT_USED)   /* a = length of the entry list of b (including removed       \
                    entries; for enumeration) */                               \
  X(DICT_LIVE)   /* a = whether entry c of b is live */                        \
  X(DICT_HAS)    /* a = b.ContainsKey(c) */                                    \
  X(DICT_REMOVE) /* a = b.Remove(c) */                                         \
  X(ISTYPE)  /* a = whether the runtime type of b is #c (false for null) */    \
  X(CAST)    /* a = (type #c) b. Faults on mismatch (null passes) */           \
  X(ASCAST)  /* a = b as type #c (null on mismatch) */                         \
  X(BOX_I32) /* a = (object) b (box type #c) */                                \
  X(BOX_F32)                                                                   \
  X(BOX_B)                                                                     \
  X(UNBOX_I32) /* a = (int) b (faults unless it is box type #c) */             \
  X(UNBOX_F32)                                                                 \
  X(UNBOX_B)                                                                   \
  X(EQ_REC)  /* a = b == c (value equality by runtime type; records field by   \
                field) */                                                      \
  X(CLONE)   /* a = shallow copy of b (record with) */                         \
  X(CHR)     /* a = 1-byte string (b is 0..255) */                             \
  X(CALLR)   /* Calls runtime function #c with the arguments in a.., return    \
                values in b.. */                                               \
  X(CLOSURE) /* a = closure (function #c, captures in registers b..) */        \
  X(LDCAP)   /* a = capture #c (b is its own closure) */                       \
  X(CALL)    /* Calls function #c with the arguments in a.., return values go  \
                into b.. */                                                    \
  X(CALLD)   /* Calls the closure in a with the arguments in a+1.., return     \
                values in b.., #c is the function type */                      \
  X(CALLI) /* Calls interface method #c (selector) with a (receiver).., return \
              values in b.. */                                                 \
  X(RET)   /* Returns the values in a.. */                                     \
  X(RET0)                                                                      \
  X(CONCAT)    /* a = b + c (string; null is the empty string) */              \
  X(I2S)       /* a = b.ToString() (int) */                                    \
  X(F2S)       /* a = b.ToString() (float, runtime/fmt) */                     \
  X(B2S)       /* a = "True" / "False" */                                      \
  X(PRINT_I32) /* Console.WriteLine(int) */                                    \
  X(PRINT_F32) /* Console.WriteLine(float) */                                  \
  X(PRINT_B)   /* Console.WriteLine(bool) */                                   \
  X(PRINT_S)   /* Console.WriteLine(string) */                                 \
  X(TRACE)     /* --trace: marks that line #imm was passed (index into the     \
                  module's trace line table) */                                \
  X(TVAL) /* --trace: records assigned value leaf #b (register a) to value     \
             location #c */

enum {
#define TVM_OP_ENUM(name) TVM_OP_##name,
  TVM_OPS(TVM_OP_ENUM)
#undef TVM_OP_ENUM
      TVM_OP_COUNT
};

/* Kinds of registers, statics, and leaves (doc/vm-design.md §6.1) */
enum { TVM_K_I32 = 0, TVM_K_F32 = 1, TVM_K_BOOL = 2, TVM_K_REF = 3 };

/* Type kinds */
enum {
  TVM_T_CLASS = 1,  /* class and cell (box for a captured variable) */
  TVM_T_STRUCT = 2, /* Used only for the layout of array elements */
  TVM_T_ARRAY = 3,
  TVM_T_STRING = 4,
  TVM_T_LIST = 5,
  TVM_T_DICT = 6,
  TVM_T_FUNC =
      7, /* closure type (sequence of argument and return value leaves) */
  TVM_T_IFACE =
      8, /* interface. The value is an object of an implementing class */
  TVM_T_OBJECT =
      9,          /* object. The value is any object (value types are boxed) */
  TVM_T_BOX = 10, /* int / float / bool stored in an object */
};

/* class flags */
#define TVM_CLASS_RECORD 1

/* Element kind when the array element type is a struct */
#define TVM_K_STRUCT 4

#define TVM_NO_TYPE 0xFFFF
