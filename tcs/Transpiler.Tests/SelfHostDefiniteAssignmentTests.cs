namespace TinyCs.Tests;

/// <summary>
/// セルフホストの確実な代入 (CS0165) が Roslyn と同じ判定になるか。
/// 同じ本体を両方に通し、CS0165 の有無を比べる (条件式の真偽ごとの状態、loop、switch、lambda、到達できない場所)。
/// </summary>
public class SelfHostDefiniteAssignmentTests
{
    private const string Template = """
        public static class P
        {
            static bool C() => true;
            static void Use(int v) { }
            static bool TryGet(out int v) { v = 1; return true; }
            public static void F(bool c, int k)
            {
                BODY
            }
        }
        """;

    [Theory]
    [InlineData("int x; Use(x);")]
    [InlineData("int x; if (c) x = 1; Use(x);")]
    [InlineData("int x; if (c) x = 1; else x = 2; Use(x);")]
    [InlineData("int x; if (c && TryGet(out x)) Use(x);")]
    [InlineData("int x; if (c || TryGet(out x)) Use(x);")]
    [InlineData("int x; if (!c || TryGet(out x)) { } else Use(x);")]
    [InlineData("int x; if (c && TryGet(out x)) { } else Use(x);")]
    [InlineData("int x; while (c) { x = 1; } Use(x);")]
    [InlineData("int x; while (true) { x = 1; break; } Use(x);")]
    [InlineData("int x; do { x = 1; } while (c); Use(x);")]
    [InlineData("int x; for (int i = 0; i < k; i++) { x = i; } Use(x);")]
    [InlineData("int x; for (;;) { x = 1; break; } Use(x);")]
    [InlineData("int x; switch (k) { case 1: x = 1; break; default: x = 2; break; } Use(x);")]
    [InlineData("int x; switch (k) { case 1: x = 1; break; } Use(x);")]
    [InlineData("int x; switch (k) { case 1: x = 1; break; case 2: return; default: x = 3; break; } Use(x);")]
    [InlineData("int x; if (c) return; else x = 1; Use(x);")]
    [InlineData("int x; x = c ? 1 : 2; Use(x);")]
    [InlineData("int x; int y = c ? (x = 1) : (x = 2); Use(x);")]
    [InlineData("int x; int y = c ? (x = 1) : 2; Use(x);")]
    [InlineData("int x; System.Action a = () => Use(x); x = 1;")]
    [InlineData("int x; x = 1; System.Action a = () => Use(x);")]
    [InlineData("int x; System.Action a = () => { x = 1; }; Use(x);")]
    [InlineData("int x; while (c) { if (k > 1) { x = 2; break; } } Use(x);")]
    [InlineData("int x; while (c) { if (k > 1) continue; x = 2; } Use(x);")]
    [InlineData("int x; foreach (var q in new System.Collections.Generic.List<int>()) { x = q; } Use(x);")]
    [InlineData("int x; if (TryGet(out x) && x > 0) Use(x);")]
    [InlineData("int x; bool b = c && TryGet(out x); Use(x);")]
    [InlineData("int x; if (true) x = 1; Use(x);")]
    [InlineData("int x; if (false) Use(x);")]
    [InlineData("int x; while (false) { Use(x); }")]
    [InlineData("int x; x = x + 1;")]
    [InlineData("int x; x += 1;")]
    [InlineData("int x; x++;")]
    [InlineData("int x; TryGet(out x); Use(x);")]
    [InlineData("int x, y; x = y = 3; Use(x); Use(y);")]
    [InlineData("string s; if (c) s = \"a\"; else return; Use(s.Length);")]
    [InlineData("int x; do { if (c) break; x = 1; } while (c); Use(x);")]
    [InlineData("int x; do { x = 1; if (c) continue; } while (c); Use(x);")]
    [InlineData("int x; for (int i = 0; ; i++) { if (i > 3) { x = i; break; } } Use(x);")]
    [InlineData("int x; switch (k) { case 1: return; default: return; } Use(x);")]
    [InlineData("int x; int y = k switch { 1 => x = 1, _ => x = 2 }; Use(x);")]
    [InlineData("int x; int y = k switch { 1 => x = 1, _ => 2 }; Use(x);")]
    [InlineData("int x; int[]? a = null; int n = a?.Length ?? (x = 1); Use(x);")]
    [InlineData("int x; if (c is true) x = 1; else x = 2; Use(x);")]
    [InlineData("int x; switch (1) { case 1: x = 1; break; } Use(x);")]
    [InlineData("int x; switch (2) { case 1: x = 1; break; } Use(x);")]
    [InlineData("bool b; switch (1) { case 2 when b: break; }")]
    [InlineData("int a; int b; (a, b) = (1, 2); Use(a + b);")]
    [InlineData("int a; int b; (a, b) = (b, 2); Use(a);")]
    [InlineData("int x; if (!(true)) Use(x); else x = 1; Use(x);")]
    [InlineData("int x; bool t = true && !false; if (t) x = 1; Use(x);")]
    public void MatchesReference(string body)
    {
        var source = Template.Replace("BODY", body);
        var roslyn = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("t",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)], TcsLibrary.References,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        bool referenceReports = roslyn.GetDiagnostics().Any(d => d.Id == "CS0165");
        var self = Checker.Check([source]);
        bool selfReports = self.Errors.Any(e => e.Contains("CS0165"));
        Assert.True(referenceReports == selfReports,
            $"Roslyn CS0165={referenceReports}, self-host CS0165={selfReports}\n{string.Join("\n", self.Errors)}");
    }
}
