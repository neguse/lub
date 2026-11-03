using System.Collections.Generic;
using TcsC;
using TcsSelf;

namespace TcsVm;

/// <summary>IL の書き出し (TcsSelf.ExportResult) から .tcsb を作る (doc/vm-design.md §15)。
/// 関数は entry と static initializer から到達するものだけを作る。誤りは CFail に記録し、出力は誤りが無いときだけ使う。</summary>
/// <summary>module の型と static の記述 (reload の計画で旧版と比べる)。</summary>
public sealed class VmDescription
{
    public List<TypeTableEntry> Types = new List<TypeTableEntry>();
    public List<StaticKey> Statics = new List<StaticKey>();
}

public static class VmEmitter
{
    /// <summary>VM で止める診断。`--ref` の interface の実装 (RefInterfaceImplementation) は Lua の名前の写し方の制約なので、
    /// C# の名前で呼ぶ VM と tcs2c の module の混在 (X-4) では止めない。`--ref` の class / interface への型 test と cast
    /// も、VM は型の表で判定できるので止めない (object-model §10.6)。</summary>
    public static List<string> Diagnostics(List<string> diagnostics)
    {
        var allowed = "RefInterfaceImplementation( RefTypeTest( RefCast( RefInterfaceTypeTest(".Split(' ');
        var r = new List<string>();
        foreach (var d in diagnostics)
        {
            bool ok = false;
            foreach (var p in allowed)
            {
                if (d.Contains("unsupported syntax: " + p)) ok = true;
            }
            if (!ok) r.Add(d);
        }
        return r;
    }

    /// <summary>compile の結果から VM が受ける IL を取り出す。止める診断があれば null で、diagnostics にその診断
    /// (旧 C# 版の IL の export と VmEmitter.Diagnostics を続けたもの)。</summary>
    public static ExportResult? Exported(CompileResult result, List<string> diagnostics)
    {
        var errors = new List<string>();
        foreach (var d in result.Diagnostics)
        {
            if (!d.Contains(": warning ") && !d.StartsWith("warning ")) errors.Add(d);
        }
        var stop = Diagnostics(errors);
        if (result.Export == null || stop.Count > 0)
        {
            if (errors.Count == 0) stop.Add("export failed");
            foreach (var d in stop) diagnostics.Add(d);
            return null;
        }
        var more = Diagnostics(result.Export.Diagnostics);
        if (more.Count > 0)
        {
            foreach (var d in more) diagnostics.Add(d);
            return null;
        }
        return result.Export;
    }

    /// <param name="entryClass">Main を持つ class (Main が 1 つなら "")</param>
    /// <param name="exports">entry の他に作る関数 (`C.M`。host が tvm_find で引く)。`C.*` は class C の static method 全部</param>
    public static List<int> Emit(ExportResult program, string entryClass, List<string> exports)
    {
        var m = Build(program, entryClass, exports);
        if (CFail.Failed) return new List<int>();
        return m.Write();
    }

    /// <summary>Emit と同じ。desc に module の型と static の記述 (次の EmitReload の旧版) を返す。</summary>
    public static List<int> Emit(ExportResult program, string entryClass, List<string> exports, out VmDescription desc)
    {
        desc = new VmDescription();
        var m = Build(program, entryClass, exports);
        if (CFail.Failed) return new List<int>();
        var module = m.Write();
        // 記述は書き出す前の module から作る (Describe は builder に型を足しうるので、書き出す module とは別に作る。reload の旧版と同じ)
        var d = Build(program, entryClass, exports);
        if (CFail.Failed) return new List<int>();
        desc.Types = d.Describe(out List<StaticKey> statics);
        desc.Statics = statics;
        return module;
    }

    /// <summary>batch (watch の stream と tvm_apply_batch の形): u32 の module の長さ、module、u32 の計画の長さ、計画。</summary>
    public static List<int> Batch(List<int> module, List<int> plan)
    {
        var w = new List<int>();
        VBytes.I32(w, module.Count);
        VBytes.Bytes(w, module);
        VBytes.I32(w, plan.Count);
        VBytes.Bytes(w, plan);
        return w;
    }

    /// <summary>reload (doc/object-model.md §6): 旧版と新版を同じ entry / exports で作り、新版の module を返す (plan に計画、
    /// reasons に restart の理由)。計画の byte 列は自分の string 表を前に持つ。restart の理由は Lua の reload と同じ規則
    /// (ReloadRules。呼ぶ側が求めて restart に渡す)。新版には、足した (と型を変えた) instance field の initializer と、足した static の initializer
    /// (`型.&lt;reload-static&gt;`) を関数として足す。</summary>
    public static List<int> EmitReload(ExportResult oldProgram, ExportResult newProgram, string entryClass, List<string> exports,
        List<string> restart, out List<int> plan, out List<string> reasons)
    {
        plan = new List<int>();
        reasons = new List<string>();
        var old = Build(oldProgram, entryClass, exports);
        if (CFail.Failed) return new List<int>();
        var oldDesc = new VmDescription();
        oldDesc.Types = old.Describe(out List<StaticKey> oldStatics);
        oldDesc.Statics = oldStatics;
        var module = EmitReload(oldDesc, newProgram, entryClass, exports, restart, out List<int> plan1, out List<string> reasons1,
            out VmDescription unused);
        plan = plan1;
        reasons = reasons1;
        return module;
    }

    /// <summary>EmitReload と同じ。旧版は前の Emit / EmitReload が返した記述 (旧版を作り直さない)。newDesc に新版の記述を返す。</summary>
    public static List<int> EmitReload(VmDescription oldDesc, ExportResult newProgram, string entryClass, List<string> exports,
        List<string> restart, out List<int> plan, out List<string> reasons, out VmDescription newDesc)
    {
        plan = new List<int>();
        reasons = new List<string>();
        newDesc = new VmDescription();
        var oldTypes = oldDesc.Types;
        var oldStatics = oldDesc.Statics;
        var m = Build(newProgram, entryClass, exports);
        if (CFail.Failed) return new List<int>();
        var types0 = m.Describe(out List<StaticKey> statics0);
        newDesc.Types = types0;
        newDesc.Statics = statics0;
        var first = ReloadPlan.Compute(oldTypes, types0, oldStatics, statics0, restart, null);

        var inits = new Dictionary<string, int>();
        foreach (var tp in first.Types)
        {
            if (tp.Change != ReloadPlan.Relayout) continue;
            var ci = m.ClassInfo(tp.Name);
            if (ci == null) continue;
            var fields = new List<ExField>();
            foreach (var f in ci.Fields)
            {
                if (!f.IsStatic) fields.Add(f);
            }
            for (int k = 0; k < fields.Count && k < tp.Fields.Count; k++)
            {
                var field = fields[k];
                if (tp.Fields[k].Source == ReloadPlan.FieldRetained || field.Init == null) continue;
                string typeName = tp.Name;
                inits[tp.Name + "\n" + field.Name] = m.Request(tp.Name + ".<init>" + field.Name,
                    def => new FunctionCompiler(m, def, typeName).CompileFieldInit(field));
            }
        }
        var added = new HashSet<string>();
        foreach (var s in first.Statics)
        {
            if (s.Change == ReloadPlan.Added) added.Add(s.Owner + "\n" + s.Field);
        }
        foreach (var o in m.StaticOwners())
        {
            var fresh = new List<ExField>();
            foreach (var f in o.Statics)
            {
                if (f.Init != null && added.Contains(o.Name + "\n" + f.Name)) fresh.Add(f);
            }
            if (fresh.Count == 0) continue;
            string owner = o.Name;
            m.Request(owner + ".<reload-static>", def => new FunctionCompiler(m, def, owner).CompileStaticInit(fresh, false));
        }
        m.CompilePending();
        if (CFail.Failed) return new List<int>();

        var newTypes = m.Describe(out List<StaticKey> newStatics);
        var p = ReloadPlan.Compute(oldTypes, newTypes, oldStatics, newStatics, restart, inits);
        var strings = new TypeTableStrings();
        foreach (var name in ReloadPlan.Names(p)) strings.Str(name);
        var w = new List<int>();
        VBytes.I32(w, strings.Strings.Count);
        foreach (var s in strings.Strings)
        {
            var b = VBytes.Utf8(s);
            VBytes.I32(w, b.Count);
            VBytes.Bytes(w, b);
        }
        ReloadPlan.Write(w, p, strings);
        var module = m.Write();
        plan = w;
        reasons = p.Reasons;
        return module;
    }

    private static ModuleBuilder Build(ExportResult program0, string entryClass0, List<string> exports)
    {
        var program = program0;
        string entryClass = entryClass0;
        // top-level 文は class TopLevel の static Main にする (tcs2c と同じ)
        if (program0.TopLevel != null)
        {
            program = new ExportResult();
            foreach (var c in program0.Classes) program.Classes.Add(c);
            foreach (var d in program0.Diagnostics) program.Diagnostics.Add(d);
            foreach (var s in program0.Structs) program.Structs.Add(s);
            foreach (var e in program0.Enums) program.Enums.Add(e);
            foreach (var e in program0.EnumTypes) program.EnumTypes.Add(e);
            foreach (var f in program0.ForeignMethods) program.ForeignMethods.Add(f);
            foreach (var v in program0.ForeignValues) program.ForeignValues.Add(v);
            var top = new ExClass();
            top.Name = "TopLevel";
            top.LayoutHash = "0";
            var main = new ExMethod();
            main.Name = "Main";
            main.IsStatic = true;
            main.Body = program0.TopLevel;
            main.ReturnType = "void";
            top.Methods.Add(main);
            program.Classes.Add(top);
            if (entryClass == "") entryClass = "TopLevel";
        }
        var m = new ModuleBuilder(program);
        var classes = new List<ExClass>();
        foreach (var c in program.Classes)
        {
            if (!c.IsInterface && !c.IsExternal) classes.Add(c);
        }
        foreach (var c in program.Classes)
        {
            // --ref の stub の class も同じ (host の data class も継承を持たない)
            if (!c.IsInterface && c.BaseName != null)
            {
                CFail.Report("class inheritance is outside TinyC#" + (c.IsExternal ? " (the --ref class " + c.Name + " has a base class)" : ""));
                return m;
            }
        }

        // static initializer を先に並べる (VM は並びの順に走らせる)。定数だけの型を先に、遅延の型を後に
        // (遅延の型の関数は 2 回目以降は何もしない。static の読み書きの前にも呼ぶ)
        var owners = m.StaticOwners();
        for (int pass = 0; pass < 2; pass++)
        {
            bool lazy = pass == 1;
            foreach (var o in owners)
            {
                var inits = new List<ExField>();
                foreach (var f in o.Statics)
                {
                    if (f.Init != null) inits.Add(f);
                }
                if (inits.Count == 0 || m.IsLazyStatic(o.Name) != lazy) continue;
                string name = o.Name;
                m.Request(name + ".<static>", def => new FunctionCompiler(m, def, name).CompileStaticInit(inits, lazy));
            }
        }

        var mainClasses = new List<ExClass>();
        var mains = new List<ExMethod>();
        foreach (var c in classes)
        {
            if (entryClass != "" && c.Name != entryClass) continue;
            foreach (var x in c.Methods)
            {
                if (x.Name == "Main" && x.IsStatic)
                {
                    mainClasses.Add(c);
                    mains.Add(x);
                }
            }
        }
        if (mains.Count == 1)
        {
            var mc = mainClasses[0];
            var main = mains[0];
            m.Entry = m.Request(mc.Name + ".Main", def => new FunctionCompiler(m, def, mc.Name).CompileMethod(main));
        }
        else if (entryClass != "")
        {
            CFail.Report("no Main in " + entryClass);
            return m;
        }
        foreach (var export in exports)
        {
            int dot = -1;
            var u = new TinySystem.Text.Utf8Bytes(export);
            for (int i = u.Length - 1; i >= 0; i--)
            {
                if (u[i] == '.')
                {
                    dot = i;
                    break;
                }
            }
            string head = ContractFacts.Sub(export, 0, dot < 0 ? 0 : dot);
            ExClass? cls = null;
            foreach (var c in classes)
            {
                if (c.Name == head || c.DisplayName == head)
                {
                    cls = c;
                    break;
                }
            }
            if (cls == null)
            {
                CFail.Report("no class for export " + export);
                return m;
            }
            string mname = ContractFacts.Sub(export, dot + 1, u.Length);
            foreach (var x in cls.Methods)
            {
                if (!x.IsStatic || !(mname == "*" || x.Name == mname)) continue;
                var ec = cls;
                var method = x;
                m.Request(ec.Name + "." + method.Name, def => new FunctionCompiler(m, def, ec.Name).CompileMethod(method));
            }
        }
        // module 単位の混在 (X-4): --ref の module の interface を実装する class の instance method は、C の module から
        // 呼ばれる (tvm_invoke_method) ので、VM の中で到達しなくても作る
        foreach (var c in classes)
        {
            bool hostImpl = false;
            foreach (var i in c.Interfaces)
            {
                ExClass? ic;
                if (!m.Classes.TryGetValue(m.CanonName(i), out ic) || !ic.IsInterface) hostImpl = true;
            }
            if (!hostImpl) continue;
            foreach (var x in c.Methods)
            {
                if (x.IsStatic || x.Body == null) continue;
                var hc = c;
                var method = x;
                m.Request(hc.Name + "." + method.Name, def => new FunctionCompiler(m, def, hc.Name).CompileMethod(method));
            }
        }
        // reload の hook (hot-reload §8): instance の OnReload と entry の型の static の OnReload は、
        // 到達しなくても作る (tvm_apply が呼ぶ)
        foreach (var c in classes)
        {
            foreach (var x in c.Methods)
            {
                if (x.Name != "OnReload" || x.Parameters.Count != 0) continue;
                if (x.IsStatic && !(m.Entry >= 0 && m.Functions[m.Entry].Name == c.Name + ".Main")) continue;
                var rc = c;
                var method = x;
                m.Request(rc.Name + "." + method.Name, def => new FunctionCompiler(m, def, rc.Name).CompileMethod(method));
            }
        }
        m.CompilePending();
        return m;
    }
}
