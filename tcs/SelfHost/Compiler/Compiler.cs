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
}
