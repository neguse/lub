namespace TinyCs;

public class Program
{
    private const string Version = "0.1.0";

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--map-stacktrace")
            return RunMapStackTrace(args);

        if (args.Length > 0 && args[0] == "check")
            return RunCheck(args[1..]);

        if (args.Length == 0)
        {
            PrintUsage(Console.Error);
            return 1;
        }

        if (args is ["--help"] or ["-h"])
        {
            PrintUsage(Console.Out);
            return 0;
        }

        if (args is ["--version"])
        {
            Console.WriteLine($"tcs {Version}");
            return 0;
        }

        var inputPaths = new List<string>();
        var refPaths = new List<string>();
        string? outputPath = null;
        string? entryClass = null;
        string? preludePath = null;
        bool emitSourceMap = false;
        bool watchMode = false;
        bool includeRuntime = true;
        bool checkNaming = true;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-o")
            {
                if (IsMissingOptionValue(args, i))
                    return Error("missing value for -o");
                outputPath = args[++i];
            }
            else if (args[i] == "--ref")
            {
                if (IsMissingOptionValue(args, i))
                    return Error("missing value for --ref");
                refPaths.Add(args[++i]);
            }
            else if (args[i] == "--entry")
            {
                if (IsMissingOptionValue(args, i))
                    return Error("missing value for --entry");
                entryClass = args[++i];
            }
            else if (args[i] == "--prelude")
            {
                if (IsMissingOptionValue(args, i))
                    return Error("missing value for --prelude");
                preludePath = args[++i];
            }
            else if (args[i] == "--sourcemap")
            {
                emitSourceMap = true;
            }
            else if (args[i] == "--watch" || args[i] == "-w")
            {
                watchMode = true;
            }
            else if (args[i] == "--no-runtime")
            {
                includeRuntime = false;
            }
            else if (args[i] == "--no-naming-check")
            {
                checkNaming = false;
            }
            else if (!args[i].StartsWith('-'))
            {
                inputPaths.Add(args[i]);
            }
            else
            {
                return Error($"unknown option: {args[i]}");
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

        foreach (var path in refPaths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Error: ref file not found: {path}");
                return 1;
            }
        }

        if (preludePath != null && !File.Exists(preludePath))
        {
            Console.Error.WriteLine($"Error: prelude file not found: {preludePath}");
            return 1;
        }

        if (watchMode)
        {
            if (outputPath == null)
            {
                Console.Error.WriteLine("Error: --watch requires -o <output.lua>");
                return 1;
            }
            return RunWatch(inputPaths, refPaths, new BuildOptions(
                outputPath, entryClass, preludePath, emitSourceMap,
                includeRuntime, checkNaming));
        }

        return RunOnce(inputPaths, refPaths, new BuildOptions(
            outputPath, entryClass, preludePath, emitSourceMap,
            includeRuntime, checkNaming));
    }

    private sealed record BuildOptions(string? OutputPath, string? EntryClass,
        string? PreludePath, bool EmitSourceMap, bool IncludeRuntime,
        bool CheckNaming);

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: tcs <input.cs> [input2.cs ...] [--ref <ref.cs>] [-o <output.lua>] [--entry <Class>] [--prelude <shim.lua>] [--sourcemap] [--watch] [--no-runtime] [--no-naming-check]");
        writer.WriteLine("       tcs check <input.cs> [input2.cs ...] [--ref <ref.cs>] [--no-naming-check]");
        writer.WriteLine("       tcs --map-stacktrace <output.lua.map> [trace.txt]");
        writer.WriteLine("       tcs --help");
        writer.WriteLine("       tcs --version");
        writer.WriteLine("       tcs <input.cs>              # prints to stdout");
        writer.WriteLine("       tcs check <input.cs>        # diagnostics only, no Lua output");
        writer.WriteLine("       --ref <file.cs>             # type-check only (no Lua output)");
        writer.WriteLine("       --entry <Class>             # append 'return <Class>' so the output loads as a Lua module");
        writer.WriteLine("       --no-naming-check           # suppress C# naming convention warnings (host wire-format code)");
        writer.WriteLine("       --prelude <shim.lua>        # prepend a user Lua file (host API shim, etc.) to the output");
        writer.WriteLine("       --no-runtime                # omit embedded TinySystem runtime prelude");
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }

    private static bool IsMissingOptionValue(string[] args, int optionIndex) =>
        optionIndex + 1 >= args.Length
        || args[optionIndex + 1].StartsWith('-');

    private static int RunMapStackTrace(string[] args)
    {
        if (args.Length < 2 || args.Length > 3)
        {
            Console.Error.WriteLine("Usage: tcs --map-stacktrace <output.lua.map> [trace.txt]");
            Console.Error.WriteLine("       If trace.txt is omitted, stack trace is read from stdin.");
            return 1;
        }

        var mapPath = args[1];
        if (!File.Exists(mapPath))
        {
            Console.Error.WriteLine($"Error: source map not found: {mapPath}");
            return 1;
        }

        try
        {
            var sourceMapJson = File.ReadAllText(mapPath);
            var stackTrace = args.Length == 3
                ? File.ReadAllText(args[2])
                : Console.In.ReadToEnd();
            Console.Write(SourceMapResolver.AnnotateStackTrace(stackTrace,
                sourceMapJson));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int RunCheck(string[] args)
    {
        var inputPaths = new List<string>();
        var refPaths = new List<string>();
        bool checkNaming = true;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--ref")
            {
                if (IsMissingOptionValue(args, i))
                    return Error("missing value for --ref");
                refPaths.Add(args[++i]);
            }
            else if (args[i] == "--no-naming-check")
            {
                checkNaming = false;
            }
            else if (!args[i].StartsWith('-'))
            {
                inputPaths.Add(args[i]);
            }
            else
            {
                return Error($"unknown option for check: {args[i]}");
            }
        }

        if (inputPaths.Count == 0)
            return Error("no input file specified");

        foreach (var path in inputPaths)
        {
            if (!File.Exists(path))
                return Error($"file not found: {path}");
        }

        foreach (var path in refPaths)
        {
            if (!File.Exists(path))
                return Error($"ref file not found: {path}");
        }

        try
        {
            var sources = inputPaths.Select(File.ReadAllText).ToArray();
            var refSources = refPaths.Count > 0
                ? refPaths.Select(File.ReadAllText).ToArray() : null;
            var result = Transpiler.TranspileWithDiagnostics(sources,
                inputPaths.ToArray(), refSources, checkNaming: checkNaming);

            foreach (var err in result.Errors)
                Console.Error.WriteLine(err);
            foreach (var warn in result.Warnings)
                Console.Error.WriteLine(warn);

            return result.Success && result.Warnings.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int RunOnce(List<string> inputPaths, List<string> refPaths,
        BuildOptions options)
    {
        try
        {
            var sources = inputPaths.Select(File.ReadAllText).ToArray();
            var refSources = refPaths.Count > 0
                ? refPaths.Select(File.ReadAllText).ToArray() : null;
            var result = Transpiler.TranspileWithDiagnostics(sources, inputPaths.ToArray(),
                refSources, options.EntryClass, options.CheckNaming);

            if (!result.Success)
            {
                foreach (var err in result.Errors)
                    Console.Error.WriteLine(err);
                return 1;
            }

            foreach (var warn in result.Warnings)
                Console.Error.WriteLine(warn);

            var (lua, sourceMapLineOffset) = ComposeOutput(result.Lua, options);

            if (options.OutputPath is { } outputPath)
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(outputPath, lua);
                Console.Error.WriteLine($"Wrote {outputPath}");

                if (options.EmitSourceMap && result.SourceMap != null)
                {
                    var mapPath = outputPath + ".map";
                    File.WriteAllText(mapPath,
                        result.SourceMap.ToJson(sourceMapLineOffset));
                    Console.Error.WriteLine($"Wrote {mapPath}");
                }
            }
            else
            {
                Console.Write(lua);
                if (options.EmitSourceMap && result.SourceMap != null)
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

    // User prelude (--prelude) → runtime prelude の順で前置し、SourceMap の
    // Lua 行 offset を合算する。
    private static (string Lua, int SourceMapLineOffset) ComposeOutput(
        string lua, BuildOptions options)
    {
        var offset = 0;
        if (options.PreludePath is { } preludePath)
        {
            var prelude = File.ReadAllText(preludePath);
            if (!prelude.EndsWith('\n'))
                prelude += "\n";
            lua = prelude + lua;
            offset += prelude.Count(ch => ch == '\n');
        }
        if (options.IncludeRuntime)
        {
            var bundle = LuaRuntime.EmbedRuntime(lua);
            lua = bundle.Lua;
            offset += bundle.SourceMapLineOffset;
        }
        return (lua, offset);
    }

    private static int RunWatch(List<string> inputPaths, List<string> refPaths,
        BuildOptions options)
    {
        var watchPaths = inputPaths.Concat(refPaths).ToArray();
        Console.Error.WriteLine($"Watching {watchPaths.Length} file(s)... (Ctrl+C to stop)");

        // Initial build
        Rebuild(inputPaths, refPaths, options);

        // Set up file watchers for each unique directory
        var watchers = new List<FileSystemWatcher>();
        var watchedDirs = new HashSet<string>();
        var watchedFiles = new HashSet<string>(
            watchPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        foreach (var path in watchPaths)
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

                Rebuild(inputPaths, refPaths, options);
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

    private static void Rebuild(List<string> inputPaths, List<string> refPaths,
        BuildOptions options)
    {
        try
        {
            var sources = inputPaths.Select(File.ReadAllText).ToArray();
            var refSources = refPaths.Count > 0
                ? refPaths.Select(File.ReadAllText).ToArray() : null;
            var result = Transpiler.TranspileWithDiagnostics(sources, inputPaths.ToArray(),
                refSources, options.EntryClass, options.CheckNaming);

            if (!result.Success)
            {
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Build FAILED:");
                foreach (var err in result.Errors)
                    Console.Error.WriteLine($"  {err}");
                return;
            }

            foreach (var warn in result.Warnings)
                Console.Error.WriteLine($"  {warn}");

            var (lua, sourceMapLineOffset) = ComposeOutput(result.Lua, options);

            var outputPath = options.OutputPath!;
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, lua);

            if (options.EmitSourceMap && result.SourceMap != null)
                File.WriteAllText(outputPath + ".map",
                    result.SourceMap.ToJson(sourceMapLineOffset));

            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Built {outputPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error: {ex.Message}");
        }
    }
}
