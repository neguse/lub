using TinyCs;
using TinyCs.Tcs2c;

return Run(args);

static int Run(string[] args)
{
    try
    {
        var options = Options.Parse(args);
        if (options.Help)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        var sources = options.Inputs.Select(File.ReadAllText).ToArray();
        var exported = IlExport.Export(sources, specializeGenerics: true,
            referenceSources: options.References.Select(File.ReadAllText).ToArray(),
            sourcePaths: [.. options.Inputs]);
        if (exported.Diagnostics.Length > 0)
            throw new Tcs2cException("TinyC# diagnostics:\n" +
                string.Join("\n", exported.Diagnostics));

        var emitter = new CEmitter(exported, options.DigestF32);
        if (options.VmHost)
        {
            if (!options.Lib) throw new Tcs2cException("--vm-host needs --lib");
            emitter.VmHostSurface = IlExport.HostSurface(sources);
        }
        var c = emitter.Emit(options.EntryClass, options.Lib);
        if (options.OutputPath is null)
            Console.Write(c);
        else
            File.WriteAllText(options.OutputPath, c);
        return 0;
    }
    catch (Tcs2cException ex)
    {
        Console.Error.WriteLine($"tcs2c: {ex.Message}");
        return 1;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"tcs2c: {ex.Message}");
        return 1;
    }
}

file sealed record Options(
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> References,
    string? OutputPath,
    string? EntryClass,
    bool DigestF32,
    bool Help,
    bool Lib = false,
    bool VmHost = false)
{
    public const string Usage =
        "usage: tcs2c [--entry CLASS] [--digest-f32] [--lib [--vm-host]] [--ref STUB.cs] [-o OUTPUT.c] " +
        "INPUT.cs [INPUT.cs ...]";

    public static Options Parse(string[] args)
    {
        var inputs = new List<string>();
        var references = new List<string>();
        string? output = null;
        string? entry = null;
        var digestF32 = false;
        var help = false;
        var lib = false;
        var vmHost = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    help = true;
                    break;
                case "-o" or "--output":
                    output = TakeValue(args, ref i);
                    break;
                case "--entry":
                    entry = TakeValue(args, ref i);
                    break;
                case "--ref":
                    references.Add(TakeValue(args, ref i));
                    break;
                case "--digest-f32":
                    digestF32 = true;
                    break;
                case "--lib":
                    lib = true;
                    break;
                case "--vm-host":
                    vmHost = true;
                    break;
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                        throw new Tcs2cException($"unknown option: {args[i]}\n{Usage}");
                    inputs.Add(args[i]);
                    break;
            }
        }

        if (!help && inputs.Count == 0)
            throw new Tcs2cException(Usage);
        return new Options(inputs, references, output, entry, digestF32, help, lib, vmHost);
    }

    private static string TakeValue(string[] args, ref int i)
    {
        if (++i >= args.Length)
            throw new Tcs2cException($"missing value for {args[i - 1]}\n{Usage}");
        return args[i];
    }
}
