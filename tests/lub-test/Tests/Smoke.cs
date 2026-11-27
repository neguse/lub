namespace LubTest.Tests;

/// <summary>The C smoke tests (tests/c): one executable each, pass or fail by its exit code.</summary>
public static class Smoke
{
    public static IEnumerable<Test> All(Site site)
    {
        string[] everywhere =
        [
            "lub_tcs_sources_smoke", "lub_tcs_resolve_smoke", "lub_vm_project_smoke", "lub_physics_box2d_smoke", "lub_physics_box3d_smoke",
            "lub_surfacenets_smoke", "lub_sdf_smoke", "lub_resources_smoke", "lub_gamepad_smoke", "lub_window_input_smoke",
            "lub_shader_layout_smoke", "lub_xr_math_smoke",
        ];
        foreach (var name in everywhere) yield return One(name);
        // Slang to DXIL: the compiler of D3D12 exists only on Windows
        if (site.Windows) yield return One("lub_shader_d3d12_smoke");
    }

    private static Test One(string target) => new("smoke." + target["lub_".Length..^"_smoke".Length], ["smoke", "c"], async run =>
    {
        // On Windows the DLLs of the smoke tests are put next to lub.exe by the build of lub
        await Prepare.Lub(run);
        await Prepare.Target(run, target);
        var output = await run.Headless(run.Site.Exe(target), []);
        run.Check(output.Ok, target + " exited with " + output.ExitCode);
    });
}
