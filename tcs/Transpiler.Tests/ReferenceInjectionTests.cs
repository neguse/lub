using Microsoft.CodeAnalysis;
using TinyCs;
using Xunit;

namespace TinyCs.Tests;

// browser-wasm 相当の host (Assembly.Location が使えない) を想定した
// TcsLibrary.References 注入口の契約テスト。runtime pack の DLL を
// byte image として読み込み、ファイルパス経由なしで補完の session (EditorSession) の compilation が組めること。
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

// TcsLibrary.References は process で 1 つなので、差し替えている間は他の test (BclCatalogTests など) を並べて動かさない
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TranspilerReferencesCollection
{
    public const string Name = "TranspilerReferences";
}
