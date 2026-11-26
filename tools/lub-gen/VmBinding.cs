using System.Collections.Generic;
using TcsSelf;

namespace LubGen;

// Generates the tcs VM (tcs/tcsvm) host functions: C that registers the lub C API as host functions (tvm_host) the VM calls.
// Registers every function in the stub (the player does not pick a game). Only when --source is given, narrows them to the functions that game uses (for tests).
// The VM object layout, including host data classes, follows the same C struct rules as tcs2c (tcs docs/tcs/object-model.md §3, §5), so
// records are read and written through C struct mirrors (VmLub_<type>). After loading and after reload, vm_lub_verify compares the offsets against the VM type table.
// Arguments are passed one by one in C# order (tcs vm-design §8.5), out (&) is a pointer to where the VM receives the value, and rets holds only the return value.
// Struct records are values with the same layout as the C API struct even inside the VM, so the S{Lub_<type>} block is read and written directly as the C API struct.
// The conversion rules are the same as the tcs2c glue code (TcsBinding). Errors where tcs2c stops the process (tcs_fault) become a fault of that host function.
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
    // VM type name → C cache variable (looked up with vm_lub_type)
    private List<string> typeNames = new List<string>();
    // Host functions to register: IL name, argument leaves, return leaves, C function
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

    // C type of a VM value (fields and arguments)
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
                // Struct records are values in C layout even inside the VM (same order as the C API struct; vm_lub_verify checks it)
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

    // Name in the VM type table (tcs VType.Display)
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

    // Type cache variables (one per VM type name)
    private string TypeVar(string vmName)
    {
        int at = typeNames.IndexOf(vmName);
        if (at < 0)
        {
            at = typeNames.Count;
            typeNames.Add(vmName);
        }
        return "&s->types[" + at + "]";
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
        foreach (var field in type.Fields) r.Add(new FieldPath(field, prefix + field.HostName));
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

    // Functions to call (when a game is given, only those that game uses)
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

    // IL name of an instance method of a foreign type ("lub.bytes.get")
    private static string MethodHostName(ApiType type, ApiFunction method) =>
        ApiModelLoader.RootNamespace.ToLower() + "." + type.Name.ToLower() + "." + method.HostName;

    public string Run()
    {
        var functionNs = new List<ApiNamespace>();
        var functions = new List<ApiFunction>();
        foreach (var ns in model.Namespaces)
        {
            foreach (var f in ns.Functions)
            {
                if (!Wanted(ns.HostPath + "." + f.HostName)) continue;
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
        // The OnEvent argument is built by the host from LubEventData
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
        foreach (var name in inputs) Line("static Lub" + name + " vm_lub_to_" + name + "(VmLub *s, " + Struct(name) + " *source);");
        foreach (var name in outputs) Line("VM_LUB_UNUSED static " + Struct(name) + " *vm_lub_from_" + name + "(VmLub *s, const Lub" + name + " *source);");
        foreach (var name in inputLists) Line("static Lub" + name + " *vm_lub_to_" + name + "_list(VmLub *s, void *source, int32_t *count);");
        foreach (var name in outputLists) Line("static void *vm_lub_from_" + name + "_list(VmLub *s, void *list, const Lub" + name + " *source, int32_t count);");
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
                var hostName = ns.HostPath + "." + value.HostName;
                if (!WantedValue(hostName)) continue;
                var name = CHeader.FunctionName(ns, value.HostName);
                var fn = "vm_host_" + name;
                Line("static TvmStatus " + fn + "(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {");
                Line("  (void)a; VM_LUB_ENTER();");
                PackReturn(value.Type, From(value.Type, name + "(s->ctx)"));
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
        // Type caches are emitted after the names the body uses are known
        Line("static const char *const vm_lub_type_names[" + (typeNames.Count == 0 ? 1 : typeNames.Count) + "] = {");
        foreach (var n in typeNames) Line("  \"" + n + "\",");
        if (typeNames.Count == 0) Line("  \"\",");
        Line("};");
        output.Write(body.ToString());
        Line("/* Sets up the state (once, on a zero-filled VmLub). Returns 0 on failure */");
        Line("static int vm_lub_init(VmLub *s, LubContext *ctx) {");
        Line("  size_t n = sizeof(vm_lub_type_names) / sizeof(vm_lub_type_names[0]);");
        Line("  s->ctx = ctx;");
        Line("  s->types = calloc(n, sizeof(VmLubType));");
        Line("  if (!s->types) return 0;");
        Line("  for (size_t i = 0; i < n; i++) s->types[i].name = vm_lub_type_names[i];");
        Line("  return 1;\n}");
        Line("/* Turns EventData into a VM object (OnEvent in src/vm_player.c) */");
        Line("static void *vm_lub_event(VmLub *s, const LubEventData *e) { return vm_lub_from_EventData(s, e); }");
        Line("static const struct { const char *name, *params, *rets; TvmHostFn fn; } vm_lub_hosts[] = {");
        foreach (var r in registrations) Line("  " + r + ",");
        Line("  {NULL, NULL, NULL, NULL},");
        Line("};");
        Line("/* Registers all host functions (before tvm_load). The caller sets s->vm. Returns 0 on failure */");
        Line("static int vm_lub_register(VmLub *s, TvmVm *vm) {");
        Line("  for (int i = 0; vm_lub_hosts[i].name; i++)");
        Line("    if (!tvm_host(vm, vm_lub_hosts[i].name, vm_lub_hosts[i].params, vm_lub_hosts[i].rets, vm_lub_hosts[i].fn, s)) return 0;");
        Line("  return 1;\n}");
        return output.ToString();
    }

    // Keyed creation for [LubNoC] (`Readback Gfx.Readback(string key)`): there is no C API; returns an object placed in the host table under key
    private static bool IsKeyedMaker(ApiFunction f) =>
        f.NoC && f.Return.Kind == LubTypeKind.Keyed && f.Params.Count == 1 && f.Params[0].Type.Kind == LubTypeKind.String;

    private void KeyedMaker(ApiNamespace ns, ApiFunction f)
    {
        var fn = "vm_host_" + CHeader.FunctionName(ns, f.HostName);
        Line("static TvmStatus " + fn + "(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {");
        Line("  VM_LUB_ENTER();");
        Line("  if (!a[0].p) { vm_lub_fail(s, \"null reference\"); VM_LUB_LEAVE(vm); }");
        Line("  " + Struct(f.Return.Name) + " *result = vm_lub_new(s, " + TypeVar(Il(f.Return.Name)) + ", sizeof(*result));");
        Line("  result->host_value = vm_lub_key_store(s, vm_lub_str(a[0].p));");
        Line("  r[0].p = result;");
        Line("  VM_LUB_LEAVE(vm);\n}");
        registrations.Add("{\"" + ns.HostPath + "." + f.HostName + "\", \"s\", \"o\", " + fn + "}");
    }

    // ---- C struct mirrors ----

    private static bool IsObjectType(ApiType t) =>
        t.Kind == "record" || t.Kind == "handle" || t.Kind == "view" || t.Kind == "native" || t.Kind == "keyed";

    private void Structs()
    {
        foreach (var t in model.Types)
        {
            if (IsObjectType(t)) Line("typedef struct " + Struct(t.Name) + " " + Struct(t.Name) + ";");
        }
        var checks = new List<string>();
        // Struct records are values with the same layout as the C API struct even inside the VM. T? is { bool has; T v; }
        foreach (var t in model.Types)
        {
            if (t.Kind != "struct") continue;
            var opt = "VmLubOpt_" + t.Name;
            Line("typedef struct " + opt + " { bool has; " + CHeader.TypeName(t.Name) + " v; } " + opt + ";");
            foreach (var f in t.Fields) checks.Add("{\"" + Il(t.Name) + "\", \"" + f.HostName + "\", offsetof(" + CHeader.TypeName(t.Name) + ", " + f.HostName + ")}");
            OptChecks(Il(t.Name), opt, checks);
        }
        foreach (var t in model.Types)
        {
            if (!IsObjectType(t)) continue;
            // The payload is type_id, host_value aligned to 8 bytes, then the fields (starting from the base)
            Line("struct " + Struct(t.Name) + " {");
            Line("  uint32_t type_id;");
            Line("  uint64_t host_value;");
            foreach (var fp in Fields(t))
            {
                Line("  " + VmType(fp.Field.Type) + " f_" + fp.Field.HostName + ";");
                checks.Add("{\"" + Il(t.Name) + "\", \"" + fp.Field.HostName + "\", offsetof(" + Struct(t.Name) + ", f_" + fp.Field.HostName + ")}");
            }
            // Private fields of the stub (Bindings blob)
            if (t.Kind == "native")
            {
                Line("  void *f_blob;");
                checks.Add("{\"" + Il(t.Name) + "\", \"blob\", offsetof(" + Struct(t.Name) + ", f_blob)}");
            }
            Line("};");
        }
        // Block of nullable scalars (S{int?} for arguments and return values, record fields)
        OptChecks("int", "VmLubOptI32", checks);
        OptChecks("float", "VmLubOptF32", checks);
        OptChecks("bool", "VmLubOptBool", checks);
        Line("_Static_assert(offsetof(VmLub_Bindings, host_value) == 8, \"the VM puts host_value 8 bytes into the payload\");");
        Line("/* For the types the module has, checks that the offsets in the VM type table match the C structs (after loading and reload) */");
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

    // VM T? ({ bool HasValue; T Value; }) and the field of the C mirror
    private static void OptChecks(string scalar, string cType, List<string> checks)
    {
        checks.Add("{\"" + scalar + "?\", \"HasValue\", offsetof(" + cType + ", has)}");
        checks.Add("{\"" + scalar + "?\", \"Value\", offsetof(" + cType + ", v)}");
    }

    // ---- Value conversion ----

    private string From(TypeRef type, string value)
    {
        switch (type.Kind)
        {
            case LubTypeKind.String:
                return "vm_lub_string(s, " + value + ")";
            case LubTypeKind.Handle:
                return "vm_lub_handle_" + type.Name + "(s, " + value + ")";
            case LubTypeKind.View:
                return "vm_lub_view_" + type.Name + "(s, " + value + ")";
            case LubTypeKind.Record:
                return "vm_lub_from_" + type.Name + "(s, &" + value + ")";
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
        if (elem.Kind == LubTypeKind.Record) filled = "vm_lub_from_" + elem.Name + "_list(s, " + list + ", " + value + ", " + value + "_count)";
        else if (elem.IsScalar) filled = "vm_lub_list(s, " + TypeVar(VmTypeName(type)) + ", " + list + ", " + value + ", " + value + "_count, sizeof(*" + value + "))";
        else if (elem.Kind == LubTypeKind.String) filled = "vm_lub_string_list(s, " + TypeVar(VmTypeName(type)) + ", " + list + ", " + value + ", " + value + "_count)";
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
        if (type.Kind == LubTypeKind.Keyed) return "vm_lub_key_load(s, " + value + " ? " + value + "->host_value : 0)";
        if (type.IsScalar || type.Kind == LubTypeKind.Struct) return type.Nullable ? value + ".v" : value;
        GenError.Report("unsupported field: " + type.ToString());
        return "";
    }

    private static string HasPath(string prefix, string path, ApiField field) =>
        prefix + Text.DropEnd(path, Text.Len(field.HostName)) + "has_" + field.HostName;

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

    private static string FieldCallbackName(ApiType type, ApiField field) => "vm_lub_" + type.Name + "_" + field.HostName;

    private void InputRecord(ApiType type)
    {
        int funcs = 0;
        foreach (var f in type.Fields)
        {
            if (f.Type.Kind != LubTypeKind.Func) continue;
            if (funcs >= 8) GenError.Report("too many callback fields: " + type.Name);
            Trampoline(FieldCallbackName(type, f), f.Type, type.Name + "." + f.Name, "VmLubHeld *held = user; VmLub *s = held->s; void *closure = vm_lub_held_get(held, " + funcs + ");", "!closure", "closure", "vm_lub_held_fault(held, " + funcs + ")");
            funcs++;
        }
        Line("static Lub" + type.Name + " vm_lub_to_" + type.Name + "(VmLub *s, " + Struct(type.Name) + " *source) {");
        Line("  (void)s; Lub" + type.Name + " result = {0}; if (!source) return result;");
        foreach (var fp in Fields(type))
        {
            var field = fp.Field;
            var path = fp.Path;
            var src = "source->f_" + field.HostName;
            var dst = "result." + path;
            var has = HasPath("result.", path, field);
            if ((field.Type.IsScalar || field.Type.Kind == LubTypeKind.Struct) && field.Optional)
                Line("  if (" + src + ".has) { " + has + " = true; " + dst + " = " + src + ".v; }");
            else if (field.Bits)
                Line("  " + (field.Optional ? has + " = " : "") + "vm_lub_bits(s, " + src + ", &" + dst + ");");
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
                Line("    if (tvm_array_len(" + src + ") != " + length + ") vm_lub_fail(s, \"" + type.Name + "." + field.Name + ": the array must have " + length + " elements\");");
                Line("    else memcpy(" + dst + ", tvm_array_data(" + src + "), sizeof(" + dst + "));\n  }");
            }
            else if (field.Type.Kind == LubTypeKind.List)
            {
                var elem = field.Type.Elem!;
                Line("  if (" + src + ") {");
                if (elem.Kind == LubTypeKind.Record)
                {
                    Line("    " + dst + " = vm_lub_to_" + elem.Name + "_list(s, " + src + ", &" + dst + "_count);");
                    Line("  }");
                    continue;
                }
                Line("    " + dst + "_count = tvm_list_count(" + src + ");");
                if (elem.IsScalar && field.ArrayLen >= 0)
                {
                    int cap = field.ArrayLen;
                    Line("    if (" + dst + "_count > " + cap + ") { vm_lub_fail(s, \"" + type.Name + "." + field.Name + ": at most " + cap + " elements\"); " + dst + "_count = 0; }");
                    Line("    memcpy(" + dst + ", tvm_list_data(" + src + "), (size_t)" + dst + "_count * sizeof(*" + dst + "));");
                }
                else if (elem.IsScalar) Line("    " + dst + " = tvm_list_data(" + src + ");");
                else if (elem.Kind == LubTypeKind.Handle)
                {
                    Line("    LubHandle *items = vm_lub_alloc(s, (size_t)" + dst + "_count * sizeof(*items));");
                    Line("    for (int i = 0; i < " + dst + "_count; i++) { " + Struct(elem.Name) + " *item = ((" + Struct(elem.Name) + " **)tvm_list_data(" + src + "))[i]; items[i] = item ? (int32_t)item->host_value : 0; }");
                    Line("    " + dst + " = items;");
                }
                else if (elem.Kind == LubTypeKind.Array && field.ArrayLen >= 0)
                {
                    int len = field.ArrayLen;
                    Line("    " + Scalar(elem.Elem!) + " (*items)[" + len + "] = vm_lub_alloc(s, (size_t)" + dst + "_count * sizeof(*items));");
                    Line("    for (int i = 0; i < " + dst + "_count; i++) { void *item = ((void **)tvm_list_data(" + src + "))[i]; if (tvm_array_len(item) != " + len + ") vm_lub_fail(s, \"" + type.Name + "." + field.Name + ": each array must have " + len + " elements\"); else memcpy(items[i], tvm_array_data(item), sizeof(items[i])); }");
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
                Line("  if (" + src + ") { " + has + " = true; " + dst + " = vm_lub_to_" + field.Type.Name + "(s, " + src + "); }");
            else if (field.Type.Kind == LubTypeKind.Record)
                Line("  " + dst + " = vm_lub_to_" + field.Type.Name + "(s, " + src + ");");
            else if (field.Type.Kind == LubTypeKind.Func)
            {
                int slot = FuncFieldIndex(type, field);
                Line("  if (" + src + ") {");
                Line("    if (!result.user) { result.user = vm_lub_held_new(s); result.user_release = vm_lub_held_release; }");
                Line("    ((VmLubHeld *)result.user)->h[" + slot + "] = tvm_hold(s->vm, " + src + ");");
                Line("    " + dst + " = " + FieldCallbackName(type, field) + ";");
                Line("  }");
            }
            else Line("  " + dst + " = " + To(field.Type, src) + ";");
        }
        Line("  return result;\n}");
    }

    private void OutputRecord(ApiType type)
    {
        Line("static " + Struct(type.Name) + " *vm_lub_from_" + type.Name + "(VmLub *s, const Lub" + type.Name + " *source) {");
        Line("  " + Struct(type.Name) + " *result = vm_lub_new(s, " + TypeVar(Il(type.Name)) + ", sizeof(*result));");
        foreach (var fp in Fields(type))
        {
            var field = fp.Field;
            var path = fp.Path;
            var dst = "result->f_" + field.HostName;
            var src = "source->" + path;
            var has = HasPath("source->", path, field);
            if (field.Type.Kind == LubTypeKind.Array)
            {
                // Array fields are created by the host (VM objects do not run field initializers)
                Line("  " + dst + " = vm_lub_array(s, " + TypeVar(VmTypeName(field.Type)) + ", " + field.ArrayLen + ");");
                Line("  if (" + dst + ") memcpy(tvm_array_data(" + dst + "), " + src + ", sizeof(" + src + "));");
            }
            else if ((field.Type.IsScalar || field.Type.Kind == LubTypeKind.Struct) && field.Optional)
                Line("  " + dst + " = (" + Opt(field.Type) + "){" + has + ", " + src + "};");
            else if (field.Bits)
                Line("  " + dst + " = " + (field.Optional ? "!" + has + " ? NULL : " : "") + "vm_lub_bits_string(s, " + src + ");");
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
        Line("static Lub" + name + " *vm_lub_to_" + name + "_list(VmLub *s, void *source, int32_t *count) {");
        Line("  *count = tvm_list_count(source); Lub" + name + " *items = vm_lub_alloc(s, (size_t)*count * sizeof(*items));");
        Line("  for (int i = 0; i < *count; i++) items[i] = vm_lub_to_" + name + "(s, ((" + Struct(name) + " **)tvm_list_data(source))[i]);");
        Line("  return items;\n}");
    }

    private void OutputList(string name)
    {
        var listType = TypeVar("List<" + Il(name) + ">");
        Line("static void *vm_lub_from_" + name + "_list(VmLub *s, void *list, const Lub" + name + " *source, int32_t count) {");
        Line("  if (!list) list = vm_lub_list_new(s, " + listType + ");");
        Line("  if (!list) return NULL;");
        Line("  int32_t base = tvm_list_count(list);");
        Line("  if (!tvm_list_resize(s->vm, list, base + count)) { vm_lub_fail(s, \"allocation-overflow\"); return list; }");
        Line("  for (int i = 0; i < count; i++) ((void **)tvm_list_data(list))[base + i] = vm_lub_from_" + name + "(s, &source[i]);");
        Line("  return list;\n}");
    }

    private void Handle(ApiType type)
    {
        Line("static " + Struct(type.Name) + " *vm_lub_handle_" + type.Name + "(VmLub *s, LubHandle handle) {");
        // An invalid handle (0) is null
        Line("  if (!handle) return NULL;");
        Line("  " + Struct(type.Name) + " *result = vm_lub_new(s, " + TypeVar(Il(type.Name)) + ", sizeof(*result));");
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
            Line("    lub_gfx_resource_info(s->ctx, handle, &key, &version);");
            Line("    result->f_version = version;");
            Line("  }");
        }
        Line("  return result;\n}");
    }

    private void View(ApiType type)
    {
        Line("static " + Struct(type.Name) + " *vm_lub_view_" + type.Name + "(VmLub *s, LubView value) {");
        Line("  if (!value.ptr) return NULL;");
        Line("  " + Struct(type.Name) + " *result = vm_lub_new(s, " + TypeVar(Il(type.Name)) + ", sizeof(*result));");
        Line("  result->host_value = vm_lub_view_store(s, value); result->f_length = value.len;");
        Line("  return result;\n}");
    }

    // ---- Host functions ----

    // One item of the arguments or return values (params / rets of tvm_host; tcs tcsvm.h).
    // Nullable scalars are VM structs ({ bool has; T v; }), so they are passed as a C-layout block S{int?}
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

    // VM type name of a scalar (enum is int)
    private static string ScalarVmName(TypeRef type) =>
        type.Kind == LubTypeKind.Double ? "float" : type.Kind == LubTypeKind.Bool ? "bool" : "int";

    // Expression that converts the k-th argument a[k] to a C value
    private static string Unpack(TypeRef type, int k)
    {
        if ((type.IsScalar || type.Kind == LubTypeKind.Struct) && type.Nullable) return "*(const " + Opt(type) + " *)a[" + k + "].p";
        if (type.Kind == LubTypeKind.Struct) return "*(const " + CHeader.TypeName(type.Name) + " *)a[" + k + "].p";
        if (type.Kind == LubTypeKind.Double) return "a[" + k + "].f";
        if (type.Kind == LubTypeKind.Bool) return "a[" + k + "].i != 0";
        if (type.IsScalar) return "a[" + k + "].i";
        return "a[" + k + "].p";
    }

    // Writes the value to return value r[0] (for a block return value, to where r[0].p points)
    private void PackReturn(TypeRef type, string value)
    {
        if (type.IsScalar && type.Nullable) Line("  *(" + Opt(type) + " *)r[0].p = " + value + ";");
        else if (type.Kind == LubTypeKind.Double) Line("  r[0].f = " + value + ";");
        else if (type.IsScalar) Line("  r[0].i = " + value + ";");
        else Line("  r[0].p = " + value + ";");
    }

    // Out location of the k-th argument (the VM passes it zero-filled). For scalars a pointer to the C value, for references the slot where the VM object is placed.
    private static string OutPlace(TypeRef type, int k) => Place(type, "a[" + k + "].p");

    // Outs that the C API writes directly to the VM location (non-nullable scalars and struct records)
    private static bool Direct(TypeRef type) => !type.Nullable && (type.IsScalar || type.Kind == LubTypeKind.Struct);

    // Expression that turns the location p passed by the VM into a pointer of the C type (for scalars and struct records the value location, otherwise the slot where the VM object is placed)
    private static string Place(TypeRef type, string p) =>
        (type.IsScalar ? "(" + Scalar(type) + " *)" : type.Kind == LubTypeKind.Struct ? "(" + CHeader.TypeName(type.Name) + " *)" : "(void **)") + p;

    // Expression that zeroes the location
    private static string Zero(TypeRef type) =>
        type.IsScalar ? "0" : type.Kind == LubTypeKind.Struct ? "(" + CHeader.TypeName(type.Name) + "){0}" : "NULL";

    // Instance method of a foreign type. The view's Get(int) reads a byte of the view
    private void Method(ApiType type, ApiFunction method, string hostName)
    {
        var fn = "vm_host_" + hostName.Replace(".", "_");
        if (type.Kind == "view" && method.Name == "Get" && method.Params.Count == 1
            && method.Params[0].Type.Kind == LubTypeKind.Int && method.Return.Kind == LubTypeKind.Int)
        {
            Line("static TvmStatus " + fn + "(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {");
            Line("  VM_LUB_ENTER();");
            Line("  " + Struct(type.Name) + " *self = a[0].p; int32_t p_index = a[1].i;");
            Line("  if (!self) { vm_lub_fail(s, \"null reference\"); VM_LUB_LEAVE(vm); }");
            Line("  LubView view = vm_lub_view_load(s, self->host_value);");
            Line("  if (s->error) VM_LUB_LEAVE(vm);");
            Line("  if (p_index < 0 || p_index >= view.len) { vm_lub_fail(s, \"view index out of range\"); VM_LUB_LEAVE(vm); }");
            Line("  r[0].i = view.ptr[p_index];");
            Line("  VM_LUB_LEAVE(vm);\n}");
            registrations.Add("{\"" + hostName + "\", \"oi\", \"i\", " + fn + "}");
        }
        else GenError.Report("unsupported method: " + type.Name + "." + method.Name);
    }

    // Outs received as C values go into the variable n_<name>. A list uses two: the pointer and n_<name>_count. A record that can be null receives its presence in has
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
        var name = CHeader.FunctionName(ns, function.HostName);
        var hostName = ns.HostPath + "." + function.HostName;
        foreach (var param in function.Params)
        {
            if (param.Type.Kind == LubTypeKind.Func) Callback(name, param);
        }
        var fn = "vm_host_" + name;
        Line("static TvmStatus " + fn + "(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user) {");
        Line("  (void)a; (void)r; VM_LUB_ENTER();");
        // Arguments arrive one by one in a[k] in C# order. For out (&), a[k].p is where the VM receives the value
        var tokens = new List<string>();
        for (int k = 0; k < function.Params.Count; k++)
        {
            var p = function.Params[k];
            tokens.Add((p.IsOut ? "&" : "") + Token(p.Type));
            if (!p.IsOut) Line("  " + VmType(p.Type) + " p_" + p.HostName + " = " + Unpack(p.Type, k) + ";");
        }
        var st = new CallState();
        var args = st.Args;
        args.Add("s->ctx");
        string? nativeName = null;
        string? nativeParam = null;
        foreach (var param in function.Params)
        {
            if (param.IsOut || param.IsCount) continue;
            var p = "p_" + param.HostName;
            var n = "n_" + param.HostName;
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
                var count = "p_" + countParam.HostName;
                Line("  if (" + p + " && (uint32_t)" + count + " > (uint32_t)tvm_array_len(" + p + ")) { vm_lub_fail(s, \"" + hostName + ": count is out of range for the array\"); VM_LUB_LEAVE(vm); }");
                args.Add(p + " ? (const float *)tvm_array_data(" + p + ") : NULL");
                args.Add(count);
            }
            else if (type.Kind == LubTypeKind.Record)
            {
                Line("  Lub" + type.Name + " " + n + " = vm_lub_to_" + type.Name + "(s, " + p + ");");
                args.Add(p + " ? &" + n + " : NULL");
            }
            else if (type.Kind == LubTypeKind.List && type.Elem!.IsScalar)
            {
                args.Add(p + " ? tvm_list_data(" + p + ") : NULL");
                args.Add(p + " ? tvm_list_count(" + p + ") : 0");
            }
            else if (type.Kind == LubTypeKind.List && type.Elem!.Kind == LubTypeKind.Record)
            {
                Line("  int32_t " + n + "_count = 0; Lub" + type.Elem.Name + " *" + n + " = " + p + " ? vm_lub_to_" + type.Elem.Name + "_list(s, " + p + ", &" + n + "_count) : NULL;");
                args.Add(n);
                args.Add(n + "_count");
            }
            else if (type.Kind == LubTypeKind.View)
            {
                Line("  LubView " + n + " = {0};");
                Line("  if (" + p + ") " + n + " = vm_lub_view_load(s, " + p + "->host_value);");
                args.Add(n + ".ptr");
                args.Add(n + ".len");
            }
            else if (type.Kind == LubTypeKind.Native)
            {
                Line("  if (!" + p + ") { vm_lub_fail(s, \"null reference\"); VM_LUB_LEAVE(vm); }");
                Line("  LubBindings *" + n + " = vm_lub_bindings_block(s, " + p + ");");
                args.Add(n);
                nativeName = n;
                nativeParam = p;
            }
            else if (type.Kind == LubTypeKind.Array && type.Elem!.Kind == LubTypeKind.Double)
            {
                Line("  if (!" + p + ") { vm_lub_fail(s, \"null reference\"); VM_LUB_LEAVE(vm); }");
                args.Add("(const float *)tvm_array_data(" + p + ")");
                args.Add("tvm_array_len(" + p + ")");
            }
            else if (type.Kind == LubTypeKind.Struct && !type.Nullable) args.Add(p);
            else if (type.IsScalar && type.Nullable) args.Add(p + ".has ? &" + p + ".v : NULL");
            else if (type.Kind == LubTypeKind.Func)
            {
                Line("  VmLubVisit v_" + param.HostName + " = {s, " + p + "};");
                args.Add(p + " ? " + CallbackName(name, param) + " : NULL");
                args.Add("&v_" + param.HostName);
            }
            else
            {
                // A null handle where null is not accepted is an error
                if (type.Kind == LubTypeKind.Handle && !type.Nullable && !(param.Optional && !param.HasDefault))
                    Line("  if (!" + p + ") { vm_lub_fail(s, \"" + hostName + ": " + param.Name + " is null\"); VM_LUB_LEAVE(vm); }");
                args.Add(To(type, p));
            }
        }
        // Input conversion errors (array length, bit mask) stop before the C API is called
        Line("  if (s->error) VM_LUB_LEAVE(vm);");
        // Outs of scalars and struct records are written by the C API directly into the VM location. Others are received as C values, converted to VM values, and placed in the location
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
            else Output(param.Type, "n_" + param.HostName, st);
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
        // A nullable scalar return value is the block at r[0].p ({ bool has; T v; }), which the C API writes directly
        bool block = has && ret.IsScalar;
        // A struct record return value is also the block at r[0].p, which the C API writes directly as its out
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
        if (visits) Line("  bool vm_lub_visitor_outer = s->visitor_failed; s->visitor_failed = false;");
        if (function.NoFail) Line("  " + (byValue ? "result = " : "") + call + ";");
        else Line("  LubStatus status = " + call + ";");
        // A query whose visitor failed is null (return value and outs are 0)
        if (visits)
        {
            Line("  if (s->visitor_failed) {");
            Line("    fprintf(stderr, \"lub: " + hostName + " visitor: %s\\n\", s->visitor_text);");
            Line("    s->visitor_failed = vm_lub_visitor_outer;");
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
            Line("  s->visitor_failed = vm_lub_visitor_outer;");
        }
        if (!function.NoFail)
        {
            if (nativeName != null)
                Line("  while (status == LUB_BINDINGS_FULL) { " + nativeName + " = vm_lub_bindings_grow(s, " + nativeParam + "); status = " + call + "; }");
            Line("  vm_lub_check(s, status);");
            Line("  if (s->error) VM_LUB_LEAVE(vm);");
        }
        // A not-found result (LUB_NOT_FOUND) or a result without a value is null, and a struct record is the zero value. Scalar outs then stay as the C API wrote them
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
            var n = "n_" + param.HostName;
            var value = From(param.Type, n);
            if (param.Type.Kind == LubTypeKind.Record && param.Type.Nullable) value = n + "_has ? " + value + " : NULL";
            if (missing != null) value = "(" + missing + ") ? NULL : " + value;
            Line("  *" + OutPlace(param.Type, k) + " = " + value + ";");
        }
        Line("  VM_LUB_LEAVE(vm);\n}");
        registrations.Add("{\"" + hostName + "\", \"" + string.Join("", tokens) + "\", \"" + Token(ret) + "\", " + fn + "}");
    }

    // A Func argument maps to a C function pointer + user (a pair of state and the VM closure). The C API calls it only during the call
    private static string CallbackName(string function, ApiParam param) => "vm_lub_" + function + "_" + param.HostName;

    private void Callback(string function, ApiParam param) =>
        Trampoline(CallbackName(function, param), param.Type, param.Name, "VmLubVisit *visit = user; VmLub *s = visit->s;", "s->visitor_failed", "visit->closure", "vm_lub_visitor_fail(s)");

    // Default value of a callback (when it cannot be called or fails)
    private static string CallbackDefault(TypeRef ret) =>
        ret.Kind == LubTypeKind.Bool ? "true" : ret.Kind == LubTypeKind.Double ? "1.0f" : ret.Kind == LubTypeKind.Int ? "1" : "";

    // Calls the VM closure from a C function pointer. The prologue prepares the closure; on skip, returns the default value without calling it
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
                sets.Add("args[" + at + "].p = vm_lub_from_" + type.Name + "(s, " + a + ");");
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
        // If an earlier call failed, does not go back into the VM
        Line("  if (s->error || " + skip + ") " + giveUp);
        foreach (var st in sets) Line("  " + st);
        Line("  if (tvm_invoke(s->vm, " + closure + ", args, " + at + ", rets, " + (ret.Kind == LubTypeKind.Void ? "0" : "1") + ") != TVM_OK) { " + onFault + "; " + giveUp + " }");
        if (cRet == "float") Line("  return rets[0].f;");
        else if (cRet == "bool") Line("  return rets[0].i != 0;");
        else if (cRet != "void") Line("  return rets[0].i;");
        Line("}");
    }

    // The Bindings contents block is placed in a GC blob (it is reachable through the field's blob, so the GC reclaims it once released)
    private void NativeBindings()
    {
        Line("static LubBindings *vm_lub_bindings_alloc(VmLub *s, VmLub_Bindings *owner, int32_t scale) {");
        Line("  void *block = tvm_blob_new(s->vm, lub_bindings_size(scale));");
        Line("  if (!block) { vm_lub_fail(s, \"allocation-overflow\"); return NULL; }");
        Line("  owner->f_blob = block;");
        Line("  lub_bindings_init((LubBindings *)block, scale);");
        Line("  return (LubBindings *)block;\n}");
        Line("static LubBindings *vm_lub_bindings_block(VmLub *s, VmLub_Bindings *owner) {");
        Line("  return owner->f_blob ? (LubBindings *)owner->f_blob : vm_lub_bindings_alloc(s, owner, 1);\n}");
        Line("static LubBindings *vm_lub_bindings_grow(VmLub *s, VmLub_Bindings *owner) {");
        Line("  LubBindings *old = (LubBindings *)owner->f_blob;");
        Line("  LubBindings *grown = vm_lub_bindings_alloc(s, owner, lub_bindings_scale(old) * 2);");
        Line("  if (!grown) return old;");
        Line("  lub_bindings_grow_into(grown, old);");
        Line("  return grown;\n}");
    }

    // VM_LUB_ENTER / VM_LUB_LEAVE: entry and exit of a host function. Keeps errors and scratch space separate across nesting (callback to VM, VM to host function).
    // Errors are accumulated with vm_lub_fail and turned into a host function fault (tvm_host_fault) at the exit
    private const string Helpers = """
        #if defined(__GNUC__) || defined(__clang__)
        #define VM_LUB_UNUSED __attribute__((unused))
        #else
        #define VM_LUB_UNUSED
        #endif
        typedef struct VmLubOptI32 { bool has; int32_t v; } VmLubOptI32;
        typedef struct VmLubOptF32 { bool has; float v; } VmLubOptF32;
        typedef struct VmLubOptBool { bool has; bool v; } VmLubOptBool;
        /* A VM type (loading and reload recreate it, so it is looked up again by version) */
        typedef struct VmLubType { const char *name; uint32_t version; const TvmTypeInfo *type; } VmLubType;
        /* Binding state for one VM. Host functions receive it as the tvm_host user */
        typedef struct VmLub {
          LubContext *ctx;
          TvmVm *vm;
          /* The error of the current host function (NULL if none) and storage for its text */
          const char *error;
          char error_text[1024];
          /* C memory that lives only during a host function */
          void **temps;
          size_t ntemps, ctemps;
          /* Fault of a query visitor */
          bool visitor_failed;
          char visitor_text[1024];
          VmLubType *types;
          /* Keys of keyed objects (host_value is the table index + 1. Keys are few, so the table is kept) */
          char **keys;
          uint32_t nkeys;
          /* View storage (until the end of the frame) */
          LubView *views;
          uint32_t view_count, view_capacity;
          int32_t view_frame;
        } VmLub;
        static void vm_lub_fail(VmLub *s, const char *message) {
          if (!s->error) s->error = message;
        }
        static void vm_lub_fail_text(VmLub *s, const char *message) {
          if (s->error) return;
          snprintf(s->error_text, sizeof s->error_text, "%s", message);
          s->error = s->error_text;
        }
        /* A VM fault inside a callback (the trace is in tvm_error) */
        static void vm_lub_fail_vm(VmLub *s) { vm_lub_fail_text(s, tvm_error(s->vm)); }
        /* Fault of a query visitor. The query does not fault; it returns null (the message goes to the log) */
        static void vm_lub_visitor_fail(VmLub *s) {
          if (s->visitor_failed) return;
          snprintf(s->visitor_text, sizeof s->visitor_text, "%s", tvm_error(s->vm));
          s->visitor_failed = true;
        }
        /* Callback fields of records (such as the world Filter): the runtime keeps them, so the closure is held with tvm_hold.
           It is released when the runtime releases the callback (user_release). If the VM is not the one that created it, it is neither called nor released */
        /* The user passed to a query visitor (only during the call) */
        typedef struct VmLubVisit { VmLub *s; void *closure; } VmLubVisit;
        typedef struct VmLubHeld { VmLub *s; TvmVm *vm; int n; TvmHandle h[8]; bool logged[8]; } VmLubHeld;
        static VmLubHeld *vm_lub_held_new(VmLub *s) {
          VmLubHeld *held = calloc(1, sizeof(*held));
          if (!held) abort();
          held->s = s;
          held->vm = s->vm;
          return held;
        }
        static void VM_LUB_UNUSED vm_lub_held_release(void *user) {
          VmLubHeld *held = user;
          VmLub *s = held->s;
          if (held->vm == s->vm && s->vm)
            for (int i = 0; i < 8; i++) if (held->h[i]) tvm_release(s->vm, held->h[i]);
          free(held);
        }
        static void *vm_lub_held_get(VmLubHeld *held, int i) {
          VmLub *s = held->s;
          return held->vm == s->vm && s->vm && held->h[i] ? tvm_handle_get(s->vm, held->h[i]) : NULL;
        }
        /* A fault is logged only once per callback, and execution continues with the default value */
        static void vm_lub_held_fault(VmLubHeld *held, int i) {
          if (held->logged[i]) return;
          held->logged[i] = true;
          fprintf(stderr, "lub: callback error: %s\n", tvm_error(held->s->vm));
        }
        static void vm_lub_check(VmLub *s, LubStatus status) {
          if (status == LUB_ERROR) {
            if (s->error) return;
            snprintf(s->error_text, sizeof s->error_text, "lub-api: %s", lub_last_error(s->ctx));
            s->error = s->error_text;
          }
        }
        /* C memory that lives only during a host function (such as lists of input records) */
        static void *vm_lub_alloc(VmLub *s, size_t size) {
          if (s->ntemps == s->ctemps) {
            size_t cap = s->ctemps ? s->ctemps * 2 : 16;
            void **grown = realloc(s->temps, cap * sizeof(void *));
            if (!grown) abort();
            s->temps = grown;
            s->ctemps = cap;
          }
          void *p = calloc(1, size ? size : 1);
          if (!p) abort();
          s->temps[s->ntemps++] = p;
          return p;
        }
        static void vm_lub_free_to(VmLub *s, size_t mark) {
          while (s->ntemps > mark) free(s->temps[--s->ntemps]);
        }
        #define VM_LUB_ENTER() VmLub *s = user; size_t vm_lub_mark = s->ntemps; const char *vm_lub_outer = s->error; s->error = NULL
        #define VM_LUB_LEAVE(vm) return vm_lub_leave(s, vm, vm_lub_mark, vm_lub_outer)
        static TvmStatus vm_lub_leave(VmLub *s, TvmVm *vm, size_t mark, const char *outer) {
          vm_lub_free_to(s, mark);
          const char *error = s->error;
          s->error = outer;
          return error ? tvm_host_fault(vm, error) : TVM_OK;
        }
        static const TvmTypeInfo *vm_lub_type(VmLub *s, VmLubType *t) {
          uint32_t version = tvm_types_version(s->vm);
          if (t->version != version) {
            t->type = tvm_type(s->vm, t->name);
            t->version = version;
          }
          if (!t->type && !s->error) {
            snprintf(s->error_text, sizeof s->error_text, "lub: the module has no type %s", t->name);
            s->error = s->error_text;
          }
          return t->type;
        }
        /* If the type is missing (the error is already recorded), returns throwaway C memory that absorbs the caller's writes */
        static void *vm_lub_new(VmLub *s, VmLubType *t, size_t size) {
          const TvmTypeInfo *type = vm_lub_type(s, t);
          void *o = type ? tvm_object_new_of(s->vm, type) : NULL;
          return o ? o : vm_lub_alloc(s, size);
        }
        static void *vm_lub_list_new(VmLub *s, VmLubType *t) {
          const TvmTypeInfo *type = vm_lub_type(s, t);
          return type ? tvm_list_new_of(s->vm, type, 0) : NULL;
        }
        static void *vm_lub_array(VmLub *s, VmLubType *t, int32_t len) {
          const TvmTypeInfo *type = vm_lub_type(s, t);
          return type ? tvm_array_new_of(s->vm, type, len) : NULL;
        }
        static LubStr vm_lub_key_load(VmLub *s, uint64_t host_value) {
          if (host_value == 0 || host_value > s->nkeys) { vm_lub_fail(s, "null reference"); return (LubStr){0}; }
          const char *key = s->keys[host_value - 1];
          return (LubStr){key, (int32_t)strlen(key)};
        }
        static uint64_t vm_lub_key_store(VmLub *s, LubStr key) {
          for (uint32_t i = 0; i < s->nkeys; i++)
            if ((int32_t)strlen(s->keys[i]) == key.len && memcmp(s->keys[i], key.ptr, (size_t)key.len) == 0) return i + 1;
          char **grown = realloc(s->keys, (s->nkeys + 1) * sizeof(char *));
          char *copy = malloc((size_t)key.len + 1);
          if (!grown || !copy) abort();
          memcpy(copy, key.ptr, (size_t)key.len);
          copy[key.len] = 0;
          s->keys = grown;
          s->keys[s->nkeys++] = copy;
          return s->nkeys;
        }
        static LubStr vm_lub_str(void *text) {
          size_t len = 0;
          const char *data = tvm_string_data(text, &len);
          return data ? (LubStr){data, (int32_t)len} : (LubStr){0};
        }
        static void *vm_lub_string(VmLub *s, LubStr text) {
          return text.ptr ? tvm_string_new(s->vm, text.ptr, (size_t)text.len) : NULL;
        }
        static void *vm_lub_list(VmLub *s, VmLubType *t, void *list, const void *data, int32_t count, size_t size) {
          if (!list) list = vm_lub_list_new(s, t);
          if (!list) return NULL;
          if (!tvm_list_resize(s->vm, list, count)) { vm_lub_fail(s, "allocation-overflow"); return list; }
          if (count) memcpy(tvm_list_data(list), data, (size_t)count * size);
          return list;
        }
        static void *vm_lub_string_list(VmLub *s, VmLubType *t, void *list, const LubStr *data, int32_t count) {
          if (!list) list = vm_lub_list_new(s, t);
          if (!list) return NULL;
          if (!tvm_list_resize(s->vm, list, count)) { vm_lub_fail(s, "allocation-overflow"); return list; }
          void **items = (void **)tvm_list_data(list);
          for (int32_t i = 0; i < count; i++) items[i] = vm_lub_string(s, data[i]);
          return list;
        }
        """;

    // The 64 bit mask of [LubBits] is a hex string on the C# surface. The rules are the same as the tcs2c glue code
    private const string BitsHelpers = """
        #include <inttypes.h>
        static void vm_lub_bits_fault(VmLub *s, const char *what, LubStr text) {
          if (s->error) return;
          snprintf(s->error_text, sizeof s->error_text, "bit mask: %s '%.*s'", what, (int)text.len, text.ptr);
          s->error = s->error_text;
        }
        static bool vm_lub_bits(VmLub *s, void *string, uint64_t *out) {
          if (!string) return false;
          LubStr text = vm_lub_str(string);
          int32_t length = text.len, i = 0;
          if (length >= 2 && text.ptr[0] == '0' && (text.ptr[1] == 'x' || text.ptr[1] == 'X')) i = 2;
          if (i == length) { vm_lub_bits_fault(s, "empty hex string", text); return false; }
          if (length - i > 16) { vm_lub_bits_fault(s, "more than 16 hex digits", text); return false; }
          uint64_t value = 0;
          for (; i < length; i++) {
            unsigned char c = (unsigned char)text.ptr[i];
            int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (digit < 0) { vm_lub_bits_fault(s, "invalid hex", text); return false; }
            value = value << 4 | (uint64_t)digit;
          }
          *out = value;
          return true;
        }
        static void *vm_lub_bits_string(VmLub *s, uint64_t value) {
          char text[17];
          snprintf(text, sizeof(text), "%016" PRIx64, value);
          return tvm_string_new(s->vm, text, 16);
        }
        """;

    // A view is valid until the end of the frame. VM objects can survive across frames, so the actual data is kept in a host table and host_value is the frame and an index
    private const string ViewHelpers = """
        static uint64_t vm_lub_view_store(VmLub *s, LubView value) {
          int32_t frame = lub_frame_index(s->ctx);
          if (frame != s->view_frame) { s->view_frame = frame; s->view_count = 0; }
          if (s->view_count == s->view_capacity) {
            s->view_capacity = s->view_capacity ? s->view_capacity * 2 : 16;
            s->views = realloc(s->views, s->view_capacity * sizeof(*s->views));
            if (!s->views) abort();
          }
          s->views[s->view_count] = value;
          return (uint64_t)(uint32_t)frame << 32 | s->view_count++;
        }
        static LubView vm_lub_view_load(VmLub *s, uint64_t host_value) {
          int32_t frame = lub_frame_index(s->ctx);
          uint32_t index = (uint32_t)host_value;
          if ((int32_t)(host_value >> 32) != frame || frame != s->view_frame || index >= s->view_count
              || s->views[index].frame != frame) {
            vm_lub_fail(s, "stale view: a view is valid until the end of its frame (copy what you keep)");
            return (LubView){0};
          }
          return s->views[index];
        }
        """;
}
