using System.Collections.Generic;
using TcsSelf;

namespace LubGen;

/// <summary>stub から C API の header (include/lub/lub_api.h) を導く。
/// C の関数は stub の関数と 1 対 1、struct は class と 1 対 1。
/// </summary>
public static class CHeader
{
    public const string Prologue = """
        // lub の C API。
        // cs-lib/lub_stub.cs から tools/lub-gen が生成する (手で編集しない。再生成: dotnet run --project tools/lub-gen -- header)。
        //
        // 規則:
        //   - context を第 1 引数に取り、失敗しうる関数は LubStatus を返す。
        //     失敗は LUB_ERROR と lub_last_error() の文字列、問い合わせの対象が
        //     無いときは LUB_NOT_FOUND (last_error は書かない)。
        //   - 型は int32 / float / bool / UTF-8 の byte 列だけ。文字列は LubStr
        //     (pointer + length、NUL 終端を要求しない)。
        //   - ゲームの memory は呼び出しの間だけ借用する。runtime の memory は
        //     LubView か runtime 所有の配列で返す。配列は frame の終わりまで
        //     有効。ただし phys2d / phys3d の配列 (body の shapes や query の
        //     結果) は、同じ buffer を使う次の呼び出しまで有効で、visitor の
        //     中で呼んだ関数が返した配列は、外側の関数が戻るときに解放される
        //     (phys2d / phys3d の節の doc)。
        //     frame を跨いで生きるものは runtime 所有の keyed resource で、
        //     ゲームは key と int32 の handle だけ持つ。
        //   - 省略可能な field は has_x + x (実装が既定値を入れる)。省略可能な
        //     引数は pointer (NULL = 無し)。
        //   - version と data を取る宣言 (use_* / snd) は、version が stored と
        //     一致すれば data を読まない。version を渡して data を NULL にした
        //     宣言は、一致すれば成功 (LUB_OK)、一致する resource が無ければ
        //     (作られていない・sweep 済み・version 違い) 何も変えず
        //     LUB_NOT_FOUND。version を省いた NULL は LUB_ERROR。use_texture の
        //     px だけは NULL が空 texture の宣言なので、一致しない NULL は
        //     空 texture に作り直す。data を持つ側が data を作る前に問い合わせ
        //     られるよう、data == NULL かつ data_count == LUB_DATA_DEFERRED の
        //     呼び出しは「version が一致するときだけ成功 (LUB_OK)、一致しなければ
        //     何も変えず LUB_NOT_FOUND」。
        //   - main thread 限定。
        #pragma once
        #include <stdbool.h>
        #include <stddef.h>
        #include <stdint.h>

        #ifdef __cplusplus
        extern "C" {
        #endif

        // 共有 library (lub_shared) の export。
        // Windows で dll を作るときだけ LUB_BUILD_SHARED が立つ。
        #ifndef LUB_API
        #if defined(_WIN32) && defined(LUB_BUILD_SHARED)
        #define LUB_API __declspec(dllexport)
        #else
        #define LUB_API
        #endif
        #endif

        typedef struct LubContext LubContext;

        typedef enum LubStatus {
          LUB_OK = 0,
          LUB_ERROR = 1,
          LUB_NOT_FOUND = 2,
          LUB_BINDINGS_FULL = 3,
        } LubStatus;

        // data を後回しにする問い合わせの data_count (上の規則を参照)。
        #define LUB_DATA_DEFERRED (-1)

        // UTF-8 の byte 列。
        // ptr は len byte だけ有効で NUL 終端は要らない。
        typedef struct LubStr {
          const char *ptr;
          int32_t len;
        } LubStr;

        // runtime の memory への view。
        // frame 番号 `frame` の間だけ有効。
        typedef struct LubView {
          const uint8_t *ptr;
          int32_t len;
          int32_t frame;
        } LubView;

        // runtime 所有の resource への handle。
        // 0 = 無し。
        // resource が sweep されるまで同じ値で、hot reload を跨いでも有効。
        // sweep された handle を渡すと LUB_ERROR (同じ key で宣言し直すと別の handle になる)。
        typedef int32_t LubHandle;

        // draw / dispatch の bindings の 1 項目。
        // name は shader の reflection 名。
        // handle が 0 でなければ buffer / texture の束縛、そうでなければ values (count 個の float) の uniform 値。
        typedef struct LubBinding {
          LubStr name;
          LubHandle handle;
          const float *values;
          int32_t count;
        } LubBinding;

        // Gfx の Bindings の中身。
        // block は呼ぶ側が確保し、lub_bindings_size(scale) byte を lub_bindings_init で初期化する。
        // 解放は block を手放すだけでよい (runtime は何も確保しない)。
        // block が足りない呼び出しは何も変えずに LUB_BINDINGS_FULL を返す。
        // 呼ぶ側は scale を 2 倍にした block を init して lub_bindings_grow_into で写し、やり直す。
        // block は呼び出しの間で動かしてよい。
        typedef struct LubBindings LubBindings;
        LUB_API size_t lub_bindings_size(int32_t scale);
        LUB_API void lub_bindings_init(LubBindings *b, int32_t scale);
        LUB_API int32_t lub_bindings_scale(const LubBindings *b);
        LUB_API void lub_bindings_grow_into(LubBindings *dst, LubBindings *src);

        // 直近の LUB_ERROR の message。
        // 次の API 呼び出しまで有効。
        LUB_API const char *lub_last_error(LubContext *ctx);

        // 現在の frame 番号 (LubView.frame と比較する)。
        LUB_API int32_t lub_frame_index(LubContext *ctx);

        """;

    public const string Epilogue = """

        // lub_gfx_draw / lub_gfx_dispatch の、名前と値の組を配列で渡す形 (.NET 実行が使う)。
        LUB_API LubStatus lub_gfx_draw_items(LubContext *ctx, int32_t count,
                                             const LubBinding *bindings,
                                             int32_t bindings_count,
                                             const LubDrawOpts *opts,
                                             int32_t instances);
        LUB_API LubStatus lub_gfx_dispatch_items(LubContext *ctx, int32_t x,
                                                 int32_t y, int32_t z,
                                                 const LubBinding *bindings,
                                                 int32_t bindings_count,
                                                 const LubDispatchOpts *opts);

        #ifdef __cplusplus
        }
        #endif

        """;

    // ------------------------------------------------------------ names

    public static string TypeName(string csName) => "Lub" + csName;

    // root (Lub) の enum は namespace の段を持たない: Lub.EventKind → LubEventKind / LUB_EVENT_KIND_QUIT。
    public static string EnumName(string qualified)
    {
        var parts = qualified.Split('.');
        var kept = new List<string>();
        for (int i = 0; i < parts.Length; i++)
        {
            if (i == 0 && parts[0] == ApiModelLoader.RootNamespace) continue;
            kept.Add(parts[i]);
        }
        return "Lub" + string.Join("", kept);
    }

    public static string EnumMember(ApiEnum e, ApiEnumMember m) =>
        e.Namespace == ApiModelLoader.RootNamespace
            ? "LUB_" + LuaNames.HostConst(e.Name) + "_" + m.LuaName
            : "LUB_" + LuaNames.HostConst(e.Namespace) + "_" + LuaNames.HostConst(e.Name) + "_" + m.LuaName;

    // lub.gfx.begin_pass → lub_gfx_begin_pass、lubx.vorbis.decode → lubx_vorbis_decode。
    public static string FunctionName(ApiNamespace ns, string luaName) => ns.LuaPath.Replace(".", "_") + "_" + luaName;

    public static string Generate(ApiModel model)
    {
        var g = new CHeaderGen(model);
        return g.Run();
    }

    // ---------------------------------------------------------- helpers (Facade と共有)

    /// <summary>C の宣言の型 (struct の field)。</summary>
    public static void EmitStruct(Out sb, ApiType t)
    {
        var name = TypeName(t.Name);
        Comment(sb, t.Doc);
        sb.Write("typedef struct " + name + " {\n");
        bool any = false;
        bool hasFunc = false;
        foreach (var f in t.Fields)
        {
            if (f.Type.Kind == LubTypeKind.Func) hasFunc = true;
        }
        if (hasFunc)
        {
            sb.Write("  void *user; // callback に渡す\n");
            sb.Write("  // runtime が callback を手放すとき (次の宣言で置き換える、resource が\n");
            sb.Write("  // sweep される) に呼ぶ。NULL 可。\n");
            sb.Write("  void (*user_release)(void *user);\n");
            any = true;
        }
        foreach (var f in t.Fields)
        {
            EmitField(sb, f);
            any = true;
        }
        if (!any) sb.Write("  int32_t unused; // 空の struct を避ける\n");
        sb.Write("} " + name + ";\n\n");
    }

    private static void EmitField(Out sb, ApiField f)
    {
        var n = f.LuaName;
        var tr = f.Type;
        var doc = f.Doc != "" ? " // " + f.Doc : "";
        string line;
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
            case LubTypeKind.Double:
            case LubTypeKind.Bool:
                if (f.Optional) sb.Write("  bool has_" + n + ";\n");
                line = "  " + Scalar(tr) + " " + n + ";";
                break;
            case LubTypeKind.Enum:
                if (f.Optional) sb.Write("  bool has_" + n + ";\n");
                line = "  int32_t " + n + "; // " + EnumName(tr.Name);
                if (f.Doc != "") line += "。" + f.Doc;
                doc = "";
                break;
            case LubTypeKind.String:
                if (f.Bits)
                {
                    if (f.Optional) sb.Write("  bool has_" + n + ";\n");
                    line = "  uint64_t " + n + "; // bit mask (C# 面は hex 文字列)";
                    if (f.Doc != "") line += "。" + f.Doc;
                    doc = "";
                }
                else line = "  LubStr " + n + ";" + (f.Optional ? " // len 0 = 無し" : "");
                break;
            case LubTypeKind.Handle:
                line = "  LubHandle " + n + ";" + (f.Optional ? " // 0 = 無し" : "");
                break;
            case LubTypeKind.Keyed:
                line = "  LubStr " + n + ";";
                break;
            case LubTypeKind.View:
                line = "  LubView " + n + ";";
                break;
            case LubTypeKind.Record:
                if (f.Optional) sb.Write("  bool has_" + n + ";\n");
                line = "  " + TypeName(tr.Name) + " " + n + ";";
                break;
            case LubTypeKind.List:
            {
                var elem = tr.Elem!;
                if (elem.Kind == LubTypeKind.Array)
                {
                    if (f.ArrayLen < 0) GenError.Report(f.Name + ": List of array needs [LubArray]");
                    line = "  const " + Scalar(elem.Elem!) + " (*" + n + ")[" + f.ArrayLen + "];\n  int32_t " + n + "_count;";
                }
                else if (f.ArrayLen >= 0)
                {
                    line = "  " + ElemType(elem) + " " + n + "[" + f.ArrayLen + "];\n  int32_t " + n + "_count;";
                }
                else
                {
                    line = "  const " + ElemType(elem) + " *" + n + ";" + (f.Optional ? " // NULL = 無し" : "") + "\n  int32_t " + n + "_count;";
                }
                break;
            }
            case LubTypeKind.Array:
            {
                if (f.ArrayLen < 0) GenError.Report(f.Name + ": array field needs [LubArray]");
                if (f.Optional) sb.Write("  bool has_" + n + ";\n");
                line = "  " + Scalar(tr.Elem!) + " " + n + "[" + f.ArrayLen + "];";
                break;
            }
            case LubTypeKind.Func:
                line = "  " + FuncPointer(tr, n) + ";";
                break;
            default:
                GenError.Report(f.Name + ": unsupported field type " + tr.ToString());
                line = "";
                break;
        }
        sb.Write(line + doc + "\n");
    }

    public static string Scalar(TypeRef tr)
    {
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
                return "int32_t";
            case LubTypeKind.Double:
                return "float";
            case LubTypeKind.Bool:
                return "bool";
            case LubTypeKind.Enum:
                return "int32_t";
        }
        GenError.Report("not a scalar: " + tr.ToString());
        return "";
    }

    public static string ElemType(TypeRef elem)
    {
        switch (elem.Kind)
        {
            case LubTypeKind.Int:
            case LubTypeKind.Double:
            case LubTypeKind.Bool:
            case LubTypeKind.Enum:
                return Scalar(elem);
            case LubTypeKind.String:
                return "LubStr";
            case LubTypeKind.Handle:
                return "LubHandle";
            case LubTypeKind.Record:
                return TypeName(elem.Name);
        }
        GenError.Report("unsupported element type " + elem.ToString());
        return "";
    }

    public static string FuncReturn(TypeRef ret)
    {
        switch (ret.Kind)
        {
            case LubTypeKind.Void:
                return "void";
            case LubTypeKind.Bool:
                return "bool";
            case LubTypeKind.Double:
                return "float";
            case LubTypeKind.Int:
                return "int32_t";
        }
        GenError.Report("unsupported callback return " + ret.ToString());
        return "";
    }

    public static string FuncParams(TypeRef fn)
    {
        var parts = new List<string>();
        parts.Add("void *user");
        int i = 0;
        foreach (var p in fn.FuncParams!)
        {
            string pn = Text.Sub("abcdefghijklmnopqrstuvwxyz", i, 1);
            if (p.Kind == LubTypeKind.Record) parts.Add("const " + TypeName(p.Name) + " *" + pn);
            else if (p.IsScalar) parts.Add(Scalar(p) + " " + pn);
            else GenError.Report("unsupported callback param " + p.ToString());
            i++;
        }
        return string.Join(", ", parts);
    }

    public static string FuncPointer(TypeRef fn, string name) => FuncReturn(fn.FuncReturn!) + " (*" + name + ")(" + FuncParams(fn) + ")";

    public static void Comment(Out sb, string doc)
    {
        if (doc == "") return;
        foreach (var line in WrapText(doc, 76)) sb.Write("// " + line + "\n");
    }

    // 表示幅 (CJK は 2) で折り返す。
    // 折り返し位置は空白の後か、CJK の文字 (句読点・閉じ括弧を除く) の前。
    // 空白は原文のものだけ。
    // 位置は C# 版と同じく UTF-16 の単位で数える (BMP の外の文字は 2 単位、幅は 1 ずつ)。
    public static List<string> WrapText(string text, int width)
    {
        var units = new List<int>();
        foreach (var r in text.EnumerateRunes())
        {
            int v = r.Value;
            if (v >= 0x10000)
            {
                units.Add(0xD800);
                units.Add(0xDC00);
            }
            else units.Add(v);
        }
        var runes = new List<string>();
        foreach (var r in text.EnumerateRunes())
        {
            runes.Add(r.ToString());
            if (r.Value >= 0x10000) runes.Add("");
        }
        var result = new List<string>();
        int start = 0;
        int len = units.Count;
        while (start < len)
        {
            int w = 0;
            int lastBreak = -1;
            int i = start;
            for (; i < len; i++)
            {
                int c = units[i];
                int cw = IsWide(c) ? 2 : 1;
                if (w + cw > width && lastBreak > start) break;
                if (c == ' ') lastBreak = i + 1;
                else if (IsWide(c) && !IsWidePunct(c) && i > start && units[i - 1] != ' ') lastBreak = i;
                w += cw;
            }
            int end = i >= len ? len : lastBreak;
            var piece = new List<string>();
            for (int k = start; k < end; k++) piece.Add(runes[k]);
            result.Add(string.Join("", piece).Trim());
            start = end;
            while (start < len && units[start] == ' ') start++;
        }
        return result;
    }

    private static bool IsWide(int c) => c > 0x2E7F && !(c >= 0xD800 && c <= 0xDFFF);

    private static bool IsWidePunct(int c)
    {
        foreach (var r in "。、」』）］〕〉》・ー：；！？".EnumerateRunes())
        {
            if (r.Value == c) return true;
        }
        return false;
    }

    // UTF-16 の長さ (C# 版の string.Length)
    public static int Utf16Length(string s)
    {
        int n = 0;
        foreach (var r in s.EnumerateRunes()) n += r.Value >= 0x10000 ? 2 : 1;
        return n;
    }

    // clang-format 風に 80 桁で引数を折り返す。
    public static string Wrap(string head, string rest)
    {
        int headLen = Utf16Length(head);
        var indent = Text.Repeat(" ", headLen);
        var parts = rest.Split(", ");
        var sb = new List<string>();
        sb.Add(head);
        int col = headLen;
        for (int i = 0; i < parts.Length; i++)
        {
            var piece = parts[i] + (i + 1 < parts.Length ? "," : "");
            int pl = Utf16Length(piece);
            if (col + pl + (i > 0 ? 1 : 0) > 80 && i > 0)
            {
                sb.Add("\n");
                sb.Add(indent);
                col = headLen;
            }
            else if (i > 0)
            {
                sb.Add(" ");
                col++;
            }
            sb.Add(piece);
            col += pl;
        }
        return string.Join("", sb);
    }
}

internal sealed class CHeaderGen
{
    private ApiModel model;
    private Out sb = new Out();
    private Dictionary<string, ApiType> byName = new Dictionary<string, ApiType>();
    private HashSet<string> done = new HashSet<string>();
    private HashSet<string> visiting = new HashSet<string>();

    public CHeaderGen(ApiModel model)
    {
        this.model = model;
    }

    public string Run()
    {
        sb.Write(CHeader.Prologue);
        EmitEnums();
        EmitConsts();
        EmitTypes();
        foreach (var ns in model.Namespaces) EmitNamespaceFunctions(ns);
        sb.Write(CHeader.Epilogue);
        return sb.ToString();
    }

    private static string FnTypedefName(ApiNamespace ns, ApiFunction f, ApiParam p) =>
        "Lub" + ns.Name + f.Name + Text.UpperFirst(p.Name) + "Fn";

    // ------------------------------------------------------------ enums

    private void EmitEnums()
    {
        foreach (var ns in model.Namespaces)
        {
            foreach (var e in ns.Enums)
            {
                CHeader.Comment(sb, e.Doc);
                sb.Write("typedef enum " + CHeader.EnumName(ns.Name + "." + e.Name) + " {\n");
                foreach (var m in e.Members)
                {
                    sb.Write("  " + CHeader.EnumMember(e, m) + " = " + m.Value + ",");
                    if (m.Doc != "") sb.Write(" // " + m.Doc);
                    sb.Write("\n");
                }
                sb.Write("} " + CHeader.EnumName(ns.Name + "." + e.Name) + ";\n\n");
            }
        }
    }

    private void EmitConsts()
    {
        foreach (var ns in model.Namespaces)
        {
            foreach (var c in ns.Consts)
            {
                if (c.Value.Kind != ValueKind.Int) continue;
                CHeader.Comment(sb, c.Doc);
                sb.Write("#define LUB_" + LuaNames.HostConst(ns.Name) + "_" + c.LuaName + " " + c.Value.I + "\n\n");
            }
        }
    }

    // ------------------------------------------------------------ types

    private void EmitTypes()
    {
        var records = new List<ApiType>();
        foreach (var t in model.Types)
        {
            if (t.Kind == "record") records.Add(t);
        }
        foreach (var t in records) byName[t.Name] = t;
        foreach (var t in records) Visit(t);
    }

    private void Visit(ApiType t)
    {
        if (done.Contains(t.Name)) return;
        if (!visiting.Add(t.Name))
        {
            GenError.Report("type cycle at " + t.Name);
            return;
        }
        foreach (var dep in Deps(t))
        {
            ApiType? d;
            if (byName.TryGetValue(dep, out d)) Visit(d!);
        }
        visiting.Remove(t.Name);
        done.Add(t.Name);
        CHeader.EmitStruct(sb, t);
    }

    private static List<string> Deps(ApiType t)
    {
        var r = new List<string>();
        foreach (var f in t.Fields) Deps(f.Type, r);
        return r;
    }

    private static void Deps(TypeRef tr, List<string> r)
    {
        switch (tr.Kind)
        {
            case LubTypeKind.Record:
                r.Add(tr.Name);
                break;
            case LubTypeKind.List:
            case LubTypeKind.Array:
                Deps(tr.Elem!, r);
                break;
            case LubTypeKind.Func:
                foreach (var p in tr.FuncParams!) Deps(p, r);
                break;
        }
    }

    // -------------------------------------------------------- functions

    private void EmitNamespaceFunctions(ApiNamespace ns)
    {
        string label = ns.LuaPath == "lub" ? "core" : ns.LuaRoot == "lub" ? Text.From(ns.LuaPath, Text.Len("lub.")) : ns.LuaPath;
        int dashes = 70 - CHeader.Utf16Length(label);
        if (dashes < 1) dashes = 1;
        sb.Write("// " + Text.Repeat("-", dashes) + " " + label + "\n");
        if (ns.Doc != "") CHeader.Comment(sb, ns.Doc);
        sb.Write("\n");
        foreach (var f in ns.StaticFields)
        {
            CHeader.Comment(sb, f.Doc);
            sb.Write("LUB_API " + ReturnCType(f.Type) + " " + CHeader.FunctionName(ns, f.LuaName) + "(LubContext *ctx);\n\n");
        }
        foreach (var f in ns.Functions)
        {
            if (f.NoC) continue;
            foreach (var p in f.Params)
            {
                if (p.Type.Kind != LubTypeKind.Func) continue;
                var fn = p.Type;
                sb.Write("typedef " + CHeader.FuncReturn(fn.FuncReturn!) + " (*" + FnTypedefName(ns, f, p) + ")(" + CHeader.FuncParams(fn) + ");\n");
            }
            CHeader.Comment(sb, f.Doc);
            sb.Write(Prototype(ns, f) + "\n\n");
        }
    }

    private static string ReturnCType(TypeRef tr)
    {
        switch (tr.Kind)
        {
            case LubTypeKind.Void:
                return "void";
            case LubTypeKind.Int:
                return "int32_t";
            case LubTypeKind.Double:
                return "float";
            case LubTypeKind.Bool:
                return "bool";
            case LubTypeKind.Enum:
                return "int32_t";
            case LubTypeKind.Handle:
                return "LubHandle";
        }
        GenError.Report("unsupported direct return " + tr.ToString());
        return "";
    }

    private string Prototype(ApiNamespace ns, ApiFunction f)
    {
        var args = new List<string>();
        args.Add("LubContext *ctx");
        foreach (var p in f.Params)
        {
            if (!p.IsOut && !p.IsCount) InParam(ns, f, p, args);
        }
        foreach (var p in f.Params)
        {
            if (!p.IsOut) continue;
            OutParam(p.Type, p.LuaName, args);
            if (p.Type.Kind == LubTypeKind.Record && p.Type.Nullable) args.Add("bool *has_" + p.LuaName);
        }
        string ret;
        var r = f.Return;
        if (f.NoFail)
        {
            if (r.Kind == LubTypeKind.Void || r.Kind == LubTypeKind.Int || r.Kind == LubTypeKind.Double || r.Kind == LubTypeKind.Bool
                || r.Kind == LubTypeKind.Enum || r.Kind == LubTypeKind.Handle)
                ret = ReturnCType(r);
            else
            {
                ret = "void";
                OutParam(r, "out", args);
            }
        }
        else
        {
            ret = "LubStatus";
            if (r.Kind != LubTypeKind.Void)
            {
                OutParam(r, "out", args);
                if (r.Nullable && (r.IsScalar || (r.Kind == LubTypeKind.Record && f.Maybe))) args.Add("bool *has");
            }
        }
        return CHeader.Wrap("LUB_API " + ret + " " + CHeader.FunctionName(ns, f.LuaName) + "(", string.Join(", ", args) + ");");
    }

    private static void InParam(ApiNamespace ns, ApiFunction f, ApiParam p, List<string> o)
    {
        var n = p.LuaName;
        var tr = p.Type;
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
            case LubTypeKind.Double:
            case LubTypeKind.Bool:
            case LubTypeKind.Enum:
                o.Add(tr.Nullable ? "const " + CHeader.Scalar(tr) + " *" + n : CHeader.Scalar(tr) + " " + n);
                return;
            case LubTypeKind.String:
                o.Add("LubStr " + n);
                return;
            case LubTypeKind.Handle:
                o.Add("LubHandle " + n);
                return;
            case LubTypeKind.Keyed:
                o.Add("LubStr " + n);
                return;
            case LubTypeKind.View:
                o.Add("const uint8_t *" + n);
                o.Add("int32_t " + n + "_len");
                return;
            case LubTypeKind.Record:
                o.Add("const " + CHeader.TypeName(tr.Name) + " *" + n);
                return;
            case LubTypeKind.List:
                o.Add("const " + CHeader.ElemType(tr.Elem!) + " *" + n);
                o.Add("int32_t " + n + "_count");
                return;
            case LubTypeKind.Func:
                o.Add(FnTypedefName(ns, f, p) + " " + n);
                o.Add("void *" + n + "_user");
                return;
            case LubTypeKind.Native:
                o.Add(CHeader.TypeName(tr.Name) + " *" + n);
                return;
            case LubTypeKind.Array:
                if (tr.Elem!.Kind == LubTypeKind.Double)
                {
                    o.Add("const float *" + n);
                    o.Add("int32_t " + n + "_count");
                    return;
                }
                break;
        }
        GenError.Report(ns.Name + "." + f.Name + ": unsupported parameter " + p.Name + ": " + tr.ToString());
    }

    private static void OutParam(TypeRef tr, string n, List<string> o)
    {
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
            case LubTypeKind.Double:
            case LubTypeKind.Bool:
            case LubTypeKind.Enum:
                o.Add(CHeader.Scalar(tr) + " *" + n);
                return;
            case LubTypeKind.String:
                o.Add("LubStr *" + n);
                return;
            case LubTypeKind.Handle:
                o.Add("LubHandle *" + n);
                return;
            case LubTypeKind.View:
                o.Add("LubView *" + n);
                return;
            case LubTypeKind.Record:
                o.Add(CHeader.TypeName(tr.Name) + " *" + n);
                return;
            case LubTypeKind.List:
                o.Add("const " + CHeader.ElemType(tr.Elem!) + " **" + n);
                o.Add("int32_t *" + n + "_count");
                return;
        }
        GenError.Report("unsupported out / return type " + tr.ToString());
    }
}
