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
    // using するときだけ出力に含め、user が同じ namespace を宣言するなら読まない (参照実装と同じ)。
    // 1 回きりの compile も増分の Session と同じ段 (parse、組み立て、宣言、束縛、出力) を通る
    public static CompileResult CompileWithLibraries(List<string> paths, List<string> texts, List<string> refPaths, List<string> refTexts,
        List<string> libPaths, List<string> libTexts, string entry)
    {
        var session = new Session(entry);
        for (int i = 0; i < paths.Count; i++) session.SetSource(paths[i], texts[i]);
        for (int i = 0; i < refPaths.Count; i++) session.SetReference(refPaths[i], refTexts[i]);
        for (int i = 0; i < libPaths.Count; i++) session.SetLibrary(libPaths[i], libTexts[i]);
        return session.Compile();
    }
}
