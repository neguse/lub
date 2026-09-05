using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LubGen;
using TinyCs;

// lub-gen: cs-lib/lub_stub.cs を記述として読み、面を検査し生成物を作る。
//   lub-gen check                     記述の検査 (名前の規則・衝突・予約語)
//   lub-gen model [-o out.json]       API model を JSON で出す
//   lub-gen surface-test [-o file]    prelude が全 member を持つかの Lua テスト
//   lub-gen header [-o file]          C API の header (include/lub/lub_api.h)
//   lub-gen lua [-o file]             Lua binding (src/gen/lua_api_gen.c)
// 既定の stub は repo root からの相対パス cs-lib/lub_stub.cs。

var verb = args.Length > 0 ? args[0] : "check";
string? outPath = null;
var stubPath = "cs-lib/lub_stub.cs";
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "-o" && i + 1 < args.Length) outPath = args[++i];
    else if (args[i] == "--stub" && i + 1 < args.Length) stubPath = args[++i];
    else
    {
        Console.Error.WriteLine($"unknown arg: {args[i]}");
        return 2;
    }
}

if (!File.Exists(stubPath))
{
    Console.Error.WriteLine($"stub not found: {stubPath} (run from the repo root)");
    return 2;
}

var model = ApiModelLoader.Load(stubPath, out var compileErrors);
if (compileErrors.Count > 0)
{
    foreach (var e in compileErrors) Console.Error.WriteLine(e);
    return 1;
}

switch (verb)
{
    case "check":
        {
            var problems = Checks.Run(model);
            foreach (var p in problems) Console.Error.WriteLine("lub-gen: " + p);
            Console.Error.WriteLine(
                $"lub-gen: {model.Namespaces.Count} namespaces, " +
                $"{model.Namespaces.Sum(n => n.Functions.Count)} functions, " +
                $"{model.Namespaces.Sum(n => n.Enums.Count)} enums, " +
                $"{model.Types.Count} types, {problems.Count} problems");
            return problems.Count == 0 ? 0 : 1;
        }
    case "model":
        {
            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            });
            Emit(outPath, json);
            return 0;
        }
    case "surface-test":
        {
            Emit(outPath, SurfaceTest.Generate(model));
            return 0;
        }
    case "header":
        {
            Emit(outPath, CHeader.Generate(model));
            return 0;
        }
    case "lua":
        {
            Emit(outPath, LuaBinding.Generate(model));
            return 0;
        }
    default:
        Console.Error.WriteLine($"unknown verb: {verb}");
        return 2;
}

static void Emit(string? path, string text)
{
    if (path == null) Console.Out.Write(text);
    else File.WriteAllText(path, text);
}

static class Checks
{
    public static List<string> Run(ApiModel model)
    {
        var problems = new List<string>();
        foreach (var ns in model.Namespaces)
        {
            // overload は同じ Lua 名 1 つに落ちる (C# 名が同じなら衝突ではない)
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            void Claim(string lua, string csharp)
            {
                if (seen.TryGetValue(lua, out var prev))
                {
                    if (prev != csharp)
                        problems.Add($"{ns.Name}: '{prev}' and '{csharp}' both map to Lua '{lua}'");
                }
                else seen[lua] = csharp;
                if (lua.EndsWith('_') && LuaNaming.IsLuaKeyword(lua[..^1]))
                    problems.Add($"{ns.Name}.{csharp}: maps to a Lua keyword ('{lua[..^1]}'); rename the member");
                if (!csharp.Any(char.IsLower))
                    problems.Add($"{ns.Name}.{csharp}: all-caps name; use PascalCase");
            }
            var csNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in ns.Functions)
            {
                if (!csNames.Add(f.Name))
                    problems.Add($"{ns.Name}.{f.Name}: overload (C の名前が衝突する); rename one of them");
                Claim(f.LuaName, f.Name);
                var sawOptional = false;
                foreach (var p in f.Params)
                {
                    if (p.Optional) sawOptional = true;
                    else if (sawOptional && !p.IsOut)
                        problems.Add($"{ns.Name}.{f.Name}: required parameter '{p.Name}' after an optional one");
                }
            }
            foreach (var f in ns.StaticFields) Claim(f.LuaName, f.Name);
            // Lua の定数は namespace 直下に平らに置くので、別 enum の同名メンバは
            // 同じ値のときだけ共有できる (Blend.None / Cull.None、DontCare)
            // [LubLuaString] の enum は Lua 面の値が小文字の文字列
            var constValues = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var e in ns.Enums)
            {
                var values = new HashSet<int>();
                foreach (var m in e.Members)
                {
                    object value = e.LuaString ? m.LuaName.ToLowerInvariant() : m.Value;
                    if (constValues.TryGetValue(m.LuaName, out var prevValue))
                    {
                        if (!Equals(prevValue, value))
                            problems.Add($"{ns.Name}.{e.Name}.{m.Name}: Lua '{m.LuaName}' already has value {prevValue}");
                    }
                    else
                    {
                        constValues[m.LuaName] = value;
                        Claim(m.LuaName, $"{e.Name}.{m.Name}");
                    }
                    if (!values.Add(m.Value))
                        problems.Add($"{ns.Name}.{e.Name}: duplicate value {m.Value}");
                }
            }
        }
        foreach (var t in model.Types)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in t.Fields.Select(f => (f.LuaName, f.Name))
                         .Concat(t.Methods.Select(m => (m.LuaName, m.Name))))
            {
                if (seen.TryGetValue(f.LuaName, out var prev))
                    problems.Add($"{t.Name}: '{prev}' and '{f.Name}' both map to Lua '{f.LuaName}'");
                else seen[f.LuaName] = f.Name;
            }
        }
        return problems;
    }
}

static class SurfaceTest
{
    // prelude が stub の全 function / enum メンバを lub table に持つことを
    // 実行時に確かめる Lua entry。native gate の Lua テストとして走らせる。
    public static string Generate(ApiModel model)
    {
        var sb = new StringBuilder();
        sb.Append("-- generated by tools/lub-gen (surface-test); do not edit.\n");
        sb.Append("-- cs-lib/lub_stub.cs が宣言する function / enum メンバを\n");
        sb.Append("-- samples/lub_prelude.lua が lub table に過不足なく持つかを確かめる。\n");
        sb.Append("local M = {}\n\n");
        sb.Append("local function check(path, kind)\n");
        sb.Append("\tlocal v = lub\n");
        sb.Append("\tfor seg in string.gmatch(path, \"[^.]+\") do\n");
        sb.Append("\t\tv = v and v[seg]\n");
        sb.Append("\tend\n");
        sb.Append("\tassert(v ~= nil, \"lub.\" .. path .. \" is missing from the prelude\")\n");
        sb.Append("\tif kind == \"function\" then\n");
        sb.Append("\t\tassert(type(v) == \"function\", \"lub.\" .. path .. \" must be a function\")\n");
        sb.Append("\tend\n");
        sb.Append("end\n\n");
        sb.Append("function M.on_init()\n");
        sb.Append("\tlub.config({ backend = os.getenv(\"LUB_BACKEND\") or \"sdlgpu\" })\n");
        sb.Append("end\n\n");
        sb.Append("function M.on_frame()\n");
        var count = 0;
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ns in model.Namespaces)
        {
            var prefix = ns.LuaPath == "lub" ? "" : ns.LuaPath["lub.".Length..] + ".";
            foreach (var f in ns.Functions)
            {
                if (!emitted.Add(prefix + f.LuaName)) continue;
                sb.Append($"\tcheck(\"{prefix}{f.LuaName}\", \"function\")\n");
                count++;
            }
            foreach (var e in ns.Enums)
                foreach (var m in e.Members)
                {
                    if (!emitted.Add(prefix + m.LuaName)) continue;
                    sb.Append($"\tcheck(\"{prefix}{m.LuaName}\", \"value\")\n");
                    count++;
                }
        }
        sb.Append($"\tprint(\"API_SURFACE_OK members={count}\")\n");
        sb.Append("\tlub.quit()\n");
        sb.Append("end\n\n");
        sb.Append("return M\n");
        return sb.ToString();
    }
}
