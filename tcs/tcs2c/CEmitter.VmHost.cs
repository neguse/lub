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

    /// <summary>VM の host 関数の種類の文字。境界を越えられない型は null。</summary>
    private string? Letter(CType t, bool ret) => t.Kind switch
    {
        CTypeKind.I32 => "i",
        CTypeKind.F32 => "f",
        CTypeKind.Bool => "b",
        CTypeKind.String => "s",
        CTypeKind.Ref when !_facts.IsDeclaredExternal(t.Name!) => "o",
        CTypeKind.Object => "o",
        _ => null,
    };
}
