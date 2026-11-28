using System.Text;

namespace LubTest.Tests;

/// <summary>The developer tools of the lub CLI that work with tcs as a child process: `lub check`, the cleanup of the child, and reading its frames.</summary>
public static class DevTools
{
    public static IEnumerable<Test> All(Site site)
    {
        yield return new Test("tools.lub-check", ["tools"], LubCheck);
        foreach (var launcher in new[] { "direct", "dotnet-run" })
        {
            foreach (var how in new[] { "exit", "kill" })
                yield return new Test($"tools.tcs-process-cleanup.{launcher}.{how}", ["tools", "process"], run => ProcessCleanup(run, launcher, how));
        }
        yield return new Test("tools.tcs-pipeline", ["tools", "process"], Pipeline);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>`lub check &lt;entry&gt;` checks the C# without opening a window or running the game, and answers with the exit code.</summary>
    private static async Task LubCheck(Run run)
    {
        var site = run.Site;
        await Prepare.Lub(run);
        var env = await Prepare.TcsEnv(run);
        // It must work without a display
        env["DISPLAY"] = null;
        env["WAYLAND_DISPLAY"] = null;
        var work = run.Temp;
        Task<Output> Check(params string[] args) => run.Exec(site.Lub, ["check", .. args], env);

        // Passes because the inputs include the .cs in subdirectories, the cs-lib implementation (Lubx) and the stub (Lub)
        Write(Path.Combine(work, "good", "Good.csproj"), "");
        Write(Path.Combine(work, "good", "Good.cs"), """
            using Lub;
            using Lubx;

            public static class Good
            {
                public static void OnInit()
                {
                    App.Config(new ConfigOpts { Width = Helper.Size, Height = Helper.Size });
                }

                public static void OnFrame(float dt)
                {
                    Color c = Color.Rgb(1.0f, 1.0f, 1.0f);
                }
            }
            """);
        Write(Path.Combine(work, "good", "sub", "Helper.cs"), """
            public static class Helper
            {
                public const int Size = 64;
            }
            """);
        var good = await Check(Path.Combine(work, "good", "Good.csproj"));
        run.Check(good.Ok, "a valid entry was rejected");
        run.Check(!good.Text.Contains("backend selected", StringComparison.Ordinal), "the check started the game");
        run.Check(!Directory.Exists(Path.Combine(work, "good", ".lub")), "the check wrote a build output");

        // Sees the same inputs as lub <csproj>: the .cs of a Lub="Source" reference are inputs too, and *.ref.cs are declaration-only references (--ref)
        Write(Path.Combine(work, "shared", "Shared.csproj"), "");
        Write(Path.Combine(work, "shared", "Shared.cs"), """
            public static class Shared
            {
                public static int Twice(int v) { return v * 2; }
            }
            """);
        Write(Path.Combine(work, "uses", "Uses.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../shared/Shared.csproj" Lub="Source" />
              </ItemGroup>
            </Project>
            """);
        Write(Path.Combine(work, "uses", "Native.ref.cs"), """
            public static class Native
            {
                public static int Get() { return 0; }
            }
            """);
        Write(Path.Combine(work, "uses", "Uses.cs"), """
            public static class Uses
            {
                public static void OnFrame(float dt)
                {
                    int v = Shared.Twice(Native.Get());
                }
            }
            """);
        var uses = await Check(Path.Combine(work, "uses", "Uses.csproj"));
        run.Check(uses.Ok, "an entry with a Lub=\"Source\" reference and a *.ref.cs was rejected");
        run.Check(!Directory.Exists(Path.Combine(work, "uses", ".lub")), "the check wrote a build output");

        Write(Path.Combine(work, "bad", "Bad.csproj"), "");
        Write(Path.Combine(work, "bad", "Bad.cs"), """
            public static class Bad
            {
                public static void OnFrame(float dt)
                {
                    NoSuchType.Call();
                }
            }
            """);
        var bad = await Check(Path.Combine(work, "bad", "Bad.csproj"));
        run.Check(bad.ExitCode == 1, "a broken entry should exit 1, got " + bad.ExitCode);
        run.Check(bad.Text.Split('\n').Any(l => l.Contains("Bad.cs", StringComparison.Ordinal) && l.Contains("NoSuchType", StringComparison.Ordinal)),
            "the diagnostic does not name the problem");

        var none = await Check();
        run.Check(none.ExitCode == 1, "no entry should exit 1, got " + none.ExitCode);
        run.Check(none.Text.Contains("usage: lub check", StringComparison.Ordinal), "no entry should print the usage");

        var missing = await Check(Path.Combine(work, "missing", "Missing.csproj"));
        run.Check(missing.ExitCode == 1, "a missing entry should exit 1, got " + missing.ExitCode);
    }

    /// <summary>The `tcs vm --watch` that `lub &lt;Entry&gt;.csproj` starts does not remain after lub ends.
    /// tcs is started in 2 ways: directly (as the tcs of the release kit is), and by `dotnet run` with tcs as its child.
    /// lub ends in 2 ways: by itself after a capture, and killed at once (SIGKILL, or TerminateProcess on Windows).
    /// The processes are found by the name of the entry class in their command lines.</summary>
    private static async Task ProcessCleanup(Run run, string launcher, string how)
    {
        var site = run.Site;
        await Prepare.Lub(run);
        var tcs = await Prepare.TcsEnv(run);
        string token = "TcsCleanup" + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(run.Temp, token);
        Write(Path.Combine(dir, token + ".csproj"), "");
        Write(Path.Combine(dir, token + ".cs"), $$"""
            using Lub;

            public static class {{token}}
            {
                public static void OnInit()
                {
                    App.Config(new ConfigOpts { Width = 64, Height = 64 });
                }

                public static void OnFrame(float dt)
                {
                }
            }
            """);
        // Without LUB_TCS, lub starts the tcs of the checkout with dotnet run
        var env = new Dictionary<string, string?> { ["LUB_TCS"] = launcher == "direct" ? tcs["LUB_TCS"] : null };
        // No window is needed, and the Linux CI has no display
        if (site.Linux) env["SDL_VIDEO_DRIVER"] = "offscreen";
        if (site.Windows)
        {
            env["LUB_BACKEND"] = "d3d12";
            env["LUB_D3D12_WARP"] = "1";
        }
        // Killed at once, lub gets no capture, so that it does not end by itself
        string[] capture = how == "exit" ? ["--capture", Path.Combine(dir, "out.png"), "--capture-frame", "5", "--fixed-dt", CsRuntime.FixedDt] : [];
        var lub = run.Start(site.Lub, [Path.Combine(dir, token + ".csproj"), .. capture], env);
        if (how == "kill")
        {
            // The first output tcs writes: once it is there, tcs is running
            var module = Path.Combine(dir, ".lub", token + ".tcsb");
            for (int i = 0; i < 1800 && !File.Exists(module) && lub.Running; i++) await Task.Delay(100);
            run.Check(File.Exists(module) && lub.Running, "lub ended before tcs wrote its output");
            lub.Kill();
            await lub.Wait(TimeSpan.FromSeconds(30));
        }
        else if (!await lub.Wait(TimeSpan.FromMinutes(3)))
        {
            lub.Kill();
            run.Fail("lub did not exit");
        }
        string left = "";
        for (int i = 0; i < 20; i++)
        {
            left = await ProcessesWith(run, token);
            if (left == "") break;
            await Task.Delay(500);
        }
        if (left == "") return;
        foreach (var line in left.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(line.TrimStart().Split(' ')[0], out int pid)) continue;
            try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
        }
        run.Fail("still running after lub ended:\n" + left);
    }

    /// <summary>Lines of `<pid> <command line>` of the processes whose command line contains the text (not counting the one that lists them).</summary>
    private static async Task<string> ProcessesWith(Run run, string text)
    {
        if (run.Site.Windows)
        {
            var listed = await run.Exec("powershell", ["-NoProfile", "-Command",
                $"Get-CimInstance Win32_Process | Where-Object {{ $_.CommandLine -like '*{text}*' -and $_.ProcessId -ne $PID }} | ForEach-Object {{ '{{0}} {{1}}' -f $_.ProcessId, $_.CommandLine }}"]);
            return string.Join("\n", listed.Text.Split('\n').Select(l => l.Trim('\r', ' ')).Where(l => l != ""));
        }
        var all = await run.Exec("ps", ["-axo", "pid=,command="]);
        return string.Join("\n", all.Text.Split('\n').Where(l => l.Contains(text, StringComparison.Ordinal) && !l.Contains("ps -axo", StringComparison.Ordinal)));
    }

    /// <summary>lub takes the frames out of the standard output of the tcs child process (tests/c/tcs_pipeline_smoke.c).
    /// Instead of tcs vm --watch, a fake that writes frames in one write and then waits is given as LUB_TCS: this program started with `fake-tcs`.</summary>
    private static async Task Pipeline(Run run)
    {
        var site = run.Site;
        await Prepare.Target(run, "lub_tcs_pipeline_smoke");
        // LUB_TCS is split on spaces
        var self = Path.Combine(AppContext.BaseDirectory, "lub-test.dll").Replace('\\', '/');
        run.Check(!self.Contains(' '), "the path of the test runner has a space: " + self);
        var output = await run.Exec(site.Exe("lub_tcs_pipeline_smoke"), [], new Dictionary<string, string?> { ["LUB_TCS"] = "dotnet " + self + " fake-tcs" },
            timeout: TimeSpan.FromMinutes(2));
        run.Check(output.Ok, "lub_tcs_pipeline_smoke exited with " + output.ExitCode);
    }

    /// <summary>What the fake tcs writes: a frame of an unknown kind, a diff, and a compile error, in a single write.
    /// The body of the unknown frame has lines that look like frame headers, and multi-byte UTF-8: unless lub skips the body by its byte count, a fake diff appears.</summary>
    public static byte[] FakeTcsOutput()
    {
        static string Frame(string kind, string body) => $"@@tcs_{kind} {Encoding.UTF8.GetByteCount(body)}\n{body}";
        return Encoding.UTF8.GetBytes(Frame("future_kind", "@@tcs_vm_batch 5\nWRONG\n\u2713") + Frame("vm_batch", "QUJDREVG") + Frame("build_failed", "oops!"));
    }
}
