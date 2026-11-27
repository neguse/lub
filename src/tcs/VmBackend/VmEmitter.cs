using System.Collections.Generic;
using TcsC;
using Tcs;

namespace TcsVm;

/// <summary>Builds a .tcsb from the IL output (Tcs.ExportResult) (docs/tcs/vm-design.md §15).
/// Only functions reachable from the entry and the static initializers are built. Errors are recorded in CFail, and the output is used only when there are no errors.</summary>
/// <summary>Description of a module's types and statics (compared with the old version in the reload plan).</summary>
public sealed class VmDescription
{
    public List<TypeTableEntry> Types = new List<TypeTableEntry>();
    public List<StaticKey> Statics = new List<StaticKey>();
}

public static class VmEmitter
{
    /// <summary>The diagnostics that stop the VM: all but the compliance errors it accepts (VmAccepted).</summary>
    public static List<string> Diagnostics(List<string> diagnostics)
    {
        var r = new List<string>();
        foreach (var d in diagnostics)
        {
            if (!VmAccepted.Is(d)) r.Add(d);
        }
        return r;
    }

    /// <summary>Extracts the IL that the VM accepts from the compile result. If there are blocking diagnostics, returns null and puts them in blocking.</summary>
    public static ExportResult? Exported(CompileResult result, List<Diagnostic> blocking)
    {
        int errors = 0;
        int stop = 0;
        foreach (var d in result.Diagnostics)
        {
            if (!d.IsError) continue;
            errors++;
            if (VmAccepted.Is(d.ToString())) continue;
            blocking.Add(d);
            stop++;
        }
        if (result.Export == null || stop > 0)
        {
            if (stop == 0) blocking.Add(Diagnostic.Plain("export failed"));
            return null;
        }
        var more = Diagnostics(result.Export.Diagnostics);
        if (more.Count > 0)
        {
            foreach (var d in more) blocking.Add(Diagnostic.Plain(d));
            return null;
        }
        // A body that could not be turned into IL becomes null, and the VM can only say "no IL body" about the caller, so report the reason and location here and stop
        if (result.Export.InternalErrors.Count > 0)
        {
            foreach (var ie in result.Export.InternalErrors) blocking.Add(Diagnostic.Plain("internal: " + ie));
            return null;
        }
        return result.Export;
    }

    /// <param name="entryClass">The class that has Main ("" if there is only one Main)</param>
    /// <param name="exports">Functions to build besides the entry (`C.M`; the host looks them up with tvm_find). `C.*` means all static methods of class C</param>
    public static List<int> Emit(ExportResult program, string entryClass, List<string> exports)
    {
        var m = Build(program, entryClass, exports);
        if (CFail.Failed) return new List<int>();
        return m.Write();
    }

    /// <summary>Same as Emit. Returns the description of the module's types and statics (the old version for the next EmitReload) in desc.</summary>
    public static List<int> Emit(ExportResult program, string entryClass, List<string> exports, out VmDescription desc)
    {
        desc = new VmDescription();
        var m = Build(program, entryClass, exports);
        if (CFail.Failed) return new List<int>();
        var module = m.Write();
        // The description is built from the module before writing (Describe may add types to the builder, so it is built separately from the module being written; same as the old version for reload)
        var d = Build(program, entryClass, exports);
        if (CFail.Failed) return new List<int>();
        desc.Types = d.Describe(out List<StaticKey> statics);
        desc.Statics = statics;
        return module;
    }

    /// <summary>batch (the form of the watch stream and tvm_apply_batch): u32 module length, module, u32 plan length, plan.</summary>
    public static List<int> Batch(List<int> module, List<int> plan)
    {
        var w = new List<int>();
        VBytes.I32(w, module.Count);
        VBytes.Bytes(w, module);
        VBytes.I32(w, plan.Count);
        VBytes.Bytes(w, plan);
        return w;
    }

    /// <summary>reload (docs/tcs/object-model.md §6): builds the old and new versions with the same entry / exports and returns the new module (the plan in plan,
    /// the restart reasons in reasons). The plan's byte sequence carries its own string table in front. The restart reasons follow the ReloadRules rules
    /// (ReloadRules; the caller computes them and passes them to restart). As functions, the new version gets the initializers of added (and retyped) instance fields, and of added statics and
    /// statics to re-evaluate (reinit; ReloadRules.ReinitStatics) (`type.&lt;reload-static&gt;`).</summary>
    public static List<int> EmitReload(ExportResult oldProgram, ExportResult newProgram, string entryClass, List<string> exports,
        List<string> restart, List<string> reinit, out List<int> plan, out List<string> reasons)
    {
        plan = new List<int>();
        reasons = new List<string>();
        var old = Build(oldProgram, entryClass, exports);
        if (CFail.Failed) return new List<int>();
        var oldDesc = new VmDescription();
        oldDesc.Types = old.Describe(out List<StaticKey> oldStatics);
        oldDesc.Statics = oldStatics;
        var module = EmitReload(oldDesc, newProgram, entryClass, exports, restart, reinit, out List<int> plan1, out List<string> reasons1,
            out VmDescription unused);
        plan = plan1;
        reasons = reasons1;
        return module;
    }

    /// <summary>Same as EmitReload. The old version is the description returned by the previous Emit / EmitReload (the old version is not rebuilt). Returns the new version's description in newDesc.</summary>
    public static List<int> EmitReload(VmDescription oldDesc, ExportResult newProgram, string entryClass, List<string> exports,
        List<string> restart, List<string> reinit, out List<int> plan, out List<string> reasons, out VmDescription newDesc)
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
        foreach (var key in reinit) added.Add(key);
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
        // Top-level statements become the static Main of class TopLevel (same as tcs2c)
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
            // The same goes for --ref stub classes (host data classes have no inheritance either)
            if (!c.IsInterface && c.BaseName != null)
            {
                CFail.Report("class inheritance is outside TinyC#" + (c.IsExternal ? " (the --ref class " + c.Name + " has a base class)" : ""));
                return m;
            }
        }

        // --trace: classes declared in the recorded files (classes with only statics and no methods also become roots of the value tree and of the state snapshot)
        foreach (var c in classes)
        {
            if (c.File != "" && m.IsTraced(c.File)) m.TraceClass(c.Name);
        }

        // Static initializers are listed first (the VM runs them in list order). Constant-only types first, lazy types after them
        // (a lazy type's function does nothing from the second call on. It is also called before static reads and writes)
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
        // Per-module mixing (X-4): instance methods of classes that implement a --ref module's interface are called from the C module
        // (tvm_invoke_method), so they are built even if they are unreachable inside the VM
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
        // Reload hooks (hot-reload §8): the instance OnReload and the static OnReload of the entry type
        // are built even if unreachable (tvm_apply calls them)
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
        RequestFieldInits(m);
        return m;
    }

    /// <summary>Builds the initializers of instance fields of classes in the type table (`type.&lt;init&gt;field`) even if they are unreachable.
    /// State loading (docs/tcs/vm-design.md §13.4) calls them to give fields missing from the state their initializer values.
    /// An initializer may add new classes to the type table, so this repeats until no more are added.</summary>
    private static void RequestFieldInits(ModuleBuilder m)
    {
        var done = new Dictionary<string, bool>();
        bool added = true;
        while (added && !CFail.Failed)
        {
            added = false;
            foreach (var name in m.UsedClasses())
            {
                if (done.ContainsKey(name)) continue;
                done[name] = true;
                var ci = m.ClassInfo(name);
                if (ci == null) continue;
                foreach (var f in ci.Fields)
                {
                    if (f.IsStatic || f.Init == null) continue;
                    var field = f;
                    string typeName = name;
                    m.Request(typeName + ".<init>" + field.Name, def => new FunctionCompiler(m, def, typeName).CompileFieldInit(field));
                    added = true;
                }
            }
            m.CompilePending();
        }
    }
}
