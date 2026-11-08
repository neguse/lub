using System.Collections.Generic;

namespace TcsSelf;

public sealed class CompileResult
{
    public bool Ok;
    public List<string> Diagnostics = new List<string>();
    public int ErrorCount;
    // tcs c と tcs vm への入力 (Session.ExportOnly のとき)
    public ExportResult? Export;
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
}
