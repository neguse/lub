using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs.Tests.SpecConformance;

internal sealed record SpecDotnetRun(bool Ok, string Output, string? Error);

/// <summary>
/// Differential oracle that runs examples in memory on real .NET to get stdout (C2).
/// The expected values come from a real C# implementation and do not depend on handwritten expected values.
/// </summary>
internal sealed class SpecDotnetExecutor
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(10);

    // Capture goes through ConsoleCapture (the single AsyncLocal routing in the process).
    // If another test removes the router with a raw SetOut, the capture becomes empty, so SetOut is
    // never done outside ConsoleCapture.

    // As in the classifier, supply the equivalent of SDK ImplicitUsings (assuming the template csproj).
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
            .Concat(TcsLibrary.LibraryTrees.Where(t => t.FilePath != TcsLibrary.ValueTupleLibraryPath))
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

            using var writer = new StringWriter();
            using var scope = TinyCs.Tests.ConsoleCapture.BeginOut(writer);
            try
            {
                // AsyncLocal flows to the Task.Run child — only this run's writes
                // go to the writer, and output from parallel tests falls through to the fallback
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

            }
            return new SpecDotnetRun(true, writer.ToString(), null);
        }
        finally
        {
            context.Unload();
        }
    }
}
