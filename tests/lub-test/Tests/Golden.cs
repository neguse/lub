namespace LubTest.Tests;

/// <summary>The golden image tests: the visual tests of tests/cs are captured on each backend and compared with tests/golden/&lt;name&gt;_&lt;backend&gt;.png.
/// A capture is the same on every machine because the frame and the frame time are fixed, a readback arrives exactly 2 frames later (--readback-fixed-latency),
/// and the rasterizer is the CPU one of the OS (Linux: lavapipe, Windows: WARP).
/// On Linux both backends render through lavapipe and must give the same bytes, so vulkan is compared with the sdlgpu goldens.
/// macOS has no machine-independent rasterizer and no goldens of its own: the capture of the real GPU is compared with the sdlgpu golden within a tolerance (PngDiff).
/// --update-goldens writes the captures as the new goldens (never from macOS, and only from the backend that owns the golden).</summary>
public static class Golden
{
    private const string Frame = "30";

    public static IEnumerable<Test> All(Site site)
    {
        var list = new CsList(site);
        foreach (var name in list.Visual)
        {
            foreach (var backend in site.Backends)
                yield return new Test($"golden.{name}.{backend}", ["golden", backend], run => One(run, name, backend));
        }
    }

    private static async Task One(Run run, string name, string backend)
    {
        var site = run.Site;
        if (site.Linux) run.Check(site.Lavapipe != null, "goldens need Mesa lavapipe (install vulkan-swrast on Arch, mesa-vulkan-drivers on Debian)");
        string owner = site.Windows ? backend : "sdlgpu";
        var golden = site.Path_("tests", "golden", $"{name}_{owner}.png");
        var capture = Path.Combine(run.Temp, $"{name}_{backend}.png");
        await CsRuntime.OnVm(run, name, backend, ["--capture", capture, "--capture-frame", Frame, "--fixed-dt", CsRuntime.FixedDt, "--readback-fixed-latency"]);
        run.Check(File.Exists(capture), "no capture was written");
        if (site.UpdateGoldens && !site.Mac && owner == backend)
        {
            File.Copy(capture, golden, overwrite: true);
            run.Log("updated " + golden);
            return;
        }
        run.Check(File.Exists(golden), golden + " is missing (run with --update-goldens to create it)");
        if (site.Mac)
        {
            var (same, metrics) = PngDiff.Compare(capture, golden);
            run.Log(metrics);
            run.Check(same, $"{capture} differs from {golden}: {metrics}");
            return;
        }
        run.Check(File.ReadAllBytes(capture).AsSpan().SequenceEqual(File.ReadAllBytes(golden)), $"{capture} differs from {golden}");
    }
}
