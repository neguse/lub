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

    /// <summary>
    /// 実行せずに、受理と拒否の判定だけを比べる。参照実装が受理した入力をセルフホストが拒否する (拒否) か、
    /// 参照実装が拒否した入力をセルフホストが受理する (見逃し) なら test の失敗にする。
    /// 見逃しのうち、意味に影響しない検査として参照実装 (Roslyn) だけに任せるもの (§6.6) は deferred として数える。
    /// </summary>
    /// <param name="referenceAccepted">参照実装が error なしで変換したか</param>
    /// <param name="referenceDiagnostics">参照実装の診断 (見逃しの分類に使う)</param>
    public static void CheckVerdict(string[] sources, bool referenceAccepted, string referenceDiagnostics)
    {
        if (!Enabled) return;
        var key = string.Join("\n// ----\n", sources);
        var paths = sources.Select((_, i) => $"input{i}.cs").ToList();
        TcsSelf.CompileResult result;
        try
        {
            result = TcsSelf.Compiler.CompileWithLibraries(paths, sources.ToList(), [], [],
                [LinqPath.Value], [File.ReadAllText(LinqPath.Value)], "");
        }
        catch (Exception ex)
        {
            Report("crash", key, ex.GetType().Name + ": " + ex.Message);
            if (RecordOnly) return;
            throw;
        }
        var errors = result.Diagnostics.Where(d => d.Contains(" error ")).ToList();
        // tcs check と同じく、準拠の警告 (TCS1003) も拒否に数える
        var subsetWarnings = result.Diagnostics.Where(d => d.Contains(" warning TCS100")).ToList();
        if (referenceAccepted)
        {
            if (result.Ok && subsetWarnings.Count > 0)
            {
                Report("stricter", key, subsetWarnings[0]);
                return;
            }
            if (result.Ok)
            {
                Report("compiles", key, "");
                return;
            }
            if (errors.Any(d => d.Contains("TCS1900")))
            {
                Report("unsupported", key, errors.First(d => d.Contains("TCS1900")));
                return;
            }
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
        if (!result.Ok || subsetWarnings.Count > 0)
        {
            Report("rejects", key, errors.FirstOrDefault() ?? subsetWarnings.FirstOrDefault() ?? "");
            return;
        }
        var ids = DiagnosticIds(referenceDiagnostics);
        if (ids.Count > 0 && ids.All(id => DeferredChecks.Contains(id)))
        {
            Report("deferred", key, string.Join(",", ids));
            return;
        }
        Report("missed", key, string.Join(",", ids) + " " + FirstLine(referenceDiagnostics));
        if (RecordOnly) return;
        throw new Xunit.Sdk.XunitException(
            "self-hosted compiler accepted a program the reference compiler rejects:\n" + referenceDiagnostics);
    }

    // 意味 (出力の Lua) に影響しないので参照実装 (Roslyn) だけが判定する検査 (doc/self-hosting-design.md §6.6、§17.5)。
    // セルフホストは開発の速い経路で、正否の判定は IDE と CI の参照実装が持つ
    // 一覧は doc/self-hosting-design.md §17.6 と同じ。
    private static readonly HashSet<string> DeferredChecks =
    [
        // accessibility の不整合 (public な method が internal な型を返すなど)
        "CS0050",
        // 型引数の制約の形 (循環、struct 制約の型引数を制約に使う、sealed な型を制約に使う、override の制約の不一致)
        "CS0454", "CS0455", "CS0456", "CS0701", "CS0425",
        // variance (in / out) の妥当性と、variant な interface の中の型
        "CS1961", "CS8427",
        // 修飾子の組み合わせ (init accessor の readonly)
        "CS8903",
        // tuple の要素の名前 (Item2 を別の位置に使う)
        "CS8125",
    ];

    private static List<string> DiagnosticIds(string diagnostics)
    {
        var ids = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(diagnostics, @"\b(?:error|warning) ((?:CS|TCS)\d{4})\b"))
        {
            if (!ids.Contains(m.Groups[1].Value)) ids.Add(m.Groups[1].Value);
        }
        return ids;
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
