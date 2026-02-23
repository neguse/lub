namespace TinyCs;

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: tcs <input.cs> [input2.cs ...] [-o <output.lua>] [--sourcemap] [--watch]");
            Console.Error.WriteLine("       tcs <input.cs>              # prints to stdout");
            return 1;
        }

        var inputPaths = new List<string>();
        string? outputPath = null;
        bool emitSourceMap = false;
        bool watchMode = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
            else if (args[i] == "--sourcemap")
            {
                emitSourceMap = true;
            }
            else if (args[i] == "--watch" || args[i] == "-w")
            {
                watchMode = true;
            }
            else if (!args[i].StartsWith('-'))
            {
                inputPaths.Add(args[i]);
            }
        }

        if (inputPaths.Count == 0)
        {
            Console.Error.WriteLine("Error: no input file specified");
            return 1;
        }

        foreach (var path in inputPaths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Error: file not found: {path}");
                return 1;
            }
        }

        if (watchMode)
        {
            if (outputPath == null)
            {
                Console.Error.WriteLine("Error: --watch requires -o <output.lua>");
                return 1;
            }
            return RunWatch(inputPaths, outputPath, emitSourceMap);
        }

        return RunOnce(inputPaths, outputPath, emitSourceMap);
    }

    private static int RunOnce(List<string> inputPaths, string? outputPath, bool emitSourceMap)
    {
        try
        {
            var sources = inputPaths.Select(File.ReadAllText).ToArray();
            var result = Transpiler.TranspileWithDiagnostics(sources, inputPaths.ToArray());

            if (!result.Success)
            {
                foreach (var err in result.Errors)
                    Console.Error.WriteLine(err);
                return 1;
            }

            foreach (var warn in result.Warnings)
                Console.Error.WriteLine($"warning: {warn}");

            var lua = result.Lua;
            if (outputPath != null)
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(outputPath, lua);
                Console.Error.WriteLine($"Wrote {outputPath}");

                if (emitSourceMap && result.SourceMap != null)
                {
                    var mapPath = outputPath + ".map";
                    File.WriteAllText(mapPath, result.SourceMap.ToJson());
                    Console.Error.WriteLine($"Wrote {mapPath}");
                }
            }
            else
            {
                Console.Write(lua);
                if (emitSourceMap && result.SourceMap != null)
                {
                    Console.Error.WriteLine("--- sourcemap ---");
                    Console.Error.WriteLine(result.SourceMap.ToJson());
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int RunWatch(List<string> inputPaths, string outputPath, bool emitSourceMap)
    {
        Console.Error.WriteLine($"Watching {inputPaths.Count} file(s)... (Ctrl+C to stop)");

        // Initial build
        Rebuild(inputPaths, outputPath, emitSourceMap);

        // Set up file watchers for each unique directory
        var watchers = new List<FileSystemWatcher>();
        var watchedDirs = new HashSet<string>();
        var watchedFiles = new HashSet<string>(
            inputPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        foreach (var path in inputPaths)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            if (!watchedDirs.Add(dir)) continue;

            var watcher = new FileSystemWatcher(dir, "*.cs")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watchers.Add(watcher);
        }

        // Debounce: wait for changes to settle
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var pending = new ManualResetEventSlim(false);

        foreach (var watcher in watchers)
        {
            watcher.Changed += (_, e) =>
            {
                if (watchedFiles.Contains(Path.GetFullPath(e.FullPath)))
                    pending.Set();
            };
        }

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                pending.Wait(cts.Token);
                pending.Reset();

                // Debounce: wait 100ms for more changes
                Thread.Sleep(100);
                pending.Reset();

                Rebuild(inputPaths, outputPath, emitSourceMap);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var w in watchers) w.Dispose();
        }

        Console.Error.WriteLine("\nStopped.");
        return 0;
    }

    private static void Rebuild(List<string> inputPaths, string outputPath, bool emitSourceMap)
    {
        try
        {
            var sources = inputPaths.Select(File.ReadAllText).ToArray();
            var result = Transpiler.TranspileWithDiagnostics(sources, inputPaths.ToArray());

            if (!result.Success)
            {
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Build FAILED:");
                foreach (var err in result.Errors)
                    Console.Error.WriteLine($"  {err}");
                return;
            }

            foreach (var warn in result.Warnings)
                Console.Error.WriteLine($"  warning: {warn}");

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, result.Lua);

            if (emitSourceMap && result.SourceMap != null)
                File.WriteAllText(outputPath + ".map", result.SourceMap.ToJson());

            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Built {outputPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error: {ex.Message}");
        }
    }
}
