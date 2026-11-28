namespace LubTest;

/// <summary>Every test of the repo. The files in Tests/ declare them by kind.</summary>
public static class Catalog
{
    public static List<Test> All(Site site)
    {
        var tests = new List<Test>();
        tests.AddRange(Tests.Smoke.All(site));
        tests.AddRange(Tests.CsRuntime.All(site));
        tests.AddRange(Tests.CsSteps.All(site));
        tests.AddRange(Tests.CsSteps.ShaderCache(site));
        tests.AddRange(Tests.CsSteps.DotnetSample(site));
        tests.AddRange(Tests.Golden.All(site));
        tests.AddRange(Tests.Ios.All(site));
        tests.AddRange(Tests.Bindings.All(site));
        tests.AddRange(Tests.Tools.All(site));
        tests.AddRange(Tests.DevTools.All(site));
        tests.AddRange(Tests.VmProjects.All(site));
        tests.AddRange(Tests.Lider.All(site));
        tests.AddRange(Tests.Tcs.All(site));
        tests.AddRange(Tests.TcsC.All(site));
        tests.AddRange(Tests.TcsVm.All(site));
        tests.AddRange(Tests.TcsNative.All(site));
        return tests;
    }
}
