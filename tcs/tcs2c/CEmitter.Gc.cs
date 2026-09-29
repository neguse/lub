namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private string TraceValue(CType? type) => type switch
    {
        { IsNullable: true } => "tcs_trace_ref",
        { Kind: CTypeKind.StructVal } => $"tcs_trace_value_{Names.Id(type.Name!)}",
        _ => "NULL",
    };

    private void EmitTraceValue(CType type, string place)
    {
        var trace = TraceValue(type);
        if (trace != "NULL") Line($"{trace}(&({place}));");
    }

    private void EmitGcTracers()
    {
        foreach (var st in _facts.Structs.Values)
            Line($"static void tcs_trace_value_{Names.Id(st.Name)}(void *);");
        foreach (var st in _facts.Structs.Values)
        {
            Line($"static void tcs_trace_value_{Names.Id(st.Name)}(void *slot)");
            Line("{");
            _indent++;
            Line($"{CType.Struct(st.Name).CName} *value = slot;");
            Line("(void)value;");
            foreach (var field in st.Fields)
                EmitTraceValue(_facts.MapType(field.Type), $"value->{Names.Field(field.Name)}");
            _indent--;
            Line("}");
        }
        foreach (var cls in _program.Classes)
        {
            Line($"static void tcs_trace_object_{Names.Id(cls.Name)}(void *slot)");
            Line("{");
            _indent++;
            Line($"{Names.Class(cls.Name)} *value = slot;");
            Line("(void)value;");
            foreach (var link in ChainRootFirst(cls.Name))
            foreach (var field in link.Fields.Where(f => !f.IsStatic))
                EmitTraceValue(_facts.Field(link.Name, field.Name).Type,
                    $"value->{Names.Field(field.Name)}");
            _indent--;
            Line("}");
        }
        Line("static void tcs_trace_roots(void *unused)");
        Line("{");
        _indent++;
        Line("(void)unused;");
        foreach (var cls in _program.Classes)
        foreach (var field in cls.Fields.Where(f => f.IsStatic))
            EmitTraceValue(_facts.Field(cls.Name, field.Name).Type,
                Names.StaticField(cls.Name, field.Name));
        _indent--;
        Line("}");
    }

    private void EmitGcExports()
    {
        Line("void tcs_lib_collect(void) { tcs_gc_collect(tcs_trace_roots); }");
        Line("size_t tcs_lib_heap_bytes(void) { return tcs_gc_bytes; }");
        Line("size_t tcs_lib_heap_objects(void) { return tcs_gc_objects; }");
    }

    private void EmitGcReturnBoundary()
    {
        Line("if (--tcs_gc_call_depth == 0 && tcs_gc_bytes > tcs_gc_threshold)");
        Line("    tcs_gc_collect(tcs_trace_roots);");
    }
}
