using System.Collections.Generic;

namespace TcsSelf;

// One file of a session. Holds the tree as parsed and does not reparse it until the contents change
public sealed class SessionFile
{
    public string Path;
    public string Text;
    // 0 = user source, 1 = --ref declarations, 2 = library shipped with tcs
    public int Role;
    public SourceFile? Source;
    public Node? Unit;
    public Diagnostics Diags = new Diagnostics();
    public bool Dirty = true;

    public SessionFile(string path, string text, int role)
    {
        Path = path;
        Text = text;
        Role = role;
    }
}

// An incremental compile session. Add, replace, and remove files, then compile.
// Parsing is redone only for files whose contents changed; declaration, binding, and output are redone every time on a copy of the parsed tree (binding modifies the tree).
// A compile is split into Steps: parsing each changed file one at a time, compliance check and tree assembly, declaration, binding, output.
// The output depends on the mode: ExportOnly gives IL, CheckOnly checks IL building, and otherwise only binding and compliance diagnostics.
// The host calls Step repeatedly and can do other work between Steps (receiving new edits, tcs2c GC frame boundaries)
public sealed class Session
{
    private readonly List<SessionFile> _files = new List<SessionFile>();
    private string _entry = "";

    // State of an in-progress compile
    private int _phase;
    private Compilation? _c;
    private Declarer? _declarer;
    private CompileResult _result = new CompileResult();
    // Number of diagnostics before declaration
    private int _bindMark;
    // Compliance check diagnostics reported only at the output stage
    private List<Diagnostic> _deferred = new List<Diagnostic>();

    // Number of files parsed in the last compile (measures how well incremental compilation works)
    public int ParsedFiles;
    // How many compiles have finished
    public int Revision;
    // Produce the input for tcs c and tcs vm (type metadata and body IL)
    public bool ExportOnly;
    // Check only (tcs check and the analyzer): does not return IL, but builds it and reports bodies that cannot be built, entry class errors, and internal build errors as diagnostics
    public bool CheckOnly;
    // With ExportOnly, monomorphize generics into closed copies before writing out (C backend and VM)
    public bool Specialize;
    // Keep the bound declarations (type and member symbols, syntax trees) in Declarations. Used by the tool that reads API descriptions (lub-gen)
    public bool KeepDeclarations;
    public Compilation? Declarations;

    private const int PhaseIdle = 0;
    private const int PhaseParse = 1;
    private const int PhaseDeclare = 3;
    private const int PhaseBind = 4;
    private const int PhaseEmit = 5;
    // Monomorphization for the C backend (one step per round), followed by writing out the IL
    private const int PhaseSpecialize = 6;
    private const int PhaseExport = 7;
    private Specializer? _specializer;
    private bool _specializeDone;
    private Compilation? _specialized;
    private string _specializeError = "";
    // Whether the last monomorphization created closed copies
    private bool _lastSpecializeCopied;

    public Session(string entry)
    {
        _entry = entry;
    }

    public CompileResult Result => _result;

    public bool Busy => _phase != PhaseIdle;

    // Add or replace a user source (not reparsed if the contents are the same)
    public void SetSource(string path, string text) => Set(path, text, 0);

    public void SetReference(string path, string text) => Set(path, text, 1);

    public void SetLibrary(string path, string text) => Set(path, text, 2);

    // Add as a separate file even if the same path exists (for hosts that list inputs without paths)
    public void Add(string path, string text, int role)
    {
        _files.Add(new SessionFile(path, text, role));
    }

    // The parsed tree of the file at path (reparsed if the contents changed). null if missing
    public Node? ParsedUnit(string path)
    {
        foreach (var f in _files)
        {
            if (f.Path != path) continue;
            if (f.Dirty) ParseFile(f);
            return f.Unit;
        }
        return null;
    }

    public bool Remove(string path)
    {
        for (int i = 0; i < _files.Count; i++)
        {
            if (_files[i].Path == path)
            {
                _files.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    private void Set(string path, string text, int role)
    {
        foreach (var f in _files)
        {
            if (f.Path != path) continue;
            if (f.Text != text || f.Role != role)
            {
                f.Text = text;
                f.Role = role;
                f.Dirty = true;
            }
            return;
        }
        _files.Add(new SessionFile(path, text, role));
    }

    // Start a compile (if a previous compile is in progress, discard it and start over)
    public void Start()
    {
        _phase = PhaseParse;
        _specializer = null;
        _specializeDone = false;
        _specialized = null;
        _specializeError = "";
        _c = null;
        _declarer = null;
        Declarations = null;
        ParsedFiles = 0;
    }

    // Advance the compile by one step. Returns true when finished and puts the result in Result
    public bool Step()
    {
        switch (_phase)
        {
            case PhaseIdle:
                return true;
            case PhaseParse:
                foreach (var f in _files)
                {
                    if (!f.Dirty) continue;
                    ParseFile(f);
                    return false;
                }
                // Once there are no files left to reparse, assemble in the same step
                Assemble();
                // Do not bind if there are syntax errors (CSxxxx). Syntax outside the subset (TCS1001) was read by rebuilding the tree, so type checking continues too
                _phase = HasSyntaxError(_c!.Diags) ? PhaseEmit : PhaseDeclare;
                _bindMark = _c.Diags.Items.Count;
                return false;
            case PhaseDeclare:
                _declarer = new Declarer(_c!);
                _declarer.DeclareAll();
                _phase = PhaseBind;
                return false;
            case PhaseBind:
                new Binder(_c!, _declarer!).BindAll();
                new SemanticChecks(_c!).CheckAll();
                // When there is syntax or API outside the subset, C# errors in declaration and binding may be side effects of it
                // (calls to skipped local functions, calls with params that are not expanded, etc.). Subset diagnostics are reported first
                if (HasSubsetError(_c!.Diags)) DropCSharpErrorsFrom(_c.Diags, _bindMark);
                if (_c.Diags.ErrorCount == 0)
                {
                    foreach (var d in _deferred)
                    {
                        _c.Diags.Items.Add(d);
                        _c.Diags.ErrorCount++;
                    }
                }
                _phase = PhaseEmit;
                return false;
            case PhaseEmit:
                if (StartSpecialize())
                {
                    _phase = PhaseSpecialize;
                    return false;
                }
                Finish();
                _phase = PhaseIdle;
                Revision++;
                return true;
            case PhaseSpecialize:
                if (!_specializer!.Step()) return false;
                _lastSpecializeCopied = _specializer.MadeCopies;
                _specialized = _specializer.Result;
                _specializeError = _specializer.Result == null ? _specializer.Error : "";
                _specializer = null;
                _specializeDone = true;
                _phase = PhaseExport;
                return false;
            case PhaseExport:
                Finish();
                _phase = PhaseIdle;
                Revision++;
                return true;
        }
        return true;
    }

    // Run Step from Start to the end
    public CompileResult Compile()
    {
        Start();
        while (!Step())
        {
        }
        return _result;
    }

    private void ParseFile(SessionFile f)
    {
        f.Source = new SourceFile(0, f.Path, f.Text, f.Role == 1);
        f.Diags = new Diagnostics();
        f.Unit = Parser.ParseFile(f.Source, f.Diags);
        f.Dirty = false;
        ParsedFiles++;
    }

    // Put copies of the parsed trees into the compilation in the order: user sources, --ref, used libraries
    private void Assemble()
    {
        var c = new Compilation();
        _c = c;
        var userNamespaces = new HashSet<string>();
        var userUsings = new HashSet<string>();
        for (int role = 0; role < 2; role++)
        {
            foreach (var f in _files)
            {
                if (f.Role != role) continue;
                f.Source!.IsLibrary = false;
                f.Source.Id = c.Files.Count;
                c.Files.Add(f.Source);
                c.Units.Add(f.Unit!.CloneTree());
                Merge(c.Diags, f.Diags);
                if (role == 0)
                {
                    CollectNamespaces(f.Unit, "", userNamespaces, userUsings);
                    // A file without usings writes library types with qualified names (`TinySystem.Linq.Enumerable.Count(...)`)
                    CollectQualifiedPrefixes(f.Unit!, userUsings);
                }
            }
        }
        // A library is read only when the user has a using for its namespace, and not when the user declares the same namespace
        foreach (var f in _files)
        {
            if (f.Role != 2) continue;
            // The System.ValueTuple declaration is read only when tuple syntax is present
            if (f.Source!.Path == "TinySystem/ValueTuple.cs")
            {
                bool tuples = false;
                foreach (var u in _files)
                {
                    if (u.Role == 0 && u.Unit != null && ContainsTupleSyntax(u.Unit)) tuples = true;
                }
                if (!tuples) continue;
                f.Source.IsLibrary = true;
                f.Source.Id = c.Files.Count;
                c.Files.Add(f.Source);
                c.Units.Add(f.Unit!.CloneTree());
                Merge(c.Diags, f.Diags);
                continue;
            }
            var libNamespaces = new HashSet<string>();
            CollectNamespaces(f.Unit!, "", libNamespaces, new HashSet<string>());
            bool declaredByUser = false;
            bool used = false;
            foreach (var ns in libNamespaces)
            {
                if (userNamespaces.Contains(ns)) declaredByUser = true;
                if (userUsings.Contains(ns)) used = true;
            }
            if (declaredByUser || !used) continue;
            f.Source!.IsLibrary = true;
            f.Source.Id = c.Files.Count;
            c.Files.Add(f.Source);
            c.Units.Add(f.Unit!.CloneTree());
            Merge(c.Diags, f.Diags);
        }
        // Compliance checks decided by syntax alone (TCS1001). --ref declarations and libraries are excluded
        var compliance = new Compliance(c.Diags);
        for (int i = 0; i < c.Units.Count; i++)
        {
            if (!c.Files[i].IsRef && !c.Files[i].IsLibrary) compliance.CheckUnit(c.Units[i]);
        }
        _deferred = compliance.Deferred;
    }

    private static void DropCSharpErrorsFrom(Diagnostics diags, int mark)
    {
        var kept = new List<Diagnostic>();
        for (int i = 0; i < diags.Items.Count; i++)
        {
            var d = diags.Items[i];
            if (i >= mark && IsCascadeError(d.Code))
            {
                if (d.IsError) diags.ErrorCount--;
                continue;
            }
            kept.Add(d);
        }
        diags.Items = kept;
    }

    // C# errors that can arise as side effects of skipped syntax or unimplemented APIs (name resolution, overload, conversion, reachability, missing implementation)
    private static bool IsCascadeError(string code)
    {
        switch (code)
        {
            case "CS0019":
            case "CS0021":
            case "CS0022":
            case "CS0023":
            case "CS0029":
            case "CS0030":
            case "CS0103":
            case "CS0117":
            case "CS0123":
            case "CS0150":
            case "CS0161":
            case "CS0165":
            case "CS0234":
            case "CS0246":
            case "CS0266":
            case "CS0305":
            case "CS0428":
            case "CS0535":
            case "CS0815":
            case "CS1061":
            case "CS1501":
            case "CS1503":
            case "CS1579":
            case "CS1660":
                return true;
        }
        return false;
    }

    private static bool HasSubsetError(Diagnostics diags)
    {
        foreach (var d in diags.Items)
        {
            if (d.IsError && (d.Code == "TCS1001" || d.Code == "TCS1002")) return true;
        }
        return false;
    }

    private static bool HasSyntaxError(Diagnostics diags)
    {
        foreach (var d in diags.Items)
        {
            if (d.IsError && d.Code.StartsWith("CS")) return true;
        }
        return false;
    }

    // Tuple type / expression syntax (not including the var (a, b) declaration form)
    private static bool ContainsTupleSyntax(Node n)
    {
        if (n.Kind == NodeKind.TupleType || n.Kind == NodeKind.Tuple) return true;
        if (n.Kind == NodeKind.DeconstructAssign && n.Text != "var") return true;
        if (n.Kind == NodeKind.Foreach && n.Count > 0 && n.Text != "(var)") return true;
        if (n.A != null && ContainsTupleSyntax(n.A)) return true;
        if (n.B != null && ContainsTupleSyntax(n.B)) return true;
        if (n.C != null && ContainsTupleSyntax(n.C)) return true;
        if (n.D != null && ContainsTupleSyntax(n.D)) return true;
        if (n.List != null)
        {
            foreach (var x in n.List)
            {
                if (ContainsTupleSyntax(x)) return true;
            }
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2)
            {
                if (ContainsTupleSyntax(x)) return true;
            }
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3)
            {
                if (ContainsTupleSyntax(x)) return true;
            }
        }
        return false;
    }

    private static bool HasCSharpErrors(Diagnostics diags)
    {
        foreach (var d in diags.Items)
        {
            if (d.IsError && d.Code.StartsWith("CS")) return true;
        }
        return false;
    }

    private static void Merge(Diagnostics into, Diagnostics from)
    {
        foreach (var d in from.Items) into.Items.Add(d);
        into.ErrorCount += from.ErrorCount;
    }

    // Start monomorphization in steps (when writing out for the C backend and the binding result can be written out). true if started
    private bool StartSpecialize()
    {
        var c = _c!;
        if (!(ExportOnly && Specialize)) return false;
        if (!(c.Diags.ErrorCount == 0 || !HasCSharpErrors(c.Diags))) return false;
        _specializer = NewSpecializer(c);
        _specializeDone = false;
        return true;
    }

    // Monomorphization rewrites the unbound tree (file order is the same as the compilation)
    private Specializer NewSpecializer(Compilation c)
    {
        var files = new List<SourceFile>();
        var units = new List<Node>();
        var origins = new List<Node>();
        foreach (var sf in c.Files)
        {
            foreach (var f in _files)
            {
                if (f.Source == sf)
                {
                    files.Add(sf);
                    units.Add(f.Unit!.CloneTree());
                    origins.Add(f.Unit!);
                }
            }
        }
        var sp = new Specializer(files, units);
        // The first round checks, on the tree bound by the session (c is a copy of the same f.Unit), whether there are no types or methods to close (if there are none, it does not rebind).
        // If the previous compile created closed copies, checking is wasted effort, so it binds from the start
        if (c.Units.Count == units.Count && !_lastSpecializeCopied) sp.Seed(c, origins);
        return sp;
    }

    private void Finish()
    {
        var c = _c!;
        var result = new CompileResult();
        // The export is produced for input without C# errors even if there are compliance errors (TCS) (diagnostics are returned with it; the tcs VM allows type checks on --ref types)
        bool exportable = ExportOnly && !HasCSharpErrors(c.Diags);
        if (c.Diags.ErrorCount == 0 || exportable)
        {
            if (ExportOnly)
            {
                var exported = c;
                string specializeError = "";
                if (Specialize)
                {
                    // Step advances monomorphization by the number of rounds in earlier steps. Paths other than Compile (calls that skip steps) keep running it here
                    if (!_specializeDone)
                    {
                        var sp = NewSpecializer(c);
                        _specialized = sp.Run();
                        _specializeError = _specialized == null ? sp.Error : "";
                    }
                    if (_specialized == null) specializeError = _specializeError;
                    else exported = _specialized;
                    _specialized = null;
                    _specializeDone = false;
                }
                if (specializeError != "")
                {
                    result.Diagnostics.Add(specializeError);
                    c.Diags.ErrorCount++;
                }
                else
                {
                    result.Export = new IlExporter(exported).Export();
                    // Binding errors in the monomorphized tree (for investigation; bindings missed in copies)
                    if (exported != c)
                    {
                        foreach (var d in exported.Diags.Items)
                        {
                            if (d.IsError) result.Export.InternalErrors.Add("specialized: " + d.ToString());
                        }
                    }
                }
            }
            else if (CheckOnly)
            {
                if (CheckIl(c, result)) c.Diags.ErrorCount++;
            }
        }
        foreach (var diag in c.Diags.Ordered()) result.Diagnostics.Add(diag.ToString());
        result.ErrorCount = c.Diags.ErrorCount;
        result.Ok = c.Diags.ErrorCount == 0;
        _result = result;
        if (KeepDeclarations) Declarations = c;
        _c = null;
        _declarer = null;
    }

    // Runs the same building as IL write-out and adds its errors to the result diagnostics. true if there are errors.
    // Bodies and expressions that cannot be built (TCS1001) come in file and position order, followed by entry class errors and internal build errors
    private bool CheckIl(Compilation c, CompileResult result)
    {
        var exporter = new IlExporter(c);
        var export = exporter.Export();
        var nodes = new List<Node>();
        foreach (var n in exporter.UnsupportedBodies)
        {
            if (!nodes.Contains(n)) nodes.Add(n);
        }
        // Insertion sort (equal positions stay in recorded order)
        for (int i = 1; i < nodes.Count; i++)
        {
            var x = nodes[i];
            int j = i - 1;
            while (j >= 0 && (nodes[j].File.Id > x.File.Id || nodes[j].File.Id == x.File.Id && nodes[j].Pos > x.Pos))
            {
                nodes[j + 1] = nodes[j];
                j--;
            }
            nodes[j + 1] = x;
        }
        var errors = new List<string>();
        foreach (var n in nodes)
        {
            var d = new Diagnostic("TCS1001", "unsupported syntax: " + n.Kind, n.File, n.Pos, true).ToString();
            if (!errors.Contains(d)) errors.Add(d);
        }
        if (_entry != "") EntryClass.Resolve(c, _entry, errors);
        foreach (var ie in export.InternalErrors) errors.Add("internal: " + ie);
        foreach (var e in errors) result.Diagnostics.Add(e);
        return errors.Count > 0;
    }

    // Declared namespaces and namespaces with usings
    // A qualified name (`A.B.C`) and its prefixes (`A`, `A.B`). The same for using alias targets
    private static void CollectQualifiedPrefixes(Node n, HashSet<string> into)
    {
        if (n.Kind == NodeKind.Member || n.Kind == NodeKind.TypeName && n.A != null)
        {
            var dotted = DottedName(n);
            if (dotted != null) AddPrefixes(dotted, into);
        }
        else if (n.Kind == NodeKind.Using) AddPrefixes(n.Text, into);
        if (n.A != null) CollectQualifiedPrefixes(n.A, into);
        if (n.B != null) CollectQualifiedPrefixes(n.B, into);
        if (n.C != null) CollectQualifiedPrefixes(n.C, into);
        if (n.D != null) CollectQualifiedPrefixes(n.D, into);
        if (n.List != null)
        {
            foreach (var x in n.List) CollectQualifiedPrefixes(x, into);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) CollectQualifiedPrefixes(x, into);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) CollectQualifiedPrefixes(x, into);
        }
    }

    private static string? DottedName(Node n)
    {
        if (n.Kind == NodeKind.Name || n.Kind == NodeKind.TypeName && n.A == null) return n.Text;
        if ((n.Kind == NodeKind.Member || n.Kind == NodeKind.TypeName) && n.A != null)
        {
            var left = DottedName(n.A);
            if (left == null) return null;
            return left + "." + n.Text;
        }
        return null;
    }

    private static void AddPrefixes(string dotted, HashSet<string> into)
    {
        var parts = dotted.Split('.');
        string acc = "";
        foreach (var p in parts)
        {
            acc = acc == "" ? p : acc + "." + p;
            into.Add(acc);
        }
    }

    public static void CollectNamespaces(Node container, string ns, HashSet<string> declared, HashSet<string> usings)
    {
        if (container.List == null) return;
        foreach (var n in container.List)
        {
            if (n.Kind == NodeKind.Using) usings.Add(n.Text);
            else if (n.Kind == NodeKind.Namespace)
            {
                string inner = ns == "" ? n.Text : ns + "." + n.Text;
                declared.Add(inner);
                CollectNamespaces(n, inner, declared, usings);
            }
        }
    }
}
