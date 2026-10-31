using System.Security.Cryptography;
using System.Text;

namespace TinyCs.Tests;

/// <summary>
/// セルフホストの compiler (SelfHost/) の差分の検査。TCS_SELFHOST_DIFF=1 のとき、
/// 参照実装で変換して実行した入力をセルフホストの compiler にも通し、実行結果を比べる。
/// 結果は 一致 / 未対応 (セルフホストが TCS1900 で拒否した) / 厳しい (セルフホストだけがサブセットの違反を報告した) /
/// 拒否 (それ以外の診断) / 食い違い / crash (セルフホストの compiler 自身が落ちた) に分ける。
/// 食い違い (出力が違う、セルフホストの出力が実行時に失敗する)、拒否、crash は test の失敗にする。
/// TCS_SELFHOST_REPORT があれば結果を 1 行ずつ追記する (集計は SelfHost/diff-report.sh)。
/// Transpiler.Tests (TranspileAndRun) と tcs2c.Tests (2 backend の差分) が共有する。
/// </summary>
internal static class SelfHostDifferential
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("TCS_SELFHOST_DIFF") == "1";

    private static readonly string? ReportPath =
        Environment.GetEnvironmentVariable("TCS_SELFHOST_REPORT");

    // 1 にすると拒否、食い違い、crash を記録するだけで test を落とさない (母集団の全体を 1 回で集めるため)
    private static readonly bool RecordOnly =
        Environment.GetEnvironmentVariable("TCS_SELFHOST_DIFF_RECORD_ONLY") == "1";

    private static readonly object ReportLock = new();

    // tcs と一緒に配る library (TinySystem/Linq.cs)
    private static readonly Lazy<string> LinqPath = new(() =>
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "tcs.slnx"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? ".", "TinySystem", "Linq.cs");
    });

    private static readonly Lazy<string> Prelude = new(() =>
    {
        // runtime の前置き: 空のプログラムを参照実装で変換し、CLI と同じく runtime を埋め込んだもの
        // (末尾の型 local の印を除く)
        var lua = TinyCs.LuaRuntime.EmbedRuntime(TinyCs.Transpiler.Transpile("// empty")).Lua;
        const string marker = "-- tcs:type-locals";
        var at = lua.LastIndexOf(marker, StringComparison.Ordinal);
        return at >= 0 ? lua[..at] : lua;
    });

    /// <param name="sources">C# の入力</param>
    /// <param name="luaTail">変換結果の後ろに足して実行する Lua (print(expr) や Main())</param>
    /// <param name="referenceResult">参照実装の出力の実行結果</param>
    /// <param name="runLua">Lua の script を実行して stdout を返す (参照実装と同じ正規化をする)</param>
    public static void Check(string[] sources, string luaTail, string referenceResult,
        Func<string, string> runLua)
    {
        if (!Enabled) return;
        var paths = sources.Select((_, i) => $"input{i}.cs").ToList();
        var key = string.Join("\n// ----\n", sources);
        TcsSelf.CompileResult result;
        try
        {
            result = TcsSelf.Compiler.CompileWithLibraries(paths, sources.ToList(), [], [],
                [LinqPath.Value], [File.ReadAllText(LinqPath.Value)], "");
        }
        catch (Exception ex)
        {
            // セルフホストの compiler 自身の不具合 (Lua では fault になる)
            Report("crash", key, ex.GetType().Name + ": " + ex.Message);
            if (RecordOnly) return;
            throw;
        }
        if (!result.Ok)
        {
            var errors = result.Diagnostics.Where(d => d.Contains(" error ")).ToList();
            // TCS1900 (セルフホストが未対応の機能) があれば、それに続く診断も未対応の結果として数える
            if (errors.Any(d => d.Contains("TCS1900")))
            {
                Report("unsupported", key, errors.First(d => d.Contains("TCS1900")));
                return;
            }
            // セルフホストだけがサブセットの違反 (TCS1001〜1003) を見つけた: 参照実装の検査の取りこぼしなので、
            // 数えるが test は落とさない (tasks.md に参照実装の側の不具合として記録する)
            if (errors.All(d => d.Contains("TCS1001") || d.Contains("TCS1002") || d.Contains("TCS1003")))
            {
                Report("stricter", key, errors.First());
                return;
            }
            Report("rejected", key, errors.FirstOrDefault() ?? "");
            if (RecordOnly) return;
            throw new Xunit.Sdk.XunitException(
                "self-hosted compiler rejected a program the reference compiler accepts:\n" +
                string.Join("\n", result.Diagnostics));
        }
        var script = $"{Prelude.Value}{result.Lua}\n{luaTail}";
        string selfResult;
        try
        {
            selfResult = runLua(script);
        }
        catch (Exception ex)
        {
            // luaTail が参照実装の overload の名前 (Name__sig) を呼ぶ場合は比べられない
            if (ex.Message.Contains("__") && ex.Message.Contains("nil value") && ex.Message.Contains("(field '"))
            {
                Report("incomparable", key, FirstLine(ex.Message));
                return;
            }
            Report("mismatch", key, FirstLine(ex.Message));
            if (RecordOnly) return;
            throw new Xunit.Sdk.XunitException(
                "self-hosted compiler output failed at run time:\n" + ex.Message);
        }
        if (selfResult != referenceResult)
        {
            Report("mismatch", key, $"expected {referenceResult} got {selfResult}");
            if (RecordOnly) return;
            throw new Xunit.Sdk.XunitException(
                $"self-hosted compiler output differs: expected \"{referenceResult}\", got \"{selfResult}\"\n--- lua ---\n{result.Lua}");
        }
        Report("equal", key, "");
    }

    private static string FirstLine(string s)
    {
        foreach (var line in s.Split('\n'))
        {
            if (line.Contains("lua32") || line.Contains("error")) return line.Trim();
        }
        return s.Trim();
    }

    private static void Report(string kind, string source, string detail)
    {
        if (ReportPath == null) return;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
        var line = $"{kind}\t{hash}\t{detail.Replace('\t', ' ').Replace('\n', ' ')}\n";
        lock (ReportLock)
        {
            File.AppendAllText(ReportPath, line);
            if (kind != "equal")
            {
                var dir = Path.Combine(Path.GetDirectoryName(ReportPath)!, "selfhost-cases");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{kind}-{hash}.cs"), source);
            }
        }
    }
}
