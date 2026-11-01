namespace TinyCs.Vm;

/// <summary>IlExport の結果から .tcsb を作る (doc/vm-design.md §15)。
/// 関数は entry と static initializer から到達するものだけを作る。</summary>
internal static class VmEmitter
{
    /// <param name="entryClass">Main を持つ class (Main が 1 つなら省略できる)</param>
    /// <param name="exports">entry の他に作る関数 (`C.M`。host が tvm_find で引く)。
    /// `C.*` は class C の static method 全部</param>
    public static byte[] Emit(IlExportResult program, string? entryClass, IEnumerable<string>? exports = null)
    {
        if (program.TopLevel is not null) throw new VmException("top-level statements are not in the VM yet");
        var m = new ModuleBuilder(program);
        var classes = program.Classes.Where(c => !c.IsInterface && !c.IsExternal).ToList();
        if (classes.Any(c => c.BaseName is not null)) throw new VmException("class inheritance is outside TinyC#");

        // static initializer を先に並べる (VM は並びの順に走らせる)
        foreach (var c in classes)
        {
            var inits = c.Fields.Where(f => f.IsStatic && f.Init is not null).ToList();
            if (inits.Count > 0)
                m.Request(c.Name + ".<static>", def => new FunctionCompiler(m, def, c.Name).CompileStaticInit(inits));
        }

        {
            var mains = classes
                .Where(c => entryClass is null || c.Name == entryClass)
                .SelectMany(c => c.Methods.Where(x => x.Name == "Main" && x.IsStatic).Select(x => (c, x)))
                .ToList();
            if (mains.Count == 1)
            {
                var (c, main) = mains[0];
                m.Entry = m.Request(c.Name + ".Main", def => new FunctionCompiler(m, def, c.Name).CompileMethod(main));
            }
            else if (entryClass is not null) throw new VmException($"no Main in {entryClass}");
        }
        foreach (var export in exports ?? [])
        {
            var dot = export.LastIndexOf('.');
            var cls = classes.FirstOrDefault(c => c.Name == export[..dot] || c.DisplayName == export[..dot])
                ?? throw new VmException($"no class for export {export}");
            var name = export[(dot + 1)..];
            foreach (var method in cls.Methods.Where(x => x.IsStatic && (name == "*" || x.Name == name)))
                m.Request(cls.Name + "." + method.Name, def => new FunctionCompiler(m, def, cls.Name).CompileMethod(method));
        }
        m.CompilePending();
        return m.Write();
    }
}
