using System.Text;
using System.Text.RegularExpressions;
using TinyCs;

namespace LubGen;

/// <summary>stub から Lua binding (src/gen/lua_api_gen.c) を導く。C API
/// (CHeader が導いた形) と Lua の値の詰め替えだけを持ち、Lua 固有の土台は
/// src/lua_gen_support.h の手書き helper を呼ぶ。</summary>
public static class LuaBinding
{
    public static string Generate(ApiModel model) => new Gen(model).Run();

    private sealed class Gen(ApiModel model)
    {
        private StringBuilder sb = new();

        // 生成した helper (enum の表、trampoline、read_ / fill_ / push_ / push_list_)
        // は断片として持ち、関数と登録から辿れるものだけを出す (使わない static
        // 関数は warning になる)。
        private sealed record Piece(IReadOnlyList<string> Names, string Proto, string Body);
        private readonly List<Piece> fragments = new();

        private void Fragment(string name, string proto, Action emit) => Fragment([name], proto, emit);

        private void Fragment(IReadOnlyList<string> names, string proto, Action emit)
        {
            var saved = sb;
            sb = new StringBuilder();
            emit();
            fragments.Add(new Piece(names, proto, sb.ToString()));
            sb = saved;
        }

        private static readonly Regex HelperRef = new(
            @"\b(?:names|values|name|read|fill|push_list|push)_Lub[A-Za-z0-9_]*|\btramp_[A-Za-z0-9_]+",
            RegexOptions.Compiled);

        private HashSet<Piece> Reachable(string roots)
        {
            var byName = new Dictionary<string, Piece>(StringComparer.Ordinal);
            foreach (var f in fragments)
                foreach (var n in f.Names) byName[n] = f;
            var used = new HashSet<Piece>();
            var work = new Queue<string>();
            work.Enqueue(roots);
            while (work.Count > 0)
            {
                foreach (Match m in HelperRef.Matches(work.Dequeue()))
                    if (byName.TryGetValue(m.Value, out var f) && used.Add(f))
                        work.Enqueue(f.Body);
            }
            return used;
        }
        private readonly Dictionary<string, ApiType> types = model.Types.ToDictionary(t => t.Name);
        private readonly Dictionary<string, ApiEnum> enums =
            model.Namespaces.SelectMany(n => n.Enums.Select(e => (n.Name + "." + e.Name, e)))
                .ToDictionary(p => p.Item1, p => p.e);
        private readonly Dictionary<string, string> handleKinds = new();

        public string Run()
        {
            foreach (var t in model.Types.Where(t => t.Kind is "handle" or "keyed"))
                handleKinds[t.Name] = HandleKind(t.Name);
            EmitEnumTables();
            foreach (var t in Records()) EmitTrampolines(t);
            foreach (var t in Records()) EmitReader(t);
            foreach (var t in Records()) EmitPusher(t);
            foreach (var ns in model.Namespaces)
                foreach (var f in ns.Functions.Where(f => !f.NoC))
                    EmitFunction(ns, f);
            EmitRegister();
            // entry の on_event の引数。player (src/lua_api.c) が呼ぶ。
            if (types.ContainsKey("EventData"))
                sb.Append("void lub_lua_push_event(lua_State *L, const LubEventData *e) {\n  push_LubEventData(L, e);\n}\n");
            var roots = sb.ToString();
            var used = Reachable(roots);
            var outSb = new StringBuilder();
            outSb.Append("""
                // lub の Lua binding。cs-lib/lub_stub.cs から tools/lub-gen が生成する
                // (手で編集しない。再生成: dotnet run --project tools/lub-gen -- lua)。
                // C API (include/lub/lub_api.h) への詰め替えだけを持つ。Lua の値の
                // 読み書き、sentinel、view、callback の土台は src/lua_gen_support.h。
                #include "lua_gen_support.h"
                #include <string.h>


                """);
            foreach (var f in fragments.Where(used.Contains)) outSb.Append(f.Proto);
            outSb.Append('\n');
            foreach (var f in fragments.Where(used.Contains)) outSb.Append(f.Body);
            outSb.Append(roots);
            return outSb.ToString();
        }

        private IEnumerable<ApiType> Records() => model.Types.Where(t => t.Kind == "record");

        // ------------------------------------------------------------ names

        // sentinel の __lub_kind。TextureRef → "texture"、WorldRef3d → "world3d"。
        private static string HandleKind(string name)
        {
            var n = name.EndsWith("Ref3d", StringComparison.Ordinal) ? name[..^5] + "3d"
                : name.EndsWith("Ref", StringComparison.Ordinal) ? name[..^3]
                : name;
            return LuaNaming.Member(n);
        }

        private string Kind(TypeRef tr) => handleKinds.TryGetValue(tr.Name, out var k) ? k : LuaNaming.Member(tr.Name);

        private static string CType(string csName) => "Lub" + csName;

        private static string EnumC(string qualified) => CHeader.EnumName(qualified);

        private static string FnName(ApiNamespace ns, string luaName)
        {
            var prefix = ns.LuaPath == "lub" ? "lub" : "lub_" + ns.LuaPath["lub.".Length..].Replace('.', '_');
            return prefix + "_" + luaName;
        }

        private static string LName(ApiNamespace ns, string luaName)
        {
            var prefix = ns.LuaPath == "lub" ? "l" : "l_" + ns.LuaPath["lub.".Length..].Replace('.', '_');
            return prefix + "_" + luaName;
        }

        private static string FnTypedef(ApiNamespace ns, ApiFunction f, ApiParam p) =>
            "Lub" + ns.Name + f.Name + char.ToUpperInvariant(p.Name[0]) + p.Name[1..] + "Fn";

        private bool IsLuaString(TypeRef tr) => tr.Kind == LubTypeKind.Enum && enums[tr.Name].LuaString;

        private ApiEnum EnumOf(TypeRef tr) => enums[tr.Name];

        // ------------------------------------------------------------ enums

        private void EmitEnumTables()
        {
            foreach (var e in enums.Values.Where(e => e.LuaString))
            {
                var c = EnumC(e.Namespace + "." + e.Name);
                Fragment([$"names_{c}", $"values_{c}"], "", () =>
                {
                    sb.Append($"static const char *const names_{c}[] = {{");
                    sb.Append(string.Join(", ", e.Members.Select(m => $"\"{m.LuaName.ToLowerInvariant()}\"")));
                    sb.Append(", NULL};\n");
                    sb.Append($"static const int32_t values_{c}[] = {{");
                    sb.Append(string.Join(", ", e.Members.Select(m => m.Value.ToString())));
                    sb.Append("};\n");
                });
                Fragment($"name_{c}", "", () =>
                {
                    sb.Append($"static const char *name_{c}(int32_t v) {{\n");
                    sb.Append($"  for (int i = 0; names_{c}[i]; ++i)\n    if (values_{c}[i] == v)\n      return names_{c}[i];\n  return NULL;\n}}\n\n");
                });
            }
        }

        // ------------------------------------------------------------ types

        private IEnumerable<ApiField> AllFields(ApiType t)
        {
            if (t.Base != null && types.TryGetValue(t.Base, out var b))
                foreach (var f in AllFields(b)) yield return f;
            foreach (var f in t.Fields) yield return f;
        }

        private void EmitReader(ApiType t)
        {
            var c = CType(t.Name);
            Fragment($"read_{c}", $"static void read_{c}(lua_State *L, int idx, void *out);\n", () => EmitReaderBody(t));
        }

        private void EmitReaderBody(ApiType t)
        {
            var c = CType(t.Name);
            sb.Append($"static void read_{c}(lua_State *L, int idx, void *out_) {{\n");
            sb.Append($"  {c} *o = ({c} *)out_;\n");
            sb.Append("  idx = lua_absindex(L, idx);\n  (void)o;\n");
            if (t.Base != null)
                sb.Append($"  read_{CType(t.Base)}(L, idx, &o->base);\n");
            var funcs = t.Fields.Where(f => f.Type.Kind == LubTypeKind.Func).ToList();
            if (funcs.Count > 0)
            {
                sb.Append($"  LgenCallbacks *cb = lgen_callbacks_new(L, {funcs.Count});\n");
                sb.Append("  o->user = cb;\n  o->user_release = lgen_callbacks_free;\n");
            }
            foreach (var f in t.Fields)
            {
                var n = f.LuaName;
                var q = $"\"{n}\"";
                var tr = f.Type;
                switch (tr.Kind)
                {
                    case LubTypeKind.Int:
                        sb.Append(f.Optional ? $"  o->has_{n} = lgen_int_opt(L, idx, {q}, &o->{n});\n"
                            : $"  o->{n} = lgen_int(L, idx, {q}, 0);\n");
                        break;
                    case LubTypeKind.Double:
                        sb.Append(f.Optional ? $"  o->has_{n} = lgen_num_opt(L, idx, {q}, &o->{n});\n"
                            : $"  o->{n} = lgen_num(L, idx, {q}, 0.0f);\n");
                        break;
                    case LubTypeKind.Bool:
                        sb.Append(f.Optional ? $"  o->has_{n} = lgen_bool_opt(L, idx, {q}, &o->{n});\n"
                            : $"  o->{n} = lgen_bool(L, idx, {q}, false);\n");
                        break;
                    case LubTypeKind.Enum:
                        if (IsLuaString(tr))
                        {
                            var ec = EnumC(tr.Name);
                            var has = f.Optional ? $"&o->has_{n}" : "NULL";
                            sb.Append($"  o->{n} = lgen_enum_str(L, idx, {q}, names_{ec}, values_{ec}, \"{EnumOf(tr).Name}\", {has});\n");
                        }
                        else
                            sb.Append(f.Optional ? $"  o->has_{n} = lgen_int_opt(L, idx, {q}, &o->{n});\n"
                                : $"  o->{n} = lgen_int(L, idx, {q}, 0);\n");
                        break;
                    case LubTypeKind.String:
                        if (f.Bits)
                            sb.Append(f.Optional ? $"  o->has_{n} = lgen_bits_opt(L, idx, {q}, &o->{n});\n"
                                : $"  lgen_bits_opt(L, idx, {q}, &o->{n});\n");
                        else
                            sb.Append($"  o->{n} = lgen_str(L, idx, {q});\n");
                        break;
                    case LubTypeKind.Handle:
                        sb.Append($"  o->{n} = lgen_ref(L, idx, {q}, \"{Kind(tr)}\");\n");
                        break;
                    case LubTypeKind.Keyed:
                        sb.Append($"  o->{n} = lgen_keyed(L, idx, {q}, \"{Kind(tr)}\");\n");
                        break;
                    case LubTypeKind.View:
                        throw new InvalidOperationException($"{t.Name}.{f.Name}: Bytes field in a desc is not supported");
                    case LubTypeKind.Record:
                        sb.Append($"  if (lgen_has(L, idx, {q})) {{\n");
                        sb.Append($"    lua_getfield(L, idx, {q});\n");
                        sb.Append($"    read_{CType(tr.Name)}(L, -1, &o->{n});\n");
                        sb.Append("    lua_pop(L, 1);\n");
                        if (f.Optional) sb.Append($"    o->has_{n} = true;\n");
                        sb.Append("  }\n");
                        break;
                    case LubTypeKind.List:
                        {
                            var elem = tr.Elem!;
                            if (elem.Kind == LubTypeKind.Array)
                                sb.Append($"  o->{n} = (const float (*)[{f.ArrayLen}])lgen_float_rows(L, idx, {q}, {f.ArrayLen}, &o->{n}_count);\n");
                            else if (f.ArrayLen is int len)
                            {
                                if (elem.Kind == LubTypeKind.Double)
                                    sb.Append($"  lgen_floats_fixed(L, idx, {q}, o->{n}, {len}, &o->{n}_count);\n");
                                else if (elem.Kind == LubTypeKind.Int)
                                    sb.Append($"  lgen_ints_fixed(L, idx, {q}, o->{n}, {len}, &o->{n}_count);\n");
                                else throw new InvalidOperationException($"{t.Name}.{f.Name}: fixed array of {elem}");
                            }
                            else
                                switch (elem.Kind)
                                {
                                    case LubTypeKind.Double:
                                        sb.Append($"  o->{n} = lgen_floats(L, idx, {q}, &o->{n}_count);\n");
                                        break;
                                    case LubTypeKind.Int:
                                    case LubTypeKind.Enum:
                                        sb.Append($"  o->{n} = lgen_ints(L, idx, {q}, &o->{n}_count);\n");
                                        break;
                                    case LubTypeKind.String:
                                        sb.Append($"  o->{n} = lgen_strs(L, idx, {q}, &o->{n}_count);\n");
                                        break;
                                    case LubTypeKind.Handle:
                                        sb.Append($"  o->{n} = lgen_handles(L, idx, {q}, \"{Kind(elem)}\", &o->{n}_count);\n");
                                        break;
                                    case LubTypeKind.Record:
                                        sb.Append($"  o->{n} = (const {CType(elem.Name)} *)lgen_records(L, idx, {q}, sizeof({CType(elem.Name)}), read_{CType(elem.Name)}, &o->{n}_count);\n");
                                        break;
                                    default:
                                        throw new InvalidOperationException($"{t.Name}.{f.Name}: list of {elem}");
                                }
                            break;
                        }
                    case LubTypeKind.Array:
                        {
                            var len = f.ArrayLen!.Value;
                            var call = tr.Elem!.Kind == LubTypeKind.Double
                                ? $"lgen_floats_fixed(L, idx, {q}, o->{n}, {len}, NULL)"
                                : $"lgen_ints_fixed(L, idx, {q}, o->{n}, {len}, NULL)";
                            sb.Append(f.Optional ? $"  o->has_{n} = {call};\n" : $"  {call};\n");
                            break;
                        }
                    case LubTypeKind.Func:
                        {
                            var i = funcs.IndexOf(f);
                            sb.Append($"  o->{n} = lgen_callbacks_field(L, cb, {i}, idx, {q}) ? tramp_{c}_{n} : NULL;\n");
                            break;
                        }
                    default:
                        throw new InvalidOperationException($"{t.Name}.{f.Name}: unsupported {tr}");
                }
            }
            sb.Append("}\n\n");
        }

        private void EmitPusher(ApiType t)
        {
            var c = CType(t.Name);
            Fragment($"fill_{c}", $"static void fill_{c}(lua_State *L, const {c} *v);\n", () => EmitFillBody(t));
            Fragment($"push_{c}", $"static void push_{c}(lua_State *L, const {c} *v);\n", () =>
            {
                sb.Append($"static void push_{c}(lua_State *L, const {c} *v) {{\n");
                sb.Append($"  lua_createtable(L, 0, {AllFields(t).Count()});\n  fill_{c}(L, v);\n}}\n\n");
            });
            Fragment($"push_list_{c}", $"static void push_list_{c}(lua_State *L, const {c} *v, int32_t n);\n", () =>
            {
                sb.Append($"static void push_list_{c}(lua_State *L, const {c} *v, int32_t n) {{\n");
                sb.Append("  lua_createtable(L, n, 0);\n  for (int32_t i = 0; i < n; ++i) {\n");
                sb.Append($"    push_{c}(L, &v[i]);\n    lua_rawseti(L, -2, i + 1);\n  }}\n}}\n\n");
            });
        }

        private void EmitFillBody(ApiType t)
        {
            var c = CType(t.Name);
            sb.Append($"static void fill_{c}(lua_State *L, const {c} *v) {{\n");
            if (t.Base != null)
                sb.Append($"  fill_{CType(t.Base)}(L, &v->base);\n");
            foreach (var f in t.Fields)
            {
                var n = f.LuaName;
                var q = $"\"{n}\"";
                var tr = f.Type;
                var guard = f.Optional ? $"  if (v->has_{n})\n  " : "  ";
                switch (tr.Kind)
                {
                    case LubTypeKind.Int:
                        sb.Append(guard).Append($"lgen_set_int(L, {q}, v->{n});\n");
                        break;
                    case LubTypeKind.Double:
                        sb.Append(guard).Append($"lgen_set_num(L, {q}, v->{n});\n");
                        break;
                    case LubTypeKind.Bool:
                        sb.Append(guard).Append($"lgen_set_bool(L, {q}, v->{n});\n");
                        break;
                    case LubTypeKind.Enum:
                        if (IsLuaString(tr))
                        {
                            var ec = EnumC(tr.Name);
                            sb.Append(f.Optional ? $"  if (v->has_{n} && name_{ec}(v->{n})) {{\n" : $"  if (name_{ec}(v->{n})) {{\n");
                            sb.Append($"    lua_pushstring(L, name_{ec}(v->{n}));\n    lua_setfield(L, -2, {q});\n  }}\n");
                        }
                        else
                            sb.Append(guard).Append($"lgen_set_int(L, {q}, v->{n});\n");
                        break;
                    case LubTypeKind.String:
                        if (f.Bits)
                            sb.Append(guard).Append($"lgen_set_bits(L, {q}, v->{n});\n");
                        else if (f.Optional)
                            sb.Append($"  if (v->{n}.len > 0)\n    lgen_set_str(L, {q}, v->{n});\n");
                        else
                            sb.Append($"  lgen_set_str(L, {q}, v->{n});\n");
                        break;
                    case LubTypeKind.Handle:
                        sb.Append($"  if (v->{n}) {{\n    lgen_push_ref(L, \"{Kind(tr)}\", v->{n});\n    lua_setfield(L, -2, {q});\n  }}\n");
                        break;
                    case LubTypeKind.Keyed:
                        sb.Append($"  if (v->{n}.len > 0) {{\n    lgen_push_keyed(L, \"{Kind(tr)}\", v->{n});\n    lua_setfield(L, -2, {q});\n  }}\n");
                        break;
                    case LubTypeKind.View:
                        sb.Append($"  if (v->{n}.ptr) {{\n    lgen_push_bytes_view(L, v->{n});\n    lua_setfield(L, -2, {q});\n  }}\n");
                        break;
                    case LubTypeKind.Record:
                        sb.Append(f.Optional ? $"  if (v->has_{n}) {{\n" : "  {\n");
                        sb.Append($"    push_{CType(tr.Name)}(L, &v->{n});\n    lua_setfield(L, -2, {q});\n  }}\n");
                        break;
                    case LubTypeKind.List:
                        {
                            var elem = tr.Elem!;
                            string push;
                            if (elem.Kind == LubTypeKind.Array)
                                push = $"lgen_push_float_table(L, (const float *)v->{n}, v->{n}_count * {f.ArrayLen})";
                            else if (f.ArrayLen is int)
                                push = elem.Kind == LubTypeKind.Double
                                    ? $"lgen_push_float_table(L, v->{n}, v->{n}_count)"
                                    : $"lgen_push_int_table(L, v->{n}, v->{n}_count)";
                            else
                                push = elem.Kind switch
                                {
                                    LubTypeKind.Double => $"lgen_push_float_view(L, v->{n}, v->{n}_count)",
                                    LubTypeKind.Int or LubTypeKind.Enum => $"lgen_push_int_view(L, v->{n}, v->{n}_count)",
                                    LubTypeKind.String => $"lgen_push_str_table(L, v->{n}, v->{n}_count)",
                                    LubTypeKind.Handle => $"lgen_push_handle_table(L, \"{Kind(elem)}\", v->{n}, v->{n}_count)",
                                    LubTypeKind.Record => $"push_list_{CType(elem.Name)}(L, v->{n}, v->{n}_count)",
                                    _ => throw new InvalidOperationException($"{t.Name}.{f.Name}: list of {elem}"),
                                };
                            var cond = f.ArrayLen is int || elem.Kind == LubTypeKind.Array ? "" : $"  if (v->{n})\n  ";
                            sb.Append(cond.Length > 0 ? cond : "  ").Append($"{{\n    {push};\n    lua_setfield(L, -2, {q});\n  }}\n");
                            break;
                        }
                    case LubTypeKind.Array:
                        {
                            var push = tr.Elem!.Kind == LubTypeKind.Double
                                ? $"lgen_push_float_table(L, v->{n}, {f.ArrayLen})"
                                : $"lgen_push_int_table(L, v->{n}, {f.ArrayLen})";
                            sb.Append(f.Optional ? $"  if (v->has_{n}) {{\n" : "  {\n");
                            sb.Append($"    {push};\n    lua_setfield(L, -2, {q});\n  }}\n");
                            break;
                        }
                    case LubTypeKind.Func:
                        break; // callback は Lua に戻さない
                    default:
                        throw new InvalidOperationException($"{t.Name}.{f.Name}: unsupported {tr}");
                }
            }
            sb.Append("  (void)L;\n  (void)v;\n");
            sb.Append("}\n\n");
        }

        // -------------------------------------------------------- callbacks

        private void EmitTrampolines(ApiType t)
        {
            var c = CType(t.Name);
            var funcs = t.Fields.Where(f => f.Type.Kind == LubTypeKind.Func).ToList();
            for (var i = 0; i < funcs.Count; i++)
                EmitTrampoline($"tramp_{c}_{funcs[i].LuaName}", funcs[i].Type, i, funcs[i].LuaName);
        }

        private void EmitTrampoline(string name, TypeRef fn, int slot, string what) =>
            Fragment(name, "", () => EmitTrampolineBody(name, fn, slot, what));

        private void EmitTrampolineBody(string name, TypeRef fn, int slot, string what)
        {
            var ret = fn.FuncReturn!;
            var retC = ret.Kind switch
            {
                LubTypeKind.Bool => "bool",
                LubTypeKind.Double => "float",
                LubTypeKind.Int => "int32_t",
                LubTypeKind.Void => "void",
                _ => throw new InvalidOperationException($"callback {what}: return {ret}"),
            };
            var ps = fn.FuncParams!;
            var args = new List<string> { "void *user" };
            for (var i = 0; i < ps.Count; i++)
            {
                var p = ps[i];
                var pn = (char)('a' + i);
                args.Add(p.Kind switch
                {
                    LubTypeKind.Record => $"const {CType(p.Name)} *{pn}",
                    LubTypeKind.Int or LubTypeKind.Enum => $"int32_t {pn}",
                    LubTypeKind.Double => $"float {pn}",
                    LubTypeKind.Bool => $"bool {pn}",
                    _ => throw new InvalidOperationException($"callback {what}: param {p}"),
                });
            }
            // 既定値: 判定は「続行 / 衝突する」、数値は 0 (runtime が既定に読み替える)。
            var def = ret.Kind switch
            {
                LubTypeKind.Bool => "true",
                LubTypeKind.Double => "1.0f",
                LubTypeKind.Int => "1",
                _ => "",
            };
            sb.Append($"static {retC} {name}({string.Join(", ", args)}) {{\n");
            sb.Append("  LgenCallbacks *cb = (LgenCallbacks *)user;\n  lua_State *L = cb->L;\n");
            sb.Append($"  if (!lgen_callbacks_push(cb, {slot}))\n    return{(def.Length > 0 ? " " + def : "")};\n");
            for (var i = 0; i < ps.Count; i++)
            {
                var p = ps[i];
                var pn = (char)('a' + i);
                sb.Append(p.Kind switch
                {
                    LubTypeKind.Record => $"  push_{CType(p.Name)}(L, {pn});\n",
                    LubTypeKind.Int or LubTypeKind.Enum => $"  lua_pushinteger(L, {pn});\n",
                    LubTypeKind.Double => $"  lua_pushnumber(L, {pn});\n",
                    LubTypeKind.Bool => $"  lua_pushboolean(L, {pn});\n",
                    _ => throw new InvalidOperationException(),
                });
            }
            var nres = ret.Kind == LubTypeKind.Void ? 0 : 1;
            sb.Append($"  if (!lgen_callbacks_call(cb, {slot}, {ps.Count}, {nres}))\n    return{(def.Length > 0 ? " " + def : "")};\n");
            switch (ret.Kind)
            {
                case LubTypeKind.Bool:
                    sb.Append("  bool r = lua_toboolean(L, -1);\n  lua_pop(L, 1);\n  return r;\n");
                    break;
                case LubTypeKind.Double:
                    sb.Append("  float r = (float)lua_tonumber(L, -1);\n  lua_pop(L, 1);\n  return r;\n");
                    break;
                case LubTypeKind.Int:
                    sb.Append("  int32_t r = (int32_t)lua_tointeger(L, -1);\n  lua_pop(L, 1);\n  return r;\n");
                    break;
            }
            sb.Append("}\n\n");
        }

        // -------------------------------------------------------- functions

        private void EmitFunction(ApiNamespace ns, ApiFunction f)
        {
            foreach (var p in f.Params.Where(p => p.Type.Kind == LubTypeKind.Func))
                EmitTrampoline($"tramp_{LName(ns, f.LuaName)}_{p.LuaName}", p.Type, 0, $"{ns.Name}.{f.Name}.{p.Name}");

            sb.Append($"static int {LName(ns, f.LuaName)}(lua_State *L) {{\n");
            sb.Append("  (void)L;\n  LgenMark mark = lgen_mark();\n");
            var call = new List<string> { "lgen_ctx()" };
            var post = new StringBuilder(); // 呼び出し後の後始末
            var idx = 0;
            foreach (var p in f.Params.Where(p => !p.IsOut))
            {
                idx++;
                var n = p.LuaName;
                var tr = p.Type;
                var opt = tr.Nullable || p.Optional;
                switch (tr.Kind)
                {
                    case LubTypeKind.Int:
                    case LubTypeKind.Double:
                    case LubTypeKind.Bool:
                    case LubTypeKind.Enum:
                        {
                            var ct = tr.Kind == LubTypeKind.Double ? "float" : tr.Kind == LubTypeKind.Bool ? "bool" : "int32_t";
                            string read = tr.Kind switch
                            {
                                LubTypeKind.Double => $"(float)luaL_checknumber(L, {idx})",
                                LubTypeKind.Bool => $"lua_toboolean(L, {idx})",
                                LubTypeKind.Enum when IsLuaString(tr) =>
                                    $"lgen_enum_str_arg(L, {idx}, names_{EnumC(tr.Name)}, values_{EnumC(tr.Name)}, \"{EnumOf(tr).Name}\")",
                                _ => $"(int32_t)luaL_checkinteger(L, {idx})",
                            };
                            if (opt)
                            {
                                sb.Append($"  {ct} {n}_v = 0;\n  const {ct} *{n} = NULL;\n");
                                sb.Append($"  if (!lua_isnoneornil(L, {idx})) {{\n    {n}_v = {read};\n    {n} = &{n}_v;\n  }}\n");
                            }
                            else
                                sb.Append($"  {ct} {n} = {read};\n");
                            call.Add(n);
                            break;
                        }
                    case LubTypeKind.String:
                        sb.Append(opt ? $"  LubStr {n} = lgen_str_opt(L, {idx});\n" : $"  LubStr {n} = lgen_str_arg(L, {idx});\n");
                        call.Add(n);
                        break;
                    case LubTypeKind.Handle:
                        sb.Append($"  LubHandle {n} = lgen_ref_arg(L, {idx}, \"{Kind(tr)}\", {(opt ? "false" : "true")});\n");
                        call.Add(n);
                        break;
                    case LubTypeKind.Keyed:
                        sb.Append($"  LubStr {n} = lgen_keyed_arg(L, {idx}, \"{Kind(tr)}\");\n");
                        call.Add(n);
                        break;
                    case LubTypeKind.View:
                        sb.Append($"  int32_t {n}_len = 0;\n  const uint8_t *{n} = lgen_bytes_arg(L, {idx}, &{n}_len, {(opt ? "false" : "true")});\n");
                        call.Add(n);
                        call.Add($"{n}_len");
                        break;
                    case LubTypeKind.Record:
                        {
                            var c = CType(tr.Name);
                            sb.Append($"  {c} {n}_v;\n  memset(&{n}_v, 0, sizeof {n}_v);\n  const {c} *{n} = NULL;\n");
                            if (opt)
                                sb.Append($"  if (!lua_isnoneornil(L, {idx})) {{\n    luaL_checktype(L, {idx}, LUA_TTABLE);\n    read_{c}(L, {idx}, &{n}_v);\n    {n} = &{n}_v;\n  }}\n");
                            else
                                sb.Append($"  luaL_checktype(L, {idx}, LUA_TTABLE);\n  read_{c}(L, {idx}, &{n}_v);\n  {n} = &{n}_v;\n");
                            call.Add(n);
                            break;
                        }
                    case LubTypeKind.List:
                        {
                            var elem = tr.Elem!;
                            var req = opt ? "false" : "true";
                            sb.Append($"  int32_t {n}_count = 0;\n");
                            switch (elem.Kind)
                            {
                                case LubTypeKind.Double:
                                    sb.Append($"  const float *{n} = lgen_floats_arg(L, {idx}, &{n}_count, {req});\n");
                                    break;
                                case LubTypeKind.Int:
                                case LubTypeKind.Enum:
                                    sb.Append($"  const int32_t *{n} = lgen_ints_arg(L, {idx}, &{n}_count, {req});\n");
                                    break;
                                case LubTypeKind.Record:
                                    sb.Append($"  const {CType(elem.Name)} *{n} = (const {CType(elem.Name)} *)lgen_records_arg(L, {idx}, sizeof({CType(elem.Name)}), read_{CType(elem.Name)}, &{n}_count, {req});\n");
                                    break;
                                default:
                                    throw new InvalidOperationException($"{ns.Name}.{f.Name}: list param of {elem}");
                            }
                            call.Add(n);
                            call.Add($"{n}_count");
                            break;
                        }
                    case LubTypeKind.Func:
                        sb.Append($"  LgenCallbacks *{n}_cb = lgen_callbacks_arg(L, {idx});\n");
                        call.Add($"{n}_cb ? tramp_{LName(ns, f.LuaName)}_{n} : NULL");
                        call.Add($"{n}_cb");
                        // visitor の error は nil, "<fn> visitor: msg" で返す
                        post.Append($"  if (lgen_callbacks_error({n}_cb)) {{\n");
                        post.Append($"    lua_pushnil(L);\n    lua_pushfstring(L, \"{FnName(ns, f.LuaName)[4..]} visitor: %s\", lgen_callbacks_error({n}_cb));\n");
                        post.Append($"    lgen_callbacks_free({n}_cb);\n    lgen_release(mark);\n    return 2;\n  }}\n");
                        post.Append($"  lgen_callbacks_free({n}_cb);\n");
                        break;
                    case LubTypeKind.Dict:
                        sb.Append($"  int32_t {n}_count = 0;\n  const LubBinding *{n} = lgen_bindings_arg(L, {idx}, &{n}_count);\n");
                        call.Add(n);
                        call.Add($"{n}_count");
                        break;
                    default:
                        throw new InvalidOperationException($"{ns.Name}.{f.Name}: unsupported param {p.Name}: {tr}");
                }
            }
            // out 引数と戻り値
            var outs = new List<(string name, TypeRef type, bool hasFlag)>();
            foreach (var p in f.Params.Where(p => p.IsOut))
            {
                DeclareOut(p.LuaName, p.Type, call);
                var hasFlag = p.Type.Kind == LubTypeKind.Record && p.Type.Nullable;
                if (hasFlag)
                {
                    sb.Append($"  bool has_{p.LuaName} = false;\n");
                    call.Add($"&has_{p.LuaName}");
                }
                outs.Add((p.LuaName, p.Type, hasFlag));
            }
            var r = f.Return;
            var direct = f.NoFail && r.Kind is LubTypeKind.Void or LubTypeKind.Int or LubTypeKind.Double or LubTypeKind.Bool or LubTypeKind.Enum or LubTypeKind.Handle;
            var retHas = false;
            if (!direct && r.Kind != LubTypeKind.Void)
            {
                DeclareOut("out", r, call);
                retHas = !f.NoFail && r.Nullable && (r.IsScalar || (r.Kind == LubTypeKind.Record && f.Maybe));
                if (retHas)
                {
                    sb.Append("  bool has = false;\n");
                    call.Add("&has");
                }
            }
            var callExpr = $"{FnName(ns, f.LuaName)}({string.Join(", ", call)})";
            if (direct)
            {
                if (r.Kind == LubTypeKind.Void) sb.Append($"  {callExpr};\n");
                else sb.Append($"  {ReturnCType(r)} out = {callExpr};\n");
                sb.Append(post);
                sb.Append("  lgen_release(mark);\n");
            }
            else if (f.NoFail)
            {
                sb.Append($"  {callExpr};\n");
                sb.Append(post);
                sb.Append("  lgen_release(mark);\n");
            }
            else
            {
                sb.Append($"  LubStatus st = {callExpr};\n");
                sb.Append(post);
                sb.Append("  lgen_release(mark);\n");
                sb.Append("  if (st == LUB_ERROR)\n    return lgen_raise(L);\n");
                var nret = (r.Kind == LubTypeKind.Void ? 0 : 1) + outs.Count;
                if (nret == 1)
                {
                    // 対象が無い: nil, "not found" (従来の Lua 面の契約)
                    sb.Append("  if (st == LUB_NOT_FOUND) {\n    lua_pushnil(L);\n    lua_pushstring(L, \"not found\");\n    return 2;\n  }\n");
                }
                else if (nret > 0)
                {
                    sb.Append("  if (st == LUB_NOT_FOUND) {\n");
                    for (var i = 0; i < nret; i++) sb.Append("    lua_pushnil(L);\n");
                    sb.Append($"    return {nret};\n  }}\n");
                }
            }
            var results = 0;
            if (r.Kind != LubTypeKind.Void)
            {
                // key で宣言する resource の handle は sentinel に key 列を持たせる
                var keyParam = f.Params.FirstOrDefault(p => !p.IsOut && p.Type.Kind == LubTypeKind.String && p.LuaName == "key");
                var parent = f.Params.Where(p => !p.IsOut).Select((p, i) => (p, i)).FirstOrDefault(x => x.p.Type.Kind == LubTypeKind.Handle);
                if (r.Kind == LubTypeKind.Handle && keyParam != null)
                {
                    var parentIdx = parent.p != null ? parent.i + 1 : 0;
                    sb.Append($"  if (out == 0)\n    lua_pushnil(L);\n  else\n    lgen_push_ref_keyed(L, \"{Kind(r)}\", out, {parentIdx}, key);\n");
                }
                else if (direct) PushValue("out", r, false);
                else PushValue("out", r, retHas ? "has" : null);
                results++;
            }
            foreach (var (name, type, hasFlag) in outs)
            {
                PushValue(name, type, hasFlag ? $"has_{name}" : null);
                results++;
            }
            sb.Append($"  return {results};\n}}\n\n");
        }

        private void DeclareOut(string n, TypeRef tr, List<string> call)
        {
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Enum:
                    sb.Append($"  int32_t {n} = 0;\n");
                    call.Add($"&{n}");
                    break;
                case LubTypeKind.Double:
                    sb.Append($"  float {n} = 0;\n");
                    call.Add($"&{n}");
                    break;
                case LubTypeKind.Bool:
                    sb.Append($"  bool {n} = false;\n");
                    call.Add($"&{n}");
                    break;
                case LubTypeKind.String:
                    sb.Append($"  LubStr {n} = {{NULL, 0}};\n");
                    call.Add($"&{n}");
                    break;
                case LubTypeKind.Handle:
                    sb.Append($"  LubHandle {n} = 0;\n");
                    call.Add($"&{n}");
                    break;
                case LubTypeKind.View:
                    sb.Append($"  LubView {n} = {{NULL, 0, 0}};\n");
                    call.Add($"&{n}");
                    break;
                case LubTypeKind.Record:
                    sb.Append($"  {CType(tr.Name)} {n};\n  memset(&{n}, 0, sizeof {n});\n");
                    call.Add($"&{n}");
                    break;
                case LubTypeKind.List:
                    {
                        var elem = tr.Elem!;
                        var ct = elem.Kind switch
                        {
                            LubTypeKind.Double => "float",
                            LubTypeKind.Int or LubTypeKind.Enum => "int32_t",
                            LubTypeKind.String => "LubStr",
                            LubTypeKind.Handle => "LubHandle",
                            LubTypeKind.Record => CType(elem.Name),
                            _ => throw new InvalidOperationException($"out list of {elem}"),
                        };
                        sb.Append($"  const {ct} *{n} = NULL;\n  int32_t {n}_count = 0;\n");
                        call.Add($"&{n}");
                        call.Add($"&{n}_count");
                        break;
                    }
                default:
                    throw new InvalidOperationException($"unsupported out / return {tr}");
            }
        }

        private static string ReturnCType(TypeRef tr) => tr.Kind switch
        {
            LubTypeKind.Int or LubTypeKind.Enum => "int32_t",
            LubTypeKind.Double => "float",
            LubTypeKind.Bool => "bool",
            LubTypeKind.Handle => "LubHandle",
            _ => throw new InvalidOperationException($"direct return {tr}"),
        };

        private void PushValue(string n, TypeRef tr, object? has)
        {
            var guard = has is string h ? $"  if (!{h})\n    lua_pushnil(L);\n  else\n  " : "  ";
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                    sb.Append(guard).Append($"lua_pushinteger(L, {n});\n");
                    break;
                case LubTypeKind.Enum:
                    if (IsLuaString(tr))
                    {
                        var ec = EnumC(tr.Name);
                        sb.Append(guard).Append($"lua_pushstring(L, name_{ec}({n}) ? name_{ec}({n}) : \"\");\n");
                    }
                    else
                        sb.Append(guard).Append($"lua_pushinteger(L, {n});\n");
                    break;
                case LubTypeKind.Double:
                    sb.Append(guard).Append($"lua_pushnumber(L, {n});\n");
                    break;
                case LubTypeKind.Bool:
                    sb.Append(guard).Append($"lua_pushboolean(L, {n});\n");
                    break;
                case LubTypeKind.String:
                    if (tr.Nullable)
                        sb.Append($"  if ({n}.len == 0 && !{n}.ptr)\n    lua_pushnil(L);\n  else\n    lgen_push_str(L, {n});\n");
                    else
                        sb.Append(guard).Append($"lgen_push_str(L, {n});\n");
                    break;
                case LubTypeKind.Handle:
                    sb.Append($"  if ({n} == 0)\n    lua_pushnil(L);\n  else\n    lgen_push_ref(L, \"{Kind(tr)}\", {n});\n");
                    break;
                case LubTypeKind.View:
                    sb.Append($"  if (!{n}.ptr)\n    lua_pushnil(L);\n  else\n    lgen_push_bytes_view(L, {n});\n");
                    break;
                case LubTypeKind.Record:
                    sb.Append(guard).Append($"push_{CType(tr.Name)}(L, &{n});\n");
                    break;
                case LubTypeKind.List:
                    {
                        var elem = tr.Elem!;
                        var push = elem.Kind switch
                        {
                            LubTypeKind.Double => $"lgen_push_float_view(L, {n}, {n}_count)",
                            LubTypeKind.Int or LubTypeKind.Enum => $"lgen_push_int_view(L, {n}, {n}_count)",
                            LubTypeKind.String => $"lgen_push_str_table(L, {n}, {n}_count)",
                            LubTypeKind.Handle => $"lgen_push_handle_table(L, \"{Kind(elem)}\", {n}, {n}_count)",
                            LubTypeKind.Record => $"push_list_{CType(elem.Name)}(L, {n}, {n}_count)",
                            _ => throw new InvalidOperationException(),
                        };
                        if (tr.Nullable)
                            sb.Append($"  if (!{n})\n    lua_pushnil(L);\n  else\n    {push};\n");
                        else
                            sb.Append(guard).Append($"{push};\n");
                        break;
                    }
                default:
                    throw new InvalidOperationException($"push {tr}");
            }
        }

        // --------------------------------------------------------- register

        private void EmitRegister()
        {
            sb.Append("void lub_api_gen_register(lua_State *L) {\n");
            sb.Append("  lua_newtable(L); // lub\n");
            foreach (var ns in model.Namespaces)
            {
                var isRoot = ns.LuaPath == "lub";
                if (!isRoot) sb.Append($"  lua_newtable(L); // lub.{ns.LuaPath["lub.".Length..]}\n");
                foreach (var f in ns.Functions.Where(f => !f.NoC))
                    sb.Append($"  lua_pushcfunction(L, {LName(ns, f.LuaName)});\n  lua_setfield(L, -2, \"{f.LuaName}\");\n");
                foreach (var e in ns.Enums)
                    foreach (var m in e.Members)
                    {
                        if (e.LuaString) sb.Append($"  lua_pushstring(L, \"{m.LuaName.ToLowerInvariant()}\");\n");
                        else sb.Append($"  lua_pushinteger(L, {m.Value});\n");
                        sb.Append($"  lua_setfield(L, -2, \"{m.LuaName}\");\n");
                    }
                foreach (var c in ns.Consts)
                {
                    if (c.Value is int iv) sb.Append($"  lua_pushinteger(L, {iv});\n");
                    else if (c.Value is string sv) sb.Append($"  lua_pushstring(L, \"{sv}\");\n");
                    else continue;
                    sb.Append($"  lua_setfield(L, -2, \"{c.LuaName}\");\n");
                }
                foreach (var f in ns.StaticFields)
                {
                    if (f.Type.Kind == LubTypeKind.Handle)
                        sb.Append($"  lgen_push_ref(L, \"{Kind(f.Type)}\", {FnName(ns, f.LuaName)}(lgen_ctx()));\n  lua_setfield(L, -2, \"{f.LuaName}\");\n");
                }
                if (!isRoot) sb.Append($"  lua_setfield(L, -2, \"{ns.LuaPath["lub.".Length..]}\");\n");
            }
            EmitRefMethods();
            sb.Append("  lua_setglobal(L, \"lub\");\n}\n");
        }

        // handle ごとの method table (world:step(...) の形)。第 1 引数がその
        // handle の関数を、kind の接頭辞 (body_ / shape_ / joint_ / chain_ /
        // world_) を落とした名前で持つ。metatable "lub.ref.<kind>" の __index
        // に置き、lgen_push_ref が sentinel に付ける。lub.__refs.<kind> からも
        // 引ける (prelude が互換の method を足すため)。
        private void EmitRefMethods()
        {
            var kinds = model.Types.Where(t => t.Kind == "handle").Select(t => t.Name).ToList();
            sb.Append("  lua_newtable(L); // lub.__refs\n");
            foreach (var typeName in kinds)
            {
                var kind = handleKinds[typeName];
                var baseKind = kind.EndsWith("3d", StringComparison.Ordinal) ? kind[..^2] : kind;
                var entries = new List<(string method, string cfn)>();
                foreach (var ns in model.Namespaces)
                    foreach (var f in ns.Functions.Where(f => !f.NoC))
                    {
                        var first = f.Params.FirstOrDefault(p => !p.IsOut);
                        if (first == null || first.Type.Kind != LubTypeKind.Handle || first.Type.Name != typeName)
                            continue;
                        var name = f.LuaName;
                        if (name.StartsWith(baseKind + "_", StringComparison.Ordinal) && name.Length > baseKind.Length + 1)
                            name = name[(baseKind.Length + 1)..];
                        if (entries.Any(e => e.method == name)) continue;
                        entries.Add((name, LName(ns, f.LuaName)));
                    }
                if (entries.Count == 0) continue;
                sb.Append($"  luaL_newmetatable(L, \"lub.ref.{kind}\");\n");
                sb.Append($"  lua_createtable(L, 0, {entries.Count}); // methods\n");
                foreach (var (method, cfn) in entries)
                    sb.Append($"  lua_pushcfunction(L, {cfn});\n  lua_setfield(L, -2, \"{method}\");\n");
                sb.Append("  lua_pushvalue(L, -1);\n");
                sb.Append($"  lua_setfield(L, -4, \"{kind}\"); // lub.__refs.{kind}\n");
                sb.Append("  lua_setfield(L, -2, \"__index\");\n  lua_pop(L, 1);\n");
            }
            sb.Append("  lua_setfield(L, -2, \"__refs\");\n");
        }
    }
}
