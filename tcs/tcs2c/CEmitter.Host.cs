using TinyCs;

namespace TinyCs.Tcs2c;

// 出荷形の入口: --lib の外部 linkage 関数 (init / entry / GC 境界 / hold /
// 統計) と、実行形の main。
internal sealed partial class CEmitter
{
    // 静的 link 出荷形 (--lib): main を持たず、初期化と各 static void method
    // (引数は int / float / bool のみ) を外部 linkage で公開する。entry は
    // 最外の呼び出しから戻った点 (C# スタックが空) で自動的にフレーム境界
    // (tcs_gc_frame) を踏む。host が entry の外で持つ tcs の pointer は
    // tcs_lib_hold した slot 経由でだけ境界を跨げる
    private void EmitLibEntryPoints()
    {
        // host が foreign 引数の配列 / Dict の要素型を確かめるための型 tag
        Line($"#define TCS_TYPE_ARRAY_F32 {RuntimeTypeId(CType.Array(CType.F32))}");
        Line($"#define TCS_TYPE_ARRAY_I32 {RuntimeTypeId(CType.Array(CType.I32))}");
        Line($"#define TCS_TYPE_DICT_STRING_OBJECT " +
            $"{RuntimeTypeId(CType.Dict(CType.String, CType.Object))}");
        Line("void");
        Line("tcs_lib_init(void)");
        Line("{");
        _indent++;
        Line("tcs_gc_call_depth++;");
        Line("tcs_init_statics();");
        Line("if (--tcs_gc_call_depth == 0) tcs_gc_frame();");
        _indent--;
        Line("}");
        Line();
        // 明示のフレーム境界 (entry 自動境界の後なら空のフレーム)
        Line("void");
        Line("tcs_lib_gc(void)");
        Line("{");
        _indent++;
        Line("if (tcs_gc_call_depth == 0) tcs_gc_frame();");
        _indent--;
        Line("}");
        Line();
        // 境界 + 旧世代 full GC (host が明示的に回収したいとき)
        Line("void");
        Line("tcs_lib_collect(void)");
        Line("{");
        _indent++;
        Line("if (tcs_gc_call_depth != 0) return;");
        Line("tcs_gc_frame();");
        Line("tcs_gc_collect();");
        _indent--;
        Line("}");
        Line();
        Line("size_t");
        Line("tcs_lib_heap_bytes(void)");
        Line("{");
        _indent++;
        Line("return tcs_gc_old_bytes + tcs_gc_nursery_bytes;");
        _indent--;
        Line("}");
        Line();
        Line("size_t");
        Line("tcs_lib_heap_objects(void)");
        Line("{");
        _indent++;
        Line("return tcs_gc_object_count + tcs_gc_nursery_objects;");
        _indent--;
        Line("}");
        Line();
        // host が frame を跨いで持つ tcs object の slot (境界で昇格先に書き換わる)
        Line("void");
        Line("tcs_lib_hold(void **slot)");
        Line("{");
        _indent++;
        Line("tcs_gc_hold(slot);");
        _indent--;
        Line("}");
        Line();
        Line("void");
        Line("tcs_lib_release(void **slot)");
        Line("{");
        _indent++;
        Line("tcs_gc_release(slot);");
        _indent--;
        Line("}");
        Line();
        foreach (var cls in _program.Classes.Where(c => !c.IsInterface))
        foreach (var method in cls.Methods.Where(m => m.IsStatic))
        {
            var fact = _facts.Method(cls.Name, method.Name);
            if (fact.ReturnType != CType.Void || fact.Parameters.Any(p =>
                p.Type.Kind is not (CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool)))
                continue;
            Line("void");
            Line($"tcs_entry_{Names.Id(cls.Name)}_{Names.Id(method.Name)}({ParameterList(fact)})");
            Line("{");
            _indent++;
            Line("tcs_gc_call_depth++;");
            var args = string.Join(", ",
                fact.Parameters.Select((p, i) => $"v_{Names.Id(p.Name)}_{i}"));
            Line($"{Names.Method(cls.Name, method.Name)}({args});");
            Line("if (--tcs_gc_call_depth == 0) tcs_gc_frame();");
            _indent--;
            Line("}");
            Line();
        }
    }

    private void EmitEntryPoint((IlClassInfo Class, IlMethodInfo Method)? entry)
    {
        Line("int");
        Line("main(void)");
        Line("{");
        _indent++;
        // 実行形は Main 全体が 1 フレーム (境界が無いので GC は走らない)
        Line("tcs_init_statics();");
        if (_digestF32) Line("tcs_digest = UINT32_C(2166136261);");
        if (entry is { } selected)
            Line($"{Names.Method(selected.Class.Name, selected.Method.Name)}();");
        if (_digestF32)
            Line("printf(\"%08\" PRIx32 \"\\n\", tcs_digest);");
        Line("return 0;");
        _indent--;
        Line("}");
    }

}
