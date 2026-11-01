namespace TinyCs.Vm;

/// <summary>命令の一覧。tcsvm/vm/tcsvm_ops.h の TVM_OPS と同じ順 (形式は同 header の注記)。</summary>
internal enum Op : byte
{
    NOP,
    MOV,
    LDK,
    LDNULL,
    ADD_I32,
    SUB_I32,
    MUL_I32,
    DIV_I32,
    REM_I32,
    AND_I32,
    OR_I32,
    XOR_I32,
    SHL_I32,
    SHR_I32,
    NEG_I32,
    BNOT_I32,
    ADDI_I32,
    ADD_F32,
    SUB_F32,
    MUL_F32,
    DIV_F32,
    NEG_F32,
    I2F,
    F2I,
    NOT_B,
    EQ_I32,
    LT_I32,
    LE_I32,
    EQ_F32,
    LT_F32,
    LE_F32,
    EQ_B,
    EQ_REF,
    JMP,
    JT,
    JF,
    JT_LT_I32,
    JF_LT_I32,
    JT_LE_I32,
    JF_LE_I32,
    JT_EQ_I32,
    JF_EQ_I32,
    JT_LT_F32,
    JF_LT_F32,
    JT_LE_F32,
    JF_LE_F32,
    JT_EQ_F32,
    JF_EQ_F32,
    LDS,
    STS,
    NEW,
    LDF_I32,
    LDF_F32,
    LDF_B,
    LDF_REF,
    STF_I32,
    STF_F32,
    STF_B,
    STF_REF,
    NEWARR,
    ARR_LEN,
    LDE_I32,
    LDE_F32,
    LDE_B,
    LDE_REF,
    STE_I32,
    STE_F32,
    STE_B,
    STE_REF,
    LDEF_I32,
    LDEF_F32,
    LDEF_B,
    LDEF_REF,
    STEF_I32,
    STEF_F32,
    STEF_B,
    STEF_REF,
    CALL,
    RET,
    RET0,
    PRINT_I32,
    PRINT_F32,
    PRINT_B,
}

internal static class OpAbi
{
    /// <summary>名前の列の FNV-1a。VM が読み込み時に自分の一覧と比べる。</summary>
    public static uint Hash()
    {
        var h = 2166136261u;
        foreach (var name in Enum.GetNames<Op>())
        {
            foreach (var ch in name) h = (h ^ (byte)ch) * 16777619u;
            h = (h ^ (byte)',') * 16777619u;
        }
        return h;
    }
}
