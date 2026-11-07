using System.Collections.Generic;
using TcsSelf;

namespace LubGen;

// lub-gen: cs-lib/lub_stub.cs を記述として読み、面を検査し生成物を作る。
//   lub-gen check                     記述の検査 (名前の規則・衝突・予約語)
//   lub-gen model [-o out.json]       API model を JSON で出す
//   lub-gen header [-o file]          C API の header (include/lub/lub_api.h)
//   lub-gen docs [-o file]            API reference のデータ (web/gen/lub-api-docs.json)
//   lub-gen facade [-o file]          .NET 実行の facade (dotnet/Lub/Lub.g.cs)
//   lub-gen facade-lubx [-o file]     namespace Lubx の .NET 実行の facade (dotnet/Lub/Lubx.g.cs)
//   lub-gen tcs --source F.cs ... [--entry CLASS] [-o file]
//                                     tcs2c 実行の接続コード (entry は src/tcs_host.c 用)
//   lub-gen vm [--source F.cs ...] [-o file]
//                                     tcs VM の host の関数 (src/vm_player.c 用)。--source を渡せばそのゲームが使う関数だけ
// 既定の stub は repo root からの相対パス cs-lib/lub_stub.cs。
// TinyC# で書き、tcs の compiler (SelfHost/Compiler) と一緒に C (tcs c) か .NET で動かす。OS に触る所は tcs の TcsHost。
public static class Tool
{
    public static void Start()
    {
        TcsHost.SetExitCode(Run());
        TcsHost.FlushOut();
        TcsHost.SetDone();
    }

    // 全部を Start で済ませるので、Step は来ない
    public static void Step()
    {
        TcsHost.SetDone();
    }

    private static void Err(string text) => TcsHost.WriteErr(text + "\n");

    private static int Run()
    {
        var args = new List<string>();
        for (int i = 0; i < TcsHost.ArgCount(); i++) args.Add(TcsHost.Arg(i));
        string verb = args.Count > 0 ? args[0] : "check";
        string? outPath = null;
        string stubPath = "cs-lib/lub_stub.cs";
        var sources = new List<string>();
        string? entry = null;
        for (int i = 1; i < args.Count; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Count) outPath = args[++i];
            else if (args[i] == "--stub" && i + 1 < args.Count) stubPath = args[++i];
            else if (args[i] == "--source" && i + 1 < args.Count) sources.Add(args[++i]);
            else if (args[i] == "--entry" && i + 1 < args.Count) entry = args[++i];
            else
            {
                Err("unknown arg: " + args[i]);
                return 2;
            }
        }

        if (!TcsHost.FileExists(stubPath))
        {
            Err("stub not found: " + stubPath + " (run from the repo root)");
            return 2;
        }

        string stubText = TcsHost.ReadFile(stubPath);
        var compileErrors = new List<string>();
        var model = ApiModelLoader.Load(stubPath, stubText, compileErrors);
        if (compileErrors.Count > 0)
        {
            foreach (var e in compileErrors) Err(e);
            return 1;
        }
        if (GenError.Failed)
        {
            Err("lub-gen: " + GenError.Message);
            return 1;
        }

        switch (verb)
        {
            case "tcs":
            {
                var texts = new List<string>();
                foreach (var s in sources) texts.Add(TcsHost.ReadFile(s));
                var diagnostics = new List<string>();
                var game = GameExport.Export(texts, stubText, diagnostics);
                if (game == null || diagnostics.Count != 0)
                {
                    foreach (var error in diagnostics) Err(error);
                    return 1;
                }
                string text = TcsBinding.Generate(model, game, entry);
                if (GenError.Failed)
                {
                    Err("lub-gen: " + GenError.Message);
                    return 1;
                }
                return Emit(outPath, text);
            }
            case "vm":
            {
                ExportResult? game = null;
                if (sources.Count > 0)
                {
                    var texts = new List<string>();
                    foreach (var s in sources) texts.Add(TcsHost.ReadFile(s));
                    var diagnostics = new List<string>();
                    game = GameExport.Export(texts, stubText, diagnostics);
                    if (game == null || diagnostics.Count != 0)
                    {
                        foreach (var error in diagnostics) Err(error);
                        return 1;
                    }
                }
                string text = VmBinding.Generate(model, game);
                if (GenError.Failed)
                {
                    Err("lub-gen: " + GenError.Message);
                    return 1;
                }
                return Emit(outPath, text);
            }
            case "check":
            {
                var problems = Checks.Run(model);
                foreach (var p in problems) Err("lub-gen: " + p);
                int functions = 0;
                int enums = 0;
                foreach (var n in model.Namespaces)
                {
                    functions += n.Functions.Count;
                    enums += n.Enums.Count;
                }
                Err("lub-gen: " + model.Namespaces.Count + " namespaces, " + functions + " functions, " + enums + " enums, "
                    + model.Types.Count + " types, " + problems.Count + " problems");
                return problems.Count == 0 ? 0 : 1;
            }
            case "model":
                return Emit(outPath, Json.Write(ModelJson.Of(model), false));
            case "header":
                return Emit(outPath, CHeader.Generate(model));
            case "docs":
                return Emit(outPath, Docs.Generate(model, stubPath));
            case "facade":
                return Emit(outPath, Facade.Generate(model));
            case "facade-lubx":
                return Emit(outPath, Facade.GenerateLubx(model));
        }
        Err("unknown verb: " + verb);
        return 2;
    }

    private static int Emit(string? path, string text)
    {
        if (GenError.Failed)
        {
            Err("lub-gen: " + GenError.Message);
            return 1;
        }
        if (path == null)
        {
            TcsHost.WriteOut(text);
            return 0;
        }
        string error = TcsHost.WriteFileAtomic(path, text);
        if (error != "")
        {
            Err("lub-gen: " + error);
            return 1;
        }
        return 0;
    }
}

/// <summary>`lub-gen tcs` のゲームの IL (tcs c と同じ export。generic は単相化する)。</summary>
public static class GameExport
{
    // tcs VM が許す準拠の誤り (--ref の型の検査と interface の実装)。C# 版の IlExport.Export と同じく、これだけなら IL を作って診断を付ける
    private static bool VmAllowed(string d)
    {
        foreach (var p in "RefInterfaceImplementation( RefTypeTest( RefCast( RefInterfaceTypeTest(".Split(' '))
        {
            if (d.Contains("unsupported syntax: " + p)) return true;
        }
        return false;
    }

    public static ExportResult? Export(List<string> sources, string stub, List<string> diagnostics)
    {
        var session = new Session("");
        session.ExportOnly = true;
        session.Specialize = true;
        foreach (var s in sources) session.Add("", s, 0);
        session.Add("", stub, 1);
        session.Add("TinySystem/Linq.cs", TcsHost.ToolFile("lib/TinySystem/Linq.cs"), 2);
        session.Add("TinySystem/ValueTuple.cs", TcsHost.ToolFile("lib/TinySystem/ValueTuple.cs"), 2);
        var result = session.Compile();
        var errors = new List<string>();
        foreach (var d in result.Diagnostics)
        {
            if (!d.Contains(": warning ") && !d.StartsWith("warning ")) errors.Add(d);
        }
        bool stop = false;
        foreach (var e in errors)
        {
            if (!VmAllowed(e)) stop = true;
        }
        if (result.Export == null || stop)
        {
            if (errors.Count == 0) diagnostics.Add("export failed");
            foreach (var e in errors) diagnostics.Add(e);
            return null;
        }
        foreach (var e in errors) diagnostics.Add(e);
        foreach (var e in result.Export.Diagnostics) diagnostics.Add(e);
        return result.Export;
    }
}

public static class Checks
{
    private static void Claim(string ns, string lua, string csharp, Dictionary<string, string> seen, List<string> problems)
    {
        string? prev;
        if (seen.TryGetValue(lua, out prev))
        {
            if (prev != csharp) problems.Add(ns + ": '" + prev + "' and '" + csharp + "' both map to Lua '" + lua + "'");
        }
        else seen[lua] = csharp;
        if (lua.EndsWith("_") && LuaNames.IsLuaKeyword(Text.DropEnd(lua, 1)))
            problems.Add(ns + "." + csharp + ": maps to a Lua keyword ('" + Text.DropEnd(lua, 1) + "'); rename the member");
        if (!Text.HasLower(csharp)) problems.Add(ns + "." + csharp + ": all-caps name; use PascalCase");
    }

    public static List<string> Run(ApiModel model)
    {
        var problems = new List<string>();
        foreach (var ns in model.Namespaces)
        {
            // overload は同じ Lua 名 1 つに落ちる (C# 名が同じなら衝突ではない)
            var seen = new Dictionary<string, string>();
            var csNames = new HashSet<string>();
            foreach (var f in ns.Functions)
            {
                if (!csNames.Add(f.Name)) problems.Add(ns.Name + "." + f.Name + ": overload (C の名前が衝突する); rename one of them");
                Claim(ns.Name, f.LuaName, f.Name, seen, problems);
                bool sawOptional = false;
                foreach (var p in f.Params)
                {
                    if (p.HasDefault && (p.DefaultLiteral == null || p.Type.Nullable
                        || !(p.Type.Kind == LubTypeKind.Int || p.Type.Kind == LubTypeKind.Double || p.Type.Kind == LubTypeKind.Bool)))
                        problems.Add(ns.Name + "." + f.Name + ": default value of '" + p.Name + "' must be an int / float / bool literal on a non-nullable scalar (or use null)");
                    if (p.Optional) sawOptional = true;
                    else if (sawOptional && !p.IsOut)
                        problems.Add(ns.Name + "." + f.Name + ": required parameter '" + p.Name + "' after an optional one");
                }
            }
            foreach (var f in ns.StaticFields) Claim(ns.Name, f.LuaName, f.Name, seen, problems);
            foreach (var e in ns.Enums)
            {
                var values = new HashSet<int>();
                foreach (var m in e.Members)
                {
                    if (!values.Add(m.Value)) problems.Add(ns.Name + "." + e.Name + ": duplicate value " + m.Value);
                }
            }
        }
        foreach (var t in model.Types)
        {
            var seen = new Dictionary<string, string>();
            var pairs = new List<string>();
            var names = new List<string>();
            foreach (var f in t.Fields)
            {
                pairs.Add(f.LuaName);
                names.Add(f.Name);
            }
            foreach (var m in t.Methods)
            {
                pairs.Add(m.LuaName);
                names.Add(m.Name);
            }
            for (int i = 0; i < pairs.Count; i++)
            {
                string? prev;
                if (seen.TryGetValue(pairs[i], out prev)) problems.Add(t.Name + ": '" + prev + "' and '" + names[i] + "' both map to Lua '" + pairs[i] + "'");
                else seen[pairs[i]] = names[i];
            }
        }
        StructRecords(model, problems);
        return problems;
    }

    // struct の record は C の layout の値で渡るので、field は int / float / bool / enum と、それだけでできた struct の record に限る。
    // 使えるのは関数の引数と out と戻り値 (null を許さない)、record の field (null を許す)、struct の record の field。
    private static void StructRecords(ApiModel model, List<string> problems)
    {
        foreach (var t in model.Types)
        {
            if (t.Kind != "struct")
            {
                foreach (var f in t.Fields) StructUse(model, f.Type, t.Name + "." + f.Name, true, problems);
                continue;
            }
            if (t.PositionalParams.Count > 0)
                problems.Add(t.Name + ": a struct record cannot have positional parameters (tcs cannot call the constructor of a --ref struct); declare fields or { get; init; } properties");
            else if (t.Fields.Count == 0) problems.Add(t.Name + ": a struct record needs at least one field");
            if (t.Methods.Count > 0) problems.Add(t.Name + ": a struct record cannot have methods");
            foreach (var f in t.Fields)
            {
                string where = t.Name + "." + f.Name;
                if (!f.Type.IsBlittable || f.ArrayLen >= 0 || f.Bits)
                    problems.Add(where + ": a struct record field must be int, float, bool, an enum or a struct record made of them (got " + f.Type.ToString() + ")");
                else StructUse(model, f.Type, where, false, problems);
                if (f.Accessor == "get") problems.Add(where + ": a struct record property needs init or set");
            }
        }
        foreach (var ns in model.Namespaces)
        {
            foreach (var f in ns.Functions)
            {
                string where = ns.Name + "." + f.Name;
                foreach (var p in f.Params) StructUse(model, p.Type, where + " " + p.Name, false, problems);
                StructUse(model, f.Return, where + " return", false, problems);
            }
            foreach (var f in ns.StaticFields)
            {
                if (HasStruct(f.Type)) problems.Add(ns.Name + "." + f.Name + ": a static value cannot be a struct record");
            }
        }
    }

    private static void StructUse(ApiModel model, TypeRef t, string where, bool nullableOk, List<string> problems)
    {
        if (t.Kind == LubTypeKind.Struct)
        {
            var st = model.FindType(t.Name);
            if (st == null || st.Kind != "struct") problems.Add(where + ": " + t.Name + " is not a top-level struct record of the stub");
            if (t.Nullable && !nullableOk) problems.Add(where + ": a nullable struct record (" + t.ToString() + ") can only be a field of a class record");
            return;
        }
        if ((t.Elem != null && HasStruct(t.Elem)) || (t.FuncParams != null && HasStruct(t)))
            problems.Add(where + ": a struct record cannot be in a List, an array or a callback (" + t.ToString() + ")");
    }

    private static bool HasStruct(TypeRef t)
    {
        if (t.Kind == LubTypeKind.Struct) return true;
        if (t.Elem != null && HasStruct(t.Elem)) return true;
        if (t.FuncReturn != null && HasStruct(t.FuncReturn)) return true;
        if (t.FuncParams != null)
        {
            foreach (var p in t.FuncParams)
            {
                if (HasStruct(p)) return true;
            }
        }
        return false;
    }
}

/// <summary>`lub-gen model` の JSON (C# 版の JsonSerializer の WriteIndented と WhenWritingDefault の形)。</summary>
public static class ModelJson
{
    private static void Str(JValue o, string key, string? value)
    {
        if (value != null) o.Set(key, JValue.Str(value));
    }

    private static void Flag(JValue o, string key, bool value)
    {
        if (value) o.Set(key, JValue.Bool(true));
    }

    private static void Int(JValue o, string key, int value)
    {
        if (value != 0) o.Set(key, JValue.Int(value));
    }

    private static void Len(JValue o, string key, int value)
    {
        if (value >= 0) o.Set(key, JValue.Int(value));
    }

    private static JValue? Value(ConstVal v)
    {
        if (v.Kind == ValueKind.Int) return JValue.Int(v.I);
        if (v.Kind == ValueKind.Float) return JValue.Num(v.F.ToString("R"));
        if (v.Kind == ValueKind.Bool) return JValue.Bool(v.B);
        if (v.Kind == ValueKind.String) return JValue.Str(v.S);
        return null;
    }

    private static JValue Type(TypeRef t)
    {
        var o = JValue.Obj();
        Int(o, "Kind", (int)t.Kind);
        Str(o, "Name", t.Name);
        Flag(o, "Nullable", t.Nullable);
        if (t.Elem != null) o.Set("Elem", Type(t.Elem));
        if (t.FuncParams != null)
        {
            var a = JValue.Arr();
            foreach (var p in t.FuncParams) a.Add(Type(p));
            o.Set("FuncParams", a);
        }
        if (t.FuncReturn != null) o.Set("FuncReturn", Type(t.FuncReturn));
        return o;
    }

    private static JValue Function(ApiFunction f)
    {
        var o = JValue.Obj();
        Str(o, "Name", f.Name);
        Str(o, "LuaName", f.LuaName);
        o.Set("Return", Type(f.Return));
        var ps = JValue.Arr();
        foreach (var p in f.Params)
        {
            var po = JValue.Obj();
            Str(po, "Name", p.Name);
            Str(po, "LuaName", p.LuaName);
            po.Set("Type", Type(p.Type));
            Flag(po, "Optional", p.Optional);
            Flag(po, "IsOut", p.IsOut);
            Len(po, "ArrayLen", p.ArrayLen);
            var d = Value(p.Default);
            if (d != null) po.Set("Default", d);
            Flag(po, "IsCount", p.IsCount);
            Str(po, "DefaultLiteral", p.DefaultLiteral);
            ps.Add(po);
        }
        o.Set("Params", ps);
        Str(o, "Doc", f.Doc);
        Flag(o, "NoFail", f.NoFail);
        Flag(o, "Maybe", f.Maybe);
        Flag(o, "NoC", f.NoC);
        return o;
    }

    private static JValue Field(ApiField f)
    {
        var o = JValue.Obj();
        Str(o, "Name", f.Name);
        Str(o, "LuaName", f.LuaName);
        o.Set("Type", Type(f.Type));
        Flag(o, "Optional", f.Optional);
        Str(o, "Doc", f.Doc);
        Len(o, "ArrayLen", f.ArrayLen);
        Flag(o, "Bits", f.Bits);
        if (f.Accessor != "") Str(o, "Accessor", f.Accessor);
        Flag(o, "Readonly", f.Readonly);
        return o;
    }

    private static JValue Enum(ApiEnum e)
    {
        var o = JValue.Obj();
        Str(o, "Name", e.Name);
        Str(o, "Namespace", e.Namespace);
        Str(o, "LuaPath", e.LuaPath);
        var ms = JValue.Arr();
        foreach (var m in e.Members)
        {
            var mo = JValue.Obj();
            Str(mo, "Name", m.Name);
            Str(mo, "LuaName", m.LuaName);
            Int(mo, "Value", m.Value);
            Str(mo, "Doc", m.Doc);
            ms.Add(mo);
        }
        o.Set("Members", ms);
        Str(o, "Doc", e.Doc);
        Int(o, "Line", e.Line);
        return o;
    }

    public static JValue Of(ApiModel model)
    {
        var root = JValue.Obj();
        var nss = JValue.Arr();
        foreach (var n in model.Namespaces)
        {
            var o = JValue.Obj();
            Str(o, "Name", n.Name);
            Str(o, "LuaPath", n.LuaPath);
            var fs = JValue.Arr();
            foreach (var f in n.Functions) fs.Add(Function(f));
            o.Set("Functions", fs);
            var es = JValue.Arr();
            foreach (var e in n.Enums) es.Add(Enum(e));
            o.Set("Enums", es);
            var sf = JValue.Arr();
            foreach (var f in n.StaticFields) sf.Add(Field(f));
            o.Set("StaticFields", sf);
            var cs = JValue.Arr();
            foreach (var c in n.Consts)
            {
                var co = JValue.Obj();
                Str(co, "Name", c.Name);
                Str(co, "LuaName", c.LuaName);
                var v = Value(c.Value);
                if (v != null) co.Set("Value", v);
                Str(co, "Doc", c.Doc);
                cs.Add(co);
            }
            o.Set("Consts", cs);
            Str(o, "Doc", n.Doc);
            Int(o, "Line", n.Line);
            Str(o, "LuaRoot", n.LuaRoot);
            nss.Add(o);
        }
        root.Set("Namespaces", nss);
        var ts = JValue.Arr();
        foreach (var t in model.Types)
        {
            var o = JValue.Obj();
            Str(o, "Name", t.Name);
            Str(o, "Kind", t.Kind);
            if (t.Declaration != "") Str(o, "Declaration", t.Declaration);
            var fs = JValue.Arr();
            foreach (var f in t.Fields) fs.Add(Field(f));
            o.Set("Fields", fs);
            var ms = JValue.Arr();
            foreach (var m in t.Methods) ms.Add(Function(m));
            o.Set("Methods", ms);
            Str(o, "Doc", t.Doc);
            Int(o, "Line", t.Line);
            ts.Add(o);
        }
        root.Set("Types", ts);
        return root;
    }
}
