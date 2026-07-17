using TinyCs;
using TinyCs.Luoc;

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
            throw new LuocException("TinyC# diagnostics:\n" +
                string.Join("\n", exported.Diagnostics));

        var facts = SourceFacts.Create(sources, options.Inputs);
        var c = new CEmitter(exported, facts).Emit(options.EntryClass);
        if (options.OutputPath is null)
            Console.Write(c);
        else
            File.WriteAllText(options.OutputPath, c);
        return 0;
    }
    catch (LuocException ex)
    {
        Console.Error.WriteLine($"luoc: {ex.Message}");
        return 1;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"luoc: {ex.Message}");
        return 1;
    }
}

file sealed record Options(
    IReadOnlyList<string> Inputs,
    string? OutputPath,
    string? EntryClass,
    bool Help)
{
    public const string Usage =
        "usage: luoc [--entry CLASS] [-o OUTPUT.c] INPUT.cs [INPUT.cs ...]";

    public static Options Parse(string[] args)
    {
        var inputs = new List<string>();
        string? output = null;
        string? entry = null;
        var help = false;

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
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                        throw new LuocException($"unknown option: {args[i]}\n{Usage}");
                    inputs.Add(args[i]);
                    break;
            }
        }

        if (!help && inputs.Count == 0)
            throw new LuocException(Usage);
        return new Options(inputs, output, entry, help);
    }

    private static string TakeValue(string[] args, ref int i)
    {
        if (++i >= args.Length)
            throw new LuocException($"missing value for {args[i - 1]}\n{Usage}");
        return args[i];
    }
}
