using System.Collections.Generic;

namespace TcsSelf;

public sealed class CompileResult
{
    public bool Ok;
    // runtime の前置きを含まない Lua (host が前置きの後ろにつなぐ)
    public string Lua = "";
    public List<string> Diagnostics = new List<string>();
    public int ErrorCount;
}

// セルフホストの compiler の入口
public static class Compiler
{
    // 構文解析だけを行い、診断の文字列を返す (速度の測定と構文の検査に使う)
    public static List<string> ParseOnly(List<string> paths, List<string> texts)
    {
        var diags = new Diagnostics();
        for (int i = 0; i < paths.Count; i++)
        {
            var file = new SourceFile(i, paths[i], texts[i], false);
            Parser.ParseFile(file, diags);
        }
        var result = new List<string>();
        foreach (var d in diags.Items) result.Add(d.ToString());
        return result;
    }

    // paths / texts を Lua にする。refPaths / refTexts は型検査だけに使う宣言 (--ref)。entry は最後に return する型
    public static CompileResult Compile(List<string> paths, List<string> texts, List<string> refPaths, List<string> refTexts, string entry)
    {
        return CompileWithLibraries(paths, texts, refPaths, refTexts, new List<string>(), new List<string>(), entry);
    }

    // libPaths / libTexts は tcs と一緒に配る library (TinySystem/Linq.cs)。user が library の namespace を
    // using するときだけ出力に含め、user が同じ namespace を宣言するなら読まない (参照実装と同じ)
    public static CompileResult CompileWithLibraries(List<string> paths, List<string> texts, List<string> refPaths, List<string> refTexts,
        List<string> libPaths, List<string> libTexts, string entry)
    {
        var c = new Compilation();
        int id = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            var f = new SourceFile(id, paths[i], texts[i], false);
            id++;
            c.Files.Add(f);
        }
        for (int i = 0; i < refPaths.Count; i++)
        {
            var f = new SourceFile(id, refPaths[i], refTexts[i], true);
            id++;
            c.Files.Add(f);
        }
        foreach (var f in c.Files) c.Units.Add(Parser.ParseFile(f, c.Diags));
        var userNamespaces = new HashSet<string>();
        var userUsings = new HashSet<string>();
        for (int i = 0; i < c.Units.Count; i++)
        {
            if (c.Files[i].IsRef) continue;
            CollectNamespaces(c.Units[i], "", userNamespaces, userUsings);
        }
        for (int i = 0; i < libPaths.Count; i++)
        {
            var f = new SourceFile(id, libPaths[i], libTexts[i], false);
            id++;
            f.IsLibrary = true;
            var libDiags = new Diagnostics();
            var unit = Parser.ParseFile(f, libDiags);
            var libNamespaces = new HashSet<string>();
            CollectNamespaces(unit, "", libNamespaces, new HashSet<string>());
            bool declaredByUser = false;
            bool used = false;
            foreach (var ns in libNamespaces)
            {
                if (userNamespaces.Contains(ns)) declaredByUser = true;
                if (userUsings.Contains(ns)) used = true;
            }
            if (declaredByUser || !used) continue;
            foreach (var d in libDiags.Items) c.Diags.Items.Add(d);
            c.Diags.ErrorCount += libDiags.ErrorCount;
            c.Files.Add(f);
            c.Units.Add(unit);
        }
        // 構文だけで決まる準拠検査 (TCS1001)。--ref の宣言と library は対象外
        var compliance = new Compliance(c.Diags);
        for (int i = 0; i < c.Units.Count; i++)
        {
            if (!c.Files[i].IsRef && !c.Files[i].IsLibrary) compliance.CheckUnit(c.Units[i]);
        }
        var result = new CompileResult();
        if (c.Diags.ErrorCount == 0)
        {
            var d = new Declarer(c);
            d.DeclareAll();
            var b = new Binder(c, d);
            b.BindAll();
            if (c.Diags.ErrorCount == 0)
            {
                var e = new Emitter(c);
                result.Lua = e.EmitProgram(entry);
            }
        }
        foreach (var diag in c.Diags.Items) result.Diagnostics.Add(diag.ToString());
        result.ErrorCount = c.Diags.ErrorCount;
        result.Ok = c.Diags.ErrorCount == 0;
        return result;
    }

    // 宣言する namespace と using する namespace
    private static void CollectNamespaces(Node container, string ns, HashSet<string> declared, HashSet<string> usings)
    {
        if (container.List == null) return;
        foreach (var n in container.List)
        {
            if (n.Kind == NodeKind.Using) usings.Add(n.Text);
            else if (n.Kind == NodeKind.Namespace)
            {
                string inner = ns == "" ? n.Text : ns + "." + n.Text;
                declared.Add(inner);
                CollectNamespaces(n, inner, declared, usings);
            }
        }
    }
}
