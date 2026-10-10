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
        // list の要素として渡す / 受け取る record (配列との詰め替えを別に生成する)
        private readonly HashSet<string> inputLists = new();
        private readonly HashSet<string> outputLists = new();
        private void Line(string text = "") => output.AppendLine(text);
        // tcs2c の C 記号 (Tcs_ / tcs_new_ / TCS_TYPE_) は namespace 修飾した型名で引く
        private static string Il(string name) => ApiModelLoader.RootNamespace + "_" + name;
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
            LubTypeKind.Handle or LubTypeKind.View or LubTypeKind.Record => $"Tcs_{Il(type.Name)} *",
            LubTypeKind.List => "TcsList *", LubTypeKind.Array => "TcsArray *",
            LubTypeKind.Dict => "TcsDict *",
            _ => throw new InvalidOperationException($"unsupported tcs parameter: {type}"),
        };
        private static string NativeType(TypeRef type) => type.Kind switch
        {
            LubTypeKind.String => "LubStr", LubTypeKind.Handle => "LubHandle",
            LubTypeKind.View => "LubView", LubTypeKind.Record => "Lub" + type.Name,
            LubTypeKind.List => $"const {NativeType(type.Elem!)} *",
            _ => Scalar(type),
        };
        private IEnumerable<(ApiField Field, string Path)> Fields(ApiType type, string prefix = "")
        {
            if (type.Base != null)
                foreach (var field in Fields(model.FindType(type.Base)!, prefix + "base.")) yield return field;
            foreach (var field in type.Fields) yield return (field, prefix + field.LuaName);
        }
        private void Record(TypeRef type, bool isOutput)
        {
            if (type.Kind is LubTypeKind.List or LubTypeKind.Array)
            {
                if (type.Elem!.Kind == LubTypeKind.Record) (isOutput ? outputLists : inputLists).Add(type.Elem.Name);
                Record(type.Elem, isOutput);
                return;
            }
            if (type.Kind != LubTypeKind.Record || !(isOutput ? outputs : inputs).Add(type.Name)) return;
            foreach (var (field, _) in Fields(model.FindType(type.Name)!)) Record(field.Type, isOutput);
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
                foreach (var param in function.Params) Record(param.Type, param.IsOut);
                Record(function.Return, true);
            }
            Line("/* Generated from lub_stub.cs. Include after the tcs2c game source. */");
            Line("#include <lub/lub_api.h>");
            Line("static LubContext *tcs_lub_context;");
            Line(Helpers);
            var external = model.Types.Where(t => game.Classes.Any(c => c.IsExternal && c.Name == Il(t.Name))).ToArray();
            if (external.Any(t => t.Kind == "view")) Line(ViewHelpers);
            foreach (var type in external)
            {
                if (type.Kind == "handle") Handle(type);
                if (type.Kind == "view") View(type);
            }
            foreach (var name in inputs) Line($"static Lub{name} tcs_lub_to_{name}(Tcs_{Il(name)} *source);");
            foreach (var name in outputs) Line($"static Tcs_{Il(name)} *tcs_lub_from_{name}(const Lub{name} *source);");
            foreach (var name in inputLists) Line($"static Lub{name} *tcs_lub_to_{name}_list(TcsList *source, int32_t *count);");
            foreach (var name in outputLists) Line($"static TcsList *tcs_lub_from_{name}_list(TcsList *list, const Lub{name} *source, int32_t count);");
            foreach (var name in inputs) InputRecord(model.FindType(name)!);
            foreach (var name in outputs) OutputRecord(model.FindType(name)!);
            foreach (var name in inputLists) InputList(name);
            foreach (var name in outputLists) OutputList(name);
            foreach (var type in external)
                foreach (var method in type.Methods)
                {
                    var foreign = game.ForeignMethods.FirstOrDefault(m => m.Receiver == Il(type.Name) && m.Name.EndsWith("." + method.LuaName, StringComparison.Ordinal));
                    if (foreign != null) Method(type, method, foreign.Name);
                }
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
            Line($"static Tcs_{Il(type.Name)} *tcs_lub_handle_{type.Name}(LubHandle handle) {{");
            Line($"  Tcs_{Il(type.Name)} *result = tcs_new_{Il(type.Name)}();");
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
            Line($"static Tcs_{Il(type.Name)} *tcs_lub_view_{type.Name}(LubView value) {{");
            Line("  if (!value.ptr) return NULL;");
            Line($"  Tcs_{Il(type.Name)} *result = tcs_new_{Il(type.Name)}();");
            Line("  result->host_value = tcs_lub_view_store(value); result->f_length = value.len;");
            Line("  return result;\n}");
        }
        private static string From(TypeRef type, string value) => type.Kind switch
        {
            LubTypeKind.String => $"tcs_lub_string({value})",
            LubTypeKind.Handle => $"tcs_lub_handle_{type.Name}({value})",
            LubTypeKind.View => $"tcs_lub_view_{type.Name}({value})",
            LubTypeKind.Record => $"tcs_lub_from_{type.Name}(&{value})",
            LubTypeKind.List => ListFrom(type, value, "NULL", type.Nullable),
            _ when type.IsScalar => value,
            _ => throw new InvalidOperationException($"unsupported return: {type}"),
        };
        // C の list (pointer + <value>_count) を tcs の List に写す。list があれば
        // (tcs_new が作った field) それを埋め、無ければ新しく作る。nullable は
        // pointer NULL を null にする
        private static string ListFrom(TypeRef type, string value, string list, bool nullable)
        {
            var elem = type.Elem!;
            var filled = elem.Kind switch
            {
                LubTypeKind.Record => $"tcs_lub_from_{elem.Name}_list({list}, {value}, {value}_count)",
                _ when elem.IsScalar => $"tcs_lub_list({list}, {value}, {value}_count, sizeof(*{value}))",
                _ => throw new InvalidOperationException($"unsupported list result: {type}"),
            };
            return nullable ? $"({value} ? {filled} : NULL)" : filled;
        }
        private static string To(TypeRef type, string value) => type.Kind switch
        {
            LubTypeKind.String => $"tcs_lub_str({value})",
            LubTypeKind.Handle => $"({value} ? (int32_t){value}->host_value : 0)",
            _ when type.IsScalar => type.Nullable ? value + ".v" : value,
            _ => throw new InvalidOperationException($"unsupported field: {type}"),
        };
        private void InputRecord(ApiType type)
        {
            Line($"static Lub{type.Name} tcs_lub_to_{type.Name}(Tcs_{Il(type.Name)} *source) {{");
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
                    var elem = field.Type.Elem!;
                    Line($"  if ({src}) {{");
                    if (elem.Kind == LubTypeKind.Record)
                    {
                        Line($"    {dst} = tcs_lub_to_{elem.Name}_list({src}, &{dst}_count);");
                        Line("  }");
                        continue;
                    }
                    Line($"    {dst}_count = tcs_list_length({src});");
                    if (elem.IsScalar && field.ArrayLen is int cap)
                    {
                        // 固定長の配列 field (`T n[cap]; int32_t n_count`): 長さ cap までを写す
                        Line($"    if ({dst}_count > {cap}) tcs_fault(\"lub-array-length\");");
                        Line($"    memcpy({dst}, {src}->data, (size_t){dst}_count * sizeof(*{dst}));");
                    }
                    else if (elem.IsScalar) Line($"    {dst} = {src}->data;");
                    else if (elem.Kind == LubTypeKind.Handle)
                    {
                        Line($"    LubHandle *items = tcs_alloc((size_t){dst}_count * sizeof(*items));");
                        Line($"    for (int i = 0; i < {dst}_count; i++) {{ Tcs_{Il(elem.Name)} *item = ((Tcs_{Il(elem.Name)} **){src}->data)[i]; items[i] = item ? (int32_t)item->host_value : 0; }}");
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
            Line($"static Tcs_{Il(type.Name)} *tcs_lub_from_{type.Name}(const Lub{type.Name} *source) {{");
            Line($"  Tcs_{Il(type.Name)} *result = tcs_new_{Il(type.Name)}();");
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
                else if (field.Type.Kind == LubTypeKind.List)
                    Line($"  {dst} = {ListFrom(field.Type, src, dst, field.Optional && field.ArrayLen == null)};");
                else Line($"  {dst} = {From(field.Type, src)};");
            }
            Line("  return result;\n}");
        }
        private void InputList(string name)
        {
            Line($"static Lub{name} *tcs_lub_to_{name}_list(TcsList *source, int32_t *count) {{");
            Line($"  *count = tcs_list_length(source); Lub{name} *items = tcs_alloc((size_t)*count * sizeof(*items));");
            Line($"  for (int i = 0; i < *count; i++) items[i] = tcs_lub_to_{name}(((Tcs_{Il(name)} **)source->data)[i]);");
            Line("  return items;\n}");
        }
        private void OutputList(string name)
        {
            Line($"static TcsList *tcs_lub_from_{name}_list(TcsList *list, const Lub{name} *source, int32_t count) {{");
            Line("  if (!list) list = tcs_list_new(sizeof(void *), &tcs_layout_ptr);");
            Line($"  for (int i = 0; i < count; i++) {{ Tcs_{Il(name)} *item = tcs_lub_from_{name}(&source[i]); tcs_list_add(list, &item, sizeof(item), &tcs_layout_ptr); }}");
            Line("  return list;\n}");
        }
        // 外部型の instance method。view の Get(int) は view の byte を読む
        private void Method(ApiType type, ApiFunction method, string foreignName)
        {
            var name = "tcs_host_" + foreignName.Replace('.', '_');
            if (type.Kind == "view" && method.Name == "Get" && method.Params.Count == 1
                && method.Params[0].Type.Kind == LubTypeKind.Int && method.Return.Kind == LubTypeKind.Int)
            {
                Line($"int32_t {name}(Tcs_{Il(type.Name)} *self, int32_t p_index) {{");
                Line("  LubView view = tcs_lub_view_load(((Tcs_" + Il(type.Name) + " *)tcs_nonnull(self))->host_value);");
                Line("  if (p_index < 0 || p_index >= view.len) tcs_fault(\"bounds\");");
                Line("  return view.ptr[p_index];\n}");
            }
            else throw new InvalidOperationException($"unsupported method: {type.Name}.{method.Name}");
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
                else if (type.Kind == LubTypeKind.List && type.Elem!.Kind == LubTypeKind.Record)
                {
                    Line($"  int32_t {n}_count = 0; Lub{type.Elem.Name} *{n} = {p} ? tcs_lub_to_{type.Elem.Name}_list({p}, &{n}_count) : NULL;");
                    args.Add(n); args.Add(n + "_count");
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
            // C の out は変数 n_<name> に受ける。list は pointer と n_<name>_count の 2 つ
            void Output(TypeRef type, string n)
            {
                Line($"  {NativeType(type)} {n} = {{0}};");
                args.Add("&" + n);
                if (type.Kind != LubTypeKind.List) return;
                Line($"  int32_t {n}_count = 0;");
                args.Add($"&{n}_count");
            }
            foreach (var param in function.Params.Where(p => p.IsOut)) Output(param.Type, "n_" + param.LuaName);
            bool returned = function.Return.Kind != LubTypeKind.Void;
            // NoFail の C API は scalar と handle だけを戻り値で返し、それ以外は out 引数に書く
            bool byValue = function.NoFail && (function.Return.IsScalar || function.Return.Kind == LubTypeKind.Handle);
            if (byValue) Line($"  {NativeType(function.Return)} result = {{0}};");
            else if (returned) Output(function.Return, "result");
            // [LubMaybe] の record は値の有無を has で受ける
            bool maybe = !function.NoFail && function.Maybe && function.Return.Kind == LubTypeKind.Record && function.Return.Nullable;
            if (maybe) { Line("  bool has = false;"); args.Add("&has"); }
            var call = $"{name}({string.Join(", ", args)})";
            if (function.NoFail) Line($"  {(byValue ? "result = " : "")}{call};");
            else
            {
                Line($"  LubStatus status = {call}; tcs_lub_check(status);");
                if (returned && !function.Return.IsScalar) Line("  if (status == LUB_NOT_FOUND) return NULL;");
                if (maybe) Line("  if (!has) return NULL;");
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
            foreach (var type in model.Types.Where(t => t.Kind == "handle" && game.Classes.Any(c => c.Name == Il(t.Name))))
                Line($"      else if (type == TCS_TYPE_{Il(type.Name)}) binding->handle = (int32_t)((Tcs_{Il(type.Name)} *)value)->host_value;");
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
            static TcsList *tcs_lub_list(TcsList *list, const void *data, int32_t count, size_t size) {
              if (!list) list = tcs_list_new(size, NULL);
              tcs_list_reserve(list, (size_t)count);
              if (count) memcpy(list->data, data, (size_t)count * size);
              list->length = (size_t)count;
              return list;
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
