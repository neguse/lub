using TinyCs.Vm;

namespace TinyCs.Tcs2c.Tests;

/// <summary>VM の backend が関数を作れないときの誤りは、他の診断と同じ形 (`path(line,col): error ...`) で、変換していた文の場所を持つ。
/// 誤りの記録 (TcsC.CFail) は C backend と共有の static なので、並んで動く C の生成に誤りが混ざらないよう、他の test と並べない。</summary>
[Collection(Name)]
public class VmErrorPositionTests
{
    public const string Name = "VM backend error";

    [Fact]
    public void FunctionError_CarriesTheStatementPosition()
    {
        var source = """
            using System;
            public static class P
            {
                public static void Main()
                {
                    float y = 1f;
                    var a = Math.Atan2(y, 2f);
                    Console.WriteLine((float)a);
                }
            }
            """;

        var ex = Assert.Throws<VmException>(() => SelfHostVm.Emit([source], "P", paths: ["Dbl.cs"]));

        Assert.StartsWith("Dbl.cs(7,9): error TCS1001: ", ex.Message);
        Assert.Contains("type 'double' is not in the VM yet", ex.Message);
    }
}

[CollectionDefinition(VmErrorPositionTests.Name, DisableParallelization = true)]
public sealed class VmErrorPositionCollection
{
}
