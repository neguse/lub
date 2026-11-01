using TinyCs;
using TinyCs.Vm;

return Run(args);

// --watch (doc/vm-design.md §13.3): 入力が変わるたびに直前の build からの reload を作り、-o を書き直してから
// 標準出力へ `@@tcs_<kind> <byte 数>` の行と本文を書く。kind は vm_batch (本文は batch の base64 の 1 行と改行。
// host は tvm_apply_batch で当てる) と restart_required (本文は改行終端の理由の行。host は -o を読み直す)。
// 診断のある編集は stderr に書いて、次の編集を待つ。以後の差分は最後に書いた build から作る
static int Watch(List<string> inputs, string[] refSources, string output, string? entry, List<string> exports)
{
    var stdout = Console.OpenStandardOutput();
    void Message(string kind, string text)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(text);
        stdout.Write(System.Text.Encoding.ASCII.GetBytes($"@@tcs_{kind} {body.Length}\n"));
        stdout.Write(body);
        stdout.Flush();
    }
    var session = new VmSession(entry, exports, refSources);
    string[] Read() => [.. inputs.Select(File.ReadAllText)];
    var first = session.Update(Read(), inputs);
    if (first.Kind != "module")
    {
        Console.Error.WriteLine("tcs2vm: " + string.Join("\n", first.Errors ?? []));
        return 1;
    }
    File.WriteAllBytes(output, first.Module!);
    DateTime Stamp() => inputs.Max(File.GetLastWriteTimeUtc);
    var seen = Stamp();
    Console.Error.WriteLine("tcs2vm: watching");
    while (true)
    {
        Thread.Sleep(100);
        var now = Stamp();
        if (now == seen) continue;
        seen = now;
        var r = session.Update(Read(), inputs);
        switch (r.Kind)
        {
            case "error":
                Console.Error.WriteLine("tcs2vm: " + string.Join("\n", r.Errors ?? []));
                break;
            case "restart":
                File.WriteAllBytes(output, r.Module!);
                Message("restart_required", string.Concat(r.Reasons!.Select(x => x + "\n")));
                break;
            default:
                File.WriteAllBytes(output, r.Module!);
                Message("vm_batch", System.Convert.ToBase64String(r.Batch!) + "\n");
                break;
        }
    }
}

static int Run(string[] args)
{
    var inputs = new List<string>();
    string? output = null, entry = null;
    var dump = false;
    var exports = new List<string>();
    var reloadFrom = new List<string>();
    var refs = new List<string>();
    var watch = false;
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
            case "--ref": refs.Add(args[++i]); break;
            case "--watch": watch = true; break;
            case "--dump-il": dump = true; break;
            case "-h" or "--help":
                Console.WriteLine("usage: tcs2vm [--entry CLASS] [--export CLASS.METHOD|CLASS.*] [--ref HOST_STUB.cs] [--watch] [--dump-il] [--reload-from OLD.cs ... --plan OUT.plan] [-o OUT.tcsb] INPUT.cs ...");
                return 0;
            default: inputs.Add(args[i]); break;
        }
    }
    try
    {
        var sources = inputs.Select(File.ReadAllText).ToArray();
        var refSources = refs.Select(File.ReadAllText).ToArray();
        var exported = IlExport.Export(sources, specializeGenerics: true, referenceSources: refSources, sourcePaths: [.. inputs]);
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
                referenceSources: refSources, sourcePaths: [.. reloadFrom]);
            if (old.Diagnostics.Length > 0)
                throw new VmException("TinyC# diagnostics (old version):\n" + string.Join("\n", old.Diagnostics));
            var (module, plan) = VmEmitter.EmitReload(old, exported, entry, exports);
            File.WriteAllBytes(output, module);
            File.WriteAllBytes(planOut, plan);
            return 0;
        }
        if (watch) return Watch(inputs, refSources, output, entry, exports);
        File.WriteAllBytes(output, VmEmitter.Emit(exported, entry, exports));
        return 0;
    }
    catch (VmException ex)
    {
        Console.Error.WriteLine($"tcs2vm: {ex.Message}");
        return 1;
    }
}
