using System.Collections.Generic;

namespace LubGen;

/// <summary>stub から .NET 実行の facade (dotnet/Lub/Lub.g.cs) を導く。
/// stub と同じ C# の面 (class / enum / static メソッド) を持ち、中身は C API (CHeader が導いた形) への詰め替えと P/Invoke。
/// Lua binding (LuaBinding) と同じ規則を C# で書いたもので、土台 (arena、例外、callback の箱、host) は手書きの dotnet/Lub/LubRuntime.cs にある。
/// </summary>
public static class Facade
{
    public static string Generate(ApiModel model) => new FacadeGen(model).Run();

    /// <summary>namespace Lubx の面 (dotnet/Lub/Lubx.g.cs)。
    /// P/Invoke の宣言と詰め替えの土台は Lub.g.cs の LubNative と LubRuntime を使う (同じ assembly)。
    /// </summary>
    public static string GenerateLubx(ApiModel model) => new FacadeGen(model).RunLubx();
}

internal sealed class FacadeGen
{
    private ApiModel model;
    private Out sb = new Out();
    private Dictionary<string, ApiType> types = new Dictionary<string, ApiType>();
    private Dictionary<string, ApiEnum> enums = new Dictionary<string, ApiEnum>();

    public FacadeGen(ApiModel model)
    {
        this.model = model;
        foreach (var t in model.Types) types[t.Name] = t;
        foreach (var n in model.Namespaces)
        {
            foreach (var e in n.Enums) enums[n.Name + "." + e.Name] = e;
        }
    }

    private const string LubHead = """
        // lub の .NET 実行の facade。
        // cs-lib/lub_stub.cs から tools/lub-gen が生成する (手で編集しない。再生成: scripts/gen-api.sh)。
        // stub と同じ面を持ち、中身は C API (include/lub/lub_api.h) への詰め替えと P/Invoke。
        // 土台は LubRuntime.cs。
        #nullable enable
        #pragma warning disable CS0649, CS8618, CS0169, CS0414, IDE1006
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;

        namespace Lub;


        """;

    private const string LubxHead = """
        // lubx が使う C の実装 (src/lubx/) の .NET 実行の facade。
        // cs-lib/lub_stub.cs の namespace Lubx から tools/lub-gen が生成する (手で編集しない。再生成: scripts/gen-api.sh)。
        // P/Invoke の宣言は Lub.g.cs の LubNative にある。
        #nullable enable
        #pragma warning disable CS0649, CS8618, CS0169, CS0414, IDE1006
        using System;
        using System.Collections.Generic;
        using Lub;

        namespace Lubx;


        """;

    public string Run()
    {
        sb.Write(LubHead);
        foreach (var t in model.Types)
        {
            if (t.Kind != "record" && t.Kind != "native") EmitOpaque(t);
        }
        foreach (var t in model.Types)
        {
            if (t.Kind == "record") EmitRecord(t);
        }
        EmitApi("lub");
        EmitNative();
        return sb.ToString();
    }

    public string RunLubx()
    {
        sb.Write(LubxHead);
        EmitApi("lubx");
        return sb.ToString();
    }

    // ------------------------------------------------------------ names

    private static string C(string csName) => "Lub" + csName;

    private static string NativeFn(ApiNamespace ns, string luaName) => CHeader.FunctionName(ns, luaName);

    private string EnumCs(TypeRef tr)
    {
        // stub の enum は namespace class の入れ子: Gfx.LoadAction / Lub.EventKind
        ApiEnum? e;
        if (!enums.TryGetValue(tr.Name, out e))
        {
            GenError.Report("unknown enum " + tr.Name);
            return tr.Name;
        }
        return e!.Namespace == ApiModelLoader.RootNamespace ? "Lub." + e.Name : "Lub." + tr.Name;
    }

    private static string Q(TypeRef tr) => tr.Nullable ? "?" : "";

    // 公開面の型 (stub と同じ表記)。
    // root の enum は Lub. 付き。
    private string Cs(TypeRef tr)
    {
        if (tr.Kind == LubTypeKind.Enum) return EnumCs(tr) + Q(tr);
        if (tr.Kind == LubTypeKind.List) return "List<" + Cs(tr.Elem!) + ">" + Q(tr);
        if (tr.Kind == LubTypeKind.Array) return Cs(tr.Elem!) + "[]" + Q(tr);
        if (tr.Kind == LubTypeKind.Func) return FuncCs(tr) + Q(tr);
        return CsNames.Type(tr);
    }

    private string FuncCs(TypeRef tr)
    {
        var ps = new List<string>();
        foreach (var p in tr.FuncParams!) ps.Add(Cs(p));
        var r = tr.FuncReturn!;
        if (r.Kind == LubTypeKind.Void) return ps.Count == 0 ? "Action" : "Action<" + string.Join(", ", ps) + ">";
        ps.Add(Cs(r));
        return "Func<" + string.Join(", ", ps) + ">";
    }

    // native 側の struct field / 引数の型
    private static string NScalar(TypeRef tr)
    {
        if (tr.Kind == LubTypeKind.Int || tr.Kind == LubTypeKind.Enum) return "int";
        if (tr.Kind == LubTypeKind.Double) return "float";
        if (tr.Kind == LubTypeKind.Bool) return "bool";
        GenError.Report("not a scalar: " + tr.ToString());
        return "";
    }

    private static string NElem(TypeRef elem)
    {
        if (elem.IsScalar) return NScalar(elem);
        if (elem.Kind == LubTypeKind.String) return "LubStr";
        if (elem.Kind == LubTypeKind.Handle) return "int";
        if (elem.Kind == LubTypeKind.Record) return C(elem.Name);
        GenError.Report("unsupported element type " + elem.ToString());
        return "";
    }

    private static string FnPtr(TypeRef fn)
    {
        var ps = new List<string>();
        ps.Add("void*");
        foreach (var p in fn.FuncParams!)
        {
            if (p.Kind == LubTypeKind.Record) ps.Add(C(p.Name) + "*");
            else if (p.Kind == LubTypeKind.Int || p.Kind == LubTypeKind.Enum) ps.Add("int");
            else if (p.Kind == LubTypeKind.Double) ps.Add("float");
            else if (p.Kind == LubTypeKind.Bool) ps.Add("byte");
            else GenError.Report("unsupported callback param " + p.ToString());
        }
        var r = fn.FuncReturn!;
        if (r.Kind == LubTypeKind.Void) ps.Add("void");
        else if (r.Kind == LubTypeKind.Bool) ps.Add("byte");
        else if (r.Kind == LubTypeKind.Double) ps.Add("float");
        else if (r.Kind == LubTypeKind.Int) ps.Add("int");
        else GenError.Report("unsupported callback return " + r.ToString());
        return "delegate* unmanaged[Cdecl]<" + string.Join(", ", ps) + ">";
    }

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

    private static bool HasFuncs(ApiType t) => Funcs(t).Count > 0;

    private static string Doc(string doc) => doc != "" ? "    /// <summary>" + doc + "</summary>\n" : "";

    private static bool HasNativeParam(ApiFunction f)
    {
        foreach (var p in f.Params)
        {
            if (p.Type.Kind == LubTypeKind.Native) return true;
        }
        return false;
    }

    // ------------------------------------------------------ opaque types

    private void EmitOpaque(ApiType t)
    {
        sb.Write(Doc(t.Doc).Replace("    ///", "///"));
        if (t.Kind == "handle")
        {
            sb.Write("public sealed class " + t.Name + "\n{\n");
            sb.Write("    internal readonly int H;\n");
            sb.Write("    internal " + t.Name + "(int h) { H = h; }\n");
            foreach (var f in t.Fields)
            {
                if (f.Name == "Version" && f.Type.Kind == LubTypeKind.Int) sb.Write("    public int Version => LubRuntime.ResourceVersion(H);\n");
                else GenError.Report(t.Name + "." + f.Name + ": handle field is not supported by the facade");
            }
            sb.Write("}\n\n");
        }
        else if (t.Kind == "view")
        {
            sb.Write("public sealed unsafe class " + t.Name + "\n{\n");
            sb.Write("    internal readonly byte* Ptr;\n    internal readonly int Frame;\n");
            sb.Write("    public int Length;\n");
            sb.Write("    internal " + t.Name + "(byte* ptr, int len, int frame) { Ptr = ptr; Length = len; Frame = frame; }\n");
            sb.Write("    /// <summary>この frame の間だけ有効な view。</summary>\n");
            sb.Write("    public ReadOnlySpan<byte> AsSpan() { LubRuntime.CheckView(Frame); return new ReadOnlySpan<byte>(Ptr, Length); }\n");
            sb.Write("    public byte[] ToArray() => AsSpan().ToArray();\n");
            foreach (var m in t.Methods)
            {
                if (m.Name == "Get" && m.Params.Count == 1)
                    sb.Write("    public int " + m.Name + "(int " + m.Params[0].Name + ") => AsSpan()[" + m.Params[0].Name + "];\n");
                else GenError.Report(t.Name + "." + m.Name + ": view method is not supported by the facade");
            }
            sb.Write("}\n\n");
        }
        else if (t.Kind == "keyed")
        {
            sb.Write("public sealed class " + t.Name + "\n{\n");
            sb.Write("    public readonly string Key;\n");
            sb.Write("    public " + t.Name + "(string key) { Key = key; }\n");
            sb.Write("}\n\n");
        }
        else GenError.Report(t.Name + ": unknown kind " + t.Kind);
    }

    // ---------------------------------------------------------- records

    private void EmitRecord(ApiType t)
    {
        sb.Write(Doc(t.Doc).Replace("    ///", "///"));
        sb.Write("public class " + t.Name + (t.Base != null ? " : " + t.Base : "") + "\n{\n");
        foreach (var f in t.Fields)
        {
            sb.Write(Doc(f.Doc));
            string init = f.Type.Kind == LubTypeKind.List && !f.Type.Nullable ? " = new " + Cs(f.Type) + "()" : "";
            sb.Write("    public " + Cs(f.Type) + " " + f.Name + init + ";\n");
        }
        sb.Write("}\n\n");
    }

    // -------------------------------------------------------------- api

    private void EmitApi(string luaRoot)
    {
        // stub と同じく namespace Lub (または Lubx) 直下に置く (root の enum は namespace 直下、各 static class は partial: App は手書きの LubHost.cs の Run と合わさる)
        foreach (var ns in model.Namespaces)
        {
            if (ns.LuaRoot != luaRoot) continue;
            bool root = ns.Name == ApiModelLoader.RootNamespace;
            string ind = root ? "" : "    ";
            if (!root)
            {
                sb.Write(Doc(ns.Doc).Replace("    ///", "///"));
                sb.Write("public static unsafe partial class " + ns.Name + "\n{\n");
            }
            foreach (var e in ns.Enums)
            {
                if (e.Doc != "") sb.Write(ind + "/// <summary>" + e.Doc + "</summary>\n");
                sb.Write(ind + "public enum " + e.Name + "\n" + ind + "{\n");
                foreach (var m in e.Members) sb.Write(ind + "    " + m.Name + " = " + m.Value + ",\n");
                sb.Write(ind + "}\n\n");
            }
            foreach (var c in ns.Consts)
                sb.Write(ind + "public const " + CsNames.ConstType(c.Value) + " " + c.Name + " = " + CsNames.Const(c.Value) + ";\n");
            foreach (var f in ns.StaticFields)
            {
                if (f.Doc != "") sb.Write(ind + "/// <summary>" + f.Doc + "</summary>\n");
                string call = "LubNative." + NativeFn(ns, f.LuaName) + "(LubRuntime.Ctx)";
                sb.Write(ind + "public static " + Cs(f.Type) + " " + f.Name + " => " + FromDirect(f.Type, call) + ";\n\n");
            }
            foreach (var f in ns.Functions) EmitFunction(ns, f, ind);
            if (!root) sb.Write("}\n\n");
        }
    }

    // 直接値 (NoFail の戻り値、static field) を公開型に写す
    private string FromDirect(TypeRef tr, string expr)
    {
        switch (tr.Kind)
        {
            case LubTypeKind.Void:
            case LubTypeKind.Int:
            case LubTypeKind.Double:
                return expr;
            case LubTypeKind.Bool:
                return "(" + expr + " != 0)";
            case LubTypeKind.Enum:
                return "(" + EnumCs(tr) + ")" + expr;
            case LubTypeKind.Handle:
                return "LubNative.H_" + tr.Name + "(" + expr + ")" + (tr.Nullable ? "" : "!");
        }
        GenError.Report("unsupported direct value " + tr.ToString());
        return "";
    }

    private static bool IsDirect(TypeRef r) =>
        r.Kind == LubTypeKind.Void || r.Kind == LubTypeKind.Int || r.Kind == LubTypeKind.Double || r.Kind == LubTypeKind.Bool
        || r.Kind == LubTypeKind.Enum || r.Kind == LubTypeKind.Handle;

    private void EmitFunction(ApiNamespace ns, ApiFunction f, string ind)
    {
        var ps = new List<string>();
        foreach (var p in f.Params)
            ps.Add((p.IsOut ? "out " : "") + Cs(p.Type) + " " + p.Name + (p.Optional ? " = " + (p.DefaultLiteral ?? "null") : ""));
        if (f.Doc != "") sb.Write(ind + "/// <summary>" + f.Doc + "</summary>\n");
        sb.Write(ind + "public static " + Cs(f.Return) + " " + f.Name + "(" + string.Join(", ", ps) + ")\n" + ind + "{\n");
        var b = new Out();
        string i2 = ind + "    ";
        if (f.NoC)
        {
            if (f.Return.Kind == LubTypeKind.Keyed && f.Params.Count == 1 && f.Params[0].Type.Kind == LubTypeKind.String)
                b.Write(i2 + "return new " + f.Return.Name + "(" + f.Params[0].Name + ");\n");
            else GenError.Report(ns.Name + "." + f.Name + ": [LubNoC] needs a hand-written facade");
            sb.Write(b.ToString());
            sb.Write(ind + "}\n\n");
            return;
        }
        // .NET の Bindings は managed の配列だけで持つ (LubRuntime.cs)。
        // Bindings* は C を呼ばず、Draw / Dispatch は arena に写して配列を受ける C API (_items) を呼ぶ。
        if (f.Params.Count > 0 && f.Params[0].Type.Kind == LubTypeKind.Native)
        {
            var margs = new List<string>();
            for (int k = 1; k < f.Params.Count; k++)
            {
                var p = f.Params[k];
                margs.Add(p.Type.Kind == LubTypeKind.Handle ? p.Name + ".H" : p.Name);
            }
            b.Write(i2 + f.Params[0].Name + "." + Text.From(f.Name, Text.Len("Bindings")) + "(" + string.Join(", ", margs) + ");\n");
            sb.Write(b.ToString());
            sb.Write(ind + "}\n\n");
            return;
        }
        b.Write(i2 + "var a = LubRuntime.Arena.Begin();\n" + i2 + "try\n" + i2 + "{\n");
        string i3 = i2 + "    ";
        var args = new List<string>();
        args.Add("LubRuntime.Ctx");
        var post = new Out(); // 呼び出し後 (out の変換)
        foreach (var p in f.Params)
        {
            if (!p.IsOut) InArg(f, p, b, i3, args);
        }
        foreach (var p in f.Params)
        {
            if (!p.IsOut) continue;
            OutArg(p.Type, p.LuaName, b, i3, args);
            if (p.Type.Kind == LubTypeKind.Record && p.Type.Nullable)
            {
                b.Write(i3 + "bool has_" + p.LuaName + " = false;\n");
                args.Add("&has_" + p.LuaName);
            }
        }
        var r = f.Return;
        string fn = "LubNative." + NativeFn(ns, f.LuaName);
        if (HasNativeParam(f)) fn += "_items";
        string retExpr;
        if (f.NoFail)
        {
            if (IsDirect(r))
            {
                string call = fn + "(" + string.Join(", ", args) + ")";
                if (r.Kind == LubTypeKind.Void) b.Write(i3 + call + ";\n");
                else b.Write(i3 + "var r = " + call + ";\n");
                retExpr = r.Kind == LubTypeKind.Void ? "" : FromDirect(r, "r");
            }
            else
            {
                OutArg(r, "out", b, i3, args);
                b.Write(i3 + fn + "(" + string.Join(", ", args) + ");\n");
                retExpr = OutValue(r, "out", null);
            }
            foreach (var p in f.Params)
            {
                if (p.IsOut) post.Write(OutAssign(p, i3, null));
            }
        }
        else
        {
            bool hasRet = r.Kind != LubTypeKind.Void;
            string? has = null;
            if (hasRet)
            {
                OutArg(r, "out", b, i3, args);
                if (r.Nullable && (r.IsScalar || (r.Kind == LubTypeKind.Record && f.Maybe)))
                {
                    b.Write(i3 + "bool has = false;\n");
                    args.Add("&has");
                    has = "has";
                }
            }
            b.Write(i3 + "var st = " + fn + "(" + string.Join(", ", args) + ");\n");
            // NOT_FOUND: 戻り値が null を許す (か戻り値が無い) なら null / 既定値、そうでなければ例外
            var notFound = new Out();
            notFound.Write(i3 + "if (st == LubNative.LUB_NOT_FOUND)\n" + i3 + "{\n");
            foreach (var p in f.Params)
            {
                if (p.IsOut) notFound.Write(i3 + "    " + p.Name + " = default!;\n");
            }
            if (!hasRet) notFound.Write(i3 + "    return;\n");
            else if (r.Nullable) notFound.Write(i3 + "    return null;\n");
            else if (r.Kind == LubTypeKind.Bool) notFound.Write(i3 + "    return false;\n");
            else notFound.Write(i3 + "    throw new LubException(\"" + ns.Name + "." + f.Name + ": not found\");\n");
            notFound.Write(i3 + "}\n");
            b.Write(notFound.ToString());
            b.Write(i3 + "LubRuntime.Check(st);\n");
            foreach (var p in f.Params)
            {
                if (p.IsOut) post.Write(OutAssign(p, i3, p.Type.Kind == LubTypeKind.Record && p.Type.Nullable ? "has_" + p.LuaName : null));
            }
            retExpr = hasRet ? OutValue(r, "out", has) : "";
        }
        b.Write(post.ToString());
        if (retExpr != "") b.Write(i3 + "return " + retExpr + ";\n");
        b.Write(i2 + "}\n" + i2 + "finally\n" + i2 + "{\n" + i3 + "a.End();\n" + i2 + "}\n");
        sb.Write(b.ToString());
        sb.Write(ind + "}\n\n");
    }

    // 入力引数を native の引数列に写す。
    // 前処理は b に書く。
    private void InArg(ApiFunction f, ApiParam p, Out b, string ind, List<string> o)
    {
        if (p.IsCount) return;
        string n = p.Name;
        var tr = p.Type;
        string v = "_" + p.LuaName;
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
            case LubTypeKind.Double:
            case LubTypeKind.Bool:
            case LubTypeKind.Enum:
                if (tr.Nullable)
                {
                    b.Write(ind + NScalar(tr) + " " + v + " = " + (tr.Kind == LubTypeKind.Enum ? "(int)" : "") + "(" + n + " ?? default);\n");
                    o.Add(n + ".HasValue ? &" + v + " : null");
                }
                else if (tr.Kind == LubTypeKind.Enum) o.Add("(int)" + n);
                else if (tr.Kind == LubTypeKind.Bool) o.Add("(byte)(" + n + " ? 1 : 0)");
                else o.Add(n);
                return;
            case LubTypeKind.String:
                o.Add("a.Str(" + n + ")");
                return;
            case LubTypeKind.Handle:
                o.Add(tr.Nullable ? "(" + n + "?.H ?? 0)" : n + ".H");
                return;
            case LubTypeKind.Keyed:
                o.Add("a.Str(" + n + ".Key)");
                return;
            case LubTypeKind.View:
                if (tr.Nullable)
                {
                    b.Write(ind + "if (" + n + " != null) LubRuntime.CheckView(" + n + ".Frame);\n");
                    b.Write(ind + "byte* " + v + " = " + n + " == null ? null : " + n + ".Ptr;\n");
                    o.Add(v);
                    o.Add(n + "?.Length ?? 0");
                }
                else
                {
                    b.Write(ind + "LubRuntime.CheckView(" + n + ".Frame);\n");
                    o.Add(n + ".Ptr");
                    o.Add(n + ".Length");
                }
                return;
            case LubTypeKind.Record:
                b.Write(ind + "LubNative." + C(tr.Name) + "* " + v + " = null;\n");
                b.Write(ind + "if (" + n + " != null)\n" + ind + "{\n" + ind + "    " + v + " = a.Alloc<LubNative." + C(tr.Name) + ">(1);\n"
                    + ind + "    LubNative.To_" + C(tr.Name) + "(" + n + ", a, " + v + ");\n" + ind + "}\n");
                o.Add(v);
                return;
            case LubTypeKind.List:
                b.Write(ind + "int " + v + "_n = 0;\n");
                b.Write(ind + "var " + v + " = " + ListToNative(tr, n, v + "_n", p.ArrayLen) + ";\n");
                o.Add(v);
                o.Add(v + "_n");
                return;
            case LubTypeKind.Array:
                if (tr.Elem!.Kind == LubTypeKind.Double)
                {
                    int at = f.Params.IndexOf(p);
                    var count = at + 1 < f.Params.Count ? f.Params[at + 1] : null;
                    if (count == null || !count.IsCount)
                    {
                        GenError.Report(f.Name + ": " + p.Name + " needs a [LubCount] parameter next to it");
                        return;
                    }
                    b.Write(ind + "var " + v + " = a.PinFloats(" + n + ", " + count.Name + ");\n");
                    o.Add(v);
                    o.Add(count.Name);
                    return;
                }
                break;
            case LubTypeKind.Func:
                b.Write(ind + "void* " + v + "_user = " + n + " == null ? null : a.Callback(" + n + ");\n");
                o.Add(n + " == null ? null : &LubNative.Tramp_" + TrampName(f, p));
                o.Add(v + "_user");
                return;
            case LubTypeKind.Native:
                b.Write(ind + "int " + v + "_n = 0;\n" + ind + "var " + v + " = " + n + ".ToNative(a, out " + v + "_n);\n");
                o.Add(v);
                o.Add(v + "_n");
                return;
        }
        GenError.Report(f.Name + ": unsupported parameter " + p.Name + ": " + tr.ToString());
    }

    private static string TrampName(ApiFunction f, ApiParam p) => "fn_" + f.Name + "_" + p.Name;

    // List<T> を arena に写す式 (count は countVar に書く)
    private string ListToNative(TypeRef tr, string expr, string countVar, int arrayLen)
    {
        var elem = tr.Elem!;
        switch (elem.Kind)
        {
            case LubTypeKind.Double:
                return "a.Floats(" + expr + ", out " + countVar + ")";
            case LubTypeKind.Int:
            case LubTypeKind.Enum:
                return "a.Ints(" + expr + ", out " + countVar + ")";
            case LubTypeKind.Bool:
                return "a.Bools(" + expr + ", out " + countVar + ")";
            case LubTypeKind.String:
                return "a.Strs(" + expr + ", out " + countVar + ")";
            case LubTypeKind.Handle:
                return "a.Handles(" + expr + ", out " + countVar + ", static h => h.H)";
            case LubTypeKind.Array:
                if (arrayLen < 0) GenError.Report("List of array needs [LubArray]");
                return "a.FloatRows(" + expr + ", out " + countVar + ", " + arrayLen + ")";
            case LubTypeKind.Record:
                return "a.Records<" + elem.Name + ", LubNative." + C(elem.Name) + ">(" + expr + ", out " + countVar + ", &LubNative.To_" + C(elem.Name) + ")";
        }
        GenError.Report("unsupported list element " + elem.ToString());
        return "";
    }

    // out / 戻り値の受け皿を宣言し、native の引数列を返す
    private void OutArg(TypeRef tr, string n, Out b, string ind, List<string> o)
    {
        string v = "o_" + n;
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
            case LubTypeKind.Double:
            case LubTypeKind.Bool:
            case LubTypeKind.Enum:
                b.Write(ind + NScalar(tr) + " " + v + " = default;\n");
                o.Add("&" + v);
                return;
            case LubTypeKind.String:
                b.Write(ind + "LubNative.LubStr " + v + " = default;\n");
                o.Add("&" + v);
                return;
            case LubTypeKind.Handle:
                b.Write(ind + "int " + v + " = 0;\n");
                o.Add("&" + v);
                return;
            case LubTypeKind.View:
                b.Write(ind + "LubNative.LubView " + v + " = default;\n");
                o.Add("&" + v);
                return;
            case LubTypeKind.Record:
                b.Write(ind + "LubNative." + C(tr.Name) + " " + v + " = default;\n");
                o.Add("&" + v);
                return;
            case LubTypeKind.List:
                b.Write(ind + NElemQualified(tr.Elem!) + "* " + v + " = null;\n" + ind + "int " + v + "_n = 0;\n");
                o.Add("&" + v);
                o.Add("&" + v + "_n");
                return;
        }
        GenError.Report("unsupported out / return type " + tr.ToString());
    }

    // LubNative の外 (facade の本体) から見た native の要素型
    private static string NElemQualified(TypeRef elem)
    {
        if (elem.Kind == LubTypeKind.String) return "LubNative.LubStr";
        if (elem.Kind == LubTypeKind.Record) return "LubNative." + C(elem.Name);
        return NElem(elem);
    }

    // 受け皿を公開型の値にする式
    private string OutValue(TypeRef tr, string n, string? has)
    {
        string v = "o_" + n;
        string guard = has != null ? "!" + has + " ? null : " : "";
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
                return tr.Nullable ? "(" + guard + "(int?)" + v + ")" : v;
            case LubTypeKind.Double:
                return tr.Nullable ? "(" + guard + "(float?)" + v + ")" : v;
            case LubTypeKind.Bool:
                return tr.Nullable ? "(" + guard + "(bool?)" + v + ")" : v;
            case LubTypeKind.Enum:
                return tr.Nullable ? "(" + guard + "(" + EnumCs(tr) + "?)" + v + ")" : "(" + EnumCs(tr) + ")" + v;
            case LubTypeKind.String:
                return tr.Nullable ? "LubRuntime.StrOrNull(" + v + ")" : "LubRuntime.Str(" + v + ")";
            case LubTypeKind.Handle:
                return "LubNative.H_" + tr.Name + "(" + v + ")" + (tr.Nullable ? "" : "!");
            case LubTypeKind.View:
                return "LubRuntime.View(" + v + ")";
            case LubTypeKind.Record:
                return has != null ? "(" + guard + "LubNative.From_" + C(tr.Name) + "(&" + v + "))" : "LubNative.From_" + C(tr.Name) + "(&" + v + ")";
            case LubTypeKind.List:
                return ListFromNative(tr, v, v + "_n");
        }
        GenError.Report("unsupported out / return type " + tr.ToString());
        return "";
    }

    private string ListFromNative(TypeRef tr, string ptr, string count)
    {
        var elem = tr.Elem!;
        switch (elem.Kind)
        {
            case LubTypeKind.Double:
                return "LubRuntime.FloatList(" + ptr + ", " + count + ")";
            case LubTypeKind.Int:
                return "LubRuntime.IntList(" + ptr + ", " + count + ")";
            case LubTypeKind.Enum:
                return "LubRuntime.EnumList<" + EnumCs(elem) + ">(" + ptr + ", " + count + ")";
            case LubTypeKind.Bool:
                return "LubRuntime.BoolList(" + ptr + ", " + count + ")";
            case LubTypeKind.String:
                return "LubRuntime.StrList(" + ptr + ", " + count + ")";
            case LubTypeKind.Handle:
                return "LubRuntime.HandleList(" + ptr + ", " + count + ", LubNative.H_" + elem.Name + ")";
            case LubTypeKind.Record:
                return "LubRuntime.RecordList<" + elem.Name + ", LubNative." + C(elem.Name) + ">(" + ptr + ", " + count + ", &LubNative.From_" + C(elem.Name) + ")";
        }
        GenError.Report("unsupported list element " + elem.ToString());
        return "";
    }

    private string OutAssign(ApiParam p, string ind, string? has) => ind + p.Name + " = " + OutValue(p.Type, p.LuaName, has) + ";\n";

    // ----------------------------------------------------------- native

    private void EmitNative()
    {
        sb.Write("internal static unsafe partial class LubNative\n{\n");
        foreach (var t in model.Types)
        {
            if (t.Kind == "handle") sb.Write("    internal static " + t.Name + "? H_" + t.Name + "(int h) => h == 0 ? null : new " + t.Name + "(h);\n\n");
        }
        var sorted = SortedRecords();
        foreach (var t in sorted) EmitStruct(t);
        foreach (var ns in model.Namespaces)
        {
            foreach (var f in ns.StaticFields)
                sb.Write("    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]\n    internal static extern " + NReturn(f.Type) + " " + NativeFn(ns, f.LuaName) + "(void* ctx);\n\n");
            foreach (var f in ns.Functions)
            {
                if (!f.NoC) EmitExtern(ns, f);
            }
        }
        foreach (var t in sorted) EmitConverters(t);
        foreach (var t in sorted) EmitFieldTrampolines(t);
        foreach (var ns in model.Namespaces)
        {
            foreach (var f in ns.Functions)
            {
                if (f.NoC) continue;
                foreach (var p in f.Params)
                {
                    if (p.Type.Kind == LubTypeKind.Func) EmitTrampoline(TrampName(f, p), p.Type, 0);
                }
            }
        }
        sb.Write("}\n");
    }

    private List<ApiType> SortedRecords()
    {
        // 依存順 (C# の struct は定義順に依らないが読みやすさのため)
        var done = new List<ApiType>();
        var seen = new HashSet<string>();
        foreach (var t in model.Types)
        {
            if (t.Kind == "record") Visit(t, done, seen);
        }
        return done;
    }

    private void Visit(ApiType t, List<ApiType> done, HashSet<string> seen)
    {
        if (!seen.Add(t.Name)) return;
        ApiType? b;
        if (t.Base != null && types.TryGetValue(t.Base, out b)) Visit(b!, done, seen);
        foreach (var f in AllFields(t))
        {
            var deps = new List<string>();
            Deps(f.Type, deps);
            foreach (var d in deps)
            {
                ApiType? dt;
                if (types.TryGetValue(d, out dt) && dt!.Kind == "record") Visit(dt, done, seen);
            }
        }
        done.Add(t);
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

    private static string NReturn(TypeRef tr)
    {
        switch (tr.Kind)
        {
            case LubTypeKind.Void:
                return "void";
            case LubTypeKind.Int:
            case LubTypeKind.Enum:
            case LubTypeKind.Handle:
                return "int";
            case LubTypeKind.Double:
                return "float";
            case LubTypeKind.Bool:
                return "byte";
        }
        GenError.Report("unsupported direct return " + tr.ToString());
        return "";
    }

    // C の struct と同じ並び (CHeader.EmitStruct と 1 対 1)
    private void EmitStruct(ApiType t)
    {
        string c = C(t.Name);
        sb.Write("    [StructLayout(LayoutKind.Sequential)]\n    internal struct " + c + "\n    {\n");
        bool any = false;
        if (t.Base != null)
        {
            sb.Write("        public " + C(t.Base) + " @base;\n");
            any = true;
        }
        if (HasFuncs(t))
        {
            sb.Write("        public void* user;\n        public delegate* unmanaged[Cdecl]<void*, void> user_release;\n");
            any = true;
        }
        foreach (var f in t.Fields)
        {
            string n = f.LuaName;
            var tr = f.Type;
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Double:
                case LubTypeKind.Bool:
                case LubTypeKind.Enum:
                    if (f.Optional) sb.Write("        public bool @has_" + n + ";\n");
                    sb.Write("        public " + NScalar(tr) + " @" + n + ";\n");
                    break;
                case LubTypeKind.String:
                    if (f.Bits)
                    {
                        if (f.Optional) sb.Write("        public bool @has_" + n + ";\n");
                        sb.Write("        public ulong @" + n + ";\n");
                    }
                    else sb.Write("        public LubStr @" + n + ";\n");
                    break;
                case LubTypeKind.Handle:
                    sb.Write("        public int @" + n + ";\n");
                    break;
                case LubTypeKind.Keyed:
                    sb.Write("        public LubStr @" + n + ";\n");
                    break;
                case LubTypeKind.View:
                    sb.Write("        public LubView @" + n + ";\n");
                    break;
                case LubTypeKind.Record:
                    if (f.Optional) sb.Write("        public bool @has_" + n + ";\n");
                    sb.Write("        public " + C(tr.Name) + " @" + n + ";\n");
                    break;
                case LubTypeKind.List:
                {
                    var elem = tr.Elem!;
                    if (elem.Kind == LubTypeKind.Array)
                        sb.Write("        public " + NScalar(elem.Elem!) + "* @" + n + ";\n        public int @" + n + "_count;\n");
                    else if (f.ArrayLen >= 0)
                        sb.Write("        public fixed " + NElem(elem) + " @" + n + "[" + f.ArrayLen + "];\n        public int @" + n + "_count;\n");
                    else
                        sb.Write("        public " + NElem(elem) + "* @" + n + ";\n        public int @" + n + "_count;\n");
                    break;
                }
                case LubTypeKind.Array:
                    if (f.ArrayLen < 0) GenError.Report(f.Name + ": array field needs [LubArray]");
                    if (f.Optional) sb.Write("        public bool @has_" + n + ";\n");
                    sb.Write("        public fixed " + NScalar(tr.Elem!) + " @" + n + "[" + f.ArrayLen + "];\n");
                    break;
                case LubTypeKind.Func:
                    sb.Write("        public " + FnPtr(tr) + " @" + n + ";\n");
                    break;
                default:
                    GenError.Report(f.Name + ": unsupported field type " + tr.ToString());
                    break;
            }
            any = true;
        }
        if (!any) sb.Write("        public int unused;\n");
        sb.Write("    }\n\n");
    }

    private void EmitExtern(ApiNamespace ns, ApiFunction f)
    {
        if (HasNativeParam(f)) return;
        var args = new List<string>();
        args.Add("void* ctx");
        foreach (var p in f.Params)
        {
            if (!p.IsOut && !p.IsCount) NInParam(f, p, args);
        }
        foreach (var p in f.Params)
        {
            if (!p.IsOut) continue;
            NOutParam(p.Type, p.LuaName, args);
            if (p.Type.Kind == LubTypeKind.Record && p.Type.Nullable) args.Add("bool* has_" + p.LuaName);
        }
        string ret;
        var r = f.Return;
        if (f.NoFail)
        {
            if (IsDirect(r)) ret = NReturn(r);
            else
            {
                ret = "void";
                NOutParam(r, "out", args);
            }
        }
        else
        {
            ret = "int";
            if (r.Kind != LubTypeKind.Void)
            {
                NOutParam(r, "out", args);
                if (r.Nullable && (r.IsScalar || (r.Kind == LubTypeKind.Record && f.Maybe))) args.Add("bool* has");
            }
        }
        sb.Write("    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]\n");
        sb.Write("    internal static extern " + ret + " " + NativeFn(ns, f.LuaName) + "(" + string.Join(", ", args) + ");\n\n");
    }

    private void NInParam(ApiFunction f, ApiParam p, List<string> o)
    {
        string n = "@" + p.LuaName;
        var tr = p.Type;
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
            case LubTypeKind.Double:
            case LubTypeKind.Enum:
                o.Add(tr.Nullable ? NScalar(tr) + "* " + n : NScalar(tr) + " " + n);
                return;
            case LubTypeKind.Bool:
                o.Add(tr.Nullable ? "bool* " + n : "byte " + n);
                return;
            case LubTypeKind.String:
            case LubTypeKind.Keyed:
                o.Add("LubStr " + n);
                return;
            case LubTypeKind.Handle:
                o.Add("int " + n);
                return;
            case LubTypeKind.View:
                o.Add("byte* " + n);
                o.Add("int " + n + "_len");
                return;
            case LubTypeKind.Record:
                o.Add(C(tr.Name) + "* " + n);
                return;
            case LubTypeKind.List:
            case LubTypeKind.Array:
                o.Add(NElem(tr.Elem!) + "* " + n);
                o.Add("int " + n + "_count");
                return;
            case LubTypeKind.Func:
                o.Add(FnPtr(tr) + " " + n);
                o.Add("void* " + n + "_user");
                return;
        }
        GenError.Report(f.Name + ": unsupported parameter " + p.Name + ": " + tr.ToString());
    }

    private static void NOutParam(TypeRef tr, string name, List<string> o)
    {
        string n = "@" + name;
        switch (tr.Kind)
        {
            case LubTypeKind.Int:
            case LubTypeKind.Double:
            case LubTypeKind.Bool:
            case LubTypeKind.Enum:
                o.Add(NScalar(tr) + "* " + n);
                return;
            case LubTypeKind.String:
                o.Add("LubStr* " + n);
                return;
            case LubTypeKind.Handle:
                o.Add("int* " + n);
                return;
            case LubTypeKind.View:
                o.Add("LubView* " + n);
                return;
            case LubTypeKind.Record:
                o.Add(C(tr.Name) + "* " + n);
                return;
            case LubTypeKind.List:
                o.Add(NElem(tr.Elem!) + "** " + n);
                o.Add("int* " + n + "_count");
                return;
        }
        GenError.Report("unsupported out / return type " + tr.ToString());
    }

    // ------------------------------------------------------- converters

    private void EmitConverters(ApiType t)
    {
        string c = C(t.Name);
        // To_: 公開 object → native struct (arena に memory を取る)
        sb.Write("    internal static void To_" + c + "(" + t.Name + " o, LubRuntime.Arena a, " + c + "* s)\n    {\n");
        if (t.Base != null) sb.Write("        To_" + C(t.Base) + "(o, a, &s->@base);\n");
        if (HasFuncs(t))
        {
            var names = new List<string>();
            foreach (var f in Funcs(t)) names.Add("o." + f.Name);
            sb.Write("        var box = a.CallbackBox(new Delegate?[] { " + string.Join(", ", names) + " });\n");
            sb.Write("        s->user = box;\n        s->user_release = box == null ? null : &LubRuntime.ReleaseUser;\n");
        }
        foreach (var f in t.Fields)
        {
            string n = f.LuaName;
            var tr = f.Type;
            string v = "o." + f.Name;
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Double:
                case LubTypeKind.Bool:
                case LubTypeKind.Enum:
                {
                    string cast = tr.Kind == LubTypeKind.Double ? "(float)" : tr.Kind == LubTypeKind.Enum ? "(int)" : "";
                    if (f.Optional) sb.Write("        s->@has_" + n + " = " + v + ".HasValue;\n        s->@" + n + " = " + cast + "(" + v + " ?? default);\n");
                    else sb.Write("        s->@" + n + " = " + cast + v + ";\n");
                    break;
                }
                case LubTypeKind.String:
                    if (f.Bits)
                    {
                        if (f.Optional) sb.Write("        s->@has_" + n + " = " + v + " != null;\n        s->@" + n + " = LubRuntime.Bits(" + v + ");\n");
                        else sb.Write("        s->@" + n + " = LubRuntime.Bits(" + v + ");\n");
                    }
                    else sb.Write("        s->@" + n + " = a.Str(" + v + ");\n");
                    break;
                case LubTypeKind.Handle:
                    sb.Write("        s->@" + n + " = " + v + "?.H ?? 0;\n");
                    break;
                case LubTypeKind.Keyed:
                    sb.Write("        s->@" + n + " = a.Str(" + v + "?.Key);\n");
                    break;
                case LubTypeKind.View:
                    sb.Write("        s->@" + n + " = LubRuntime.ViewOf(" + v + ");\n");
                    break;
                case LubTypeKind.Record:
                    if (f.Optional) sb.Write("        s->@has_" + n + " = " + v + " != null;\n        if (" + v + " != null) To_" + C(tr.Name) + "(" + v + ", a, &s->@" + n + ");\n");
                    else sb.Write("        if (" + v + " != null) To_" + C(tr.Name) + "(" + v + ", a, &s->@" + n + ");\n");
                    break;
                case LubTypeKind.List:
                {
                    var elem = tr.Elem!;
                    if (elem.Kind != LubTypeKind.Array && f.ArrayLen >= 0)
                        sb.Write("        s->@" + n + "_count = LubRuntime.Fixed" + FixedKind(elem) + "(" + v + ", s->@" + n + ", " + f.ArrayLen + ");\n");
                    else sb.Write("        s->@" + n + " = " + ListToNative(tr, v, "s->@" + n + "_count", f.ArrayLen) + ";\n");
                    break;
                }
                case LubTypeKind.Array:
                    if (f.Optional) sb.Write("        s->@has_" + n + " = " + v + " != null;\n");
                    sb.Write("        LubRuntime.Fixed" + FixedKind(tr.Elem!) + "(" + v + ", s->@" + n + ", " + f.ArrayLen + ");\n");
                    break;
                case LubTypeKind.Func:
                    sb.Write("        s->@" + n + " = " + v + " == null ? null : &Tramp_" + c + "_" + n + ";\n");
                    break;
                default:
                    GenError.Report(t.Name + "." + f.Name + ": unsupported " + tr.ToString());
                    break;
            }
        }
        sb.Write("    }\n\n");

        // Fill_ / From_: native struct → 公開 object
        sb.Write("    internal static void Fill_" + c + "(" + t.Name + " o, " + c + "* s)\n    {\n");
        if (t.Base != null) sb.Write("        Fill_" + C(t.Base) + "(o, &s->@base);\n");
        foreach (var f in t.Fields)
        {
            string n = f.LuaName;
            var tr = f.Type;
            string v = "o." + f.Name;
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Bool:
                case LubTypeKind.Double:
                    sb.Write(f.Optional ? "        " + v + " = s->@has_" + n + " ? s->@" + n + " : null;\n" : "        " + v + " = s->@" + n + ";\n");
                    break;
                case LubTypeKind.Enum:
                    sb.Write(f.Optional ? "        " + v + " = s->@has_" + n + " ? (" + EnumCs(tr) + ")s->@" + n + " : null;\n" : "        " + v + " = (" + EnumCs(tr) + ")s->@" + n + ";\n");
                    break;
                case LubTypeKind.String:
                    if (f.Bits)
                        sb.Write(f.Optional ? "        " + v + " = s->@has_" + n + " ? LubRuntime.BitsStr(s->@" + n + ") : null;\n" : "        " + v + " = LubRuntime.BitsStr(s->@" + n + ");\n");
                    else
                        sb.Write(tr.Nullable ? "        " + v + " = LubRuntime.StrOrNull(s->@" + n + ");\n" : "        " + v + " = LubRuntime.Str(s->@" + n + ");\n");
                    break;
                case LubTypeKind.Handle:
                    sb.Write("        " + v + " = H_" + tr.Name + "(s->@" + n + ")" + (tr.Nullable ? "" : "!") + ";\n");
                    break;
                case LubTypeKind.Keyed:
                    sb.Write("        " + v + " = (s->@" + n + ".len > 0 ? new " + tr.Name + "(LubRuntime.Str(s->@" + n + ")) : null)" + (tr.Nullable ? "" : "!") + ";\n");
                    break;
                case LubTypeKind.View:
                    sb.Write("        " + v + " = LubRuntime.View(s->@" + n + ")" + (tr.Nullable ? "" : "!") + ";\n");
                    break;
                case LubTypeKind.Record:
                    if (f.Optional) sb.Write("        " + v + " = s->@has_" + n + " ? From_" + C(tr.Name) + "(&s->@" + n + ") : null;\n");
                    else sb.Write("        " + v + " = From_" + C(tr.Name) + "(&s->@" + n + ");\n");
                    break;
                case LubTypeKind.List:
                {
                    // 無い配列 (NULL) は Lua と同じく null にする (空 list にしない。ゲームは有無で分岐する)
                    var elem = tr.Elem!;
                    if (elem.Kind == LubTypeKind.Array)
                        sb.Write("        " + v + " = s->@" + n + " == null ? null! : LubRuntime.FloatRowList(s->@" + n + ", s->@" + n + "_count, " + (f.ArrayLen >= 0 ? f.ArrayLen.ToString() : "") + ");\n");
                    else if (f.ArrayLen >= 0)
                        sb.Write("        " + v + " = " + ListFromNative(tr, "s->@" + n, "s->@" + n + "_count") + ";\n");
                    else
                        sb.Write("        " + v + " = s->@" + n + " == null ? null! : " + ListFromNative(tr, "s->@" + n, "s->@" + n + "_count") + ";\n");
                    break;
                }
                case LubTypeKind.Array:
                {
                    string arr = "LubRuntime." + FixedKind(tr.Elem!) + "Array(s->@" + n + ", " + f.ArrayLen + ")";
                    sb.Write(f.Optional ? "        " + v + " = s->@has_" + n + " ? " + arr + " : null;\n" : "        " + v + " = " + arr + ";\n");
                    break;
                }
                case LubTypeKind.Func:
                    break; // callback は戻さない
                default:
                    GenError.Report(t.Name + "." + f.Name + ": unsupported " + tr.ToString());
                    break;
            }
        }
        sb.Write("    }\n\n");
        sb.Write("    internal static " + t.Name + " From_" + c + "(" + c + "* s)\n    {\n        var o = new " + t.Name + "();\n        Fill_" + c + "(o, s);\n        return o;\n    }\n\n");
    }

    private static string FixedKind(TypeRef elem)
    {
        if (elem.Kind == LubTypeKind.Double) return "Floats";
        if (elem.Kind == LubTypeKind.Int || elem.Kind == LubTypeKind.Enum) return "Ints";
        GenError.Report("unsupported fixed array element " + elem.ToString());
        return "";
    }

    // ------------------------------------------------------ trampolines

    private void EmitFieldTrampolines(ApiType t)
    {
        string c = C(t.Name);
        var funcs = Funcs(t);
        for (int i = 0; i < funcs.Count; i++) EmitTrampoline(c + "_" + funcs[i].LuaName, funcs[i].Type, i);
    }

    // C から呼ばれる static 関数。
    // user は LubRuntime.CallbackBox の GCHandle。
    private void EmitTrampoline(string name, TypeRef fn, int slot)
    {
        var ret = fn.FuncReturn!;
        string retC;
        if (ret.Kind == LubTypeKind.Bool) retC = "byte";
        else if (ret.Kind == LubTypeKind.Double) retC = "float";
        else if (ret.Kind == LubTypeKind.Int) retC = "int";
        else if (ret.Kind == LubTypeKind.Void) retC = "void";
        else
        {
            GenError.Report("callback " + name + ": return " + ret.ToString());
            retC = "";
        }
        var ps = fn.FuncParams!;
        var args = new List<string>();
        args.Add("void* user");
        var conv = new List<string>();
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            string pn = Text.Sub("abcdefghijklmnopqrstuvwxyz", i, 1);
            if (p.Kind == LubTypeKind.Record) args.Add(C(p.Name) + "* " + pn);
            else if (p.Kind == LubTypeKind.Int || p.Kind == LubTypeKind.Enum) args.Add("int " + pn);
            else if (p.Kind == LubTypeKind.Double) args.Add("float " + pn);
            else if (p.Kind == LubTypeKind.Bool) args.Add("byte " + pn);
            else GenError.Report("callback " + name + ": param " + p.ToString());
            if (p.Kind == LubTypeKind.Record) conv.Add("From_" + C(p.Name) + "(" + pn + ")");
            else if (p.Kind == LubTypeKind.Enum) conv.Add("(" + EnumCs(p) + ")" + pn);
            else if (p.Kind == LubTypeKind.Bool) conv.Add("(" + pn + " != 0)");
            else conv.Add(pn);
        }
        // 既定値: 判定は「続行 / 衝突する」、数値は 0 (runtime が既定に読み替える)。
        string def = "";
        if (ret.Kind == LubTypeKind.Bool) def = "1";
        else if (ret.Kind == LubTypeKind.Double) def = "1.0f";
        else if (ret.Kind == LubTypeKind.Int) def = "1";
        string delegateType = FuncCs(fn);
        sb.Write("    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n");
        sb.Write("    internal static " + retC + " Tramp_" + name + "(" + string.Join(", ", args) + ")\n    {\n");
        sb.Write("        var box = LubRuntime.CallbackBox.From(user);\n");
        sb.Write("        var cb = (" + delegateType + "?)box.Slots[" + slot + "];\n");
        sb.Write("        if (cb == null) return" + (def != "" ? " " + def : "") + ";\n");
        sb.Write("        try\n        {\n");
        string call = "cb(" + string.Join(", ", conv) + ")";
        if (ret.Kind == LubTypeKind.Void) sb.Write("            " + call + ";\n");
        else if (ret.Kind == LubTypeKind.Bool) sb.Write("            return (byte)(" + call + " ? 1 : 0);\n");
        else if (ret.Kind == LubTypeKind.Double) sb.Write("            return (float)" + call + ";\n");
        else sb.Write("            return " + call + ";\n");
        sb.Write("        }\n        catch (Exception e)\n        {\n");
        sb.Write("            box.Fail(" + slot + ", \"" + name + "\", e);\n");
        if (def != "") sb.Write("            return " + def + ";\n");
        sb.Write("        }\n    }\n\n");
    }
}
