using TinyCs;
using TinyCs.Vm;

return Run(args);

static int Run(string[] args)
{
    var inputs = new List<string>();
    string? output = null, entry = null;
    var dump = false;
    var exports = new List<string>();
    var reloadFrom = new List<string>();
    string? planOut = null;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-o": output = args[++i]; break;
            case "--entry": entry = args[++i]; break;
            case "--export": exports.Add(args[++i]); break;
            case "--reload-from": reloadFrom.Add(args[++i]); break;
            case "--plan": planOut = args[++i]; break;
            case "--dump-il": dump = true; break;
            case "-h" or "--help":
                Console.WriteLine("usage: tcs2vm [--entry CLASS] [--export CLASS.METHOD|CLASS.*] [--dump-il] [--reload-from OLD.cs ... --plan OUT.plan] [-o OUT.tcsb] INPUT.cs ...");
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
        if (output is null) throw new VmException("-o is required");
        if (reloadFrom.Count > 0)
        {
            // reload: 旧版 (--reload-from) と新版から、新版の module と計画 (--plan) を作る (doc/object-model.md §6)
            if (planOut is null) throw new VmException("--plan is required with --reload-from");
            var old = IlExport.Export(reloadFrom.Select(File.ReadAllText).ToArray(), specializeGenerics: true,
                sourcePaths: [.. reloadFrom]);
            if (old.Diagnostics.Length > 0)
                throw new VmException("TinyC# diagnostics (old version):\n" + string.Join("\n", old.Diagnostics));
            var (module, plan) = VmEmitter.EmitReload(old, exported, entry, exports);
            File.WriteAllBytes(output, module);
            File.WriteAllBytes(planOut, plan);
            return 0;
        }
        File.WriteAllBytes(output, VmEmitter.Emit(exported, entry, exports));
        return 0;
    }
    catch (VmException ex)
    {
        Console.Error.WriteLine($"tcs2vm: {ex.Message}");
        return 1;
    }
}
