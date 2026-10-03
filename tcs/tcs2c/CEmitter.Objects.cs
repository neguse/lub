using TinyCs;

namespace TinyCs.Tcs2c;

// `object` (CType.Object = void *): 参照型はそのまま、int / float / bool は
// TcsBox に box する。cast の検査は GC header の type_id (class は tcs_init_C、
// string は tcs_string_new、box は tcs_box_*、配列 / List / Dict / closure は
// box 時に tcs_typed で構造型 id を付ける) で行う。interface は実装 class の
// 集合を切り替える tcs_is_I で判定する。
internal sealed partial class CEmitter
{
    private readonly Dictionary<string, uint> _runtimeTypeIds = new();

    private static string TypeKey(CType type) => $"{type.Kind}:{type.Name}" +
        (type.Element is null ? "" : $"<{TypeKey(type.Element)}>") +
        (type.Key is null ? "" : $"[{TypeKey(type.Key)}]") +
        (type.Parameters is null ? "" : $"({string.Join(",", type.Parameters.Select(TypeKey))})");

    // 実行時型 tag。class は DFS 範囲の先頭、構造型は program 内で intern
    private string RuntimeTypeId(CType type)
    {
        if (type == CType.String) return "TCS_TYPE_STRING";
        if (type == CType.I32) return "TCS_TYPE_BOX_I32";
        if (type == CType.F32) return "TCS_TYPE_BOX_F32";
        if (type == CType.Bool) return "TCS_TYPE_BOX_BOOL";
        if (type.Kind == CTypeKind.Nullable) return RuntimeTypeId(type.Element!);
        if (type.Kind == CTypeKind.Ref) return Names.TypeId(type.Name!);
        var key = TypeKey(type);
        if (!_runtimeTypeIds.TryGetValue(key, out var id))
            _runtimeTypeIds[key] = id = 0x80000000U + (uint)_runtimeTypeIds.Count;
        return $"UINT32_C({id})";
    }

    private static string BoxField(CType scalar) => scalar.Kind switch
    {
        CTypeKind.I32 => "i",
        CTypeKind.F32 => "f",
        CTypeKind.Bool => "b",
        _ => throw new Tcs2cException($"cannot box {scalar}"),
    };

    private static string BoxFunction(CType scalar) => scalar.Kind switch
    {
        CTypeKind.I32 => "tcs_box_i32",
        CTypeKind.F32 => "tcs_box_f32",
        CTypeKind.Bool => "tcs_box_bool",
        _ => throw new Tcs2cException($"cannot box {scalar}"),
    };

    // 値 → object
    private string RenderBox(IlExpr value)
    {
        var type = TypeOf(value);
        if (type == CType.Object) return RenderExpr(value);
        if (type == CType.Null) return "NULL";
        if (type.Kind is CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool)
            return $"{BoxFunction(type)}({RenderExpr(value)})";
        if (type.Kind == CTypeKind.Nullable
            && type.Element!.Kind is CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool)
        {
            var temp = Temp("opt");
            return $"({{ {type.CName} {temp} = {RenderExpr(value)}; " +
                $"{temp}.has ? {BoxFunction(type.Element)}({temp}.v) : NULL; }})";
        }
        if (type.Kind is CTypeKind.String or CTypeKind.Ref)
            return $"((void *){RenderExpr(value)})";
        if (type.IsNullable)
            return $"tcs_typed({RenderExpr(value)}, {RuntimeTypeId(type)})";
        throw new Tcs2cException($"cannot box {type}");
    }

    // object → スカラ (型が一致する box だけ)
    private string RenderUnbox(IlExpr value, CType target) =>
        $"(tcs_unbox({RenderExpr(value)}, {RuntimeTypeId(target)})->value.{BoxField(target)})";

    // interface ごとの実装判定 (実行時型 tag の switch)
    private void EmitInterfaceChecks()
    {
        foreach (var iface in _program.Classes.Where(c => c.IsInterface))
        {
            Line($"static bool {Names.InterfaceCheck(iface.Name)}(void *object)");
            Line("{");
            _indent++;
            Line("if (object == NULL) return false;");
            Line("switch (TCS_GC_HEADER(object)->type_id) {");
            foreach (var cls in _program.Classes.Where(c => !c.IsInterface
                && IsAncestorOrSame(iface.Name, c.Name)))
                Line($"case {Names.TypeId(cls.Name)}: return true;");
            Line("default: return false;");
            Line("}");
            _indent--;
            Line("}");
            Line();
        }
    }
}
