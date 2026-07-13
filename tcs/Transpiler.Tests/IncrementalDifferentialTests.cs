namespace TinyCs.Tests;

// T174: incremental/full diagnostics differential harness
// (doc/incremental-module-compilation-design.md §9)。
// IncrementalCompilationSession (T175) 導入時に Left 側を session の増分結果へ
// 差し替える。それまでは full vs full の恒等比較で、canonical 化・比較・
// diff 表示の契約を先に固定する。
public class IncrementalDifferentialTests
{
    // canonical key: (id, severity, path, span, message)。現行 API は整形済み
    // 文字列を返すため、Roslyn 標準形式 `path(line,col): severity ID: message`
    // をパースして順序非依存の正規形へ落とす。パース不能な行はそのまま使う。
    internal static List<string> Canonicalize(TranspileResult result)
    {
        var all = result.Errors.Select(e => (text: e, severity: "error"))
            .Concat(result.Warnings.Select(w => (text: w, severity: "warning")));
        var keys = new List<string>();
        foreach (var (text, severity) in all)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                text,
                @"^(?<path>.*)\((?<line>\d+),(?<col>\d+)\):\s*(?<sev>\w+)\s+(?<id>[A-Z]+\d+):\s*(?<msg>.*)$");
            keys.Add(m.Success
                ? $"{m.Groups["id"].Value}|{m.Groups["sev"].Value}|{m.Groups["path"].Value}|" +
                  $"{m.Groups["line"].Value},{m.Groups["col"].Value}|{m.Groups["msg"].Value}"
                : $"raw|{severity}||0,0|{text}");
        }
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    // 一致なら空文字列、不一致なら missing/extra を人間が読める形で返す。
    internal static string Diff(List<string> left, List<string> right)
    {
        var missing = right.Except(left).ToList(); // right にあって left にない
        var extra = left.Except(right).ToList();   // left にだけある
        if (missing.Count == 0 && extra.Count == 0)
            return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"diagnostics differ (left={left.Count}, right={right.Count}):");
        foreach (var d in missing)
            sb.AppendLine($"  missing in left:  {d}");
        foreach (var d in extra)
            sb.AppendLine($"  extra in left:    {d}");
        return sb.ToString();
    }

    private static void AssertIdentical(string[] sources, string[]? paths = null)
    {
        var a = Transpiler.TranspileWithDiagnostics(sources, paths);
        var b = Transpiler.TranspileWithDiagnostics(sources, paths);
        var diff = Diff(Canonicalize(a), Canonicalize(b));
        Assert.True(diff.Length == 0, diff);
    }

    [Fact]
    public void CleanInput_FullVsFull_IsIdentical()
    {
        AssertIdentical(["""
            public class T
            {
                public static int Test() { return 42; }
            }
            """]);
    }

    [Fact]
    public void WarningInput_FullVsFull_IsIdentical()
    {
        // lowerCamel メソッドは naming warning を出す (checkNaming: true 既定)
        AssertIdentical(["""
            public class T
            {
                public static int test() { return 1; }
            }
            """], ["game/T.cs"]);
    }

    [Fact]
    public void ErrorInput_FullVsFull_IsIdentical()
    {
        // CS エラー + TCS1001 (try) の混在でも決定的に一致する
        AssertIdentical([
            """
            public class A
            {
                public static int Bad() { return Undefined; }
            }
            """,
            """
            public class B
            {
                public static void AlsoBad() { try { } finally { } }
            }
            """,
        ], ["game/A.cs", "game/B.cs"]);
    }

    [Fact]
    public void Canonicalize_ParsesRoslynFormat()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test() { return Undefined; }
            }
            """], ["game/T.cs"]);
        Assert.False(result.Success);
        var keys = Canonicalize(result);
        // (id, severity, path, span, message) が個別 field として取れている
        Assert.Contains(keys, k =>
            k.StartsWith("CS0103|error|") && k.Contains("game/T.cs") && !k.StartsWith("raw|"));
    }

    [Fact]
    public void Diff_ReportsMissingAndExtra()
    {
        var left = new List<string> { "CS0001|error|a.cs|1,1|only-left" };
        var right = new List<string> { "CS0002|error|b.cs|2,2|only-right" };
        var diff = Diff(left, right);
        Assert.Contains("missing in left", diff);
        Assert.Contains("only-right", diff);
        Assert.Contains("extra in left", diff);
        Assert.Contains("only-left", diff);
    }
}
