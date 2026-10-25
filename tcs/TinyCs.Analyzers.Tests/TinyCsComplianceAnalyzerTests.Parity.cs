using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace TinyCs.Analyzers.Tests;

// 意味を見る TCS1001 の規則は、analyzer と transpiler / `tcs check` が同じ位置に同じ名前で出す
public partial class TinyCsComplianceAnalyzerTests
{
    [Theory]
    [InlineData("""
        System.Console.WriteLine(args.Length);
        """)]
    [InlineData("""
        public static class T
        {
            [System.Diagnostics.Conditional("DEBUG")]
            public static void Log() { }
        }
        """)]
    [InlineData("""
        public class T
        {
            public int F() => 1;
            public System.Func<int> G() => F;
        }
        """)]
    [InlineData("""
        public static class T
        {
            public static string F(int w) => $"{1,w}";
        }
        """)]
    [InlineData("""
        public interface IShape { }
        public static class T
        {
            public static bool F(object o) => o is IShape;
        }
        """)]
    [InlineData("""
        using System.Collections.Generic;
        public readonly record struct K(int A);
        public static class T
        {
            public static Dictionary<K, int> F() => new();
        }
        """)]
    [InlineData("""
        public class B { }
        public class D : B { }
        """)]
    [InlineData("""
        public interface IA { void F(int a); }
        public interface IB { void F(string s); }
        public class T : IA, IB
        {
            public void F(int a) { }
            public void F(string s) { }
        }
        """)]
    [InlineData("""
        using System.Collections.Generic;
        public interface IShape { int Area(); }
        public struct Sq : IShape { public int S; public int Area() => S * S; }
        public readonly record struct P(int X);
        public static class T
        {
            public static void F(List<object> xs)
            {
                object o = new Sq();
                IShape s = new Sq();
                xs.Add(new P(1));
                var c = (object)new P(2);
                P? n = null;
                object m = n;
            }
        }
        """)]
    public async Task SemanticRules_MatchTranspilerDiagnostics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create("TinyCsParity",
            [tree, LinqTree.Value.WithRootAndOptions(LinqTree.Value.GetRoot(), tree.Options)],
            GetReferences(), new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var analyzer = (await compilation
                .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TinyCsComplianceAnalyzer()))
                .GetAnalyzerDiagnosticsAsync())
            .Where(d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax
                && d.Location.SourceTree == tree)
            .Select(d => $"{d.Location.GetLineSpan().StartLinePosition.Line + 1}," +
                $"{d.Location.GetLineSpan().StartLinePosition.Character + 1}: " +
                Regex.Match(d.GetMessage(), "'(.*)'").Groups[1].Value)
            .Order().ToList();
        var transpiler = TinyCsComplianceFacts.AnalyzeUnsupportedSyntaxes(tree,
                compilation.GetSemanticModel(tree))
            .Select(line => Regex.Match(line, @"\((\d+,\d+)\): error TCS1001: unsupported syntax: (.*)$"))
            .Select(m => $"{m.Groups[1].Value}: {m.Groups[2].Value}")
            .Order().ToList();

        Assert.NotEmpty(transpiler);
        Assert.Equal(transpiler, analyzer);
    }
}
