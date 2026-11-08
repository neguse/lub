using System.Collections.Generic;

namespace LubGen;

// API reference のデータ (web/gen/lub-api-docs.json)。
// web/scripts/gen-api-docs.mjs が読み、doc の markdown を HTML にしてガイドと合わせて web/public/api-docs.json にする。
// 形は web/playground/docs.ts の ApiDocs (guides を除く)。
//
// - module = stub の namespace (Lub / Gfx / Input / ...)。
//   主型は namespace 自身、
//   補助型は namespace の enum と、その namespace の関数から辿れる record / handle
//   (宣言順の namespace が先に取る)。どの namespace からも辿れない型は root の
//   Lub に置く。
// - signature は C# の形。
// - 型の kind は class で、struct の record は struct。
public static class Docs
{
    private const string RootModule = ApiModelLoader.RootNamespace;

    private static JValue Member(string name, string kind, string signature, string doc) =>
        JValue.Obj().Set("name", JValue.Str(name)).Set("kind", JValue.Str(kind)).Set("signature", JValue.Str(signature)).Set("doc", JValue.Str(doc));

    private static JValue DocType(string kind, string path, string name, string module, string file, string doc,
        JValue members, bool isEnum)
    {
        var o = JValue.Obj();
        o.Set("kind", JValue.Str(kind)).Set("path", JValue.Str(path)).Set("name", JValue.Str(name)).Set("module", JValue.Str(module))
            .Set("file", JValue.Str(file)).Set("doc", JValue.Str(doc)).Set("members", members);
        if (isEnum) o.Set("isEnum", JValue.Bool(true));
        return o;
    }

    public static string Generate(ApiModel model, string stubPath)
    {
        var owner = AssignTypes(model);
        var modules = JValue.Arr();
        foreach (var ns in model.Namespaces)
        {
            string parent = ns.HostRoot == "lub" ? RootModule : ApiModelLoader.LubxNamespace;
            string path = ns.Name == RootModule ? RootModule : parent + "." + ns.Name;
            string file = stubPath + "#L" + ns.Line;
            var types = JValue.Arr();
            types.Add(DocType("class", path, ns.Name, path, file, ns.Doc, NamespaceMembers(ns), false));
            foreach (var e in ns.Enums)
                types.Add(DocType("enum", path + "." + e.Name, e.Name, path, stubPath + "#L" + e.Line, e.Doc, EnumMembers(e), true));
            foreach (var t in model.Types)
            {
                if (owner[t.Name] != ns.Name) continue;
                types.Add(DocType(t.Kind == "struct" ? "struct" : "class", t.Name, t.Name, path, stubPath + "#L" + t.Line, t.Doc, TypeMembers(t), false));
            }
            modules.Add(JValue.Obj().Set("module", JValue.Str(path)).Set("package", JValue.Str("lub")).Set("name", JValue.Str(ns.Name))
                .Set("file", JValue.Str(file)).Set("types", types));
        }
        var packages = JValue.Arr().Add(JValue.Obj().Set("name", JValue.Str("lub")).Set("modules", modules));
        var root = JValue.Obj().Set("source", JValue.Str(stubPath)).Set("packages", packages);
        return Json.Write(root, true) + "\n";
    }

    private static bool Traced(LubTypeKind k) =>
        k == LubTypeKind.Record || k == LubTypeKind.Struct || k == LubTypeKind.Handle || k == LubTypeKind.View || k == LubTypeKind.Keyed;

    // 型がどの namespace の module に載るか。
    // 宣言順の namespace から関数の引数 / 戻り値 / field を辿って最初に届いた namespace。
    private static Dictionary<string, string> AssignTypes(ApiModel model)
    {
        var owner = new Dictionary<string, string>();
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
                if (r.FuncParams != null)
                {
                    foreach (var p in r.FuncParams) queue.Enqueue(p);
                }
                if (r.FuncReturn != null) queue.Enqueue(r.FuncReturn);
                if (!Traced(r.Kind)) continue;
                var t = model.FindType(r.Name);
                if (t == null || owner.ContainsKey(t.Name)) continue;
                owner[t.Name] = ns.Name;
                foreach (var f in t.Fields) queue.Enqueue(f.Type);
                foreach (var m in t.Methods)
                {
                    queue.Enqueue(m.Return);
                    foreach (var p in m.Params) queue.Enqueue(p.Type);
                }
            }
        }
        foreach (var t in model.Types)
        {
            if (!owner.ContainsKey(t.Name)) owner[t.Name] = RootModule;
        }
        return owner;
    }

    private static JValue NamespaceMembers(ApiNamespace ns)
    {
        var members = JValue.Arr();
        foreach (var c in ns.Consts)
            members.Add(Member(c.Name, "var", "const " + CsNames.ConstType(c.Value) + " " + c.Name + " = " + CsNames.Const(c.Value), c.Doc));
        foreach (var f in ns.StaticFields)
            members.Add(Member(f.Name, "var", "static " + CsNames.Type(f.Type) + " " + f.Name, f.Doc));
        foreach (var f in ns.Functions)
            members.Add(Member(f.Name, "method", "static " + Signature(f), f.Doc));
        return members;
    }

    private static JValue EnumMembers(ApiEnum e)
    {
        var members = JValue.Arr();
        foreach (var m in e.Members) members.Add(Member(m.Name, "ctor", m.Name + " = " + m.Value, m.Doc));
        return members;
    }

    private static JValue TypeMembers(ApiType t)
    {
        var members = JValue.Arr();
        foreach (var f in t.Fields)
        {
            string sig = CsNames.Type(f.Type) + " " + f.Name;
            if (f.ArrayLen >= 0) sig += " (" + f.ArrayLen + " 個)";
            members.Add(Member(f.Name, "field", sig, f.Doc));
        }
        foreach (var m in t.Methods) members.Add(Member(m.Name, "method", Signature(m), m.Doc));
        return members;
    }

    private static string Signature(ApiFunction f)
    {
        var ps = new List<string>();
        foreach (var p in f.Params)
        {
            string s = CsNames.Type(p.Type) + " " + p.Name;
            if (p.IsOut) s = "out " + s;
            if (p.Optional) s += " = " + (p.DefaultLiteral ?? "null");
            ps.Add(s);
        }
        return CsNames.Type(f.Return) + " " + f.Name + "(" + string.Join(", ", ps) + ")";
    }
}
