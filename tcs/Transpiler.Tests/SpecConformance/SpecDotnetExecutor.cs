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

    // Console.Out はプロセス全域の状態なので、差し替え中は排他する
    // (xUnit の並列テストと交錯しないように)。
    private static readonly object ConsoleLock = new();

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

            lock (ConsoleLock)
            {
                var originalOut = Console.Out;
                using var writer = new StringWriter();
                Console.SetOut(writer);
                try
                {
                    var invocation = Task.Run(() =>
                        entry.Invoke(null, arguments));
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
                    Console.SetOut(originalOut);
                }
                return new SpecDotnetRun(true, writer.ToString(), null);
            }
        }
        finally
        {
            context.Unload();
        }
    }
}
