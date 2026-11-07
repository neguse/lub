using System.Collections.Generic;
using TcsSelf;

namespace LubGen;

// tcs VM (third_party/tcs/tcsvm) の host の関数を作る: lub の C API を、VM が呼ぶ host の関数 (tvm_host) として登録する C。
// 登録するのは stub の全部の関数 (player はゲームを選ばない)。--source を渡したときだけ、そのゲームが使う関数に絞る (テスト用)。
// VM の object の layout は host の data class も tcs2c と同じ C の struct の規則 (tcs の doc/object-model.md §3、§5) なので、
// record は C の struct の写し (VmLub_<型>) で読み書きする。読み込みと reload の後に vm_lub_verify が offset を VM の型の表と比べる。
// 引数は C# の順に 1 つずつ渡り (tcs の vm-design §8.5)、out (&) は VM が値を受ける場所の pointer、rets は戻り値だけ。
// struct の record は VM の中でも C API の struct と同じ layout の値なので、S{Lub_<型>} の塊を C API の struct としてそのまま読み書きする。
// 変換の規則は tcs2c の接続コード (TcsBinding) と同じ。tcs2c が process を止める誤り (tcs_fault) は、その host の関数の fault にする。
public static class VmBinding
{
    public static string Generate(ApiModel model, ExportResult? game)
    {
        var g = new VmBindingGen(model, game);
        return g.Run();
    }
}

internal sealed class VmBindingGen
{
    private ApiModel model;
    private ExportResult? game;
    private Out output = new Out();
    private HashSet<string> inputs = new HashSet<string>();
    private HashSet<string> outputs = new HashSet<string>();
    private HashSet<string> inputLists = new HashSet<string>();
    private HashSet<string> outputLists = new HashSet<string>();
    // VM の型の名前 → C の cache 変数 (vm_lub_type で引く)
    private List<string> typeNames = new List<string>();
    // 登録する host の関数: IL の名前、引数の葉、戻り値の葉、C の関数
    private List<string> registrations = new List<string>();

    public VmBindingGen(ApiModel model, ExportResult? game)
    {
        this.model = model;
        this.game = game;
    }

    private void Line(string text) => output.Line(text);

    private static string Il(string name) => ApiModelLoader.RootNamespace + "_" + name;

    private static string Struct(string name) => "VmLub_" + name;

    private static string Scalar(TypeRef type)
    {
        if (type.Kind == LubTypeKind.Int || type.Kind == LubTypeKind.Enum) return "int32_t";
        if (type.Kind == LubTypeKind.Double) return "float";
        if (type.Kind == LubTypeKind.Bool) return "bool";
        GenError.Report("unsupported scalar: " + type.ToString());
        return "";
    }

    private static string Opt(TypeRef type)
    {
        if (type.Kind == LubTypeKind.Struct) return "VmLubOpt_" + type.Name;
        if (type.Kind == LubTypeKind.Double) return "VmLubOptF32";
        if (type.Kind == LubTypeKind.Bool) return "VmLubOptBool";
        return "VmLubOptI32";
    }

    // VM の値の C の型 (field と引数)
    private static string VmType(TypeRef type)
    {
        switch (type.Kind)
        {
            case LubTypeKind.Void:
                return "void";
            case LubTypeKind.Int:
            case LubTypeKind.Double:
            case LubTypeKind.Bool:
            case LubTypeKind.Enum:
                return type.Nullable ? Opt(type) : Scalar(type);
            case LubTypeKind.Struct:
                // struct の record は VM の中でも C の layout の値 (C API の struct と同じ並び。vm_lub_verify が確かめる)
                return type.Nullable ? Opt(type) : CHeader.TypeName(type.Name);
            case LubTypeKind.Handle:
            case LubTypeKind.View:
            case LubTypeKind.Record:
            case LubTypeKind.Native:
            case LubTypeKind.Keyed:
                return Struct(type.Name) + " *";
        }
        return "void *";
    }

    // VM の型の表の名前 (tcs の VType.Display)
    private static string VmTypeName(TypeRef type)
    {
        switch (type.Kind)
        {
            case LubTypeKind.Int:
                return "int";
            case LubTypeKind.Double:
                return "float";
            case LubTypeKind.Bool:
                return "bool";
            case LubTypeKind.String:
                return "string";
            case LubTypeKind.Handle:
            case LubTypeKind.View:
            case LubTypeKind.Record:
            case LubTypeKind.Native:
            case LubTypeKind.Keyed:
            case LubTypeKind.Struct:
                return Il(type.Name);
            case LubTypeKind.List:
                return "List<" + VmTypeName(type.Elem!) + ">";
            case LubTypeKind.Array:
                return VmTypeName(type.Elem!) + "[]";
        }
        GenError.Report("no VM type name for " + type.ToString());
        return "";
    }

    // 型の cache 変数 (VM の型の名前ごとに 1 つ)
    private string TypeVar(string vmName)
    {
        int at = typeNames.IndexOf(vmName);
        if (at < 0)
        {
            at = typeNames.Count;
            typeNames.Add(vmName);
        }
        return "&vm_lub_types[" + at + "]";
    }

    private static string NativeType(TypeRef type)
    {
        switch (type.Kind)
        {
            case LubTypeKind.String:
                return "LubStr";
            case LubTypeKind.Handle:
                return "LubHandle";
            case LubTypeKind.View:
                return "LubView";
            case LubTypeKind.Record:
            case LubTypeKind.Struct:
                return "Lub" + type.Name;
            case LubTypeKind.List:
                return "const " + NativeType(type.Elem!) + " *";
        }
        return Scalar(type);
    }

    private List<FieldPath> Fields(ApiType type)
    {
        var r = new List<FieldPath>();
        AddFields(type, "", r);
        return r;
    }

    private void AddFields(ApiType type, string prefix, List<FieldPath> r)
    {
        foreach (var field in type.Fields) r.Add(new FieldPath(field, prefix + field.LuaName));
    }

    private void Record(TypeRef type, bool isOutput)
    {
        if (type.Kind == LubTypeKind.Func)
        {
            foreach (var param in type.FuncParams!) Record(param, true);
            return;
        }
        if (type.Kind == LubTypeKind.List || type.Kind == LubTypeKind.Array)
        {
            if (type.Elem!.Kind == LubTypeKind.Record) (isOutput ? outputLists : inputLists).Add(type.Elem.Name);
            Record(type.Elem, isOutput);
            return;
        }
        if (type.Kind != LubTypeKind.Record || !(isOutput ? outputs : inputs).Add(type.Name)) return;
        foreach (var fp in Fields(model.FindType(type.Name)!)) Record(fp.Field.Type, isOutput);
    }

    private bool AnyBits(HashSet<string> names)
    {
        foreach (var name in names)
        {
            foreach (var fp in Fields(model.FindType(name)!))
            {
                if (fp.Field.Bits) return true;
            }
        }
        return false;
    }

    // 呼ぶ関数 (ゲームを渡したときはそのゲームが使うものだけ)
    private bool Wanted(string hostName)
    {
        if (game == null) return true;
        foreach (var m in game.ForeignMethods)
        {
            if (m.Name == hostName) return true;
        }
        return false;
    }

    private bool WantedValue(string hostName)
    {
        if (game == null) return true;
        foreach (var v in game.ForeignValues)
        {
            if (!v.HasConstant && v.Name == hostName) return true;
        }
        return false;
    }

    // 外部の型の instance method の IL の名前 ("lub.bytes.get")
    private static string MethodHostName(ApiType type, ApiFunction method) =>
        ApiModelLoader.RootNamespace.ToLower() + "." + type.Name.ToLower() + "." + method.LuaName;

    public string Run()
    {
        var functionNs = new List<ApiNamespace>();
        var functions = new List<ApiFunction>();
        foreach (var ns in model.Namespaces)
        {
            foreach (var f in ns.Functions)
            {
                if (!Wanted(ns.LuaPath + "." + f.LuaName)) continue;
                if (f.NoC && !IsKeyedMaker(f)) continue;
                functionNs.Add(ns);
                functions.Add(f);
            }
        }
        foreach (var function in functions)
        {
            foreach (var param in function.Params) Record(param.Type, param.IsOut);
            Record(function.Return, true);
        }
        // OnEvent の引数は host が LubEventData から作る
        Record(new TypeRef(LubTypeKind.Record, "EventData", false, null, null, null), true);
        Line("/* Generated from lub_stub.cs by lub-gen vm (the tcs VM host functions). Do not edit. */");
        Line("#include <lub/lub_api.h>");
        Line("#include \"tcsvm.h\"");
        Line("#include <stddef.h>");
        Line("#include <stdio.h>");
        Line("#include <stdlib.h>");
        Line("#include <string.h>");
        Line(Helpers);
        Structs();
        if (GenError.Failed) return "";
        var body = new Out();
        var head = output;
        output = body;
        bool anyView = false;
        foreach (var t in model.Types)
        {
            if (t.Kind == "view") anyView = true;
        }
        if (anyView) Line(ViewHelpers);
        if (AnyBits(inputs) || AnyBits(outputs)) Line(BitsHelpers);
        foreach (var type in model.Types)
        {
            if (type.Kind == "handle") Handle(type);
            if (type.Kind == "view") View(type);
        }
        foreach (var name in inputs) Line("static Lub" + name + " vm_lub_to_" + name + "(" + Struct(name) + " *source);");
        foreach (var name in outputs) Line("VM_LUB_UNUSED static " + Struct(name) + " *vm_lub_from_" + name + "(const Lub" + name + " *source);");
        foreach (var name in inputLists) Line("static Lub" + name + " *vm_lub_to_" + name + "_list(void *source, int32_t *count);");
        foreach (var name in outputLists) Line("static void *vm_lub_from_" + name + "_list(void *list, const Lub" + name + " *source, int32_t count);");
        foreach (var name in inputs) InputRecord(model.FindType(name)!);
        foreach (var name in outputs) OutputRecord(model.FindType(name)!);
        foreach (var name in inputLists) InputList(name);
        foreach (var name in outputLists) OutputList(name);
        if (GenError.Failed) return "";
        foreach (var type in model.Types)
        {
            foreach (var method in type.Methods)
            {
                var hostName = MethodHostName(type, method);
                if (Wanted(hostName)) Method(type, method, hostName);
                if (GenError.Failed) return "";
            }
        }
        NativeBindings();
        foreach (var ns in model.Namespaces)
        {
            foreach (var value in ns.StaticFields)
            {
                var hostName = ns.LuaPath + "." + value.LuaName;
                if (!WantedValue(hostName)) continue;
                var name = CHeader.FunctionName(ns, value.LuaName);
                var fn = "vm_host_" + name;
                Line("static TvmStatus " + fn + "(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {");
                Line("  (void)a; (void)user; VM_LUB_ENTER();");
                PackReturn(value.Type, From(value.Type, name + "(vm_lub_context)"));
                Line("  VM_LUB_LEAVE(vm);\n}");
                registrations.Add("{\"" + hostName + "\", \"\", \"" + Token(value.Type) + "\", " + fn + "}");
            }
        }
        if (GenError.Failed) return "";
        for (int i = 0; i < functions.Count; i++)
        {
            if (functions[i].NoC)
            {
                KeyedMaker(functionNs[i], functions[i]);
                continue;
            }
            Function(functionNs[i], functions[i]);
            if (GenError.Failed)
            {
                GenError.Message = functionNs[i].Name + "." + functions[i].Name + ": " + GenError.Message;
                return "";
            }
        }
        output = head;
        // 型の cache は本文が使う名前が決まってから置く
        Line("static VmLubType vm_lub_types[" + (typeNames.Count == 0 ? 1 : typeNames.Count) + "] = {");
        foreach (var n in typeNames) Line("  {\"" + n + "\", 0, NULL},");
        if (typeNames.Count == 0) Line("  {\"\", 0, NULL},");
        Line("};");
        output.Write(body.ToString());
        Line("/* EventData を VM の object にする (src/vm_player.c の OnEvent) */");
        Line("static void *vm_lub_event(const LubEventData *e) { return vm_lub_from_EventData(e); }");
        Line("static const struct { const char *name, *params, *rets; TvmHostFn fn; } vm_lub_hosts[] = {");
        foreach (var r in registrations) Line("  " + r + ",");
        Line("  {NULL, NULL, NULL, NULL},");
        Line("};");
        Line("/* host の関数を全部登録する (tvm_load の前)。失敗したら 0 */");
        Line("static int vm_lub_register(TvmVm *vm) {");
        Line("  for (int i = 0; vm_lub_hosts[i].name; i++)");
        Line("    if (!tvm_host(vm, vm_lub_hosts[i].name, vm_lub_hosts[i].params, vm_lub_hosts[i].rets, vm_lub_hosts[i].fn, NULL)) return 0;");
        Line("  return 1;\n}");
        return output.ToString();
    }

    // [LubNoC] の keyed の作成 (`Readback Gfx.Readback(string key)`): C API は無く、key を host の表に置いた object を返す
    private static bool IsKeyedMaker(ApiFunction f) =>
        f.NoC && f.Return.Kind == LubTypeKind.Keyed && f.Params.Count == 1 && f.Params[0].Type.Kind == LubTypeKind.String;

    private void KeyedMaker(ApiNamespace ns, ApiFunction f)
    {
        var fn = "vm_host_" + CHeader.FunctionName(ns, f.LuaName);
        Line("static TvmStatus " + fn + "(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {");
        Line("  (void)user; VM_LUB_ENTER();");
        Line("  if (!a[0].p) { vm_lub_fail(\"null reference\"); VM_LUB_LEAVE(vm); }");
        Line("  " + Struct(f.Return.Name) + " *result = vm_lub_new(" + TypeVar(Il(f.Return.Name)) + ", sizeof(*result));");
        Line("  result->host_value = vm_lub_key_store(vm_lub_str(a[0].p));");
        Line("  r[0].p = result;");
        Line("  VM_LUB_LEAVE(vm);\n}");
        registrations.Add("{\"" + ns.LuaPath + "." + f.LuaName + "\", \"s\", \"o\", " + fn + "}");
    }

    // ---- C の struct の写し ----

    private static bool IsObjectType(ApiType t) =>
        t.Kind == "record" || t.Kind == "handle" || t.Kind == "view" || t.Kind == "native" || t.Kind == "keyed";

    private void Structs()
    {
        foreach (var t in model.Types)
        {
            if (IsObjectType(t)) Line("typedef struct " + Struct(t.Name) + " " + Struct(t.Name) + ";");
        }
        var checks = new List<string>();
        // struct の record は VM の中でも C API の struct と同じ layout の値。T? は { bool has; T v; }
        foreach (var t in model.Types)
        {
            if (t.Kind != "struct") continue;
            var opt = "VmLubOpt_" + t.Name;
            Line("typedef struct " + opt + " { bool has; " + CHeader.TypeName(t.Name) + " v; } " + opt + ";");
            foreach (var f in t.Fields) checks.Add("{\"" + Il(t.Name) + "\", \"" + f.LuaName + "\", offsetof(" + CHeader.TypeName(t.Name) + ", " + f.LuaName + ")}");
            OptChecks(Il(t.Name), opt, checks);
        }
        foreach (var t in model.Types)
        {
            if (!IsObjectType(t)) continue;
            // payload は type_id、8 byte に揃えた host_value、field (基底から)
            Line("struct " + Struct(t.Name) + " {");
            Line("  uint32_t type_id;");
            Line("  uint64_t host_value;");
            foreach (var fp in Fields(t))
            {
                Line("  " + VmType(fp.Field.Type) + " f_" + fp.Field.LuaName + ";");
                checks.Add("{\"" + Il(t.Name) + "\", \"" + fp.Field.LuaName + "\", offsetof(" + Struct(t.Name) + ", f_" + fp.Field.LuaName + ")}");
            }
            // stub の private field (Bindings の blob)
            if (t.Kind == "native")
            {
                Line("  void *f_blob;");
                checks.Add("{\"" + Il(t.Name) + "\", \"blob\", offsetof(" + Struct(t.Name) + ", f_blob)}");
            }
            Line("};");
        }
        // null になりうる scalar の塊 (引数と戻り値の S{int?}、record の field)
        OptChecks("int", "VmLubOptI32", checks);
        OptChecks("float", "VmLubOptF32", checks);
        OptChecks("bool", "VmLubOptBool", checks);
        Line("_Static_assert(offsetof(VmLub_Bindings, host_value) == 8, \"the VM puts host_value 8 bytes into the payload\");");
        Line("/* module が持つ型だけ、VM の型の表の offset と C の struct が同じことを確かめる (読み込みと reload の後) */");
        Line("static const struct { const char *type, *field; size_t offset; } vm_lub_layout[] = {");
        foreach (var c in checks) Line("  " + c + ",");
        Line("  {NULL, NULL, 0},");
        Line("};");
        Line("static int vm_lub_verify(TvmVm *vm, char *error, size_t size) {");
        Line("  for (int i = 0; vm_lub_layout[i].type; i++) {");
        Line("    const TvmTypeInfo *t = tvm_type(vm, vm_lub_layout[i].type);");
        Line("    if (!t) continue;");
        Line("    int32_t at = tvm_type_field_offset(t, vm_lub_layout[i].field);");
        Line("    if (at != (int32_t)vm_lub_layout[i].offset) {");
        Line("      snprintf(error, size, \"%s.%s is at %d in the VM, %d in the host\", vm_lub_layout[i].type, vm_lub_layout[i].field, (int)at, (int)vm_lub_layout[i].offset);");
        Line("      return 0;");
        Line("    }");
        Line("  }");
        Line("  return 1;\n}");
    }

    // VM の T? ({ bool HasValue; T Value; }) と C の写しの field
    private static void OptChecks(string scalar, string cType, List<string> checks)
    {
        checks.Add("{\"" + scalar + "?\", \"HasValue\", offsetof(" + cType + ", has)}");
        checks.Add("{\"" + scalar + "?\", \"Value\", offsetof(" + cType + ", v)}");
    }

    // ---- 値の変換 ----

    private string From(TypeRef type, string value)
    {
        switch (type.Kind)
        {
            case LubTypeKind.String:
                return "vm_lub_string(" + value + ")";
            case LubTypeKind.Handle:
                return "vm_lub_handle_" + type.Name + "(" + value + ")";
            case LubTypeKind.View:
                return "vm_lub_view_" + type.Name + "(" + value + ")";
            case LubTypeKind.Record:
                return "vm_lub_from_" + type.Name + "(&" + value + ")";
            case LubTypeKind.List:
                return ListFrom(type, value, "NULL", type.Nullable);
        }
        if (type.IsScalar || type.Kind == LubTypeKind.Struct) return value;
        GenError.Report("unsupported return: " + type.ToString());
        return "";
    }

    private string ListFrom(TypeRef type, string value, string list, bool nullable)
    {
        var elem = type.Elem!;
        string filled;
        if (elem.Kind == LubTypeKind.Record) filled = "vm_lub_from_" + elem.Name + "_list(" + list + ", " + value + ", " + value + "_count)";
        else if (elem.IsScalar) filled = "vm_lub_list(" + TypeVar(VmTypeName(type)) + ", " + list + ", " + value + ", " + value + "_count, sizeof(*" + value + "))";
        else if (elem.Kind == LubTypeKind.String) filled = "vm_lub_string_list(" + TypeVar(VmTypeName(type)) + ", " + list + ", " + value + ", " + value + "_count)";
        else
        {
            GenError.Report("unsupported list result: " + type.ToString());
            filled = "";
        }
        return nullable ? "(" + value + " ? " + filled + " : NULL)" : filled;
    }

    private static string To(TypeRef type, string value)
    {
        if (type.Kind == LubTypeKind.String) return "vm_lub_str(" + value + ")";
        if (type.Kind == LubTypeKind.Handle) return "(" + value + " ? (int32_t)" + value + "->host_value : 0)";
        if (type.Kind == LubTypeKind.Keyed) return "vm_lub_key_load(" + value + " ? " + value + "->host_value : 0)";
        if (type.IsScalar || type.Kind == LubTypeKind.Struct) return type.Nullable ? value + ".v" : value;
        GenError.Report("unsupported field: " + type.ToString());
        return "";
    }

    private static string HasPath(string prefix, string path, ApiField field) =>
        prefix + Text.DropEnd(path, Text.Len(field.LuaName)) + "has_" + field.LuaName;

    private static int FuncFieldIndex(ApiType type, ApiField field)
    {
        int i = 0;
        foreach (var f in type.Fields)
        {
            if (f == field) return i;
            if (f.Type.Kind == LubTypeKind.Func) i++;
        }
        return -1;
    }

    private static string FieldCallbackName(ApiType type, ApiField field) => "vm_lub_" + type.Name + "_" + field.LuaName;

    private void InputRecord(ApiType type)
    {
        int funcs = 0;
        foreach (var f in type.Fields)
        {
            if (f.Type.Kind != LubTypeKind.Func) continue;
            if (funcs >= 8) GenError.Report("too many callback fields: " + type.Name);
            Trampoline(FieldCallbackName(type, f), f.Type, type.Name + "." + f.Name, "VmLubHeld *held = user; void *closure = vm_lub_held_get(held, " + funcs + ");", "!closure", "closure", "vm_lub_held_fault(held, " + funcs + ")");
            funcs++;
        }
        Line("static Lub" + type.Name + " vm_lub_to_" + type.Name + "(" + Struct(type.Name) + " *source) {");
        Line("  Lub" + type.Name + " result = {0}; if (!source) return result;");
        foreach (var fp in Fields(type))
        {
            var field = fp.Field;
            var path = fp.Path;
            var src = "source->f_" + field.LuaName;
            var dst = "result." + path;
            var has = HasPath("result.", path, field);
            if ((field.Type.IsScalar || field.Type.Kind == LubTypeKind.Struct) && field.Optional)
                Line("  if (" + src + ".has) { " + has + " = true; " + dst + " = " + src + ".v; }");
            else if (field.Bits)
                Line("  " + (field.Optional ? has + " = " : "") + "vm_lub_bits(" + src + ", &" + dst + ");");
            else if (field.Type.Kind == LubTypeKind.Array)
            {
                if (field.ArrayLen < 0)
                {
                    GenError.Report("array length required");
                    return;
                }
                int length = field.ArrayLen;
                Line("  if (" + src + ") {");
                if (field.Optional) Line("    " + has + " = true;");
                Line("    if (tvm_array_len(" + src + ") != " + length + ") vm_lub_fail(\"" + type.Name + "." + field.Name + ": the array must have " + length + " elements\");");
                Line("    else memcpy(" + dst + ", tvm_array_data(" + src + "), sizeof(" + dst + "));\n  }");
            }
            else if (field.Type.Kind == LubTypeKind.List)
            {
                var elem = field.Type.Elem!;
                Line("  if (" + src + ") {");
                if (elem.Kind == LubTypeKind.Record)
                {
                    Line("    " + dst + " = vm_lub_to_" + elem.Name + "_list(" + src + ", &" + dst + "_count);");
                    Line("  }");
                    continue;
                }
                Line("    " + dst + "_count = tvm_list_count(" + src + ");");
                if (elem.IsScalar && field.ArrayLen >= 0)
                {
                    int cap = field.ArrayLen;
                    Line("    if (" + dst + "_count > " + cap + ") { vm_lub_fail(\"" + type.Name + "." + field.Name + ": at most " + cap + " elements\"); " + dst + "_count = 0; }");
                    Line("    memcpy(" + dst + ", tvm_list_data(" + src + "), (size_t)" + dst + "_count * sizeof(*" + dst + "));");
                }
                else if (elem.IsScalar) Line("    " + dst + " = tvm_list_data(" + src + ");");
                else if (elem.Kind == LubTypeKind.Handle)
                {
                    Line("    LubHandle *items = vm_lub_alloc((size_t)" + dst + "_count * sizeof(*items));");
                    Line("    for (int i = 0; i < " + dst + "_count; i++) { " + Struct(elem.Name) + " *item = ((" + Struct(elem.Name) + " **)tvm_list_data(" + src + "))[i]; items[i] = item ? (int32_t)item->host_value : 0; }");
                    Line("    " + dst + " = items;");
                }
                else if (elem.Kind == LubTypeKind.Array && field.ArrayLen >= 0)
                {
                    int len = field.ArrayLen;
                    Line("    " + Scalar(elem.Elem!) + " (*items)[" + len + "] = vm_lub_alloc((size_t)" + dst + "_count * sizeof(*items));");
                    Line("    for (int i = 0; i < " + dst + "_count; i++) { void *item = ((void **)tvm_list_data(" + src + "))[i]; if (tvm_array_len(item) != " + len + ") vm_lub_fail(\"" + type.Name + "." + field.Name + ": each array must have " + len + " elements\"); else memcpy(items[i], tvm_array_data(item), sizeof(items[i])); }");
                    Line("    " + dst + " = items;");
                }
                else
                {
                    GenError.Report("unsupported list field: " + field.Name);
                    return;
                }
                Line("  }");
            }
            else if (field.Type.Kind == LubTypeKind.Record && field.Optional)
                Line("  if (" + src + ") { " + has + " = true; " + dst + " = vm_lub_to_" + field.Type.Name + "(" + src + "); }");
            else if (field.Type.Kind == LubTypeKind.Record)
                Line("  " + dst + " = vm_lub_to_" + field.Type.Name + "(" + src + ");");
            else if (field.Type.Kind == LubTypeKind.Func)
            {
                int slot = FuncFieldIndex(type, field);
                Line("  if (" + src + ") {");
                Line("    if (!result.user) { result.user = vm_lub_held_new(); result.user_release = vm_lub_held_release; }");
                Line("    ((VmLubHeld *)result.user)->h[" + slot + "] = tvm_hold(vm_lub_vm, " + src + ");");
                Line("    " + dst + " = " + FieldCallbackName(type, field) + ";");
                Line("  }");
            }
            else Line("  " + dst + " = " + To(field.Type, src) + ";");
        }
        Line("  return result;\n}");
    }

    private void OutputRecord(ApiType type)
    {
        Line("static " + Struct(type.Name) + " *vm_lub_from_" + type.Name + "(const Lub" + type.Name + " *source) {");
        Line("  " + Struct(type.Name) + " *result = vm_lub_new(" + TypeVar(Il(type.Name)) + ", sizeof(*result));");
        foreach (var fp in Fields(type))
        {
            var field = fp.Field;
            var path = fp.Path;
            var dst = "result->f_" + field.LuaName;
            var src = "source->" + path;
            var has = HasPath("source->", path, field);
            if (field.Type.Kind == LubTypeKind.Array)
            {
                // 配列の field は host が作る (VM の object は field の initializer を走らせない)
                Line("  " + dst + " = vm_lub_array(" + TypeVar(VmTypeName(field.Type)) + ", " + field.ArrayLen + ");");
                Line("  if (" + dst + ") memcpy(tvm_array_data(" + dst + "), " + src + ", sizeof(" + src + "));");
            }
            else if ((field.Type.IsScalar || field.Type.Kind == LubTypeKind.Struct) && field.Optional)
                Line("  " + dst + " = (" + Opt(field.Type) + "){" + has + ", " + src + "};");
            else if (field.Bits)
                Line("  " + dst + " = " + (field.Optional ? "!" + has + " ? NULL : " : "") + "vm_lub_bits_string(" + src + ");");
            else if (field.Type.Kind == LubTypeKind.Record && field.Optional)
                Line("  " + dst + " = " + has + " ? " + From(field.Type, src) + " : NULL;");
            else if (field.Type.Kind == LubTypeKind.List)
                Line("  " + dst + " = " + ListFrom(field.Type, src, "NULL", field.Optional && field.ArrayLen < 0) + ";");
            else Line("  " + dst + " = " + From(field.Type, src) + ";");
        }
        Line("  return result;\n}");
    }

    private void InputList(string name)
    {
        Line("static Lub" + name + " *vm_lub_to_" + name + "_list(void *source, int32_t *count) {");
        Line("  *count = tvm_list_count(source); Lub" + name + " *items = vm_lub_alloc((size_t)*count * sizeof(*items));");
        Line("  for (int i = 0; i < *count; i++) items[i] = vm_lub_to_" + name + "(((" + Struct(name) + " **)tvm_list_data(source))[i]);");
        Line("  return items;\n}");
    }

    private void OutputList(string name)
    {
        var listType = TypeVar("List<" + Il(name) + ">");
        Line("static void *vm_lub_from_" + name + "_list(void *list, const Lub" + name + " *source, int32_t count) {");
        Line("  if (!list) list = vm_lub_list_new(" + listType + ");");
        Line("  if (!list) return NULL;");
        Line("  int32_t base = tvm_list_count(list);");
        Line("  if (!tvm_list_resize(vm_lub_vm, list, base + count)) { vm_lub_fail(\"allocation-overflow\"); return list; }");
        Line("  for (int i = 0; i < count; i++) ((void **)tvm_list_data(list))[base + i] = vm_lub_from_" + name + "(&source[i]);");
        Line("  return list;\n}");
    }

    private void Handle(ApiType type)
    {
        Line("static " + Struct(type.Name) + " *vm_lub_handle_" + type.Name + "(LubHandle handle) {");
        // 無効な handle (0) は null
        Line("  if (!handle) return NULL;");
        Line("  " + Struct(type.Name) + " *result = vm_lub_new(" + TypeVar(Il(type.Name)) + ", sizeof(*result));");
        Line("  result->host_value = (uint32_t)handle;");
        bool hasVersion = false;
        foreach (var f in type.Fields)
        {
            if (f.Name == "Version") hasVersion = true;
        }
        if (hasVersion)
        {
            Line("  if (handle) {");
            Line("    LubStr key; int32_t version = 0;");
            Line("    lub_gfx_resource_info(vm_lub_context, handle, &key, &version);");
            Line("    result->f_version = version;");
            Line("  }");
        }
        Line("  return result;\n}");
    }

    private void View(ApiType type)
    {
        Line("static " + Struct(type.Name) + " *vm_lub_view_" + type.Name + "(LubView value) {");
        Line("  if (!value.ptr) return NULL;");
        Line("  " + Struct(type.Name) + " *result = vm_lub_new(" + TypeVar(Il(type.Name)) + ", sizeof(*result));");
        Line("  result->host_value = vm_lub_view_store(value); result->f_length = value.len;");
        Line("  return result;\n}");
    }

    // ---- host の関数 ----

    // 引数か戻り値の 1 項 (tvm_host の params / rets。tcs の tcsvm.h)。
    // null になりうる scalar は VM の struct ({ bool has; T v; }) なので、C の layout の塊 S{int?} で渡る
    private static string Token(TypeRef type)
    {
        if (type.Kind == LubTypeKind.Void) return "";
        if (type.IsScalar)
        {
            if (type.Nullable) return "S{" + ScalarVmName(type) + "?}";
            return type.Kind == LubTypeKind.Double ? "f" : type.Kind == LubTypeKind.Bool ? "b" : "i";
        }
        if (type.Kind == LubTypeKind.Struct) return "S{" + Il(type.Name) + (type.Nullable ? "?" : "") + "}";
        if (type.Kind == LubTypeKind.String) return "s";
        return "o";
    }

    // scalar の VM の型の名前 (enum は int)
    private static string ScalarVmName(TypeRef type) =>
        type.Kind == LubTypeKind.Double ? "float" : type.Kind == LubTypeKind.Bool ? "bool" : "int";

    // k 番目の引数 a[k] を C の値にする式
    private static string Unpack(TypeRef type, int k)
    {
        if ((type.IsScalar || type.Kind == LubTypeKind.Struct) && type.Nullable) return "*(const " + Opt(type) + " *)a[" + k + "].p";
        if (type.Kind == LubTypeKind.Struct) return "*(const " + CHeader.TypeName(type.Name) + " *)a[" + k + "].p";
        if (type.Kind == LubTypeKind.Double) return "a[" + k + "].f";
        if (type.Kind == LubTypeKind.Bool) return "a[" + k + "].i != 0";
        if (type.IsScalar) return "a[" + k + "].i";
        return "a[" + k + "].p";
    }

    // 値を戻り値 r[0] に書く (塊の戻り値は r[0].p の先)
    private void PackReturn(TypeRef type, string value)
    {
        if (type.IsScalar && type.Nullable) Line("  *(" + Opt(type) + " *)r[0].p = " + value + ";");
        else if (type.Kind == LubTypeKind.Double) Line("  r[0].f = " + value + ";");
        else if (type.IsScalar) Line("  r[0].i = " + value + ";");
        else Line("  r[0].p = " + value + ";");
    }

    // k 番目の引数の out の場所 (VM が 0 で埋めて渡す)。scalar は C の値の pointer、参照は VM の object を置く slot
    private static string OutPlace(TypeRef type, int k) => Place(type, "a[" + k + "].p");

    // C API が VM の場所に直接書く out (null にならない scalar と struct の record)
    private static bool Direct(TypeRef type) => !type.Nullable && (type.IsScalar || type.Kind == LubTypeKind.Struct);

    // VM が渡す場所 p を C の型の pointer にする式 (scalar と struct の record は値の場所、他は VM の object を置く slot)
    private static string Place(TypeRef type, string p) =>
        (type.IsScalar ? "(" + Scalar(type) + " *)" : type.Kind == LubTypeKind.Struct ? "(" + CHeader.TypeName(type.Name) + " *)" : "(void **)") + p;

    // 場所を 0 にする式
    private static string Zero(TypeRef type) =>
        type.IsScalar ? "0" : type.Kind == LubTypeKind.Struct ? "(" + CHeader.TypeName(type.Name) + "){0}" : "NULL";

    // 外部型の instance method。view の Get(int) は view の byte を読む
    private void Method(ApiType type, ApiFunction method, string hostName)
    {
        var fn = "vm_host_" + hostName.Replace(".", "_");
        if (type.Kind == "view" && method.Name == "Get" && method.Params.Count == 1
            && method.Params[0].Type.Kind == LubTypeKind.Int && method.Return.Kind == LubTypeKind.Int)
        {
            Line("static TvmStatus " + fn + "(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {");
            Line("  (void)user; VM_LUB_ENTER();");
            Line("  " + Struct(type.Name) + " *self = a[0].p; int32_t p_index = a[1].i;");
            Line("  if (!self) { vm_lub_fail(\"null reference\"); VM_LUB_LEAVE(vm); }");
            Line("  LubView view = vm_lub_view_load(self->host_value);");
            Line("  if (vm_lub_error) VM_LUB_LEAVE(vm);");
            Line("  if (p_index < 0 || p_index >= view.len) { vm_lub_fail(\"view index out of range\"); VM_LUB_LEAVE(vm); }");
            Line("  r[0].i = view.ptr[p_index];");
            Line("  VM_LUB_LEAVE(vm);\n}");
            registrations.Add("{\"" + hostName + "\", \"oi\", \"i\", " + fn + "}");
        }
        else GenError.Report("unsupported method: " + type.Name + "." + method.Name);
    }

    // C の値で受ける out は変数 n_<name> に受ける。list は pointer と n_<name>_count の 2 つ。null になりうる record は has で受ける
    private void Output(TypeRef type, string n, CallState st)
    {
        Line("  " + NativeType(type) + " " + n + " = {0};");
        st.Args.Add("&" + n);
        if (type.Kind == LubTypeKind.Record && type.Nullable)
        {
            Line("  bool " + n + "_has = false;");
            st.Args.Add("&" + n + "_has");
        }
        if (type.Kind != LubTypeKind.List) return;
        Line("  int32_t " + n + "_count = 0;");
        st.Args.Add("&" + n + "_count");
    }

    private void Function(ApiNamespace ns, ApiFunction function)
    {
        var name = CHeader.FunctionName(ns, function.LuaName);
        var hostName = ns.LuaPath + "." + function.LuaName;
        foreach (var param in function.Params)
        {
            if (param.Type.Kind == LubTypeKind.Func) Callback(name, param);
        }
        var fn = "vm_host_" + name;
        Line("static TvmStatus " + fn + "(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {");
        Line("  (void)a; (void)r; (void)user; VM_LUB_ENTER();");
        // 引数は C# の順に 1 つずつ a[k] に来る。out (&) の a[k].p は VM が値を受ける場所
        var tokens = new List<string>();
        for (int k = 0; k < function.Params.Count; k++)
        {
            var p = function.Params[k];
            tokens.Add((p.IsOut ? "&" : "") + Token(p.Type));
            if (!p.IsOut) Line("  " + VmType(p.Type) + " p_" + p.LuaName + " = " + Unpack(p.Type, k) + ";");
        }
        var st = new CallState();
        var args = st.Args;
        args.Add("vm_lub_context");
        string? nativeName = null;
        string? nativeParam = null;
        foreach (var param in function.Params)
        {
            if (param.IsOut || param.IsCount) continue;
            var p = "p_" + param.LuaName;
            var n = "n_" + param.LuaName;
            var type = param.Type;
            ApiParam? countParam = null;
            int index = function.Params.IndexOf(param);
            for (int k = index + 1; k < function.Params.Count; k++)
            {
                if (function.Params[k].IsCount)
                {
                    countParam = function.Params[k];
                    break;
                }
            }
            if (type.Kind == LubTypeKind.Array && type.Elem!.Kind == LubTypeKind.Double && countParam != null)
            {
                var count = "p_" + countParam.LuaName;
                Line("  if (" + p + " && (uint32_t)" + count + " > (uint32_t)tvm_array_len(" + p + ")) { vm_lub_fail(\"" + hostName + ": count is out of range for the array\"); VM_LUB_LEAVE(vm); }");
                args.Add(p + " ? (const float *)tvm_array_data(" + p + ") : NULL");
                args.Add(count);
            }
            else if (type.Kind == LubTypeKind.Record)
            {
                Line("  Lub" + type.Name + " " + n + " = vm_lub_to_" + type.Name + "(" + p + ");");
                args.Add(p + " ? &" + n + " : NULL");
            }
            else if (type.Kind == LubTypeKind.List && type.Elem!.IsScalar)
            {
                args.Add(p + " ? tvm_list_data(" + p + ") : NULL");
                args.Add(p + " ? tvm_list_count(" + p + ") : 0");
            }
            else if (type.Kind == LubTypeKind.List && type.Elem!.Kind == LubTypeKind.Record)
            {
                Line("  int32_t " + n + "_count = 0; Lub" + type.Elem.Name + " *" + n + " = " + p + " ? vm_lub_to_" + type.Elem.Name + "_list(" + p + ", &" + n + "_count) : NULL;");
                args.Add(n);
                args.Add(n + "_count");
            }
            else if (type.Kind == LubTypeKind.View)
            {
                Line("  LubView " + n + " = {0};");
                Line("  if (" + p + ") " + n + " = vm_lub_view_load(" + p + "->host_value);");
                args.Add(n + ".ptr");
                args.Add(n + ".len");
            }
            else if (type.Kind == LubTypeKind.Native)
            {
                Line("  if (!" + p + ") { vm_lub_fail(\"null reference\"); VM_LUB_LEAVE(vm); }");
                Line("  LubBindings *" + n + " = vm_lub_bindings_block(" + p + ");");
                args.Add(n);
                nativeName = n;
                nativeParam = p;
            }
            else if (type.Kind == LubTypeKind.Array && type.Elem!.Kind == LubTypeKind.Double)
            {
                Line("  if (!" + p + ") { vm_lub_fail(\"null reference\"); VM_LUB_LEAVE(vm); }");
                args.Add("(const float *)tvm_array_data(" + p + ")");
                args.Add("tvm_array_len(" + p + ")");
            }
            else if (type.Kind == LubTypeKind.Struct && !type.Nullable) args.Add(p);
            else if (type.IsScalar && type.Nullable) args.Add(p + ".has ? &" + p + ".v : NULL");
            else if (type.Kind == LubTypeKind.Func)
            {
                args.Add(p + " ? " + CallbackName(name, param) + " : NULL");
                args.Add(p);
            }
            else
            {
                // null を受けない handle は誤り
                if (type.Kind == LubTypeKind.Handle && !type.Nullable && !(param.Optional && !param.HasDefault))
                    Line("  if (!" + p + ") { vm_lub_fail(\"" + hostName + ": " + param.Name + " is null\"); VM_LUB_LEAVE(vm); }");
                args.Add(To(type, p));
            }
        }
        // 入力の変換の誤り (配列の長さ、bit mask) は C API を呼ぶ前に止める
        Line("  if (vm_lub_error) VM_LUB_LEAVE(vm);");
        // scalar と struct の record の out は VM の場所に C API が直接書く。他は C の値で受け、VM の値にして場所に置く
        for (int k = 0; k < function.Params.Count; k++)
        {
            var param = function.Params[k];
            if (!param.IsOut) continue;
            if (Direct(param.Type)) args.Add(OutPlace(param.Type, k));
            else if (param.Type.IsScalar || param.Type.Kind == LubTypeKind.Struct)
            {
                GenError.Report("unsupported out: " + param.Name + ": " + param.Type.ToString());
                return;
            }
            else Output(param.Type, "n_" + param.LuaName, st);
        }
        var ret = function.Return;
        bool returned = ret.Kind != LubTypeKind.Void;
        bool byValue = function.NoFail && (ret.IsScalar || ret.Kind == LubTypeKind.Handle);
        if (byValue && ret.IsScalar && ret.Nullable)
        {
            GenError.Report("nullable NoFail return: " + ret.ToString());
            return;
        }
        bool has = !function.NoFail && ret.Nullable && (ret.IsScalar || (ret.Kind == LubTypeKind.Record && function.Maybe));
        // null になりうる scalar の戻り値は r[0].p の塊 ({ bool has; T v; }) で、C API が直接書く
        bool block = has && ret.IsScalar;
        // struct の record の戻り値も r[0].p の塊で、C API の out に直接書かせる
        bool structRet = ret.Kind == LubTypeKind.Struct;
        if (structRet && ret.Nullable)
        {
            GenError.Report("unsupported return: " + ret.ToString());
            return;
        }
        if (byValue) Line("  " + NativeType(ret) + " result = {0};");
        else if (block)
        {
            Line("  " + Opt(ret) + " *result = r[0].p;");
            args.Add("&result->v");
        }
        else if (structRet) args.Add(Place(ret, "r[0].p"));
        else if (returned) Output(new TypeRef(ret.Kind, ret.Name, false, ret.Elem, ret.FuncParams, ret.FuncReturn), "result", st);
        if (block) args.Add("&result->has");
        else if (has)
        {
            Line("  bool has = false;");
            args.Add("&has");
        }
        var call = name + "(" + string.Join(", ", args) + ")";
        bool visits = false;
        foreach (var param in function.Params)
        {
            if (param.Type.Kind == LubTypeKind.Func) visits = true;
        }
        if (visits) Line("  bool vm_lub_visitor_outer = vm_lub_visitor_failed; vm_lub_visitor_failed = false;");
        if (function.NoFail) Line("  " + (byValue ? "result = " : "") + call + ";");
        else Line("  LubStatus status = " + call + ";");
        // visitor が失敗した query は null (戻り値と out は 0)
        if (visits)
        {
            Line("  if (vm_lub_visitor_failed) {");
            Line("    fprintf(stderr, \"lub: " + hostName + " visitor: %s\\n\", vm_lub_visitor_text);");
            Line("    vm_lub_visitor_failed = vm_lub_visitor_outer;");
            if (block) Line("    *result = (" + Opt(ret) + "){0};");
            else if (structRet) Line("    *" + Place(ret, "r[0].p") + " = " + Zero(ret) + ";");
            else if (returned) Line("    r[0].u = 0;");
            for (int k = 0; k < function.Params.Count; k++)
            {
                var param = function.Params[k];
                if (param.IsOut) Line("    *" + OutPlace(param.Type, k) + " = " + Zero(param.Type) + ";");
            }
            Line("    VM_LUB_LEAVE(vm);");
            Line("  }");
            Line("  vm_lub_visitor_failed = vm_lub_visitor_outer;");
        }
        if (!function.NoFail)
        {
            if (nativeName != null)
                Line("  while (status == LUB_BINDINGS_FULL) { " + nativeName + " = vm_lub_bindings_grow(" + nativeParam + "); status = " + call + "; }");
            Line("  vm_lub_check(status);");
            Line("  if (vm_lub_error) VM_LUB_LEAVE(vm);");
        }
        // 見つからない (LUB_NOT_FOUND) か値の無い結果は null で、struct の record は 0 の値。scalar の out はそのとき C API が書いたまま
        string? missing = null;
        if (!function.NoFail)
        {
            if (returned && !ret.IsScalar) missing = "status == LUB_NOT_FOUND";
            if (has && !ret.IsScalar) missing = missing == null ? "!has" : missing + " || !has";
        }
        if (missing != null)
        {
            if (structRet) Line("  if (" + missing + ") *" + Place(ret, "r[0].p") + " = " + Zero(ret) + ";");
            for (int k = 0; k < function.Params.Count; k++)
            {
                var param = function.Params[k];
                if (param.IsOut && param.Type.Kind == LubTypeKind.Struct) Line("  if (" + missing + ") *" + OutPlace(param.Type, k) + " = " + Zero(param.Type) + ";");
            }
        }
        if (returned && !block && !structRet)
        {
            var value = From(ret, "result");
            if (missing != null) value = "(" + missing + ") ? NULL : " + value;
            PackReturn(ret, value);
        }
        for (int k = 0; k < function.Params.Count; k++)
        {
            var param = function.Params[k];
            if (!param.IsOut || Direct(param.Type)) continue;
            var n = "n_" + param.LuaName;
            var value = From(param.Type, n);
            if (param.Type.Kind == LubTypeKind.Record && param.Type.Nullable) value = n + "_has ? " + value + " : NULL";
            if (missing != null) value = "(" + missing + ") ? NULL : " + value;
            Line("  *" + OutPlace(param.Type, k) + " = " + value + ";");
        }
        Line("  VM_LUB_LEAVE(vm);\n}");
        registrations.Add("{\"" + hostName + "\", \"" + string.Join("", tokens) + "\", \"" + Token(ret) + "\", " + fn + "}");
    }

    // Func 引数は C の関数 pointer + user (VM の closure) に写す。C API が呼び出しの間だけ呼ぶ
    private static string CallbackName(string function, ApiParam param) => "vm_lub_" + function + "_" + param.LuaName;

    private void Callback(string function, ApiParam param) =>
        Trampoline(CallbackName(function, param), param.Type, param.Name, "", "vm_lub_visitor_failed", "user", "vm_lub_visitor_fail()");

    // callback の既定の値 (呼べないか失敗したとき)
    private static string CallbackDefault(TypeRef ret) =>
        ret.Kind == LubTypeKind.Bool ? "true" : ret.Kind == LubTypeKind.Double ? "1.0f" : ret.Kind == LubTypeKind.Int ? "1" : "";

    // C の関数 pointer から VM の closure を呼ぶ。prologue が closure を用意し、skip なら呼ばずに既定の値を返す
    private void Trampoline(string cname, TypeRef fn, string what, string prologue, string skip, string closure, string onFault)
    {
        var ret = fn.FuncReturn!;
        if (ret.Kind != LubTypeKind.Void && !ret.IsScalar)
        {
            GenError.Report("unsupported callback return: " + what + ": " + fn.ToString());
            return;
        }
        var cParams = new List<string>();
        cParams.Add("void *user");
        var sets = new List<string>();
        int at = 0;
        for (int i = 0; i < fn.FuncParams!.Count; i++)
        {
            var type = fn.FuncParams[i];
            var a = Text.Sub("abcdefghijklmnopqrstuvwxyz", i, 1);
            if (type.Kind == LubTypeKind.Record)
            {
                cParams.Add("const Lub" + type.Name + " *" + a);
                sets.Add("args[" + at + "].p = vm_lub_from_" + type.Name + "(" + a + ");");
            }
            else if (type.IsScalar && !type.Nullable)
            {
                cParams.Add(Scalar(type) + " " + a);
                sets.Add("args[" + at + "]." + (type.Kind == LubTypeKind.Double ? "f" : "i") + " = " + a + ";");
            }
            else
            {
                GenError.Report("unsupported callback parameter: " + what + ": " + fn.ToString());
                return;
            }
            at++;
        }
        var cRet = ret.Kind == LubTypeKind.Void ? "void" : Scalar(ret);
        var giveUp = "return" + (cRet == "void" ? "" : " " + CallbackDefault(ret)) + ";";
        Line("static " + cRet + " " + cname + "(" + string.Join(", ", cParams) + ") {");
        Line("  TvmSlot args[" + (at == 0 ? 1 : at) + "] = {{0}}, rets[1] = {{0}};");
        if (prologue != "") Line("  " + prologue);
        // 前の呼び出しが失敗したら VM に戻らない
        Line("  if (vm_lub_error || " + skip + ") " + giveUp);
        foreach (var st in sets) Line("  " + st);
        Line("  if (tvm_invoke(vm_lub_vm, " + closure + ", args, " + at + ", rets, " + (ret.Kind == LubTypeKind.Void ? "0" : "1") + ") != TVM_OK) { " + onFault + "; " + giveUp + " }");
        if (cRet == "float") Line("  return rets[0].f;");
        else if (cRet == "bool") Line("  return rets[0].i != 0;");
        else if (cRet != "void") Line("  return rets[0].i;");
        Line("}");
    }

    // Bindings の中身の block は GC の blob に置く (field の blob で辿れるので、手放せば GC が回収する)
    private void NativeBindings()
    {
        Line("static LubBindings *vm_lub_bindings_alloc(VmLub_Bindings *owner, int32_t scale) {");
        Line("  void *block = tvm_blob_new(vm_lub_vm, lub_bindings_size(scale));");
        Line("  if (!block) { vm_lub_fail(\"allocation-overflow\"); return NULL; }");
        Line("  owner->f_blob = block;");
        Line("  lub_bindings_init((LubBindings *)block, scale);");
        Line("  return (LubBindings *)block;\n}");
        Line("static LubBindings *vm_lub_bindings_block(VmLub_Bindings *owner) {");
        Line("  return owner->f_blob ? (LubBindings *)owner->f_blob : vm_lub_bindings_alloc(owner, 1);\n}");
        Line("static LubBindings *vm_lub_bindings_grow(VmLub_Bindings *owner) {");
        Line("  LubBindings *old = (LubBindings *)owner->f_blob;");
        Line("  LubBindings *grown = vm_lub_bindings_alloc(owner, lub_bindings_scale(old) * 2);");
        Line("  if (!grown) return old;");
        Line("  lub_bindings_grow_into(grown, old);");
        Line("  return grown;\n}");
    }

    // VM_LUB_ENTER / VM_LUB_LEAVE: host の関数の入口と出口。入れ子 (callback から VM、VM から host の関数) の誤りと一時領域を分ける。
    // 誤りは vm_lub_fail で積み、出口で host の関数の fault (tvm_host_fault) にする
    private const string Helpers = """
        #if defined(__GNUC__) || defined(__clang__)
        #define VM_LUB_UNUSED __attribute__((unused))
        #else
        #define VM_LUB_UNUSED
        #endif
        typedef struct VmLubOptI32 { bool has; int32_t v; } VmLubOptI32;
        typedef struct VmLubOptF32 { bool has; float v; } VmLubOptF32;
        typedef struct VmLubOptBool { bool has; bool v; } VmLubOptBool;
        static LubContext *vm_lub_context;
        static TvmVm *vm_lub_vm;
        static const char *vm_lub_error;
        static char vm_lub_error_text[1024];
        static void **vm_lub_temps;
        static size_t vm_lub_ntemps, vm_lub_ctemps;
        static void vm_lub_fail(const char *message) {
          if (!vm_lub_error) vm_lub_error = message;
        }
        static void vm_lub_fail_text(const char *message) {
          if (vm_lub_error) return;
          snprintf(vm_lub_error_text, sizeof vm_lub_error_text, "%s", message);
          vm_lub_error = vm_lub_error_text;
        }
        /* callback の中の VM の fault (trace は tvm_error) */
        static void vm_lub_fail_vm(void) { vm_lub_fail_text(tvm_error(vm_lub_vm)); }
        /* query の visitor の fault。query は fault にせず null を返す (message は log に出す) */
        static bool vm_lub_visitor_failed;
        static char vm_lub_visitor_text[1024];
        static void vm_lub_visitor_fail(void) {
          if (vm_lub_visitor_failed) return;
          snprintf(vm_lub_visitor_text, sizeof vm_lub_visitor_text, "%s", tvm_error(vm_lub_vm));
          vm_lub_visitor_failed = true;
        }
        /* record の callback の field (world の Filter など): runtime が持ち続けるので、closure を tvm_hold で持つ。
           runtime が手放すとき (user_release) に放す。作った VM でなければ呼ばず、放さない */
        typedef struct VmLubHeld { TvmVm *vm; int n; TvmHandle h[8]; bool logged[8]; } VmLubHeld;
        static VmLubHeld *vm_lub_held_new(void) {
          VmLubHeld *held = calloc(1, sizeof(*held));
          if (!held) abort();
          held->vm = vm_lub_vm;
          return held;
        }
        static void VM_LUB_UNUSED vm_lub_held_release(void *user) {
          VmLubHeld *held = user;
          if (held->vm == vm_lub_vm && vm_lub_vm)
            for (int i = 0; i < 8; i++) if (held->h[i]) tvm_release(vm_lub_vm, held->h[i]);
          free(held);
        }
        static void *vm_lub_held_get(VmLubHeld *held, int i) {
          return held->vm == vm_lub_vm && vm_lub_vm && held->h[i] ? tvm_handle_get(vm_lub_vm, held->h[i]) : NULL;
        }
        /* fault は callback ごとに 1 回だけ log に出し、既定の値で続ける */
        static void vm_lub_held_fault(VmLubHeld *held, int i) {
          if (held->logged[i]) return;
          held->logged[i] = true;
          fprintf(stderr, "lub: callback error: %s\n", tvm_error(vm_lub_vm));
        }
        static void vm_lub_check(LubStatus status) {
          if (status == LUB_ERROR) {
            if (vm_lub_error) return;
            snprintf(vm_lub_error_text, sizeof vm_lub_error_text, "lub-api: %s", lub_last_error(vm_lub_context));
            vm_lub_error = vm_lub_error_text;
          }
        }
        /* host の関数の間だけの C の memory (入力の record の list など) */
        static void *vm_lub_alloc(size_t size) {
          if (vm_lub_ntemps == vm_lub_ctemps) {
            size_t cap = vm_lub_ctemps ? vm_lub_ctemps * 2 : 16;
            void **grown = realloc(vm_lub_temps, cap * sizeof(void *));
            if (!grown) abort();
            vm_lub_temps = grown;
            vm_lub_ctemps = cap;
          }
          void *p = calloc(1, size ? size : 1);
          if (!p) abort();
          vm_lub_temps[vm_lub_ntemps++] = p;
          return p;
        }
        static void vm_lub_free_to(size_t mark) {
          while (vm_lub_ntemps > mark) free(vm_lub_temps[--vm_lub_ntemps]);
        }
        #define VM_LUB_ENTER() size_t vm_lub_mark = vm_lub_ntemps; const char *vm_lub_outer = vm_lub_error; vm_lub_error = NULL
        #define VM_LUB_LEAVE(vm) return vm_lub_leave(vm, vm_lub_mark, vm_lub_outer)
        static TvmStatus vm_lub_leave(TvmVm *vm, size_t mark, const char *outer) {
          vm_lub_free_to(mark);
          const char *error = vm_lub_error;
          vm_lub_error = outer;
          return error ? tvm_host_fault(vm, error) : TVM_OK;
        }
        /* VM の型 (読み込みと reload で作り直すので版で引き直す) */
        typedef struct VmLubType { const char *name; uint32_t version; const TvmTypeInfo *type; } VmLubType;
        static const TvmTypeInfo *vm_lub_type(VmLubType *t) {
          uint32_t version = tvm_types_version(vm_lub_vm);
          if (t->version != version) {
            t->type = tvm_type(vm_lub_vm, t->name);
            t->version = version;
          }
          if (!t->type && !vm_lub_error) {
            snprintf(vm_lub_error_text, sizeof vm_lub_error_text, "lub: the module has no type %s", t->name);
            vm_lub_error = vm_lub_error_text;
          }
          return t->type;
        }
        /* 型が無ければ (誤りは積んである) 捨てる C の memory を返し、呼び出し側の書き込みを受ける */
        static void *vm_lub_new(VmLubType *t, size_t size) {
          const TvmTypeInfo *type = vm_lub_type(t);
          void *o = type ? tvm_object_new_of(vm_lub_vm, type) : NULL;
          return o ? o : vm_lub_alloc(size);
        }
        static void *vm_lub_list_new(VmLubType *t) {
          const TvmTypeInfo *type = vm_lub_type(t);
          return type ? tvm_list_new_of(vm_lub_vm, type, 0) : NULL;
        }
        static void *vm_lub_array(VmLubType *t, int32_t len) {
          const TvmTypeInfo *type = vm_lub_type(t);
          return type ? tvm_array_new_of(vm_lub_vm, type, len) : NULL;
        }
        /* keyed の object の key (host_value は表の番号 + 1。key は少なく、表は process の間残す) */
        static char **vm_lub_keys;
        static uint32_t vm_lub_nkeys;
        static LubStr vm_lub_key_load(uint64_t host_value) {
          if (host_value == 0 || host_value > vm_lub_nkeys) { vm_lub_fail("null reference"); return (LubStr){0}; }
          const char *key = vm_lub_keys[host_value - 1];
          return (LubStr){key, (int32_t)strlen(key)};
        }
        static uint64_t vm_lub_key_store(LubStr key) {
          for (uint32_t i = 0; i < vm_lub_nkeys; i++)
            if ((int32_t)strlen(vm_lub_keys[i]) == key.len && memcmp(vm_lub_keys[i], key.ptr, (size_t)key.len) == 0) return i + 1;
          char **grown = realloc(vm_lub_keys, (vm_lub_nkeys + 1) * sizeof(char *));
          char *copy = malloc((size_t)key.len + 1);
          if (!grown || !copy) abort();
          memcpy(copy, key.ptr, (size_t)key.len);
          copy[key.len] = 0;
          vm_lub_keys = grown;
          vm_lub_keys[vm_lub_nkeys++] = copy;
          return vm_lub_nkeys;
        }
        static LubStr vm_lub_str(void *text) {
          size_t len = 0;
          const char *data = tvm_string_data(text, &len);
          return data ? (LubStr){data, (int32_t)len} : (LubStr){0};
        }
        static void *vm_lub_string(LubStr text) {
          return text.ptr ? tvm_string_new(vm_lub_vm, text.ptr, (size_t)text.len) : NULL;
        }
        static void *vm_lub_list(VmLubType *t, void *list, const void *data, int32_t count, size_t size) {
          if (!list) list = vm_lub_list_new(t);
          if (!list) return NULL;
          if (!tvm_list_resize(vm_lub_vm, list, count)) { vm_lub_fail("allocation-overflow"); return list; }
          if (count) memcpy(tvm_list_data(list), data, (size_t)count * size);
          return list;
        }
        static void *vm_lub_string_list(VmLubType *t, void *list, const LubStr *data, int32_t count) {
          if (!list) list = vm_lub_list_new(t);
          if (!list) return NULL;
          if (!tvm_list_resize(vm_lub_vm, list, count)) { vm_lub_fail("allocation-overflow"); return list; }
          void **items = (void **)tvm_list_data(list);
          for (int32_t i = 0; i < count; i++) items[i] = vm_lub_string(data[i]);
          return list;
        }
        """;

    // [LubBits] の 64 bit mask は C# 面では hex 文字列。規則は tcs2c の接続コードと同じ
    private const string BitsHelpers = """
        #include <inttypes.h>
        static void vm_lub_bits_fault(const char *what, LubStr text) {
          if (vm_lub_error) return;
          snprintf(vm_lub_error_text, sizeof vm_lub_error_text, "bit mask: %s '%.*s'", what, (int)text.len, text.ptr);
          vm_lub_error = vm_lub_error_text;
        }
        static bool vm_lub_bits(void *string, uint64_t *out) {
          if (!string) return false;
          LubStr text = vm_lub_str(string);
          int32_t length = text.len, i = 0;
          if (length >= 2 && text.ptr[0] == '0' && (text.ptr[1] == 'x' || text.ptr[1] == 'X')) i = 2;
          if (i == length) { vm_lub_bits_fault("empty hex string", text); return false; }
          if (length - i > 16) { vm_lub_bits_fault("more than 16 hex digits", text); return false; }
          uint64_t value = 0;
          for (; i < length; i++) {
            unsigned char c = (unsigned char)text.ptr[i];
            int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (digit < 0) { vm_lub_bits_fault("invalid hex", text); return false; }
            value = value << 4 | (uint64_t)digit;
          }
          *out = value;
          return true;
        }
        static void *vm_lub_bits_string(uint64_t value) {
          char text[17];
          snprintf(text, sizeof(text), "%016" PRIx64, value);
          return tvm_string_new(vm_lub_vm, text, 16);
        }
        """;

    // view は frame の終わりまで有効。VM の object は frame を跨いで残りうるので、実体は host の表に置き、host_value は frame と番号
    private const string ViewHelpers = """
        static LubView *vm_lub_views;
        static uint32_t vm_lub_view_count, vm_lub_view_capacity;
        static int32_t vm_lub_view_frame;
        static uint64_t vm_lub_view_store(LubView value) {
          int32_t frame = lub_frame_index(vm_lub_context);
          if (frame != vm_lub_view_frame) { vm_lub_view_frame = frame; vm_lub_view_count = 0; }
          if (vm_lub_view_count == vm_lub_view_capacity) {
            vm_lub_view_capacity = vm_lub_view_capacity ? vm_lub_view_capacity * 2 : 16;
            vm_lub_views = realloc(vm_lub_views, vm_lub_view_capacity * sizeof(*vm_lub_views));
            if (!vm_lub_views) abort();
          }
          vm_lub_views[vm_lub_view_count] = value;
          return (uint64_t)(uint32_t)frame << 32 | vm_lub_view_count++;
        }
        static LubView vm_lub_view_load(uint64_t host_value) {
          int32_t frame = lub_frame_index(vm_lub_context);
          uint32_t index = (uint32_t)host_value;
          if ((int32_t)(host_value >> 32) != frame || frame != vm_lub_view_frame || index >= vm_lub_view_count
              || vm_lub_views[index].frame != frame) {
            vm_lub_fail("stale view: a view is valid until the end of its frame (copy what you keep)");
            return (LubView){0};
          }
          return vm_lub_views[index];
        }
        """;
}
