using System.Collections.Generic;
using Tcs;

namespace LubGen;

public static class TcsBinding
{
    public static string Generate(ApiModel model, ExportResult game, string? entry)
    {
        var g = new TcsBindingGen(model, game, entry);
        return g.Run();
    }
}

/// <summary>A record field and its path inside the C struct (base fields are prefixed with base.).</summary>
internal sealed class FieldPath
{
    public ApiField Field;
    public string Path;

    public FieldPath(ApiField field, string path)
    {
        Field = field;
        Path = path;
    }
}

/// <summary>State while generating the glue code for one C API function (what the local function Output captured in the C# version).</summary>
internal sealed class CallState
{
    public List<string> Args = new List<string>();
}

internal sealed class TcsBindingGen
{
    private ApiModel model;
    private ExportResult game;
    private string? entryName;
    private Out output = new Out();
    private HashSet<string> inputs = new HashSet<string>();
    private HashSet<string> outputs = new HashSet<string>();
    // Records passed or received as list elements (the repacking to and from arrays is generated separately)
    private HashSet<string> inputLists = new HashSet<string>();
    private HashSet<string> outputLists = new HashSet<string>();
    // Struct records (the tcs2c value struct and the C API struct are copied field by field)
    private HashSet<string> structInputs = new HashSet<string>();
    private HashSet<string> structOutputs = new HashSet<string>();

    public TcsBindingGen(ApiModel model, ExportResult game, string? entryName)
    {
        this.model = model;
        this.game = game;
        this.entryName = entryName;
    }

    private void Line(string text) => output.Line(text);

    // tcs2c C symbols (Tcs_ / tcs_new_ / TCS_TYPE_) are looked up by the namespace-qualified type name
    private static string Il(string name) => ApiModelLoader.RootNamespace + "_" + name;

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
        if (type.Kind == LubTypeKind.Struct) return "TcsOpt_S_" + Il(type.Name);
        if (type.Kind == LubTypeKind.Double) return "TcsOptF32";
        if (type.Kind == LubTypeKind.Bool) return "TcsOptBool";
        return "TcsOptI32";
    }

    private static string TcsType(TypeRef type)
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
                return type.Nullable ? Opt(type) : "Tcs_" + Il(type.Name);
            case LubTypeKind.String:
                return "TcsString *";
            case LubTypeKind.Handle:
            case LubTypeKind.View:
            case LubTypeKind.Record:
            case LubTypeKind.Native:
                return "Tcs_" + Il(type.Name) + " *";
            case LubTypeKind.List:
                return "TcsList *";
            case LubTypeKind.Array:
                return "TcsArray *";
            case LubTypeKind.Func:
                return "TcsClosure *";
        }
        GenError.Report("unsupported tcs parameter: " + type.ToString());
        return "";
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
        // Callback arguments are created by the host and passed to tcs
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
        if (type.Kind == LubTypeKind.Struct)
        {
            if (!(isOutput ? structOutputs : structInputs).Add(type.Name)) return;
            foreach (var f in model.FindType(type.Name)!.Fields) Record(f.Type, isOutput);
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

    public string Run()
    {
        var calls = new HashSet<string>();
        foreach (var m in game.ForeignMethods) calls.Add(m.Name);
        var values = new HashSet<string>();
        foreach (var v in game.ForeignValues)
        {
            if (!v.HasConstant) values.Add(v.Name);
        }
        var functionNs = new List<ApiNamespace>();
        var functions = new List<ApiFunction>();
        foreach (var ns in model.Namespaces)
        {
            foreach (var f in ns.Functions)
            {
                if (!calls.Contains(ns.HostPath + "." + f.HostName)) continue;
                functionNs.Add(ns);
                functions.Add(f);
            }
        }
        foreach (var function in functions)
        {
            if (function.NoC)
            {
                GenError.Report("No C API for " + function.Name);
                return "";
            }
            foreach (var param in function.Params) Record(param.Type, param.IsOut);
            Record(function.Return, true);
        }
        // The OnEvent argument is a record the host builds from LubEventData, so the entry is resolved before collecting records
        var entry = FindEntry();
        if (GenError.Failed) return "";
        var onEvent = entry == null ? null : EventHook(entry);
        if (GenError.Failed) return "";
        if (onEvent != null) Record(new TypeRef(LubTypeKind.Record, "EventData", false, null, null, null), true);
        Line("/* Generated from lub_stub.cs. Include after the tcs2c game source. */");
        Line("#include <lub/lub_api.h>");
        Line("static LubContext *tcs_lub_context;");
        Line(Helpers);
        var external = new List<ApiType>();
        foreach (var t in model.Types)
        {
            bool ext = false;
            foreach (var c in game.Classes)
            {
                if (c.IsExternal && c.Name == Il(t.Name)) ext = true;
            }
            if (ext) external.Add(t);
        }
        bool anyView = false;
        foreach (var t in external)
        {
            if (t.Kind == "view") anyView = true;
        }
        if (anyView) Line(ViewHelpers);
        if (AnyBits(inputs) || AnyBits(outputs)) Line(BitsHelpers);
        foreach (var type in external)
        {
            if (type.Kind == "handle") Handle(type);
            if (type.Kind == "view") View(type);
        }
        foreach (var name in structInputs) Line("static Lub" + name + " tcs_lub_to_" + name + "(Tcs_" + Il(name) + " source);");
        foreach (var name in structOutputs) Line("static Tcs_" + Il(name) + " tcs_lub_from_" + name + "(const Lub" + name + " *source);");
        foreach (var name in structInputs) StructInput(model.FindType(name)!);
        foreach (var name in structOutputs) StructOutput(model.FindType(name)!);
        foreach (var name in inputs) Line("static Lub" + name + " tcs_lub_to_" + name + "(Tcs_" + Il(name) + " *source);");
        foreach (var name in outputs) Line("static Tcs_" + Il(name) + " *tcs_lub_from_" + name + "(const Lub" + name + " *source);");
        foreach (var name in inputLists) Line("static Lub" + name + " *tcs_lub_to_" + name + "_list(TcsList *source, int32_t *count);");
        foreach (var name in outputLists) Line("static TcsList *tcs_lub_from_" + name + "_list(TcsList *list, const Lub" + name + " *source, int32_t count);");
        foreach (var name in inputs) InputRecord(model.FindType(name)!);
        foreach (var name in outputs) OutputRecord(model.FindType(name)!);
        foreach (var name in inputLists) InputList(name);
        foreach (var name in outputLists) OutputList(name);
        if (GenError.Failed) return "";
        foreach (var type in external)
        {
            foreach (var method in type.Methods)
            {
                ExForeignMethod? foreign = null;
                foreach (var m in game.ForeignMethods)
                {
                    if (m.Receiver == Il(type.Name) && m.Name.EndsWith("." + method.HostName))
                    {
                        foreign = m;
                        break;
                    }
                }
                if (foreign != null) Method(type, method, foreign.Name);
                if (GenError.Failed) return "";
            }
        }
        bool anyNative = false;
        foreach (var f in functions)
        {
            foreach (var p in f.Params)
            {
                if (p.Type.Kind == LubTypeKind.Native) anyNative = true;
            }
        }
        if (anyNative) NativeBindings();
        foreach (var ns in model.Namespaces)
        {
            foreach (var value in ns.StaticFields)
            {
                if (!values.Contains(ns.HostPath + "." + value.HostName)) continue;
                var name = CHeader.FunctionName(ns, value.HostName);
                Line(TcsType(value.Type) + " tcs_host_" + name + "(void) { return " + From(value.Type, name + "(tcs_lub_context)") + "; }");
            }
        }
        if (GenError.Failed) return "";
        for (int i = 0; i < functions.Count; i++)
        {
            Function(functionNs[i], functions[i]);
            if (GenError.Failed)
            {
                GenError.Message = functionNs[i].Name + "." + functions[i].Name + ": " + GenError.Message;
                return "";
            }
        }
        if (entry != null) Entry(entry, onEvent);
        if (GenError.Failed) return "";
        return output.ToString();
    }

    private static bool HasFrame(ExClass c)
    {
        foreach (var m in c.Methods)
        {
            if (m.IsStatic && m.Name == "OnFrame") return true;
        }
        return false;
    }

    private static ExMethod? StaticMethod(ExClass c, string name)
    {
        foreach (var m in c.Methods)
        {
            if (m.IsStatic && m.Name == name) return m;
        }
        return null;
    }

    // Entry point called by src/tcs_host.c.
    // Connects OnInit / OnFrame / OnQuit / OnEvent of the entry class to tcs_entry_<Class>_<method> from tcs2c --lib; missing hooks do nothing.
    // The entry class is --entry (IL name MyGame_Game or C# MyGame.Game), or if missing, the only class with a static OnFrame.
    // Both cases require a static OnFrame.
    // Outputs nothing for inputs where it is not found (such as checking cs-lib alone).
    private ExClass? FindEntry()
    {
        if (entryName != null)
        {
            ExClass? entry = null;
            foreach (var c in game.Classes)
            {
                if (!c.IsExternal && !c.IsInterface && (c.Name == entryName || c.DisplayName == entryName))
                {
                    entry = c;
                    break;
                }
            }
            if (entry == null)
            {
                GenError.Report("entry class not found: " + entryName);
                return null;
            }
            if (!HasFrame(entry))
            {
                GenError.Report("entry class " + entryName + " has no static OnFrame(float)");
                return null;
            }
            return entry;
        }
        var candidates = new List<ExClass>();
        foreach (var c in game.Classes)
        {
            if (!c.IsExternal && !c.IsInterface && HasFrame(c)) candidates.Add(c);
        }
        if (candidates.Count > 1)
        {
            var names = new List<string>();
            foreach (var c in candidates) names.Add(c.Name);
            GenError.Report("several classes have a static OnFrame (" + string.Join(", ", names) + "); pass --entry CLASS");
            return null;
        }
        return candidates.Count == 0 ? null : candidates[0];
    }

    private ExMethod? EventHook(ExClass entry)
    {
        var found = StaticMethod(entry, "OnEvent");
        if (found != null && (found.ReturnType != "void" || found.Parameters.Count != 1
            || found.ParameterTypes[0] != ApiModelLoader.RootNamespace + ".EventData"))
        {
            GenError.Report(entry.Name + ".OnEvent must be static void (Lub.EventData) to be an entry");
            return null;
        }
        return found;
    }

    private void Entry(ExClass entry, ExMethod? onEvent)
    {
        Line("/* Entry class " + entry.Name + ": called by src/tcs_host.c. */");
        Line("#define LUB_TCS_ENTRY 1");
        Hook(entry, "OnInit", "on_init", "", "");
        Hook(entry, "OnFrame", "on_frame", "float dt", "dt");
        Hook(entry, "OnQuit", "on_quit", "", "");
        Line(onEvent == null
            ? "static void tcs_game_on_event(const LubEventData *e) { (void)e; }"
            : "static void tcs_game_on_event(const LubEventData *e) { tcs_entry_" + entry.Name + "_OnEvent(tcs_lub_from_EventData(e)); }");
        ErrorHook(entry);
    }

    // Asks the entry's OnError how to handle a faulted callback (Log if there is none).
    // The message is a nursery string that does not cross the boundary, so during OnError the nesting depth is raised to stop the boundary
    private void ErrorHook(ExClass entry)
    {
        var found = StaticMethod(entry, "OnError");
        if (found == null)
        {
            Line("static int32_t tcs_game_on_error(const char *message) { (void)message; return LUB_ERROR_ACTION_LOG; }");
            return;
        }
        if (found.ReturnType != ApiModelLoader.RootNamespace + ".ErrorAction" || found.Parameters.Count != 1
            || found.ParameterTypes[0] != "string")
        {
            GenError.Report(entry.Name + ".OnError must be static Lub.ErrorAction (string) to be an entry");
            return;
        }
        Line("static int32_t tcs_game_on_error(const char *message) {");
        Line("  tcs_gc_call_depth++;");
        Line("  int32_t action = (int32_t)tcs_m_" + entry.Name + "_OnError(tcs_string_new((const unsigned char *)message, strlen(message)));");
        Line("  tcs_gc_call_depth--;");
        Line("  tcs_lib_gc();");
        Line("  return action;\n}");
    }

    private void Hook(ExClass entry, string method, string hook, string parameter, string argument)
    {
        var found = StaticMethod(entry, method);
        var signature = "static void tcs_game_" + hook + "(" + (parameter == "" ? "void" : parameter) + ")";
        if (found == null)
        {
            Line(signature + " {}");
            return;
        }
        int expected = parameter == "" ? 0 : 1;
        if (found.ReturnType != "void" || found.Parameters.Count != expected
            || (expected == 1 && found.ParameterTypes[0] != "float"))
        {
            GenError.Report(entry.Name + "." + method + " must be static void " + (expected == 1 ? "(float)" : "()") + " to be an entry");
            return;
        }
        Line(signature + " { tcs_entry_" + entry.Name + "_" + method + "(" + argument + "); }");
    }

    private void Handle(ApiType type)
    {
        Line("static Tcs_" + Il(type.Name) + " *tcs_lub_handle_" + type.Name + "(LubHandle handle) {");
        Line("  Tcs_" + Il(type.Name) + " *result = tcs_new_" + Il(type.Name) + "();");
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
            Line("    lub_gfx_resource_info(tcs_lub_context, handle, &key, &version);");
            Line("    result->f_version = version;");
            Line("  }");
        }
        Line("  return result;\n}");
    }

    private void View(ApiType type)
    {
        Line("static Tcs_" + Il(type.Name) + " *tcs_lub_view_" + type.Name + "(LubView value) {");
        Line("  if (!value.ptr) return NULL;");
        Line("  Tcs_" + Il(type.Name) + " *result = tcs_new_" + Il(type.Name) + "();");
        Line("  result->host_value = tcs_lub_view_store(value); result->f_length = value.len;");
        Line("  return result;\n}");
    }

    private static string From(TypeRef type, string value)
    {
        switch (type.Kind)
        {
            case LubTypeKind.String:
                return "tcs_lub_string(" + value + ")";
            case LubTypeKind.Handle:
                return "tcs_lub_handle_" + type.Name + "(" + value + ")";
            case LubTypeKind.View:
                return "tcs_lub_view_" + type.Name + "(" + value + ")";
            case LubTypeKind.Record:
            case LubTypeKind.Struct:
                return "tcs_lub_from_" + type.Name + "(&" + value + ")";
            case LubTypeKind.List:
                return ListFrom(type, value, "NULL", type.Nullable);
        }
        if (type.IsScalar) return value;
        GenError.Report("unsupported return: " + type.ToString());
        return "";
    }

    // Copies a C list (pointer + <value>_count) into a tcs List.
    // If a list exists (a field created by tcs_new), fills it; otherwise creates a new one.
    // When nullable, a NULL pointer becomes null
    private static string ListFrom(TypeRef type, string value, string list, bool nullable)
    {
        var elem = type.Elem!;
        string filled;
        if (elem.Kind == LubTypeKind.Record) filled = "tcs_lub_from_" + elem.Name + "_list(" + list + ", " + value + ", " + value + "_count)";
        else if (elem.IsScalar) filled = "tcs_lub_list(" + list + ", " + value + ", " + value + "_count, sizeof(*" + value + "))";
        else if (elem.Kind == LubTypeKind.String) filled = "tcs_lub_string_list(" + list + ", " + value + ", " + value + "_count)";
        else
        {
            GenError.Report("unsupported list result: " + type.ToString());
            filled = "";
        }
        return nullable ? "(" + value + " ? " + filled + " : NULL)" : filled;
    }

    private static string To(TypeRef type, string value)
    {
        if (type.Kind == LubTypeKind.String) return "tcs_lub_str(" + value + ")";
        if (type.Kind == LubTypeKind.Handle) return "(" + value + " ? (int32_t)" + value + "->host_value : 0)";
        if (type.IsScalar) return type.Nullable ? value + ".v" : value;
        if (type.Kind == LubTypeKind.Struct) return "tcs_lub_to_" + type.Name + "(" + value + (type.Nullable ? ".v" : "") + ")";
        GenError.Report("unsupported field: " + type.ToString());
        return "";
    }

    private static string HasPath(string prefix, string path, ApiField field) =>
        prefix + Text.DropEnd(path, Text.Len(field.HostName)) + "has_" + field.HostName;

    // Struct record: copies between the tcs2c value struct (fields are f_<name>) and the C API struct field by field
    private void StructInput(ApiType type)
    {
        Line("static Lub" + type.Name + " tcs_lub_to_" + type.Name + "(Tcs_" + Il(type.Name) + " source) {");
        Line("  Lub" + type.Name + " result;");
        foreach (var f in type.Fields) Line("  result." + f.HostName + " = " + To(f.Type, "source.f_" + f.HostName) + ";");
        Line("  return result;\n}");
    }

    private void StructOutput(ApiType type)
    {
        Line("static Tcs_" + Il(type.Name) + " tcs_lub_from_" + type.Name + "(const Lub" + type.Name + " *source) {");
        Line("  Tcs_" + Il(type.Name) + " result;");
        foreach (var f in type.Fields) Line("  result.f_" + f.HostName + " = " + From(f.Type, "source->" + f.HostName) + ";");
        Line("  return result;\n}");
    }

    private void InputRecord(ApiType type)
    {
        Line("static Lub" + type.Name + " tcs_lub_to_" + type.Name + "(Tcs_" + Il(type.Name) + " *source) {");
        Line("  Lub" + type.Name + " result = {0}; if (!source) return result;");
        foreach (var fp in Fields(type))
        {
            var field = fp.Field;
            var path = fp.Path;
            var src = "source->f_" + field.HostName;
            var dst = "result." + path;
            var has = HasPath("result.", path, field);
            if (field.Type.IsScalar && field.Optional)
                Line("  if (" + src + ".has) { " + has + " = true; " + dst + " = " + src + ".v; }");
            else if (field.Type.Kind == LubTypeKind.Struct && field.Optional)
                Line("  if (" + src + ".has) { " + has + " = true; " + dst + " = " + To(field.Type, src) + "; }");
            else if (field.Bits)
                Line("  " + (field.Optional ? has + " = " : "") + "tcs_lub_bits(" + src + ", &" + dst + ");");
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
                Line("    if (tcs_array_length(" + src + ") != " + length + ") tcs_fault(\"lub-array-length\");");
                Line("    memcpy(" + dst + ", " + src + "->data, sizeof(" + dst + "));\n  }");
            }
            else if (field.Type.Kind == LubTypeKind.List)
            {
                var elem = field.Type.Elem!;
                Line("  if (" + src + ") {");
                if (elem.Kind == LubTypeKind.Record)
                {
                    Line("    " + dst + " = tcs_lub_to_" + elem.Name + "_list(" + src + ", &" + dst + "_count);");
                    Line("  }");
                    continue;
                }
                Line("    " + dst + "_count = tcs_list_length(" + src + ");");
                if (elem.IsScalar && field.ArrayLen >= 0)
                {
                    int cap = field.ArrayLen;
                    // Fixed-length array field (`T n[cap]; int32_t n_count`): copies up to cap elements
                    Line("    if (" + dst + "_count > " + cap + ") tcs_fault(\"lub-array-length\");");
                    Line("    memcpy(" + dst + ", " + src + "->data, (size_t)" + dst + "_count * sizeof(*" + dst + "));");
                }
                else if (elem.IsScalar) Line("    " + dst + " = " + src + "->data;");
                else if (elem.Kind == LubTypeKind.Handle)
                {
                    Line("    LubHandle *items = tcs_alloc((size_t)" + dst + "_count * sizeof(*items));");
                    Line("    for (int i = 0; i < " + dst + "_count; i++) { Tcs_" + Il(elem.Name) + " *item = ((Tcs_" + Il(elem.Name) + " **)" + src + "->data)[i]; items[i] = item ? (int32_t)item->host_value : 0; }");
                    Line("    " + dst + " = items;");
                }
                else if (elem.Kind == LubTypeKind.Array && field.ArrayLen >= 0)
                {
                    int len = field.ArrayLen;
                    Line("    " + Scalar(elem.Elem!) + " (*items)[" + len + "] = tcs_alloc((size_t)" + dst + "_count * sizeof(*items));");
                    Line("    for (int i = 0; i < " + dst + "_count; i++) { TcsArray *item = ((TcsArray **)" + src + "->data)[i]; if (tcs_array_length(item) != " + len + ") tcs_fault(\"lub-array-length\"); memcpy(items[i], item->data, sizeof(items[i])); }");
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
                Line("  if (" + src + ") { " + has + " = true; " + dst + " = tcs_lub_to_" + field.Type.Name + "(" + src + "); }");
            else if (field.Type.Kind == LubTypeKind.Record)
                Line("  " + dst + " = tcs_lub_to_" + field.Type.Name + "(" + src + ");");
            // Callbacks stored in a record (WorldCallbacks) are kept by the runtime across declarations, so they are not supported.
            // If null, the C field also stays NULL
            else if (field.Type.Kind == LubTypeKind.Func)
                Line("  if (" + src + ") tcs_fault(\"lub-callback-field: " + type.Name + "." + field.Name + "\");");
            else Line("  " + dst + " = " + To(field.Type, src) + ";");
        }
        Line("  return result;\n}");
    }

    private void OutputRecord(ApiType type)
    {
        Line("static Tcs_" + Il(type.Name) + " *tcs_lub_from_" + type.Name + "(const Lub" + type.Name + " *source) {");
        Line("  Tcs_" + Il(type.Name) + " *result = tcs_new_" + Il(type.Name) + "();");
        foreach (var fp in Fields(type))
        {
            var field = fp.Field;
            var path = fp.Path;
            var dst = "result->f_" + field.HostName;
            var src = "source->" + path;
            var has = HasPath("source->", path, field);
            if (field.Type.Kind == LubTypeKind.Array)
            {
                Line("  if (tcs_array_length(" + dst + ") != " + (field.ArrayLen >= 0 ? field.ArrayLen.ToString() : "") + ") tcs_fault(\"lub-array-length\");");
                Line("  memcpy(" + dst + "->data, " + src + ", sizeof(" + src + "));");
            }
            else if (field.Type.IsScalar && field.Optional)
                Line("  " + dst + " = (" + Opt(field.Type) + "){" + has + ", " + src + "};");
            else if (field.Type.Kind == LubTypeKind.Struct && field.Optional)
                Line("  " + dst + " = (" + Opt(field.Type) + "){" + has + ", " + From(field.Type, src) + "};");
            else if (field.Bits)
                Line("  " + dst + " = " + (field.Optional ? "!" + has + " ? NULL : " : "") + "tcs_lub_bits_string(" + src + ");");
            else if (field.Type.Kind == LubTypeKind.Record && field.Optional)
                Line("  " + dst + " = " + has + " ? " + From(field.Type, src) + " : NULL;");
            else if (field.Type.Kind == LubTypeKind.List)
                Line("  " + dst + " = " + ListFrom(field.Type, src, dst, field.Optional && field.ArrayLen < 0) + ";");
            else Line("  " + dst + " = " + From(field.Type, src) + ";");
        }
        Line("  return result;\n}");
    }

    private void InputList(string name)
    {
        Line("static Lub" + name + " *tcs_lub_to_" + name + "_list(TcsList *source, int32_t *count) {");
        Line("  *count = tcs_list_length(source); Lub" + name + " *items = tcs_alloc((size_t)*count * sizeof(*items));");
        Line("  for (int i = 0; i < *count; i++) items[i] = tcs_lub_to_" + name + "(((Tcs_" + Il(name) + " **)source->data)[i]);");
        Line("  return items;\n}");
    }

    private void OutputList(string name)
    {
        Line("static TcsList *tcs_lub_from_" + name + "_list(TcsList *list, const Lub" + name + " *source, int32_t count) {");
        Line("  if (!list) list = tcs_list_new(sizeof(void *), &tcs_layout_ptr);");
        Line("  for (int i = 0; i < count; i++) { Tcs_" + Il(name) + " *item = tcs_lub_from_" + name + "(&source[i]); tcs_list_add(list, &item, sizeof(item), &tcs_layout_ptr); }");
        Line("  return list;\n}");
    }

    // Instance method of a foreign type.
    // The view's Get(int) reads a byte of the view
    private void Method(ApiType type, ApiFunction method, string foreignName)
    {
        var name = "tcs_host_" + foreignName.Replace(".", "_");
        if (type.Kind == "view" && method.Name == "Get" && method.Params.Count == 1
            && method.Params[0].Type.Kind == LubTypeKind.Int && method.Return.Kind == LubTypeKind.Int)
        {
            Line("int32_t " + name + "(Tcs_" + Il(type.Name) + " *self, int32_t p_index) {");
            Line("  LubView view = tcs_lub_view_load(((Tcs_" + Il(type.Name) + " *)tcs_nonnull(self))->host_value);");
            Line("  if (p_index < 0 || p_index >= view.len) tcs_fault(\"bounds\");");
            Line("  return view.ptr[p_index];\n}");
        }
        else GenError.Report("unsupported method: " + type.Name + "." + method.Name);
    }

    // C out values are received in the variable n_<name>.
    // A list uses two: the pointer and n_<name>_count.
    // A record that can be null receives its presence in has_<name> right after it
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
        foreach (var param in function.Params)
        {
            if (param.Type.Kind == LubTypeKind.Func) Callback(name, param);
        }
        var signature = new List<string>();
        foreach (var p in function.Params) signature.Add(TcsType(p.Type) + (p.IsOut ? " *" : " ") + "p_" + p.HostName);
        Line(TcsType(function.Return) + " tcs_host_" + name + "(" + (function.Params.Count == 0 ? "void" : string.Join(", ", signature)) + ") {");
        var st = new CallState();
        var args = st.Args;
        args.Add("tcs_lub_context");
        string? nativeName = null;
        string? nativeParam = null;
        foreach (var param in function.Params)
        {
            if (param.IsOut || param.IsCount) continue;
            var p = "p_" + param.HostName;
            var n = "n_" + param.HostName;
            var type = param.Type;
            // [LubCount] argument after an array (uses the first count elements of the array)
            ApiParam? countParam = null;
            int at = function.Params.IndexOf(param);
            for (int k = at + 1; k < function.Params.Count; k++)
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
                Line("  if (" + p + " && (uint32_t)" + count + " > (uint32_t)tcs_array_length(" + p + ")) tcs_fault(\"lub-array-length\");");
                args.Add(p + " ? (const float *)" + p + "->data : NULL");
                args.Add(count);
            }
            else if (type.Kind == LubTypeKind.Record)
            {
                Line("  Lub" + type.Name + " " + n + " = tcs_lub_to_" + type.Name + "(" + p + ");");
                args.Add(p + " ? &" + n + " : NULL");
            }
            else if (type.Kind == LubTypeKind.List && type.Elem!.IsScalar)
            {
                args.Add(p + " ? " + p + "->data : NULL");
                args.Add(p + " ? tcs_list_length(" + p + ") : 0");
            }
            else if (type.Kind == LubTypeKind.List && type.Elem!.Kind == LubTypeKind.Record)
            {
                Line("  int32_t " + n + "_count = 0; Lub" + type.Elem.Name + " *" + n + " = " + p + " ? tcs_lub_to_" + type.Elem.Name + "_list(" + p + ", &" + n + "_count) : NULL;");
                args.Add(n);
                args.Add(n + "_count");
            }
            else if (type.Kind == LubTypeKind.View)
            {
                Line("  LubView " + n + " = {0};");
                Line("  if (" + p + ") " + n + " = tcs_lub_view_load(" + p + "->host_value);");
                args.Add(n + ".ptr");
                args.Add(n + ".len");
            }
            else if (type.Kind == LubTypeKind.Native)
            {
                Line("  tcs_nonnull(" + p + "); LubBindings *" + n + " = tcs_lub_bindings_block(" + p + ");");
                args.Add(n);
                nativeName = n;
                nativeParam = p;
            }
            else if (type.Kind == LubTypeKind.Array && type.Elem!.Kind == LubTypeKind.Double)
            {
                Line("  tcs_nonnull(" + p + ");");
                args.Add("(const float *)" + p + "->data");
                args.Add("tcs_array_length(" + p + ")");
            }
            else if (type.IsScalar && type.Nullable) args.Add(p + ".has ? &" + p + ".v : NULL");
            else if (type.Kind == LubTypeKind.Func)
            {
                args.Add(p + " ? " + CallbackName(name, param) + " : NULL");
                args.Add(p);
            }
            else args.Add(To(type, p));
        }
        foreach (var param in function.Params)
        {
            if (param.IsOut) Output(param.Type, "n_" + param.HostName, st);
        }
        var ret = function.Return;
        bool returned = ret.Kind != LubTypeKind.Void;
        // NoFail C APIs return only scalars and handles as the return value and write everything else to out arguments
        bool byValue = function.NoFail && (ret.IsScalar || ret.Kind == LubTypeKind.Handle);
        if (byValue && ret.IsScalar && ret.Nullable)
        {
            GenError.Report("nullable NoFail return: " + ret.ToString());
            return;
        }
        if (byValue) Line("  " + NativeType(ret) + " result = {0};");
        else if (returned) Output(new TypeRef(ret.Kind, ret.Name, false, ret.Elem, ret.FuncParams, ret.FuncReturn), "result", st);
        // Nullable scalars and [LubMaybe] records receive value presence via has (same condition as CHeader)
        bool has = !function.NoFail && ret.Nullable && (ret.IsScalar || (ret.Kind == LubTypeKind.Record && function.Maybe));
        if (has)
        {
            Line("  bool has = false;");
            args.Add("&has");
        }
        var call = name + "(" + string.Join(", ", args) + ")";
        if (function.NoFail) Line("  " + (byValue ? "result = " : "") + call + ";");
        else
        {
            Line("  LubStatus status = " + call + ";");
            if (nativeName != null)
                Line("  while (status == LUB_BINDINGS_FULL) { " + nativeName + " = tcs_lub_bindings_grow(" + nativeParam + "); status = " + call + "; }");
            Line("  tcs_lub_check(status);");
            if (returned && ret.Kind == LubTypeKind.Struct) Line("  if (status == LUB_NOT_FOUND) return (" + TcsType(ret) + "){0};");
            else if (returned && !ret.IsScalar) Line("  if (status == LUB_NOT_FOUND) return NULL;");
            if (has && !ret.IsScalar) Line("  if (!has) return NULL;");
        }
        foreach (var param in function.Params)
        {
            if (!param.IsOut) continue;
            var n = "n_" + param.HostName;
            var value = From(param.Type, n);
            if (param.Type.Kind == LubTypeKind.Record && param.Type.Nullable) value = n + "_has ? " + value + " : NULL";
            Line("  *p_" + param.HostName + " = " + value + ";");
        }
        if (has && ret.IsScalar) Line("  return (" + Opt(ret) + "){has, result};");
        else if (returned) Line("  return " + From(ret, "result") + ";");
        Line("}");
    }

    // A Func argument maps to a C function pointer + user.
    // user is the tcs closure, which the C API calls only during the call (GC does not run because it is mid-frame)
    private static string CallbackName(string function, ApiParam param) => "tcs_lub_" + function + "_" + param.HostName;

    private void Callback(string function, ApiParam param)
    {
        var fn = param.Type;
        var ret = fn.FuncReturn!;
        if (ret.Kind != LubTypeKind.Void && !ret.IsScalar)
        {
            GenError.Report("unsupported callback return: " + param.Name + ": " + fn.ToString());
            return;
        }
        var cParams = new List<string>();
        cParams.Add("void *user");
        var tcsParams = new List<string>();
        tcsParams.Add("void **");
        var values = new List<string>();
        values.Add("f->cells");
        for (int i = 0; i < fn.FuncParams!.Count; i++)
        {
            var type = fn.FuncParams[i];
            var a = Text.Sub("abcdefghijklmnopqrstuvwxyz", i, 1);
            if (type.Kind == LubTypeKind.Record) cParams.Add("const Lub" + type.Name + " *" + a);
            else if (type.IsScalar) cParams.Add(Scalar(type) + " " + a);
            else
            {
                GenError.Report("unsupported callback parameter: " + param.Name + ": " + fn.ToString());
                return;
            }
            tcsParams.Add(TcsType(type));
            values.Add(type.Kind == LubTypeKind.Record ? "tcs_lub_from_" + type.Name + "(" + a + ")" : a);
        }
        var cRet = ret.Kind == LubTypeKind.Void ? "void" : Scalar(ret);
        Line("static " + cRet + " " + CallbackName(function, param) + "(" + string.Join(", ", cParams) + ") {");
        Line("  TcsClosure *f = user;");
        var call = "((" + TcsType(ret) + " (*)(" + string.Join(", ", tcsParams) + "))f->fn)(" + string.Join(", ", values) + ")";
        Line("  tcs_lub_callback_depth++;");
        Line("  " + (cRet == "void" ? "" : cRet + " result = ") + call + ";");
        Line("  tcs_lub_callback_depth--;");
        if (cRet != "void") Line("  return result;");
        Line("}");
    }

    // The contents block is placed in a GC byte array (it is reachable through the Bindings field, so the GC reclaims it once released).
    private void NativeBindings()
    {
        Line("static LubBindings *tcs_lub_bindings_alloc(Tcs_Lub_Bindings *owner, int32_t scale) {");
        Line("  TcsArray *block = tcs_array_new((int32_t)lub_bindings_size(scale), 1, NULL);");
        Line("  owner->f_blob = block; tcs_wb(owner);");
        Line("  lub_bindings_init((LubBindings *)block->data, scale);");
        Line("  return (LubBindings *)block->data;\n}");
        Line("static LubBindings *tcs_lub_bindings_block(Tcs_Lub_Bindings *owner) {");
        Line("  return owner->f_blob ? (LubBindings *)owner->f_blob->data : tcs_lub_bindings_alloc(owner, 1);\n}");
        Line("static LubBindings *tcs_lub_bindings_grow(Tcs_Lub_Bindings *owner) {");
        Line("  LubBindings *old = (LubBindings *)owner->f_blob->data;");
        Line("  LubBindings *grown = tcs_lub_bindings_alloc(owner, lub_bindings_scale(old) * 2);");
        Line("  lub_bindings_grow_into(grown, old);");
        Line("  return grown;\n}");
    }

    private const string Helpers = """
        /* Depth of callbacks called from inside runtime functions.
           A fault in this span cannot unwind across the runtime's frames, so the host stops the process */
        static int tcs_lub_callback_depth;
        static void tcs_lub_check(LubStatus status) {
          if (status != LUB_ERROR) return;
          static char text[1024];
          snprintf(text, sizeof text, "lub-api: %s", lub_last_error(tcs_lub_context));
          tcs_fault(text);
        }
        static LubStr tcs_lub_str(TcsString *text) {
          return text ? (LubStr){(const char *)text->data, (int32_t)text->length} : (LubStr){0};
        }
        static TcsString *tcs_lub_string(LubStr text) {
          return text.ptr ? tcs_string_new((const unsigned char *)text.ptr, (size_t)text.len) : NULL;
        }
        static TcsList *tcs_lub_list(TcsList *list, const void *data, int32_t count, size_t size) {
          if (!list) list = tcs_list_new(size, NULL);
          tcs_list_reserve(list, (size_t)count);
          if (count) memcpy(list->data, data, (size_t)count * size);
          list->length = (size_t)count;
          return list;
        }
        static TcsList *tcs_lub_string_list(TcsList *list, const LubStr *data, int32_t count) {
          if (!list) list = tcs_list_new(sizeof(TcsString *), NULL);
          tcs_list_reserve(list, (size_t)count);
          for (int32_t i = 0; i < count; i++) ((TcsString **)list->data)[i] = tcs_lub_string(data[i]);
          list->length = (size_t)count;
          return list;
        }
        """;

    // The 64 bit mask of [LubBits] is a hex string on the C# surface (16 digits when output).
    // The parsing rules are the same as the tcs VM host functions (VmBinding): null means no value, 1 to 16 hex digits (0x prefix allowed) are read, and empty or 17 or more digits is an error.
    private const string BitsHelpers = """
        #include <inttypes.h>
        static _Noreturn void tcs_lub_bits_fault(const char *what, TcsString *text) {
          fprintf(stderr, "bit mask: %s '%.*s'\n", what, (int)text->length, (const char *)text->data);
          tcs_fault("lub-bits");
        }
        static bool tcs_lub_bits(TcsString *text, uint64_t *out) {
          if (!text) return false;
          int32_t length = (int32_t)text->length, i = 0;
          if (length >= 2 && text->data[0] == '0' && (text->data[1] == 'x' || text->data[1] == 'X')) i = 2;
          if (i == length) tcs_lub_bits_fault("empty hex string", text);
          if (length - i > 16) tcs_lub_bits_fault("more than 16 hex digits", text);
          uint64_t value = 0;
          for (; i < length; i++) {
            unsigned char c = text->data[i];
            int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (digit < 0) tcs_lub_bits_fault("invalid hex", text);
            value = value << 4 | (uint64_t)digit;
          }
          *out = value;
          return true;
        }
        static TcsString *tcs_lub_bits_string(uint64_t value) {
          char text[17];
          snprintf(text, sizeof(text), "%016" PRIx64, value);
          return tcs_string_new((const unsigned char *)text, 16);
        }
        """;

    // A view is valid until the end of the frame.
    // tcs objects can survive across frames, so the actual data is kept in a host-side table and host_value holds only the frame and an index.
    private const string ViewHelpers = """
        static LubView *tcs_lub_views;
        static uint32_t tcs_lub_view_count, tcs_lub_view_capacity;
        static int32_t tcs_lub_view_frame;
        static uint64_t tcs_lub_view_store(LubView value) {
          int32_t frame = lub_frame_index(tcs_lub_context);
          if (frame != tcs_lub_view_frame) { tcs_lub_view_frame = frame; tcs_lub_view_count = 0; }
          if (tcs_lub_view_count == tcs_lub_view_capacity) {
            tcs_lub_view_capacity = tcs_lub_view_capacity ? tcs_lub_view_capacity * 2 : 16;
            tcs_lub_views = realloc(tcs_lub_views, tcs_lub_view_capacity * sizeof(*tcs_lub_views));
            if (!tcs_lub_views) tcs_fault("allocation-overflow");
          }
          tcs_lub_views[tcs_lub_view_count] = value;
          return (uint64_t)(uint32_t)frame << 32 | tcs_lub_view_count++;
        }
        static LubView tcs_lub_view_load(uint64_t host_value) {
          int32_t frame = lub_frame_index(tcs_lub_context);
          uint32_t index = (uint32_t)host_value;
          if ((int32_t)(host_value >> 32) != frame || frame != tcs_lub_view_frame || index >= tcs_lub_view_count
              || tcs_lub_views[index].frame != frame) tcs_fault("lub-expired-view");
          return tcs_lub_views[index];
        }
        """;
}
