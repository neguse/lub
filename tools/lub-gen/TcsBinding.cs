using System.Text;
using TinyCs;

namespace LubGen;

public static class TcsBinding
{
    public static string Generate(ApiModel model, IlExportResult game) => new Gen(model, game).Run();

    private sealed class Gen(ApiModel model, IlExportResult game)
    {
        private readonly StringBuilder output = new();
        private readonly HashSet<string> inputs = new();
        private readonly HashSet<string> outputs = new();
        private void Line(string text = "") => output.AppendLine(text);
        private static string Scalar(TypeRef type) => type.Kind switch
        {
            LubTypeKind.Int or LubTypeKind.Enum => "int32_t",
            LubTypeKind.Double => "float", LubTypeKind.Bool => "bool",
            _ => throw new InvalidOperationException($"unsupported scalar: {type}"),
        };
        private static string Opt(TypeRef type) => type.Kind switch
        {
            LubTypeKind.Double => "TcsOptF32", LubTypeKind.Bool => "TcsOptBool", _ => "TcsOptI32",
        };
        private static string TcsType(TypeRef type) => type.Kind switch
        {
            LubTypeKind.Void => "void",
            LubTypeKind.Int or LubTypeKind.Double or LubTypeKind.Bool or LubTypeKind.Enum
                => type.Nullable ? Opt(type) : Scalar(type),
            LubTypeKind.String => "TcsString *",
            LubTypeKind.Handle or LubTypeKind.View or LubTypeKind.Record => $"Tcs_{type.Name} *",
            LubTypeKind.List => "TcsList *", LubTypeKind.Array => "TcsArray *",
            LubTypeKind.Dict => "TcsDict *",
            _ => throw new InvalidOperationException($"unsupported tcs parameter: {type}"),
        };
        private static string NativeType(TypeRef type) => type.Kind switch
        {
            LubTypeKind.String => "LubStr", LubTypeKind.Handle => "LubHandle",
            LubTypeKind.View => "LubView", LubTypeKind.Record => "Lub" + type.Name,
            _ => Scalar(type),
        };
        private IEnumerable<(ApiField Field, string Path)> Fields(ApiType type, string prefix = "")
        {
            if (type.Base != null)
                foreach (var field in Fields(model.FindType(type.Base)!, prefix + "base.")) yield return field;
            foreach (var field in type.Fields) yield return (field, prefix + field.LuaName);
        }
        private void Record(TypeRef type, HashSet<string> set)
        {
            if (type.Kind != LubTypeKind.Record || !set.Add(type.Name)) return;
            foreach (var (field, _) in Fields(model.FindType(type.Name)!)) Record(field.Type, set);
        }

        public string Run()
        {
            var calls = game.ForeignMethods.Select(m => m.Name).ToHashSet();
            var values = game.ForeignValues.Where(v => v.Constant == null).Select(v => v.Name).ToHashSet();
            var functions = model.Namespaces.SelectMany(ns => ns.Functions
                .Where(f => calls.Contains(ns.LuaPath + "." + f.LuaName)).Select(f => (ns, f))).ToArray();
            foreach (var (_, function) in functions)
            {
                if (function.NoC) throw new InvalidOperationException($"No C API for {function.Name}");
                foreach (var param in function.Params) Record(param.Type, param.IsOut ? outputs : inputs);
                Record(function.Return, outputs);
            }
            Line("/* Generated from lub_stub.cs. Include after the tcs2c game source. */");
            Line("#include <lub/lub_api.h>");
            Line("static LubContext *tcs_lub_context;");
            Line(Helpers);
            var external = model.Types.Where(t => game.Classes.Any(c => c.IsExternal && c.Name == t.Name)).ToArray();
            if (external.Any(t => t.Kind == "view")) Line(ViewHelpers);
            foreach (var type in external)
            {
                if (type.Kind == "handle") Handle(type);
                if (type.Kind == "view") View(type);
            }
            foreach (var name in inputs) Line($"static Lub{name} tcs_lub_to_{name}(Tcs_{name} *source);");
            foreach (var name in outputs) Line($"static Tcs_{name} *tcs_lub_from_{name}(const Lub{name} *source);");
            foreach (var name in inputs) InputRecord(model.FindType(name)!);
            foreach (var name in outputs) OutputRecord(model.FindType(name)!);
            if (functions.Any(f => f.f.Params.Any(p => p.Type.Kind == LubTypeKind.Dict))) Bindings();
            foreach (var ns in model.Namespaces)
                foreach (var value in ns.StaticFields.Where(f => values.Contains(ns.LuaPath + "." + f.LuaName)))
                {
                    var name = CHeader.FunctionName(ns, value.LuaName);
                    Line($"{TcsType(value.Type)} tcs_host_{name}(void) {{ return {From(value.Type, name + "(tcs_lub_context)")}; }}");
                }
            foreach (var (ns, function) in functions) Function(ns, function);
            return output.ToString();
        }

        private void Handle(ApiType type)
        {
            Line($"static Tcs_{type.Name} *tcs_lub_handle_{type.Name}(LubHandle handle) {{");
            Line($"  Tcs_{type.Name} *result = tcs_new_{type.Name}();");
            Line("  result->host_value = (uint32_t)handle;");
            if (type.Fields.Any(f => f.Name == "Version"))
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
            Line($"static Tcs_{type.Name} *tcs_lub_view_{type.Name}(LubView value) {{");
            Line("  if (!value.ptr) return NULL;");
            Line($"  Tcs_{type.Name} *result = tcs_new_{type.Name}();");
            Line("  result->host_value = tcs_lub_view_store(value); result->f_length = value.len;");
            Line("  return result;\n}");
        }
        private static string From(TypeRef type, string value) => type.Kind switch
        {
            LubTypeKind.String => $"tcs_lub_string({value})",
            LubTypeKind.Handle => $"tcs_lub_handle_{type.Name}({value})",
            LubTypeKind.View => $"tcs_lub_view_{type.Name}({value})",
            LubTypeKind.Record => $"tcs_lub_from_{type.Name}(&{value})",
            _ when type.IsScalar => value,
            _ => throw new InvalidOperationException($"unsupported return: {type}"),
        };
        private static string To(TypeRef type, string value) => type.Kind switch
        {
            LubTypeKind.String => $"tcs_lub_str({value})",
            LubTypeKind.Handle => $"({value} ? (int32_t){value}->host_value : 0)",
            _ when type.IsScalar => type.Nullable ? value + ".v" : value,
            _ => throw new InvalidOperationException($"unsupported field: {type}"),
        };
        private void InputRecord(ApiType type)
        {
            Line($"static Lub{type.Name} tcs_lub_to_{type.Name}(Tcs_{type.Name} *source) {{");
            Line($"  Lub{type.Name} result = {{0}}; if (!source) return result;");
            foreach (var (field, path) in Fields(type))
            {
                var src = "source->f_" + field.LuaName;
                var dst = "result." + path;
                var has = "result." + path[..^field.LuaName.Length] + "has_" + field.LuaName;
                if (field.Type.IsScalar && field.Optional)
                    Line($"  if ({src}.has) {{ {has} = true; {dst} = {src}.v; }}");
                else if (field.Type.Kind == LubTypeKind.Array)
                {
                    var length = field.ArrayLen ?? throw new InvalidOperationException("array length required");
                    Line($"  if ({src}) {{");
                    if (field.Optional) Line($"    {has} = true;");
                    Line($"    if (tcs_array_length({src}) != {length}) tcs_fault(\"lub-array-length\");");
                    Line($"    memcpy({dst}, {src}->data, sizeof({dst}));\n  }}");
                }
                else if (field.Type.Kind == LubTypeKind.List)
                {
                    Line($"  if ({src}) {{");
                    Line($"    {dst}_count = tcs_list_length({src});");
                    var elem = field.Type.Elem!;
                    if (elem.IsScalar) Line($"    {dst} = {src}->data;");
                    else if (elem.Kind == LubTypeKind.Handle)
                    {
                        Line($"    LubHandle *items = tcs_alloc((size_t){dst}_count * sizeof(*items));");
                        Line($"    for (int i = 0; i < {dst}_count; i++) {{ Tcs_{elem.Name} *item = ((Tcs_{elem.Name} **){src}->data)[i]; items[i] = item ? (int32_t)item->host_value : 0; }}");
                        Line($"    {dst} = items;");
                    }
                    else if (elem.Kind == LubTypeKind.Array && field.ArrayLen is int len)
                    {
                        Line($"    {Scalar(elem.Elem!)} (*items)[{len}] = tcs_alloc((size_t){dst}_count * sizeof(*items));");
                        Line($"    for (int i = 0; i < {dst}_count; i++) {{ TcsArray *item = ((TcsArray **){src}->data)[i]; if (tcs_array_length(item) != {len}) tcs_fault(\"lub-array-length\"); memcpy(items[i], item->data, sizeof(items[i])); }}");
                        Line($"    {dst} = items;");
                    }
                    else throw new InvalidOperationException($"unsupported list field: {field.Name}");
                    Line("  }");
                }
                else if (field.Type.Kind == LubTypeKind.Record)
                    Line($"  {dst} = tcs_lub_to_{field.Type.Name}({src});");
                else Line($"  {dst} = {To(field.Type, src)};");
            }
            Line("  return result;\n}");
        }
        private void OutputRecord(ApiType type)
        {
            Line($"static Tcs_{type.Name} *tcs_lub_from_{type.Name}(const Lub{type.Name} *source) {{");
            Line($"  Tcs_{type.Name} *result = tcs_new_{type.Name}();");
            foreach (var (field, path) in Fields(type))
            {
                var dst = "result->f_" + field.LuaName;
                var src = "source->" + path;
                if (field.Type.Kind == LubTypeKind.Array)
                {
                    Line($"  if (tcs_array_length({dst}) != {field.ArrayLen}) tcs_fault(\"lub-array-length\");");
                    Line($"  memcpy({dst}->data, {src}, sizeof({src}));");
                }
                else if (field.Type.IsScalar && field.Optional)
                    Line($"  {dst} = ({Opt(field.Type)}){{source->{path[..^field.LuaName.Length]}has_{field.LuaName}, {src}}};");
                else Line($"  {dst} = {From(field.Type, src)};");
            }
            Line("  return result;\n}");
        }

        private void Function(ApiNamespace ns, ApiFunction function)
        {
            var name = CHeader.FunctionName(ns, function.LuaName);
            var signature = function.Params.Select(p => TcsType(p.Type) + (p.IsOut ? " *" : " ") + "p_" + p.LuaName);
            Line($"{TcsType(function.Return)} tcs_host_{name}({(function.Params.Count == 0 ? "void" : string.Join(", ", signature))}) {{");
            var args = new List<string> { "tcs_lub_context" };
            foreach (var param in function.Params.Where(p => !p.IsOut))
            {
                var p = "p_" + param.LuaName;
                var n = "n_" + param.LuaName;
                var type = param.Type;
                if (type.Kind == LubTypeKind.Record)
                {
                    Line($"  Lub{type.Name} {n} = tcs_lub_to_{type.Name}({p});");
                    args.Add($"{p} ? &{n} : NULL");
                }
                else if (type.Kind == LubTypeKind.List && type.Elem!.IsScalar)
                {
                    args.Add($"{p} ? {p}->data : NULL"); args.Add($"{p} ? tcs_list_length({p}) : 0");
                }
                else if (type.Kind == LubTypeKind.View)
                {
                    Line($"  LubView {n} = {{0}};");
                    Line($"  if ({p}) {n} = tcs_lub_view_load({p}->host_value);");
                    args.Add(n + ".ptr"); args.Add(n + ".len");
                }
                else if (type.Kind == LubTypeKind.Dict)
                {
                    Line($"  int32_t {n}_count = 0; LubBinding *{n} = tcs_lub_bindings({p}, &{n}_count);");
                    args.Add(n); args.Add(n + "_count");
                }
                else if (type.IsScalar && type.Nullable) args.Add($"{p}.has ? &{p}.v : NULL");
                else args.Add(To(type, p));
            }
            foreach (var param in function.Params.Where(p => p.IsOut))
            {
                Line($"  {NativeType(param.Type)} n_{param.LuaName} = {{0}};");
                args.Add("&n_" + param.LuaName);
            }
            bool returned = function.Return.Kind != LubTypeKind.Void;
            if (returned) Line($"  {NativeType(function.Return)} result = {{0}};");
            if (!function.NoFail && returned) args.Add("&result");
            var call = $"{name}({string.Join(", ", args)})";
            if (function.NoFail) Line($"  {(returned ? "result = " : "")}{call};");
            else
            {
                Line($"  LubStatus status = {call}; tcs_lub_check(status);");
                if (returned && !function.Return.IsScalar) Line("  if (status == LUB_NOT_FOUND) return NULL;");
            }
            foreach (var param in function.Params.Where(p => p.IsOut))
                Line($"  *p_{param.LuaName} = {From(param.Type, "n_" + param.LuaName)};");
            if (returned) Line($"  return {From(function.Return, "result")};");
            Line("}");
        }

        private void Bindings()
        {
            Line("static LubBinding *tcs_lub_bindings(TcsDict *source, int32_t *count) {");
            Line("  tcs_nonnull(source); if (TCS_GC_HEADER(source)->type_id != TCS_TYPE_DICT_STRING_OBJECT) tcs_fault(\"lub-binding-dictionary\");");
            Line("  int capacity = tcs_dict_count(source); TcsDict *uniforms = NULL;");
            Line("  for (size_t i = 0; i < source->bucket_count; i++) for (TcsDictNode *n = source->buckets[i]; n; n = n->next) {");
            Line("    if (n->key_s->length == 8 && memcmp(n->key_s->data, \"uniforms\", 8) == 0) { memcpy(&uniforms, n->value, sizeof(uniforms)); tcs_nonnull(uniforms); if (TCS_GC_HEADER(uniforms)->type_id != TCS_TYPE_DICT_STRING_OBJECT) tcs_fault(\"lub-uniform-dictionary\"); capacity += tcs_dict_count(uniforms); }");
            Line("  }");
            Line("  LubBinding *result = tcs_alloc((size_t)capacity * sizeof(*result)); *count = 0;");
            Line("  for (int pass = 0; pass < 2; pass++) {");
            Line("    TcsDict *dict = pass ? uniforms : source; if (!dict) continue;");
            Line("    for (size_t i = 0; i < dict->bucket_count; i++) for (TcsDictNode *n = dict->buckets[i]; n; n = n->next) {");
            Line("      if (n->key_s->length >= 2 && n->key_s->data[0] == '_' && n->key_s->data[1] == '_') continue;");
            Line("      if (!pass && n->key_s->length == 8 && memcmp(n->key_s->data, \"uniforms\", 8) == 0) continue;");
            Line("      void *value; memcpy(&value, n->value, sizeof(value)); tcs_nonnull(value);");
            Line("      LubBinding *binding = &result[(*count)++]; binding->name = tcs_lub_str(n->key_s);");
            Line("      uint32_t type = TCS_GC_HEADER(value)->type_id;");
            Line("      if (pass) { if (type != TCS_TYPE_ARRAY_F32) tcs_fault(\"lub-uniform-type\"); TcsArray *array = value; binding->values = (const float *)array->data; binding->count = tcs_array_length(array); }");
            foreach (var type in model.Types.Where(t => t.Kind == "handle" && game.Classes.Any(c => c.Name == t.Name)))
                Line($"      else if (type == TCS_TYPE_{type.Name}) binding->handle = (int32_t)((Tcs_{type.Name} *)value)->host_value;");
            Line("      else tcs_fault(\"lub-binding-type\");");
            Line("    }\n  }\n  return result;\n}");
        }

        private const string Helpers = """
            static void tcs_lub_check(LubStatus status) {
              if (status == LUB_ERROR) { fprintf(stderr, "%s\n", lub_last_error(tcs_lub_context)); tcs_fault("lub-api"); }
            }
            static LubStr tcs_lub_str(TcsString *text) {
              return text ? (LubStr){(const char *)text->data, tcs_string_length(text)} : (LubStr){0};
            }
            static TcsString *tcs_lub_string(LubStr text) {
              return text.ptr ? tcs_string_new((const unsigned char *)text.ptr, (size_t)text.len) : NULL;
            }
            """;

        // view は frame の終わりまで有効。tcs の object は frame を跨いで残りうるので、
        // 実体は host 側の表に置き、host_value には frame と番号だけを持たせる。
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
}
