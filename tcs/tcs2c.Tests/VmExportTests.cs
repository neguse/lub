namespace TinyCs.Tcs2c.Tests;

/// <summary>compile の結果から VM が受ける IL を取り出す所 (TcsVm.VmEmitter.Exported)。</summary>
public class VmExportTests
{
    [Fact]
    public void Exported_StopsWithTheReasonWhenABodyCouldNotBeBuilt()
    {
        var result = new TcsSelf.CompileResult { Export = new TcsSelf.ExportResult() };
        result.Export.InternalErrors.Add("IlBuild: builtin X.Y (Call at A.cs:9)");
        var diagnostics = new List<string>();

        var exported = TcsVm.VmEmitter.Exported(result, diagnostics);

        Assert.Null(exported);
        Assert.Equal(["internal: IlBuild: builtin X.Y (Call at A.cs:9)"], diagnostics);
    }
}
