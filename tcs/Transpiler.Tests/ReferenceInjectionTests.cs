using Microsoft.CodeAnalysis;
using TinyCs;
using Xunit;

namespace TinyCs.Tests;

// browser-wasm 相当の host (Assembly.Location が使えない) を想定した
// Transpiler.References 注入口の契約テスト。runtime pack の DLL を
// byte image として読み込み、ファイルパス経由なしで transpile できること。
[Collection("TranspilerReferences")]
public class ReferenceInjectionTests
{
    private static MetadataReference FromBytes(string path) =>
        MetadataReference.CreateFromStream(
            new MemoryStream(File.ReadAllBytes(path)));

    [Fact]
    public void ByteImageReferences_TranspileSucceeds()
    {
        var runtimeDir =
            Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var original = Transpiler.References;
        try
        {
            Transpiler.References =
            [
                FromBytes(typeof(object).Assembly.Location),
                FromBytes(Path.Combine(runtimeDir, "System.Runtime.dll")),
                FromBytes(Path.Combine(runtimeDir, "System.Collections.dll")),
                FromBytes(Path.Combine(runtimeDir, "System.Linq.dll")),
                FromBytes(Path.Combine(runtimeDir, "System.Console.dll")),
                FromBytes(typeof(global::TinySystem.Random).Assembly.Location),
            ];
            var result = Transpiler.TranspileWithDiagnostics(
                [
                    """
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
                    """
                ],
                entryClass: "Hello");
            Assert.True(result.Success, string.Join("\n", result.Errors));
            Assert.Contains("return Hello", result.Lua);
        }
        finally
        {
            Transpiler.References = original;
        }
    }
}
