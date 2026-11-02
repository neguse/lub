using System.Collections.Generic;
using TcsSelf;
using TinySystem.Text;

namespace LubGen;

/// <summary>stub から Lua binding (src/gen/lua_api_gen.c) を導く。
/// C API (CHeader が導いた形) と Lua の値の詰め替えだけを持ち、Lua 固有の土台は src/lua_gen_support.h の手書き helper を呼ぶ。
/// </summary>
public static class LuaBinding
{
    public static string Generate(ApiModel model) => new LuaBindingGen(model).Run();
}

// 生成した helper の断片 (名前、前方宣言、本体)
internal sealed class LuaPiece
{
    public List<string> Names;
    public string Proto;
    public string Body;

    public LuaPiece(List<string> names, string proto, string body)
    {
        Names = names;
        Proto = proto;
        Body = body;
    }
}

internal sealed class LuaBindingGen
{
    private ApiModel model;
    private Out sb = new Out();

    // 生成した helper (enum の表、trampoline、read_ / fill_ / push_ / push_list_) は断片として持ち、関数と登録から辿れるものだけを出す (使わない static 関数は warning になる)。
    private List<LuaPiece> fragments = new List<LuaPiece>();
    private Out? savedSb;

    private Dictionary<string, ApiType> types = new Dictionary<string, ApiType>();
    private Dictionary<string, ApiEnum> enums = new Dictionary<string, ApiEnum>();
    // enums の挿入の順
    private List<ApiEnum> enumOrder = new List<ApiEnum>();
    private Dictionary<string, string> handleKinds = new Dictionary<string, string>();

    public LuaBindingGen(ApiModel model)
    {
        this.model = model;
        foreach (var t in model.Types) types[t.Name] = t;
        foreach (var n in model.Namespaces)
        {
            foreach (var e in n.Enums)
            {
                enums[n.Name + "." + e.Name] = e;
                enumOrder.Add(e);
            }
        }
    }

    // 断片の出力を始める (sb を断片の出力に切り替える)
    private void BeginFragment()
    {
        savedSb = sb;
        sb = new Out();
    }

    private void EndFragment(List<string> names, string proto)
    {
        fragments.Add(new LuaPiece(names, proto, sb.ToString()));
        sb = savedSb!;
        savedSb = null;
    }

    private static List<string> One(string name)
    {
        var r = new List<string>();
        r.Add(name);
        return r;
    }

    private static bool IsWordByte(int c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c >= 0x80;

    private static bool StartsAt(Utf8Bytes u, int at, string prefix)
    {
        var p = new Utf8Bytes(prefix);
        if (at + p.Length > u.Length) return false;
        for (int k = 0; k < p.Length; k++)
        {
            if (u[at + k] != p[k]) return false;
        }
        return true;
    }

    private static string[] HelperPrefixes = "names values name read fill push_list push".Split(' ');

    // C# 版の正規表現 \b(?:names|values|name|read|fill|push_list|push)_Lub[A-Za-z0-9_]*|\btramp_[A-Za-z0-9_]+ の一致を順に集める
    private static List<string> HelperRefs(string text)
    {
        var r = new List<string>();
        var u = new Utf8Bytes(text);
        int i = 0;
        while (i < u.Length)
        {
            if (i > 0 && IsWordByte(u[i - 1]))
            {
                i++;
                continue;
            }
            int end = -1;
            foreach (var p in HelperPrefixes)
            {
                if (StartsAt(u, i, p + "_Lub"))
                {
                    end = i + Text.Len(p + "_Lub");
                    break;
                }
            }
            if (end < 0 && StartsAt(u, i, "tramp_"))
            {
                int k = i + Text.Len("tramp_");
                if (k < u.Length && IsAsciiWord(u[k])) end = k;
            }
            if (end < 0)
            {
                i++;
                continue;
            }
            while (end < u.Length && IsAsciiWord(u[end])) end++;
            r.Add(u.Slice(i, end - i).ToString());
            i = end;
        }
        return r;
    }

    private static bool IsAsciiWord(int c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    private HashSet<LuaPiece> Reachable(string roots)
    {
        var byName = new Dictionary<string, LuaPiece>();
        foreach (var f in fragments)
        {
            foreach (var n in f.Names) byName[n] = f;
        }
        var used = new HashSet<LuaPiece>();
        var work = new List<string>();
        work.Add(roots);
        int at = 0;
        while (at < work.Count)
        {
            foreach (var m in HelperRefs(work[at]))
            {
                LuaPiece? f;
                if (byName.TryGetValue(m, out f) && used.Add(f!)) work.Add(f!.Body);
            }
            at++;
        }
        return used;
    }

    public string Run()
    {
        foreach (var t in model.Types)
        {
            if (t.Kind == "handle" || t.Kind == "keyed") handleKinds[t.Name] = HandleKind(t.Name);
        }
        EmitEnumTables();
        foreach (var t in Records()) EmitTrampolines(t);
        foreach (var t in Records()) EmitReader(t);
        foreach (var t in Records()) EmitPusher(t);
        foreach (var ns in model.Namespaces)
        {
            foreach (var f in ns.Functions)
            {
                if (!f.NoC) EmitFunction(ns, f);
            }
        }
        EmitRegister();
        // entry の OnEvent の引数。
        // player (src/lua_api.c) が呼ぶ。
        if (types.ContainsKey("EventData"))
            sb.Write("void lub_lua_push_event(lua_State *L, const LubEventData *e) {\n  push_LubEventData(L, e);\n}\n");
        var roots = sb.ToString();
        var used = Reachable(roots);
        var outSb = new Out();
        outSb.Write("""
            // lub の Lua binding。
            // cs-lib/lub_stub.cs から tools/lub-gen が生成する (手で編集しない。再生成: dotnet run --project tools/lub-gen -- lua)。
            // C API (include/lub/lub_api.h) への詰め替えだけを持つ。
            // Lua の値の読み書き、sentinel、view、callback の土台は src/lua_gen_support.h。
            #include "lua_gen_support.h"
            #include <string.h>


            """);
        foreach (var f in fragments)
        {
            if (used.Contains(f)) outSb.Write(f.Proto);
        }
        outSb.Write("\n");
        foreach (var f in fragments)
        {
            if (used.Contains(f)) outSb.Write(f.Body);
        }
        outSb.Write(roots);
        return outSb.ToString();
    }

    private List<ApiType> Records()
    {
        var r = new List<ApiType>();
        foreach (var t in model.Types)
        {
            if (t.Kind == "record") r.Add(t);
        }
        return r;
    }

    // ------------------------------------------------------------ names

    // sentinel の __lub_kind。
    // TextureRef → "texture"、WorldRef3d → "world3d"。
    private static string HandleKind(string name)
    {
        string n = name.EndsWith("Ref3d") ? Text.DropEnd(name, 5) + "3d"
            : name.EndsWith("Ref") ? Text.DropEnd(name, 3)
            : name;
        return LuaNames.HostMember(n);
    }

    private string Kind(TypeRef tr)
    {
        string k;
        if (handleKinds.TryGetValue(tr.Name, out k)) return k;
        return LuaNames.HostMember(tr.Name);
    }

    private static string CType(string csName) => "Lub" + csName;

    private static string EnumC(string qualified) => CHeader.EnumName(qualified);

    private static string FnName(ApiNamespace ns, string luaName) => CHeader.FunctionName(ns, luaName);

    // lub.gfx.begin_pass → l_gfx_begin_pass、lubx.vorbis.decode → l_lubx_vorbis_decode
    private static string LName(ApiNamespace ns, string luaName)
    {
        string path = ns.LuaRoot == "lub" ? Text.From(ns.LuaPath, Text.Len("lub")) : "_" + ns.LuaPath;
        return "l" + path.Replace(".", "_") + "_" + luaName;
    }

    private bool IsLuaString(TypeRef tr) => tr.Kind == LubTypeKind.Enum && enums[tr.Name].LuaString;

    private ApiEnum EnumOf(TypeRef tr) => enums[tr.Name];

    private static string Letter(int i) => Text.Sub("abcdefghijklmnopqrstuvwxyz", i, 1);

    // ------------------------------------------------------------ enums

    private void EmitEnumTables()
    {
        foreach (var e in enumOrder)
        {
            if (!e.LuaString) continue;
            var c = EnumC(e.Namespace + "." + e.Name);
            BeginFragment();
            sb.Write("static const char *const names_" + c + "[] = {");
            var names = new List<string>();
            foreach (var m in e.Members) names.Add("\"" + m.LuaName.ToLower() + "\"");
            sb.Write(string.Join(", ", names));
            sb.Write(", NULL};\n");
            sb.Write("static const int32_t values_" + c + "[] = {");
            var values = new List<string>();
            foreach (var m in e.Members) values.Add(m.Value.ToString());
            sb.Write(string.Join(", ", values));
            sb.Write("};\n");
            var both = new List<string>();
            both.Add("names_" + c);
            both.Add("values_" + c);
            EndFragment(both, "");
            BeginFragment();
            sb.Write("static const char *name_" + c + "(int32_t v) {\n");
            sb.Write("  for (int i = 0; names_" + c + "[i]; ++i)\n    if (values_" + c + "[i] == v)\n      return names_" + c + "[i];\n  return NULL;\n}\n\n");
            EndFragment(One("name_" + c), "");
        }
    }

    // ------------------------------------------------------------ types

    private List<ApiField> AllFields(ApiType t)
    {
        var r = new List<ApiField>();
        ApiType? b;
        if (t.Base != null && types.TryGetValue(t.Base, out b))
        {
            foreach (var f in AllFields(b!)) r.Add(f);
        }
        foreach (var f in t.Fields) r.Add(f);
        return r;
    }

    private static List<ApiField> Funcs(ApiType t)
    {
        var r = new List<ApiField>();
        foreach (var f in t.Fields)
        {
            if (f.Type.Kind == LubTypeKind.Func) r.Add(f);
        }
        return r;
    }

    private void EmitReader(ApiType t)
    {
        var c = CType(t.Name);
        BeginFragment();
        EmitReaderBody(t);
        EndFragment(One("read_" + c), "static void read_" + c + "(lua_State *L, int idx, void *out);\n");
    }

    private void EmitReaderBody(ApiType t)
    {
        var c = CType(t.Name);
        sb.Write("static void read_" + c + "(lua_State *L, int idx, void *out_) {\n");
        sb.Write("  " + c + " *o = (" + c + " *)out_;\n");
        sb.Write("  idx = lua_absindex(L, idx);\n  (void)o;\n");
        if (t.Base != null) sb.Write("  read_" + CType(t.Base) + "(L, idx, &o->base);\n");
        var funcs = Funcs(t);
        if (funcs.Count > 0)
        {
            sb.Write("  LgenCallbacks *cb = lgen_callbacks_new(L, " + funcs.Count + ");\n");
            sb.Write("  o->user = cb;\n  o->user_release = lgen_callbacks_free;\n");
        }
        foreach (var f in t.Fields)
        {
            var n = f.LuaName;
            var q = "\"" + n + "\"";
            var tr = f.Type;
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                    sb.Write(f.Optional ? "  o->has_" + n + " = lgen_int_opt(L, idx, " + q + ", &o->" + n + ");\n"
                        : "  o->" + n + " = lgen_int(L, idx, " + q + ", 0);\n");
                    break;
                case LubTypeKind.Double:
                    sb.Write(f.Optional ? "  o->has_" + n + " = lgen_num_opt(L, idx, " + q + ", &o->" + n + ");\n"
                        : "  o->" + n + " = lgen_num(L, idx, " + q + ", 0.0f);\n");
                    break;
                case LubTypeKind.Bool:
                    sb.Write(f.Optional ? "  o->has_" + n + " = lgen_bool_opt(L, idx, " + q + ", &o->" + n + ");\n"
                        : "  o->" + n + " = lgen_bool(L, idx, " + q + ", false);\n");
                    break;
                case LubTypeKind.Enum:
                    if (IsLuaString(tr))
                    {
                        var ec = EnumC(tr.Name);
                        var has = f.Optional ? "&o->has_" + n : "NULL";
                        sb.Write("  o->" + n + " = lgen_enum_str(L, idx, " + q + ", names_" + ec + ", values_" + ec + ", \"" + EnumOf(tr).Name + "\", " + has + ");\n");
                    }
                    else
                        sb.Write(f.Optional ? "  o->has_" + n + " = lgen_int_opt(L, idx, " + q + ", &o->" + n + ");\n"
                            : "  o->" + n + " = lgen_int(L, idx, " + q + ", 0);\n");
                    break;
                case LubTypeKind.String:
                    if (f.Bits)
                        sb.Write(f.Optional ? "  o->has_" + n + " = lgen_bits_opt(L, idx, " + q + ", &o->" + n + ");\n"
                            : "  lgen_bits_opt(L, idx, " + q + ", &o->" + n + ");\n");
                    else
                        sb.Write("  o->" + n + " = lgen_str(L, idx, " + q + ");\n");
                    break;
                case LubTypeKind.Handle:
                    sb.Write("  o->" + n + " = lgen_ref(L, idx, " + q + ", \"" + Kind(tr) + "\");\n");
                    break;
                case LubTypeKind.Keyed:
                    sb.Write("  o->" + n + " = lgen_keyed(L, idx, " + q + ", \"" + Kind(tr) + "\");\n");
                    break;
                case LubTypeKind.View:
                    // 文字列か view userdata。
                    // frame は今の frame (Lua 側で作った文字列は frame を持たない)
                    sb.Write("  if (lgen_has(L, idx, " + q + ")) {\n    lua_getfield(L, idx, " + q + ");\n");
                    sb.Write("    int32_t " + n + "_len = 0;\n    o->" + n + ".ptr = lgen_bytes_arg(L, lua_gettop(L), &" + n + "_len, true);\n");
                    sb.Write("    o->" + n + ".len = " + n + "_len;\n    o->" + n + ".frame = lub_frame_index(lgen_ctx());\n    lua_pop(L, 1);\n  }\n");
                    break;
                case LubTypeKind.Record:
                    sb.Write("  if (lgen_has(L, idx, " + q + ")) {\n");
                    sb.Write("    lua_getfield(L, idx, " + q + ");\n");
                    sb.Write("    read_" + CType(tr.Name) + "(L, -1, &o->" + n + ");\n");
                    sb.Write("    lua_pop(L, 1);\n");
                    if (f.Optional) sb.Write("    o->has_" + n + " = true;\n");
                    sb.Write("  }\n");
                    break;
                case LubTypeKind.List:
                {
                    var elem = tr.Elem!;
                    if (elem.Kind == LubTypeKind.Array)
                        sb.Write("  o->" + n + " = (const float (*)[" + f.ArrayLen + "])lgen_float_rows(L, idx, " + q + ", " + f.ArrayLen + ", &o->" + n + "_count);\n");
                    else if (f.ArrayLen >= 0)
                    {
                        int len = f.ArrayLen;
                        if (elem.Kind == LubTypeKind.Double)
                            sb.Write("  lgen_floats_fixed(L, idx, " + q + ", o->" + n + ", " + len + ", &o->" + n + "_count);\n");
                        else if (elem.Kind == LubTypeKind.Int)
                            sb.Write("  lgen_ints_fixed(L, idx, " + q + ", o->" + n + ", " + len + ", &o->" + n + "_count);\n");
                        else GenError.Report(t.Name + "." + f.Name + ": fixed array of " + elem.ToString());
                    }
                    else
                    {
                        switch (elem.Kind)
                        {
                            case LubTypeKind.Double:
                                sb.Write("  o->" + n + " = lgen_floats(L, idx, " + q + ", &o->" + n + "_count);\n");
                                break;
                            case LubTypeKind.Int:
                            case LubTypeKind.Enum:
                                sb.Write("  o->" + n + " = lgen_ints(L, idx, " + q + ", &o->" + n + "_count);\n");
                                break;
                            case LubTypeKind.String:
                                sb.Write("  o->" + n + " = lgen_strs(L, idx, " + q + ", &o->" + n + "_count);\n");
                                break;
                            case LubTypeKind.Handle:
                                sb.Write("  o->" + n + " = lgen_handles(L, idx, " + q + ", \"" + Kind(elem) + "\", &o->" + n + "_count);\n");
                                break;
                            case LubTypeKind.Record:
                                sb.Write("  o->" + n + " = (const " + CType(elem.Name) + " *)lgen_records(L, idx, " + q + ", sizeof(" + CType(elem.Name) + "), read_" + CType(elem.Name) + ", &o->" + n + "_count);\n");
                                break;
                            default:
                                GenError.Report(t.Name + "." + f.Name + ": list of " + elem.ToString());
                                break;
                        }
                    }
                    break;
                }
                case LubTypeKind.Array:
                {
                    int len = f.ArrayLen;
                    string call = tr.Elem!.Kind == LubTypeKind.Double
                        ? "lgen_floats_fixed(L, idx, " + q + ", o->" + n + ", " + len + ", NULL)"
                        : "lgen_ints_fixed(L, idx, " + q + ", o->" + n + ", " + len + ", NULL)";
                    sb.Write(f.Optional ? "  o->has_" + n + " = " + call + ";\n" : "  " + call + ";\n");
                    break;
                }
                case LubTypeKind.Func:
                {
                    int i = -1;
                    for (int k = 0; k < funcs.Count; k++)
                    {
                        if (funcs[k] == f)
                        {
                            i = k;
                            break;
                        }
                    }
                    sb.Write("  o->" + n + " = lgen_callbacks_field(L, cb, " + i + ", idx, " + q + ") ? tramp_" + c + "_" + n + " : NULL;\n");
                    break;
                }
                default:
                    GenError.Report(t.Name + "." + f.Name + ": unsupported " + tr.ToString());
                    break;
            }
        }
        sb.Write("}\n\n");
    }

    private void EmitPusher(ApiType t)
    {
        var c = CType(t.Name);
        BeginFragment();
        EmitFillBody(t);
        EndFragment(One("fill_" + c), "static void fill_" + c + "(lua_State *L, const " + c + " *v);\n");
        BeginFragment();
        sb.Write("static void push_" + c + "(lua_State *L, const " + c + " *v) {\n");
        sb.Write("  lua_createtable(L, 0, " + AllFields(t).Count + ");\n  fill_" + c + "(L, v);\n}\n\n");
        EndFragment(One("push_" + c), "static void push_" + c + "(lua_State *L, const " + c + " *v);\n");
        BeginFragment();
        sb.Write("static void push_list_" + c + "(lua_State *L, const " + c + " *v, int32_t n) {\n");
        sb.Write("  lua_createtable(L, n, 0);\n  for (int32_t i = 0; i < n; ++i) {\n");
        sb.Write("    push_" + c + "(L, &v[i]);\n    lua_rawseti(L, -2, i + 1);\n  }\n}\n\n");
        EndFragment(One("push_list_" + c), "static void push_list_" + c + "(lua_State *L, const " + c + " *v, int32_t n);\n");
    }

    private void EmitFillBody(ApiType t)
    {
        var c = CType(t.Name);
        sb.Write("static void fill_" + c + "(lua_State *L, const " + c + " *v) {\n");
        if (t.Base != null) sb.Write("  fill_" + CType(t.Base) + "(L, &v->base);\n");
        foreach (var f in t.Fields)
        {
            var n = f.LuaName;
            var q = "\"" + n + "\"";
            var tr = f.Type;
            var guard = f.Optional ? "  if (v->has_" + n + ")\n  " : "  ";
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                    sb.Write(guard + "lgen_set_int(L, " + q + ", v->" + n + ");\n");
                    break;
                case LubTypeKind.Double:
                    sb.Write(guard + "lgen_set_num(L, " + q + ", v->" + n + ");\n");
                    break;
                case LubTypeKind.Bool:
                    sb.Write(guard + "lgen_set_bool(L, " + q + ", v->" + n + ");\n");
                    break;
                case LubTypeKind.Enum:
                    if (IsLuaString(tr))
                    {
                        var ec = EnumC(tr.Name);
                        sb.Write(f.Optional ? "  if (v->has_" + n + " && name_" + ec + "(v->" + n + ")) {\n" : "  if (name_" + ec + "(v->" + n + ")) {\n");
                        sb.Write("    lua_pushstring(L, name_" + ec + "(v->" + n + "));\n    lua_setfield(L, -2, " + q + ");\n  }\n");
                    }
                    else
                        sb.Write(guard + "lgen_set_int(L, " + q + ", v->" + n + ");\n");
                    break;
                case LubTypeKind.String:
                    if (f.Bits)
                        sb.Write(guard + "lgen_set_bits(L, " + q + ", v->" + n + ");\n");
                    else if (f.Optional)
                        sb.Write("  if (v->" + n + ".len > 0)\n    lgen_set_str(L, " + q + ", v->" + n + ");\n");
                    else
                        sb.Write("  lgen_set_str(L, " + q + ", v->" + n + ");\n");
                    break;
                case LubTypeKind.Handle:
                    sb.Write("  if (v->" + n + ") {\n    lgen_push_ref(L, \"" + Kind(tr) + "\", v->" + n + ");\n    lua_setfield(L, -2, " + q + ");\n  }\n");
                    break;
                case LubTypeKind.Keyed:
                    sb.Write("  if (v->" + n + ".len > 0) {\n    lgen_push_keyed(L, \"" + Kind(tr) + "\", v->" + n + ");\n    lua_setfield(L, -2, " + q + ");\n  }\n");
                    break;
                case LubTypeKind.View:
                    sb.Write("  if (v->" + n + ".ptr) {\n    lgen_push_bytes_view(L, v->" + n + ");\n    lua_setfield(L, -2, " + q + ");\n  }\n");
                    break;
                case LubTypeKind.Record:
                    sb.Write(f.Optional ? "  if (v->has_" + n + ") {\n" : "  {\n");
                    sb.Write("    push_" + CType(tr.Name) + "(L, &v->" + n + ");\n    lua_setfield(L, -2, " + q + ");\n  }\n");
                    break;
                case LubTypeKind.List:
                {
                    var elem = tr.Elem!;
                    string push;
                    if (elem.Kind == LubTypeKind.Array)
                        push = "lgen_push_float_table(L, (const float *)v->" + n + ", v->" + n + "_count * " + f.ArrayLen + ")";
                    else if (f.ArrayLen >= 0)
                        push = elem.Kind == LubTypeKind.Double
                            ? "lgen_push_float_table(L, v->" + n + ", v->" + n + "_count)"
                            : "lgen_push_int_table(L, v->" + n + ", v->" + n + "_count)";
                    else
                    {
                        switch (elem.Kind)
                        {
                            case LubTypeKind.Double:
                                push = "lgen_push_float_view(L, v->" + n + ", v->" + n + "_count)";
                                break;
                            case LubTypeKind.Int:
                            case LubTypeKind.Enum:
                                push = "lgen_push_int_view(L, v->" + n + ", v->" + n + "_count)";
                                break;
                            case LubTypeKind.String:
                                push = "lgen_push_str_table(L, v->" + n + ", v->" + n + "_count)";
                                break;
                            case LubTypeKind.Handle:
                                push = "lgen_push_handle_table(L, \"" + Kind(elem) + "\", v->" + n + ", v->" + n + "_count)";
                                break;
                            case LubTypeKind.Record:
                                push = "push_list_" + CType(elem.Name) + "(L, v->" + n + ", v->" + n + "_count)";
                                break;
                            default:
                                GenError.Report(t.Name + "." + f.Name + ": list of " + elem.ToString());
                                push = "";
                                break;
                        }
                    }
                    string cond = f.ArrayLen >= 0 || elem.Kind == LubTypeKind.Array ? "" : "  if (v->" + n + ")\n  ";
                    sb.Write((cond != "" ? cond : "  ") + "{\n    " + push + ";\n    lua_setfield(L, -2, " + q + ");\n  }\n");
                    break;
                }
                case LubTypeKind.Array:
                {
                    string push = tr.Elem!.Kind == LubTypeKind.Double
                        ? "lgen_push_float_table(L, v->" + n + ", " + f.ArrayLen + ")"
                        : "lgen_push_int_table(L, v->" + n + ", " + f.ArrayLen + ")";
                    sb.Write(f.Optional ? "  if (v->has_" + n + ") {\n" : "  {\n");
                    sb.Write("    " + push + ";\n    lua_setfield(L, -2, " + q + ");\n  }\n");
                    break;
                }
                case LubTypeKind.Func:
                    break; // callback は Lua に戻さない
                default:
                    GenError.Report(t.Name + "." + f.Name + ": unsupported " + tr.ToString());
                    break;
            }
        }
        sb.Write("  (void)L;\n  (void)v;\n");
        sb.Write("}\n\n");
    }

    // -------------------------------------------------------- callbacks

    private void EmitTrampolines(ApiType t)
    {
        var c = CType(t.Name);
        var funcs = Funcs(t);
        for (int i = 0; i < funcs.Count; i++)
            EmitTrampoline("tramp_" + c + "_" + funcs[i].LuaName, funcs[i].Type, i, funcs[i].LuaName);
    }

    private void EmitTrampoline(string name, TypeRef fn, int slot, string what)
    {
        BeginFragment();
        EmitTrampolineBody(name, fn, slot, what);
        EndFragment(One(name), "");
    }

    private void EmitTrampolineBody(string name, TypeRef fn, int slot, string what)
    {
        var ret = fn.FuncReturn!;
        string retC;
        switch (ret.Kind)
        {
            case LubTypeKind.Bool:
                retC = "bool";
                break;
            case LubTypeKind.Double:
                retC = "float";
                break;
            case LubTypeKind.Int:
                retC = "int32_t";
                break;
            case LubTypeKind.Void:
                retC = "void";
                break;
            default:
                GenError.Report("callback " + what + ": return " + ret.ToString());
                retC = "";
                break;
        }
        var ps = fn.FuncParams!;
        var args = new List<string>();
        args.Add("void *user");
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            var pn = Letter(i);
            switch (p.Kind)
            {
                case LubTypeKind.Record:
                    args.Add("const " + CType(p.Name) + " *" + pn);
                    break;
                case LubTypeKind.Int:
                case LubTypeKind.Enum:
                    args.Add("int32_t " + pn);
                    break;
                case LubTypeKind.Double:
                    args.Add("float " + pn);
                    break;
                case LubTypeKind.Bool:
                    args.Add("bool " + pn);
                    break;
                default:
                    GenError.Report("callback " + what + ": param " + p.ToString());
                    break;
            }
        }
        // 既定値: 判定は「続行 / 衝突する」、数値は 0 (runtime が既定に読み替える)。
        string def = ret.Kind == LubTypeKind.Bool ? "true" : ret.Kind == LubTypeKind.Double ? "1.0f" : ret.Kind == LubTypeKind.Int ? "1" : "";
        string retStmt = "return" + (def != "" ? " " + def : "");
        sb.Write("static " + retC + " " + name + "(" + string.Join(", ", args) + ") {\n");
        sb.Write("  LgenCallbacks *cb = (LgenCallbacks *)user;\n  lua_State *L = cb->L;\n");
        sb.Write("  if (!lgen_callbacks_push(cb, " + slot + "))\n    " + retStmt + ";\n");
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            var pn = Letter(i);
            switch (p.Kind)
            {
                case LubTypeKind.Record:
                    sb.Write("  push_" + CType(p.Name) + "(L, " + pn + ");\n");
                    break;
                case LubTypeKind.Int:
                case LubTypeKind.Enum:
                    sb.Write("  lua_pushinteger(L, " + pn + ");\n");
                    break;
                case LubTypeKind.Double:
                    sb.Write("  lua_pushnumber(L, " + pn + ");\n");
                    break;
                case LubTypeKind.Bool:
                    sb.Write("  lua_pushboolean(L, " + pn + ");\n");
                    break;
                default:
                    GenError.Report("callback " + what + ": param " + p.ToString());
                    break;
            }
        }
        int nres = ret.Kind == LubTypeKind.Void ? 0 : 1;
        sb.Write("  if (!lgen_callbacks_call(cb, " + slot + ", " + ps.Count + ", " + nres + "))\n    " + retStmt + ";\n");
        switch (ret.Kind)
        {
            case LubTypeKind.Bool:
                sb.Write("  bool r = lua_toboolean(L, -1);\n  lua_pop(L, 1);\n  return r;\n");
                break;
            case LubTypeKind.Double:
                sb.Write("  float r = (float)lua_tonumber(L, -1);\n  lua_pop(L, 1);\n  return r;\n");
                break;
            case LubTypeKind.Int:
                sb.Write("  int32_t r = (int32_t)lua_tointeger(L, -1);\n  lua_pop(L, 1);\n  return r;\n");
                break;
        }
        sb.Write("}\n\n");
    }

    // -------------------------------------------------------- functions

    private void EmitFunction(ApiNamespace ns, ApiFunction f)
    {
        foreach (var p in f.Params)
        {
            if (p.Type.Kind == LubTypeKind.Func)
                EmitTrampoline("tramp_" + LName(ns, f.LuaName) + "_" + p.LuaName, p.Type, 0, ns.Name + "." + f.Name + "." + p.Name);
        }

        sb.Write("static int " + LName(ns, f.LuaName) + "(lua_State *L) {\n");
        sb.Write("  (void)L;\n  LgenMark mark = lgen_mark();\n");
        var call = new List<string>();
        call.Add("lgen_ctx()");
        var post = new Out(); // 呼び出し後の後始末
        int idx = 0;
        var lazy = LazyData(ns, f);
        string? lazyConvert = null; // lazy data の変換 (probe が外れたときだけ走る)
        int lazyIdx = 0;
        string? nativeName = null;
        int nativeIdx = 0;
        foreach (var p in f.Params)
        {
            if (p.IsOut) continue;
            idx++;
            var n = p.LuaName;
            var tr = p.Type;
            // 既定値のある引数は値で渡す (省略なら既定値)。
            // null が既定のものは pointer
            bool opt = tr.Nullable || (p.Optional && !p.HasDefault);
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Double:
                case LubTypeKind.Bool:
                case LubTypeKind.Enum:
                {
                    string ct = tr.Kind == LubTypeKind.Double ? "float" : tr.Kind == LubTypeKind.Bool ? "bool" : "int32_t";
                    string read;
                    if (tr.Kind == LubTypeKind.Double) read = "(float)luaL_checknumber(L, " + idx + ")";
                    else if (tr.Kind == LubTypeKind.Bool) read = "lua_toboolean(L, " + idx + ")";
                    else if (tr.Kind == LubTypeKind.Enum && IsLuaString(tr))
                        read = "lgen_enum_str_arg(L, " + idx + ", names_" + EnumC(tr.Name) + ", values_" + EnumC(tr.Name) + ", \"" + EnumOf(tr).Name + "\")";
                    else read = "(int32_t)luaL_checkinteger(L, " + idx + ")";
                    if (p.DefaultLiteral != null)
                        sb.Write("  " + ct + " " + n + " = lua_isnoneornil(L, " + idx + ") ? " + p.DefaultLiteral + " : " + read + ";\n");
                    else if (opt)
                    {
                        sb.Write("  " + ct + " " + n + "_v = 0;\n  const " + ct + " *" + n + " = NULL;\n");
                        sb.Write("  if (!lua_isnoneornil(L, " + idx + ")) {\n    " + n + "_v = " + read + ";\n    " + n + " = &" + n + "_v;\n  }\n");
                    }
                    else
                        sb.Write("  " + ct + " " + n + " = " + read + ";\n");
                    call.Add(n);
                    break;
                }
                case LubTypeKind.String:
                    sb.Write(opt ? "  LubStr " + n + " = lgen_str_opt(L, " + idx + ");\n" : "  LubStr " + n + " = lgen_str_arg(L, " + idx + ");\n");
                    call.Add(n);
                    break;
                case LubTypeKind.Handle:
                    sb.Write("  LubHandle " + n + " = lgen_ref_arg(L, " + idx + ", \"" + Kind(tr) + "\", " + (opt ? "false" : "true") + ");\n");
                    call.Add(n);
                    break;
                case LubTypeKind.Keyed:
                    sb.Write("  LubStr " + n + " = lgen_keyed_arg(L, " + idx + ", \"" + Kind(tr) + "\");\n");
                    call.Add(n);
                    break;
                case LubTypeKind.View:
                    sb.Write("  int32_t " + n + "_len = 0;\n  const uint8_t *" + n + " = lgen_bytes_arg(L, " + idx + ", &" + n + "_len, " + (opt ? "false" : "true") + ");\n");
                    call.Add(n);
                    call.Add(n + "_len");
                    break;
                case LubTypeKind.Record:
                {
                    var c = CType(tr.Name);
                    sb.Write("  " + c + " " + n + "_v;\n  memset(&" + n + "_v, 0, sizeof " + n + "_v);\n  const " + c + " *" + n + " = NULL;\n");
                    if (opt)
                        sb.Write("  if (!lua_isnoneornil(L, " + idx + ")) {\n    luaL_checktype(L, " + idx + ", LUA_TTABLE);\n    read_" + c + "(L, " + idx + ", &" + n + "_v);\n    " + n + " = &" + n + "_v;\n  }\n");
                    else
                        sb.Write("  luaL_checktype(L, " + idx + ", LUA_TTABLE);\n  read_" + c + "(L, " + idx + ", &" + n + "_v);\n  " + n + " = &" + n + "_v;\n");
                    call.Add(n);
                    break;
                }
                case LubTypeKind.List:
                {
                    var elem = tr.Elem!;
                    string req = opt ? "false" : "true";
                    sb.Write("  int32_t " + n + "_count = 0;\n");
                    if (p == lazy)
                    {
                        string ct = elem.Kind == LubTypeKind.Double ? "float" : "int32_t";
                        string arg = elem.Kind == LubTypeKind.Double ? "lgen_floats_arg" : "lgen_ints_arg";
                        sb.Write("  const " + ct + " *" + n + " = NULL;\n");
                        lazyConvert = "    " + n + " = " + arg + "(L, " + idx + ", &" + n + "_count, false);\n";
                        lazyIdx = idx;
                        call.Add(n);
                        call.Add(n + "_count");
                        break;
                    }
                    switch (elem.Kind)
                    {
                        case LubTypeKind.Double:
                            sb.Write("  const float *" + n + " = lgen_floats_arg(L, " + idx + ", &" + n + "_count, " + req + ");\n");
                            break;
                        case LubTypeKind.Int:
                        case LubTypeKind.Enum:
                            sb.Write("  const int32_t *" + n + " = lgen_ints_arg(L, " + idx + ", &" + n + "_count, " + req + ");\n");
                            break;
                        case LubTypeKind.Record:
                            sb.Write("  const " + CType(elem.Name) + " *" + n + " = (const " + CType(elem.Name) + " *)lgen_records_arg(L, " + idx + ", sizeof(" + CType(elem.Name) + "), read_" + CType(elem.Name) + ", &" + n + "_count, " + req + ");\n");
                            break;
                        default:
                            GenError.Report(ns.Name + "." + f.Name + ": list param of " + elem.ToString());
                            break;
                    }
                    call.Add(n);
                    call.Add(n + "_count");
                    break;
                }
                case LubTypeKind.Func:
                    sb.Write("  LgenCallbacks *" + n + "_cb = lgen_callbacks_arg(L, " + idx + ");\n");
                    call.Add(n + "_cb ? tramp_" + LName(ns, f.LuaName) + "_" + n + " : NULL");
                    call.Add(n + "_cb");
                    // visitor の error は nil, "<fn> visitor: msg" で返す
                    post.Write("  if (lgen_callbacks_error(" + n + "_cb)) {\n");
                    post.Write("    lua_pushnil(L);\n    lua_pushfstring(L, \"" + Text.From(FnName(ns, f.LuaName), 4) + " visitor: %s\", lgen_callbacks_error(" + n + "_cb));\n");
                    post.Write("    lgen_callbacks_free(" + n + "_cb);\n    lgen_release(mark);\n    return 2;\n  }\n");
                    post.Write("  lgen_callbacks_free(" + n + "_cb);\n");
                    break;
                case LubTypeKind.Native:
                    sb.Write("  LubBindings *" + n + " = lgen_bindings_native(L, " + idx + ");\n");
                    call.Add(n);
                    nativeName = n;
                    nativeIdx = idx;
                    break;
                case LubTypeKind.Array:
                    if (tr.Elem!.Kind == LubTypeKind.Double)
                    {
                        sb.Write("  int32_t " + n + "_count = 0;\n  const float *" + n + " = lgen_floats_arg(L, " + idx + ", &" + n + "_count, true);\n");
                        call.Add(n);
                        call.Add(n + "_count");
                    }
                    else GenError.Report(ns.Name + "." + f.Name + ": unsupported param " + p.Name + ": " + tr.ToString());
                    break;
                default:
                    GenError.Report(ns.Name + "." + f.Name + ": unsupported param " + p.Name + ": " + tr.ToString());
                    break;
            }
        }
        // out 引数と戻り値
        var outNames = new List<string>();
        var outTypes = new List<TypeRef>();
        var outHas = new List<bool>();
        foreach (var p in f.Params)
        {
            if (!p.IsOut) continue;
            DeclareOut(p.LuaName, p.Type, call);
            bool hasFlag = p.Type.Kind == LubTypeKind.Record && p.Type.Nullable;
            if (hasFlag)
            {
                sb.Write("  bool has_" + p.LuaName + " = false;\n");
                call.Add("&has_" + p.LuaName);
            }
            outNames.Add(p.LuaName);
            outTypes.Add(p.Type);
            outHas.Add(hasFlag);
        }
        var r = f.Return;
        bool direct = f.NoFail && (r.Kind == LubTypeKind.Void || r.Kind == LubTypeKind.Int || r.Kind == LubTypeKind.Double
            || r.Kind == LubTypeKind.Bool || r.Kind == LubTypeKind.Enum || r.Kind == LubTypeKind.Handle);
        bool retHas = false;
        if (!direct && r.Kind != LubTypeKind.Void)
        {
            DeclareOut("out", r, call);
            retHas = !f.NoFail && r.Nullable && (r.IsScalar || (r.Kind == LubTypeKind.Record && f.Maybe));
            if (retHas)
            {
                sb.Write("  bool has = false;\n");
                call.Add("&has");
            }
        }
        string callExpr = FnName(ns, f.LuaName) + "(" + string.Join(", ", call) + ")";
        if (direct)
        {
            if (r.Kind == LubTypeKind.Void) sb.Write("  " + callExpr + ";\n");
            else sb.Write("  " + ReturnCType(r) + " out = " + callExpr + ";\n");
            sb.Write(post.ToString());
            sb.Write("  lgen_release(mark);\n");
        }
        else if (f.NoFail)
        {
            sb.Write("  " + callExpr + ";\n");
            sb.Write(post.ToString());
            sb.Write("  lgen_release(mark);\n");
        }
        else
        {
            if (lazy != null)
            {
                // version を渡した宣言は、まず data 無しで version の一致だけを問う (lub_api.h の LUB_DATA_DEFERRED)。
                // 外れたときだけ data を変換する。
                var probeArgs = new List<string>();
                foreach (var c in call)
                {
                    if (c == lazy.LuaName) probeArgs.Add("NULL");
                    else if (c == lazy.LuaName + "_count") probeArgs.Add("LUB_DATA_DEFERRED");
                    else probeArgs.Add(c);
                }
                string probe = string.Join(", ", probeArgs);
                sb.Write("  LubStatus st = LUB_NOT_FOUND;\n");
                sb.Write("  if (!lua_isnoneornil(L, " + lazyIdx + ") && version)\n    st = " + FnName(ns, f.LuaName) + "(" + probe + ");\n");
                sb.Write("  if (st == LUB_NOT_FOUND) {\n" + lazyConvert + "    st = " + callExpr + ";\n  }\n");
            }
            else
                sb.Write("  LubStatus st = " + callExpr + ";\n");
            if (nativeName != null)
                sb.Write("  while (st == LUB_BINDINGS_FULL) {\n    " + nativeName + " = lgen_bindings_grow(L, " + nativeIdx + ");\n    st = " + callExpr + ";\n  }\n");
            sb.Write(post.ToString());
            sb.Write("  lgen_release(mark);\n");
            sb.Write("  if (st == LUB_ERROR)\n    return lgen_raise(L);\n");
            int nret = (r.Kind == LubTypeKind.Void ? 0 : 1) + outNames.Count;
            if (nret == 1)
            {
                // 対象が無い: nil, "not found" (従来の Lua 面の契約)
                sb.Write("  if (st == LUB_NOT_FOUND) {\n    lua_pushnil(L);\n    lua_pushstring(L, \"not found\");\n    return 2;\n  }\n");
            }
            else if (nret > 0)
            {
                sb.Write("  if (st == LUB_NOT_FOUND) {\n");
                for (int i = 0; i < nret; i++) sb.Write("    lua_pushnil(L);\n");
                sb.Write("    return " + nret + ";\n  }\n");
            }
        }
        int results = 0;
        if (r.Kind != LubTypeKind.Void)
        {
            // key で宣言する resource の handle は sentinel に key 列を持たせる
            ApiParam? keyParam = null;
            foreach (var p in f.Params)
            {
                if (!p.IsOut && p.Type.Kind == LubTypeKind.String && p.LuaName == "key")
                {
                    keyParam = p;
                    break;
                }
            }
            int parentIdx = 0;
            int k = 0;
            foreach (var p in f.Params)
            {
                if (p.IsOut) continue;
                if (p.Type.Kind == LubTypeKind.Handle)
                {
                    parentIdx = k + 1;
                    break;
                }
                k++;
            }
            if (r.Kind == LubTypeKind.Handle && keyParam != null)
                sb.Write("  if (out == 0)\n    lua_pushnil(L);\n  else\n    lgen_push_ref_keyed(L, \"" + Kind(r) + "\", out, " + parentIdx + ", key);\n");
            else if (direct) PushValue("out", r, null);
            else PushValue("out", r, retHas ? "has" : null);
            results++;
        }
        for (int i = 0; i < outNames.Count; i++)
        {
            PushValue(outNames[i], outTypes[i], outHas[i] ? "has_" + outNames[i] : null);
            results++;
        }
        sb.Write("  return " + results + ";\n}\n\n");
    }

    // version と組になる data (scalar の list)。
    // version が一致する宣言では読まない契約なので、変換を probe の後ろに回す。
    // stub では nullable。
    private static ApiParam? LazyData(ApiNamespace ns, ApiFunction f)
    {
        ApiParam? version = null;
        foreach (var p in f.Params)
        {
            if (!p.IsOut && p.LuaName == "version")
            {
                version = p;
                break;
            }
        }
        if (version == null || version.Type.Kind != LubTypeKind.Int) return null;
        ApiParam? data = null;
        foreach (var p in f.Params)
        {
            if (p.IsOut || p.Type.Kind != LubTypeKind.List) continue;
            var ek = p.Type.Elem!.Kind;
            if (ek == LubTypeKind.Double || ek == LubTypeKind.Int || ek == LubTypeKind.Enum)
            {
                data = p;
                break;
            }
        }
        if (data == null) return null;
        if (!data.Type.Nullable)
        {
            GenError.Report(ns.Name + "." + f.Name + ": data next to version must be nullable");
            return null;
        }
        if (f.NoFail)
        {
            GenError.Report(ns.Name + "." + f.Name + ": data next to version must report status");
            return null;
        }
        return data;
    }

    private void DeclareOut(string n, TypeRef tr, List<string> call)
    {
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
            case LubTypeKind.Enum:
                sb.Write("  int32_t " + n + " = 0;\n");
                call.Add("&" + n);
                break;
            case LubTypeKind.Double:
                sb.Write("  float " + n + " = 0;\n");
                call.Add("&" + n);
                break;
            case LubTypeKind.Bool:
                sb.Write("  bool " + n + " = false;\n");
                call.Add("&" + n);
                break;
            case LubTypeKind.String:
                sb.Write("  LubStr " + n + " = {NULL, 0};\n");
                call.Add("&" + n);
                break;
            case LubTypeKind.Handle:
                sb.Write("  LubHandle " + n + " = 0;\n");
                call.Add("&" + n);
                break;
            case LubTypeKind.View:
                sb.Write("  LubView " + n + " = {NULL, 0, 0};\n");
                call.Add("&" + n);
                break;
            case LubTypeKind.Record:
                sb.Write("  " + CType(tr.Name) + " " + n + ";\n  memset(&" + n + ", 0, sizeof " + n + ");\n");
                call.Add("&" + n);
                break;
            case LubTypeKind.List:
            {
                var elem = tr.Elem!;
                string ct;
                switch (elem.Kind)
                {
                    case LubTypeKind.Double:
                        ct = "float";
                        break;
                    case LubTypeKind.Int:
                    case LubTypeKind.Enum:
                        ct = "int32_t";
                        break;
                    case LubTypeKind.String:
                        ct = "LubStr";
                        break;
                    case LubTypeKind.Handle:
                        ct = "LubHandle";
                        break;
                    case LubTypeKind.Record:
                        ct = CType(elem.Name);
                        break;
                    default:
                        GenError.Report("out list of " + elem.ToString());
                        ct = "";
                        break;
                }
                sb.Write("  const " + ct + " *" + n + " = NULL;\n  int32_t " + n + "_count = 0;\n");
                call.Add("&" + n);
                call.Add("&" + n + "_count");
                break;
            }
            default:
                GenError.Report("unsupported out / return " + tr.ToString());
                break;
        }
    }

    private static string ReturnCType(TypeRef tr)
    {
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
            case LubTypeKind.Enum:
                return "int32_t";
            case LubTypeKind.Double:
                return "float";
            case LubTypeKind.Bool:
                return "bool";
            case LubTypeKind.Handle:
                return "LubHandle";
        }
        GenError.Report("direct return " + tr.ToString());
        return "";
    }

    private void PushValue(string n, TypeRef tr, string? has)
    {
        string guard = has != null ? "  if (!" + has + ")\n    lua_pushnil(L);\n  else\n  " : "  ";
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
                sb.Write(guard + "lua_pushinteger(L, " + n + ");\n");
                break;
            case LubTypeKind.Enum:
                if (IsLuaString(tr))
                {
                    var ec = EnumC(tr.Name);
                    sb.Write(guard + "lua_pushstring(L, name_" + ec + "(" + n + ") ? name_" + ec + "(" + n + ") : \"\");\n");
                }
                else
                    sb.Write(guard + "lua_pushinteger(L, " + n + ");\n");
                break;
            case LubTypeKind.Double:
                sb.Write(guard + "lua_pushnumber(L, " + n + ");\n");
                break;
            case LubTypeKind.Bool:
                sb.Write(guard + "lua_pushboolean(L, " + n + ");\n");
                break;
            case LubTypeKind.String:
                if (tr.Nullable)
                    sb.Write("  if (" + n + ".len == 0 && !" + n + ".ptr)\n    lua_pushnil(L);\n  else\n    lgen_push_str(L, " + n + ");\n");
                else
                    sb.Write(guard + "lgen_push_str(L, " + n + ");\n");
                break;
            case LubTypeKind.Handle:
                sb.Write("  if (" + n + " == 0)\n    lua_pushnil(L);\n  else\n    lgen_push_ref(L, \"" + Kind(tr) + "\", " + n + ");\n");
                break;
            case LubTypeKind.View:
                sb.Write("  if (!" + n + ".ptr)\n    lua_pushnil(L);\n  else\n    lgen_push_bytes_view(L, " + n + ");\n");
                break;
            case LubTypeKind.Record:
                sb.Write(guard + "push_" + CType(tr.Name) + "(L, &" + n + ");\n");
                break;
            case LubTypeKind.List:
            {
                var elem = tr.Elem!;
                string push;
                switch (elem.Kind)
                {
                    case LubTypeKind.Double:
                        push = "lgen_push_float_view(L, " + n + ", " + n + "_count)";
                        break;
                    case LubTypeKind.Int:
                    case LubTypeKind.Enum:
                        push = "lgen_push_int_view(L, " + n + ", " + n + "_count)";
                        break;
                    case LubTypeKind.String:
                        push = "lgen_push_str_table(L, " + n + ", " + n + "_count)";
                        break;
                    case LubTypeKind.Handle:
                        push = "lgen_push_handle_table(L, \"" + Kind(elem) + "\", " + n + ", " + n + "_count)";
                        break;
                    case LubTypeKind.Record:
                        push = "push_list_" + CType(elem.Name) + "(L, " + n + ", " + n + "_count)";
                        break;
                    default:
                        GenError.Report("push list of " + elem.ToString());
                        push = "";
                        break;
                }
                if (tr.Nullable)
                    sb.Write("  if (!" + n + ")\n    lua_pushnil(L);\n  else\n    " + push + ";\n");
                else
                    sb.Write(guard + push + ";\n");
                break;
            }
            default:
                GenError.Report("push " + tr.ToString());
                break;
        }
    }

    // --------------------------------------------------------- register

    // global の table ごと (lub、lubx) に組み立てる。
    private void EmitRegister()
    {
        sb.Write("void lub_api_gen_register(lua_State *L) {\n");
        var roots = new List<string>();
        foreach (var ns in model.Namespaces)
        {
            if (!roots.Contains(ns.LuaRoot)) roots.Add(ns.LuaRoot);
        }
        foreach (var root in roots) EmitRoot(root);
        sb.Write("}\n");
    }

    private void EmitRoot(string root)
    {
        sb.Write("  lua_newtable(L); // " + root + "\n");
        foreach (var ns in model.Namespaces)
        {
            if (ns.LuaRoot != root) continue;
            bool isRoot = ns.LuaPath == root;
            string member = isRoot ? "" : Text.From(ns.LuaPath, Text.Len(root) + 1);
            if (!isRoot) sb.Write("  lua_newtable(L); // " + ns.LuaPath + "\n");
            foreach (var f in ns.Functions)
            {
                if (f.NoC) continue;
                sb.Write("  lua_pushcfunction(L, " + LName(ns, f.LuaName) + ");\n  lua_setfield(L, -2, \"" + f.LuaName + "\");\n");
            }
            foreach (var e in ns.Enums)
            {
                foreach (var m in e.Members)
                {
                    if (e.LuaString) sb.Write("  lua_pushstring(L, \"" + m.LuaName.ToLower() + "\");\n");
                    else sb.Write("  lua_pushinteger(L, " + m.Value + ");\n");
                    sb.Write("  lua_setfield(L, -2, \"" + m.LuaName + "\");\n");
                }
            }
            foreach (var c in ns.Consts)
            {
                if (c.Value.Kind == ValueKind.Int) sb.Write("  lua_pushinteger(L, " + c.Value.I + ");\n");
                else if (c.Value.Kind == ValueKind.String) sb.Write("  lua_pushstring(L, \"" + c.Value.S + "\");\n");
                else continue;
                sb.Write("  lua_setfield(L, -2, \"" + c.LuaName + "\");\n");
            }
            foreach (var f in ns.StaticFields)
            {
                if (f.Type.Kind == LubTypeKind.Handle)
                    sb.Write("  lgen_push_ref(L, \"" + Kind(f.Type) + "\", " + FnName(ns, f.LuaName) + "(lgen_ctx()));\n  lua_setfield(L, -2, \"" + f.LuaName + "\");\n");
            }
            if (!isRoot) sb.Write("  lua_setfield(L, -2, \"" + member + "\");\n");
        }
        if (root == "lub") EmitRefMethods();
        sb.Write("  lua_setglobal(L, \"" + root + "\");\n");
    }

    // handle ごとの method table (world:step(...) の形)。
    // 第 1 引数がその handle の関数を、kind の接頭辞 (body_ / shape_ / joint_ / chain_ / world_) を落とした名前で持つ。
    // metatable "lub.ref.<kind>" の __index に置き、lgen_push_ref が sentinel に付ける。
    // lub.__refs.<kind> からも引ける (prelude が互換の method を足すため)。
    private void EmitRefMethods()
    {
        sb.Write("  lua_newtable(L); // lub.__refs\n");
        foreach (var t in model.Types)
        {
            if (t.Kind != "handle") continue;
            var typeName = t.Name;
            var kind = handleKinds[typeName];
            string baseKind = kind.EndsWith("3d") ? Text.DropEnd(kind, 2) : kind;
            var methods = new List<string>();
            var cfns = new List<string>();
            foreach (var ns in model.Namespaces)
            {
                foreach (var f in ns.Functions)
                {
                    if (f.NoC) continue;
                    ApiParam? first = null;
                    foreach (var p in f.Params)
                    {
                        if (!p.IsOut)
                        {
                            first = p;
                            break;
                        }
                    }
                    if (first == null || first.Type.Kind != LubTypeKind.Handle || first.Type.Name != typeName) continue;
                    string name = f.LuaName;
                    if (name.StartsWith(baseKind + "_") && Text.Len(name) > Text.Len(baseKind) + 1)
                        name = Text.From(name, Text.Len(baseKind) + 1);
                    if (methods.Contains(name)) continue;
                    methods.Add(name);
                    cfns.Add(LName(ns, f.LuaName));
                }
            }
            if (methods.Count == 0) continue;
            sb.Write("  luaL_newmetatable(L, \"lub.ref." + kind + "\");\n");
            sb.Write("  lua_createtable(L, 0, " + methods.Count + "); // methods\n");
            for (int i = 0; i < methods.Count; i++)
                sb.Write("  lua_pushcfunction(L, " + cfns[i] + ");\n  lua_setfield(L, -2, \"" + methods[i] + "\");\n");
            sb.Write("  lua_pushvalue(L, -1);\n");
            sb.Write("  lua_setfield(L, -4, \"" + kind + "\"); // lub.__refs." + kind + "\n");
            sb.Write("  lua_setfield(L, -2, \"__index\");\n  lua_pop(L, 1);\n");
        }
        sb.Write("  lua_setfield(L, -2, \"__refs\");\n");
    }
}
