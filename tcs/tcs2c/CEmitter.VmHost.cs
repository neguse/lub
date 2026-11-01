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
        var registered = new List<(string Name, string Params, string Rets, string Fn)>();
        // VM の delegate を tcs2c の closure に包む (X-4): 関数型ごとに trampoline を作る。cells[0] が VM の closure
        _vmhWrappers.Clear();
        foreach (var h in surface)
        {
            var cls = _program.Classes.FirstOrDefault(c => c.DisplayName == h.TypeDisplay || c.Name == h.TypeDisplay);
            if (cls is null || cls.Methods.All(m => m.Name != h.Method || !m.IsStatic)) continue;
            foreach (var p in _facts.Method(cls.Name, h.Method).Parameters.Where(p => p.Type.Kind == CTypeKind.Closure))
                VmhWrapper(p.Type);
        }
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
                CTypeKind.Closure => $"{VmhWrapper(p.Type)}(a[{k}].p)",
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
                case CTypeKind.Ref or CTypeKind.Object: Line($"r[0].p = {call};"); break;
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
        EmitVmSharedHeap();
        Line("/* この module の公開 static method を VM の host 関数として登録し、この module の runtime を VM の heap にする");
        Line("   (shared heap: 1 つの heap と GC。doc/object-model.md §10.2)。tvm_load の前に呼ぶ。登録できた数を返す */");
        Line("int");
        Line("tcs_vm_register(TvmVm *vm)");
        Line("{");
        _indent++;
        Line("int n = 0;");
        Line("tcs_gc_foreign_refs = tvm_heap_refs;");
        Line("tcs_gc_root_hook = tvm_heap_roots;");
        Line("tvm_use_heap(vm, &tcs_vmh_heap);");
        Line("tcs_vmh_vm = vm;");
        foreach (var (name, ps, rs, fn) in registered)
            Line($"n += tvm_host(vm, \"{name}\", \"{ps}\", \"{rs}\", {fn}, NULL);");
        Line("return n;");
        _indent--;
        Line("}");
        Line();
    }

    // shared heap: VM の確保と回収をこの module の runtime (nursery と旧世代、write barrier) に任せる関数表
    private void EmitVmSharedHeap()
    {
        Line("static void *tcs_vmh_alloc(uint32_t kind, size_t size) { return tcs_gc_alloc(kind, NULL, size); }");
        Line("static void tcs_vmh_enter(void) { tcs_gc_call_depth++; }");
        Line("static void tcs_vmh_leave(void) { tcs_gc_call_depth--; }");
        Line("static void tcs_vmh_barrier(const void *owner) { tcs_wb(owner); }");
        Line("static void tcs_vmh_frame(void) { if (tcs_gc_call_depth == 0) tcs_gc_frame(); }");
        // reload の張り替え (X-3): 旧世代の全部の object と、この module の static と hold の参照の場所を渡す
        Line("static void");
        Line("tcs_vmh_visit_value(void *value, const TcsLayout *layout, TvmSlotFn fn)");
        Line("{");
        _indent++;
        Line("uint32_t i;");
        Line("if (layout == NULL) return;");
        Line("for (i = 0; i < layout->count; i++) fn((void **)((unsigned char *)value + layout->offsets[i]));");
        _indent--;
        Line("}");
        Line("static void");
        Line("tcs_vmh_visit_object(TcsGcHeader *hdr, TvmSlotFn fn)");
        Line("{");
        _indent++;
        Line("void *payload = TCS_GC_PAYLOAD(hdr);");
        Line("size_t i;");
        Line("switch (hdr->kind) {");
        Line("case TCS_KIND_OBJECT: tcs_vmh_visit_value(payload, hdr->layout, fn); break;");
        Line("case TCS_KIND_ARRAY: {");
        Line("    TcsArray *array = payload;");
        Line("    for (i = 0; hdr->layout != NULL && i < array->length; i++)");
        Line("        tcs_vmh_visit_value(array->data + i * array->element_size, hdr->layout, fn);");
        Line("    break;");
        Line("}");
        Line("case TCS_KIND_LIST: {");
        Line("    TcsList *list = payload;");
        Line("    for (i = 0; hdr->layout != NULL && list->data != NULL && i < list->length; i++)");
        Line("        tcs_vmh_visit_value((unsigned char *)list->data + i * list->element_size, hdr->layout, fn);");
        Line("    break;");
        Line("}");
        Line("case TCS_KIND_DICT_NODE: {");
        Line("    TcsDictNode *node = payload;");
        Line("    fn((void **)&node->key_s);");
        Line("    tcs_vmh_visit_value(node->value, hdr->layout, fn);");
        Line("    break;");
        Line("}");
        Line("case TCS_KIND_CLOSURE: {");
        Line("    TcsClosure *closure = payload;");
        Line("    size_t cells = (hdr->size - sizeof(TcsClosure)) / sizeof(void *);");
        Line("    for (i = 0; i < cells; i++) fn(&closure->cells[i]);");
        Line("    break;");
        Line("}");
        Line("case TCS_KIND_FOREIGN: tvm_heap_refs(payload, fn); break;");
        Line("default: break;");
        Line("}");
        _indent--;
        Line("}");
        Line("static void");
        Line("tcs_vmh_visit_all(TvmSlotFn fn)");
        Line("{");
        _indent++;
        Line("TcsGcHeader *hdr;");
        Line("size_t i;");
        foreach (var cls in StaticOwners)
        foreach (var field in cls.Fields.Where(f => f.IsStatic))
        {
            var fact = _facts.Field(cls.Name, field.Name);
            var name = Names.StaticField(cls.Name, field.Name);
            if (IsPointerType(fact.Type))
                Line($"fn((void **)&{name});");
            else if (fact.Type.Kind == CTypeKind.StructVal
                || fact.Type is { Kind: CTypeKind.Nullable, Element.Kind: CTypeKind.StructVal })
                Line($"tcs_vmh_visit_value(&{name}, {LayoutRef(fact.Type)}, fn);");
        }
        Line("for (i = 0; i < tcs_gc_hold_count; i++) fn(tcs_gc_holds[i]);");
        Line("for (hdr = tcs_gc_objects; hdr != NULL; hdr = hdr->next) tcs_vmh_visit_object(hdr, fn);");
        _indent--;
        Line("}");
        Line("static void");
        Line("tcs_vmh_each(void (*fn)(void *payload, uint32_t kind, void *ctx), void *ctx)");
        Line("{");
        _indent++;
        Line("TcsGcHeader *hdr;");
        Line("for (hdr = tcs_gc_objects; hdr != NULL; hdr = hdr->next) fn(TCS_GC_PAYLOAD(hdr), hdr->kind, ctx);");
        _indent--;
        Line("}");
        Line("static const TvmHeap tcs_vmh_heap = { tcs_vmh_alloc, tcs_vmh_enter, tcs_vmh_leave, tcs_vmh_barrier,");
        Line("    tcs_vmh_frame, tcs_vmh_visit_all, tcs_vmh_each };");
        Line();
    }

    private readonly Dictionary<string, string> _vmhWrappers = [];

    private static bool VmhValue(CType t, ContractFacts facts) =>
        t.Kind is CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool or CTypeKind.String or CTypeKind.Object
        || t.Kind == CTypeKind.Ref && !facts.IsDeclaredExternal(t.Name!);

    /// <summary>C の値 expr を VM の slot a[i] に書く文。</summary>
    private static string VmhToSlot(CType t, string expr, string slot) => t.Kind switch
    {
        CTypeKind.I32 => $"{slot}.i = {expr};",
        CTypeKind.F32 => $"{slot}.f = {expr};",
        CTypeKind.Bool => $"{slot}.i = {expr} ? 1 : 0;",
        CTypeKind.String => $"{slot}.p = {expr} ? tvm_string_new(tcs_vmh_vm, (const char *){expr}->data, {expr}->length) : NULL;",
        _ => $"{slot}.p = {expr};",
    };

    /// <summary>VM の slot を C の型 t の値にする式。</summary>
    private static string VmhFromSlot(CType t, string slot) => t.Kind switch
    {
        CTypeKind.I32 => $"{slot}.i",
        CTypeKind.F32 => $"{slot}.f",
        CTypeKind.Bool => $"({slot}.i != 0)",
        CTypeKind.String => $"tcs_vmh_str_in({slot}.p)",
        _ => $"({t.CName}){slot}.p",
    };

    /// <summary>interface の dispatcher の、VM の object (kind FOREIGN) への枝 (X-4)。呼べない形なら null。</summary>
    private string? VmhDispatchBranch(MethodFact fact, string method)
    {
        if (VmHostSurface is null || fact.OutParameters.Count > 0) return null;
        if (fact.Parameters.Any(p => !VmhValue(p.Type, _facts))) return null;
        if (fact.ReturnType != CType.Void && !VmhValue(fact.ReturnType, _facts)) return null;
        var sb = new System.Text.StringBuilder();
        var n = fact.Parameters.Count;
        sb.Append("if (TCS_GC_HEADER(v_self)->kind == TCS_KIND_FOREIGN) { ");
        sb.Append($"TvmSlot a[{Math.Max(n, 1)}], r[1]; ");
        for (var i = 0; i < n; i++)
            sb.Append(VmhToSlot(fact.Parameters[i].Type, $"v_{Names.Id(fact.Parameters[i].Name)}_{i}", $"a[{i}]") + " ");
        var hasRet = fact.ReturnType != CType.Void;
        sb.Append($"if (tvm_invoke_method(tcs_vmh_vm, v_self, \"{method}\", a, {n}, r, {(hasRet ? 1 : 0)}) != TVM_OK) ");
        sb.Append("{ fprintf(stderr, \"%s\\n\", tvm_error(tcs_vmh_vm)); tcs_fault(\"vm-method\"); } ");
        sb.Append(hasRet ? $"return {VmhFromSlot(fact.ReturnType, "r[0]")}; }}" : "return; }");
        return sb.ToString();
    }

    /// <summary>VM の delegate を包める関数型 (引数と戻り値が scalar / string / class / object)。</summary>
    private bool VmhCallable(CType t) =>
        t.Parameters!.All(p => p.Kind is CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool or CTypeKind.String
            or CTypeKind.Object || p.Kind == CTypeKind.Ref && !_facts.IsDeclaredExternal(p.Name!))
        && (t.Element!.Kind is CTypeKind.Void or CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool or CTypeKind.String
            or CTypeKind.Object || t.Element.Kind == CTypeKind.Ref && !_facts.IsDeclaredExternal(t.Element.Name!));

    /// <summary>関数型 t の VM の delegate を tcs2c の closure に包む関数の名前 (無ければ trampoline と一緒に出す)。</summary>
    private string VmhWrapper(CType t)
    {
        var key = ClosureFnPtrType(t);
        if (_vmhWrappers.TryGetValue(key, out var name)) return name;
        if (!VmhCallable(t)) return "";
        var n = _vmhWrappers.Count;
        name = $"tcs_vmh_wrap_{n}";
        _vmhWrappers[key] = name;
        var ps = t.Parameters!;
        var paramList = string.Join("", ps.Select((p, i) => $", {p.CName} p{i}"));
        Line($"static {t.Element!.CName}");
        Line($"tcs_vmh_tramp_{n}(void **cells{paramList})");
        Line("{");
        _indent++;
        Line($"TvmSlot args[{Math.Max(ps.Count, 1)}], rets[1];");
        for (var i = 0; i < ps.Count; i++)
            Line(ps[i].Kind switch
            {
                CTypeKind.I32 => $"args[{i}].i = p{i};",
                CTypeKind.F32 => $"args[{i}].f = p{i};",
                CTypeKind.Bool => $"args[{i}].i = p{i} ? 1 : 0;",
                CTypeKind.String => $"args[{i}].p = p{i} ? tvm_string_new(tcs_vmh_vm, (const char *)p{i}->data, p{i}->length) : NULL;",
                _ => $"args[{i}].p = p{i};",
            });
        var hasRet = t.Element.Kind != CTypeKind.Void;
        Line($"if (tvm_invoke(tcs_vmh_vm, cells[0], args, {ps.Count}, rets, {(hasRet ? 1 : 0)}) != TVM_OK) {{");
        Line("    fprintf(stderr, \"%s\\n\", tvm_error(tcs_vmh_vm));");
        Line("    tcs_fault(\"vm-delegate\");");
        Line("}");
        if (hasRet)
            Line(t.Element.Kind switch
            {
                CTypeKind.I32 => "return rets[0].i;",
                CTypeKind.F32 => "return rets[0].f;",
                CTypeKind.Bool => "return rets[0].i != 0;",
                CTypeKind.String => "return tcs_vmh_str_in(rets[0].p);",
                _ => $"return ({t.Element.CName})rets[0].p;",
            });
        _indent--;
        Line("}");
        Line("static TcsClosure *");
        Line($"{name}(void *closure)");
        Line("{");
        _indent++;
        Line("TcsClosure *c;");
        Line("if (closure == NULL) return NULL;");
        Line($"c = tcs_new_closure((void *)tcs_vmh_tramp_{n}, 1);");
        Line("c->cells[0] = closure;");
        Line("return c;");
        _indent--;
        Line("}");
        Line();
        return name;
    }

    /// <summary>VM の host 関数の種類の文字。境界を越えられない型は null。</summary>
    private string? Letter(CType t, bool ret) => t.Kind switch
    {
        CTypeKind.I32 => "i",
        CTypeKind.F32 => "f",
        CTypeKind.Bool => "b",
        CTypeKind.String => "s",
        CTypeKind.Ref when !_facts.IsDeclaredExternal(t.Name!) => "o",
        CTypeKind.Object => "o",
        CTypeKind.Closure when !ret && VmhCallable(t) => "o",
        _ => null,
    };
}
