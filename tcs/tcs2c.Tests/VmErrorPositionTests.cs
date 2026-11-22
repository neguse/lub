using TinyCs.Vm;

namespace TinyCs.Tcs2c.Tests;

/// <summary>Errors when the VM backend cannot build a function have the same form as other diagnostics (`path(line,col): error ...`) and carry the location of the statement being converted.
/// The error record (TcsC.CFail) is a static shared with the C backend, so this test does not run in parallel with other tests, to keep errors out of C generation running alongside.</summary>
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
