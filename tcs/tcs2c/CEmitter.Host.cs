namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private void EmitLibEntryPoints()
    {
        Line($"#define TCS_TYPE_ARRAY_F32 {RuntimeTypeId(CType.Array(CType.F32))}");
        Line($"#define TCS_TYPE_DICT_STRING_OBJECT {RuntimeTypeId(CType.Dict(CType.String, CType.Object))}");
        EmitGcExports();
        Line("void");
        Line("tcs_lib_init(void)");
        Line("{");
        _indent++;
        Line("tcs_gc_call_depth++;");
        Line("tcs_init_statics();");
        EmitGcReturnBoundary();
        _indent--;
        Line("}");
        Line();
        foreach (var cls in _program.Classes)
        foreach (var method in cls.Methods.Where(m => m.IsStatic))
        {
            var fact = _facts.Method(cls.Name, method.Name);
            if (fact.ReturnType != CType.Void || fact.Parameters.Any(p =>
                p.Type.Kind is not (CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool))) continue;
            Line("void");
            Line($"tcs_entry_{Names.Id(cls.Name)}_{Names.Id(method.Name)}({ParameterList(fact)})");
            Line("{");
            _indent++;
            Line("tcs_gc_call_depth++;");
            var args = string.Join(", ", fact.Parameters.Select((p, i) => $"v_{Names.Id(p.Name)}_{i}"));
            Line($"{Names.Method(cls.Name, method.Name)}({args});");
            EmitGcReturnBoundary();
            _indent--;
            Line("}");
            Line();
        }
    }
}
