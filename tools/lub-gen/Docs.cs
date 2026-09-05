using System.Text.Json;
using System.Text.Json.Serialization;

namespace LubGen;

// API reference のデータ (web/gen/lub-api-docs.json)。web/scripts/gen-api-docs.mjs
// が読み、doc の markdown を HTML にしてガイドと合わせて web/public/api-docs.json
// にする。形は web/playground/docs.ts の ApiDocs (guides を除く)。
//
// - module = stub の namespace (Lub / Gfx / Input / ...)。主型は namespace 自身、
//   補助型は namespace の enum と、その namespace の関数から辿れる record / handle
//   (宣言順の namespace が先に取る)。どの namespace からも辿れない型は root の
//   Lub に置く。
// - signature は C# の形。Lua 面の名前は lua に別で持つ。
public static class Docs
{
    private sealed record Member(string Name, string Kind, string Signature,
        string? Lua, string Doc);

    private sealed record DocType(string Kind, string Path, string Name, string Module,
        string File, string Doc, IReadOnlyList<Member> Members,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Extends,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsEnum);

    private sealed record Module(
        [property: JsonPropertyName("module")] string ModuleName, string Package, string Name,
        string Lua, string File, IReadOnlyList<DocType> Types);

    private sealed record Package(string Name, IReadOnlyList<Module> Modules);

    private sealed record Root(string Source, IReadOnlyList<Package> Packages);

    private const string RootModule = ApiModelLoader.RootClass;

    public static string Generate(ApiModel model, string stubPath)
    {
        var owner = AssignTypes(model);
        var modules = new List<Module>();
        foreach (var ns in model.Namespaces)
        {
            var path = ns.Name == RootModule ? RootModule : RootModule + "." + ns.Name;
            var file = $"{stubPath}#L{ns.Line}";
            var types = new List<DocType>
            {
                new("class", path, ns.Name, path, file, ns.Doc, NamespaceMembers(ns), null, false),
            };
            foreach (var e in ns.Enums)
                types.Add(new DocType("enum", path + "." + e.Name, e.Name, path,
                    $"{stubPath}#L{e.Line}", e.Doc, EnumMembers(ns, e), null, true));
            foreach (var t in model.Types.Where(t => owner[t.Name] == ns.Name))
                types.Add(new DocType("class", t.Name, t.Name, path, $"{stubPath}#L{t.Line}",
                    t.Doc, TypeMembers(t), t.Base, false));
            modules.Add(new Module(path, "lub", ns.Name, ns.LuaPath, file, types));
        }
        var root = new Root(stubPath, [new Package("lub", modules)]);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return JsonSerializer.Serialize(root, options) + "\n";
    }

    // 型がどの namespace の module に載るか。宣言順の namespace から関数の
    // 引数 / 戻り値 / field を辿って最初に届いた namespace。
    private static Dictionary<string, string> AssignTypes(ApiModel model)
    {
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ns in model.Namespaces)
        {
            var queue = new Queue<TypeRef>();
            foreach (var f in ns.Functions)
            {
                queue.Enqueue(f.Return);
                foreach (var p in f.Params) queue.Enqueue(p.Type);
            }
            foreach (var f in ns.StaticFields) queue.Enqueue(f.Type);
            while (queue.Count > 0)
            {
                var r = queue.Dequeue();
                if (r.Elem != null) queue.Enqueue(r.Elem);
                if (r.FuncParams != null) foreach (var p in r.FuncParams) queue.Enqueue(p);
                if (r.FuncReturn != null) queue.Enqueue(r.FuncReturn);
                if (r.Kind is not (LubTypeKind.Record or LubTypeKind.Handle
                    or LubTypeKind.View or LubTypeKind.Keyed)) continue;
                var t = model.FindType(r.Name);
                if (t == null || owner.ContainsKey(t.Name)) continue;
                owner[t.Name] = ns.Name;
                if (t.Base != null && model.FindType(t.Base) is { } b)
                    queue.Enqueue(new TypeRef(LubTypeKind.Record, b.Name, false, null, null, null));
                foreach (var f in t.Fields) queue.Enqueue(f.Type);
                foreach (var m in t.Methods)
                {
                    queue.Enqueue(m.Return);
                    foreach (var p in m.Params) queue.Enqueue(p.Type);
                }
            }
        }
        foreach (var t in model.Types)
            owner.TryAdd(t.Name, RootModule);
        return owner;
    }

    private static List<Member> NamespaceMembers(ApiNamespace ns)
    {
        var lua = ns.LuaPath + ".";
        var members = new List<Member>();
        foreach (var c in ns.Consts)
            members.Add(new Member(c.Name, "var", $"const {CsConstType(c.Value)} {c.Name} = {CsConst(c.Value)}",
                lua + c.LuaName, c.Doc));
        foreach (var f in ns.StaticFields)
            members.Add(new Member(f.Name, "var", $"static {CsType(f.Type)} {f.Name}", lua + f.LuaName, f.Doc));
        foreach (var f in ns.Functions)
            members.Add(new Member(f.Name, "method", "static " + Signature(f), lua + f.LuaName, f.Doc));
        return members;
    }

    private static List<Member> EnumMembers(ApiNamespace ns, ApiEnum e) =>
        e.Members.Select(m => new Member(m.Name, "ctor", $"{m.Name} = {m.Value}",
            e.LuaString ? $"\"{m.LuaName.ToLowerInvariant()}\"" : ns.LuaPath + "." + m.LuaName, m.Doc)).ToList();

    private static List<Member> TypeMembers(ApiType t)
    {
        var members = new List<Member>();
        foreach (var f in t.Fields)
        {
            var sig = $"{CsType(f.Type)} {f.Name}";
            if (f.ArrayLen != null) sig += $" ({f.ArrayLen} 個)";
            members.Add(new Member(f.Name, "field", sig, f.LuaName, f.Doc));
        }
        foreach (var m in t.Methods)
            members.Add(new Member(m.Name, "method", Signature(m), ":" + m.LuaName, m.Doc));
        return members;
    }

    private static string Signature(ApiFunction f)
    {
        var ps = f.Params.Select(p =>
        {
            var s = $"{CsType(p.Type)} {p.Name}";
            if (p.IsOut) s = "out " + s;
            if (p.Optional) s += " = null";
            return s;
        });
        return $"{CsType(f.Return)} {f.Name}({string.Join(", ", ps)})";
    }

    private static string CsType(TypeRef t)
    {
        var s = t.Kind switch
        {
            LubTypeKind.Void => "void",
            LubTypeKind.List => $"List<{CsType(t.Elem!)}>",
            LubTypeKind.Array => $"{CsType(t.Elem!)}[]",
            LubTypeKind.Dict => "Dictionary<string, object>",
            LubTypeKind.Func => t.FuncReturn!.Kind == LubTypeKind.Void
                ? (t.FuncParams!.Count == 0 ? "Action" : $"Action<{string.Join(", ", t.FuncParams!.Select(CsType))}>")
                : $"Func<{string.Join(", ", t.FuncParams!.Select(CsType).Append(CsType(t.FuncReturn!)))}>",
            _ => t.Name,
        };
        return t.Nullable ? s + "?" : s;
    }

    private static string CsConstType(object v) => v switch
    {
        int => "int",
        double => "double",
        bool => "bool",
        string => "string",
        _ => v.GetType().Name,
    };

    private static string CsConst(object v) => v switch
    {
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };
}
