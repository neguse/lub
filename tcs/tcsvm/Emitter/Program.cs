using TinyCs;
using TinyCs.Vm;

return Run(args);

static int Run(string[] args)
{
    var inputs = new List<string>();
    string? output = null, entry = null;
    var dump = false;
    var exports = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-o": output = args[++i]; break;
            case "--entry": entry = args[++i]; break;
            case "--export": exports.Add(args[++i]); break;
            case "--dump-il": dump = true; break;
            case "-h" or "--help":
                Console.WriteLine("usage: tcs2vm [--entry CLASS] [--export CLASS.METHOD|CLASS.*] [--dump-il] [-o OUT.tcsb] INPUT.cs ...");
                return 0;
            default: inputs.Add(args[i]); break;
        }
    }
    try
    {
        var sources = inputs.Select(File.ReadAllText).ToArray();
        var exported = IlExport.Export(sources, specializeGenerics: true, sourcePaths: [.. inputs]);
        if (exported.Diagnostics.Length > 0)
            throw new VmException("TinyC# diagnostics:\n" + string.Join("\n", exported.Diagnostics));
        if (dump)
        {
            Console.Write(IlDump.Program(exported));
            return 0;
        }
        var bytes = VmEmitter.Emit(exported, entry, exports);
        if (output is null) throw new VmException("-o is required");
        File.WriteAllBytes(output, bytes);
        return 0;
    }
    catch (VmException ex)
    {
        Console.Error.WriteLine($"tcs2vm: {ex.Message}");
        return 1;
    }
}
