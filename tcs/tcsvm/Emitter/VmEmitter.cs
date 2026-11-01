using TinyCs.Backend;

namespace TinyCs.Vm;

/// <summary>IlExport の結果から .tcsb を作る (doc/vm-design.md §15)。
/// 関数は entry と static initializer から到達するものだけを作る。</summary>
internal static class VmEmitter
{
    /// <param name="entryClass">Main を持つ class (Main が 1 つなら省略できる)</param>
    /// <param name="exports">entry の他に作る関数 (`C.M`。host が tvm_find で引く)。
    /// `C.*` は class C の static method 全部</param>
    public static byte[] Emit(IlExportResult program, string? entryClass, IEnumerable<string>? exports = null) =>
        Build(program, entryClass, exports).Write();

    /// <summary>reload (doc/object-model.md §6): 旧版と新版を同じ entry / exports で作り、新版の module と計画を返す。
    /// 計画の byte 列は自分の string 表を前に持つ。restart の理由は Lua の reload と同じ規則 (ReloadClassifier)。
    /// 新版には、足した (と型を変えた) instance field の initializer と、足した static の initializer
    /// (`型.&lt;reload-static&gt;`) を関数として足す。</summary>
    public static (byte[] Module, byte[] Plan) EmitReload(IlExportResult oldProgram, IlExportResult newProgram,
        string? entryClass, IEnumerable<string>? exports = null)
    {
        var (module, plan, _) = EmitReloadWithReasons(oldProgram, newProgram, entryClass, exports);
        return (module, plan);
    }

    /// <summary>batch (watch の stream と tvm_apply_batch の形): u32 の module の長さ、module、u32 の計画の長さ、計画。</summary>
    public static byte[] Batch(byte[] module, byte[] plan)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(module.Length);
        w.Write(module);
        w.Write(plan.Length);
        w.Write(plan);
        return ms.ToArray();
    }

    public static (byte[] Module, byte[] Plan, IReadOnlyList<string> Reasons) EmitReloadWithReasons(
        IlExportResult oldProgram, IlExportResult newProgram, string? entryClass, IEnumerable<string>? exports = null)
    {
        var list = exports?.ToList() ?? [];
        var (oldTypes, oldStatics) = Build(oldProgram, entryClass, list).Describe();
        var m = Build(newProgram, entryClass, list);
        var reasons = ReloadClassifier.RestartReasons(oldProgram, newProgram);
        var (types0, statics0) = m.Describe();
        var first = ReloadPlan.Compute(oldTypes, types0, oldStatics, statics0, reasons);

        var inits = new Dictionary<(string Type, string Field), int>();
        foreach (var tp in first.Types.Where(t => t.Change == ReloadPlan.Relayout))
        {
            if (m.ClassInfo(tp.Name) is not { } ci) continue;
            var fields = ci.Fields.Where(f => !f.IsStatic).ToList();
            for (var k = 0; k < fields.Count && k < tp.Fields.Length; k++)
            {
                var field = fields[k];
                if (tp.Fields[k].Source == ReloadPlan.FieldRetained || field.Init is null) continue;
                inits[(tp.Name, field.Name)] = m.Request($"{tp.Name}.<init>{field.Name}",
                    def => new FunctionCompiler(m, def, tp.Name).CompileFieldInit(field));
            }
        }
        var added = first.Statics.Where(s => s.Change == ReloadPlan.Added).Select(s => (s.Owner, s.Field)).ToHashSet();
        foreach (var (owner, statics) in m.StaticOwners().ToList())
        {
            var fresh = statics.Where(f => f.Init is not null && added.Contains((owner, f.Name))).ToList();
            if (fresh.Count == 0) continue;
            m.Request(owner + ".<reload-static>", def => new FunctionCompiler(m, def, owner).CompileStaticInit(fresh));
        }
        m.CompilePending();

        var (newTypes, newStatics) = m.Describe();
        var plan = ReloadPlan.Compute(oldTypes, newTypes, oldStatics, newStatics, reasons, inits);
        var strings = new List<string>();
        var index = new Dictionary<string, int>();
        int Str(string s)
        {
            if (index.TryGetValue(s, out var i)) return i;
            index[s] = strings.Count;
            strings.Add(s);
            return strings.Count - 1;
        }
        foreach (var name in ReloadPlan.Names(plan)) Str(name);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, System.Text.Encoding.UTF8);
        w.Write(strings.Count);
        foreach (var s in strings)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            w.Write(b.Length);
            w.Write(b);
        }
        ReloadPlan.Write(w, plan, Str);
        return (m.Write(), ms.ToArray(), plan.Reasons);
    }

    private static ModuleBuilder Build(IlExportResult program, string? entryClass, IEnumerable<string>? exports)
    {
        // top-level 文は class TopLevel の static Main にする (tcs2c と同じ)
        if (program.TopLevel is { } top)
        {
            program = program with
            {
                Classes = [.. program.Classes, new IlClassInfo("TopLevel", null, [], "0",
                    [new IlMethodInfo("Main", true, [], top, "void", [])])],
                TopLevel = null,
            };
            entryClass ??= "TopLevel";
        }
        var m = new ModuleBuilder(program);
        var classes = program.Classes.Where(c => !c.IsInterface && !c.IsExternal).ToList();
        if (classes.Any(c => c.BaseName is not null)) throw new VmException("class inheritance is outside TinyC#");

        // static initializer を先に並べる (VM は並びの順に走らせる)。定数だけの型を先に、遅延の型を後に
        // (遅延の型の関数は 2 回目以降は何もしない。static の読み書きの前にも呼ぶ)
        var owners = m.StaticOwners().ToList();
        foreach (var lazy in new[] { false, true })
            foreach (var (name, statics) in owners)
            {
                var inits = statics.Where(f => f.Init is not null).ToList();
                if (inits.Count == 0 || m.IsLazyStatic(name) != lazy) continue;
                m.Request(name + ".<static>", def => new FunctionCompiler(m, def, name).CompileStaticInit(inits, lazy));
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
        // reload の hook (hot-reload §8): instance の OnReload と entry の型の static の OnReload は、
        // 到達しなくても作る (tvm_apply が呼ぶ)
        foreach (var c in classes)
            foreach (var method in c.Methods.Where(x => x.Name == "OnReload" && x.Parameters.IsEmpty
                && (!x.IsStatic || m.Entry >= 0 && m.Functions[m.Entry].Name == c.Name + ".Main")))
                m.Request(c.Name + "." + method.Name, def => new FunctionCompiler(m, def, c.Name).CompileMethod(method));
        m.CompilePending();
        return m;
    }
}
