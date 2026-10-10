using System.Diagnostics;

namespace TinyCs.Tests;

public class WatchModeTests
{
    [Fact]
    public void Watch_OutputCollision_ExitsBeforeInitialBuild()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(),
            $"tcs_watch_collision_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var inputPath = Path.Combine(tmpDir, "test.cs");
        var source = """
            public static class T
            {
                public static int Value() => 1;
            }
            """;
        File.WriteAllText(inputPath, source);

        try
        {
            var psi = CreateTranspilerProcess(
                inputPath, "-o", inputPath, "--watch", "--no-runtime");

            using var proc = Process.Start(psi)!;
            try
            {
                var exited = proc.WaitForExit(5000);
                Assert.True(exited, "watch must reject a colliding output before waiting");
                var stderr = proc.StandardError.ReadToEnd();
                Assert.Equal(1, proc.ExitCode);
                Assert.Contains("conflicts with input", stderr);
                Assert.Equal(source, File.ReadAllText(inputPath));
            }
            finally
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void Watch_HardLinkOutputCollision_ExitsBeforeInitialBuild()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()
            && !OperatingSystem.IsMacOS()) return;

        var tmpDir = Path.Combine(Path.GetTempPath(),
            $"tcs_watch_link_collision_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var inputPath = Path.Combine(tmpDir, "test.cs");
        var outputPath = Path.Combine(tmpDir, "output.lua");
        var source = """
            public static class T
            {
                public static int Value() => 1;
            }
            """;
        File.WriteAllText(inputPath, source);
        FileLinkTestHelper.CreateHardLink(outputPath, inputPath);
        var originalWriteTime = File.GetLastWriteTimeUtc(inputPath);

        try
        {
            var psi = CreateTranspilerProcess(
                inputPath, "-o", outputPath, "--watch", "--no-runtime");

            using var proc = Process.Start(psi)!;
            try
            {
                var exited = proc.WaitForExit(5000);
                Assert.True(exited,
                    "watch must reject a hard-link output before waiting");
                var stderr = proc.StandardError.ReadToEnd();
                Assert.Equal(1, proc.ExitCode);
                Assert.Contains("conflicts with input", stderr);
                Assert.Equal(source, File.ReadAllText(inputPath));
                Assert.Equal(originalWriteTime,
                    File.GetLastWriteTimeUtc(inputPath));
            }
            finally
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void Watch_FileChange_TriggersRebuild()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"tcs_watch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var inputPath = Path.Combine(tmpDir, "test.cs");
        var outputPath = Path.Combine(tmpDir, "test.lua");

        try
        {
            // Write V1
            File.WriteAllText(inputPath, """
                public class Hello
                {
                    public static int Value() { return 1; }
                }
                """);

            // Start tcs --watch
            var psi = CreateTranspilerProcess(
                inputPath, "-o", outputPath, "--watch");

            using var proc = Process.Start(psi)!;

            try
            {
                // Wait for initial build
                WaitForFile(outputPath, timeoutMs: 15000);
                var v1 = File.ReadAllText(outputPath);
                Assert.Contains("Hello", v1);

                // Write V2 (change return value)
                Thread.Sleep(500); // let watcher settle
                File.WriteAllText(inputPath, """
                    public class Hello
                    {
                        public static int Value() { return 42; }
                    }
                    """);

                // Wait for rebuild (output file should change)
                WaitForFileChange(outputPath, v1, timeoutMs: 5000);
                var v2 = File.ReadAllText(outputPath);
                Assert.Contains("42", v2);
            }
            finally
            {
                // dotnet run の子プロセス (Transpiler --watch) ごと止める
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    [Fact]
    public void Watch_RefFileChange_TriggersRebuild()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"tcs_watch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var inputPath = Path.Combine(tmpDir, "test.cs");
        var refPath = Path.Combine(tmpDir, "engine.cs");
        var outputPath = Path.Combine(tmpDir, "test.lua");

        try
        {
            File.WriteAllText(inputPath, """
                using Engine;

                public class T
                {
                    public static int Value() { return Api.Value(); }
                }
                """);
            File.WriteAllText(refPath, """
                namespace Engine;
                public static class Api
                {
                    public static int Value() => default!;
                }
                """);

            var psi = CreateTranspilerProcess(
                inputPath, "--ref", refPath, "-o", outputPath, "--watch");

            using var proc = Process.Start(psi)!;

            try
            {
                WaitForFile(outputPath, timeoutMs: 15000);
                var initialWrite = File.GetLastWriteTimeUtc(outputPath);

                Thread.Sleep(500);
                File.WriteAllText(refPath, """
                    namespace Engine;
                    public static class Api
                    {
                        public static int Value() => default!;
                        public static int Other() => default!;
                    }
                    """);

                WaitForFileWriteAfter(outputPath, initialWrite, timeoutMs: 5000);
            }
            finally
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    // ホスト (lub) は watch の初回出力を module として読み、以後は --reload-chunks が
    // 標準出力に順に書く chunk を同じ VM で順に実行する。ホストが当てる前に 2 回 build
    // が終わっても (デバッガ停止・長いフレーム)、2 つの chunk を順に当てれば途中の
    // build で足した field も初期化され、初期化済みの状態が更新後の method から読める
    [Fact]
    public void Watch_ReloadChunks_ApplyInOrderAcrossMissedFrames()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"tcs_watch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var inputPath = Path.Combine(tmpDir, "Game.cs");
        var refPath = Path.Combine(tmpDir, "engine.cs");
        var outputPath = Path.Combine(tmpDir, "Game.lua");
        const string Game = """
            using Engine;

            public class State
            {
                public int V;
            }

            public static class Game
            {
                static State? state;
                static int frames = 0;
                public static void OnInit() { state = new State { V = Api.Base(new Opts { W = 40 }) }; }
                public static int OnFrame()
                {
                    frames = frames + 1;
                    return state!.V + frames;
                }
            }
            """;
        var withField = Game
            .Replace("public int V;", "public int V;\n    public int Bonus = 5;")
            .Replace("state!.V + frames", "state!.V + state.Bonus + frames");
        var usingField = withField
            .Replace("state!.V + state.Bonus + frames", "(state!.V + state.Bonus) * 10 + frames");

        try
        {
            File.WriteAllText(inputPath, Game);
            File.WriteAllText(refPath, """
                namespace Engine;
                public class Opts { public int W; }
                public static class Api
                {
                    public static int Base(Opts opts) => default!;
                }
                """);

            var psi = CreateTranspilerProcess(inputPath, "--ref", refPath,
                "-o", outputPath, "--entry", "Game", "--watch", "--reload-chunks");

            using var proc = Process.Start(psi)!;
            var chunks = new ReloadChunkReader(proc.StandardOutput.BaseStream);

            try
            {
                WaitForFile(outputPath, timeoutMs: 15000);
                var v1 = File.ReadAllText(outputPath);

                // ホストが 1 つ目を当てる前に 2 回目の build が終わる
                Thread.Sleep(500);
                File.WriteAllText(inputPath, withField);
                var addField = chunks.Next(timeoutMs: 5000);
                Thread.Sleep(500);
                File.WriteAllText(inputPath, usingField);
                var useField = chunks.Next(timeoutMs: 5000);

                const string Host = "engine = { api = { base = function(opts) return opts.w + 2 end } }";
                var inOrder = $$"""
                    {{Host}}
                    local m = assert(load({{LuaLongString(v1)}}, "=v1"))()
                    m.on_init()
                    local before = m.on_frame()
                    local add = assert(load({{LuaLongString(addField)}}, "=add"))
                    add()
                    assert(load({{LuaLongString(useField)}}, "=use"))()
                    add() -- 既に含む chunk は何もしない
                    print(before, m.on_frame())
                    """;
                Assert.Equal("43\t472", TestHelper.RunLua(inOrder).Trim());

                // 1 つ目を飛ばして 2 つ目だけ当てると、何も変えずに失敗する
                var skipped = $$"""
                    {{Host}}
                    local m = assert(load({{LuaLongString(v1)}}, "=v1"))()
                    m.on_init()
                    m.on_frame()
                    local ok, err = pcall(assert(load({{LuaLongString(useField)}}, "=use")))
                    print(ok, err:find("restart the host", 1, true) ~= nil, m.on_frame())
                    """;
                Assert.Equal("false\ttrue\t44", TestHelper.RunLua(skipped).Trim());
            }
            finally
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    // tcs --reload-chunks の標準出力 (`@@tcs_reload_chunk <byte 数>` の行 + 本文) を読む
    private sealed class ReloadChunkReader
    {
        private const string Header = "@@tcs_reload_chunk ";
        private readonly System.Collections.Concurrent.BlockingCollection<string> _chunks = new();

        public ReloadChunkReader(Stream stdout)
        {
            new Thread(() =>
            {
                try
                {
                    while (ReadLine(stdout) is { } line)
                    {
                        if (!line.StartsWith(Header, StringComparison.Ordinal)) continue;
                        var body = new byte[int.Parse(line[Header.Length..])];
                        stdout.ReadExactly(body);
                        _chunks.Add(System.Text.Encoding.UTF8.GetString(body));
                    }
                }
                catch (IOException) { }
                finally { _chunks.CompleteAdding(); }
            }) { IsBackground = true }.Start();
        }

        public string Next(int timeoutMs) =>
            _chunks.TryTake(out var chunk, timeoutMs)
                ? chunk
                : throw new TimeoutException($"no reload chunk within {timeoutMs}ms");

        private static string? ReadLine(Stream stream)
        {
            var bytes = new List<byte>();
            for (var b = stream.ReadByte(); b >= 0; b = stream.ReadByte())
            {
                if (b == '\n') return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
                bytes.Add((byte)b);
            }
            return null;
        }
    }

    private static string LuaLongString(string text)
    {
        var eq = "=";
        while (text.Contains($"]{eq}]", StringComparison.Ordinal)) eq += "=";
        return $"[{eq}[\n{text}]{eq}]";
    }

    private static void WaitForFile(string path, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return;
            Thread.Sleep(100);
        }
        throw new TimeoutException($"File {path} not created within {timeoutMs}ms");
    }

    private static void WaitForFileChange(string path, string oldContent, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path))
            {
                var current = File.ReadAllText(path);
                if (current != oldContent)
                    return;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"File {path} did not change within {timeoutMs}ms");
    }

    private static void WaitForFileWriteAfter(string path, DateTime oldWriteTimeUtc,
        int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path)
                && File.GetLastWriteTimeUtc(path) > oldWriteTimeUtc)
            {
                return;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"File {path} was not rewritten within {timeoutMs}ms");
    }

    // 出力へ埋め込まれる --prelude も build dependency として監視する。
    // 無関係ファイルの変更では rebuild しない。
    [Fact]
    public void Watch_PreludeChange_TriggersRebuild()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"tcs_watch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var inputPath = Path.Combine(tmpDir, "test.cs");
        var preludePath = Path.Combine(tmpDir, "shim.lua");
        var outputPath = Path.Combine(tmpDir, "out.lua");

        try
        {
            File.WriteAllText(inputPath, """
                public class Hello
                {
                    public static int Value() { return 1; }
                }
                """);
            File.WriteAllText(preludePath, "-- prelude v1\n");

            var psi = CreateTranspilerProcess(
                inputPath, "-o", outputPath, "--prelude", preludePath, "--watch");

            using var proc = Process.Start(psi)!;

            try
            {
                WaitForFile(outputPath, timeoutMs: 15000);
                var v1 = File.ReadAllText(outputPath);
                Assert.Contains("prelude v1", v1);

                // prelude だけを変更 (C# は無変更) → rebuild される
                Thread.Sleep(500);
                File.WriteAllText(preludePath, "-- prelude v2\n");
                WaitForFileChange(outputPath, v1, timeoutMs: 5000);
                var v2 = File.ReadAllText(outputPath);
                Assert.Contains("prelude v2", v2);

                // 無関係の .lua ファイルでは rebuild しない
                Thread.Sleep(500);
                var lastWrite = File.GetLastWriteTimeUtc(outputPath);
                File.WriteAllText(Path.Combine(tmpDir, "unrelated.lua"), "-- x\n");
                Thread.Sleep(1500);
                Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(outputPath));
            }
            finally
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }

    private static ProcessStartInfo CreateTranspilerProcess(
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}
