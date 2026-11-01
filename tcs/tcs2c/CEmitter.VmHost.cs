using TinyCs;

namespace TinyCs.Tcs2c;

// module 単位の混在 (doc/object-model.md §10): --lib --vm-host は、この module (editor など C で固定する側) の公開
// static method を tcs VM の host 関数として登録する glue (tcs_vm_register) を出す。VM の module (content) は
// この module の source を --ref に取って compile し、同じ名前 (IlExport.HostSurface) で呼ぶ。
// 境界を越える値 (1 段目): int / float / bool / enum はそのまま、string は両側の形が違うので境界で写す、
// class の object は VM の object をそのまま渡す (class の payload は両側で同じ形。M-4 の 1 段目)。
// class の object の受け渡しは引数だけで、C 側はその object を保持しない (heap と GC が別なので。2 段目で共有する)。
// 呼べない形 (out、List / Dictionary / delegate、class の戻り値) の method は登録しない (C の comment に理由)。
internal sealed partial class CEmitter
{
    public IReadOnlyList<IlHostFunction>? VmHostSurface { get; set; }

    private void EmitVmHost(IReadOnlyList<IlHostFunction> surface)
    {
        Line("/* ---- tcs VM の host 関数 (--vm-host) ---- */");
        Line("#include \"tcsvm.h\"");
        Line("static TcsString *");
        Line("tcs_vmh_str_in(void *s)");
        Line("{");
        _indent++;
        Line("size_t n;");
        Line("const char *d = tvm_string_data(s, &n);");
        Line("return d ? tcs_string_new((const unsigned char *)d, n) : NULL;");
        _indent--;
        Line("}");
        Line();
        var registered = new List<(string Name, string Params, string Rets, string Fn)>();
        foreach (var h in surface)
        {
            var cls = _program.Classes.FirstOrDefault(c => c.DisplayName == h.TypeDisplay || c.Name == h.TypeDisplay);
            if (cls is null || cls.Methods.All(m => m.Name != h.Method || !m.IsStatic)) continue;
            var fact = _facts.Method(cls.Name, h.Method);
            string? Why()
            {
                if (fact.OutParameters.Count > 0) return "out";
                if (fact.Parameters.FirstOrDefault(p => Letter(p.Type, false) is null) is { } bad) return $"parameter {bad.Name}: {bad.Type}";
                if (fact.ReturnType != CType.Void && Letter(fact.ReturnType, true) is null) return $"return {fact.ReturnType}";
                return null;
            }
            if (Why() is { } why)
            {
                Line($"/* {h.Name}: not a VM host function ({why}) */");
                continue;
            }
            var fn = $"tcs_vmh_{registered.Count}";
            var ps = string.Concat(fact.Parameters.Select(p => Letter(p.Type, false)));
            var rs = fact.ReturnType == CType.Void ? "" : Letter(fact.ReturnType, true)!;
            Line("static TvmStatus");
            Line($"{fn}(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user)");
            Line("{");
            _indent++;
            Line("(void)vm; (void)a; (void)r; (void)user;");
            Line("tcs_gc_call_depth++;");
            var args = string.Join(", ", fact.Parameters.Select((p, k) => p.Type.Kind switch
            {
                CTypeKind.I32 => $"a[{k}].i",
                CTypeKind.F32 => $"a[{k}].f",
                CTypeKind.Bool => $"a[{k}].i != 0",
                CTypeKind.String => $"tcs_vmh_str_in(a[{k}].p)",
                _ => $"({p.Type.CName})a[{k}].p",
            }));
            var call = $"{Names.Method(cls.Name, h.Method)}({args})";
            // 戻り値の string は frame の境界 (nursery が動く) より前に VM の string に写す
            switch (fact.ReturnType.Kind)
            {
                case CTypeKind.Void: Line($"{call};"); break;
                case CTypeKind.I32: Line($"r[0].i = {call};"); break;
                case CTypeKind.F32: Line($"r[0].f = {call};"); break;
                case CTypeKind.Bool: Line($"r[0].i = {call} ? 1 : 0;"); break;
                default:
                    Line($"TcsString *s = {call};");
                    Line("r[0].p = s ? tvm_string_new(vm, (const char *)s->data, s->length) : NULL;");
                    break;
            }
            Line("if (--tcs_gc_call_depth == 0) tcs_gc_frame();");
            Line("return TVM_OK;");
            _indent--;
            Line("}");
            Line();
            registered.Add((h.Name, ps, rs, fn));
        }
        Line("/* この module の公開 static method を VM の host 関数として登録する。tvm_load の前に呼ぶ。登録できた数を返す */");
        Line("int");
        Line("tcs_vm_register(TvmVm *vm)");
        Line("{");
        _indent++;
        Line("int n = 0;");
        foreach (var (name, ps, rs, fn) in registered)
            Line($"n += tvm_host(vm, \"{name}\", \"{ps}\", \"{rs}\", {fn}, NULL);");
        Line("return n;");
        _indent--;
        Line("}");
        Line();
    }

    /// <summary>VM の host 関数の種類の文字。境界を越えられない型は null。</summary>
    private string? Letter(CType t, bool ret) => t.Kind switch
    {
        CTypeKind.I32 => "i",
        CTypeKind.F32 => "f",
        CTypeKind.Bool => "b",
        CTypeKind.String => "s",
        CTypeKind.Ref when !ret && !_facts.IsDeclaredExternal(t.Name!) => "o",
        _ => null,
    };
}
