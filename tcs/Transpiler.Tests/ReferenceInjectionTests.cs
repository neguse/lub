using Microsoft.CodeAnalysis;
using TinyCs;
using Xunit;

namespace TinyCs.Tests;

// Contract test, for a host equivalent to browser-wasm (where Assembly.Location is unavailable),
// of the TcsLibrary.References injection point. It loads the runtime pack DLLs
// as byte images and checks that a completion session (EditorSession) compilation can be built without going through file paths.
[Collection(TranspilerReferencesCollection.Name)]
public class ReferenceInjectionTests
{
    private static MetadataReference FromBytes(string path) =>
        MetadataReference.CreateFromStream(
            new MemoryStream(File.ReadAllBytes(path)));

    [Fact]
    public void ByteImageReferences_EditorCompilationSucceeds()
    {
        var runtimeDir =
            Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var original = TcsLibrary.References;
        try
        {
            TcsLibrary.References =
            [
                FromBytes(typeof(object).Assembly.Location),
                FromBytes(Path.Combine(runtimeDir, "System.Runtime.dll")),
                FromBytes(Path.Combine(runtimeDir, "System.Collections.dll")),
                FromBytes(Path.Combine(runtimeDir, "System.Linq.dll")),
                FromBytes(Path.Combine(runtimeDir, "System.Console.dll")),
                FromBytes(typeof(global::TinySystem.Random).Assembly.Location),
            ];
            const string source = """
                using System.Collections.Generic;
                public static class Hello
                {
                    public static int Sum()
                    {
                        var xs = new List<int> { 1, 2, 3 };
                        var total = 0;
                        foreach (var x in xs) total += x;
                        return total;
                    }
                }
                """;
            var fork = new EditorSession([("Hello.cs", source)]).ForkWithContent("Hello.cs", source)!.Value;
            var errors = fork.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors));
            Assert.Equal(TcsLibrary.References, fork.Compilation.References);
        }
        finally
        {
            TcsLibrary.References = original;
        }
    }
}

// TcsLibrary.References is one per process, so while it is replaced, other tests (BclCatalogTests etc.) do not run in parallel.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TranspilerReferencesCollection
{
    public const string Name = "TranspilerReferences";
}
