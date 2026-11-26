namespace TinyCs.Tcs2c.Tests;

/// <summary>The place that extracts the IL the VM receives from the compile result (TcsVm.VmEmitter.Exported).</summary>
public class VmExportTests
{
    [Fact]
    public void Exported_StopsWithTheReasonWhenABodyCouldNotBeBuilt()
    {
        var result = new Tcs.CompileResult { Export = new Tcs.ExportResult() };
        result.Export.InternalErrors.Add("IlBuild: builtin X.Y (Call at A.cs:9)");
        var diagnostics = new List<string>();

        var exported = TcsVm.VmEmitter.Exported(result, diagnostics);

        Assert.Null(exported);
        Assert.Equal(["internal: IlBuild: builtin X.Y (Call at A.cs:9)"], diagnostics);
    }
}
