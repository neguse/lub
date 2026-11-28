using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace LubTest;

/// <summary>Where the tests run: the repo, the build directory, the OS, and the preparations shared by tests.</summary>
public sealed class Site
{
    public string Root { get; }
    public string Build { get; }
    public bool Windows { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public bool Mac { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public bool Linux => !Windows && !Mac;
    /// <summary>linux, macos or windows (the names `lub export` takes).</summary>
    public string Os => Windows ? "windows" : Mac ? "macos" : "linux";
    public int Jobs { get; }
    public bool UpdateGoldens { get; init; }
    /// <summary>How many inputs the fuzz tests generate, and the first seed (--fuzz-count, --fuzz-seed). null for the default of each test.</summary>
    public int? FuzzCount { get; init; }
    public int? FuzzSeed { get; init; }
    public string Logs { get; }

    private readonly ConcurrentDictionary<string, Lazy<Task>> _prepared = new();
    private readonly SemaphoreSlim _cmake = new(1, 1);
    private int _display = 300;
    private string? _lavapipe;
    private bool _lavapipeLooked;

    public Site(string root, string? build, int jobs, string? logs)
    {
        Root = root;
        Build = Path.GetFullPath(build ?? Path.Combine(root, Windows ? "build-release" : Mac ? "build-mac" : "build-release-linux"));
        Jobs = jobs;
        Logs = Path.GetFullPath(logs ?? Path.Combine(Path.GetTempPath(), "lub-test-" + Environment.ProcessId));
        Directory.CreateDirectory(Logs);
    }

    public string Path_(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>An executable in the build directory (`lub`, a smoke test).</summary>
    public string Exe(string name) => Path.Combine(Build, Windows ? name + ".exe" : name);

    public string Lub => Exe("lub");

    /// <summary>The backends the tests run on here. On Linux both render through lavapipe.</summary>
    public string[] Backends => Windows ? ["d3d12"] : Mac ? ["metal"] : ["sdlgpu", "vulkan"];

    /// <summary>The backend used when a test needs only one.</summary>
    public string Backend => Backends[0];

    /// <summary>Runs a preparation once, however many tests ask for it. A failed preparation fails every test that asked.</summary>
    public Task Need(string key, Func<Task> prepare) =>
        _prepared.GetOrAdd(key, _ => new Lazy<Task>(async () =>
        {
            try { await prepare(); }
            catch (TestFailed e) { throw new TestFailed($"the preparation `{key}` failed ({e.Message}); its output is in the log of the first test that needed it"); }
        })).Value;

    /// <summary>One cmake --build at a time on the build directory.</summary>
    public async Task<T> WithCmake<T>(Func<Task<T>> action)
    {
        await _cmake.WaitAsync();
        try { return await action(); }
        finally { _cmake.Release(); }
    }

    /// <summary>An X display number for an Xvfb of one run. Never given twice: a server that is still shutting down holds its number, and a new one with the same number fails to start.</summary>
    public int TakeDisplay() => Interlocked.Increment(ref _display);

    /// <summary>The Mesa lavapipe ICD file (the CPU Vulkan the Linux captures are defined by). null if not installed.</summary>
    public string? Lavapipe
    {
        get
        {
            if (!_lavapipeLooked)
            {
                _lavapipeLooked = true;
                const string dir = "/usr/share/vulkan/icd.d";
                _lavapipe = Directory.Exists(dir) ? Directory.GetFiles(dir, "lvp_icd*.json").Order().FirstOrDefault() : null;
            }
            return _lavapipe;
        }
    }

    /// <summary>How to start a program that opens a window where there may be no display: fills env, and returns the program and the arguments to start.
    /// Windows renders with WARP and macOS with the real GPU. On Linux, Mesa lavapipe (CPU Vulkan) is forced instead of any GPU driver, so that captures are the same on every machine;
    /// the window goes to the display that is there, else to an X server of its own (xvfb-run), else nowhere (SDL_VIDEO_DRIVER=offscreen).
    /// Without lavapipe the program runs on the machine's Vulkan driver and its captures do not match the goldens.</summary>
    public (string Program, string[] Arguments) Headless(string exe, string[] args, IDictionary<string, string?> env)
    {
        if (Windows)
        {
            env.TryAdd("LUB_D3D12_WARP", "1");
            return (exe, args);
        }
        if (Mac) return (exe, args);
        if (Lavapipe is not { } icd)
        {
            env.TryAdd("SDL_VIDEO_DRIVER", "offscreen");
            return (exe, args);
        }
        env["VK_ICD_FILENAMES"] = icd;
        // The modern Vulkan loader reads this one; older ones need the legacy name above
        env["VK_DRIVER_FILES"] = icd;
        string? Current(string name) => env.TryGetValue(name, out var set) ? set : Environment.GetEnvironmentVariable(name);
        if (Current("DISPLAY") is { Length: > 0 } || Current("WAYLAND_DISPLAY") is { Length: > 0 }) return (exe, args);
        if (Which("xvfb-run") == null)
        {
            env.TryAdd("SDL_VIDEO_DRIVER", "offscreen");
            return (exe, args);
        }
        // xvfb-run -a races when instances start at the same time (two pick the same display), so each run gets a number of its own
        return ("xvfb-run", ["-n", TakeDisplay().ToString(), exe, .. args]);
    }

    public static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var ext in RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new[] { ".exe", ".cmd", ".bat", "" } : [""])
            {
                var path = Path.Combine(dir, name + ext);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }
}
