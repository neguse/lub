namespace TinyCs.Vm;

/// <summary>IlExport の結果から .tcsb を作る (doc/vm-design.md §15)。V1 は static method と
/// static field、class の instance field、struct の配列要素の範囲。</summary>
internal static class VmEmitter
{
    public static byte[] Emit(IlExportResult program, string? entryClass)
    {
        if (program.TopLevel is not null) throw new VmException("top-level statements are not in V1");
        var m = new ModuleBuilder(program);
        var classes = program.Classes.Where(c => !c.IsInterface && !c.IsExternal).ToList();

        // 関数の番号を先に決める (呼び出しが前方を参照できるように)
        var statics = new List<(IlClassInfo Class, FunctionDef Def)>();
        foreach (var c in classes)
        {
            var inits = c.Fields.Where(f => f.IsStatic && f.Init is not null).ToList();
            if (inits.Count > 0)
                statics.Add((c, m.Functions[m.DeclareFunction(c.Name + ".<static>")]));
        }
        var methods = new List<(IlClassInfo Class, IlMethodInfo Method, FunctionDef Def)>();
        foreach (var c in classes)
            foreach (var method in c.Methods)
            {
                if (!method.IsStatic) throw new VmException($"{c.Name}.{method.Name}: instance methods are not in V1");
                methods.Add((c, method, m.Functions[m.DeclareFunction(c.Name + "." + method.Name)]));
            }

        foreach (var (c, def) in statics)
            new FunctionCompiler(m, def, c.Name).CompileStaticInit(c.Fields.Where(f => f.IsStatic && f.Init is not null));
        foreach (var (c, method, def) in methods)
            new FunctionCompiler(m, def, c.Name).CompileMethod(method);

        var mains = methods.Where(x => x.Method.Name == "Main" && (entryClass is null || x.Class.Name == entryClass)).ToList();
        if (mains.Count == 1) m.Entry = m.FindFunction(mains[0].Class.Name + ".Main")!.Value;
        else if (entryClass is not null) throw new VmException($"no Main in {entryClass}");
        return m.Write();
    }
}
