using System.Collections.Generic;
using TcsC;
using TcsVm;

namespace Tcs;

// `tcs vm` as a job: a tcs VM module (.tcsb) in the result's Module.
// With OldSources, also the reload from that version: Plan, Batch and Reasons (docs/tcs/object-model.md §6).
// Step advances it: null while it continues, the result when it is done (the job does not keep the result). How much one Step does is not part of the contract.
public sealed class TcsToVm
{
    public TcsInputs Inputs = new TcsInputs();
    // The old version of a reload (compiled with the references of Inputs). Empty for a plain build
    public TcsInputs OldSources = new TcsInputs();
    // The class that has Main ("" if there is only one Main)
    public string Entry = "";
    // Functions to build besides the entry (`C.M`, or `C.*` for all static methods of class C)
    public List<string> Exports = new List<string>();
    // Path prefixes of the files whose statements and assignments are recorded
    public List<string> Traces = new List<string>();
    // Only the IL as text in Text (for diagnosis, non-normative)
    public bool DumpIl;

    public TcsResult? Step()
    {
        var r = new TcsResult();
        var program = Export(Inputs, r);
        if (program == null) return r;
        if (DumpIl)
        {
            r.Text = IlText(program);
            return r;
        }
        CFail.Error = "";
        VmTrace.Prefixes = Traces;
        if (OldSources.Texts.Count == 0)
        {
            r.Module = VmEmitter.Emit(program, Entry, Exports);
            return CFail.Failed ? Failed() : r;
        }
        OldSources.References = Inputs.References;
        var old = Export(OldSources, r);
        if (old == null)
        {
            r.OldVersion = true;
            return r;
        }
        r.Module = VmEmitter.EmitReload(old, program, Entry, Exports, RestartReasons(old, program), ReinitStatics(old, program), out List<int> plan, out List<string> reasons);
        if (CFail.Failed) return Failed();
        r.Plan = plan;
        r.Reasons = reasons;
        r.Batch = VmEmitter.Batch(r.Module, plan);
        return r;
    }

    // The IL that the VM accepts. null with r.Kind "error" and r.Blocking if the input stops the VM
    private static ExportResult? Export(TcsInputs inputs, TcsResult r)
    {
        return Accept(TcsCompile.IlSession(inputs, true).Compile(), r);
    }

    // Takes the IL that the VM accepts out of a compile result (also for a Session that a caller keeps across edits)
    public static ExportResult? Accept(CompileResult compiled, TcsResult r)
    {
        r.Diagnostics = compiled.Diagnostics;
        var blocking = new List<Diagnostic>();
        var program = VmEmitter.Exported(compiled, blocking);
        if (program == null)
        {
            r.Kind = "error";
            r.Blocking = blocking;
        }
        return program;
    }

    public static TcsResult Failed()
    {
        var r = TcsResult.Fail(CFail.Error);
        CFail.Error = "";
        return r;
    }

    // Reasons the edit cannot be applied live (ReloadRules)
    public static List<string> RestartReasons(ExportResult oldProgram, ExportResult newProgram) =>
        ReloadRules.RestartReasons(ReloadRules.TypesOf(oldProgram), ReloadRules.TypesOf(newProgram));

    // Statics that reload re-evaluates (ReloadRules)
    public static List<string> ReinitStatics(ExportResult oldProgram, ExportResult newProgram) =>
        ReloadRules.ReinitStatics(ReloadRules.TypesOf(oldProgram), ReloadRules.TypesOf(newProgram));

    // IL text for diagnosis (non-normative)
    public static string IlText(ExportResult program)
    {
        var lines = new List<string>();
        foreach (var c in program.Classes)
        {
            lines.Add("class " + c.Name);
            foreach (var f in c.Fields)
                lines.Add("  field " + (f.IsStatic ? "static " : "") + f.Name + ": " + f.Type + " = " + IlNodes.Dump(f.Init));
            foreach (var k in c.Ctors)
            {
                var ps = new List<string>();
                for (int i = 0; i < k.Parameters.Count && i < k.ParameterTypes.Count; i++) ps.Add(k.Parameters[i] + ": " + k.ParameterTypes[i]);
                lines.Add("  ctor " + k.Name + "(" + string.Join(", ", ps) + ")");
                if (k.Body != null) BlockLines(lines, k.Body, 2);
            }
            foreach (var m in c.Methods)
            {
                var ps = new List<string>();
                for (int i = 0; i < m.Parameters.Count; i++) ps.Add(m.Parameters[i] + ": " + (i < m.ParameterTypes.Count ? m.ParameterTypes[i] : "?"));
                lines.Add("  method " + (m.IsStatic ? "static " : "") + m.Name + "(" + string.Join(", ", ps) + "): " + m.ReturnType);
                if (m.Body != null) BlockLines(lines, m.Body, 2);
            }
        }
        foreach (var s in program.Structs)
        {
            lines.Add("struct " + s.Name);
            foreach (var f in s.Fields) lines.Add("  field " + f.Name + ": " + f.Type);
        }
        if (program.TopLevel != null)
        {
            lines.Add("toplevel");
            BlockLines(lines, program.TopLevel, 1);
        }
        var parts = new List<string>();
        foreach (var l in lines) parts.Add(l + "\n");
        return string.Join("", parts);
    }

    private static void BlockLines(List<string> lines, IlBlock b, int depth)
    {
        string indent = "";
        for (int i = 0; i < depth; i++) indent = indent + "  ";
        foreach (var s in b.Stats) lines.Add(indent + IlNodes.Dump(s));
    }
}
