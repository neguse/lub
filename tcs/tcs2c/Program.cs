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
        var exported = IlExport.Export(sources);
        if (exported.Diagnostics.Length > 0)
            throw new Tcs2cException("TinyC# diagnostics:\n" +
                string.Join("\n", exported.Diagnostics));

        var c = new CEmitter(exported, options.DigestF32)
            .Emit(options.EntryClass, options.Lib);
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
    string? OutputPath,
    string? EntryClass,
    bool DigestF32,
    bool Help,
    bool Lib = false)
{
    public const string Usage =
        "usage: tcs2c [--entry CLASS] [--digest-f32] [--lib] [-o OUTPUT.c] " +
        "INPUT.cs [INPUT.cs ...]";

    public static Options Parse(string[] args)
    {
        var inputs = new List<string>();
        string? output = null;
        string? entry = null;
        var digestF32 = false;
        var help = false;
        var lib = false;

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
                case "--digest-f32":
                    digestF32 = true;
                    break;
                case "--lib":
                    lib = true;
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
        return new Options(inputs, output, entry, digestF32, help, lib);
    }

    private static string TakeValue(string[] args, ref int i)
    {
        if (++i >= args.Length)
            throw new Tcs2cException($"missing value for {args[i - 1]}\n{Usage}");
        return args[i];
    }
}
