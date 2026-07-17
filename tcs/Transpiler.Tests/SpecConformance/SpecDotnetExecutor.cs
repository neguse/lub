using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs.Tests.SpecConformance;

internal sealed record SpecDotnetRun(bool Ok, string Output, string? Error);

/// <summary>
/// 例を実 .NET で in-memory 実行して stdout を得る differential オラクル (C2)。
/// 期待値の出所が実 C# 処理系になり、手書き期待値に依存しない。
/// </summary>
internal sealed class SpecDotnetExecutor
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(10);

    // Console.Out はプロセス全域の状態。単純な SetOut 差し替えだと、xUnit の
    // 並列テストが同時に書いた行 (FileSizeGate の warning 等) が capture に
    // 混入する。AsyncLocal で「この実行の論理コールツリーからの書き込みだけ」
    // を捕捉するルーティング writer を一度だけ挿す。
    private static readonly AsyncLocal<StringWriter?> Capture = new();
    private static readonly object InstallLock = new();
    private static bool _writerInstalled;

    private static void EnsureRoutingWriter()
    {
        if (_writerInstalled)
            return;
        lock (InstallLock)
        {
            if (_writerInstalled)
                return;
            Console.SetOut(new RoutingWriter(Console.Out));
            _writerInstalled = true;
        }
    }

    private sealed class RoutingWriter(TextWriter fallback) : TextWriter
    {
        public override System.Text.Encoding Encoding => fallback.Encoding;
        private TextWriter Target => Capture.Value ?? fallback;
        public override void Write(char value) => Target.Write(value);
        public override void Write(string? value) => Target.Write(value);
        public override void WriteLine(string? value) =>
            Target.WriteLine(value);
    }

    // classifier と同じく SDK ImplicitUsings 相当を補う (template csproj 前提)。
    private const string ImplicitUsingsSource = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    public SpecDotnetRun Run(IReadOnlyList<SpecSourceFile> sources,
        string assemblyName)
    {
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(source.Code))
            .Append(CSharpSyntaxTree.ParseText(ImplicitUsingsSource))
            .ToArray();
        var compilation = CSharpCompilation.Create(assemblyName, trees,
            SpecConformanceClassifier.FullReferences.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                concurrentBuild: false));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        if (!emitted.Success)
        {
            var errors = emitted.Diagnostics
                .Where(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString());
            return new SpecDotnetRun(false, "",
                $"dotnet emit failed: {string.Join("; ", errors)}");
        }

        stream.Position = 0;
        var context = new AssemblyLoadContext(assemblyName,
            isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            var entry = assembly.EntryPoint;
            if (entry is null)
                return new SpecDotnetRun(false, "", "no entry point");
            var arguments = entry.GetParameters().Length == 1
                ? new object[] { Array.Empty<string>() }
                : null;

            EnsureRoutingWriter();
            using var writer = new StringWriter();
            Capture.Value = writer;
            try
            {
                // AsyncLocal は Task.Run の子へ流れる — この実行の書き込み
                // だけが writer へ入り、並列テストの出力は fallback へ抜ける
                var invocation = Task.Run(() => entry.Invoke(null, arguments));
                if (!invocation.Wait(ExecutionTimeout))
                    return new SpecDotnetRun(false, "",
                        $"dotnet execution timed out ({ExecutionTimeout})");
            }
            catch (AggregateException aggregate)
            {
                var inner = aggregate.InnerException
                    is TargetInvocationException target
                    ? target.InnerException ?? target
                    : aggregate.InnerException ?? aggregate;
                return new SpecDotnetRun(false, "",
                    $"dotnet execution threw: " +
                    $"{inner.GetType().Name}: {inner.Message}");
            }
            finally
            {
                Capture.Value = null;
            }
            return new SpecDotnetRun(true, writer.ToString(), null);
        }
        finally
        {
            context.Unload();
        }
    }
}
