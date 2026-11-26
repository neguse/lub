using System.Collections.Generic;
using Tcs;
using TinySystem.Text;

namespace TcsC;

// Backend that turns IL into C (a port of the C# version in the old tcs2c/ to TinyC#). The generated C is byte-identical to the C# version.
// The subset has no partial, so one class lives in one file (sections follow the original files)
public sealed class CEmitter
{
    // ---- Core ----

    private ExportResult _program;
    private ContractFacts _facts;
    private Dictionary<string, ExClass> _classes;
    private bool _digestF32;
    private List<string> _output = new List<string>();
    private List<Dictionary<string, Variable>> _scopes = new List<Dictionary<string, Variable>>();
    // Declaration order of names per scope (Dictionary enumeration order is unspecified, so traversals that affect the output use this)
    private List<List<string>> _scopeOrder = new List<List<string>>();
    private List<string?> _continueTargets = new List<string?>();
    // Break targets: null for loops (`break;`), a label for IlBreakScope (`goto`)
    private List<string?> _breakTargets = new List<string?>();
    // Break labels jumped to by goto (unused labels are not emitted: -Wunused-label)
    private HashSet<string> _usedBreakLabels = new HashSet<string>();
    private ExClass _currentClass = new ExClass();
    private ExMethod _currentMethod = new ExMethod();
    private MethodFact _currentMethodFact = new MethodFact("", "", true, CType.Void, new List<ParameterFact>(), new ExMethod());
    private int _indent;
    private int _serial;

    private List<string> _pendingClosures = new List<string>();
    private List<string> _closureDecls = new List<string>();
    private const string ClosureDeclMarker = "/*__TCS2C_CLOSURE_DECLS__*/";
    private HashSet<string> _capturedNames = new HashSet<string>();
    private int _closureSerial;

    // Result receiver and exit target of each IIFE (innermost nesting last)
    private List<IifeFrame> _iifes = new List<IifeFrame>();
    // Value returned by return in a struct constructor body (StructMembers)
    private string? _ctorReturnValue;

    // Runtime C to embed in the generated C (path relative to runtime/ → contents). Passed by the host
    private Dictionary<string, string> _runtime;

    public CEmitter(ExportResult program, bool digestF32, Dictionary<string, string> runtimeFiles)
    {
        // Inject ctors and top-level statements as synthesized methods so they go through the existing facts / prototype / EmitMethod mechanism as is
        _runtime = runtimeFiles;
        _program = Normalize(program);
        _digestF32 = digestF32;
        _facts = new ContractFacts(_program);
        _classes = new Dictionary<string, ExClass>();
        foreach (var name in _facts.Classes.Keys) _classes[name] = _facts.Classes[name];
        BuildHierarchy();
    }

    public const string CtorMethodName = "__ctor";

    // ---- Inheritance: DFS-range type IDs and chain resolution ----
    private Dictionary<string, TypeRange> _typeRange = new Dictionary<string, TypeRange>();
    private Dictionary<string, List<string>> _children = new Dictionary<string, List<string>>();
    private int _nextTypeId;

    private void BuildHierarchy()
    {
        var roots = new List<string>();
        foreach (var cls in _program.Classes)
        {
            if (cls.BaseName != null)
            {
                string b = cls.BaseName;
                if (!_classes.ContainsKey(b))
                {
                    CFail.Report("unknown base class: " + cls.Name + " : " + b);
                    return;
                }
                List<string>? list;
                if (!_children.TryGetValue(b, out list))
                {
                    list = new List<string>();
                    _children[b] = list;
                }
                list.Add(cls.Name);
            }
            else
            {
                roots.Add(cls.Name);
            }
        }
        _nextTypeId = 1;
        foreach (var root in roots) AssignTypeRange(root);
        if (_typeRange.Count != _program.Classes.Count) CFail.Report("inheritance cycle detected");
    }

    private void AssignTypeRange(string name)
    {
        if (_typeRange.ContainsKey(name))
        {
            CFail.Report("inheritance cycle at " + name);
            return;
        }
        int first = _nextTypeId++;
        _typeRange[name] = new TypeRange(first, first);
        List<string>? cs;
        if (_children.TryGetValue(name, out cs))
        {
            foreach (var child in cs) AssignTypeRange(child);
        }
        _typeRange[name] = new TypeRange(first, _nextTypeId - 1);
    }

    private List<ExClass> ChainRootFirst(string cls)
    {
        var chain = new List<ExClass>();
        string? cur = cls;
        while (cur != null)
        {
            chain.Insert(0, _classes[cur]);
            cur = _classes[cur].BaseName;
        }
        return chain;
    }

    // An ancestor is a class on the base chain, or an interface implemented somewhere on the chain
    public bool IsAncestorOrSame(string ancestor, string derived)
    {
        string? cur = derived;
        while (cur != null)
        {
            if (cur == ancestor) return true;
            foreach (var i in _classes[cur].Interfaces)
            {
                if (_facts.IlTypeName(i) == ancestor) return true;
            }
            cur = _classes[cur].BaseName;
        }
        return false;
    }

    private bool IsInterface(string cls) => _classes[cls].IsInterface;

    // Nearest class on the chain (including itself) that declares name
    private string? FindDeclaringClass(string cls, string method)
    {
        string? cur = cls;
        while (cur != null)
        {
            foreach (var m in _classes[cur].Methods)
            {
                if (m.Name == method) return cur;
            }
            cur = _classes[cur].BaseName;
        }
        return null;
    }

    private FieldFact FieldInChain(string cls, string name)
    {
        string? cur = cls;
        while (cur != null)
        {
            foreach (var f in _classes[cur].Fields)
            {
                if (f.Name == name) return _facts.Field(cur, name);
            }
            cur = _classes[cur].BaseName;
        }
        CFail.Report("unknown field: " + cls + "." + name);
        return _facts.Field(cls, name);
    }

    // Whether a strict descendant redeclares the method declared in declaring. Interface methods always dispatch to the implementation
    private bool IsPolymorphic(string declaring, string method)
    {
        if (IsInterface(declaring)) return true;
        foreach (var c in _program.Classes)
        {
            if (c.Name == declaring || !IsAncestorOrSame(declaring, c.Name)) continue;
            foreach (var m in c.Methods)
            {
                if (m.Name == method) return true;
            }
        }
        return false;
    }

    // TinySystem.Text.Utf8Bytes becomes the same kind of C value as a user struct (GC layout, T?, List elements, and default values work as is)
    private static ExStruct Utf8BytesStruct()
    {
        var st = new ExStruct();
        st.Name = CType.Utf8BytesStruct;
        st.Fields.Add(new ExField("Text", "string", false, null, null));
        st.Fields.Add(new ExField("Start", "int", false, null, null));
        st.Fields.Add(new ExField("Length", "int", false, null, null));
        st.LayoutHash = "0";
        return st;
    }

    private static ExportResult Normalize(ExportResult program)
    {
        var result = new ExportResult();
        foreach (var c in program.Classes) result.Classes.Add(c);
        if (program.TopLevel != null)
        {
            var main = new ExMethod();
            main.Name = "Main";
            main.IsStatic = true;
            main.Body = program.TopLevel;
            main.ReturnType = "void";
            var top = new ExClass();
            top.Name = "TopLevel";
            top.LayoutHash = "0";
            top.Methods.Add(main);
            result.Classes.Add(top);
        }
        foreach (var d in program.Diagnostics) result.Diagnostics.Add(d);
        // Interface methods from --ref (called by the VM; X-4 of per-module mixing) do not become C host functions
        foreach (var m in program.ForeignMethods)
        {
            bool keep = m.Receiver == null;
            foreach (var c in result.Classes)
            {
                if (c.Name == m.Receiver) keep = true;
            }
            if (keep) result.ForeignMethods.Add(m);
        }
        foreach (var v in program.ForeignValues) result.ForeignValues.Add(v);
        result.Structs.Add(Utf8BytesStruct());
        foreach (var s in program.Structs) result.Structs.Add(s);
        foreach (var e in program.Enums) result.Enums.Add(e);
        foreach (var e in program.EnumTypes) result.EnumTypes.Add(e);
        return result;
    }

    // Split generation into stages (Begin, StepMethods per class, Finish). The host can place GC frame boundaries between stages,
    // so even a large program only needs the temporary allocations of one stage. Emit runs all stages in sequence
    private bool _emitLib;
    private EntryPoint? _emitEntry;
    private int _emitClass;

    public string Emit(string? requestedEntry, bool lib)
    {
        if (!Begin(requestedEntry, lib)) return "";
        while (!StepMethods()) { }
        return Finish();
    }

    // Preamble (runtime, type declarations, prototypes, allocators). Returns false on error
    public bool Begin(string? requestedEntry, bool lib)
    {
        ValidateProgram();
        if (CFail.Failed) return false;
        var entry = lib ? null : FindEntry(requestedEntry);
        if (CFail.Failed) return false;
        _emitLib = lib;
        _emitEntry = entry;
        _emitClass = 0;

        _output.Add(RuntimePrelude());
        _output.Add(RuntimeMath());
        // Number parsing in runtime/fmt uses the whitespace of runtime/utf8, so utf8 goes first
        _output.Add(RuntimeUtf8());
        _output.Add(RuntimeFmt());
        _output.Add(RuntimeCore());
        _output.Add(RuntimeGc());
        _output.Add(RuntimeCollections());
        _output.Add(RuntimeStrings());
        _output.Add(RuntimeLib());
        if (VmHostSurface != null)
        {
            // Per-module mixing (--vm-host): the VM API and the VM (used by dispatchers and glue)
            Line("#include \"tcsvm.h\"");
            Line("static TvmVm *tcs_vmh_vm;");
            // A VM string has the same byte layout as TcsString (shared heap, so it is not copied; docs/tcs/object-model.md §10.6)
            Line("static TcsString *");
            Line("tcs_vmh_str_in(void *s)");
            Line("{");
            _indent++;
            Line("return (TcsString *)s;");
            _indent--;
            Line("}");
        }
        Line(LiteralsMarker);
        EmitClassDeclarations();
        EmitInterfaceChecks();
        EmitStaticFields();
        EmitStaticRoots();
        EmitMethodPrototypes();
        EmitAllocators();
        return !CFail.Failed;
    }

    // Write the methods of the next class. Returns true when all classes are written or on error
    public bool StepMethods()
    {
        if (CFail.Failed || _emitClass >= _program.Classes.Count) return true;
        var cls = _program.Classes[_emitClass];
        _emitClass++;
        foreach (var method in cls.Methods)
        {
            string before = CFail.Error;
            EmitMethod(cls, method);
            if (CFail.Failed && before == "") CFail.Error = CFail.Error + " (in " + cls.Name + "." + method.Name + ")";
            if (CFail.Failed) return true;
        }
        return _emitClass >= _program.Classes.Count;
    }

    // Write the trailing part (struct members, dispatchers, record equality, static initialization, entry) and return the whole C. Returns "" on error
    public string Finish()
    {
        if (CFail.Failed) return "";
        bool lib = _emitLib;
        var entry = _emitEntry;
        EmitStructMembers();
        EmitDispatchers();
        EmitForeignDispatchers();
        EmitRecordEquality();
        EmitStaticInitializer();
        if (lib)
        {
            EmitLibEntryPoints();
            if (VmHostSurface != null) EmitVmHost(VmHostSurface);
        }
        else
            EmitEntryPoint(entry);
        if (CFail.Failed) return "";
        return string.Join("", _output)
            .Replace(ClosureDeclMarker, string.Join("\n", _closureDecls))
            .Replace(LiteralsMarker, string.Join("\n", _literalDecls));
    }

    private void ValidateProgram()
    {
        if (_program.Diagnostics.Count > 0)
        {
            CFail.Report("cannot emit a program with TinyC# diagnostics");
            return;
        }
        ValidateStructs();
        foreach (var cls in _program.Classes)
        {
            Names.Id(cls.Name);
            foreach (var field in cls.Fields)
            {
                Names.Id(field.Name);
                var fact = _facts.Field(cls.Name, field.Name);
                EnsureSupportedStorageType(fact.Type, "field " + cls.Name + "." + field.Name, false);
            }
            var methodNames = new HashSet<string>();
            foreach (var method in cls.Methods)
            {
                Names.Id(method.Name);
                if (methodNames.Contains(method.Name))
                {
                    CFail.Report("method overloads are not supported: " + cls.Name + "." + method.Name);
                    return;
                }
                methodNames.Add(method.Name);
                if (method.Body == null && !method.IsAbstract && !cls.IsInterface)
                {
                    CFail.Report("method has no IL body: " + cls.Name + "." + method.Name);
                    return;
                }
                var fact = _facts.Method(cls.Name, method.Name);
                EnsureSupportedStorageType(fact.ReturnType, "return type of " + cls.Name + "." + method.Name, true);
                foreach (var parameter in fact.Parameters)
                    EnsureSupportedStorageType(parameter.Type, "parameter " + cls.Name + "." + method.Name + "." + parameter.Name, false);
                foreach (var parameter in fact.OutParameters)
                    EnsureSupportedStorageType(parameter.Type, "parameter " + cls.Name + "." + method.Name + "." + parameter.Name, false);
            }
        }
    }

    // Storage types (field / parameter / local / element): scalars, string, class references, data structs, Dictionary, closures, and arrays / Lists of those
    private static void EnsureSupportedStorageType(CType type, string where)
    {
        EnsureSupportedStorageType(type, where, false);
    }

    private static void EnsureSupportedStorageType(CType type, string where, bool allowVoid)
    {
        if (type.Kind == CTypeKind.Void && allowVoid) return;
        if (IsStorageType(type)) return;
        CFail.Report("unsupported " + where + ": " + type.Display());
    }

    private static bool IsStorageType(CType type)
    {
        switch (type.Kind)
        {
            case CTypeKind.I32:
            case CTypeKind.F32:
            case CTypeKind.Bool:
            case CTypeKind.String:
            case CTypeKind.Ref:
            case CTypeKind.StructVal:
            case CTypeKind.Closure:
            case CTypeKind.Nullable:
            case CTypeKind.Random:
            case CTypeKind.Object:
                return true;
            case CTypeKind.Dict:
                return type.KeyType != null && type.Element != null && IsStorageType(type.Element);
            case CTypeKind.Array:
            case CTypeKind.List:
            case CTypeKind.Queue:
            case CTypeKind.HashSet:
                return type.Element != null && IsStorageType(type.Element);
        }
        return false;
    }

    private EntryPoint? FindEntry(string? requested)
    {
        var candidates = new List<EntryPoint>();
        foreach (var c in _program.Classes)
        {
            foreach (var m in c.Methods)
            {
                // IL names are already mapped by the user member rule (C# identifiers as is)
                if (m.Name != "Main" || !m.IsStatic || m.Parameters.Count != 0) continue;
                if (!CType.Same(_facts.Method(c.Name, m.Name).ReturnType, CType.Void)) continue;
                if (requested != null && c.Name != requested) continue;
                candidates.Add(new EntryPoint(c, m));
            }
        }
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0 && requested == null && !_digestF32) return null;
        if (candidates.Count == 0)
        {
            CFail.Report(requested == null ? "--digest-f32 requires a static void Main() entry point" : "no static void Main() entry point in " + requested);
            return null;
        }
        CFail.Report("multiple Main entry points; pass --entry CLASS");
        return null;
    }

    private void EmitClassDeclarations()
    {
        // Emit forward typedefs for classes first (so struct fields can hold class references)
        foreach (var cls in _program.Classes)
            Line("typedef struct " + Names.Class(cls.Name) + " " + Names.Class(cls.Name) + ";");
        Line();
        EmitStructTypedefs();
        Line("typedef struct TcsObjectHeader {");
        _indent++;
        Line("uint32_t type_id;");
        _indent--;
        Line("} TcsObjectHeader;");
        Line();
        Line("enum {");
        _indent++;
        foreach (var cls in _program.Classes)
        {
            var range = _typeRange[cls.Name];
            Line(Names.TypeId(cls.Name) + " = " + range.First + ",");
            Line(Names.TypeIdMax(cls.Name) + " = " + range.Last + ",");
        }
        _indent--;
        Line("};");
        Line();
        foreach (var cls in _program.Classes)
        {
            Line("struct " + Names.Class(cls.Name) + " {");
            _indent++;
            Line("uint32_t type_id;");
            var chain = ChainRootFirst(cls.Name);
            // External data class shared with the host: a host-side handle (not seen by the GC)
            bool external = false;
            foreach (var c in chain)
            {
                if (c.IsExternal) external = true;
            }
            if (external) Line("uint64_t host_value;");
            // Flatten the inheritance chain from the root (a matching leading layout makes upcasts possible)
            foreach (var link in chain)
            {
                foreach (var field in link.Fields)
                {
                    if (field.IsStatic) continue;
                    var fact = _facts.Field(link.Name, field.Name);
                    Line(fact.Type.CName + " " + Names.Field(field.Name) + ";");
                }
            }
            _indent--;
            Line("};");
            Line();
        }
        EmitLayouts();
        EmitTypeTable();
    }

    private void EmitStaticFields()
    {
        foreach (var cls in StaticOwners)
        {
            foreach (var field in cls.Fields)
            {
                if (!field.IsStatic) continue;
                var fact = _facts.Field(cls.Name, field.Name);
                Line("static " + fact.Type.CName + " " + Names.StaticField(cls.Name, field.Name) + ";");
            }
        }
        Line();
        // Statics of lazily initialized classes are accessed through an accessor (lvalue)
        foreach (var cls in StaticOwners)
        {
            if (!HasLazyStatics(cls)) continue;
            Line("static void " + Names.StaticInit(cls.Name) + "(void);");
            foreach (var field in cls.Fields)
            {
                if (!field.IsStatic) continue;
                var fact = _facts.Field(cls.Name, field.Name);
                Line("static inline " + fact.Type.CName + " *" + Names.StaticPlace(cls.Name, field.Name) + "(void) "
                    + "{ " + Names.StaticInit(cls.Name) + "(); return &" + Names.StaticField(cls.Name, field.Name) + "; }");
            }
        }
        Line();
    }

    // Whether this is a constant expression (a combination of literals). A class with a non-constant static initializer is initialized on first access, as in C# (handles forward references / cycles)
    private static bool IsConstantExpr(IlExpr expr)
    {
        switch (expr)
        {
            case IlLit: return true;
            case IlParen paren: return IsConstantExpr(paren.E);
            case IlUn unary: return IsConstantExpr(unary.E);
            case IlBin binary: return IsConstantExpr(binary.L) && IsConstantExpr(binary.R);
        }
        return false;
    }

    private bool HasLazyStatics(ExClass cls)
    {
        foreach (var f in cls.Fields)
        {
            if (f.IsStatic && f.Init != null && !IsConstantExpr(f.Init)) return true;
        }
        return false;
    }

    // Place expression for a static field (for a lazy class, an accessor that guarantees initialization)
    private string StaticFieldPlace(ExClass cls, string field)
    {
        if (HasLazyStatics(cls)) return "(*" + Names.StaticPlace(cls.Name, field) + "())";
        return Names.StaticField(cls.Name, field);
    }

    private void EmitMethodPrototypes()
    {
        foreach (var cls in _program.Classes)
        {
            if (cls.IsInterface) continue;
            foreach (var ctor in CtorsOrDefault(cls))
            {
                var protoParams = CtorParamFacts(ctor);
                var types = new List<string>();
                foreach (var p in protoParams) types.Add(p.Type.CName);
                string signature = protoParams.Count == 0 ? "void" : string.Join(", ", types);
                var symbol = CtorSymbol(cls, ctor);
                Line("static " + Names.Class(cls.Name) + " *" + Names.New(symbol) + "(" + signature + ");");
                var initTypes = new List<string>();
                initTypes.Add(Names.Class(cls.Name) + " *");
                foreach (var p in protoParams) initTypes.Add(p.Type.CName);
                Line("static void " + Names.Init(symbol) + "(" + string.Join(", ", initTypes) + ");");
            }
        }
        foreach (var cls in _program.Classes)
        {
            foreach (var method in cls.Methods)
            {
                var fact = _facts.Method(cls.Name, method.Name);
                Line("static " + fact.ReturnType.CName + " " + Names.Method(cls.Name, method.Name) + "(" + ParameterList(fact) + ");");
            }
        }
        Line();
        // Forward declarations of dispatchers
        foreach (var cls in _program.Classes)
        {
            foreach (var method in cls.Methods)
            {
                if (method.IsStatic) continue;
                var baseName = _classes[cls.Name].BaseName;
                var declaring = baseName != null ? FindDeclaringClass(baseName, method.Name) : null;
                if (declaring != null) continue;
                if (!IsPolymorphic(cls.Name, method.Name)) continue;
                var fact = _facts.Method(cls.Name, method.Name);
                var parameters = new List<string>();
                parameters.Add(Names.Class(cls.Name) + " *");
                foreach (var p in fact.Parameters) parameters.Add(p.Type.CName);
                foreach (var p in fact.OutParameters) parameters.Add(p.Type.CName + " *");
                Line("static " + fact.ReturnType.CName + " " + Names.Dispatch(cls.Name, method.Name) + "(" + string.Join(", ", parameters) + ");");
            }
        }
        EmitStructMemberPrototypes();
        EmitRecordEqualityPrototypes();
        EmitForeignPrototypes();
        Line(ClosureDeclMarker);
    }

    private string ParameterList(MethodFact method)
    {
        var parameters = new List<string>();
        if (!method.IsStatic)
        {
            if (_facts.Structs.ContainsKey(method.ClassName))
            {
                // struct method: a pointer to the storage location and its owning heap object (NULL if on the stack; the barrier target for reference stores through self)
                parameters.Add(CType.Struct(method.ClassName).CName + " *v_self");
                parameters.Add("void *v_owner");
            }
            else
            {
                parameters.Add(CType.Ref(method.ClassName).CName + " v_self");
            }
        }
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            var p = method.Parameters[i];
            parameters.Add(p.Type.CName + " v_" + Names.Id(p.Name) + "_" + i);
        }
        for (int i = 0; i < method.OutParameters.Count; i++) parameters.Add(OutPointerParameter(method.OutParameters[i], i));
        return parameters.Count == 0 ? "void" : string.Join(", ", parameters);
    }

    private void EmitMethod(ExClass cls, ExMethod method)
    {
        _currentClass = cls;
        var fact = _facts.Method(cls.Name, method.Name);
        EmitMethodCore(method, fact, fact.IsStatic ? null : new Variable("v_self", CType.Ref(cls.Name)));
    }

    // Method body shared by class / struct. self is a reference for a class and a pointer to the storage location for a struct (Boxed = appears as a place via `(*v_self)`)
    private void EmitMethodCore(ExMethod method, MethodFact fact, Variable? selfVar)
    {
        _currentMethod = method;
        _currentMethodFact = fact;
        _scopes.Clear();
        _scopeOrder.Clear();
        _continueTargets.Clear();
        _breakTargets.Clear();
        if (method.Body != null) CollectCapturedNames(method.Body);
        PushScope();
        if (selfVar != null) AddVariable("self", selfVar);
        for (int i = 0; i < _currentMethodFact.Parameters.Count; i++)
        {
            var parameter = _currentMethodFact.Parameters[i];
            AddVariable(parameter.Name, new Variable("v_" + Names.Id(parameter.Name) + "_" + i, parameter.Type));
        }

        Line("static " + _currentMethodFact.ReturnType.CName);
        Line(Names.Method(fact.ClassName, method.Name) + "(" + ParameterList(_currentMethodFact) + ")");
        Line("{");
        _indent++;
        BoxCapturedParameters();
        // An out parameter is a plain local in the body, and its value is written to the caller at each return. The caller's receiver is a stack temporary, so no write barrier is needed
        foreach (var parameter in _currentMethodFact.OutParameters)
            DeclareLocal(parameter.Name, parameter.Type, ZeroInit(parameter.Type));
        // abstract / interface method: the dispatcher jumps to the implementation, so this body is never reached
        if (method.Body == null) Line("tcs_fault(\"abstract-method\");");
        else
        {
            EmitStats(method.Body.Stats);
            if (CType.Same(_currentMethodFact.ReturnType, CType.Void)) EmitOutWrites();
        }
        _indent--;
        Line("}");
        Line();
        PopScope();
        FlushPendingClosures();
    }

    private void EmitStats(List<IlStat> stats)
    {
        foreach (var stat in stats) EmitStat(stat);
    }

    private void EmitStat(IlStat stat)
    {
        switch (stat)
        {
            case IlLocal local: EmitLocal(local); break;
            case IlAssign assign: EmitAssign(assign); break;
            case IlCallStat call when call.Call is IlIife && TryEmitClearIife((IlIife)call.Call): break;
            case IlCallStat call: Line(RenderExpr(call.Call) + ";"); break;
            case IlIf conditional: EmitIf(conditional); break;
            case IlWhile loop: EmitWhile(loop); break;
            case IlRepeat repeat: EmitRepeat(repeat); break;
            case IlNumericFor loop: EmitNumericFor(loop); break;
            case IlForeachList loop: EmitForeachList(loop); break;
            case IlForeachDict loop: EmitForeachDict(loop); break;
            case IlForeachRunes loop: EmitForeachRunes(loop); break;
            case IlBreak:
                if (_breakTargets.Count > 0 && _breakTargets[_breakTargets.Count - 1] != null)
                {
                    string breakLabel = _breakTargets[_breakTargets.Count - 1]!;
                    _usedBreakLabels.Add(breakLabel);
                    Line("goto " + breakLabel + ";");
                }
                else Line("break;");
                break;
            case IlBreakScope scope: EmitBreakScope(scope); break;
            case IlContinue: EmitContinue(); break;
            case IlMultiAssign multi: EmitMultiAssign(multi); break;
            case IlReturn ret: EmitReturn(ret); break;
            case IlDo block: EmitDo(block); break;
            default:
                Unsupported(stat);
                break;
        }
    }

    private void EmitLocal(IlLocal local)
    {
        // Pre-declaration of a discard (`out _`): the caller provides the receiver as a temporary
        if (local.Name == "_" && local.Init == null && local.Type == null) return;
        if (local.Init == null)
        {
            // The type comes from the contract (IlLocal.Type). C zero initialization = default value
            if (local.Type == null)
            {
                CFail.Report("local has no initializer/type: " + _currentMethodFact.ClassName + "." + _currentMethod.Name + "." + local.Name);
                return;
            }
            var declared = _facts.MapType(local.Type);
            var zero = ZeroInit(declared);
            DeclareLocal(local.Name, declared, zero);
            return;
        }
        // If the declared type is a closure, use the contract type (IlClosure / method groups cannot be typed on their own)
        CType? declaredClosure = null;
        if (local.Type != null)
        {
            var mapped = _facts.TryMapType(local.Type);
            if (mapped != null && mapped.Kind == CTypeKind.Closure) declaredClosure = mapped;
        }
        var type = declaredClosure != null ? declaredClosure : TypeOf(local.Init);
        // If the declared type (contract) accepts the initializer's type, use it as the local's type (`float f = 2` / `string s = null` / `Shape s = new Rect()`)
        if (local.Type != null)
        {
            var declaredType = _facts.TryMapType(local.Type);
            if (declaredType != null && !CType.Same(declaredType, type)
                && (declaredType.CanAssignFrom(type) || IsPatternBindable(declaredType, type)))
                type = declaredType;
        }
        if (type.Kind == CTypeKind.Void || type.Kind == CTypeKind.Null)
        {
            CFail.Report("cannot infer storage type of local " + local.Name + ": " + type.Display());
            return;
        }
        DeclareLocal(local.Name, type, RenderCoerced(local.Init, type));
    }

    // Declare a local (including foreach iteration variables). A name captured by a closure gets a new heap cell per declaration (a separate variable per iteration)
    private void DeclareLocal(string name, CType type, string value)
    {
        if (_capturedNames.Contains(name))
        {
            var cell = new Variable("c_" + Names.Id(name) + "_" + _serial++, type);
            cell.Boxed = true;
            AddVariable(name, cell);
            Line(type.CName + " *" + cell.CName + " = tcs_new_cell(sizeof(*" + cell.CName + "), " + LayoutRef(type) + ");");
            Line("*" + cell.CName + " = " + value + ";");
            return;
        }
        var variable = new Variable("v_" + Names.Id(name) + "_" + _serial++, type);
        AddVariable(name, variable);
        Line(type.CName + " " + variable.CName + " = " + value + ";");
    }

    private void EmitAssign(IlAssign assign)
    {
        var targetType = TypeOfPlace(assign.Target);
        CheckAssignable(targetType, assign.Value, "assignment");
        var value = RenderCoerced(assign.Value, targetType);

        switch (assign.Target)
        {
            case IlVar variable:
                {
                    var target = Resolve(variable.Name);
                    Line(target.Boxed ? "(*" + target.CName + ") = " + value + ";" : target.CName + " = " + value + ";");
                    // A capture cell is a heap object: a reference store marks the owner dirty
                    if (target.Boxed && NeedsBarrier(targetType)) Line("tcs_wb(" + target.CName + ");");
                    return;
                }
            case IlField field:
                {
                    if (TryStaticField(field, out string staticName, out CType? staticType))
                    {
                        Line(staticName + " = " + value + ";");
                        return;
                    }
                    // Field write to a struct place (via array element, local, or class field). A C lvalue chain writes it directly
                    if (TypeOf(field.Recv).Kind == CTypeKind.StructVal)
                    {
                        var setup = new List<string>();
                        var place = RenderOwnedStructPlace(field.Recv, setup, out string? owner);
                        var member = place + "." + Names.Field(field.Name);
                        if (setup.Count > 0)
                        {
                            // Evaluate the receiver and index before the value (C# order; C's `=` does not define the evaluation order of its sides)
                            Line(TrimEndText(string.Join("", setup)));
                            var slot = Temp("slot");
                            Line(targetType.CName + " *" + slot + " = &" + member + ";");
                            member = "(*" + slot + ")";
                        }
                        Line(member + " = " + value + ";");
                        if (NeedsBarrier(targetType) && owner != null) Line("tcs_wb(" + owner + ");");
                        return;
                    }
                    var receiverType = TypeOf(field.Recv);
                    if (receiverType.Kind != CTypeKind.Ref)
                    {
                        CFail.Report("field receiver is not a class reference");
                        return;
                    }
                    // Evaluate the receiver and the value before checking the receiver for null (C# order)
                    var temp = Temp("object");
                    var fieldValue = Temp("stored");
                    Line(receiverType.CName + " " + temp + " = " + RenderExpr(field.Recv) + ";");
                    Line(targetType.CName + " " + fieldValue + " = " + value + ";");
                    Line("((" + receiverType.CName + ")tcs_nonnull(" + temp + "))->" + Names.Field(field.Name) + " = " + fieldValue + ";");
                    if (NeedsBarrier(targetType)) Line("tcs_wb(" + temp + ");");
                    return;
                }
            case IlIndex index:
                {
                    if (!index.PlusOne && TypeOf(index.Recv).Kind == CTypeKind.Dict)
                    {
                        var slotType = RequireDict(index.Recv, out CType dictType);
                        CheckAssignable(slotType, assign.Value, "dict store");
                        // Evaluate the receiver, the key and the value before taking the storage location (C# order; C's `=` does not define the evaluation order of its sides).
                        // tcs_dict_put checks for a null receiver
                        var dictTemp = Temp("dict");
                        var key = Temp("key");
                        var dictValue = Temp("stored");
                        Line("TcsDict *" + dictTemp + " = " + RenderExpr(index.Recv) + ";");
                        Line(dictType.KeyType!.CName + " " + key + " = " + RenderExpr(index.Idx) + ";");
                        Line(slotType.CName + " " + dictValue + " = " + value + ";");
                        Line("*(" + slotType.CName + " *)tcs_dict_put(" + dictTemp + ", " + DictKeyArgs(dictType.KeyType!, key) + ") = " + dictValue + ";");
                        return;
                    }
                    var sequenceType = RequireSequence(index);
                    var sequence = Temp("sequence");
                    var idx = Temp("index");
                    var place = Temp("place");
                    var stored = Temp("stored");
                    // Evaluate the receiver, index, and value before checking the range (C# order)
                    Line(sequenceType.CName + " " + sequence + " = " + RenderExpr(index.Recv) + ";");
                    Line("int32_t " + idx + " = " + RenderExpr(index.Idx) + ";");
                    Line(sequenceType.ElementCName + " " + stored + " = " + value + ";");
                    var at = sequenceType.Kind == CTypeKind.Array ? "tcs_array_at" : "tcs_list_at";
                    Line(sequenceType.ElementCName + " *" + place + " = (" + sequenceType.ElementCName + " *)" + at + "(" + sequence + ", " + idx + ");");
                    Line("*" + place + " = " + stored + ";");
                    if (NeedsBarrier(targetType)) Line("tcs_wb(" + sequence + ");");
                    return;
                }
        }
        CFail.Report("unsupported assignment place: " + NodeName(assign.Target));
    }

    private static string TrimEndText(string text)
    {
        var u = new Utf8Bytes(text);
        int end = u.Length;
        while (end > 0 && (u[end - 1] == ' ' || u[end - 1] == '\n' || u[end - 1] == '\r' || u[end - 1] == '\t')) end--;
        return u.Slice(0, end).ToString();
    }

    private void EmitReturn(IlReturn ret)
    {
        if (_iifes.Count > 0)
        {
            var frame = _iifes[_iifes.Count - 1];
            if (ret.Value == null || CType.Same(frame.Type, CType.Void))
            {
                if (ret.Value != null) Line(RenderExpr(ret.Value) + ";");
                Line("goto " + frame.Label + ";");
                return;
            }
            CheckAssignable(frame.Type, ret.Value, "iife return");
            Line("{ " + frame.Result + " = " + RenderCoerced(ret.Value, frame.Type) + "; goto " + frame.Label + "; }");
            return;
        }
        if (ret.Value == null)
        {
            if (_ctorReturnValue != null)
            {
                Line("return " + _ctorReturnValue + ";");
                return;
            }
            RequireType(CType.Void, _currentMethodFact.ReturnType, "return");
            EmitOutWrites();
            Line("return;");
            return;
        }
        if (_currentMethodFact.OutParameters.Count > 0)
        {
            CheckAssignable(_currentMethodFact.ReturnType, ret.Value, "return");
            var result = Temp("ret");
            Line(_currentMethodFact.ReturnType.CName + " " + result + " = " + RenderCoerced(ret.Value, _currentMethodFact.ReturnType) + ";");
            EmitOutWrites();
            Line("return " + result + ";");
            return;
        }
        CheckAssignable(_currentMethodFact.ReturnType, ret.Value, "return");
        Line("return " + RenderCoerced(ret.Value, _currentMethodFact.ReturnType) + ";");
    }

    private void EmitDo(IlDo block)
    {
        Line("{");
        _indent++;
        PushScope();
        EmitStats(block.Body.Stats);
        PopScope();
        _indent--;
        Line("}");
    }

    private void EmitStaticInitializer()
    {
        // Class with a non-constant initializer: runs once on first access. The flag is set first, so a circular reference reads the in-progress value (default), as in C#
        foreach (var cls in StaticOwners)
        {
            if (!HasLazyStatics(cls)) continue;
            Line("static void");
            Line(Names.StaticInit(cls.Name) + "(void)");
            Line("{");
            _indent++;
            Line("static bool initialized;");
            Line("if (initialized) return;");
            Line("initialized = true;");
            EmitStaticFieldInits(cls);
            _indent--;
            Line("}");
            Line();
            FlushPendingClosures();
        }
        Line("static void");
        Line("tcs_init_statics(void)");
        Line("{");
        _indent++;
        Line("#ifdef TCS_VERIFY_TYPES");
        Line("tcs_verify_types();");
        Line("#endif");
        // Fill in constant-only classes first. A lazy class's initializer reads other classes' statics directly without accessors, so constant classes declared later must also have their values before they are read
        foreach (var cls in StaticOwners)
        {
            if (!HasLazyStatics(cls)) EmitStaticFieldInits(cls);
        }
        foreach (var cls in StaticOwners)
        {
            if (HasLazyStatics(cls)) Line(Names.StaticInit(cls.Name) + "();");
        }
        _indent--;
        Line("}");
        Line();
        FlushPendingClosures();
    }

    private void EmitStaticFieldInits(ExClass cls)
    {
        _currentClass = cls;
        _scopes.Clear();
        _scopeOrder.Clear();
        PushScope();
        foreach (var field in cls.Fields)
        {
            if (!field.IsStatic) continue;
            var fact = _facts.Field(cls.Name, field.Name);
            if (fact.Init == null) continue;
            CheckAssignable(fact.Type, fact.Init, "initializer of " + cls.Name + "." + field.Name);
            Line(Names.StaticField(cls.Name, field.Name) + " = " + RenderCoerced(fact.Init, fact.Type) + ";");
        }
        PopScope();
    }

    private void Line()
    {
        Line("");
    }

    private void Line(string text)
    {
        _output.Add(Indent(_indent * 4) + text + "\n");
    }

    private static string Indent(int n)
    {
        var parts = new List<string>();
        for (int i = 0; i < n; i++) parts.Add(" ");
        return string.Join("", parts);
    }

    private void Fail(string message)
    {
        CFail.Report(message);
    }

    // ---- Runtime C (runtime/, placed as is in the generated C) ----

    private string RuntimeFile(string path)
    {
        string? text;
        if (_runtime.TryGetValue(path, out text)) return text;
        CFail.Report("missing runtime/" + path);
        return "";
    }

    // Embed the C of runtime/<dir>: functions become static (TCS_*_STATIC), and `#include "..."` lines are dropped
    private string EmbedRuntime(string dir, string staticDefine, string names)
    {
        var parts = new List<string>();
        parts.Add("/* ---- runtime/" + dir + " ---- */\n#define " + staticDefine + " 1\n");
        foreach (var name in names.Split(' '))
        {
            string text = RuntimeFile(dir + "/" + name);
            var lines = text.Split('\n');
            // ReadLine does not return an empty line after the trailing newline
            int count = lines.Length;
            if (count > 0 && lines[count - 1] == "") count--;
            for (int i = 0; i < count; i++)
            {
                string line = lines[i];
                if (line.EndsWith("\r")) line = Strings.Prefix(line, ContractFacts.Len(line) - 1);
                if (line.StartsWith("#include \"")) continue;
                parts.Add(line + "\n");
            }
        }
        return string.Join("", parts);
    }

    private string RuntimeMath() => EmbedRuntime("math", "TCS_MATH_STATIC", "tcs_math.h tcs_math_internal.h atan2f.c exppowf.c log10f.c log2f.c logf.c sincosf.c tanf.c");
    private string RuntimeFmt() => EmbedRuntime("fmt", "TCS_FMT_STATIC", "tcs_fmt.h f2s.c printf.c parse.c");
    // Type descriptor parser and layout (docs/tcs/object-model.md). Used only by TCS_VERIFY_TYPES builds
    private string RuntimeModel() => EmbedRuntime("model", "TCS_MODEL_STATIC", "tcs_model.h tcs_model.c");
    private string RuntimeUtf8() => EmbedRuntime("utf8", "TCS_UTF8_STATIC", "tcs_utf8.h tcs_utf8.c");
    // Body of the tcs2c runtime (runtime/core; C shared with the VM per X-2 in object-model §10.2). A fragment placed as is in the generated C
    private string RuntimePrelude() => RuntimeFile("core/tcs_prelude.h");
    // Uses tcs_format_f32 from runtime/fmt, so it goes after the embedded runtime (Math, Utf8, Fmt)
    private string RuntimeCore() => RuntimeFile("core/tcs_core.c");
    private string RuntimeGc() => RuntimeFile("core/tcs_gc.c");
    private string RuntimeCollections() => RuntimeFile("core/tcs_collections.c");
    private string RuntimeStrings() => RuntimeFile("core/tcs_strings.c");
    private string RuntimeLib() => RuntimeFile("core/tcs_lib.c");

    // List of runtime C files (the host reads them and passes them to CEmitter; paths relative to runtime/)
    public static List<string> RuntimeFileNames()
    {
        var list = new List<string>();
        foreach (var n in "tcs_math.h tcs_math_internal.h atan2f.c exppowf.c log10f.c log2f.c logf.c sincosf.c tanf.c".Split(' ')) list.Add("math/" + n);
        foreach (var n in "tcs_fmt.h f2s.c printf.c parse.c".Split(' ')) list.Add("fmt/" + n);
        foreach (var n in "tcs_model.h tcs_model.c".Split(' ')) list.Add("model/" + n);
        foreach (var n in "tcs_utf8.h tcs_utf8.c".Split(' ')) list.Add("utf8/" + n);
        foreach (var n in "tcs_prelude.h tcs_core.c tcs_gc.c tcs_collections.c tcs_strings.c tcs_lib.c".Split(' ')) list.Add("core/" + n);
        return list;
    }

    // ---- Helpers ----

    private CType TypeOfIsType(IlIsType typeTest)
    {
        if (!_classes.ContainsKey(typeTest.TypeRef))
        {
            CFail.Report("IlIsType target is not a class: " + typeTest.TypeRef);
            return CType.Bool;
        }
        var operand = TypeOf(typeTest.E);
        if (operand.Kind != CTypeKind.Ref && operand.Kind != CTypeKind.Null && operand.Kind != CTypeKind.Object)
            CFail.Report("IlIsType operand is not a class reference: " + operand.Display());
        return CType.Bool;
    }

    // Explicit downcast: null passes through; faults if the runtime type is out of range (equivalent to C#'s InvalidCastException)
    private CType TypeOfCast(IlCast cast)
    {
        if (!_classes.ContainsKey(cast.TypeRef))
        {
            CFail.Report("IlCast target is not a class: " + cast.TypeRef);
            return CType.Void;
        }
        var source = TypeOf(cast.E);
        if (source.Kind != CTypeKind.Ref && source.Kind != CTypeKind.Null && source.Kind != CTypeKind.Object)
            CFail.Report("IlCast operand is not a class reference: " + source.Display());
        return CType.Ref(cast.TypeRef);
    }

    private string RenderCast(IlCast cast)
    {
        var type = TypeOfCast(cast);
        return "((" + type.CName + ")tcs_cast(" + RenderExpr(cast.E) + ", " + Names.TypeId(cast.TypeRef) + ", " + Names.TypeIdMax(cast.TypeRef) + "))";
    }

    // Numeric cast (int / float target). IL makes float → int explicit with __tcs_trunc, so what reaches here is int ↔ float widening, identity (char / enum), and unboxing from T? / object
    private CType TypeOfNumericConvert(IlNumericConvert convert)
    {
        var target = convert.TargetType == "int" ? CType.I32 : CType.F32;
        var source = TypeOf(convert.Value);
        if (CType.Same(source, CType.Object)) return target;
        if (source.Kind == CTypeKind.Nullable) source = source.Element!;
        if (source.Kind != CTypeKind.I32 && source.Kind != CTypeKind.F32)
            CFail.Report("numeric cast of non-numeric operand: " + source.Display());
        return target;
    }

    private string RenderNumericConvert(IlNumericConvert convert)
    {
        var target = TypeOfNumericConvert(convert);
        var source = TypeOf(convert.Value);
        if (CType.Same(source, CType.Object)) return RenderUnbox(convert.Value, target);
        if (source.Kind == CTypeKind.Nullable)
        {
            // (int)x where x is T?: no value faults (same as .Value)
            var temp = Temp("opt");
            return "({ " + source.CName + " " + temp + " = " + RenderExpr(convert.Value) + "; "
                + "if (!" + temp + ".has) tcs_fault(\"nullable-value\"); (" + target.CName + ")" + temp + ".v; })";
        }
        if (CType.Same(source, target)) return RenderExpr(convert.Value);
        if (CType.Same(target, CType.I32)) return "tcs_trunc_f32(" + RenderExpr(convert.Value) + ")";
        return "((float)(" + RenderExpr(convert.Value) + "))";
    }

    // Cast to a reference type / bool / T? / object. IL makes user class downcasts explicit with IlCast. Casts from object are checked by the runtime tag (the equivalent of C#'s InvalidCastException is a fault)
    private CType TypeOfRefCast(IlRefCast cast)
    {
        var target = _facts.MapType(cast.TargetType);
        var source = TypeOf(cast.Value);
        if (CType.Same(source, target) || target.CanAssignFrom(source)) return target;
        if (CType.Same(source, CType.Object) && (target.IsNullable || target.Kind == CTypeKind.Nullable || CType.Same(target, CType.Bool)))
            return target;
        // A cast involving an interface can succeed even without a static inheritance relation (tcs_interface_cast / tcs_cast check it at runtime)
        if (source.Kind == CTypeKind.Ref && target.Kind == CTypeKind.Ref
            && (IsAncestorOrSame(source.Name!, target.Name!) || IsInterface(source.Name!) || IsInterface(target.Name!)))
            return target;
        CFail.Report("unsupported cast: " + source.Display() + " to " + target.Display());
        return target;
    }

    private string RenderRefCast(IlRefCast cast)
    {
        var target = TypeOfRefCast(cast);
        var source = TypeOf(cast.Value);
        if (CType.Same(target, CType.Object)) return RenderBox(cast.Value);
        if (!CType.Same(source, CType.Object) && (CType.Same(source, target) || target.CanAssignFrom(source)))
            return RenderCoerced(cast.Value, target);
        var value = RenderExpr(cast.Value);
        if (target.Kind == CTypeKind.Nullable)
        {
            var temp = Temp("obj");
            var field = BoxField(target.Element!);
            return "({ void *" + temp + " = " + value + "; " + temp + " == NULL ? (" + target.CName + "){0} "
                + ": (" + target.CName + "){ true, tcs_unbox(" + temp + ", " + RuntimeTypeId(target.Element!) + ")->value." + field + " }; })";
        }
        if (CType.Same(target, CType.Bool)) return RenderUnbox(cast.Value, target);
        if (target.Kind == CTypeKind.Ref && IsInterface(target.Name!))
            return "((" + target.CName + ")tcs_interface_cast(" + value + ", " + Names.InterfaceCheck(target.Name!) + "))";
        if (target.Kind == CTypeKind.Ref)
            return "((" + target.CName + ")tcs_cast(" + value + ", " + Names.TypeId(target.Name!) + ", " + Names.TypeIdMax(target.Name!) + "))";
        var id = RuntimeTypeId(target);
        return "((" + target.CName + ")tcs_cast(" + value + ", " + id + ", " + id + "))";
    }

    private string RenderIsType(IlIsType typeTest)
    {
        TypeOfIsType(typeTest);
        var value = RenderExpr(typeTest.E);
        if (IsInterface(typeTest.TypeRef)) return Names.InterfaceCheck(typeTest.TypeRef) + "(" + value + ")";
        if (!Effectful(typeTest.E))
            return "(" + value + " != NULL && tcs_type_in_range(TCS_GC_HEADER(" + value + ")->type_id, "
                + Names.TypeId(typeTest.TypeRef) + ", " + Names.TypeIdMax(typeTest.TypeRef) + "))";
        var temp = Temp("is_object");
        return "({ void *" + temp + " = " + value + "; " + temp + " != NULL && "
            + "tcs_type_in_range(((TcsObjectHeader *)" + temp + ")->type_id, "
            + Names.TypeId(typeTest.TypeRef) + ", " + Names.TypeIdMax(typeTest.TypeRef) + "); })";
    }

    private static CType TypeOfLiteral(IlLit literal)
    {
        if (literal.Kind == IlLitKind.Bool) return CType.Bool;
        if (literal.Kind == IlLitKind.Null) return CType.Null;
        if (literal.Kind == IlLitKind.String) return CType.String;
        return literal.Kind == IlLitKind.Float ? CType.F32 : CType.I32;
    }

    private string RenderLiteral(IlLit literal)
    {
        if (literal.Kind == IlLitKind.Bool) return literal.I != 0 ? "true" : "false";
        if (literal.Kind == IlLitKind.Null) return "NULL";
        if (literal.Kind == IlLitKind.String) return InternStringLiteral(Utf8List(literal.S));
        if (literal.Kind == IlLitKind.Float) return Constants.F32(literal.F);
        return Constants.I32(literal.I);
    }

    // UTF-8 byte sequence of a string
    private static List<int> Utf8List(string text)
    {
        var u = new Utf8Bytes(text);
        var bytes = new List<int>();
        for (int i = 0; i < u.Length; i++) bytes.Add(u[i]);
        return bytes;
    }

    private static CType NumericJoin(CType left, CType right, string where)
    {
        if (left.Kind != CTypeKind.I32 && left.Kind != CTypeKind.F32 || right.Kind != CTypeKind.I32 && right.Kind != CTypeKind.F32)
        {
            CFail.Report(where + " operands are not numeric: " + left.Display() + ", " + right.Display());
            return CType.I32;
        }
        return left.Kind == CTypeKind.F32 || right.Kind == CTypeKind.F32 ? CType.F32 : CType.I32;
    }

    private static bool IsScalar(CType t) => t.Kind == CTypeKind.I32 || t.Kind == CTypeKind.F32 || t.Kind == CTypeKind.Bool;

    private static bool IsNumeric(CType t) => t.Kind == CTypeKind.I32 || t.Kind == CTypeKind.F32;

    private static CType CommonType(CType left, CType right, string where)
    {
        if (CType.Same(left, right)) return left;
        if (CType.Same(left, CType.Null) && IsScalar(right)) return CType.Nullable(right);
        if (CType.Same(right, CType.Null) && IsScalar(left)) return CType.Nullable(left);
        if (left.Kind == CTypeKind.Nullable && left.CanAssignFrom(right)) return left;
        if (right.Kind == CTypeKind.Nullable && right.CanAssignFrom(left)) return right;
        if (IsNumeric(left) && IsNumeric(right)) return NumericJoin(left, right, where);
        if (left.Kind == CTypeKind.Null && right.IsNullable) return right;
        if (right.Kind == CTypeKind.Null && left.IsNullable) return left;
        if (left.Kind == CTypeKind.List && right.Kind == CTypeKind.List)
        {
            if (left.Element == null) return right;
            if (right.Element == null) return left;
        }
        // Objects of different classes (`object v = c ? new C1() : new C0()`, a C# target-typed conditional expression) are object
        if (left.Kind == CTypeKind.Ref && right.Kind == CTypeKind.Ref) return CType.Object;
        CFail.Report("incompatible " + where + ": " + left.Display() + ", " + right.Display());
        return left;
    }

    private void RequireComparable(CType left, CType right, string where)
    {
        // object: reference identity (boxes are also compared by pointer, same as C#)
        if (CType.Same(left, CType.Object) && (right.IsNullable || CType.Same(right, CType.Null))
            || CType.Same(right, CType.Object) && (left.IsNullable || CType.Same(left, CType.Null))) return;
        if (left.Kind == CTypeKind.Nullable || right.Kind == CTypeKind.Nullable)
        {
            if (CType.Same(left, CType.Null) || CType.Same(right, CType.Null)) return;
            RequireComparable(left.Kind == CTypeKind.Nullable ? left.Element! : left, right.Kind == CTypeKind.Nullable ? right.Element! : right, where);
            return;
        }
        if (left.Kind == CTypeKind.Ref && right.Kind == CTypeKind.Ref
            && (IsAncestorOrSame(left.Name!, right.Name!) || IsAncestorOrSame(right.Name!, left.Name!))) return;
        if (IsNumeric(left) && IsNumeric(right)) return;
        if (CType.Same(left, right) && (left.Kind == CTypeKind.Bool || left.Kind == CTypeKind.String || left.Kind == CTypeKind.Ref
            || left.Kind == CTypeKind.Array || left.Kind == CTypeKind.List || left.Kind == CTypeKind.Queue
            || left.Kind == CTypeKind.HashSet || left.Kind == CTypeKind.Random)) return;
        if (left.Kind == CTypeKind.Null && right.IsNullable || right.Kind == CTypeKind.Null && left.IsNullable) return;
        if (left.Kind == CTypeKind.Nullable && (right.Kind == CTypeKind.Null || right.Kind == CTypeKind.Nullable)
            || right.Kind == CTypeKind.Nullable && (left.Kind == CTypeKind.Null || left.Kind == CTypeKind.Nullable)) return;
        if (left.Kind == CTypeKind.Nullable && left.Element!.CanAssignFrom(right)
            || right.Kind == CTypeKind.Nullable && right.Element!.CanAssignFrom(left)) return;
        CFail.Report("incompatible " + where + " operands: " + left.Display() + ", " + right.Display());
    }

    private static void RequireType(CType expected, CType actual, string where)
    {
        if (!CType.Same(expected, actual)) CFail.Report(where + ": expected " + expected.Display() + ", got " + actual.Display());
    }

    // Closures / static method groups cannot be typed on their own (the target decides)
    private static bool IsClosureValue(IlExpr expr) => expr is IlClosure || expr is IlMethodGroup;

    private void CheckAssignable(CType target, IlExpr value, string where)
    {
        if (target.Kind == CTypeKind.Closure && IsClosureValue(value)) return;
        RequireAssignable(target, TypeOf(value), where);
    }

    private void RequireAssignable(CType target, CType source, string where)
    {
        if (target.CanAssignFrom(source)) return;
        // Inheritance upcast: Derived → Base (il-spec §9). Downcasts (assignment to a pattern local / explicit cast) also go through a C cast — casts are transparent through type erasure (support-matrix), and the following IlIsType guards is-patterns
        if (IsPatternBindable(target, source)) return;
        CFail.Report(where + ": cannot assign " + source.Display() + " to " + target.Display());
    }

    // Pairs that can bind to a pattern designation. object and interface inputs can match the declared type by their runtime type (for two interfaces, the same class can implement both even without an inheritance relation)
    private bool IsPatternBindable(CType target, CType source)
    {
        if (target.Kind != CTypeKind.Ref) return false;
        if (CType.Same(source, CType.Object)) return true;
        return source.Kind == CTypeKind.Ref
            && (IsAncestorOrSame(target.Name!, source.Name!) || IsAncestorOrSame(source.Name!, target.Name!)
                || IsInterface(target.Name!) || IsInterface(source.Name!));
    }

    // Render with an explicit C cast if an upcast is needed. Closures can only be generated in a typed context (IlClosure has no parameter types)
    private string RenderCoerced(IlExpr expr, CType target)
    {
        if (CType.Same(target, CType.Object)) return RenderBox(expr);
        if (target.Kind == CTypeKind.Closure)
        {
            if (expr is IlClosure closure) return RenderClosure(closure, target);
            // Passed by value, so no struct parameter copy (CopyTypes) is needed
            if (expr is IlMethodGroup group) return RenderStaticGroupThunk(group.TypeRef, group.Method, target);
        }
        var source = TypeOf(expr);
        // Implicit conversion to T? (a safety net where IL has no IlNullableWrap)
        if (target.Kind == CTypeKind.Nullable && !CType.Same(source, target))
        {
            if (source.Kind == CTypeKind.Null) return "((" + target.CName + "){0})";
            return "((" + target.CName + "){ true, " + RenderCoerced(expr, target.Element!) + " })";
        }
        var rendered = RenderExpr(expr);
        if (target.Kind == CTypeKind.Ref && source.Kind == CTypeKind.Ref && target.Name != source.Name)
            return "((" + target.CName + ")" + rendered + ")";
        return rendered;
    }

    private static bool Effectful(IlExpr expr)
    {
        switch (expr)
        {
            case IlLit literal: return literal.Kind == IlLitKind.String;
            case IlVar: return false;
            case IlParen paren: return Effectful(paren.E);
            case IlNumericConvert convert: return Effectful(convert.Value);
            case IlRefCast cast: return Effectful(cast.Value);
            case IlUn unary: return Effectful(unary.E);
            case IlBin binary: return Effectful(binary.L) || Effectful(binary.R);
            case IlTernary ternary: return Effectful(ternary.Cond) || Effectful(ternary.T) || Effectful(ternary.F);
            case IlIsType typeTest: return Effectful(typeTest.E);
            case IlCast cast: return Effectful(cast.E);
            case IlNullableWrap w: return Effectful(w.E);
            case IlNullableHasValue h: return Effectful(h.E);
            case IlNullableValue: return true;
            case IlNullableGetOrDefault g: return Effectful(g.E) || Effectful(g.Default);
            case IlLiftedBin lb: return Effectful(lb.L) || Effectful(lb.R);
            case IlLiftedUn lu: return Effectful(lu.E);
        }
        return true;
    }

    private static void RequireArity(string callee, int actual, int expected)
    {
        if (actual != expected) CFail.Report(callee + ": expected " + expected + " arguments, got " + actual);
    }

    private static string Unsupported(object node)
    {
        CFail.Report("unsupported IL node: " + NodeName(node));
        return "";
    }

    private Variable Resolve(string name)
    {
        var v = TryResolve(name);
        if (v != null) return v;
        CFail.Report("unbound IL variable: " + name);
        return new Variable("0", CType.Void);
    }

    private Variable? TryResolve(string name)
    {
        // C# Stack enumeration goes from the top: inner scopes are checked first
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            Variable? variable;
            if (_scopes[i].TryGetValue(name, out variable)) return variable;
        }
        return null;
    }

    // Redeclaring a local with the same name in IL replaces the binding (last one wins). The C side issues a distinct name each time (v_..._serial), so there is no collision
    private void AddVariable(string name, Variable variable)
    {
        var scope = _scopes[_scopes.Count - 1];
        if (!scope.ContainsKey(name)) _scopeOrder[_scopeOrder.Count - 1].Add(name);
        scope[name] = variable;
    }

    private void PushScope()
    {
        _scopes.Add(new Dictionary<string, Variable>());
        _scopeOrder.Add(new List<string>());
    }

    private void PopScope()
    {
        _scopes.RemoveAt(_scopes.Count - 1);
        _scopeOrder.RemoveAt(_scopeOrder.Count - 1);
    }

    private string Temp(string role) => "__tcs_" + role + "_" + _serial++;

    // Equivalent of `x.GetType().Name` in the C# version (IL node name used in error messages)
    private static string NodeName(object node)
    {
        switch (node)
        {
            case IlLit: return "IlLit";
            case IlVar: return "IlVar";
            case IlField: return "IlField";
            case IlIndex: return "IlIndex";
            case IlLen: return "IlLen";
            case IlBin: return "IlBin";
            case IlUn: return "IlUn";
            case IlParen: return "IlParen";
            case IlTernary: return "IlTernary";
            case IlCall: return "IlCall";
            case IlDynCall: return "IlDynCall";
            case IlInvoke: return "IlInvoke";
            case IlNewObj: return "IlNewObj";
            case IlTable: return "IlTable";
            case IlNewArray: return "IlNewArray";
            case IlNewQueue: return "IlNewQueue";
            case IlNewHashSet: return "IlNewHashSet";
            case IlNumericConvert: return "IlNumericConvert";
            case IlRefCast: return "IlRefCast";
            case IlIsType: return "IlIsType";
            case IlIsPrimType: return "IlIsPrimType";
            case IlIife: return "IlIife";
            case IlClosure: return "IlClosure";
            case IlMethodGroup: return "IlMethodGroup";
            case IlStructCopy: return "IlStructCopy";
            case IlNullableWrap: return "IlNullableWrap";
            case IlNullableHasValue: return "IlNullableHasValue";
            case IlNullableValue: return "IlNullableValue";
            case IlNullableGetOrDefault: return "IlNullableGetOrDefault";
            case IlLiftedBin: return "IlLiftedBin";
            case IlLiftedUn: return "IlLiftedUn";
            case IlCast: return "IlCast";
            case IlStructDescriptor: return "IlStructDescriptor";
            case IlWith: return "IlWith";
            case IlLocal: return "IlLocal";
            case IlAssign: return "IlAssign";
            case IlCallStat: return "IlCallStat";
            case IlIf: return "IlIf";
            case IlWhile: return "IlWhile";
            case IlRepeat: return "IlRepeat";
            case IlNumericFor: return "IlNumericFor";
            case IlForeachList: return "IlForeachList";
            case IlForeachDict: return "IlForeachDict";
            case IlForeachRunes: return "IlForeachRunes";
            case IlBreak: return "IlBreak";
            case IlContinue: return "IlContinue";
            case IlReturn: return "IlReturn";
            case IlDo: return "IlDo";
            case IlBreakScope: return "IlBreakScope";
            case IlMultiAssign: return "IlMultiAssign";
            case IlForPairs: return "IlForPairs";
        }
        return "?";
    }

    // ---- Expressions ----

    // ==== CEmitter.Expressions.cs ====

    private string RenderExpr(IlExpr expr)
    {
        switch (expr)
        {
            case IlLit literal: return RenderLiteral(literal);
            case IlVar variable:
                {
                    var cell = Resolve(variable.Name);
                    if (cell.Boxed) return "(*" + cell.CName + ")";
                    return cell.CName;
                }
            case IlField field: return RenderField(field);
            case IlIndex index: return RenderIndex(index);
            case IlLen length: return RenderLength(length);
            case IlBin binary: return RenderBinary(binary);
            case IlUn unary: return RenderUnary(unary);
            case IlParen paren: return "(" + RenderExpr(paren.E) + ")";
            case IlTernary ternary: return RenderTernary(ternary);
            case IlCall call: return RenderCall(call);
            case IlDynCall dynCall: return RenderDynCall(dynCall);
            case IlInvoke invoke: return RenderInvoke(invoke);
            case IlNewObj creation: return RenderNew(creation);
            case IlTable table: return RenderTable(table);
            case IlNewArray array: return RenderNewArray(array);
            case IlNewQueue queue: return RenderNewQueue(queue);
            case IlNewHashSet set: return RenderNewHashSet(set);
            case IlIsType typeTest: return RenderIsType(typeTest);
            // C struct value assignment is already a copy, so IL copy points (il-spec §10) write the expression as is
            case IlStructCopy copy: return RenderExpr(copy.E);
            case IlCast cast: return RenderCast(cast);
            case IlNumericConvert convert: return RenderNumericConvert(convert);
            case IlRefCast refCast: return RenderRefCast(refCast);
            case IlNullableWrap wrap: return RenderNullableWrap(wrap);
            case IlNullableHasValue has: return "((" + RenderExpr(has.E) + ").has)";
            case IlNullableValue val: return RenderNullableValue(val);
            case IlNullableGetOrDefault gd: return RenderNullableGetOrDefault(gd);
            case IlLiftedBin lifted: return RenderLiftedBin(lifted);
            case IlLiftedUn liftedUn: return RenderLiftedUn(liftedUn);
            case IlIife iife: return RenderIife(iife);
            case IlWith with: return RenderWith(with);
        }
        return Unsupported(expr);
    }

    private CType TypeOf(IlExpr expr)
    {
        switch (expr)
        {
            case IlLit literal: return TypeOfLiteral(literal);
            case IlVar variable: return Resolve(variable.Name).Type;
            case IlField field: return TypeOfField(field);
            case IlIndex index:
                if (index.PlusOne || TypeOf(index.Recv).Kind != CTypeKind.Dict)
                    return RequireSequence(index).Element!;
                return TypeOf(index.Recv).Element!;
            case IlLen length: return TypeOfLength(length);
            case IlBin binary: return TypeOfBinary(binary);
            case IlUn unary:
                if (unary.Op == IlUnOp.Not) return CType.Bool;
                return TypeOfUnary(unary);
            case IlParen paren: return TypeOf(paren.E);
            case IlTernary ternary: return TypeOfTernary(ternary);
            case IlCall call: return TypeOfCall(call);
            case IlDynCall dynCall: return TypeOfDynCall(dynCall);
            case IlInvoke invoke: return TypeOfInvoke(invoke);
            case IlNewObj creation:
                if (_facts.Structs.ContainsKey(creation.TypeName)) return CType.Struct(creation.TypeName);
                if (IsRandomTypeName(creation.TypeName)) return TypeOfRandomNew(creation);
                return CType.Ref(creation.TypeName);
            case IlTable table: return TypeOfTable(table);
            case IlNewArray array: return TypeOfNewArray(array);
            case IlNewQueue queue: return TypeOfNewQueue(queue);
            case IlNewHashSet set: return TypeOfNewHashSet(set);
            case IlIsType typeTest: return TypeOfIsType(typeTest);
            case IlStructCopy copy: return TypeOf(copy.E);
            case IlCast cast: return TypeOfCast(cast);
            case IlNumericConvert convert: return TypeOfNumericConvert(convert);
            case IlRefCast refCast: return TypeOfRefCast(refCast);
            case IlNullableWrap wrap: return TypeOfNullableWrap(wrap);
            case IlNullableHasValue has:
                RequireNullable(has.E, "HasValue");
                return CType.Bool;
            case IlNullableValue val:
                {
                    var nullable = RequireNullable(val.E, "Value");
                    if (nullable.Element == null) return CType.Void;
                    return nullable.Element;
                }
            case IlNullableGetOrDefault gd: return TypeOfNullableGetOrDefault(gd);
            case IlLiftedBin lifted: return TypeOfLiftedBin(lifted);
            case IlLiftedUn liftedUn: return TypeOfLiftedUn(liftedUn);
            case IlIife iife: return TypeOfIife(iife);
            case IlWith with: return TypeOfWith(with);
        }
        Unsupported(expr);
        return CType.Void;
    }

    private CType TypeOfPlace(IlExpr expr)
    {
        if (expr is IlVar || expr is IlField || expr is IlIndex) return TypeOf(expr);
        CFail.Report("expression is not a place: " + NodeName(expr));
        return CType.Void;
    }

    private string RenderField(IlField field)
    {
        var foreignValue = ForeignValue(field);
        if (foreignValue != null)
        {
            return RenderForeignValue(foreignValue, out CType foreignType);
        }
        if (TryStaticField(field, out string staticName, out CType staticType)) return staticName;
        var receiver = TypeOf(field.Recv);
        if (receiver.Kind == CTypeKind.Kvp && field.Recv is IlVar kvpVar)
        {
            var node = Resolve(kvpVar.Name).CName;
            if (field.Name == "Key")
            {
                if (receiver.KeyType!.Kind == CTypeKind.String) return node + "->key_s";
                if (receiver.KeyType.Kind == CTypeKind.Ref) return "((" + receiver.KeyType.CName + ")" + node + "->key_s)";
                return node + "->key_i";
            }
            if (field.Name == "Value") return "(*(" + receiver.Element!.CName + " *)" + node + "->value)";
            CFail.Report("unknown KeyValuePair member: " + field.Name);
            return "";
        }
        if (receiver.Kind == CTypeKind.StructVal)
            return RenderStructPlace(field.Recv) + "." + Names.Field(field.Name);
        if (receiver.Kind != CTypeKind.Ref)
        {
            CFail.Report("field receiver is not a class reference");
            return "";
        }
        FieldInChain(receiver.Name!, field.Name);
        return "((" + receiver.CName + ")tcs_nonnull(" + RenderExpr(field.Recv) + "))->" +
            Names.Field(field.Name);
    }

    private CType TypeOfField(IlField field)
    {
        var foreignValue = ForeignValue(field);
        if (foreignValue != null)
        {
            RenderForeignValue(foreignValue, out CType foreignType);
            return foreignType;
        }
        if (TryStaticField(field, out string staticName, out CType staticType)) return staticType;
        var receiver = TypeOf(field.Recv);
        if (receiver.Kind == CTypeKind.Kvp)
        {
            if (field.Name == "Key") return receiver.KeyType!;
            if (field.Name == "Value") return receiver.Element!;
            CFail.Report("unknown KeyValuePair member: " + field.Name);
            return CType.Void;
        }
        if (receiver.Kind == CTypeKind.StructVal)
            return _facts.StructField(receiver.Name!, field.Name);
        if (receiver.Kind != CTypeKind.Ref)
        {
            CFail.Report("field receiver is not a class reference");
            return CType.Void;
        }
        return FieldInChain(receiver.Name!, field.Name).Type;
    }

    private bool TryStaticField(IlField field, out string cName, out CType type)
    {
        cName = "";
        type = CType.Void;
        if (field.Recv is IlVar enumRecv && TryResolve(enumRecv.Name) == null)
        {
            if (_facts.TryEnumConstant(enumRecv.Name, field.Name, out int constant))
            {
                cName = Constants.I32(constant);
                type = CType.I32;
                return true;
            }
        }
        if (field.Recv is IlVar randomRecv && field.Name == "Shared"
            && IsRandomTypeName(randomRecv.Name) && TryResolve(randomRecv.Name) == null)
        {
            cName = "tcs_random_shared()";
            type = CType.Random;
            return true;
        }
        if (field.Recv is IlVar mathRecv && (mathRecv.Name == "Math" || mathRecv.Name == "MathF") && field.Name == "PI"
            && TryResolve(mathRecv.Name) == null && !_classes.ContainsKey(mathRecv.Name))
        {
            // Result of Constants.F32((float)Math.PI) (the bits of float π are 0x40490fdb)
            cName = "tcs_f32(UINT32_C(0x40490fdb))";
            type = CType.F32;
            return true;
        }
        if (field.Recv is IlVar receiver)
        {
            ExClass? cls;
            bool found = _classes.TryGetValue(receiver.Name, out cls);
            if (!found) found = StructStaticOwners.TryGetValue(receiver.Name, out cls);
            if (found && cls != null)
            {
                ExField? metadata = null;
                foreach (var f in cls.Fields)
                {
                    if (f.Name == field.Name && f.IsStatic)
                    {
                        metadata = f;
                        break;
                    }
                }
                if (metadata != null)
                {
                    cName = StaticFieldPlace(cls, field.Name);
                    type = _facts.Field(cls.Name, field.Name).Type;
                    return true;
                }
            }
        }
        cName = "";
        type = CType.Void;
        return false;
    }

    private string RenderIndex(IlIndex index)
    {
        if (!index.PlusOne && TypeOf(index.Recv).Kind == CTypeKind.Dict)
        {
            var valueType = RequireDict(index.Recv, out CType dictType);
            if (dictType.KeyType == null) return "";
            var dictTemp = Temp("dict");
            return "({ TcsDict *" + dictTemp + " = " + RenderExpr(index.Recv) + "; " +
                "*(" + valueType.CName + " *)tcs_dict_at(" + dictTemp + ", " +
                DictKeyArgs(dictType.KeyType!, index.Idx) + "); })";
        }
        var sequenceType = RequireSequence(index);
        var sequence = Temp("index_sequence");
        var position = Temp("index");
        var at = sequenceType.Kind == CTypeKind.Array
            ? "tcs_array_at" : "tcs_list_at";
        return "({ " + sequenceType.CName + " " + sequence + " = " + RenderExpr(index.Recv) + "; " +
            "int32_t " + position + " = " + RenderExpr(index.Idx) + "; " +
            "*(" + sequenceType.ElementCName + " *)" + at + "(" + sequence + ", " + position + "); })";
    }

    private CType RequireSequence(IlIndex index)
    {
        if (!index.PlusOne)
        {
            CFail.Report("only 0-based array/List indexing is supported");
            return CType.Array(CType.Void);
        }
        RequireType(CType.I32, TypeOf(index.Idx), "sequence index");
        var sequence = TypeOf(index.Recv);
        if (sequence.Kind != CTypeKind.Array && sequence.Kind != CTypeKind.List)
        {
            CFail.Report("index receiver is not an array/List");
            return CType.Array(CType.Void);
        }
        if (sequence.Element == null)
        {
            CFail.Report("cannot index a List with unknown element type");
            return CType.Array(CType.Void);
        }
        return sequence;
    }

    private CType TypeOfLength(IlLen length)
    {
        var type = TypeOf(length.E);
        if (type.Kind == CTypeKind.Array || type.Kind == CTypeKind.List) return CType.I32;
        CFail.Report("length receiver is not an array/List");
        return CType.Void;
    }

    private string RenderLength(IlLen length)
    {
        var type = TypeOf(length.E);
        string helper;
        if (type.Kind == CTypeKind.Array) helper = "tcs_array_length";
        else if (type.Kind == CTypeKind.List) helper = "tcs_list_length";
        else
        {
            CFail.Report("length receiver is not an array/List");
            return "";
        }
        return helper + "(" + RenderExpr(length.E) + ")";
    }

    private string RenderBinary(IlBin binary)
    {
        var leftType = TypeOf(binary.L);
        var rightType = TypeOf(binary.R);
        var resultType = TypeOfBinary(binary);
        if (binary.Op == IlBinOp.Concat)
        {
            var leftString = CType.Same(leftType, CType.String)
                ? RenderExpr(binary.L) : RenderToString(binary.L);
            var rightString = CType.Same(rightType, CType.String)
                ? RenderExpr(binary.R) : RenderToString(binary.R);
            var concatLeft = Temp("concat_lhs");
            var concatRight = Temp("concat_rhs");
            return "({ TcsString *" + concatLeft + " = " + leftString + "; " +
                "TcsString *" + concatRight + " = " + rightString + "; " +
                "tcs_string_concat(" + concatLeft + ", " + concatRight + "); })";
        }
        var left = RenderExpr(binary.L);
        var right = RenderExpr(binary.R);

        // ==/!= between T? and null / T? / T (lifted equality handles the remaining null comparisons from IL)
        if ((binary.Op == IlBinOp.Eq || binary.Op == IlBinOp.Ne)
            && (leftType.Kind == CTypeKind.Nullable || rightType.Kind == CTypeKind.Nullable))
            return RenderNullableEquality(binary, leftType, rightType);

        // Record ==/!= is structural equality
        if (binary.Op == IlBinOp.Eq || binary.Op == IlBinOp.Ne)
        {
            var recordType = RecordEqualityType(leftType, rightType);
            if (recordType != null)
            {
                var recordC = CType.Ref(recordType).CName;
                var eqLeft = Temp("eq_lhs");
                var eqRight = Temp("eq_rhs");
                var negate = binary.Op == IlBinOp.Ne ? "!" : "";
                return "({ " + leftType.CName + " " + eqLeft + " = " + left + "; " + rightType.CName + " " + eqRight + " = " + right + "; " +
                    negate + Names.RecordEq(recordType) + "((" + recordC + ")" + eqLeft + ", (" + recordC + ")" + eqRight + "); })";
            }
        }

        if (binary.Op == IlBinOp.And
            || binary.Op == IlBinOp.Or && CType.Same(leftType, CType.Bool))
            return RenderBinaryOperation(binary.Op, left, right, leftType, rightType);
        if (binary.Op == IlBinOp.Or && resultType.IsNullable)
        {
            if (leftType.Kind == CTypeKind.Null) return right;
            if (!Effectful(binary.L)) return "(" + left + " != NULL ? " + left + " : " + right + ")";
            var temp = Temp("coalesce");
            return "({ " + leftType.CName + " " + temp + " = " + left + "; " +
                temp + " != NULL ? " + temp + " : " + right + "; })";
        }
        if (leftType.Kind == CTypeKind.Null || rightType.Kind == CTypeKind.Null
            || !Effectful(binary.L) && !Effectful(binary.R))
            return RenderBinaryOperation(binary.Op, left, right, leftType, rightType);

        var leftTemp = Temp("lhs");
        var rightTemp = Temp("rhs");
        return "({ " + leftType.CName + " " + leftTemp + " = " + left + "; " +
            rightType.CName + " " + rightTemp + " = " + right + "; " +
            RenderBinaryOperation(binary.Op, leftTemp, rightTemp, leftType, rightType) + "; })";
    }

    private static string RenderBinaryOperation(IlBinOp op, string left, string right,
        CType leftType, CType rightType)
    {
        switch (op)
        {
            case IlBinOp.AddNum: return "(" + left + " + " + right + ")";
            case IlBinOp.Concat: return "tcs_string_concat(" + left + ", " + right + ")";
            case IlBinOp.Sub: return "(" + left + " - " + right + ")";
            case IlBinOp.Mul: return "(" + left + " * " + right + ")";
            case IlBinOp.DivNum: return "((float)" + left + " / (float)" + right + ")";
            case IlBinOp.Eq:
                if (CType.Same(leftType, CType.String) && CType.Same(rightType, CType.String))
                    return "tcs_string_equal(" + left + ", " + right + ")";
                // Reference identity: related classes / object / null are compared by pointer even if the types differ
                if (leftType.IsNullable || rightType.IsNullable)
                    return "((void *)" + left + " == (void *)" + right + ")";
                return "(" + left + " == " + right + ")";
            case IlBinOp.Ne:
                if (CType.Same(leftType, CType.String) && CType.Same(rightType, CType.String))
                    return "(!tcs_string_equal(" + left + ", " + right + "))";
                if (leftType.IsNullable || rightType.IsNullable)
                    return "((void *)" + left + " != (void *)" + right + ")";
                return "(" + left + " != " + right + ")";
            case IlBinOp.Lt:
            case IlBinOp.Le:
            case IlBinOp.Gt:
            case IlBinOp.Ge:
                if (leftType.Kind == CTypeKind.F32 || rightType.Kind == CTypeKind.F32)
                    return RenderFloatRelation(op, left, right);
                if (op == IlBinOp.Lt) return "(" + left + " < " + right + ")";
                if (op == IlBinOp.Le) return "(" + left + " <= " + right + ")";
                if (op == IlBinOp.Gt) return "(" + left + " > " + right + ")";
                return "(" + left + " >= " + right + ")";
            case IlBinOp.And: return "(" + left + " && " + right + ")";
            case IlBinOp.Or: return "(" + left + " || " + right + ")";
            case IlBinOp.BitAnd: return "(" + left + " & " + right + ")";
            case IlBinOp.BitOr: return "(" + left + " | " + right + ")";
            case IlBinOp.BitXor: return "(" + left + " ^ " + right + ")";
            case IlBinOp.Shl: return "tcs_shl(" + left + ", " + right + ")";
            case IlBinOp.Shr: return "tcs_shr(" + left + ", " + right + ")";
        }
        CFail.Report("unsupported binary operator: " + IlBinOpText(op));
        return "";
    }

    // Float ordering comparisons are written with the C99 isless family. The result is the same as < and friends (comparisons with NaN are
    // false), but GCC -O2 jump threading can derive equalities from combinations of < etc. without
    // considering NaN and remove things like `!(a == b)` (#110). The isless family is not
    // affected by this inference
    internal static string RenderFloatRelation(IlBinOp op, string left, string right)
    {
        string macro;
        if (op == IlBinOp.Lt) macro = "isless";
        else if (op == IlBinOp.Le) macro = "islessequal";
        else if (op == IlBinOp.Gt) macro = "isgreater";
        else macro = "isgreaterequal";
        return macro + "((float)(" + left + "), (float)(" + right + "))";
    }

    // Equivalent of C#'s IlBinOp.ToString() (for error messages and NumericJoin's where)
    private static string IlBinOpText(IlBinOp op)
    {
        switch (op)
        {
            case IlBinOp.AddNum: return "AddNum";
            case IlBinOp.Concat: return "Concat";
            case IlBinOp.Sub: return "Sub";
            case IlBinOp.Mul: return "Mul";
            case IlBinOp.DivNum: return "DivNum";
            case IlBinOp.RemNum: return "RemNum";
            case IlBinOp.Eq: return "Eq";
            case IlBinOp.Ne: return "Ne";
            case IlBinOp.Lt: return "Lt";
            case IlBinOp.Le: return "Le";
            case IlBinOp.Gt: return "Gt";
            case IlBinOp.Ge: return "Ge";
            case IlBinOp.And: return "And";
            case IlBinOp.Or: return "Or";
            case IlBinOp.BitAnd: return "BitAnd";
            case IlBinOp.BitOr: return "BitOr";
            case IlBinOp.BitXor: return "BitXor";
            case IlBinOp.Shl: return "Shl";
            case IlBinOp.Shr: return "Shr";
        }
        return "";
    }

    // Equivalent of C#'s IlUnOp.ToString() (for error messages)
    private static string IlUnOpText(IlUnOp op)
    {
        switch (op)
        {
            case IlUnOp.Neg: return "Neg";
            case IlUnOp.Not: return "Not";
            case IlUnOp.BitNot: return "BitNot";
            case IlUnOp.ToFloat: return "ToFloat";
        }
        return "";
    }

    private CType TypeOfBinary(IlBin binary)
    {
        var left = TypeOf(binary.L);
        var right = TypeOf(binary.R);
        switch (binary.Op)
        {
            case IlBinOp.AddNum:
            case IlBinOp.Sub:
            case IlBinOp.Mul:
                return NumericJoin(left, right, IlBinOpText(binary.Op));
            case IlBinOp.Concat:
                if (left.Kind != CTypeKind.I32 && left.Kind != CTypeKind.F32
                    && left.Kind != CTypeKind.Bool && left.Kind != CTypeKind.String
                    || right.Kind != CTypeKind.I32 && right.Kind != CTypeKind.F32
                    && right.Kind != CTypeKind.Bool && right.Kind != CTypeKind.String)
                {
                    CFail.Report("concat operands are not stringifiable: " +
                        left.Display() + ", " + right.Display());
                    return CType.Void;
                }
                return CType.String;
            case IlBinOp.DivNum:
                // IL DivNum is float division (integer division is __tcs_idiv)
                NumericJoin(left, right, "division");
                return CType.F32;
            case IlBinOp.Eq:
            case IlBinOp.Ne:
                RequireComparable(left, right, "equality");
                return CType.Bool;
            case IlBinOp.Lt:
            case IlBinOp.Le:
            case IlBinOp.Gt:
            case IlBinOp.Ge:
                NumericJoin(left, right, "comparison");
                return CType.Bool;
            case IlBinOp.And:
                RequireType(CType.Bool, left, "logical operand");
                RequireType(CType.Bool, right, "logical operand");
                return CType.Bool;
            case IlBinOp.Or:
                if (CType.Same(left, CType.Bool))
                {
                    RequireType(CType.Bool, right, "logical operand");
                    return CType.Bool;
                }
                return CommonType(left, right, "coalesce");
            case IlBinOp.BitAnd:
            case IlBinOp.BitOr:
            case IlBinOp.BitXor:
            case IlBinOp.Shl:
            case IlBinOp.Shr:
                RequireType(CType.I32, left, "bitwise operand");
                RequireType(CType.I32, right, "bitwise operand");
                return CType.I32;
        }
        CFail.Report("unsupported binary operator: " + IlBinOpText(binary.Op));
        return CType.Void;
    }

    // User-defined operators (including unary -) are IlCalls to the operator's static function
    // in IL, so operands that reach here are primitives only
    private CType TypeOfUnary(IlUn unary) => TypeOf(unary.E);

    private string RenderUnary(IlUn unary)
    {
        var value = RenderExpr(unary.E);
        var type = TypeOf(unary.E);
        // `--` is the decrement operator in C, so operands starting with `-` are wrapped in parentheses
        if (unary.Op == IlUnOp.Neg && (type.Kind == CTypeKind.I32 || type.Kind == CTypeKind.F32))
            return value.StartsWith("-") ? "(-(" + value + "))" : "(-" + value + ")";
        if (unary.Op == IlUnOp.Not && CType.Same(type, CType.Bool)) return "(!" + value + ")";
        if (unary.Op == IlUnOp.BitNot && CType.Same(type, CType.I32)) return "(~" + value + ")";
        CFail.Report("invalid unary operator " + IlUnOpText(unary.Op) + " for " + type.Display());
        return "";
    }

    private CType TypeOfTernary(IlTernary ternary)
    {
        RequireType(CType.Bool, TypeOf(ternary.Cond), "ternary condition");
        return CommonType(TypeOf(ternary.T), TypeOf(ternary.F), "ternary arms");
    }

    private string RenderTernary(IlTernary ternary)
    {
        // Coerce both arms to the result type (T? for `cond ? x : null` / upcast / int → float)
        var type = TypeOfTernary(ternary);
        return "(" + RenderExpr(ternary.Cond) + " ? " + RenderCoerced(ternary.T, type) + " : " +
            RenderCoerced(ternary.F, type) + ")";
    }

    private string RenderCall(IlCall call)
    {
        var foreign = ForeignMethod(call.Callee);
        if (foreign != null) return RenderForeignIlCall(foreign, call.Args);
        // Reference comparison between object / interface values (IL __tcs_refeq). C's == compares references, so it is written the same as IlBin Eq
        if (call.Callee == "__tcs_refeq")
            return RenderExpr(new IlBin(IlBinOp.Eq, call.Args[0], call.Args[1], null));
        var type = TypeOfCall(call);
        string callee = call.Callee;
        if (callee == "__tcs_idiv")
        {
            var values = new List<TypedValue>();
            values.Add(new TypedValue(CType.I32, RenderExpr(call.Args[0])));
            values.Add(new TypedValue(CType.I32, RenderExpr(call.Args[1])));
            return RenderOrderedCall("tcs_idiv", type, values);
        }
        if (callee == "__tcs_irem")
        {
            var values = new List<TypedValue>();
            values.Add(new TypedValue(CType.I32, RenderExpr(call.Args[0])));
            values.Add(new TypedValue(CType.I32, RenderExpr(call.Args[1])));
            return RenderOrderedCall("tcs_irem", type, values);
        }
        if (callee == "__tcs_cadd" || callee == "__tcs_csub" || callee == "__tcs_cmul")
        {
            var values = new List<TypedValue>();
            values.Add(new TypedValue(CType.I32, RenderExpr(call.Args[0])));
            values.Add(new TypedValue(CType.I32, RenderExpr(call.Args[1])));
            return RenderOrderedCall(
                "tcs_checked_" + Strings.From(callee, ContractFacts.Len("__tcs_c")), type, values);
        }
        if (callee == "__tcs_cneg") return "tcs_checked_neg(" + RenderExpr(call.Args[0]) + ")";
        if (callee == "__tcs_nonnull") return "((" + type.CName + ")tcs_nonnull(" + RenderExpr(call.Args[0]) + "))";
        if (callee == "__tcs_nomatch") return "tcs_fault(\"switch-no-match\")";
        if (callee == "__tcs_writeline") return RenderPrint(call);
        if (callee == "__tcs_write")
        {
            var values = new List<TypedValue>();
            values.Add(new TypedValue(CType.String, RenderToString(UnwrapFstr(call.Args[0]))));
            return RenderOrderedCall("tcs_write_string", CType.Void, values);
        }
        if (callee == "__tcs_str") return RenderToString(call.Args[0]);
        if (callee == "Dict.ContainsKey") return RenderDictSimple(call, "tcs_dict_contains");
        if (callee == "Dict.Remove") return RenderDictSimple(call, "tcs_dict_remove");
        if (callee == "Dict.Count")
            return "tcs_dict_count(" + RenderExpr(call.Args[0]) + ")";
        // Stringification of f32 and bool (__tcs_fstr / __tcs_bstr) maps to the C to-string
        // (shortest round-trip for f32)
        if (callee == "__tcs_fstr" || callee == "__tcs_bstr") return RenderToString(call.Args[0]);
        if (callee == "__tcs_nstr") return RenderNullableToString(call.Args[0]);
        if (callee == "List.Add") return RenderListAdd(call);
        if (IsIntrinsicCallee(call.Callee)) return RenderIntrinsic(call);
        return RenderUserCall(call);
    }

    private CType TypeOfCall(IlCall call)
    {
        var foreign = ForeignMethod(call.Callee);
        if (foreign != null) return TypeOfForeignIlCall(foreign, call.Args);
        if (call.Callee == "__tcs_refeq")
        {
            RequireArity(call.Callee, call.Args.Count, 2);
            if (CFail.Failed) return CType.Bool;
            return TypeOf(new IlBin(IlBinOp.Eq, call.Args[0], call.Args[1], null));
        }
        if (call.Callee == "__tcs_cneg")
        {
            RequireArity(call.Callee, call.Args.Count, 1);
            if (CFail.Failed) return CType.I32;
            RequireType(CType.I32, TypeOf(call.Args[0]), call.Callee);
            return CType.I32;
        }
        // Returns the reference value as is, and faults when it is null
        if (call.Callee == "__tcs_nonnull")
        {
            RequireArity(call.Callee, call.Args.Count, 1);
            if (CFail.Failed) return CType.Void;
            return TypeOf(call.Args[0]);
        }
        // The fault when no arm of a switch expression matches
        if (call.Callee == "__tcs_nomatch")
        {
            RequireArity(call.Callee, call.Args.Count, 0);
            return CType.Void;
        }
        if (call.Callee == "__tcs_idiv" || call.Callee == "__tcs_irem"
            || call.Callee == "__tcs_cadd" || call.Callee == "__tcs_csub" || call.Callee == "__tcs_cmul")
        {
            RequireArity(call.Callee, call.Args.Count, 2);
            if (CFail.Failed) return CType.I32;
            RequireType(CType.I32, TypeOf(call.Args[0]), call.Callee);
            RequireType(CType.I32, TypeOf(call.Args[1]), call.Callee);
            return CType.I32;
        }
        // Console.Write (no newline)
        if (call.Callee == "__tcs_write")
        {
            RequireArity("Console.Write", call.Args.Count, 1);
            if (CFail.Failed) return CType.Void;
            RenderToStringType(UnwrapFstr(call.Args[0]));
            return CType.Void;
        }
        if (call.Callee == "__tcs_writeline")
        {
            if (call.Args.Count == 0) return CType.Void;
            RequireArity(call.Callee, call.Args.Count, 1);
            if (CFail.Failed) return CType.Void;
            var argument = TypeOf(UnwrapFstr(call.Args[0]));
            if (_digestF32)
                RequireType(CType.F32, argument, "print/digest");
            else if (argument.Kind != CTypeKind.I32 && argument.Kind != CTypeKind.F32
                && argument.Kind != CTypeKind.Bool && argument.Kind != CTypeKind.String)
                CFail.Report("print does not support " + argument.Display());
            return CType.Void;
        }
        if (call.Callee == "Dict.ContainsKey")
        {
            RequireArity(call.Callee, call.Args.Count, 2);
            if (CFail.Failed) return CType.Bool;
            RequireDict(call.Args[0], out CType dictType0);
            if (dictType0.KeyType == null) return CType.Bool;
            RequireAssignable(dictType0.KeyType, TypeOf(call.Args[1]),
                "Dict.ContainsKey key");
            return CType.Bool;
        }
        if (call.Callee == "Dict.Remove")
        {
            RequireArity(call.Callee, call.Args.Count, 2);
            if (CFail.Failed) return CType.Bool;
            RequireDict(call.Args[0], out CType dictTypeR);
            if (dictTypeR.KeyType == null) return CType.Bool;
            RequireAssignable(dictTypeR.KeyType, TypeOf(call.Args[1]),
                "Dict.Remove key");
            return CType.Bool;
        }
        if (call.Callee == "Dict.Count")
        {
            RequireArity(call.Callee, call.Args.Count, 1);
            if (CFail.Failed) return CType.I32;
            RequireDict(call.Args[0], out CType dictTypeC);
            return CType.I32;
        }
        if (call.Callee == "__tcs_nstr")
        {
            RequireArity(call.Callee, call.Args.Count, 1);
            if (CFail.Failed) return CType.String;
            RequireNullable(call.Args[0], "__tcs_nstr");
            return CType.String;
        }
        if (call.Callee == "__tcs_str" || call.Callee == "__tcs_fstr" || call.Callee == "__tcs_bstr")
        {
            RequireArity(call.Callee, call.Args.Count, 1);
            if (CFail.Failed) return CType.String;
            var argument = TypeOf(call.Args[0]);
            if (argument.Kind != CTypeKind.I32 && argument.Kind != CTypeKind.F32
                && argument.Kind != CTypeKind.Bool && argument.Kind != CTypeKind.String)
                CFail.Report("tostring does not support " + argument.Display());
            return CType.String;
        }
        if (call.Callee == "List.Add")
        {
            RequireArity(call.Callee, call.Args.Count, 2);
            if (CFail.Failed) return CType.Void;
            CType? valueType = null;
            if (!IsClosureValue(call.Args[1])) valueType = TypeOf(call.Args[1]);
            RequireListForAdd(call.Args[0], valueType);
            return CType.Void;
        }
        if (IsIntrinsicCallee(call.Callee)) return TypeOfIntrinsic(call);
        if (TryStructCallee(call.Callee, out var st, out var member))
            return TypeOfStructCall(st, member, call.Args);
        var cls = ParseUserCallee(call.Callee, out string method);
        if (CFail.Failed) return CType.Void;
        var fact = _facts.Method(cls, method);
        // A base call (IlCall "Base.M" with self first) is a non-virtual direct call
        if (!fact.IsStatic && call.Args.Count >= 1)
        {
            var selfVar = TypeOf(call.Args[0]);
            if (selfVar.Kind != CTypeKind.Ref
                || !IsAncestorOrSame(fact.ClassName, selfVar.Name!))
            {
                CFail.Report("invalid receiver for " + cls + "." + method);
                return CType.Void;
            }
            var rest = new List<IlExpr>();
            for (int i = 1; i < call.Args.Count; i++) rest.Add(call.Args[i]);
            return ValidateMethodCall(fact, selfVar, rest);
        }
        return ValidateMethodCall(fact, null, call.Args);
    }

    // digest consumes f32 bits directly, so strip IL's f32 stringification (__tcs_fstr) and
    // look at the original f32. A normal print could keep the formatted string, but C-side
    // f32 printing is shortest round-trip anyway, so strip it and print directly (same result)
    private static IlExpr UnwrapFstr(IlExpr expr)
    {
        if (expr is IlCall fstr && fstr.Callee == "__tcs_fstr" && fstr.Args.Count == 1) return fstr.Args[0];
        return expr;
    }

    private string RenderPrint(IlCall call)
    {
        if (call.Args.Count == 0) return "tcs_print_newline()";
        var printed = UnwrapFstr(call.Args[0]);
        var argument = TypeOf(printed);
        string helper;
        if (_digestF32) helper = "tcs_digest_float";
        else if (argument.Kind == CTypeKind.I32) helper = "tcs_print_i32";
        else if (argument.Kind == CTypeKind.F32) helper = "tcs_print_f32";
        else if (argument.Kind == CTypeKind.Bool) helper = "tcs_print_bool";
        else if (argument.Kind == CTypeKind.String) helper = "tcs_print_string";
        else
        {
            CFail.Report("print does not support " + argument.Display());
            return "";
        }
        var values = new List<TypedValue>();
        values.Add(new TypedValue(argument, RenderExpr(printed)));
        return RenderOrderedCall(helper, CType.Void, values);
    }

    private CType RenderToStringType(IlExpr argument)
    {
        var type = TypeOf(argument);
        if (type.Kind != CTypeKind.I32 && type.Kind != CTypeKind.F32
            && type.Kind != CTypeKind.Bool && type.Kind != CTypeKind.String)
        {
            CFail.Report("tostring does not support " + type.Display());
            return CType.Void;
        }
        return CType.String;
    }

    private string RenderToString(IlExpr argument)
    {
        var type = TypeOf(argument);
        string helper;
        if (type.Kind == CTypeKind.I32) helper = "tcs_string_i32";
        else if (type.Kind == CTypeKind.F32) helper = "tcs_string_f32";
        else if (type.Kind == CTypeKind.Bool) helper = "tcs_string_bool";
        else if (type.Kind == CTypeKind.String) helper = "tcs_string_tostring";
        else
        {
            CFail.Report("tostring does not support " + type.Display());
            return "";
        }
        var values = new List<TypedValue>();
        values.Add(new TypedValue(type, RenderExpr(argument)));
        return RenderOrderedCall(helper, CType.String, values);
    }

    private CType RequireListForAdd(IlExpr receiver, CType? valueType)
    {
        var listType = TypeOf(receiver);
        if (listType.Kind != CTypeKind.List)
        {
            CFail.Report("List.Add receiver is not a List");
            return CType.List(CType.Void);
        }
        if (listType.Element == null)
        {
            if (valueType == null)
            {
                CFail.Report("cannot infer List element type for closure Add");
                return CType.List(CType.Void);
            }
            listType = CType.List(valueType);
            if (receiver is IlVar variable) Resolve(variable.Name).Type = listType;
        }
        if (valueType != null)
            RequireAssignable(listType.Element!, valueType, "List.Add argument");
        return listType;
    }

    private string RenderListAdd(IlCall call)
    {
        // Closure arguments are rendered typed with the element type as the target (TypeOf cannot type them)
        CType? argType = null;
        if (!IsClosureValue(call.Args[1])) argType = TypeOf(call.Args[1]);
        var listType = RequireListForAdd(call.Args[0], argType);
        var list = Temp("list");
        var value = Temp("list_value");
        return "({ TcsList *" + list + " = " + RenderExpr(call.Args[0]) + "; " +
            listType.ElementCName + " " + value + " = " +
            RenderCoerced(call.Args[1], listType.Element!) + "; " +
            "tcs_list_add(" + list + ", &" + value + ", sizeof(" + value + "), " +
            LayoutRef(listType.Element!) + "); })";
    }

    private string RenderUserCall(IlCall call)
    {
        if (TryStructCallee(call.Callee, out var st, out var member))
            return RenderStructCall(st, member, call.Args);
        var cls = ParseUserCallee(call.Callee, out string method);
        if (CFail.Failed) return "";
        var fact = _facts.Method(cls, method);
        if (!fact.IsStatic && call.Args.Count >= 1)
        {
            // base call: a direct call that bypasses the dispatcher
            var rest = new List<IlExpr>();
            for (int i = 1; i < call.Args.Count; i++) rest.Add(call.Args[i]);
            return RenderMethodCall(fact, call.Args[0], rest);
        }
        ValidateMethodCall(fact, null, call.Args);
        return RenderMethodCall(fact, null, call.Args);
    }

    // MathF.X(...) (host BCL path) is treated the same as the Math.X intrinsic
    private IlCall? AsMathCall(IlDynCall call)
    {
        if (call.Callee is IlField field && field.Recv is IlVar recv
            && (recv.Name == "MathF" || recv.Name == "Math")
            && !_classes.ContainsKey(recv.Name) && TryResolve(recv.Name) == null)
            return new IlCall("Math." + (field.Name == "Ceiling" ? "Ceil" : field.Name), call.Args, null);
        return null;
    }

    private CType TypeOfDynCall(IlDynCall call)
    {
        var foreign = ForeignCallee(call.Callee);
        if (foreign != null) return TypeOfForeignCall(foreign, call.Args);
        var mathCall = AsMathCall(call);
        if (mathCall != null) return TypeOfIntrinsic(mathCall);
        var closureType = TryTypeOfClosureCallee(call);
        if (closureType != null)
        {
            RequireArity("closure call", call.Args.Count,
                closureType.Parameters!.Count);
            for (var i = 0; i < call.Args.Count && i < closureType.Parameters.Count; i++)
                CheckAssignable(closureType.Parameters[i],
                    call.Args[i], "closure argument " + i);
            return closureType.Element!;
        }
        var fact = ParseDynCallee(call.Callee);
        return ValidateMethodCall(fact, null, call.Args);
    }

    // Returns the closure type if the callee is a closure value (variable/field etc.)
    private CType? TryTypeOfClosureCallee(IlDynCall call)
    {
        if (call.Callee is IlField member && member.Recv is IlVar receiver
            && IsStaticMemberOwner(receiver.Name) && TryResolve(receiver.Name) == null)
        {
            // Type-qualified: a closure call if it is a static field of closure type, otherwise
            // a static method call
            if (TryStaticField(member, out string staticName, out CType staticType)
                && staticType.Kind == CTypeKind.Closure)
                return staticType;
            return null;
        }
        var type = TypeOf(call.Callee);
        return type.Kind == CTypeKind.Closure ? type : (CType?)null;
    }

    private string RenderDynCall(IlDynCall call)
    {
        var foreign = ForeignCallee(call.Callee);
        if (foreign != null) return RenderForeignCall(foreign, call.Args, null, true);
        var mathCall = AsMathCall(call);
        if (mathCall != null) return RenderIntrinsic(mathCall);
        var closureType = TryTypeOfClosureCallee(call);
        if (closureType != null)
        {
            TypeOfDynCall(call);
            if (CFail.Failed) return "";
            var values = new List<TypedValue>();
            values.Add(new TypedValue(closureType, "(TcsClosure *)" + RenderExpr(call.Callee)));
            for (var i = 0; i < call.Args.Count; i++)
                values.Add(new TypedValue(closureType.Parameters![i],
                    RenderCoerced(call.Args[i], closureType.Parameters[i])));
            var closTemp = Temp("call_closure");
            var argNames = new List<string>();
            var declarations = new List<string>();
            for (var i = 0; i < values.Count; i++)
            {
                var temp = i == 0 ? closTemp : Temp("closure_arg");
                declarations.Add(
                    values[i].Type.CName + " " + temp + " = " + values[i].Value + "; ");
                if (i > 0) argNames.Add(temp);
            }
            var callParts = new List<string>();
            callParts.Add(closTemp + "->cells");
            foreach (var argName in argNames) callParts.Add(argName);
            var callArgs = string.Join(", ", callParts);
            // A null delegate is checked after the arguments are evaluated and before the call (C# order)
            return "({ " + string.Join("", declarations) + "tcs_nonnull(" + closTemp + "); " +
                "((" + ClosureFnPtrType(closureType, "") + ")" + closTemp + "->fn)(" + callArgs + "); })";
        }
        var fact = ParseDynCallee(call.Callee);
        ValidateMethodCall(fact, null, call.Args);
        return RenderMethodCall(fact, null, call.Args);
    }

    private MethodFact ParseDynCallee(IlExpr callee)
    {
        if (callee is IlField field && field.Recv is IlVar receiver
            && IsStaticMemberOwner(receiver.Name))
        {
            var fact = _facts.Method(receiver.Name, field.Name);
            if (!fact.IsStatic)
                CFail.Report("dynamic call target is not static: " +
                    receiver.Name + "." + field.Name);
            return fact;
        }
        CFail.Report("only type-qualified static IlDynCall is supported");
        return new MethodFact("", "", true, CType.Void, new List<ParameterFact>(), new ExMethod());
    }

    private CType ValidateMethodCall(MethodFact fact, CType? receiver,
        List<IlExpr> args)
    {
        if (receiver == null && !fact.IsStatic)
        {
            CFail.Report("call target is not static: " + fact.ClassName + "." + fact.Name);
            return CType.Void;
        }
        if (receiver != null && fact.IsStatic)
        {
            CFail.Report("IlInvoke target is static: " + fact.ClassName + "." + fact.Name);
            return CType.Void;
        }
        var complete = CompleteArguments(fact.Parameters, args, fact.ClassName + "." + fact.Name);
        for (var i = 0; i < complete.Count && i < fact.Parameters.Count; i++)
            CheckAssignable(fact.Parameters[i].Type, complete[i],
                "argument " + i + " of " + fact.ClassName + "." + fact.Name);
        return fact.ReturnType;
    }

    private string RenderMethodCall(MethodFact fact, IlExpr? receiver,
        List<IlExpr> args, string? function = null)
    {
        var complete = CompleteArguments(fact.Parameters, args, fact.ClassName + "." + fact.Name);
        var outArguments = TakeOutArguments();
        var values = new List<TypedValue>();
        if (receiver != null)
        {
            var receiverType = CType.Ref(fact.ClassName);
            values.Add(new TypedValue(receiverType,
                "(" + receiverType.CName + ")" + RenderExpr(receiver)));
        }
        for (var i = 0; i < complete.Count && i < fact.Parameters.Count; i++)
            values.Add(new TypedValue(fact.Parameters[i].Type,
                RenderCoerced(complete[i], fact.Parameters[i].Type)));
        string target;
        if (function != null) target = function;
        else target = Names.Method(fact.ClassName, fact.Name);
        return RenderOrderedCall(target, fact.ReturnType, values, outArguments, receiver != null);
    }

    // Evaluates the values into temps in order, then calls.
    // With checkReceiver, the first value (the receiver) is checked for null at the call, after all arguments are evaluated (C# order)
    private string RenderOrderedCall(string function, CType returnType,
        List<TypedValue> values,
        List<string>? outArguments = null, bool checkReceiver = false)
    {
        if (values.Count == 0 && (outArguments == null || outArguments.Count == 0)) return function + "()";
        var declarations = new List<string>();
        var arguments = new List<string>();
        foreach (var item in values)
        {
            var temp = Temp("arg");
            declarations.Add(item.Type.CName);
            declarations.Add(" ");
            declarations.Add(temp);
            declarations.Add(" = ");
            declarations.Add(item.Value);
            declarations.Add("; ");
            if (checkReceiver && arguments.Count == 0) arguments.Add("(" + item.Type.CName + ")tcs_nonnull(" + temp + ")");
            else arguments.Add(temp);
        }
        if (outArguments != null)
        {
            foreach (var outArgument in outArguments) arguments.Add(outArgument);
        }
        return "({ " + string.Join("", declarations) + function + "(" + string.Join(", ", arguments) + "); })";
    }

    // A (Class, Method) pair: returns Class, with Method as out
    private string ParseUserCallee(string callee, out string method)
    {
        method = "";
        var u = new Utf8Bytes(callee);
        int dot = -1;
        int lastDot = -1;
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] == '.')
            {
                if (dot < 0) dot = i;
                lastDot = i;
            }
        }
        if (dot <= 0 || dot != lastDot || dot == u.Length - 1)
        {
            CFail.Report("unsupported intrinsic or callee: " + callee);
            return "";
        }
        var cls = ContractFacts.Sub(callee, 0, dot);
        method = ContractFacts.Sub(callee, dot + 1, u.Length);
        if (!_classes.ContainsKey(cls))
        {
            CFail.Report("unknown call target class: " + callee);
            method = "";
            return "";
        }
        return cls;
    }

    // ==== CEmitter.Statements.cs ====

    // Emitting control flow statements (if / while / repeat / numeric for / foreach).

    private void EmitIf(IlIf conditional)
    {
        for (var i = 0; i < conditional.Arms.Count; i++)
        {
            var condition = conditional.Arms[i].Cond;
            var body = conditional.Arms[i].Body;
            RequireType(CType.Bool, TypeOf(condition), "if condition");
            Line((i == 0 ? "if" : "else if") + " (" + RenderExpr(condition) + ") {");
            _indent++;
            PushScope();
            EmitStats(body.Stats);
            PopScope();
            _indent--;
            Line("}");
        }
        if (conditional.Else != null)
        {
            Line("else {");
            _indent++;
            PushScope();
            EmitStats(conditional.Else.Stats);
            PopScope();
            _indent--;
            Line("}");
        }
    }

    private void EmitNumericFor(IlNumericFor loop)
    {
        RequireType(CType.I32, TypeOf(loop.Start), "numeric for start");
        RequireType(CType.I32, TypeOf(loop.Limit), "numeric for limit");
        var start = Temp("for_start");
        var limit = Temp("for_limit");
        var variableName = "v_" + Names.Id(loop.Var) + "_" + _serial;
        _serial++;
        var variable = new Variable(variableName, CType.I32);
        Line("{");
        _indent++;
        Line("int32_t " + start + " = " + RenderExpr(loop.Start) + ";");
        Line("int32_t " + limit + " = " + RenderExpr(loop.Limit) + ";");
        Line("for (int32_t " + variable.CName + " = " + start + "; " + variable.CName + (loop.Inclusive ? " <= " : " < ") + limit + "; " +
            variable.CName + " = " + variable.CName + " + INT32_C(1)) {");
        _indent++;
        PushScope();
        AddVariable(loop.Var, variable);
        _continueTargets.Add(null);
        _breakTargets.Add(null);
        EmitStats(loop.Body.Stats);
        _continueTargets.RemoveAt(_continueTargets.Count - 1);
        _breakTargets.RemoveAt(_breakTargets.Count - 1);
        PopScope();
        _indent--;
        Line("}");
        _indent--;
        Line("}");
    }

    private void EmitForeachList(IlForeachList loop)
    {
        var sequenceType = TypeOf(loop.Coll);
        if (sequenceType.Kind != CTypeKind.Array && sequenceType.Kind != CTypeKind.List
            || sequenceType.Element == null)
        {
            CFail.Report("IlForeachList requires a typed array/List, got " +
                sequenceType.Display());
            return;
        }
        var sequence = Temp("foreach_sequence");
        var length = Temp("foreach_length");
        var index = Temp("foreach_index");
        var lengthFunction = sequenceType.Kind == CTypeKind.Array
            ? "tcs_array_length" : "tcs_list_length";
        var atFunction = sequenceType.Kind == CTypeKind.Array
            ? "tcs_array_at" : "tcs_list_at";

        Line("{");
        _indent++;
        Line(sequenceType.CName + " " + sequence + " = " + RenderExpr(loop.Coll) + ";");
        Line("int32_t " + length + " = " + lengthFunction + "(" + sequence + ");");
        Line("for (int32_t " + index + " = 0; " + index + " < " + length + "; " + index + "++) {");
        _indent++;
        PushScope();
        _continueTargets.Add(null);
        _breakTargets.Add(null);
        DeclareLocal(loop.Var, sequenceType.Element!,
            "*(" + sequenceType.ElementCName + " *)" + atFunction + "(" + sequence + ", " + index + ")");
        EmitStats(loop.Body.Stats);
        _continueTargets.RemoveAt(_continueTargets.Count - 1);
        _breakTargets.RemoveAt(_breakTargets.Count - 1);
        PopScope();
        _indent--;
        Line("}");
        _indent--;
        Line("}");
    }

    // string.EnumerateRunes(): binds each codepoint in order
    private void EmitForeachRunes(IlForeachRunes loop)
    {
        RequireType(CType.String, TypeOf(loop.Str), "EnumerateRunes receiver");
        var text = Temp("runes");
        var position = Temp("rune_pos");
        Line("{");
        _indent++;
        Line("TcsString *" + text + " = (TcsString *)tcs_nonnull(" + RenderExpr(loop.Str) + ");");
        Line("size_t " + position + " = 0;");
        Line("while (" + position + " < " + text + "->length) {");
        _indent++;
        PushScope();
        _continueTargets.Add(null);
        _breakTargets.Add(null);
        DeclareLocal(loop.Var, CType.I32, "tcs_utf8_next(" + text + ", &" + position + ")");
        EmitStats(loop.Body.Stats);
        _continueTargets.RemoveAt(_continueTargets.Count - 1);
        _breakTargets.RemoveAt(_breakTargets.Count - 1);
        PopScope();
        _indent--;
        Line("}");
        _indent--;
        Line("}");
    }

    // Recognize the IL shape of List.Clear / Dictionary.Clear (IIFE: local t = recv; IlForPairs
    // sets every key to null) and turn it into a runtime call
    private bool TryEmitClearIife(IlIife iife)
    {
        if (iife.Stats.Count != 2) return false;
        if (!(iife.Stats[0] is IlLocal local) || local.Init == null) return false;
        var tmp = local.Name;
        var recv = local.Init;
        if (!(iife.Stats[1] is IlForPairs pairs) || pairs.VVar != null) return false;
        if (!(pairs.Coll is IlVar coll)) return false;
        if (pairs.Body.Stats.Count != 1) return false;
        if (!(pairs.Body.Stats[0] is IlAssign assign)) return false;
        if (!(assign.Target is IlIndex targetIndex) || targetIndex.PlusOne) return false;
        if (!(targetIndex.Recv is IlVar target)) return false;
        if (!IsNilLiteral(assign.Value)) return false;
        if (coll.Name != tmp || target.Name != tmp)
            return false;
        var type = TypeOf(recv);
        string helper;
        if (type.Kind == CTypeKind.List) helper = "tcs_list_clear";
        else if (type.Kind == CTypeKind.Dict) helper = "tcs_dict_clear";
        else
        {
            CFail.Report("Clear receiver is not a List/Dictionary: " + type.Display());
            return true;
        }
        Line(helper + "(" + RenderExpr(recv) + ");");
        return true;
    }

    private void EmitWhile(IlWhile loop)
    {
        RequireType(CType.Bool, TypeOf(loop.Cond), "while condition");
        string? label = null;
        if (loop.Trailer != null) label = Temp("continue");
        Line("while (" + RenderExpr(loop.Cond) + ") {");
        _indent++;
        _continueTargets.Add(label);
        _breakTargets.Add(null);
        PushScope();
        if (label != null) Line("{");
        if (label != null) _indent++;
        EmitStats(loop.Body.Stats);
        if (label != null) _indent--;
        if (label != null) Line("}");
        PopScope();
        if (label != null)
        {
            Line(label + ":");
            PushScope();
            EmitStats(loop.Trailer!.Stats);
            PopScope();
            Line(";");
        }
        _continueTargets.RemoveAt(_continueTargets.Count - 1);
        _breakTargets.RemoveAt(_breakTargets.Count - 1);
        _indent--;
        Line("}");
    }

    private void EmitRepeat(IlRepeat repeat)
    {
        RequireType(CType.Bool, TypeOf(repeat.Cond), "repeat condition");
        Line("do {");
        _indent++;
        _continueTargets.Add(null);
        _breakTargets.Add(null);
        PushScope();
        EmitStats(repeat.Body.Stats);
        PopScope();
        _continueTargets.RemoveAt(_continueTargets.Count - 1);
        _breakTargets.RemoveAt(_breakTargets.Count - 1);
        _indent--;
        Line("} while (" + RenderExpr(repeat.Cond) + ");");
    }

    private void EmitContinue()
    {
        if (_continueTargets.Count == 0)
        {
            CFail.Report("continue outside a loop");
            return;
        }
        var target = _continueTargets[_continueTargets.Count - 1];
        Line(target == null ? "continue;" : "goto " + target + ";");
    }

    // foreach over a Dictionary. Iteration order is insertion order (support-matrix §18).
    // It is a nested for over buckets and nodes, so break exits with a goto to after the outer for
    private void EmitForeachDict(IlForeachDict loop)
    {
        var valueType = RequireDict(loop.Coll, out CType dictType);
        var dictTemp = Temp("dict");
        var bucketTemp = Temp("bucket");
        var nodeTemp = Temp("node");
        var breakLabel = Temp("dict_break");
        Line("{");
        _indent++;
        PushScope();
        Line("TcsDict *" + dictTemp + " = ({ TcsDict *d = " +
            RenderExpr(loop.Coll) + "; (TcsDict *)tcs_nonnull(d); });");
        // Insertion order (support-matrix §18): walk order from the front. Holes from removed nodes are skipped, and adding a key midway faults
        var versionTemp = Temp("version");
        Line("uint32_t " + versionTemp + " = " + dictTemp + "->version;");
        Line("for (size_t " + bucketTemp + " = 0; " + bucketTemp + " < " + dictTemp + "->order_len; " + bucketTemp + "++)");
        Line("{");
        _indent++;
        Line("if (" + dictTemp + "->version != " + versionTemp + ") tcs_fault(\"collection-modified\");");
        Line("TcsDictNode *" + nodeTemp + " = " + dictTemp + "->order[" + bucketTemp + "];");
        Line("if (" + nodeTemp + " == NULL) continue;");
        PushScope();
        var pairType = CType.Kvp(dictType.KeyType!, valueType);
        if (_capturedNames.Contains(loop.Var))
        {
            // A KeyValuePair captured by a closure is a value: copy the node so that changes to the Dictionary after the iteration
            // are not reflected
            var copyName = "c_" + Names.Id(loop.Var) + "_" + _serial;
            _serial++;
            var copy = new Variable(copyName, pairType);
            copy.Snapshot = true;
            Line("TcsDictNode *" + copy.CName + " = tcs_dict_node_copy(" + dictTemp + ", " + nodeTemp + ");");
            AddVariable(loop.Var, copy);
        }
        else AddVariable(loop.Var, new Variable(nodeTemp, pairType));
        _continueTargets.Add(null);
        _breakTargets.Add(breakLabel);
        EmitStats(loop.Body.Stats);
        _continueTargets.RemoveAt(_continueTargets.Count - 1);
        _breakTargets.RemoveAt(_breakTargets.Count - 1);
        PopScope();
        _indent--;
        Line("}");
        if (_usedBreakLabels.Contains(breakLabel)) Line(breakLabel + ": ;");
        PopScope();
        _indent--;
        Line("}");
    }

    // IlBreakScope: put a label right after the block, and a scope-bound break exits with a goto
    // (with do { } while (0), continue would not reach the outer loop)
    private void EmitBreakScope(IlBreakScope scope)
    {
        var label = "tcs_brk_" + _serial;
        _serial++;
        _breakTargets.Add(label);
        Line("{");
        _indent++;
        PushScope();
        EmitStats(scope.Body.Stats);
        PopScope();
        _indent--;
        Line("}");
        Line(label + ": ;");
        _breakTargets.RemoveAt(_breakTargets.Count - 1);
    }

    // ---- Lists ----

    // ==== CEmitter.Lists.cs ====
    // Expand List.* (the List surface of TinySystem) into an inline loop per element type.
    // Closures are typed with the caller's element type (IlClosure has no parameter types), and the return type is
    // inferred from the body. Sort is the runtime introsort plus a lifted comparison function per call site.
    // LINQ is not here, since TinySystem/Linq.cs is compiled as user code

    private int _comparatorSerial;

    private static bool IsNilLiteral(IlExpr expr) => IlLiterals.IsNull(expr);

    // The return value is the length function (Len), and at receives the element function (At)
    private static string SeqFns(CType seq, out string at)
    {
        if (seq.Kind == CTypeKind.Array)
        {
            at = "tcs_array_at";
            return "tcs_array_length";
        }
        at = "tcs_list_at";
        return "tcs_list_length";
    }

    private CType RequireSeq(IlExpr expr, string where)
    {
        var type = TypeOf(expr);
        if (type.Kind != CTypeKind.List && type.Kind != CTypeKind.Array || type.Element == null)
        {
            CFail.Report($"{where}: receiver is not a typed List/array ({type.Display()})");
            return CType.List(CType.Void);
        }
        return type;
    }

    // Return type of a closure value: from the body for an IlClosure, for a static method group
    // from the contract, and for a variable of closure type from that type
    private CType InferClosureResult(IlExpr fn, List<CType> paramTypes)
    {
        switch (fn)
        {
            case IlClosure closure:
                {
                    if (closure.Params.Count != paramTypes.Count)
                    {
                        CFail.Report("closure arity mismatch");
                        return CType.Void;
                    }
                    PushScope();
                    for (var i = 0; i < paramTypes.Count; i++)
                        AddVariable(closure.Params[i], new Variable("__infer", paramTypes[i]));
                    CType? result;
                    if (closure.ExprBody != null) result = TypeOf(closure.ExprBody);
                    else result = ScanReturnType(closure.Body!.Stats);
                    PopScope();
                    if (result == null) return CType.Void;
                    return result;
                }
            case IlMethodGroup group:
                return _facts.Method(group.TypeRef, group.Method).ReturnType;
            default:
                {
                    var type = TypeOf(fn);
                    if (type.Kind != CTypeKind.Closure)
                    {
                        CFail.Report($"expected a closure, got {type.Display()}");
                        return CType.Void;
                    }
                    return type.Element!;
                }
        }
    }

    // Type of the first return in a block body (locals only register their types and are not emitted)
    private CType? ScanReturnType(List<IlStat> stats)
    {
        foreach (var stat in stats)
        {
            switch (stat)
            {
                case IlLocal local:
                    {
                        CType? type = null;
                        if (local.Type != null) type = _facts.TryMapType(local.Type);
                        if (type == null)
                        {
                            if (local.Init == null)
                            {
                                CFail.Report($"cannot type local {local.Name}");
                                return null;
                            }
                            type = TypeOf(local.Init);
                        }
                        AddVariable(local.Name, new Variable("__infer", type));
                        break;
                    }
                case IlReturn ret:
                    if (ret.Value != null) return TypeOf(ret.Value);
                    break;
                case IlMultiAssign multi:
                    if (multi.Declare && multi.Values.Count == 1 && multi.Targets.Count == 2
                        && multi.Values[0] is IlCall tryGet
                        && multi.Targets[0] is IlVar foundVar && multi.Targets[1] is IlVar valueVar)
                    {
                        AddVariable(foundVar.Name, new Variable("__infer", CType.Bool));
                        CType? parsed = TryParseValueType(tryGet.Callee);
                        if (parsed == null) parsed = RequireDict(tryGet.Args[0], out _);
                        AddVariable(valueVar.Name, new Variable("__infer", parsed));
                    }
                    break;
                case IlIf conditional:
                    {
                        foreach (var arm in conditional.Arms)
                        {
                            var armType = ScopedReturnType(arm.Body.Stats);
                            if (armType != null) return armType;
                        }
                        if (conditional.Else != null)
                        {
                            var elseType = ScopedReturnType(conditional.Else.Stats);
                            if (elseType != null) return elseType;
                        }
                        break;
                    }
                case IlWhile whileLoop:
                    {
                        var w = ScopedReturnType(whileLoop.Body.Stats);
                        if (w != null) return w;
                        break;
                    }
                case IlRepeat repeat:
                    {
                        var r = ScopedReturnType(repeat.Body.Stats);
                        if (r != null) return r;
                        break;
                    }
                case IlDo block:
                    {
                        var d = ScopedReturnType(block.Body.Stats);
                        if (d != null) return d;
                        break;
                    }
                case IlBreakScope scope:
                    {
                        var bs = ScopedReturnType(scope.Body.Stats);
                        if (bs != null) return bs;
                        break;
                    }
                case IlNumericFor forLoop:
                    {
                        PushScope();
                        AddVariable(forLoop.Var, new Variable("__infer", CType.I32));
                        var t = ScanReturnType(forLoop.Body.Stats);
                        PopScope();
                        if (t != null) return t;
                        break;
                    }
                case IlForeachList foreachLoop:
                    {
                        PushScope();
                        AddVariable(foreachLoop.Var, new Variable("__infer",
                            RequireSeq(foreachLoop.Coll, "foreach").Element!));
                        var t = ScanReturnType(foreachLoop.Body.Stats);
                        PopScope();
                        if (t != null) return t;
                        break;
                    }
            }
        }
        return null;
    }

    // Local function Scoped of ScanReturnType (scans the body in a new scope)
    private CType? ScopedReturnType(List<IlStat> body)
    {
        PushScope();
        var t = ScanReturnType(body);
        PopScope();
        return t;
    }

    // Sequential execution in expression position (IlIife) is a GNU statement expression. An inner return
    // is an assignment to the result variable + a goto to the trailing label (it does not jump out of the expression)
    // (_iifes is a core List<IifeFrame>; the last element is the top)

    private CType TypeOfIife(IlIife iife)
    {
        PushScope();
        var type = ScanReturnType(iife.Stats);
        PopScope();
        if (type == null) return CType.Void;
        return type;
    }

    private string RenderIife(IlIife iife)
    {
        var type = TypeOfIife(iife);
        var result = Temp("iife");
        var label = Temp("iife_done");
        var start = _output.Count;
        var savedIndent = _indent;
        _indent = 1;
        PushScope();
        _iifes.Add(new IifeFrame(result, label, type));
        EmitStats(iife.Stats);
        _iifes.RemoveAt(_iifes.Count - 1);
        PopScope();
        var bodyParts = new List<string>();
        for (var k = start; k < _output.Count; k++) bodyParts.Add(_output[k]);
        var body = string.Join("", bodyParts);
        while (_output.Count > start) _output.RemoveAt(_output.Count - 1);
        _indent = savedIndent;
        var isVoid = CType.Same(type, CType.Void);
        var declaration = isVoid
            ? "" : $"{type.CName} {result} = {ZeroInit(type)}; ";
        var value = isVoid ? "" : $"{result}; ";
        return $"({{ {declaration}\n{body}{label}: ;\n{value}}})";
    }

    private string CallClosure(string closure, CType closureType, List<string> args)
    {
        var all = new List<string>();
        all.Add($"{closure}->cells");
        foreach (var arg in args) all.Add(arg);
        var fnPtrType = ClosureFnPtrType(closureType, "");
        return $"(({fnPtrType}){closure}->fn)(" +
            string.Join(", ", all) + ")";
    }

    // Equality for List searches (EqualityComparer<T>.Default: for float, NaN equals NaN)
    private string EqualExpr(CType type, string left, string right)
    {
        var kind = type.Kind;
        if (kind == CTypeKind.I32 || kind == CTypeKind.Bool) return $"({left} == {right})";
        if (kind == CTypeKind.F32) return $"tcs_float_equals({left}, {right})";
        if (kind == CTypeKind.String) return $"tcs_string_equal({left}, {right})";
        if (kind == CTypeKind.Ref && IsRecordClass(type.Name!))
            return $"{Names.RecordEq(type.Name!)}({left}, {right})";
        if (kind == CTypeKind.Ref || kind == CTypeKind.Array || kind == CTypeKind.List || kind == CTypeKind.Queue
            || kind == CTypeKind.HashSet || kind == CTypeKind.Dict || kind == CTypeKind.Closure
            || kind == CTypeKind.Random)
            return $"({left} == {right})";
        if (kind == CTypeKind.Nullable) return NullableEqualExpr(type, left, right, true);
        // Struct values compare memberwise (C# ValueType.Equals / record struct ==)
        if (kind == CTypeKind.StructVal)
            return $"({{ {type.CName} eq_l = {left}; {type.CName} eq_r = {right}; " +
                $"{Names.StructEq(type.Name!)}(&eq_l, &eq_r); }})";
        CFail.Report($"equality is not supported for {type.Display()}");
        return "";
    }

    // Three-way comparison for sort. For float, NaN is the smallest, as in Single.CompareTo
    private static string CompareExpr(CType type, string left, string right)
    {
        if (type.Kind == CTypeKind.F32)
            return $"({left} < {right} ? -1 : {left} > {right} ? 1 : {left} == {right} ? 0 : " +
                $"{left} != {left} ? ({right} != {right} ? 0 : -1) : 1)";
        return $"({LessExpr(type, left, right)} ? -1 : {LessExpr(type, right, left)} ? 1 : 0)";
    }

    private static string LessExpr(CType type, string left, string right)
    {
        if (type.Kind == CTypeKind.I32 || type.Kind == CTypeKind.F32) return $"({left} < {right})";
        // null is the smallest (the .NET default comparer)
        if (type.Kind == CTypeKind.String) return $"(tcs_string_compare_ordinal({left}, {right}) < 0)";
        CFail.Report($"ordering is not supported for {type.Display()}");
        return "";
    }

    private static string ZeroInit(CType type)
    {
        if (type.Kind == CTypeKind.I32 || type.Kind == CTypeKind.F32 || type.Kind == CTypeKind.Bool) return "0";
        if (type.Kind == CTypeKind.StructVal || type.Kind == CTypeKind.Nullable) return $"({type.CName}){{0}}";
        return "NULL";
    }

    private CType TypeOfHashSetOp(IlCall call)
    {
        var name = Strings.From(call.Callee, ContractFacts.Len("TcsHashSet."));
        var set = TypeOf(call.Args[0]);
        if (set.Kind != CTypeKind.HashSet)
        {
            CFail.Report($"{call.Callee}: receiver is not a HashSet ({set.Display()})");
            return CType.Void;
        }
        RequireArity(call.Callee, call.Args.Count, name == "Add" || name == "Remove" || name == "Contains" ? 2 : 1);
        if (CFail.Failed) return CType.Void;
        if (call.Args.Count == 2) CheckAssignable(set.Element!, call.Args[1], call.Callee);
        switch (name)
        {
            case "Add":
            case "Remove":
            case "Contains":
                return CType.Bool;
            case "Clear":
                return CType.Void;
            case "Count":
                return CType.I32;
            case "ToList":
                return CType.List(set.Element);
        }
        CFail.Report($"unsupported HashSet member: {name}");
        return CType.Void;
    }

    private string RenderHashSetOp(IlCall call)
    {
        var name = Strings.From(call.Callee, ContractFacts.Len("TcsHashSet."));
        TypeOfHashSetOp(call);
        if (CFail.Failed) return "";
        var elem = TypeOf(call.Args[0]).Element!;
        var recv = RenderExpr(call.Args[0]);
        switch (name)
        {
            case "Clear": return $"tcs_hashset_clear({recv})";
            case "Count": return $"tcs_hashset_count({recv})";
            case "ToList": return $"tcs_hashset_to_list({recv})";
        }
        var s = Temp("set");
        var v = Temp("set_value");
        var head = $"TcsHashSet *{s} = {recv}; {elem.CName} {v} = {RenderCoerced(call.Args[1], elem)}; ";
        var key = DictKeyArgs(elem, v);
        switch (name)
        {
            case "Add":
                return $"({{ {head}tcs_hashset_add({s}, {key}, &{v}, {LayoutRef(elem)}); }})";
            case "Remove":
                return $"({{ {head}tcs_hashset_remove({s}, {key}); }})";
            case "Contains":
                return $"({{ {head}tcs_hashset_find({s}, {key}) >= 0; }})";
        }
        CFail.Report($"unsupported HashSet member: {name}");
        return "";
    }

    private CType TypeOfQueueOp(IlCall call)
    {
        var name = Strings.From(call.Callee, ContractFacts.Len("TcsQueue."));
        var queue = TypeOf(call.Args[0]);
        if (queue.Kind != CTypeKind.Queue)
        {
            CFail.Report($"{call.Callee}: receiver is not a Queue ({queue.Display()})");
            return CType.Void;
        }
        var elem = queue.Element!;
        switch (name)
        {
            case "Enqueue":
                RequireArity(call.Callee, call.Args.Count, 2);
                if (CFail.Failed) return CType.Void;
                if (!IsClosureValue(call.Args[1])) CheckAssignable(elem, call.Args[1], call.Callee);
                return CType.Void;
            case "Dequeue":
            case "Peek":
                RequireArity(call.Callee, call.Args.Count, 1);
                return elem;
            case "Clear":
                RequireArity(call.Callee, call.Args.Count, 1);
                return CType.Void;
            case "Count":
                RequireArity(call.Callee, call.Args.Count, 1);
                return CType.I32;
            // For struct elements, IL attaches a reference to op_Equality as the third argument
            case "Contains":
                RequireArity(call.Callee, call.Args.Count, elem.Kind == CTypeKind.StructVal ? 3 : 2);
                if (CFail.Failed) return CType.Void;
                CheckAssignable(elem, call.Args[1], call.Callee);
                return CType.Bool;
            case "ToList":
                RequireArity(call.Callee, call.Args.Count, 1);
                return CType.List(elem);
            default:
                CFail.Report($"unsupported Queue member: {name}");
                return CType.Void;
        }
    }

    private string RenderQueueOp(IlCall call)
    {
        var name = Strings.From(call.Callee, ContractFacts.Len("TcsQueue."));
        var type = TypeOfQueueOp(call);
        if (CFail.Failed) return "";
        var elem = TypeOf(call.Args[0]).Element!;
        var q = Temp("queue");
        var head = $"TcsQueue *{q} = {RenderExpr(call.Args[0])}; ";
        switch (name)
        {
            case "Enqueue":
                {
                    var v = Temp("queue_value");
                    return $"({{ {head}{elem.CName} {v} = {RenderCoerced(call.Args[1], elem)}; " +
                        $"tcs_queue_enqueue({q}, &{v}, sizeof({v}), {LayoutRef(elem)}); }})";
                }
            case "Dequeue":
                {
                    var v = Temp("queue_value");
                    return $"({{ {head}{elem.CName} {v} = *({elem.CName} *)tcs_queue_front({q}); " +
                        $"tcs_queue_drop({q}); {v}; }})";
                }
            case "Peek":
                return $"(*({elem.CName} *)tcs_queue_front({RenderExpr(call.Args[0])}))";
            case "Clear":
                return $"tcs_queue_clear({RenderExpr(call.Args[0])})";
            case "Count":
                return $"tcs_queue_count({RenderExpr(call.Args[0])})";
            case "ToList":
                return $"tcs_queue_to_list({RenderExpr(call.Args[0])})";
            case "Contains":
                {
                    var target = Temp("target");
                    var found = Temp("found");
                    var i = Temp("i");
                    var v = Temp("v");
                    return $"({{ {head}{elem.CName} {target} = {RenderCoerced(call.Args[1], elem)}; " +
                        $"bool {found} = false; tcs_nonnull({q}); " +
                        $"for (size_t {i} = {q}->head; {i} < {q}->items->length; {i}++) {{ " +
                        $"{elem.CName} {v} = *({elem.CName} *)tcs_list_at({q}->items, (int32_t){i}); " +
                        $"if ({EqualExpr(elem, v, target)}) {{ {found} = true; break; }} }} {found}; }})";
                }
            default:
                CFail.Report($"unsupported Queue member: {name} ({type.Display()})");
                return "";
        }
    }

    private CType TypeOfListOp(IlCall call)
    {
        var name = Strings.From(call.Callee, ContractFacts.Len("List."));
        var args = call.Args;
        var seq = RequireSeq(args[0], call.Callee);
        var elem = seq.Element!;
        switch (name)
        {
            // For mutable struct elements, IL attaches a reference to `S.__copy` as the second argument
            // (C copies elements by value, so it is not read)
            case "Copy":
                if (args.Count != 1 && args.Count != 2)
                {
                    CFail.Report("List.Copy: expected 1 argument");
                    return CType.Void;
                }
                return CType.List(elem);
            // For struct elements, IL attaches a reference to op_Equality as the third argument
            // (C generates a memberwise comparison from the element type, so it is not read)
            case "Contains":
                RequireArity(call.Callee, args.Count, elem.Kind == CTypeKind.StructVal ? 3 : 2);
                if (CFail.Failed) return CType.Void;
                CheckAssignable(elem, args[1], call.Callee);
                return CType.Bool;
            case "IndexOf":
                RequireArity(call.Callee, args.Count, elem.Kind == CTypeKind.StructVal ? 3 : 2);
                if (CFail.Failed) return CType.Void;
                CheckAssignable(elem, args[1], call.Callee);
                return CType.I32;
            case "Insert":
                RequireArity(call.Callee, args.Count, 3);
                if (CFail.Failed) return CType.Void;
                if (seq.Kind != CTypeKind.List)
                {
                    CFail.Report("Insert: receiver is not a List");
                    return CType.Void;
                }
                RequireType(CType.I32, TypeOf(args[1]), "Insert index");
                if (!IsClosureValue(args[2])) CheckAssignable(elem, args[2], call.Callee);
                return CType.Void;
            // For mutable struct elements, IL attaches a reference to `S.__copy` at the end
            case "GetRange":
                if (args.Count != 3 && args.Count != 4)
                {
                    CFail.Report("List.GetRange: expected 2 arguments");
                    return CType.Void;
                }
                if (seq.Kind != CTypeKind.List)
                {
                    CFail.Report("GetRange: receiver is not a List");
                    return CType.Void;
                }
                RequireType(CType.I32, TypeOf(args[1]), "GetRange index");
                RequireType(CType.I32, TypeOf(args[2]), "GetRange count");
                return CType.List(elem);
            case "FindIndex":
            case "RemoveAll":
                RequireArity(call.Callee, args.Count, 2);
                if (CFail.Failed) return CType.Void;
                if (name == "RemoveAll" && seq.Kind != CTypeKind.List)
                {
                    CFail.Report("RemoveAll: receiver is not a List");
                    return CType.Void;
                }
                if (!CType.Same(InferClosureResult(args[1], new List<CType> { elem }), CType.Bool))
                {
                    CFail.Report($"{call.Callee}: predicate must return bool");
                    return CType.Void;
                }
                return CType.I32;
            case "Remove":
                RequireArity(call.Callee, args.Count, elem.Kind == CTypeKind.StructVal ? 3 : 2);
                if (CFail.Failed) return CType.Void;
                if (seq.Kind != CTypeKind.List)
                {
                    CFail.Report("Remove: receiver is not a List");
                    return CType.Void;
                }
                CheckAssignable(elem, args[1], call.Callee);
                return CType.Bool;
            case "Sort":
                if (seq.Kind != CTypeKind.List)
                {
                    CFail.Report("Sort: receiver is not a List");
                    return CType.Void;
                }
                if (args.Count == 2)
                    RequireType(CType.I32, InferClosureResult(args[1], new List<CType> { elem, elem }), "Sort comparison");
                else
                    LessExpr(elem, "a", "b");
                return CType.Void;
            default:
                CFail.Report($"unsupported List member: {name}");
                return CType.Void;
        }
    }

    private string RenderListOp(IlCall call)
    {
        var name = Strings.From(call.Callee, ContractFacts.Len("List."));
        var args = call.Args;
        var resultType = TypeOfListOp(call);
        if (CFail.Failed) return "";
        var seq = RequireSeq(args[0], call.Callee);
        var elem = seq.Element!;
        var len = SeqFns(seq, out var at);
        var s = Temp("seq");
        var n = Temp("n");
        var i = Temp("i");
        var v = Temp("v");
        var f = Temp("fn");
        var sb = new List<string>();
        sb.Add($"{seq.CName} {s} = {RenderExpr(args[0])}; ");
        switch (name)
        {
            // As in C#, evaluate the receiver, index, and value in that order before checking the range
            case "Insert":
                {
                    var index = Temp("index");
                    sb.Add($"int32_t {index} = {RenderExpr(args[1])}; ");
                    sb.Add($"{elem.CName} {v} = {RenderCoerced(args[2], elem)}; ");
                    return $"({{ {JoinParts(sb)}tcs_list_insert({s}, {index}, &{v}, sizeof({v}), {LayoutRef(elem)}); }})";
                }
            case "GetRange":
                {
                    var index = Temp("index");
                    var count = Temp("count");
                    sb.Add($"int32_t {index} = {RenderExpr(args[1])}; int32_t {count} = {RenderExpr(args[2])}; ");
                    return $"({{ {JoinParts(sb)}tcs_list_get_range({s}, {index}, {count}); }})";
                }
            case "Copy":
                {
                    var copyFn = seq.Kind == CTypeKind.Array ? "tcs_array_to_list" : "tcs_list_copy";
                    return $"({{ {JoinParts(sb)}{copyFn}({s}); }})";
                }
            case "FindIndex":
                {
                    var match = ListClosure(sb, args, 1, CType.Bool, new List<CType> { elem }, f, v);
                    var r = Temp("index");
                    sb.Add($"int32_t {r} = -1; ");
                    sb.Add(ListLoop($"if ({match}) {{ {r} = {i}; break; }}", false, n, len, s, i, elem, v, at));
                    return $"({{ {JoinParts(sb)}{r}; }})";
                }
            // Compact the kept elements forward in place with the same steps as .NET RemoveAll (copying element bytes like
            // tcs_list_remove_at). The length is read every time so the result is the same even if the predicate changes the list
            case "RemoveAll":
                {
                    var match = ListClosure(sb, args, 1, CType.Bool, new List<CType> { elem }, f, v);
                    var free = Temp("free");
                    var current = Temp("current");
                    var removed = Temp("removed");
                    sb.Add($"int32_t {free} = 0; int32_t {removed} = 0; ");
                    sb.Add(ListSkip(free, false, len, s, elem, v, at, match));
                    sb.Add($"if ({free} < {len}({s})) {{ int32_t {current} = {free} + 1; ");
                    sb.Add($"while ({current} < {len}({s})) {{ {ListSkip(current, true, len, s, elem, v, at, match)}");
                    sb.Add($"if ({current} < {len}({s})) {{ {ListElement(elem, at, s, free)} = {ListElement(elem, at, s, current)}; {free}++; {current}++; }} }} ");
                    // If the predicate shrinks the list below free, it cannot be extended like .NET's _size, so it is not truncated
                    sb.Add($"{removed} = {len}({s}) - {free}; if ({removed} > 0) tcs_list_truncate({s}, (size_t){free}); }} ");
                    return $"({{ {JoinParts(sb)}{removed}; }})";
                }
            case "Contains":
            case "IndexOf":
            case "Remove":
                {
                    var target = Temp("target");
                    var r = Temp("index");
                    sb.Add($"{elem.CName} {target} = {RenderCoerced(args[1], elem)}; int32_t {r} = -1; ");
                    sb.Add(ListLoop($"if ({EqualExpr(elem, v, target)}) {{ {r} = {i}; break; }}", false, n, len, s, i, elem, v, at));
                    if (name == "Contains") return $"({{ {JoinParts(sb)}{r} >= 0; }})";
                    if (name == "IndexOf") return $"({{ {JoinParts(sb)}{r}; }})";
                    return $"({{ {JoinParts(sb)}if ({r} >= 0) tcs_list_remove_at({s}, {r}); {r} >= 0; }})";
                }
            case "Sort":
                {
                    if (args.Count == 2)
                    {
                        var type = CType.Closure(CType.I32, new List<CType> { elem, elem });
                        sb.Add($"TcsClosure *{f} = (TcsClosure *)tcs_nonnull({RenderCoerced(args[1], type)}); ");
                        var comparator = LiftComparator(elem, type, "comparison");
                        return $"({{ {JoinParts(sb)}tcs_list_sort({s}, 0, {comparator}, {f}); }})";
                    }
                    var natural = LiftComparator(elem, null, "natural");
                    // For float, as in .NET, NaNs are moved to the front before the rest are sorted
                    var start = elem.Kind == CTypeKind.F32 ? $"tcs_list_move_nans_to_front({s})" : "0";
                    return $"({{ {JoinParts(sb)}tcs_list_sort({s}, {start}, {natural}, NULL); }})";
                }
            default:
                CFail.Report($"unsupported List member: {name}");
                return "";
        }
    }

    // ToString of a StringBuilder (sb is a List<string>)
    private static string JoinParts(List<string> parts) => string.Join("", parts);

    // Local function Loop of RenderListOp (reads each element into v and runs body)
    private static string ListLoop(string body, bool reverse, string n, string len, string s, string i,
        CType elem, string v, string at)
    {
        var head = reverse
            ? $"for (int32_t {i} = {n} - 1; {i} >= 0; {i}--) {{ "
            : $"for (int32_t {i} = 0; {i} < {n}; {i}++) {{ ";
        return $"int32_t {n} = {len}({s}); " + head +
            $"{elem.CName} {v} = *({elem.CName} *){at}({s}, {i}); {body} }} ";
    }

    // Local function Closure of RenderListOp (appends a statement binding the closure to f to sb, and returns a call with v (and v2))
    private string ListClosure(List<string> sb, List<IlExpr> args, int index, CType ret, List<CType> parameters,
        string f, string v)
    {
        var type = CType.Closure(ret, parameters);
        sb.Add($"TcsClosure *{f} = (TcsClosure *)tcs_nonnull(" +
            $"{RenderCoerced(args[index], type)}); ");
        var callArgs = new List<string>();
        callArgs.Add(v);
        if (parameters.Count == 2) callArgs.Add($"{v}2");
        return CallClosure(f, type, callArgs);
    }

    // Local function Element of RenderListOp (RemoveAll)
    private static string ListElement(CType elem, string at, string s, string index) =>
        $"*({elem.CName} *){at}({s}, {index})";

    // Local function Skip of RenderListOp (RemoveAll)
    private static string ListSkip(string index, bool matching, string len, string s, CType elem, string v,
        string at, string match)
    {
        var negate = matching ? "!" : "";
        return $"while ({index} < {len}({s})) {{ {elem.CName} {v} = {ListElement(elem, at, s, index)}; " +
            $"if ({negate}({match})) break; {index}++; }} ";
    }

    // Lifted comparison function for sort. mode: natural / comparison (ctx = closure)
    private string LiftComparator(CType elem, CType? closureType, string mode)
    {
        var fnName = "tcs_cmp_" + _comparatorSerial;
        _comparatorSerial++;
        _closureDecls.Add($"static int {fnName}(void *, const void *, const void *);");
        var body = new List<string>();
        body.Add($"static int\n{fnName}(void *ctx, const void *a, const void *b)\n{{\n");
        // Apply const to the element itself (postfix). When the element is a pointer (TcsString * etc.),
        // a prefix const applies to the pointee, and const is dropped when initializing x / y
        body.Add("    TcsClosure *f = ctx; (void)f;\n");
        body.Add($"    {elem.CName} x = *({elem.CName} const *)a;\n");
        body.Add($"    {elem.CName} y = *({elem.CName} const *)b;\n");
        switch (mode)
        {
            case "natural":
                {
                    var compare = CompareExpr(elem, "x", "y");
                    body.Add($"    return {compare};\n");
                    break;
                }
            case "comparison":
                {
                    var call = CallClosure("f", closureType!, new List<string> { "x", "y" });
                    body.Add($"    int32_t r = {call};\n");
                    body.Add("    return r < 0 ? -1 : r > 0 ? 1 : 0;\n");
                    break;
                }
            default:
                CFail.Report($"unknown comparator mode: {mode}");
                return "";
        }
        body.Add("}\n\n");
        _pendingClosures.Add(string.Join("", body));
        return fnName;
    }

    // ==== CEmitter.Dict.cs ====

    // Render a dict key as the C argument pair (key_i, key_s)
    private string DictKeyArgs(CType keyType, IlExpr key) =>
        DictKeyArgs(keyType, RenderExpr(key));

    private static string DictKeyArgs(CType keyType, string rendered)
    {
        if (keyType.Kind == CTypeKind.String) return $"0, {rendered}";
        // class key: the pointer is carried in the key_s slot (traced by the GC)
        if (keyType.Kind == CTypeKind.Ref) return $"0, (TcsString *)(void *){rendered}";
        return $"{rendered}, NULL";
    }

    // On failure, set dict to a type that has values (int → void) and return void
    private CType RequireDict(IlExpr recv, out CType dict)
    {
        dict = TypeOf(recv);
        if (dict.Kind != CTypeKind.Dict)
        {
            CFail.Report($"receiver is not a Dictionary: {dict.Display()}");
            dict = CType.Dict(CType.I32, CType.Void);
            return CType.Void;
        }
        return dict.Element!;
    }


    private CType TypeOfDictTable(IlTable table)
    {
        foreach (var e in table.Entries)
        {
            if (e.NameKey != null || (e.Key == null && table.Entries.Count > 0 && table.KeyType == null))
            {
                CFail.Report("mixed IlTable entries are not supported");
                return CType.Void;
            }
        }
        CType? key = null;
        if (table.KeyType != null) key = _facts.MapType(table.KeyType);
        CType? value = null;
        if (table.ElementType != null) value = _facts.MapType(table.ElementType);
        foreach (var entry in table.Entries)
        {
            if (entry.Key == null)
            {
                CFail.Report("dict IlTable entry without key");
                return CType.Void;
            }
            // If the contract has key / value types, each entry only needs to be assignable to them
            // (boxing object values, derived → base upcasts). If there are none, infer a common type
            if (table.KeyType != null) CheckAssignable(key!, entry.Key, "dict key");
            else key = key == null ? TypeOf(entry.Key)
                : CommonType(key, TypeOf(entry.Key), "dict keys");
            if (table.ElementType != null) CheckAssignable(value!, entry.Value, "dict value");
            else value = value == null ? TypeOf(entry.Value)
                : CommonType(value, TypeOf(entry.Value), "dict values");
        }
        if (key == null || value == null)
        {
            CFail.Report(
                "cannot infer Dictionary key/value types (no metadata)");
            return CType.Void;
        }
        if (!_facts.IsDictKey(key))
        {
            CFail.Report($"Dictionary key type not supported: {key.Display()}");
            return CType.Void;
        }
        return CType.Dict(key, value);
    }

    private string RenderDictTable(IlTable table)
    {
        var type = TypeOfDictTable(table);
        if (CFail.Failed) return "";
        var dictTemp = Temp("dict");
        var sb = new List<string>();
        sb.Add($"TcsDict *{dictTemp} = tcs_typed(tcs_dict_new(" +
            $"{_facts.KeyKind(type.KeyType!)}, " +
            $"sizeof({type.Element!.CName}), {LayoutRef(type.Element)}), {RuntimeTypeId(type)}); ");
        // Evaluate key → value in that order, as in C#
        foreach (var entry in table.Entries)
        {
            var keyTemp = Temp("dict_key");
            sb.Add($"{type.KeyType!.CName} {keyTemp} = {RenderExpr(entry.Key!)}; ");
            var valueTemp = Temp("dict_value");
            sb.Add($"{type.Element!.CName} {valueTemp} = " +
                $"{RenderCoerced(entry.Value, type.Element!)}; ");
            sb.Add($"*({type.Element!.CName} *)tcs_dict_put({dictTemp}, " +
                $"{DictKeyArgs(type.KeyType!, keyTemp)}) = {valueTemp}; ");
        }
        return $"({{ {JoinParts(sb)}{dictTemp}; }})";
    }


    private string RenderDictSimple(IlCall call, string function)
    {
        TypeOfCall(call);
        RequireDict(call.Args[0], out var dictType);
        if (CFail.Failed) return "";
        var dictTemp = Temp("dict");
        return $"({{ TcsDict *{dictTemp} = {RenderExpr(call.Args[0])}; " +
            $"{function}({dictTemp}, {DictKeyArgs(dictType.KeyType!, call.Args[1])}); }})";
    }

    // Multi-return intrinsics of the (found, value) form: Dict.TryGet /
    // Math.TryParseInt / Math.TryParseFloat (out-parameter multi-return is
    // handled separately on the ref method side)
    private static CType? TryParseValueType(string callee)
    {
        if (callee == "Math.TryParseInt") return CType.I32;
        if (callee == "Math.TryParseFloat") return CType.F32;
        return null;
    }

    private void EmitMultiAssign(IlMultiAssign multi)
    {
        if (EmitForeignMultiAssign(multi)) return;
        if (EmitUserOutCall(multi)) return;
        if (multi.Values.Count == multi.Targets.Count && multi.Values.Count > 0)
        {
            EmitPairwiseMultiAssign(multi);
            return;
        }
        IlCall? tryGet = null;
        IlVar? foundVar = null;
        IlVar? valueVar = null;
        if (multi.Values.Count == 1 && multi.Values[0] is IlCall single
            && (single.Callee == "Dict.TryGet" || single.Callee == "Math.TryParseInt"
                || single.Callee == "Math.TryParseFloat")
            && multi.Targets.Count == 2
            && multi.Targets[0] is IlVar first
            && multi.Targets[1] is IlVar second)
        {
            tryGet = single;
            foundVar = first;
            valueVar = second;
        }
        if (tryGet == null || foundVar == null || valueVar == null)
        {
            CFail.Report(
                "only (found, value) multi-return intrinsics are supported in multi-assign");
            return;
        }
        var parsedType = TryParseValueType(tryGet.Callee);
        if (parsedType != null)
        {
            EmitTryParse(multi, tryGet, parsedType, foundVar, valueVar);
            return;
        }
        RequireArity("Dict.TryGet", tryGet.Args.Count, 3);
        if (CFail.Failed) return;
        var valueType = RequireDict(tryGet.Args[0], out var dictType);
        RequireAssignable(dictType.KeyType!, TypeOf(tryGet.Args[1]),
            "Dict.TryGet key");
        RequireAssignable(valueType, TypeOf(tryGet.Args[2]),
            "Dict.TryGet fallback");

        var found = multi.Declare
            ? DeclareLocal(foundVar.Name, CType.Bool) : Resolve(foundVar.Name);
        var value = multi.Declare
            ? DeclareLocal(valueVar.Name, valueType) : Resolve(valueVar.Name);
        var dictTemp = Temp("dict");
        var fallbackTemp = Temp("fallback");
        Line($"TcsDict *{dictTemp} = {RenderExpr(tryGet.Args[0])};");
        Line($"{valueType.CName} {fallbackTemp} = " +
            $"{RenderCoerced(tryGet.Args[2], valueType)};");
        Line($"{found.CName} = tcs_dict_tryget({dictTemp}, " +
            $"{DictKeyArgs(dictType.KeyType!, tryGet.Args[1])}, " +
            $"&{value.CName}, &{fallbackTemp});");
    }

    private void EmitOutWrites()
    {
        var outs = _currentMethodFact.OutParameters;
        for (var i = 0; i < outs.Count; i++)
            Line($"*o_{Names.Id(outs[i].Name)}_{i} = {Resolve(outs[i].Name).CName};");
    }

    private static string OutPointerParameter(ParameterFact parameter, int index) =>
        $"{parameter.Type.CName} *o_{Names.Id(parameter.Name)}_{index}";

    private List<string> _pendingOutArguments = new List<string>();

    // Arguments to append to the next rendered method call. They are taken out before rendering the arguments
    // so they do not go to calls inside the argument expressions
    private List<string> TakeOutArguments()
    {
        var taken = _pendingOutArguments;
        _pendingOutArguments = new List<string>();
        return taken;
    }

    private MethodFact? UserOutCallee(IlExpr call)
    {
        MethodFact? fact = null;
        if (call is IlCall structCall && TryStructCallee(structCall.Callee, out var st, out var member)
            && member != "ctor" && member != "op_Equality")
            fact = _facts.Method(st.Name, member);
        else if (call is IlCall classCall && ForeignMethod(classCall.Callee) == null
            && CountDots(classCall.Callee) == 1
            && _classes.ContainsKey(ContractFacts.Sub(classCall.Callee, 0, ContractFacts.IndexOfByte(classCall.Callee, '.'))))
        {
            var dot = ContractFacts.IndexOfByte(classCall.Callee, '.');
            fact = _facts.Method(ContractFacts.Sub(classCall.Callee, 0, dot), Strings.From(classCall.Callee, dot + 1));
        }
        else if (call is IlDynCall dyn && dyn.Callee is IlField field && field.Recv is IlVar owner
            && _classes.ContainsKey(owner.Name))
            fact = _facts.Method(owner.Name, field.Name);
        else if (call is IlInvoke invoke && TypeOf(invoke.Recv).Kind == CTypeKind.Ref)
            fact = ResolveInvokeFact(TypeOf(invoke.Recv), invoke.Method);
        if (fact != null && fact.OutParameters.Count > 0) return fact;
        return null;
    }

    // `Callee.Count(ch => ch == '.')` in UserOutCallee
    private static int CountDots(string text)
    {
        var u = new Utf8Bytes(text);
        var count = 0;
        for (var i = 0; i < u.Length; i++)
        {
            if (u[i] == '.') count++;
        }
        return count;
    }

    // Call to a user-defined method with out: IlMultiAssign([return value?, out...], [call]).
    // Pass stack temporaries as receivers, and assign to the receivers after the call
    private bool EmitUserOutCall(IlMultiAssign multi)
    {
        if (multi.Values.Count != 1) return false;
        var call = multi.Values[0];
        var fact = UserOutCallee(call);
        if (fact == null) return false;
        var hasReturn = !CType.Same(fact.ReturnType, CType.Void);
        var outTargets = new List<IlExpr>();
        for (var k = hasReturn ? 1 : 0; k < multi.Targets.Count; k++) outTargets.Add(multi.Targets[k]);
        RequireArity(fact.Name, outTargets.Count, fact.OutParameters.Count);
        if (outTargets.Count != fact.OutParameters.Count) return true;
        var temps = new List<string>();
        foreach (var parameter in fact.OutParameters)
        {
            var temp = Temp("out");
            Line($"{parameter.Type.CName} {temp} = {ZeroInit(parameter.Type)};");
            AddVariable(temp, new Variable(temp, parameter.Type));
            temps.Add(temp);
        }
        var pending = new List<string>();
        foreach (var t in temps) pending.Add($"&{t}");
        _pendingOutArguments = pending;
        var rendered = RenderExpr(call);
        if (hasReturn) Line($"{RenderExpr(multi.Targets[0])} = {rendered};");
        else Line($"{rendered};");
        for (var i = 0; i < temps.Count; i++)
        {
            if (outTargets[i] is IlVar discard && discard.Name == "_") continue;
            EmitAssign(new IlAssign(outTargets[i], new IlVar(temps[i], null), null));
        }
        return true;
    }

    // An overload separate from DeclareLocal(name, type, value) in CEmitter.cs (declares without an initial value and returns the variable)
    private Variable DeclareLocal(string name, CType type)
    {
        var variable = new Variable("v_" + Names.Id(name) + "_" + _serial, type);
        _serial++;
        AddVariable(name, variable);
        Line($"{type.CName} {variable.CName};");
        return variable;
    }

    private void EmitTryParse(IlMultiAssign multi, IlCall call, CType parsedType,
        IlVar foundVar, IlVar valueVar)
    {
        RequireArity(call.Callee, call.Args.Count, 2);
        if (CFail.Failed) return;
        RequireType(CType.String, TypeOf(call.Args[0]), call.Callee);
        RequireAssignable(parsedType, TypeOf(call.Args[1]), $"{call.Callee} fallback");
        var found = multi.Declare
            ? DeclareLocal(foundVar.Name, CType.Bool) : Resolve(foundVar.Name);
        var value = multi.Declare
            ? DeclareLocal(valueVar.Name, parsedType) : Resolve(valueVar.Name);
        var text = Temp("parse_text");
        var fallback = Temp("fallback");
        var helper = CType.Same(parsedType, CType.I32) ? "tcs_try_parse_i32" : "tcs_try_parse_f32";
        Line($"TcsString *{text} = {RenderExpr(call.Args[0])};");
        Line($"{parsedType.CName} {fallback} = {RenderCoerced(call.Args[1], parsedType)};");
        var foundPlace = found.Boxed ? "(*" + found.CName + ")" : found.CName;
        var valuePlace = value.Boxed ? "(*" + value.CName + ")" : value.CName;
        Line($"{foundPlace} = " +
            $"{helper}({text}, &{valuePlace}, {fallback});");
    }

    // Deconstructing assignment `(a, b) = (x, y)` / `var (a, b) = r`: evaluate the whole right side, then
    // assign from the left
    private void EmitPairwiseMultiAssign(IlMultiAssign multi)
    {
        var tempNames = new List<string>();
        var tempTypes = new List<CType>();
        foreach (var value in multi.Values)
        {
            var type = TypeOf(value);
            var temp = Temp("multi");
            Line($"{type.CName} {temp} = {RenderExpr(value)};");
            AddVariable(temp, new Variable(temp, type));
            tempNames.Add(temp);
            tempTypes.Add(type);
        }
        for (var i = 0; i < multi.Targets.Count; i++)
        {
            var temp = tempNames[i];
            var type = tempTypes[i];
            if (multi.Targets[i] is IlVar discard && discard.Name == "_") continue;
            if (multi.Declare)
            {
                if (multi.Targets[i] is IlVar declared)
                {
                    var variable = DeclareLocal(declared.Name, type);
                    Line($"{variable.CName} = {temp};");
                }
                else CFail.Report("multi-assign declaration target is not a variable");
                continue;
            }
            EmitAssign(new IlAssign(multi.Targets[i], new IlVar(temp, null), null));
        }
    }

    // ---- Intrinsics ----

    // ==== CEmitter.Intrinsics ====
    // Map IlCalls of the TinySystem runtime surface (String / Math / Dict.Keys / Values) and of IL callee
    // names (string.* / math.* / tonumber / os.getenv / table.remove)
    // to the C runtime (CRuntime.Strings / Lib). List.* is in CEmitter.Linq.

    private static HashSet<string>? _intrinsicPrefixes;
    private static HashSet<string>? _intrinsicNames;

    private static HashSet<string> IntrinsicPrefixes()
    {
        if (_intrinsicPrefixes == null)
        {
            var set = new HashSet<string>();
            foreach (var w in "String. Math. List. TcsQueue. TcsHashSet. Random. Char.".Split(' ')) set.Add(w);
            _intrinsicPrefixes = set;
        }
        return _intrinsicPrefixes;
    }

    private static HashSet<string> IntrinsicNames()
    {
        if (_intrinsicNames == null)
        {
            var set = new HashSet<string>();
            foreach (var w in "__tcs_fparse __tcs_iparse __tcs_getenv __tcs_fmod __tcs_cstr __tcs_upper __tcs_lower __tcs_format List.RemoveAt __tcs_trunc __tcs_char Dict.Keys Dict.Values".Split(' ')) set.Add(w);
            _intrinsicNames = set;
        }
        return _intrinsicNames;
    }

    // Calls through the facade (`using TinySystem;`) arrive as "TinySystem.X.Y"
    private static string NormalizeCallee(string callee)
    {
        if (callee.StartsWith("TinySystem.")) return Strings.From(callee, Strings.ByteLength("TinySystem."));
        return callee;
    }

    private bool IsIntrinsicCallee(string rawCallee)
    {
        // It arrives as a qualified name, so it does not collide with a user class Utf8
        if (rawCallee.StartsWith("TinySystem.Utf8.")) return true;
        var callee = NormalizeCallee(rawCallee);
        if (IntrinsicNames().Contains(callee)) return true;
        var dot = ContractFacts.IndexOfByte(callee, '.');
        if (dot <= 0) return false;
        // A user class with the same name takes precedence (user call path)
        var head = Strings.Prefix(callee, dot);
        if (_classes.ContainsKey(head)
            || _facts.Structs.ContainsKey(head)) return false;
        return IntrinsicPrefixes().Contains(Strings.Prefix(callee, dot + 1));
    }

    private CType TypeOfIntrinsic(IlCall rawCall)
    {
        var call = new IlCall(NormalizeCallee(rawCall.Callee), rawCall.Args, rawCall.Origin);
        var args = call.Args;
        if (call.Callee.StartsWith("Random."))
            return TypeOfRandomOp(call);
        if (call.Callee.StartsWith("Utf8."))
            return TypeOfUtf8Op(call);
        if (call.Callee.StartsWith("Char."))
        {
            RequireArity(call.Callee, args.Count, 1);
            if (CFail.Failed) return CType.Void;
            RequireType(CType.I32, TypeOf(args[0]), call.Callee);
            if (call.Callee == "Char.ToUpper" || call.Callee == "Char.ToLower") return CType.I32;
            if (call.Callee == "Char.ConvertFromUtf32") return CType.String;
            return CType.Bool;
        }
        // List.RemoveAt is in the switch below (its value is the removed element)
        if (call.Callee.StartsWith("List.") && call.Callee != "List.RemoveAt")
            return TypeOfListOp(call);
        if (call.Callee.StartsWith("TcsQueue."))
            return TypeOfQueueOp(call);
        if (call.Callee.StartsWith("TcsHashSet."))
            return TypeOfHashSetOp(call);
        switch (call.Callee)
        {
            case "__tcs_trunc":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                NumericJoin(TypeOf(args[0]), CType.I32, "trunc");
                return CType.I32;
            case "__tcs_char":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                RequireType(CType.I32, TypeOf(args[0]), "(char) cast");
                return CType.I32;
            case "__tcs_fparse":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                RequireType(CType.String, TypeOf(args[0]), "float.Parse");
                return CType.F32;
            case "__tcs_iparse":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                RequireType(CType.String, TypeOf(args[0]), "int.Parse");
                return CType.I32;
            case "__tcs_fmod":
                RequireArity(call.Callee, args.Count, 2);
                if (CFail.Failed) return CType.Void;
                NumericJoin(TypeOf(args[0]), TypeOf(args[1]), "fmod");
                return CType.F32;
            case "__tcs_getenv":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                RequireType(CType.String, TypeOf(args[0]), "Environment.GetEnvironmentVariable");
                return CType.String;
            case "__tcs_cstr":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                RequireType(CType.I32, TypeOf(args[0]), "char to string");
                return CType.String;
            case "__tcs_upper":
            case "__tcs_lower":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                RequireType(CType.String, TypeOf(args[0]), call.Callee);
                return CType.String;
            case "__tcs_format":
                if (args.Count == 0)
                {
                    CFail.Report("__tcs_format: missing format");
                    return CType.Void;
                }
                RequireType(CType.String, TypeOf(args[0]), "__tcs_format");
                for (var i = 1; i < args.Count; i++) FormatArgKind(TypeOf(args[i]));
                return CType.String;
            case "List.RemoveAt":
                RequireArity(call.Callee, args.Count, 2);
                if (CFail.Failed) return CType.Void;
                RequireType(CType.I32, TypeOf(args[1]), "RemoveAt index");
                return RequireList(args[0]).Element!;
            case "Dict.Keys":
                {
                    RequireArity(call.Callee, args.Count, 1);
                    if (CFail.Failed) return CType.Void;
                    RequireDict(args[0], out var keyDict);
                    return CType.List(keyDict.KeyType!);
                }
            case "Dict.Values":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                return CType.List(RequireDict(args[0], out var valueDict));
        }
        if (call.Callee.StartsWith("String."))
            return TypeOfStringOp(call);
        if (call.Callee.StartsWith("Math."))
            return TypeOfMathOp(call);
        CFail.Report($"unsupported intrinsic: {call.Callee}");
        return CType.Void;
    }

    private string RenderIntrinsic(IlCall rawCall)
    {
        var call = new IlCall(NormalizeCallee(rawCall.Callee), rawCall.Args, rawCall.Origin);
        var type = TypeOfIntrinsic(call);
        // If typing recorded an error, do not look at the arguments (the C# version exited with an exception)
        if (CFail.Failed) return "";
        var args = call.Args;
        if (call.Callee.StartsWith("Random."))
            return RenderRandomOp(call, type);
        if (call.Callee.StartsWith("Utf8."))
            return RenderUtf8Op(call, type);
        if (call.Callee.StartsWith("Char."))
        {
            var member = Strings.From(call.Callee, Strings.ByteLength("Char."));
            var fn = "";
            if (member == "IsDigit") fn = "tcs_char_is_digit";
            else if (member == "IsLetter") fn = "tcs_char_is_letter";
            else if (member == "IsLetterOrDigit") fn = "tcs_char_is_letter_or_digit";
            else if (member == "IsWhiteSpace") fn = "tcs_char_is_space";
            else if (member == "IsUpper") fn = "tcs_char_is_upper";
            else if (member == "IsLower") fn = "tcs_char_is_lower";
            else if (member == "ToUpper") fn = "tcs_char_to_upper";
            else if (member == "ToLower") fn = "tcs_char_to_lower";
            else if (member == "ConvertFromUtf32") fn = "tcs_string_from_codepoint";
            else
            {
                CFail.Report($"unsupported Char member: {member}");
                return "";
            }
            return $"{fn}({RenderExpr(args[0])})";
        }
        if (call.Callee.StartsWith("List.") && call.Callee != "List.RemoveAt")
            return RenderListOp(call);
        if (call.Callee.StartsWith("TcsQueue."))
            return RenderQueueOp(call);
        if (call.Callee.StartsWith("TcsHashSet."))
            return RenderHashSetOp(call);
        switch (call.Callee)
        {
            case "__tcs_trunc":
                if (TypeOf(args[0]).Kind == CTypeKind.I32) return RenderExpr(args[0]);
                return $"tcs_trunc_f32({RenderExpr(args[0])})";
            case "__tcs_char":
                return $"tcs_char_cast({RenderExpr(args[0])})";
            case "__tcs_fparse":
                return $"tcs_parse_f32_or_fault({RenderExpr(args[0])})";
            case "__tcs_iparse":
                return $"tcs_parse_i32_or_fault({RenderExpr(args[0])})";
            case "__tcs_fmod":
                {
                    var values = new List<TypedValue>();
                    values.Add(new TypedValue(CType.F32, RenderExpr(args[0])));
                    values.Add(new TypedValue(CType.F32, RenderExpr(args[1])));
                    return RenderOrderedCall("fmodf", type, values);
                }
            case "__tcs_getenv":
                return $"tcs_getenv({RenderExpr(args[0])})";
            case "__tcs_cstr":
                return $"tcs_string_from_byte({RenderExpr(args[0])})";
            case "__tcs_upper":
                return $"tcs_string_map_case({RenderExpr(args[0])}, 1)";
            case "__tcs_lower":
                return $"tcs_string_map_case({RenderExpr(args[0])}, 0)";
            case "__tcs_format":
                return RenderFormat(call);
            case "List.RemoveAt":
                {
                    var list = Temp("list");
                    var at = Temp("index");
                    var removed = Temp("removed");
                    var elem = type.CName;
                    return $"({{ TcsList *{list} = {RenderExpr(args[0])}; " +
                        $"int32_t {at} = {RenderExpr(args[1])}; " +
                        $"{elem} {removed} = *({elem} *)tcs_list_at({list}, {at}); " +
                        $"tcs_list_remove_at({list}, {at}); {removed}; }})";
                }
            case "Dict.Keys":
                return $"tcs_dict_keys({RenderExpr(args[0])})";
            case "Dict.Values":
                return $"tcs_dict_values({RenderExpr(args[0])})";
        }
        if (call.Callee.StartsWith("String."))
            return RenderStringOp(call, type);
        if (call.Callee.StartsWith("Math."))
            return RenderMathOp(call, type);
        CFail.Report($"unsupported intrinsic: {call.Callee}");
        return "";
    }

    private CType RequireList(IlExpr expr)
    {
        var type = TypeOf(expr);
        if (type.Kind != CTypeKind.List || type.Element == null)
        {
            CFail.Report($"receiver is not a typed List: {type.Display()}");
            return CType.List(CType.Void);
        }
        return type;
    }

    // ---- __tcs_format (printf format) ----
    private static string FormatArgKind(CType type)
    {
        switch (type.Kind)
        {
            case CTypeKind.I32: return "TCS_FMT_I32";
            case CTypeKind.F32: return "TCS_FMT_F32";
            case CTypeKind.Bool: return "TCS_FMT_BOOL";
            case CTypeKind.String: return "TCS_FMT_STR";
        }
        CFail.Report($"__tcs_format does not support {type.Display()}");
        return "";
    }

    private string RenderFormat(IlCall call)
    {
        var format = Temp("format");
        var sb = new List<string>();
        sb.Add($"TcsString *{format} = {RenderExpr(call.Args[0])}; ");
        var entries = new List<string>();
        for (var i = 1; i < call.Args.Count; i++)
        {
            var argType = TypeOf(call.Args[i]);
            var temp = Temp("fmt_arg");
            sb.Add($"{argType.CName} {temp} = {RenderExpr(call.Args[i])}; ");
            var member = "";
            if (argType.Kind == CTypeKind.I32) member = "i";
            else if (argType.Kind == CTypeKind.F32) member = "f";
            else if (argType.Kind == CTypeKind.Bool) member = "b";
            else member = "s";
            entries.Add($"{{ {FormatArgKind(argType)}, .{member} = {temp} }}");
        }
        if (entries.Count == 0)
            return $"({{ {string.Join("", sb)}tcs_string_format({format}, 0, NULL); }})";
        var array = Temp("fmt_args");
        sb.Add($"TcsFormatArg {array}[] = {{ {string.Join(", ", entries)} }}; ");
        return $"({{ {string.Join("", sb)}tcs_string_format({format}, {entries.Count}, {array}); }})";
    }

    // Turn a String.FormatFloat / String.FormatInt spec ("G", "E2", etc., il-spec §13) into a format character and precision (-1 if none)
    // Returns the format character (a 1-byte string), and the precision through out
    private static string NumberFormatSpec(IlExpr spec, string letters, out int precision)
    {
        precision = -1;
        var ok = false;
        var kind = "";
        var digits = "";
        if (spec is IlLit lit && lit.Kind == IlLitKind.String)
        {
            var u = new Utf8Bytes(lit.S);
            var n = u.Length;
            if (n >= 1 && ContractFacts.IndexOfByte(letters, u[0]) >= 0 && n - 1 <= 9)
            {
                ok = true;
                for (var i = 1; i < n; i++)
                {
                    if (u[i] < '0' || u[i] > '9') ok = false;
                }
                kind = u.Slice(0, 1).ToString();
                digits = u.Slice(1, n - 1).ToString();
            }
        }
        if (!ok)
        {
            CFail.Report($"number format: spec is not one of {letters} with a precision");
            return "";
        }
        if (digits != "") precision = int.Parse(digits);
        return kind;
    }

    private const string FloatFormatLetters = "GgEeFfNn";
    private const string IntFormatLetters = "GgEeFfNnDdXx";

    private static string NumberFormatText(string runtimeFunction, string v, string spec, int precision) =>
        $"{runtimeFunction}({v}, '{spec}', INT32_C({precision}))";

    private string RenderNumberFormat(IlCall call, string letters, string runtimeFunction)
    {
        var spec = NumberFormatSpec(call.Args[1], letters, out int precision);
        var type = TypeOf(call.Args[0]);
        if (type.Kind != CTypeKind.Nullable) return NumberFormatText(runtimeFunction, RenderExpr(call.Args[0]), spec, precision);
        var temp = Temp("opt");
        var value = RenderExpr(call.Args[0]);
        var formatted = NumberFormatText(runtimeFunction, $"{temp}.v", spec, precision);
        var empty = InternStringLiteral(new List<int>());
        return $"({{ {type.CName} {temp} = {value}; " +
            $"{temp}.has ? {formatted} : {empty}; }})";
    }

    // ---- String.* (TinySystem.String) ----
    private CType TypeOfStringOp(IlCall call)
    {
        var name = Strings.From(call.Callee, Strings.ByteLength("String."));
        var args = call.Args;
        switch (name)
        {
            case "FormatEnum":
                return TypeOfFormatEnum(call);
            case "FormatFloat":
            case "FormatInt":
                {
                    RequireArity(call.Callee, args.Count, 2);
                    if (CFail.Failed) return CType.Void;
                    var isFloat = name == "FormatFloat";
                    var element = isFloat ? CType.F32 : CType.I32;
                    var value = TypeOf(args[0]);
                    if (!CType.Same(value, element) && !CType.Same(value, CType.Nullable(element)))
                    {
                        CFail.Report($"String.{name}: value is not {element.Display()} ({value.Display()})");
                        return CType.Void;
                    }
                    NumberFormatSpec(args[1], isFloat ? FloatFormatLetters : IntFormatLetters, out int precision);
                    return CType.String;
                }
            case "Contains":
            case "StartsWith":
            case "EndsWith":
                RequireArity(call.Callee, args.Count, 2);
                if (CFail.Failed) return CType.Void;
                RequireType(CType.String, TypeOf(args[0]), name); RequireType(CType.String, TypeOf(args[1]), name);
                return CType.Bool;
            case "Replace":
                RequireArity(call.Callee, args.Count, 3);
                if (CFail.Failed) return CType.Void;
                RequireType(CType.String, TypeOf(args[0]), name); RequireType(CType.String, TypeOf(args[1]), name);
                // A null replacement string means removal (the runtime accepts NULL)
                if (TypeOf(args[2]).Kind != CTypeKind.Null) RequireType(CType.String, TypeOf(args[2]), name);
                return CType.String;
            case "Trim":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                RequireType(CType.String, TypeOf(args[0]), name);
                return CType.String;
            case "IsNullOrEmpty":
                {
                    RequireArity(call.Callee, args.Count, 1);
                    if (CFail.Failed) return CType.Void;
                    var kind = TypeOf(args[0]).Kind;
                    if (kind != CTypeKind.String && kind != CTypeKind.Null)
                    {
                        CFail.Report("IsNullOrEmpty argument is not a string");
                        return CType.Void;
                    }
                    return CType.Bool;
                }
            case "CompareOrdinal":
                RequireArity(call.Callee, args.Count, 2);
                if (CFail.Failed) return CType.Void;
                for (var i = 0; i < 2; i++)
                {
                    var kind = TypeOf(args[i]).Kind;
                    if (kind != CTypeKind.String && kind != CTypeKind.Null)
                    {
                        CFail.Report("CompareOrdinal argument is not a string");
                        return CType.Void;
                    }
                }
                return CType.I32;
            case "Split":
                if (args.Count != 1 && args.Count != 2)
                {
                    CFail.Report("String.Split: expected 1 or 2 arguments");
                    return CType.Void;
                }
                RequireType(CType.String, TypeOf(args[0]), name);
                if (args.Count == 2) RequireType(CType.String, TypeOf(args[1]), "Split separator");
                return CType.Array(CType.String);
            case "JoinEnum":
                {
                    RequireArity(name, args.Count, 3);
                    if (CFail.Failed) return CType.Void;
                    RequireType(CType.String, TypeOf(args[0]), name);
                    var enumValues = TypeOf(args[1]);
                    if ((enumValues.Kind != CTypeKind.List && enumValues.Kind != CTypeKind.Array)
                        || !CType.Same(enumValues.Element, CType.I32) || !(args[2] is IlLit))
                    {
                        CFail.Report($"String.JoinEnum: values must be enum values ({enumValues.Display()})");
                        return CType.Void;
                    }
                    return CType.String;
                }
            case "Join":
                {
                    if (args.Count < 2)
                    {
                        CFail.Report("String.Join: expected 2+ arguments");
                        return CType.Void;
                    }
                    RequireType(CType.String, TypeOf(args[0]), name);
                    var values = TypeOf(args[1]);
                    if (args.Count == 2 && (values.Kind == CTypeKind.List || values.Kind == CTypeKind.Array))
                    {
                        if (!CType.Same(values.Element, CType.String) && JoinValueKind(values.Element!) == null)
                        {
                            CFail.Report(
                                $"String.Join: values must be string, i32, f32 or bool ({values.Element!.Display()})");
                            return CType.Void;
                        }
                        return CType.String;
                    }
                    for (var i = 1; i < args.Count; i++) RequireType(CType.String, TypeOf(args[i]), "Join value");
                    return CType.String;
                }
            default:
                CFail.Report($"unsupported String member: {name}");
                return CType.Void;
        }
    }

    // Render count arguments starting at args[0] as string arguments in order (local function Strings in the C# version)
    private List<TypedValue> StringArgs(List<IlExpr> args, int count)
    {
        var values = new List<TypedValue>();
        for (var i = 0; i < count; i++) values.Add(new TypedValue(CType.String, RenderExpr(args[i])));
        return values;
    }

    private string RenderStringOp(IlCall call, CType type)
    {
        var name = Strings.From(call.Callee, Strings.ByteLength("String."));
        var args = call.Args;
        switch (name)
        {
            case "FormatEnum": return RenderFormatEnum(call);
            case "JoinEnum":
                {
                    var table = EnumTable(((IlLit)args[2]).S);
                    var sequence = TypeOf(args[1]);
                    var sep = Temp("sep");
                    var seq = Temp("enum_values");
                    var sepValue = RenderExpr(args[0]);
                    var seqValue = RenderExpr(args[1]);
                    return $"({{ TcsString *{sep} = {sepValue}; " +
                        $"{sequence.CName} {seq} = ({sequence.CName})tcs_nonnull({seqValue}); " +
                        $"tcs_string_join_enum({sep}, (const int32_t *){seq}->data, {seq}->length, " +
                        $"{EnumTableArgs(table)}); }})";
                }
            case "FormatFloat": return RenderNumberFormat(call, FloatFormatLetters, "tcs_string_f32_spec");
            case "FormatInt": return RenderNumberFormat(call, IntFormatLetters, "tcs_string_i32_spec");
            case "Contains": return RenderOrderedCall("tcs_string_contains", type, StringArgs(args, 2));
            case "StartsWith": return RenderOrderedCall("tcs_string_starts_with", type, StringArgs(args, 2));
            case "EndsWith": return RenderOrderedCall("tcs_string_ends_with", type, StringArgs(args, 2));
            case "Replace": return RenderOrderedCall("tcs_string_replace", type, StringArgs(args, 3));
            case "Trim": return $"tcs_string_trim({RenderExpr(args[0])})";
            case "IsNullOrEmpty": return $"tcs_string_is_null_or_empty({RenderExpr(args[0])})";
            case "CompareOrdinal":
                return RenderOrderedCall("tcs_string_compare_ordinal", type, StringArgs(args, 2));
            case "Split":
                {
                    if (args.Count == 2)
                    {
                        var values = StringArgs(args, 2);
                        values.Add(new TypedValue(CType.I32, "1"));
                        return RenderOrderedCall("tcs_string_split", type, values);
                    }
                    return $"tcs_string_split({RenderExpr(args[0])}, NULL, 0)";
                }
            case "Join":
                {
                    var values = TypeOf(args[1]);
                    if (args.Count == 2 && (values.Kind == CTypeKind.List || values.Kind == CTypeKind.Array))
                    {
                        var join = values.Kind == CTypeKind.List ? "tcs_string_join_list" : "tcs_string_join_array";
                        var kind = JoinValueKind(values.Element!);
                        if (kind != null)
                        {
                            var withKind = StringArgs(args, 1);
                            withKind.Add(new TypedValue(values, RenderExpr(args[1])));
                            withKind.Add(new TypedValue(CType.I32, kind));
                            return RenderOrderedCall($"{join}_values", type, withKind);
                        }
                        var plain = StringArgs(args, 1);
                        plain.Add(new TypedValue(values, RenderExpr(args[1])));
                        return RenderOrderedCall(join, type, plain);
                    }
                    var sep = Temp("sep");
                    var items = Temp("join_items");
                    var sb = new List<string>();
                    sb.Add($"TcsString *{sep} = {RenderExpr(args[0])}; ");
                    var names = new List<string>();
                    for (var i = 1; i < args.Count; i++)
                    {
                        var temp = Temp("join_item");
                        sb.Add($"TcsString *{temp} = {RenderExpr(args[i])}; ");
                        names.Add(temp);
                    }
                    sb.Add($"TcsString *{items}[] = {{ {string.Join(", ", names)} }}; ");
                    return $"({{ {string.Join("", sb)}tcs_string_join_items({sep}, {items}, {names.Count}); }})";
                }
            default:
                CFail.Report($"unsupported String member: {name}");
                return "";
        }
    }

    // string.Join over value-type elements stringifies the elements the same way as concatenation (runtime TCS_JOIN_*)
    private static string? JoinValueKind(CType element)
    {
        switch (element.Kind)
        {
            case CTypeKind.I32: return "TCS_JOIN_I32";
            case CTypeKind.F32: return "TCS_JOIN_F32";
            case CTypeKind.Bool: return "TCS_JOIN_BOOL";
        }
        return null;
    }

    // ---- TinySystem.Text (calls to TinySystem.Utf8.X; calls the runtime tcs_utf8_*) ----
    private CType TypeOfUtf8Op(IlCall call)
    {
        var name = Strings.From(call.Callee, Strings.ByteLength("Utf8."));
        if (name == "Default")
        {
            RequireArity(call.Callee, call.Args.Count, 0);
            return CType.Utf8Bytes;
        }
        CType receiver = CType.Void;
        CType result = CType.Void;
        int ints = 0;
        if (name == "RuneCount") { receiver = CType.String; result = CType.I32; ints = 0; }
        else if (name == "RuneSubstring") { receiver = CType.String; result = CType.String; ints = 2; }
        else if (name == "New") { receiver = CType.String; result = CType.Utf8Bytes; ints = 0; }
        else if (name == "At") { receiver = CType.Utf8Bytes; result = CType.I32; ints = 1; }
        else if (name == "Slice") { receiver = CType.Utf8Bytes; result = CType.Utf8Bytes; ints = 2; }
        else if (name == "ToString") { receiver = CType.Utf8Bytes; result = CType.String; ints = 0; }
        else
        {
            CFail.Report($"unsupported TinySystem.Text member: {name}");
            return CType.Void;
        }
        RequireArity(call.Callee, call.Args.Count, ints + 1);
        if (CFail.Failed) return CType.Void;
        RequireType(receiver, TypeOf(call.Args[0]), call.Callee);
        for (var i = 1; i < call.Args.Count; i++) RequireType(CType.I32, TypeOf(call.Args[i]), call.Callee);
        return result;
    }

    private string RenderUtf8Op(IlCall call, CType type)
    {
        var name = Strings.From(call.Callee, Strings.ByteLength("Utf8."));
        var bytes = CType.Utf8Bytes.CName;
        var fieldText = Names.Field("Text");
        var fieldStart = Names.Field("Start");
        var fieldLength = Names.Field("Length");
        switch (name)
        {
            case "Default":
                return $"(({bytes}){{ 0 }})";
            case "RuneCount":
                return $"tcs_string_rune_count({RenderExpr(call.Args[0])})";
            case "RuneSubstring":
                {
                    var values = new List<TypedValue>();
                    values.Add(new TypedValue(CType.String, RenderExpr(call.Args[0])));
                    values.Add(new TypedValue(CType.I32, RenderExpr(call.Args[1])));
                    values.Add(new TypedValue(CType.I32, RenderExpr(call.Args[2])));
                    return RenderOrderedCall("tcs_string_rune_substring", type, values);
                }
            case "New":
                {
                    var text = Temp("utf8_text");
                    return $"({{ TcsString *{text} = {RenderExpr(call.Args[0])}; " +
                        $"int32_t {text}_length = tcs_utf8_bytes_text_length({text}); " +
                        $"({bytes}){{ .{fieldText} = {text}, .{fieldStart} = 0, .{fieldLength} = {text}_length }}; }})";
                }
        }
        // The receiver and arguments are evaluated once each, in this order
        var u = Temp("utf8");
        var head = $"{bytes} {u} = {RenderExpr(call.Args[0])}; ";
        var view = $"{u}.{fieldText}, {u}.{fieldStart}, {u}.{fieldLength}";
        switch (name)
        {
            case "At":
                {
                    var index = Temp("utf8_index");
                    return $"({{ {head}int32_t {index} = {RenderExpr(call.Args[1])}; " +
                        $"tcs_utf8_bytes_at({view}, {index}); }})";
                }
            case "Slice":
                {
                    var start = Temp("utf8_start");
                    var count = Temp("utf8_count");
                    return $"({{ {head}int32_t {start} = {RenderExpr(call.Args[1])}; " +
                        $"int32_t {count} = {RenderExpr(call.Args[2])}; " +
                        $"tcs_utf8_bytes_check_slice({u}.{fieldLength}, {start}, {count}); " +
                        $"({bytes}){{ .{fieldText} = {u}.{fieldText}, .{fieldStart} = {u}.{fieldStart} + {start}, .{fieldLength} = {count} }}; }})";
                }
            default:
                return $"({{ {head}tcs_utf8_bytes_to_string({view}); }})";
        }
    }

    // ---- Random (TinySystem.Random: instance + Shared. The runtime TcsRandom holds the
    // xoshiro256** of il-spec §13. With a fixed seed it gives the same sequence as the tcs VM) ----
    private bool IsRandomTypeName(string name) =>
        name == "TinySystem.Random"
        || (name == "Random" && !_classes.ContainsKey("Random"));

    // Static form IlCall("Random.X"): Seed reseeds Shared, the others go through Shared
    private CType TypeOfRandomOp(IlCall call)
    {
        var name = Strings.From(call.Callee, Strings.ByteLength("Random."));
        if (name != "Seed") return TypeOfRandomMethod(name, call.Args);
        RequireArity(call.Callee, call.Args.Count, 1);
        if (CFail.Failed) return CType.Void;
        RequireType(CType.I32, TypeOf(call.Args[0]), "Random.Seed");
        return CType.Void;
    }

    private string RenderRandomOp(IlCall call, CType type)
    {
        var name = Strings.From(call.Callee, Strings.ByteLength("Random."));
        if (name != "Seed")
            return RenderRandomMethod("tcs_random_shared()", false, name, call.Args);
        return $"tcs_rand_seed(tcs_random_shared(), (uint32_t)({RenderExpr(call.Args[0])}), 0)";
    }

    private CType TypeOfRandomNew(IlNewObj creation)
    {
        if (creation.Args.Count > 1)
        {
            CFail.Report("Random: expected 0 or 1 constructor argument");
            return CType.Random;
        }
        if (creation.Args.Count == 1)
            RequireType(CType.I32, TypeOf(creation.Args[0]), "Random(seed)");
        return CType.Random;
    }

    private string RenderRandomNew(IlNewObj creation)
    {
        TypeOfRandomNew(creation);
        if (CFail.Failed) return "";
        if (creation.Args.Count == 0) return "tcs_random_new_auto()";
        return $"tcs_random_new((uint32_t)({RenderExpr(creation.Args[0])}))";
    }

    private CType TypeOfRandomMethod(string name, List<IlExpr> args)
    {
        switch (name)
        {
            case "NextFloat":
            case "NextSingle":
                RequireArity($"Random.{name}", args.Count, 0);
                return CType.F32;
            case "Next":
                if (args.Count > 2)
                {
                    CFail.Report("Random.Next: expected 0-2 arguments");
                    return CType.Void;
                }
                foreach (var a in args) RequireType(CType.I32, TypeOf(a), "Random.Next");
                return CType.I32;
            case "Range":
                RequireArity("Random.Range", args.Count, 2);
                if (CFail.Failed) return CType.Void;
                RequireType(CType.I32, TypeOf(args[0]), "Random.Range");
                RequireType(CType.I32, TypeOf(args[1]), "Random.Range");
                return CType.I32;
            default:
                CFail.Report($"unsupported Random member: {name}");
                return CType.Void;
        }
    }

    // Bind the receiver to a temp first to preserve evaluation order.
    // When nullable (an instance receiver), null is checked after the arguments are evaluated (C# order)
    private string RenderRandomMethod(string receiver, bool nullable, string name,
        List<IlExpr> args)
    {
        var type = TypeOfRandomMethod(name, args);
        if (CFail.Failed) return "";
        var r = Temp("rng");
        var head = $"TcsRandom *{r} = {receiver}; ";
        var checkedR = nullable ? $"(TcsRandom *)tcs_nonnull({r})" : r;
        switch (name)
        {
            case "NextFloat":
            case "NextSingle":
                return $"({{ {head}tcs_rand_float({checkedR}); }})";
            case "Next":
                {
                    // Next() = tcs_rand_range(0, 2147483646), Next(max) =
                    // Next(0, max), Next(min, max) = tcs_rand_below (the upper bound is exclusive; min for an empty range, a fault for max < min)
                    if (args.Count == 0)
                        return $"({{ {head}tcs_rand_range({checkedR}, 0, INT32_C(2147483646)); }})";
                    if (args.Count == 1)
                    {
                        var one = new List<TypedValue>();
                        one.Add(new TypedValue(CType.Random, r));
                        one.Add(new TypedValue(CType.I32, "0"));
                        one.Add(new TypedValue(CType.I32, RenderExpr(args[0])));
                        return $"({{ {head}" + RenderOrderedCall("tcs_rand_below", type, one, null, nullable) + "; })";
                    }
                    var two = new List<TypedValue>();
                    two.Add(new TypedValue(CType.Random, r));
                    two.Add(new TypedValue(CType.I32, RenderExpr(args[0])));
                    two.Add(new TypedValue(CType.I32, RenderExpr(args[1])));
                    return $"({{ {head}" + RenderOrderedCall("tcs_rand_below", type, two, null, nullable) + "; })";
                }
            case "Range":
                {
                    var values = new List<TypedValue>();
                    values.Add(new TypedValue(CType.Random, r));
                    values.Add(new TypedValue(CType.I32, RenderExpr(args[0])));
                    values.Add(new TypedValue(CType.I32, RenderExpr(args[1])));
                    return $"({{ {head}" + RenderOrderedCall("tcs_rand_range", type, values, null, nullable) + "; })";
                }
            default:
                CFail.Report($"unsupported Random member: {name}");
                return "";
        }
    }

    // ---- Math.* (TinySystem.Math) ----
    // Type of a numeric (i32 / f32) argument (local function Num in the C# version)
    private CType MathNumericArg(string name, List<IlExpr> args, int i)
    {
        var type = TypeOf(args[i]);
        if (type.Kind == CTypeKind.I32 || type.Kind == CTypeKind.F32) return type;
        CFail.Report($"Math.{name}: argument {i} is not numeric");
        return CType.Void;
    }

    private CType TypeOfMathOp(IlCall call)
    {
        var name = Strings.From(call.Callee, Strings.ByteLength("Math."));
        var args = call.Args;
        switch (name)
        {
            case "Min":
            case "Max":
                RequireArity(call.Callee, args.Count, 2);
                if (CFail.Failed) return CType.Void;
                return NumericJoin(MathNumericArg(name, args, 0), MathNumericArg(name, args, 1), name);
            case "Clamp":
                RequireArity(call.Callee, args.Count, 3);
                if (CFail.Failed) return CType.Void;
                return NumericJoin(NumericJoin(MathNumericArg(name, args, 0), MathNumericArg(name, args, 1), name),
                    MathNumericArg(name, args, 2), name);
            case "Abs":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                return MathNumericArg(name, args, 0);
            case "Sign":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                MathNumericArg(name, args, 0);
                return CType.I32;
            case "CompareTo":
                RequireArity(call.Callee, args.Count, 2);
                if (CFail.Failed) return CType.Void;
                NumericJoin(MathNumericArg(name, args, 0), MathNumericArg(name, args, 1), name);
                return CType.I32;
            case "Floor":
            case "Ceil":
            case "Ceiling":
            case "Sqrt":
            case "Sin":
            case "Cos":
            case "Tan":
            case "Exp":
                RequireArity(call.Callee, args.Count, 1);
                if (CFail.Failed) return CType.Void;
                MathNumericArg(name, args, 0);
                return CType.F32;
            case "Atan2":
            case "Pow":
                RequireArity(call.Callee, args.Count, 2);
                if (CFail.Failed) return CType.Void;
                MathNumericArg(name, args, 0); MathNumericArg(name, args, 1);
                return CType.F32;
            case "Log":
                if (args.Count != 1 && args.Count != 2)
                {
                    CFail.Report("Math.Log: expected 1 or 2 arguments");
                    return CType.Void;
                }
                for (var i = 0; i < args.Count; i++) MathNumericArg(name, args, i);
                return CType.F32;
            case "Round":
                if (args.Count != 1 && args.Count != 2)
                {
                    CFail.Report("Math.Round: expected 1 or 2 arguments");
                    return CType.Void;
                }
                MathNumericArg(name, args, 0);
                if (args.Count == 2) RequireType(CType.I32, TypeOf(args[1]), "Round digits");
                return CType.F32;
            default:
                CFail.Report($"unsupported Math member: {name}");
                return CType.Void;
        }
    }

    // Render f32 arguments in order (local function F in the C# version)
    private List<TypedValue> MathF32Args(List<IlExpr> args, int count)
    {
        var values = new List<TypedValue>();
        for (var i = 0; i < count; i++) values.Add(new TypedValue(CType.F32, RenderExpr(args[i])));
        return values;
    }

    private string RenderMathOp(IlCall call, CType type)
    {
        var name = Strings.From(call.Callee, Strings.ByteLength("Math."));
        var args = call.Args;
        switch (name)
        {
            case "Min":
                {
                    if (type.Kind == CTypeKind.F32)
                        return RenderOrderedCall("tcs_math_min", type, MathF32Args(args, 2));
                    var a = Temp("min_a"); var b = Temp("min_b");
                    return $"({{ {type.CName} {a} = {RenderExpr(args[0])}; {type.CName} {b} = " +
                        $"{RenderExpr(args[1])}; {b} < {a} ? {b} : {a}; }})";
                }
            case "Max":
                {
                    if (type.Kind == CTypeKind.F32)
                        return RenderOrderedCall("tcs_math_max", type, MathF32Args(args, 2));
                    var a = Temp("max_a"); var b = Temp("max_b");
                    return $"({{ {type.CName} {a} = {RenderExpr(args[0])}; {type.CName} {b} = " +
                        $"{RenderExpr(args[1])}; {b} > {a} ? {b} : {a}; }})";
                }
            case "Clamp":
                {
                    var values = new List<TypedValue>();
                    for (var i = 0; i < 3; i++) values.Add(new TypedValue(type, RenderExpr(args[i])));
                    return RenderOrderedCall(type.Kind == CTypeKind.F32 ? "tcs_math_clamp_f32" : "tcs_math_clamp_i32", type, values);
                }
            case "Abs":
                if (type.Kind == CTypeKind.I32) return $"tcs_math_abs_i32({RenderExpr(args[0])})";
                return $"fabsf({RenderExpr(args[0])})";
            case "Sign":
                {
                    var vt = TypeOf(args[0]);
                    if (vt.Kind == CTypeKind.F32) return $"tcs_math_sign_f32({RenderExpr(args[0])})";
                    var v = Temp("sign");
                    return $"({{ {vt.CName} {v} = {RenderExpr(args[0])}; " +
                        $"{v} > 0 ? INT32_C(1) : {v} < 0 ? -INT32_C(1) : INT32_C(0); }})";
                }
            case "CompareTo":
                {
                    // -1 / 0 / 1. A float NaN is smaller than any other value, and two NaNs give 0
                    var vt = NumericJoin(TypeOf(args[0]), TypeOf(args[1]), name);
                    var a = Temp("cmp_a"); var b = Temp("cmp_b");
                    var unordered = vt.Kind == CTypeKind.F32
                        ? $"{a} == {b} ? INT32_C(0) : {a} != {a} ? ({b} != {b} ? INT32_C(0) : -INT32_C(1)) : INT32_C(1)"
                        : "INT32_C(0)";
                    return $"({{ {vt.CName} {a} = {RenderCoerced(args[0], vt)}; " +
                        $"{vt.CName} {b} = {RenderCoerced(args[1], vt)}; " +
                        $"{a} < {b} ? -INT32_C(1) : {a} > {b} ? INT32_C(1) : {unordered}; }})";
                }
            case "Floor": return $"floorf({RenderExpr(args[0])})";
            case "Ceil":
            case "Ceiling": return $"ceilf({RenderExpr(args[0])})";
            case "Sqrt": return $"sqrtf({RenderExpr(args[0])})";
            case "Sin": return $"tcs_sinf({RenderExpr(args[0])})";
            case "Cos": return $"tcs_cosf({RenderExpr(args[0])})";
            case "Tan": return $"tcs_tanf({RenderExpr(args[0])})";
            case "Exp": return $"tcs_expf({RenderExpr(args[0])})";
            case "Atan2": return RenderOrderedCall("tcs_atan2f", type, MathF32Args(args, 2));
            case "Pow": return RenderOrderedCall("tcs_math_pow", type, MathF32Args(args, 2));
            case "Log":
                {
                    if (args.Count == 2)
                    {
                        var values = MathF32Args(args, 2);
                        values.Add(new TypedValue(CType.I32, "1"));
                        return RenderOrderedCall("tcs_math_log", type, values);
                    }
                    return $"tcs_math_log({RenderExpr(args[0])}, 0.0f, 0)";
                }
            case "Round":
                {
                    if (args.Count == 2)
                    {
                        var values = MathF32Args(args, 1);
                        values.Add(new TypedValue(CType.I32, RenderExpr(args[1])));
                        values.Add(new TypedValue(CType.I32, "1"));
                        return RenderOrderedCall("tcs_math_round", type, values);
                    }
                    return $"tcs_math_round({RenderExpr(args[0])}, 0, 0)";
                }
            default:
                CFail.Report($"unsupported Math member: {name}");
                return "";
        }
    }

    // ==== CEmitter.EnumNames ====
    // Enum stringification `String.FormatEnum(v, spec)` (il-spec §13). For each spec, build only one pair of file-scope tables of names
    // (string literals) and values, and the runtime
    // tcs_enum_format looks them up by the String.FormatEnum rules (il-spec §13)

    private readonly Dictionary<string, EnumTableInfo> _enumTables = new Dictionary<string, EnumTableInfo>();

    private CType TypeOfFormatEnum(IlCall call)
    {
        RequireArity(call.Callee, call.Args.Count, 2);
        if (CFail.Failed) return CType.Void;
        var value = TypeOf(call.Args[0]);
        if (!CType.Same(value, CType.I32) && !CType.Same(value, CType.Nullable(CType.I32)))
        {
            CFail.Report($"String.FormatEnum: value is not an enum ({value.Display()})");
            return CType.Void;
        }
        if (!(call.Args[1] is IlLit))
        {
            CFail.Report("String.FormatEnum: spec is not a literal");
            return CType.Void;
        }
        return CType.String;
    }

    // Local function Format in the C# version
    private static string EnumFormatText(string v, EnumTableInfo table)
    {
        return $"tcs_enum_format({v}, {EnumTableArgs(table)})";
    }

    // The table arguments of tcs_enum_format (names, values, count, Flags, unsigned)
    private static string EnumTableArgs(EnumTableInfo table)
    {
        var flags = table.Flags ? "true" : "false";
        var isUnsigned = table.Unsigned ? "true" : "false";
        return $"{table.Names}, {table.Values}, {table.Count}, {flags}, {isUnsigned}";
    }

    private string RenderFormatEnum(IlCall call)
    {
        var table = EnumTable(((IlLit)call.Args[1]).S);
        var type = TypeOf(call.Args[0]);
        if (type.Kind != CTypeKind.Nullable) return EnumFormatText(RenderExpr(call.Args[0]), table);
        var temp = Temp("opt");
        var value = RenderExpr(call.Args[0]);
        var formatted = EnumFormatText($"{temp}.v", table);
        var empty = InternStringLiteral(new List<int>());
        return $"({{ {type.CName} {temp} = {value}; " +
            $"{temp}.has ? {formatted} : {empty}; }})";
    }

    private EnumTableInfo EnumTable(string specText)
    {
        EnumTableInfo? existing;
        if (_enumTables.TryGetValue(specText, out existing)) return existing;
        // Read spec as a UTF-8 byte sequence (',' and ':' never appear inside multi-byte characters, so this matches the character split in the C# version)
        var spec = Utf8List(specText);
        // Leading marks: `*` is Flags, and a following `+` is unsigned (a uint enum)
        var flags = spec.Count > 0 && spec[0] == '*';
        var segmentStart = flags ? 1 : 0;
        var isUnsigned = segmentStart < spec.Count && spec[segmentStart] == '+';
        if (isUnsigned) segmentStart++;
        var entryNames = new List<List<int>>();
        var entryValues = new List<int>();
        for (var i = segmentStart; i <= spec.Count; i++)
        {
            if (i < spec.Count && spec[i] != ',') continue;
            // Skip empty entries (StringSplitOptions.RemoveEmptyEntries)
            if (i > segmentStart)
            {
                var colon = -1;
                for (var j = segmentStart; j < i; j++)
                {
                    if (spec[j] == ':') colon = j;
                }
                if (colon < 0)
                {
                    CFail.Report($"invalid enum format spec: {specText}");
                }
                else
                {
                    var name = new List<int>();
                    for (var j = segmentStart; j < colon; j++) name.Add(spec[j]);
                    entryNames.Add(name);
                    entryValues.Add(EnumSpecValue(spec, colon + 1, i, specText));
                }
            }
            segmentStart = i + 1;
        }
        var id = _enumTables.Count;
        var names = $"tcs_enum_names_{id}";
        var values = $"tcs_enum_values_{id}";
        var literals = new List<string>();
        foreach (var n in entryNames) literals.Add(InternStringLiteral(n));
        var valueTexts = new List<string>();
        foreach (var v in entryValues) valueTexts.Add(I32Literal(v));
        // C cannot express a zero-length array, so an enum with no members also gets one element
        var nameList = literals.Count == 0 ? "NULL" : string.Join(", ", literals);
        _literalDecls.Add($"static TcsString *const {names}[] = {{ " +
            $"{nameList} }};");
        var valueList = entryValues.Count == 0 ? "0" : string.Join(", ", valueTexts);
        _literalDecls.Add($"static const int32_t {values}[] = {{ " +
            $"{valueList} }};");
        var table = new EnumTableInfo(names, values, entryValues.Count, flags, isUnsigned);
        _enumTables[specText] = table;
        return table;
    }

    // Decimal integer in bytes [start, end) of spec (a leading sign is allowed; int.Parse(NumberStyles.AllowLeadingSign) in the C# version).
    // Accumulates on the negative side, so even int.MinValue does not overflow
    private static int EnumSpecValue(List<int> spec, int start, int end, string specText)
    {
        var negative = false;
        var i = start;
        if (i < end && (spec[i] == '-' || spec[i] == '+'))
        {
            negative = spec[i] == '-';
            i++;
        }
        if (i >= end)
        {
            CFail.Report($"invalid enum format spec: {specText}");
            return 0;
        }
        var value = 0;
        for (; i < end; i++)
        {
            var c = spec[i];
            if (c < '0' || c > '9')
            {
                CFail.Report($"invalid enum format spec: {specText}");
                return 0;
            }
            value = value * 10 - (c - '0');
        }
        return negative ? value : -value;
    }

    private static string I32Literal(int value) =>
        value == int.MinValue ? "INT32_MIN" : $"INT32_C({value})";

    // ---- Closures ----

    // ==== CEmitter.Closures.cs ====

    // ---- closure: capture-by-variable (il-spec §7) ----
    // Collect the names captured by closures directly in the body of a function (method / closure), and box that
    // function's locals/params into heap cells (T *). The decision is by name, so
    // a different variable with the same name may also be boxed, but over-boxing is harmless (semantically equivalent)
    private void CollectCapturedNames(IlBlock body)
    {
        _capturedNames.Clear();
        AddCapturedNames(body);
    }

    private void AddCapturedNames(IlBlock body)
    {
        foreach (var name in CapturedByClosuresIn(body.Stats, null))
            _capturedNames.Add(name);
    }

    // Names that closures in the body (FreeNames handles the closures nested inside them) reference from
    // outside
    private static HashSet<string> CapturedByClosuresIn(List<IlStat> stats,
        IlExpr? exprBody)
    {
        var w = new ClosureWalk(true);
        var none = new HashSet<string>();
        if (exprBody != null) CapturedExpr(exprBody, w, none);
        foreach (var st in stats) WalkStat(st, w, none);
        return w.CapturedSet;
    }

    // Expression visitor of CapturedByClosuresIn (local function E in the C# version)
    private static void CapturedExpr(IlExpr e, ClosureWalk w, HashSet<string> none)
    {
        if (e is IlClosure closure)
        {
            foreach (var name in FreeNames(closure)) w.CapturedSet.Add(name);
        }
        else Walk(e, w, none);
    }

    // Names a closure references as outer variables (including those referenced by nested
    // closures). In order of first appearance. Locals / loop variables inside a closure can have the same name
    // as an outer variable (shadowing), so declarations are bound per block
    private static List<string> FreeNames(IlClosure closure)
    {
        var w = new ClosureWalk(false);
        var parameters = new HashSet<string>();
        foreach (var p in closure.Params) parameters.Add(p);
        if (closure.ExprBody != null) FreeExpr(closure.ExprBody, w, parameters);
        if (closure.Body != null) FreeBlock(closure.Body.Stats, parameters, null, null, w);
        return w.Free;
    }

    // Expression visitor of FreeNames (local function E in the C# version)
    private static void FreeExpr(IlExpr e, ClosureWalk w, HashSet<string> bound)
    {
        switch (e)
        {
            case IlVar v:
                if (!bound.Contains(v.Name) && !w.Free.Contains(v.Name))
                    w.Free.Add(v.Name);
                break;
            case IlClosure nested:
                foreach (var name in FreeNames(nested))
                    FreeExpr(new IlVar(name, null), w, bound);
                break;
            case IlIife iife: FreeBlock(iife.Stats, bound, null, null, w); break;
            default: Walk(e, w, bound); break;
        }
    }

    // Bring in a copy of the outer block's bindings, and do not leak inner declarations outward
    // (local function Block in the C# version; declared holds 0 to 2 names, and null means no declaration)
    private static void FreeBlock(List<IlStat> stats, HashSet<string> outer,
        string? declared1, string? declared2, ClosureWalk w)
    {
        var bound = new HashSet<string>();
        foreach (var name in outer) bound.Add(name);
        if (declared1 != null) bound.Add(declared1);
        if (declared2 != null) bound.Add(declared2);
        foreach (var st in stats) FreeStat(st, w, bound);
    }

    // Statement visitor of FreeNames (local function S in the C# version)
    private static void FreeStat(IlStat st, ClosureWalk w, HashSet<string> bound)
    {
        switch (st)
        {
            case IlLocal l:
                if (l.Init != null) FreeExpr(l.Init, w, bound);
                bound.Add(l.Name);
                break;
            case IlIf i:
                foreach (var arm in i.Arms)
                {
                    FreeExpr(arm.Cond, w, bound);
                    FreeBlock(arm.Body.Stats, bound, null, null, w);
                }
                if (i.Else != null) FreeBlock(i.Else.Stats, bound, null, null, w);
                break;
            case IlWhile wh:
                FreeExpr(wh.Cond, w, bound);
                FreeBlock(wh.Body.Stats, bound, null, null, w);
                if (wh.Trailer != null) FreeBlock(wh.Trailer.Stats, bound, null, null, w);
                break;
            case IlRepeat rp:
                FreeBlock(rp.Body.Stats, bound, null, null, w);
                FreeExpr(rp.Cond, w, bound);
                break;
            case IlNumericFor nf:
                FreeExpr(nf.Start, w, bound); FreeExpr(nf.Limit, w, bound);
                FreeBlock(nf.Body.Stats, bound, nf.Var, null, w);
                break;
            case IlForeachList fl:
                FreeExpr(fl.Coll, w, bound);
                FreeBlock(fl.Body.Stats, bound, fl.Var, null, w);
                break;
            case IlForeachDict fd:
                FreeExpr(fd.Coll, w, bound);
                FreeBlock(fd.Body.Stats, bound, fd.Var, null, w);
                break;
            case IlForeachRunes fr:
                FreeExpr(fr.Str, w, bound);
                FreeBlock(fr.Body.Stats, bound, fr.Var, null, w);
                break;
            case IlForPairs fp:
                FreeExpr(fp.Coll, w, bound);
                FreeBlock(fp.Body.Stats, bound, fp.KVar, fp.VVar, w);
                break;
            case IlDo d: FreeBlock(d.Body.Stats, bound, null, null, w); break;
            case IlBreakScope bs: FreeBlock(bs.Body.Stats, bound, null, null, w); break;
            default: WalkStat(st, w, bound); break;
        }
    }

    // Where Walk / WalkStat pass child expressions and statements (Action<IlExpr> expr / Action<IlStat> stat in the C# version)
    private static void WalkExpr(IlExpr e, ClosureWalk w, HashSet<string> bound)
    {
        if (w.Captured) CapturedExpr(e, w, bound);
        else FreeExpr(e, w, bound);
    }

    private static void WalkChildStat(IlStat st, ClosureWalk w, HashSet<string> bound)
    {
        if (w.Captured) WalkStat(st, w, bound);
        else FreeStat(st, w, bound);
    }

    private static void Walk(IlExpr e, ClosureWalk w, HashSet<string> bound)
    {
        switch (e)
        {
            case IlField f: WalkExpr(f.Recv, w, bound); break;
            case IlIndex ix: WalkExpr(ix.Recv, w, bound); WalkExpr(ix.Idx, w, bound); break;
            case IlLen l: WalkExpr(l.E, w, bound); break;
            case IlBin b: WalkExpr(b.L, w, bound); WalkExpr(b.R, w, bound); break;
            case IlUn u: WalkExpr(u.E, w, bound); break;
            case IlParen p: WalkExpr(p.E, w, bound); break;
            case IlTernary t:
                WalkExpr(t.Cond, w, bound); WalkExpr(t.T, w, bound); WalkExpr(t.F, w, bound);
                break;
            case IlCall c: foreach (var a in c.Args) WalkExpr(a, w, bound); break;
            case IlDynCall d:
                WalkExpr(d.Callee, w, bound); foreach (var a in d.Args) WalkExpr(a, w, bound); break;
            case IlInvoke i:
                WalkExpr(i.Recv, w, bound); foreach (var a in i.Args) WalkExpr(a, w, bound); break;
            case IlNewObj n: foreach (var a in n.Args) WalkExpr(a, w, bound); break;
            case IlNewArray na: WalkExpr(na.Length, w, bound); break;
            case IlTable tb:
                foreach (var en in tb.Entries)
                {
                    if (en.Key != null) WalkExpr(en.Key, w, bound);
                    WalkExpr(en.Value, w, bound);
                }
                break;
            case IlIsType it: WalkExpr(it.E, w, bound); break;
            case IlStructCopy sc: WalkExpr(sc.E, w, bound); break;
            case IlCast cast: WalkExpr(cast.E, w, bound); break;
            case IlNumericConvert convert: WalkExpr(convert.Value, w, bound); break;
            case IlRefCast refCast: WalkExpr(refCast.Value, w, bound); break;
            case IlNullableWrap nw: WalkExpr(nw.E, w, bound); break;
            case IlNullableHasValue h: WalkExpr(h.E, w, bound); break;
            case IlNullableValue v: WalkExpr(v.E, w, bound); break;
            case IlNullableGetOrDefault g: WalkExpr(g.E, w, bound); WalkExpr(g.Default, w, bound); break;
            case IlLiftedBin lb: WalkExpr(lb.L, w, bound); WalkExpr(lb.R, w, bound); break;
            case IlLiftedUn lu: WalkExpr(lu.E, w, bound); break;
            case IlWith wi:
                WalkExpr(wi.Src, w, bound);
                foreach (var o in wi.Overrides) WalkExpr(o.Value, w, bound);
                break;
            case IlIife iife: foreach (var st in iife.Stats) WalkChildStat(st, w, bound); break;
        }
    }

    private static void WalkStat(IlStat st, ClosureWalk w, HashSet<string> bound)
    {
        switch (st)
        {
            case IlLocal l:
                if (l.Init != null) WalkExpr(l.Init, w, bound);
                break;
            case IlAssign a: WalkExpr(a.Target, w, bound); WalkExpr(a.Value, w, bound); break;
            case IlMultiAssign m:
                foreach (var t in m.Targets) WalkExpr(t, w, bound);
                foreach (var v in m.Values) WalkExpr(v, w, bound);
                break;
            case IlCallStat c: WalkExpr(c.Call, w, bound); break;
            case IlReturn r:
                if (r.Value != null) WalkExpr(r.Value, w, bound);
                break;
            case IlIf i:
                foreach (var arm in i.Arms)
                {
                    WalkExpr(arm.Cond, w, bound);
                    foreach (var b in arm.Body.Stats) WalkChildStat(b, w, bound);
                }
                if (i.Else != null)
                    foreach (var b in i.Else.Stats) WalkChildStat(b, w, bound);
                break;
            case IlWhile wh:
                WalkExpr(wh.Cond, w, bound);
                foreach (var b in wh.Body.Stats) WalkChildStat(b, w, bound);
                if (wh.Trailer != null)
                    foreach (var b in wh.Trailer.Stats) WalkChildStat(b, w, bound);
                break;
            case IlRepeat rp:
                foreach (var b in rp.Body.Stats) WalkChildStat(b, w, bound);
                WalkExpr(rp.Cond, w, bound);
                break;
            case IlNumericFor nf:
                WalkExpr(nf.Start, w, bound); WalkExpr(nf.Limit, w, bound);
                foreach (var b in nf.Body.Stats) WalkChildStat(b, w, bound);
                break;
            case IlForeachList fl:
                WalkExpr(fl.Coll, w, bound);
                foreach (var b in fl.Body.Stats) WalkChildStat(b, w, bound);
                break;
            case IlForeachDict fd:
                WalkExpr(fd.Coll, w, bound);
                foreach (var b in fd.Body.Stats) WalkChildStat(b, w, bound);
                break;
            case IlForeachRunes fr:
                WalkExpr(fr.Str, w, bound);
                foreach (var b in fr.Body.Stats) WalkChildStat(b, w, bound);
                break;
            case IlForPairs fp:
                WalkExpr(fp.Coll, w, bound);
                foreach (var b in fp.Body.Stats) WalkChildStat(b, w, bound);
                break;
            case IlDo d:
                foreach (var b in d.Body.Stats) WalkChildStat(b, w, bound);
                break;
            case IlBreakScope bs:
                foreach (var b in bs.Body.Stats) WalkChildStat(b, w, bound);
                break;
        }
    }

    private void BoxCapturedParameters()
    {
        var scope = _scopes[_scopes.Count - 1];
        foreach (var key in _scopeOrder[_scopeOrder.Count - 1])
        {
            if (!_capturedNames.Contains(key)) continue;
            var old = scope[key];
            if (old.Boxed || old.Type.Kind == CTypeKind.Kvp) continue;
            var cell = new Variable($"c_{old.CName}", old.Type);
            cell.Boxed = true;
            Line($"{old.Type.CName} *{cell.CName} = " +
                $"tcs_new_cell(sizeof(*{cell.CName}), {LayoutRef(old.Type)});");
            Line($"*{cell.CName} = {old.CName};");
            scope[key] = cell;
        }
    }

    private void FlushPendingClosures()
    {
        foreach (var code in _pendingClosures) _output.Add(code);
        _pendingClosures.Clear();
    }

    private static string ClosureFnPtrType(CType closure, string name = "")
    {
        var parts = new List<string>();
        parts.Add("void **");
        foreach (var p in closure.Parameters!) parts.Add(p.CName);
        var parameters = string.Join(", ", parts);
        return $"{closure.Element!.CName} (*{name})({parameters})";
    }

    // IlClosure → lifted static function + cell bindings. A closure value is
    // TcsClosure { fn, cells[] }
    private string RenderClosure(IlClosure closure, CType target)
    {
        var targetParams = target.Parameters!;
        if (closure.Params.Count != targetParams.Count)
        {
            CFail.Report("closure arity mismatch with target type");
            return "";
        }
        var fnName = $"tcs_closure_{_closureSerial++}";
        // Captures = the enclosing boxed variables referenced by the closure body and copies of iteration
        // KeyValuePairs. Those referenced by nested closures are included too, since they are passed in through the lifted function's
        // cells
        var capturedNames = new List<string>();
        var capturedCells = new List<Variable>();
        foreach (var name in FreeNames(closure))
        {
            var cell = TryResolve(name);
            if (cell != null && (cell.Boxed || cell.Snapshot))
            {
                capturedNames.Add(name);
                capturedCells.Add(cell);
            }
        }

        // Emit the lifted function to a side buffer (does not pollute the current function)
        var declParts = new List<string>();
        declParts.Add("void **");
        foreach (var p in targetParams) declParts.Add(p.CName);
        var declParams = string.Join(", ", declParts);
        _closureDecls.Add(
            $"static {target.Element!.CName} {fnName}({declParams});");
        var saved = _output.Count;
        var savedIndent = _indent;
        // Do not carry the outer IIFE's return binding into the closure body
        var savedIifes = _iifes;
        _iifes = new List<IifeFrame>();
        _indent = 0;
        Line($"static {target.Element!.CName}");
        var paramParts = new List<string>();
        paramParts.Add("void **cells");
        for (var i = 0; i < closure.Params.Count; i++)
            paramParts.Add($"{targetParams[i].CName} v_{Names.Id(closure.Params[i])}_{i}");
        var paramList = string.Join(", ", paramParts);
        Line($"{fnName}({paramList})");
        Line("{");
        _indent++;
        PushScope();
        for (var i = 0; i < closure.Params.Count; i++)
            AddVariable(closure.Params[i],
                new Variable($"v_{Names.Id(closure.Params[i])}_{i}",
                    targetParams[i]));
        for (var i = 0; i < capturedNames.Count; i++)
        {
            var name = capturedNames[i];
            var outer = capturedCells[i];
            var cellVar = new Variable($"cell_{Names.Id(name)}", outer.Type);
            cellVar.Boxed = outer.Boxed;
            cellVar.Snapshot = outer.Snapshot;
            var pointer = outer.Snapshot ? "TcsDictNode *" : $"{outer.Type.CName} *";
            Line($"{pointer}{cellVar.CName} = ({pointer})cells[{i}];");
            AddVariable(name, cellVar);
        }
        // Turn parameters / locals captured by closures inside this closure into cells, the same as
        // for methods
        var outerCaptured = _capturedNames;
        _capturedNames = CapturedByClosuresIn(
            closure.Body != null ? closure.Body.Stats : new List<IlStat>(),
            closure.ExprBody);
        BoxCapturedParameters();
        if (closure.ExprBody != null)
        {
            if (CType.Same(target.Element, CType.Void))
                Line($"{RenderExpr(closure.ExprBody)};");
            else
                Line($"return {RenderCoerced(closure.ExprBody, target.Element!)};");
        }
        else
        {
            var outerFact = _currentMethodFact;
            var paramFacts = new List<ParameterFact>();
            for (var i = 0; i < closure.Params.Count; i++)
                paramFacts.Add(new ParameterFact(closure.Params[i], targetParams[i], null));
            _currentMethodFact = new MethodFact("<closure>", fnName, true,
                target.Element!, paramFacts, null!);
            EmitStats(closure.Body!.Stats);
            _currentMethodFact = outerFact;
        }
        _capturedNames = outerCaptured;
        PopScope();
        _indent--;
        Line("}");
        Line();
        _indent = savedIndent;
        _iifes = savedIifes;
        var codeParts = new List<string>();
        for (var i = saved; i < _output.Count; i++) codeParts.Add(_output[i]);
        var code = string.Join("", codeParts);
        while (_output.Count > saved) _output.RemoveAt(_output.Count - 1);
        _pendingClosures.Add(code);

        var make = new List<string>();
        var closTemp = Temp("closure");
        make.Add($"TcsClosure *{closTemp} = tcs_new_closure(" +
            $"(void *){fnName}, {capturedNames.Count}); ");
        for (var i = 0; i < capturedCells.Count; i++)
            make.Add($"{closTemp}->cells[{i}] = (void *){capturedCells[i].CName}; ");
        return $"({{ {string.Join("", make)}{closTemp}; }})";
    }

    private readonly Dictionary<string, string> _groupThunks = new Dictionary<string, string>();

    // static method group → a thunk closure that passes arguments through (no cells)
    private string RenderStaticGroupThunk(string cls, string method,
        CType target)
    {
        var fact = _facts.Method(cls, method);
        if (fact.Parameters.Count != target.Parameters!.Count)
        {
            CFail.Report($"method group arity mismatch: {cls}.{method}");
            return "";
        }
        var key = $"{cls}.{method}";
        string? found;
        string fnName = "";
        if (_groupThunks.TryGetValue(key, out found)) fnName = found!;
        else
        {
            fnName = $"tcs_thunk_{Names.Id(cls)}_{Names.Id(method)}";
            _groupThunks[key] = fnName;
            var declParts = new List<string>();
            declParts.Add("void **");
            foreach (var p in fact.Parameters) declParts.Add(p.Type.CName);
            _closureDecls.Add($"static {fact.ReturnType.CName} {fnName}(" +
                string.Join(", ", declParts) + ");");
            var paramParts = new List<string>();
            paramParts.Add("void **cells");
            for (var i = 0; i < fact.Parameters.Count; i++)
                paramParts.Add($"{fact.Parameters[i].Type.CName} a{i}");
            var parameters = string.Join(", ", paramParts);
            var argParts = new List<string>();
            for (var i = 0; i < fact.Parameters.Count; i++) argParts.Add($"a{i}");
            var args = string.Join(", ", argParts);
            var call = $"{Names.Method(cls, method)}({args})";
            var body = CType.Same(fact.ReturnType, CType.Void)
                ? $"(void)cells; {call};" : $"(void)cells; return {call};";
            _pendingClosures.Add(
                $"static {fact.ReturnType.CName}\n{fnName}({parameters})\n" +
                "{\n    " + body + "\n}\n\n");
        }
        var closTemp = Temp("closure");
        return $"({{ TcsClosure *{closTemp} = tcs_new_closure(" +
            $"(void *){fnName}, 0); {closTemp}; }})";
    }

    // ==== CEmitter.Nullable.cs ====

    // Nullable<T> (il-spec §13): { bool has; T v; } in C. Explicit IL nodes
    // (Wrap / HasValue / Value / GetOrDefault / Lifted) are lowered one-to-one to statement
    // expressions. A lifted operation has no value if either side has no value, comparisons are false,
    // Eq / Ne treat two missing values as equal, and bool? And / Or use three-valued logic.

    private CType RequireNullable(IlExpr expr, string where)
    {
        var type = TypeOf(expr);
        if (type.Kind != CTypeKind.Nullable)
        {
            CFail.Report($"{where}: operand is not Nullable<T> ({type.Display()})");
            // So that the caller can still read Element after an error
            return CType.Nullable(CType.I32);
        }
        return type;
    }

    private static bool IsNullableElementKind(CTypeKind kind) =>
        kind == CTypeKind.I32 || kind == CTypeKind.F32 || kind == CTypeKind.Bool
            || kind == CTypeKind.StructVal;

    private CType TypeOfNullableWrap(IlNullableWrap wrap)
    {
        var declared = _facts.MapType(wrap.Type);
        if (declared.Kind == CTypeKind.Nullable) return declared;
        var element = IsNullableElementKind(declared.Kind) ? declared : TypeOf(wrap.E);
        if (!IsNullableElementKind(element.Kind))
        {
            CFail.Report($"IlNullableWrap of non-value type: {element.Display()}");
            return CType.Nullable(element);
        }
        CheckAssignable(element, wrap.E, "nullable wrap");
        return CType.Nullable(element);
    }

    private string RenderNullableWrap(IlNullableWrap wrap)
    {
        var type = TypeOfNullableWrap(wrap);
        return $"(({type.CName}){{ true, {RenderCoerced(wrap.E, type.Element!)} }})";
    }

    private string RenderNullableValue(IlNullableValue val)
    {
        var type = RequireNullable(val.E, "Value");
        var temp = Temp("opt");
        return $"({{ {type.CName} {temp} = {RenderExpr(val.E)}; " +
            $"if (!{temp}.has) tcs_fault(\"nullable-value\"); {temp}.v; }})";
    }

    private CType TypeOfNullableGetOrDefault(IlNullableGetOrDefault gd)
    {
        var type = RequireNullable(gd.E, "??");
        var fallback = TypeOf(gd.Default);
        if (fallback.Kind == CTypeKind.Nullable)
        {
            if (!CType.Same(fallback, type))
            {
                CFail.Report($"?? operand mismatch: {type.Display()}, {fallback.Display()}");
                return type;
            }
            return type;
        }
        if (fallback.Kind == CTypeKind.Null) return type;
        if ((type.Element!.Kind == CTypeKind.I32 || type.Element.Kind == CTypeKind.F32)
            && (fallback.Kind == CTypeKind.I32 || fallback.Kind == CTypeKind.F32))
            return NumericJoin(type.Element, fallback, "??");
        if (!type.Element.CanAssignFrom(fallback) && !CType.Same(type.Element, fallback))
        {
            CFail.Report($"?? fallback {fallback.Display()} is not assignable to {type.Element.Display()}");
            return type.Element;
        }
        return type.Element;
    }

    private string RenderNullableGetOrDefault(IlNullableGetOrDefault gd)
    {
        var type = RequireNullable(gd.E, "??");
        var result = TypeOfNullableGetOrDefault(gd);
        var temp = Temp("opt");
        if (result.Kind == CTypeKind.Nullable)
            return $"({{ {type.CName} {temp} = {RenderExpr(gd.E)}; " +
                $"{temp}.has ? {temp} : {RenderCoerced(gd.Default, type)}; }})";
        return $"({{ {type.CName} {temp} = {RenderExpr(gd.E)}; " +
            $"{temp}.has ? ({result.CName}){temp}.v : ({result.CName})({RenderCoerced(gd.Default, result)}); }})";
    }

    private string RenderNullableToString(IlExpr expr)
    {
        var type = RequireNullable(expr, "__tcs_nstr");
        var temp = Temp("opt");
        string helper;
        switch (type.Element!.Kind)
        {
            case CTypeKind.I32: helper = "tcs_string_i32"; break;
            case CTypeKind.F32: helper = "tcs_string_f32"; break;
            case CTypeKind.Bool: helper = "tcs_string_bool"; break;
            default:
                CFail.Report($"tostring does not support {type.Display()}");
                return "";
        }
        return $"({{ {type.CName} {temp} = {RenderExpr(expr)}; " +
            $"{temp}.has ? {helper}({temp}.v) : {InternStringLiteral(new List<int>())}; }})";
    }

    // Equality of T? with T? / T? with T / T? with null. The default is lifted Eq (IEEE
    // == for float); equals is Nullable<T>.Equals (inner values via EqualityComparer<T>.Default)
    private string NullableEqualExpr(CType type, string left, string right,
        bool equals = false)
    {
        string inner;
        if (!equals && type.Element!.Kind == CTypeKind.F32)
            inner = $"({left}.v == {right}.v)";
        else
            inner = FieldEqualExpr(type.Element!, $"{left}.v", $"{right}.v");
        return $"({left}.has == {right}.has && (!{left}.has || {inner}))";
    }

    private string RenderNullableEquality(IlBin binary, CType leftType, CType rightType)
    {
        var negate = binary.Op == IlBinOp.Ne ? "!" : "";
        var nullable = leftType.Kind == CTypeKind.Nullable ? leftType : rightType;
        var l = Temp("eq_lhs");
        var r = Temp("eq_rhs");
        var sb = new List<string>();
        sb.Add($"{nullable.CName} {l} = {RenderCoerced(binary.L, nullable)}; ");
        sb.Add($"{nullable.CName} {r} = {RenderCoerced(binary.R, nullable)}; ");
        return $"({{ {string.Join("", sb)}{negate}{NullableEqualExpr(nullable, l, r)}; }})";
    }

    private static bool IsComparison(IlLiftedOp op) =>
        op == IlLiftedOp.Eq || op == IlLiftedOp.Ne || op == IlLiftedOp.Lt || op == IlLiftedOp.Le
            || op == IlLiftedOp.Gt || op == IlLiftedOp.Ge;

    private CType TypeOfLiftedBin(IlLiftedBin lifted)
    {
        var left = RequireNullable(lifted.L, $"lifted {lifted.Op}");
        var right = RequireNullable(lifted.R, $"lifted {lifted.Op}");
        var le = left.Element!;
        var re = right.Element!;
        var op = lifted.Op;
        if (op == IlLiftedOp.And || op == IlLiftedOp.Or)
        {
            if (!CType.Same(le, CType.Bool) || !CType.Same(re, CType.Bool))
                CFail.Report("lifted And/Or operands must be bool?");
            return CType.Nullable(CType.Bool);
        }
        if (op == IlLiftedOp.Eq || op == IlLiftedOp.Ne)
        {
            // NumericJoin records an error if it is not a number (the operand mismatch throw of the C# version is never reached)
            if (!CType.Same(le, re)) NumericJoin(le, re, "lifted equality");
            return CType.Bool;
        }
        if (op == IlLiftedOp.Lt || op == IlLiftedOp.Le || op == IlLiftedOp.Gt || op == IlLiftedOp.Ge)
        {
            NumericJoin(le, re, $"lifted {lifted.Op}");
            return CType.Bool;
        }
        if (op == IlLiftedOp.BitXor && CType.Same(le, CType.Bool) && CType.Same(re, CType.Bool))
            return CType.Nullable(CType.Bool);
        if (op == IlLiftedOp.BitAnd || op == IlLiftedOp.BitOr || op == IlLiftedOp.BitXor
            || op == IlLiftedOp.Shl || op == IlLiftedOp.Shr || op == IlLiftedOp.DivInt || op == IlLiftedOp.RemInt)
        {
            RequireType(CType.I32, le, $"lifted {lifted.Op}");
            RequireType(CType.I32, re, $"lifted {lifted.Op}");
            return CType.Nullable(CType.I32);
        }
        if (op == IlLiftedOp.DivFloat || op == IlLiftedOp.RemFloat)
        {
            NumericJoin(le, re, $"lifted {lifted.Op}");
            return CType.Nullable(CType.F32);
        }
        return CType.Nullable(NumericJoin(le, re, $"lifted {lifted.Op}"));
    }

    private string RenderLiftedBin(IlLiftedBin lifted)
    {
        var result = TypeOfLiftedBin(lifted);
        var left = RequireNullable(lifted.L, "lifted");
        var right = RequireNullable(lifted.R, "lifted");
        var x = Temp("lx");
        var y = Temp("ly");
        var sbParts = new List<string>();
        sbParts.Add($"{left.CName} {x} = {RenderExpr(lifted.L)}; ");
        sbParts.Add($"{right.CName} {y} = {RenderExpr(lifted.R)}; ");
        var sb = string.Join("", sbParts);
        var xv = $"{x}.v";
        var yv = $"{y}.v";
        switch (lifted.Op)
        {
            case IlLiftedOp.Eq:
                return $"({{ {sb}{x}.has == {y}.has && (!{x}.has || {xv} == {yv}); }})";
            case IlLiftedOp.Ne:
                return $"({{ {sb}!({x}.has == {y}.has && (!{x}.has || {xv} == {yv})); }})";
            case IlLiftedOp.Lt:
            case IlLiftedOp.Le:
            case IlLiftedOp.Gt:
            case IlLiftedOp.Ge:
                {
                    IlBinOp op;
                    string symbol;
                    if (lifted.Op == IlLiftedOp.Lt) { op = IlBinOp.Lt; symbol = "<"; }
                    else if (lifted.Op == IlLiftedOp.Le) { op = IlBinOp.Le; symbol = "<="; }
                    else if (lifted.Op == IlLiftedOp.Gt) { op = IlBinOp.Gt; symbol = ">"; }
                    else { op = IlBinOp.Ge; symbol = ">="; }
                    string relation;
                    if (left.Element!.Kind == CTypeKind.F32 || right.Element!.Kind == CTypeKind.F32)
                        relation = RenderFloatRelation(op, xv, yv);
                    else
                        relation = $"{xv} {symbol} {yv}";
                    return $"({{ {sb}{x}.has && {y}.has && {relation}; }})";
                }
            case IlLiftedOp.And:
                return $"({{ {sb}{result.CName} r = {{0}}; " +
                    $"if (({x}.has && !{xv}) || ({y}.has && !{yv})) {{ r.has = true; r.v = false; }} " +
                    $"else if ({x}.has && {y}.has) {{ r.has = true; r.v = true; }} r; }})";
            case IlLiftedOp.Or:
                return $"({{ {sb}{result.CName} r = {{0}}; " +
                    $"if (({x}.has && {xv}) || ({y}.has && {yv})) {{ r.has = true; r.v = true; }} " +
                    $"else if ({x}.has && {y}.has) {{ r.has = true; r.v = false; }} r; }})";
        }
        var elem = result.Element!;
        string value;
        switch (lifted.Op)
        {
            case IlLiftedOp.Add: value = $"({xv} + {yv})"; break;
            case IlLiftedOp.Sub: value = $"({xv} - {yv})"; break;
            case IlLiftedOp.Mul: value = $"({xv} * {yv})"; break;
            case IlLiftedOp.DivFloat: value = $"((float){xv} / (float){yv})"; break;
            case IlLiftedOp.DivInt: value = $"tcs_idiv({xv}, {yv})"; break;
            case IlLiftedOp.RemFloat: value = $"fmodf({xv}, {yv})"; break;
            case IlLiftedOp.RemInt: value = $"tcs_irem({xv}, {yv})"; break;
            case IlLiftedOp.BitAnd: value = $"({xv} & {yv})"; break;
            case IlLiftedOp.BitOr: value = $"({xv} | {yv})"; break;
            case IlLiftedOp.BitXor:
                value = CType.Same(elem, CType.Bool) ? $"({xv} != {yv})" : $"({xv} ^ {yv})";
                break;
            case IlLiftedOp.Shl: value = $"tcs_shl({xv}, {yv})"; break;
            case IlLiftedOp.Shr: value = $"tcs_shr({xv}, {yv})"; break;
            default:
                CFail.Report($"unsupported lifted op: {lifted.Op}");
                return "";
        }
        return $"({{ {sb}{result.CName} r = {{0}}; " +
            $"if ({x}.has && {y}.has) {{ r.has = true; r.v = ({elem.CName})({value}); }} r; }})";
    }

    private CType TypeOfLiftedUn(IlLiftedUn lifted)
    {
        var type = RequireNullable(lifted.E, "lifted unary");
        var kind = type.Element!.Kind;
        if (lifted.Op == IlUnOp.Neg && (kind == CTypeKind.I32 || kind == CTypeKind.F32)) return type;
        if (lifted.Op == IlUnOp.Not && CType.Same(type.Element, CType.Bool)) return type;
        if (lifted.Op == IlUnOp.BitNot && CType.Same(type.Element, CType.I32)) return type;
        if (lifted.Op == IlUnOp.ToFloat && (kind == CTypeKind.I32 || kind == CTypeKind.F32))
            return CType.Nullable(CType.F32);
        CFail.Report($"invalid lifted unary {lifted.Op} for {type.Display()}");
        return type;
    }

    private string RenderLiftedUn(IlLiftedUn lifted)
    {
        var type = TypeOfLiftedUn(lifted);
        var x = Temp("lx");
        if (lifted.Op == IlUnOp.ToFloat)
        {
            var source = RequireNullable(lifted.E, "lifted unary");
            return $"({{ {source.CName} {x} = {RenderExpr(lifted.E)}; {type.CName} r = {{0}}; " +
                $"if ({x}.has) {{ r.has = true; r.v = (float){x}.v; }} r; }})";
        }
        string value;
        if (lifted.Op == IlUnOp.Neg) value = $"-{x}.v";
        else if (lifted.Op == IlUnOp.Not) value = $"!{x}.v";
        else value = $"~{x}.v";
        return $"({{ {type.CName} {x} = {RenderExpr(lifted.E)}; " +
            $"if ({x}.has) {x}.v = {value}; {x}; }})";
    }

    // ==== CEmitter.Objects.cs ====

    // `object` (CType.Object = void *): reference types stay as they are; int / float / bool are
    // boxed in TcsBox. Casts are checked with the type_id in the GC header (classes via tcs_init_C,
    // strings via tcs_string_new, boxes via tcs_box_*, and arrays / List / Dict / closures
    // get a structural type id from tcs_typed when boxed). Interfaces are tested with tcs_is_I, which switches over the set of
    // implementing classes.

    // The value is the number in intern order (the uint id of the C# version minus 0x80000000)
    private readonly Dictionary<string, int> _runtimeTypeIds = new Dictionary<string, int>();

    private static string TypeKey(CType type)
    {
        var parts = new List<string>();
        parts.Add(CType.KindName(type.Kind) + ":" + (type.Name != null ? type.Name : ""));
        if (type.Element != null) parts.Add("<" + TypeKey(type.Element) + ">");
        if (type.KeyType != null) parts.Add("[" + TypeKey(type.KeyType) + "]");
        if (type.Parameters != null)
        {
            var ps = new List<string>();
            foreach (var p in type.Parameters) ps.Add(TypeKey(p));
            parts.Add("(" + string.Join(",", ps) + ")");
        }
        return string.Join("", parts);
    }

    // Decimal of 0x80000000 + index (there is no uint, so 2147483648 is split into the low 4 digits and the upper digits and added)
    private static string RuntimeTypeIdText(int index)
    {
        int low = 3648 + index % 10000;
        int high = 214748 + index / 10000 + low / 10000;
        low = low % 10000;
        var pad = low < 10 ? "000" : low < 100 ? "00" : low < 1000 ? "0" : "";
        return high.ToString() + pad + low.ToString();
    }

    // Runtime type tag. For a class, the start of its DFS range; structural types are interned within the program
    private string RuntimeTypeId(CType type)
    {
        if (CType.Same(type, CType.String)) return "TCS_TYPE_STRING";
        if (CType.Same(type, CType.I32)) return "TCS_TYPE_BOX_I32";
        if (CType.Same(type, CType.F32)) return "TCS_TYPE_BOX_F32";
        if (CType.Same(type, CType.Bool)) return "TCS_TYPE_BOX_BOOL";
        if (type.Kind == CTypeKind.Nullable) return RuntimeTypeId(type.Element!);
        if (type.Kind == CTypeKind.Ref) return Names.TypeId(type.Name!);
        var key = TypeKey(type);
        int id;
        if (!_runtimeTypeIds.TryGetValue(key, out id))
        {
            id = _runtimeTypeIds.Count;
            _runtimeTypeIds[key] = id;
        }
        return $"UINT32_C({RuntimeTypeIdText(id)})";
    }

    private static string BoxField(CType scalar)
    {
        switch (scalar.Kind)
        {
            case CTypeKind.I32: return "i";
            case CTypeKind.F32: return "f";
            case CTypeKind.Bool: return "b";
        }
        CFail.Report($"cannot box {scalar.Display()}");
        return "";
    }

    private static string BoxFunction(CType scalar)
    {
        switch (scalar.Kind)
        {
            case CTypeKind.I32: return "tcs_box_i32";
            case CTypeKind.F32: return "tcs_box_f32";
            case CTypeKind.Bool: return "tcs_box_bool";
        }
        CFail.Report($"cannot box {scalar.Display()}");
        return "";
    }

    // value → object
    private string RenderBox(IlExpr value)
    {
        var type = TypeOf(value);
        if (CType.Same(type, CType.Object)) return RenderExpr(value);
        if (CType.Same(type, CType.Null)) return "NULL";
        if (type.Kind == CTypeKind.I32 || type.Kind == CTypeKind.F32 || type.Kind == CTypeKind.Bool)
            return $"{BoxFunction(type)}({RenderExpr(value)})";
        if (type.Kind == CTypeKind.Nullable
            && (type.Element!.Kind == CTypeKind.I32 || type.Element.Kind == CTypeKind.F32
                || type.Element.Kind == CTypeKind.Bool))
        {
            var temp = Temp("opt");
            return $"({{ {type.CName} {temp} = {RenderExpr(value)}; " +
                $"{temp}.has ? {BoxFunction(type.Element)}({temp}.v) : NULL; }})";
        }
        if (type.Kind == CTypeKind.String || type.Kind == CTypeKind.Ref)
            return $"((void *){RenderExpr(value)})";
        if (type.IsNullable)
            return $"tcs_typed({RenderExpr(value)}, {RuntimeTypeId(type)})";
        CFail.Report($"cannot box {type.Display()}");
        return "";
    }

    // object → scalar (only a box of the matching type)
    private string RenderUnbox(IlExpr value, CType target) =>
        $"(tcs_unbox({RenderExpr(value)}, {RuntimeTypeId(target)})->value.{BoxField(target)})";

    // Per-interface implementation test (a switch on the runtime type tag)
    private void EmitInterfaceChecks()
    {
        foreach (var iface in _program.Classes)
        {
            if (!iface.IsInterface) continue;
            Line($"static bool {Names.InterfaceCheck(iface.Name)}(void *object)");
            Line("{");
            _indent++;
            Line("if (object == NULL) return false;");
            Line("switch (TCS_GC_HEADER(object)->type_id) {");
            foreach (var cls in _program.Classes)
                if (!cls.IsInterface && IsAncestorOrSame(iface.Name, cls.Name))
                    Line($"case {Names.TypeId(cls.Name)}: return true;");
            // --vm-host: VM classes can also implement this interface (looked up in the VM type table; object-model §10.6)
            Line(VmHostSurface == null ? "default: return false;"
                : $"default: return TCS_GC_HEADER(object)->kind == TCS_KIND_FOREIGN && tvm_implements(tcs_vmh_vm, object, \"{iface.Name}\");");
            Line("}");
            _indent--;
            Line("}");
            Line();
        }
    }

    // ---- Structs ----

    // ==== tcs2c/CEmitter.Structs.cs ====
    // C-side emit for structs: typedef generation. Value types lower to plain
    // C structs without pointers, and copying works by value assignment.

    // struct typedefs. struct-in-struct needs complete types for value embedding, so
    // inner ones come first (structs cannot be cyclic, so this always resolves)
    private void EmitStructTypedefs()
    {
        if (_program.Structs.Count == 0)
            return;
        var pending = new List<ExStruct>();
        foreach (var s in _program.Structs) pending.Add(s);
        var emitted = new HashSet<string>();
        while (pending.Count > 0)
        {
            int nextIndex = -1;
            for (int i = 0; i < pending.Count && nextIndex < 0; i++)
            {
                bool ready = true;
                foreach (var f in pending[i].Fields)
                {
                    var embedded = _facts.MapType(f.Type);
                    if (embedded.Kind == CTypeKind.StructVal && !emitted.Contains(embedded.Name!))
                    {
                        ready = false;
                        break;
                    }
                }
                if (ready) nextIndex = i;
            }
            if (nextIndex < 0)
            {
                CFail.Report("struct dependency cycle");
                return;
            }
            var next = pending[nextIndex];
            pending.RemoveAt(nextIndex);
            emitted.Add(next.Name);
            Line("typedef struct {");
            _indent++;
            foreach (var field in next.Fields)
                Line(_facts.MapType(field.Type).CName + " " +
                    Names.Field(field.Name) + ";");
            _indent--;
            Line("} Tcs_" + Names.Id(next.Name) + ";");
            Line("typedef struct { bool has; Tcs_" + Names.Id(next.Name) + " v; } " +
                CType.Nullable(CType.Struct(next.Name)).CName + ";");
            Line();
        }
    }

    // The lvalue of a place that mutates the receiver (method self / store to a field), and
    // the heap object that owns it (null for a value on the stack). The heap object expression is
    // evaluated once into a setup temp, and both the lvalue and the owner refer to that temp.
    // Inside a struct method body, the owner of self is the v_owner passed from the caller
    // (the (Place, Owner) tuple of the C# version is returned as Place for the return value and Owner through out)
    private string RenderOwnedStructPlace(IlExpr expr,
        List<string> setup, out string? owner)
    {
        if (expr is IlVar v)
        {
            if (v.Name == "self" && _structSelfOwner)
            {
                var selfPlace = RenderStructPlace(v);
                owner = "v_owner";
                return selfPlace;
            }
            var varPlace = RenderStructPlace(v);
            var cell = Resolve(v.Name);
            owner = cell.Boxed ? cell.CName : null;
            return varPlace;
        }
        if (expr is IlField field)
        {
            if (TryStaticField(field, out string staticName, out CType staticType))
            {
                owner = null;
                return staticName;
            }
            var receiverType = TypeOf(field.Recv);
            if (receiverType.Kind == CTypeKind.StructVal)
            {
                var inner = RenderOwnedStructPlace(field.Recv, setup, out string? innerOwner);
                owner = innerOwner;
                return inner + "." + Names.Field(field.Name);
            }
            if (receiverType.Kind == CTypeKind.Ref)
            {
                var classType = receiverType;
                var fieldOwner = Temp("owner");
                setup.Add(classType.CName + " " + fieldOwner + " = (" + classType.CName + ")" +
                    "tcs_nonnull(" + RenderExpr(field.Recv) + "); ");
                owner = fieldOwner;
                return fieldOwner + "->" + Names.Field(field.Name);
            }
        }
        if (expr is IlIndex index && index.PlusOne)
        {
            var sequenceType = RequireSequence(index);
            var indexOwner = Temp("owner");
            setup.Add(sequenceType.CName + " " + indexOwner + " = " + RenderExpr(index.Recv) + "; ");
            var at = sequenceType.Kind == CTypeKind.Array
                ? "tcs_array_at" : "tcs_list_at";
            var place = "(*(" + sequenceType.ElementCName + " *)" + at + "(" +
                indexOwner + ", " + RenderExpr(index.Idx) + "))";
            owner = indexOwner;
            return place;
        }
        owner = null;
        return RenderStructPlace(expr);
    }

    // lvalue chain of a struct value. receiver/index are evaluated inline (going through a temp
    // makes it a statement-expression and it is no longer an lvalue)
    private string RenderStructPlace(IlExpr expr)
    {
        if (expr is IlVar v)
        {
            var variable = Resolve(v.Name);
            return variable.Boxed ? "(*" + variable.CName + ")" : variable.CName;
        }
        if (expr is IlIndex dictIndex && !dictIndex.PlusOne
            && TypeOf(dictIndex.Recv).Kind == CTypeKind.Dict)
        {
            // struct value in a Dictionary (read only — C# rejects writes with CS1612)
            var valueType = RequireDict(dictIndex.Recv, out CType dictType);
            return "(*(" + valueType.CName + " *)tcs_dict_at(" +
                RenderExpr(dictIndex.Recv) + ", " + DictKeyArgs(dictType.KeyType!, dictIndex.Idx) + "))";
        }
        if (expr is IlIndex index)
        {
            var sequenceType = RequireSequence(index);
            var at = sequenceType.Kind == CTypeKind.Array
                ? "tcs_array_at" : "tcs_list_at";
            return "(*(" + sequenceType.ElementCName + " *)" + at + "(" +
                RenderExpr(index.Recv) + ", " + RenderExpr(index.Idx) + "))";
        }
        if (expr is IlField field)
        {
            if (TryStaticField(field, out string staticName, out CType staticType))
                return staticName;
            var receiverType = TypeOf(field.Recv);
            if (receiverType.Kind == CTypeKind.StructVal)
                return RenderStructPlace(field.Recv) + "." +
                    Names.Field(field.Name);
            if (receiverType.Kind == CTypeKind.Ref)
                return "((" + receiverType.CName + ")tcs_nonnull(" +
                    RenderExpr(field.Recv) + "))->" + Names.Field(field.Name);
            if (receiverType.Kind == CTypeKind.Kvp)
                // struct value in a KeyValuePair (read only — C# rejects writes to kv.Value.F
                // with CS1612, and IsStructPlace sends mutating methods to a copy)
                return RenderField(field);
            CFail.Report(
                "unsupported struct place receiver: " + receiverType.Display());
            return "";
        }
        if (expr is IlParen paren)
            return "(" + RenderStructPlace(paren.E) + ")";
        if (expr is IlNullableValue || expr is IlIife || expr is IlCall || expr is IlInvoke || expr is IlDynCall
            || expr is IlWith || expr is IlStructCopy || expr is IlNewObj || expr is IlTernary)
            // rvalue struct (read only; C can read members of an rvalue struct)
            return "(" + RenderExpr(expr) + ")";
        CFail.Report(
            "unsupported struct place: " + NodeName(expr));
        return "";
    }

    // ==== tcs2c/CEmitter.StructMembers.cs ====
    // Members of struct / record struct (il-spec §10): instance methods and
    // accessors are free functions taking `self` as a pointer to the storage location (Tcs_S *),
    // explicit / positional ctors are functions that build from the zero value and return by value, and static methods and
    // operators are free functions without self.
    // Call sites are static dispatch of IlCall("S.M", [receiver, args]):
    // if receiver is a place (variable / field / array element), its address is passed (mutations
    // stay in place = a C# variable receiver), and if it is an rvalue (copy / call result),
    // the address of a temporary is passed (mutations are discarded = a C# rvalue receiver).

    // Value returned by `return;` in a struct ctor body (null outside ctors)
    // (_ctorReturnValue is declared in core)

    private List<ExStruct> StructInfos => _program.Structs;

    // Types with static fields. struct statics follow the same storage and initialization rules as class statics
    private List<ExClass> StaticOwners
    {
        get
        {
            var owners = new List<ExClass>();
            foreach (var cls in _program.Classes) owners.Add(cls);
            BuildStructStaticOwners();
            foreach (var cls in _structStaticOwnerList!) owners.Add(cls);
            return owners;
        }
    }

    private Dictionary<string, ExClass>? _structStaticOwners;
    // Values of _structStaticOwners in creation order (does not rely on Dictionary enumeration order)
    private List<ExClass>? _structStaticOwnerList;

    private Dictionary<string, ExClass> StructStaticOwners
    {
        get
        {
            BuildStructStaticOwners();
            return _structStaticOwners!;
        }
    }

    private void BuildStructStaticOwners()
    {
        if (_structStaticOwners != null) return;
        var owners = new Dictionary<string, ExClass>();
        var list = new List<ExClass>();
        foreach (var st in StructInfos)
        {
            if (st.StaticFields.Count == 0) continue;
            var owner = new ExClass();
            owner.Name = st.Name;
            owner.BaseName = null;
            owner.Fields = st.StaticFields;
            owner.LayoutHash = st.LayoutHash;
            owner.Methods = new List<ExMethod>();
            if (owners.ContainsKey(st.Name))
            {
                CFail.Report("duplicate struct: " + st.Name);
                continue;
            }
            owners[st.Name] = owner;
            list.Add(owner);
        }
        _structStaticOwners = owners;
        _structStaticOwnerList = list;
    }

    private bool IsStaticMemberOwner(string name) =>
        _classes.ContainsKey(name) || _facts.Structs.ContainsKey(name);

    private bool TryStructCallee(string callee, out ExStruct st,
        out string member)
    {
        st = new ExStruct();
        member = "";
        var u = new Utf8Bytes(callee);
        int dot = -1;
        int lastDot = -1;
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] == '.')
            {
                if (dot < 0) dot = i;
                lastDot = i;
            }
        }
        if (dot <= 0 || dot != lastDot || dot == u.Length - 1)
            return false;
        ExStruct? found;
        if (!_facts.Structs.TryGetValue(ContractFacts.Sub(callee, 0, dot), out found))
            return false;
        st = found;
        member = Strings.From(callee, dot + 1);
        return true;
    }

    private void ValidateStructs()
    {
        foreach (var st in StructInfos)
        {
            Names.Id(st.Name);
            foreach (var field in st.Fields)
            {
                Names.Id(field.Name);
                EnsureSupportedStorageType(_facts.MapType(field.Type),
                    $"field {st.Name}.{field.Name}");
            }
            foreach (var field in st.StaticFields)
            {
                Names.Id(field.Name);
                EnsureSupportedStorageType(_facts.MapType(field.Type),
                    $"static field {st.Name}.{field.Name}");
            }
            foreach (var method in st.Methods)
            {
                Names.Id(method.Name);
                if (method.Body == null)
                {
                    CFail.Report(
                        $"method has no IL body: {st.Name}.{method.Name}");
                    continue;
                }
                var fact = _facts.Method(st.Name, method.Name);
                EnsureSupportedStorageType(fact.ReturnType,
                    $"return type of {st.Name}.{method.Name}", true);
                foreach (var parameter in fact.Parameters)
                    EnsureSupportedStorageType(parameter.Type,
                        $"parameter {st.Name}.{method.Name}.{parameter.Name}");
            }
            bool missingBody = false;
            foreach (var c in StructCtors(st))
            {
                if (c.Body == null) missingBody = true;
            }
            if (missingBody)
                CFail.Report(
                    $"constructor body is not IL-exportable: {st.Name}");
        }
    }

    private static List<ExCtor> StructCtors(ExStruct st) =>
        st.Ctors;

    private static string StructCtorFunction(ExStruct st, ExCtor ctor) =>
        Names.StructCtor(ctor.Name == "ctor" ? st.Name : st.Name + "_" + ctor.Name);

    private List<ParameterFact> StructCtorParamFacts(ExStruct st, ExCtor ctor)
    {
        var list = new List<ParameterFact>();
        if (ctor.Parameters.Count != ctor.ParameterTypes.Count)
        {
            CFail.Report($"ctor metadata mismatch: {st.Name}");
            return list;
        }
        for (int i = 0; i < ctor.Parameters.Count; i++)
            list.Add(new ParameterFact(
                ctor.Parameters[i], _facts.MapType(ctor.ParameterTypes[i]), null));
        return list;
    }

    private void EmitStructMemberPrototypes()
    {
        foreach (var st in StructInfos)
        {
            foreach (var ctor in StructCtors(st))
            {
                var parameters = StructCtorParamFacts(st, ctor);
                var types = new List<string>();
                foreach (var p in parameters) types.Add(p.Type.CName);
                Line("static " + CType.Struct(st.Name).CName + " " +
                    StructCtorFunction(st, ctor) + "(" +
                    (parameters.Count == 0 ? "void"
                        : string.Join(", ", types)) +
                    ");");
            }
            foreach (var method in st.Methods)
            {
                var fact = _facts.Method(st.Name, method.Name);
                Line("static " + fact.ReturnType.CName + " " +
                    Names.Method(st.Name, method.Name) + "(" + ParameterList(fact) + ");");
            }
        }
    }

    private void EmitStructMembers()
    {
        foreach (var st in StructInfos)
        {
            foreach (var ctor in StructCtors(st)) EmitStructCtor(st, ctor);
            foreach (var method in st.Methods)
            {
                var fact = _facts.Method(st.Name, method.Name);
                _structSelfOwner = !fact.IsStatic;
                Variable? selfVar = null;
                if (!fact.IsStatic)
                {
                    selfVar = new Variable("v_self", CType.Struct(st.Name));
                    selfVar.Boxed = true;
                }
                EmitMethodCore(method, fact, selfVar);
                _structSelfOwner = false;
            }
        }
    }

    // zero value → field initializers → body (il-spec §10). self points
    // to the local value, so field writes in the body remain in the result as is
    private void EmitStructCtor(ExStruct st, ExCtor ctor)
    {
        var ctorBody = ctor.Body;
        if (ctorBody == null)
        {
            CFail.Report($"constructor body is not IL-exportable: {st.Name}");
            return;
        }
        var type = CType.Struct(st.Name);
        var parameters = StructCtorParamFacts(st, ctor);
        var factParameters = new List<ParameterFact>();
        foreach (var p in parameters) factParameters.Add(p);
        var metadata = new ExMethod();
        metadata.Name = ctor.Name;
        metadata.IsStatic = true;
        metadata.Parameters = ctor.Parameters;
        metadata.Body = ctor.Body;
        metadata.ReturnType = st.Name;
        metadata.ParameterTypes = ctor.ParameterTypes;
        _currentMethodFact = new MethodFact(st.Name, ctor.Name, true, type,
            factParameters, metadata);
        _currentMethod = _currentMethodFact.Metadata;
        _scopes.Clear();
        _scopeOrder.Clear();
        _continueTargets.Clear();
        _breakTargets.Clear();
        CollectCapturedNames(ctorBody);
        PushScope();
        for (int i = 0; i < parameters.Count; i++)
            AddVariable(parameters[i].Name,
                new Variable($"v_{Names.Id(parameters[i].Name)}_{i}",
                    parameters[i].Type));
        var selfVariable = new Variable("v_self", type);
        selfVariable.Boxed = true;
        AddVariable("self", selfVariable);
        Line($"static {type.CName}");
        var declared = new List<string>();
        for (int i = 0; i < parameters.Count; i++)
            declared.Add($"{parameters[i].Type.CName} v_{Names.Id(parameters[i].Name)}_{i}");
        Line(StructCtorFunction(st, ctor) + "(" +
            (parameters.Count == 0 ? "void"
                : string.Join(", ", declared)) + ")");
        Line("{");
        _indent++;
        Line($"{type.CName} self_value = {ZeroInit(type)};");
        Line($"{type.CName} *v_self = &self_value;");
        Line("void *v_owner = NULL; (void)v_owner;");
        _structSelfOwner = true;
        BoxCapturedParameters();
        foreach (var field in st.Fields)
        {
            if (field.Init == null) continue;
            var fieldType = _facts.MapType(field.Type);
            CheckAssignable(fieldType, field.Init,
                $"initializer of {st.Name}.{field.Name}");
            Line($"(*v_self).{Names.Field(field.Name)} = " +
                RenderCoerced(field.Init, fieldType) + ";");
        }
        _ctorReturnValue = "(*v_self)";
        EmitStats(ctorBody.Stats);
        _ctorReturnValue = null;
        _structSelfOwner = false;
        Line("return (*v_self);");
        _indent--;
        Line("}");
        Line();
        PopScope();
        FlushPendingClosures();
    }

    private CType TypeOfStructCall(ExStruct st, string member,
        List<IlExpr> args)
    {
        var type = CType.Struct(st.Name);
        ExCtor? ctor = null;
        foreach (var c in StructCtors(st))
        {
            if (c.Name == member)
            {
                ctor = c;
                break;
            }
        }
        if (ctor != null)
        {
            var parameters = StructCtorParamFacts(st, ctor);
            RequireArity($"{st.Name}.{member}", args.Count, parameters.Count);
            if (args.Count != parameters.Count) return type;
            for (int i = 0; i < args.Count; i++)
                CheckAssignable(parameters[i].Type, args[i],
                    $"argument {i} of {st.Name}.{member}");
            return type;
        }
        if (member == "ctor")
        {
            CFail.Report($"struct has no constructor: {st.Name}");
            return CType.Void;
        }
        if (member == "op_Equality")
        {
            RequireArity($"{st.Name}.op_Equality", args.Count, 2);
            if (args.Count != 2) return CType.Bool;
            CheckAssignable(type, args[0], $"{st.Name}.op_Equality left");
            CheckAssignable(type, args[1], $"{st.Name}.op_Equality right");
            return CType.Bool;
        }
        var fact = _facts.Method(st.Name, member);
        if (fact.IsStatic) return ValidateMethodCall(fact, null, args);
        if (args.Count != fact.Parameters.Count + 1)
        {
            CFail.Report(
                $"struct method call is missing its receiver: {st.Name}.{member}");
            return CType.Void;
        }
        CheckAssignable(type, args[0], $"receiver of {st.Name}.{member}");
        var rest = new List<IlExpr>();
        for (int i = 1; i < args.Count; i++) rest.Add(args[i]);
        return ValidateMethodCall(fact, type, rest);
    }

    private string RenderStructCall(ExStruct st, string member,
        List<IlExpr> args)
    {
        var type = CType.Struct(st.Name);
        ExCtor? ctor = null;
        foreach (var c in StructCtors(st))
        {
            if (c.Name == member)
            {
                ctor = c;
                break;
            }
        }
        if (ctor != null)
        {
            var parameters = StructCtorParamFacts(st, ctor);
            if (args.Count > parameters.Count)
            {
                RequireArity($"{st.Name}.{member}", args.Count, parameters.Count);
                return "";
            }
            var values = new List<TypedValue>();
            for (int i = 0; i < args.Count; i++)
                values.Add(new TypedValue(parameters[i].Type, RenderCoerced(args[i], parameters[i].Type)));
            return RenderOrderedCall(StructCtorFunction(st, ctor), type,
                values);
        }
        if (member == "op_Equality")
        {
            if (args.Count != 2)
            {
                RequireArity($"{st.Name}.op_Equality", args.Count, 2);
                return "";
            }
            var left = Temp("eq_l");
            var right = Temp("eq_r");
            return "({ " + type.CName + " " + left + " = " + RenderExpr(args[0]) + "; " +
                type.CName + " " + right + " = " + RenderExpr(args[1]) + "; " +
                Names.StructEq(st.Name) + "(&" + left + ", &" + right + "); })";
        }
        var fact = _facts.Method(st.Name, member);
        if (fact.IsStatic) return RenderMethodCall(fact, null, args);
        var outArguments = TakeOutArguments();
        if (args.Count < fact.Parameters.Count + 1)
        {
            CFail.Report(
                $"struct method call is missing its receiver: {st.Name}.{member}");
            return "";
        }
        var sb = new List<string>();
        var receiver = Temp("recv");
        var owner = Temp("owner");
        if (IsStructPlace(args[0]))
        {
            var place = RenderOwnedStructPlace(args[0], sb, out string? placeOwner);
            sb.Add($"{type.CName} *{receiver} = &{place}; ");
            sb.Add($"void *{owner} = " + (placeOwner != null ? placeOwner : "NULL") + "; ");
        }
        else
        {
            sb.Add($"{type.CName} {receiver}_value = " + RenderExpr(args[0]) + "; ");
            sb.Add($"{type.CName} *{receiver} = &{receiver}_value; ");
            sb.Add($"void *{owner} = NULL; ");
        }
        var rendered = new List<string>();
        rendered.Add(receiver);
        rendered.Add(owner);
        for (int i = 0; i < fact.Parameters.Count; i++)
        {
            var temp = Temp("arg");
            sb.Add($"{fact.Parameters[i].Type.CName} {temp} = " +
                RenderCoerced(args[i + 1], fact.Parameters[i].Type) + "; ");
            rendered.Add(temp);
        }
        foreach (var o in outArguments) rendered.Add(o);
        sb.Add(Names.Method(st.Name, member) + "(" + string.Join(", ", rendered) + "); ");
        return "({ " + string.Join("", sb) + "})";
    }

    // A C# "variable" receiver (il-spec §10): local / parameter / field / array element
    private bool IsStructPlace(IlExpr expr)
    {
        if (expr is IlVar v) return TryResolve(v.Name) != null;
        if (expr is IlField f)
        {
            if (TryStaticField(f, out string staticName, out CType staticType)) return true;
            var kind = TypeOf(f.Recv).Kind;
            // A field of an rvalue (property / call result) is part of a copy and not a place
            if (kind == CTypeKind.StructVal) return IsStructPlace(f.Recv);
            if (kind == CTypeKind.Ref) return true;
            return false;
        }
        if (expr is IlIndex index) return index.PlusOne;
        return false;
    }

    private CType TypeOfStructWith(IlWith with, CType source)
    {
        foreach (var o in with.Overrides)
            CheckAssignable(_facts.StructField(source.Name!, o.Name), o.Value,
                $"with {o.Name}");
        return source;
    }

    // Write overrides on top of a value copy (with on a record struct)
    private string RenderStructWith(IlWith with, CType type)
    {
        var copy = Temp("with_copy");
        var sb = new List<string>();
        sb.Add($"{type.CName} {copy} = " + RenderExpr(with.Src) + "; ");
        foreach (var o in with.Overrides)
        {
            var fieldType = _facts.StructField(type.Name!, o.Name);
            sb.Add($"{copy}.{Names.Field(o.Name)} = " + RenderCoerced(o.Value, fieldType) + "; ");
        }
        return "({ " + string.Join("", sb) + copy + "; })";
    }

    // ==== tcs2c/CEmitter.Records.cs ====
    // record class: structural equality (==/!=) and with expressions. In C these become a comparison function per record type and
    // a copy using the runtime layout (runtime type).

    private bool IsRecordClass(string name)
    {
        ExClass? cls;
        if (!_classes.TryGetValue(name, out cls)) return false;
        return cls.IsRecord;
    }

    // If both sides of ==/!= are class references and there is a record in the static type's chain, it is structural equality
    private string? RecordEqualityType(CType left, CType right)
    {
        if (left.Kind != CTypeKind.Ref || right.Kind != CTypeKind.Ref) return null;
        string? common = null;
        if (IsAncestorOrSame(left.Name!, right.Name!)) common = left.Name!;
        else if (IsAncestorOrSame(right.Name!, left.Name!)) common = right.Name!;
        return common != null && IsRecordClass(common) ? common : null;
    }

    private void EmitRecordEqualityPrototypes()
    {
        // In the order of _facts.Structs keys (= the order of _program.Structs; does not rely on Dictionary enumeration order)
        foreach (var info in _program.Structs)
        {
            var st = info.Name;
            Line($"static bool {Names.StructEq(st)}(const {Names.Class(st)} *a, " +
                $"const {Names.Class(st)} *b);");
        }
        foreach (var cls in _program.Classes)
        {
            if (!cls.IsRecord) continue;
            Line($"static bool {Names.RecordEq(cls.Name)}({Names.Class(cls.Name)} *a, " +
                $"{Names.Class(cls.Name)} *b);");
        }
    }

    // Equality expression for one field (a->f and b->f, or a.f and b.f). Like C#'s synthesized Equals /
    // ValueType.Equals, it compares with EqualityComparer<T>.Default
    private string FieldEqualExpr(CType type, string left, string right)
    {
        if (type.Kind == CTypeKind.F32) return $"tcs_float_equals({left}, {right})";
        if (type.Kind == CTypeKind.String) return $"tcs_string_equal({left}, {right})";
        if (type.Kind == CTypeKind.Ref && IsRecordClass(type.Name!))
            return $"{Names.RecordEq(type.Name!)}({left}, {right})";
        if (type.Kind == CTypeKind.StructVal) return $"{Names.StructEq(type.Name!)}(&{left}, &{right})";
        if (type.Kind == CTypeKind.Nullable) return NullableEqualExpr(type, left, right, true);
        // object and interface fields are compared by runtime type (value equality if the inner value is a record)
        if (IsRuntimeEqualityType(type)) return $"{ObjectEqualName}({left}, {right})";
        return $"({left} == {right})";
    }

    private const string ObjectEqualName = "tcs_eq_object";

    private bool IsRuntimeEqualityType(CType type)
    {
        return type.Kind == CTypeKind.Object || type.Kind == CTypeKind.Ref && IsInterface(type.Name!);
    }

    // Whether a record or struct field has an object or interface type (whether tcs_eq_object is needed)
    private bool NeedsObjectEquality()
    {
        foreach (var st in _program.Structs)
        {
            foreach (var f in st.Fields)
            {
                if (IsRuntimeEqualityType(_facts.MapType(f.Type))) return true;
            }
        }
        foreach (var cls in _program.Classes)
        {
            if (!cls.IsRecord) continue;
            foreach (var field in cls.Fields)
            {
                if (!field.IsStatic && IsRuntimeEqualityType(_facts.Field(cls.Name, field.Name).Type)) return true;
            }
        }
        return false;
    }

    // Equality of object values (EqualityComparer<object>.Default): the same runtime type, plus the inner value for string and box, value equality for records, and reference equality for other classes
    private void EmitObjectEquality()
    {
        Line("static bool");
        Line($"{ObjectEqualName}(void *a, void *b)");
        Line("{");
        _indent++;
        Line("uint32_t type_id;");
        Line("if (a == b) return true;");
        Line("if (a == NULL || b == NULL) return false;");
        Line("type_id = TCS_GC_HEADER(a)->type_id;");
        Line("if (type_id != TCS_GC_HEADER(b)->type_id) return false;");
        Line("switch (type_id) {");
        Line("case TCS_TYPE_STRING: return tcs_string_equal((TcsString *)a, (TcsString *)b);");
        Line("case TCS_TYPE_BOX_I32: return ((TcsBox *)a)->value.i == ((TcsBox *)b)->value.i;");
        Line("case TCS_TYPE_BOX_F32: return tcs_float_equals(((TcsBox *)a)->value.f, ((TcsBox *)b)->value.f);");
        Line("case TCS_TYPE_BOX_BOOL: return ((TcsBox *)a)->value.b == ((TcsBox *)b)->value.b;");
        foreach (var cls in _program.Classes)
        {
            if (!cls.IsRecord || cls.IsInterface) continue;
            Line($"case {Names.TypeId(cls.Name)}: return {Names.RecordEq(cls.Name)}(" +
                $"({Names.Class(cls.Name)} *)a, ({Names.Class(cls.Name)} *)b);");
        }
        Line("default: return false;");
        Line("}");
        _indent--;
        Line("}");
        Line();
    }

    private void EmitRecordEquality()
    {
        if (NeedsObjectEquality()) EmitObjectEquality();
        // Data structs compare memberwise (same as C# ValueType.Equals)
        foreach (var st in _program.Structs)
        {
            var cName = Names.Class(st.Name);
            Line("static bool");
            Line($"{Names.StructEq(st.Name)}(const {cName} *a, const {cName} *b)");
            Line("{");
            _indent++;
            var parts = new List<string>();
            foreach (var f in st.Fields)
                parts.Add(FieldEqualExpr(_facts.MapType(f.Type),
                    $"a->{Names.Field(f.Name)}", $"b->{Names.Field(f.Name)}"));
            Line("return " + (parts.Count == 0 ? "true" : string.Join(" && ", parts)) + ";");
            _indent--;
            Line("}");
            Line();
        }
        foreach (var cls in _program.Classes)
        {
            if (!cls.IsRecord) continue;
            var cName = Names.Class(cls.Name);
            Line("static bool");
            Line($"{Names.RecordEq(cls.Name)}({cName} *a, {cName} *b)");
            Line("{");
            _indent++;
            Line("if (a == b) return true;");
            Line("if (a == NULL || b == NULL) return false;");
            Line("if (a->type_id != b->type_id) return false;");
            // Derived records go to the comparison function of the runtime type (checks all fields)
            var derived = new List<ExClass>();
            foreach (var c in _program.Classes)
            {
                if (c.Name != cls.Name && IsAncestorOrSame(cls.Name, c.Name)) derived.Add(c);
            }
            if (derived.Count > 0)
            {
                Line("switch (a->type_id) {");
                foreach (var d in derived)
                    Line($"case {Names.TypeId(d.Name)}: return {Names.RecordEq(d.Name)}(" +
                        $"({Names.Class(d.Name)} *)a, ({Names.Class(d.Name)} *)b);");
                Line("default: break;");
                Line("}");
            }
            var parts = new List<string>();
            foreach (var link in ChainRootFirst(cls.Name))
                foreach (var field in link.Fields)
                {
                    if (field.IsStatic) continue;
                    parts.Add(FieldEqualExpr(_facts.Field(link.Name, field.Name).Type,
                        $"a->{Names.Field(field.Name)}", $"b->{Names.Field(field.Name)}"));
                }
            Line("return " + (parts.Count == 0 ? "true" : string.Join(" && ", parts)) + ";");
            _indent--;
            Line("}");
            Line();
        }
    }

    private CType TypeOfWith(IlWith with)
    {
        var source = TypeOf(with.Src);
        if (source.Kind == CTypeKind.StructVal) return TypeOfStructWith(with, source);
        if (source.Kind != CTypeKind.Ref)
        {
            CFail.Report("with source is not a class reference: " + source.Display());
            return CType.Void;
        }
        foreach (var o in with.Overrides)
            CheckAssignable(FieldInChain(source.Name!, o.Name).Type, o.Value, $"with {o.Name}");
        return source;
    }

    // The shallow copy uses the runtime type's layout (with on a derived record also keeps the type)
    private string RenderWith(IlWith with)
    {
        var type = TypeOfWith(with);
        if (type.Kind == CTypeKind.StructVal) return RenderStructWith(with, type);
        if (type.Kind != CTypeKind.Ref) return "";
        var src = Temp("with_src");
        var copy = Temp("with_copy");
        var header = Temp("with_header");
        var sb = new List<string>();
        sb.Add($"{type.CName} {src} = ({type.CName})tcs_nonnull(" + RenderExpr(with.Src) + "); ");
        sb.Add($"TcsGcHeader *{header} = TCS_GC_HEADER({src}); ");
        sb.Add($"{type.CName} {copy} = tcs_new_object({header}->layout); ");
        sb.Add($"memcpy({copy}, {src}, {header}->layout->size); ");
        sb.Add($"TCS_GC_HEADER({copy})->type_id = {header}->type_id; ");
        foreach (var o in with.Overrides)
        {
            var fieldType = FieldInChain(type.Name!, o.Name).Type;
            sb.Add($"{copy}->{Names.Field(o.Name)} = " + RenderCoerced(o.Value, fieldType) + "; ");
        }
        return "({ " + string.Join("", sb) + copy + "; })";
    }

    // ==== tcs2c/CEmitter.Construct.cs ====

    // Construction order:
    // base init → type_id → own class field init → ctor body.
    // Allocation is done once by the most-derived tcs_new_C with its layout (sizeof(Tcs_C) + pointer map),
    // and tcs_init_C(object, ...) chains to the base init through a prefix-compatible upcast
    // (allocating sizeof(Base) on the base side would overflow the derived fields)
    private void EmitAllocators()
    {
        foreach (var cls in _program.Classes)
        {
            if (cls.IsInterface) continue;
            foreach (var ctor in CtorsOrDefault(cls))
            {
                var cType = Names.Class(cls.Name);
                var symbol = CtorSymbol(cls, ctor);
                _currentClass = cls;
                _scopes.Clear();
                _scopeOrder.Clear();
                _continueTargets.Clear();
                _breakTargets.Clear();
                PushScope();
                var paramFacts = CtorParamFacts(ctor);
                for (int i = 0; i < paramFacts.Count; i++)
                    AddVariable(paramFacts[i].Name,
                        new Variable($"v_{Names.Id(paramFacts[i].Name)}_{i}",
                            paramFacts[i].Type));
                var parameterParts = new List<string>();
                parameterParts.Add($"{cType} *object");
                for (int i = 0; i < paramFacts.Count; i++)
                    parameterParts.Add($"{paramFacts[i].Type.CName} v_{Names.Id(paramFacts[i].Name)}_{i}");
                var parameters = string.Join(", ", parameterParts);
                Line("static void");
                Line($"{Names.Init(symbol)}({parameters})");
                Line("{");
                _indent++;
                // this and the arguments captured by closures in the ctor (base arguments / field initializers / body)
                // are boxed into cells, the same as for methods
                _capturedNames.Clear();
                if (ctor != null)
                {
                    var baseArgStats = new List<IlStat>();
                    foreach (var a in ctor.BaseArgs) baseArgStats.Add(new IlCallStat(a, null));
                    AddCapturedNames(new IlBlock(baseArgStats));
                }
                var initStats = new List<IlStat>();
                foreach (var f in cls.Fields)
                {
                    if (!f.IsStatic && f.Init != null) initStats.Add(new IlCallStat(f.Init, null));
                }
                AddCapturedNames(new IlBlock(initStats));
                if (ctor != null && ctor.Body != null) AddCapturedNames(ctor.Body);
                AddVariable("self", new Variable("object", CType.Ref(cls.Name)));
                BoxCapturedParameters();
                string? baseName = cls.BaseName;
                if (baseName != null)
                {
                    // Only --ref types can have a base class, and they have a single constructor
                    var baseParams = CtorParamFacts(CtorsOrDefault(_classes[baseName])[0]);
                    var baseArgs = CompleteArguments(baseParams,
                        ctor != null ? ctor.BaseArgs : new List<IlExpr>(),
                        $"base constructor of {cls.Name}");
                    var rendered = new List<string>();
                    rendered.Add($"({Names.Class(baseName)} *)object");
                    for (int i = 0; i < baseArgs.Count; i++)
                    {
                        CheckAssignable(baseParams[i].Type, baseArgs[i],
                            $"base ctor argument {i} of {cls.Name}");
                        var temp = Temp("base_arg");
                        Line($"{baseParams[i].Type.CName} {temp} = " +
                            RenderCoerced(baseArgs[i], baseParams[i].Type) + ";");
                        rendered.Add(temp);
                    }
                    Line($"{Names.Init(baseName)}(" + string.Join(", ", rendered) + ");");
                }
                Line($"object->type_id = {Names.TypeId(cls.Name)};");
                Line($"TCS_GC_HEADER(object)->type_id = {Names.TypeId(cls.Name)};");
                foreach (var field in cls.Fields)
                {
                    if (field.IsStatic) continue;
                    var fact = _facts.Field(cls.Name, field.Name);
                    if (fact.Init != null)
                    {
                        CheckAssignable(fact.Type, fact.Init,
                            $"initializer of {cls.Name}.{field.Name}");
                        Line($"object->{Names.Field(field.Name)} = " +
                            RenderCoerced(fact.Init, fact.Type) + ";");
                    }
                }
                if (ctor != null && ctor.Body != null)
                    EmitStats(ctor.Body.Stats);
                else if (ctor != null)
                    CFail.Report(
                        $"constructor body is not IL-exportable: {cls.Name}");
                PopScope();
                _indent--;
                Line("}");
                Line();
                FlushPendingClosures();

                var newParamParts = new List<string>();
                for (int i = 0; i < paramFacts.Count; i++)
                    newParamParts.Add($"{paramFacts[i].Type.CName} v_{Names.Id(paramFacts[i].Name)}_{i}");
                var newParams = paramFacts.Count == 0
                    ? "void"
                    : string.Join(", ", newParamParts);
                var initArgParts = new List<string>();
                initArgParts.Add("object");
                for (int i = 0; i < paramFacts.Count; i++)
                    initArgParts.Add($"v_{Names.Id(paramFacts[i].Name)}_{i}");
                var initArgs = string.Join(", ", initArgParts);
                Line($"static {cType} *");
                Line($"{Names.New(symbol)}({newParams})");
                Line("{");
                _indent++;
                Line($"{cType} *object = tcs_new_object(&{Names.ClassLayout(cls.Name)});");
                Line($"{Names.Init(symbol)}({initArgs});");
                Line("return object;");
                _indent--;
                Line("}");
                Line();
            }
        }
    }

    // A class without an explicit constructor has a single one that only does default initialization (null)
    private static List<ExCtor?> CtorsOrDefault(ExClass cls)
    {
        var list = new List<ExCtor?>();
        if (cls.Ctors.Count == 0)
        {
            list.Add(null);
            return list;
        }
        foreach (var c in cls.Ctors) list.Add(c);
        return list;
    }

    // Names of tcs_new_ / tcs_init_. Only constructors tagged with parameter types get a suffix after the type name
    private static string CtorSymbol(ExClass cls, ExCtor? ctor) =>
        ctor == null || ctor.Name == "new" ? cls.Name : cls.Name + "_" + ctor.Name;

    private List<ParameterFact> CtorParamFacts(ExCtor? ctor)
    {
        if (ctor == null) return new List<ParameterFact>();
        if (ctor.Parameters.Count != ctor.ParameterTypes.Count)
        {
            CFail.Report($"ctor metadata mismatch: {ctor.Name}");
            return new List<ParameterFact>();
        }
        return _facts.ParameterFacts(ctor.Parameters, ctor.ParameterTypes,
            ctor.ParameterDefaults);
    }

    // Fill in omitted trailing arguments with default values (IL ParameterDefaults)
    // (on error, records the error and returns an empty List)
    private static List<IlExpr> CompleteArguments(
        List<ParameterFact> parameters, List<IlExpr> supplied,
        string where)
    {
        if (supplied.Count == parameters.Count) return supplied;
        if (supplied.Count > parameters.Count)
        {
            CFail.Report($"{where}: expected {parameters.Count} arguments, " +
                $"got {supplied.Count}");
            return new List<IlExpr>();
        }
        var result = new List<IlExpr>();
        foreach (var s in supplied) result.Add(s);
        for (int i = supplied.Count; i < parameters.Count; i++)
        {
            var dflt = parameters[i].Default;
            if (dflt == null)
            {
                CFail.Report($"{where}: missing argument {parameters[i].Name}");
                return new List<IlExpr>();
            }
            result.Add(dflt);
        }
        return result;
    }


    private MethodFact ResolveInvokeFact(CType receiver, string method)
    {
        if (receiver.Kind != CTypeKind.Ref)
        {
            CFail.Report("IlInvoke receiver is not a class reference");
            return new MethodFact("", method, false, CType.Void, new List<ParameterFact>(), new ExMethod());
        }
        var declaring = FindDeclaringClass(receiver.Name!, method);
        if (declaring == null)
        {
            CFail.Report(
                $"unknown method: {receiver.Name}.{method}");
            return new MethodFact(receiver.Name!, method, false, CType.Void, new List<ParameterFact>(), new ExMethod());
        }
        return _facts.Method(declaring, method);
    }

    private CType TypeOfInvoke(IlInvoke invoke)
    {
        var receiver = TypeOf(invoke.Recv);
        if (receiver.Kind == CTypeKind.Random)
            return TypeOfRandomMethod(invoke.Method, invoke.Args);
        var foreign = ForeignInstanceMethod(receiver, invoke.Method);
        if (foreign != null)
            return TypeOfForeignCall(foreign, invoke.Args);
        var fact = ResolveInvokeFact(receiver, invoke.Method);
        return ValidateMethodCall(fact, receiver, invoke.Args);
    }

    private string RenderInvoke(IlInvoke invoke)
    {
        TypeOfInvoke(invoke);
        var receiver = TypeOf(invoke.Recv);
        if (receiver.Kind == CTypeKind.Random)
            return RenderRandomMethod(RenderExpr(invoke.Recv), true,
                invoke.Method, invoke.Args);
        var foreign = ForeignInstanceMethod(receiver, invoke.Method);
        if (foreign != null)
            return RenderForeignCall(foreign, invoke.Args, invoke.Recv);
        var fact = ResolveInvokeFact(receiver, invoke.Method);
        // If a descendant redeclares it, dispatch on the runtime type (il-spec §9)
        // The dispatcher belongs to the topmost declaring class in the chain (even if the receiver has an intermediate class
        // type, it goes through the same dispatcher)
        if (IsPolymorphic(fact.ClassName, fact.Name))
        {
            var root = fact.ClassName;
            for (string? cur = _classes[root].BaseName; cur != null; cur = _classes[cur].BaseName)
            {
                bool declares = false;
                foreach (var m in _classes[cur].Methods)
                {
                    if (m.Name == fact.Name) declares = true;
                }
                if (declares) root = cur;
            }
            var rootFact = _facts.Method(root, fact.Name);
            return RenderMethodCall(rootFact, invoke.Recv, invoke.Args,
                Names.Dispatch(root, fact.Name));
        }
        return RenderMethodCall(fact, invoke.Recv, invoke.Args);
    }

    // Runtime type dispatch: for each method "declared at the top of the chain and
    // redeclared by a strict descendant", generate a switch from type_id → nearest implementation
    private void EmitDispatchers()
    {
        foreach (var cls in _program.Classes)
            foreach (var method in cls.Methods)
            {
                if (method.IsStatic) continue;
                string? b = _classes[cls.Name].BaseName;
                string? declaring = b != null
                    ? FindDeclaringClass(b, method.Name) : null;
                if (declaring != null) continue; // The root handles redeclarations
                if (!IsPolymorphic(cls.Name, method.Name)) continue;
                var fact = _facts.Method(cls.Name, method.Name);
                var parameterParts = new List<string>();
                parameterParts.Add($"{Names.Class(cls.Name)} *v_self");
                for (int i = 0; i < fact.Parameters.Count; i++)
                    parameterParts.Add($"{fact.Parameters[i].Type.CName} v_{Names.Id(fact.Parameters[i].Name)}_{i}");
                for (int i = 0; i < fact.OutParameters.Count; i++)
                    parameterParts.Add(OutPointerParameter(fact.OutParameters[i], i));
                var parameters = string.Join(", ", parameterParts);
                Line($"static {fact.ReturnType.CName}");
                Line($"{Names.Dispatch(cls.Name, method.Name)}({parameters})");
                Line("{");
                _indent++;
                Line("switch (((TcsObjectHeader *)v_self)->type_id) {");
                foreach (var target in _program.Classes)
                {
                    if (target.IsInterface || !IsAncestorOrSame(cls.Name, target.Name)) continue;
                    // A declaration with the same name but a different signature is not an override (a separate
                    // method in C#). Jump to the nearest matching declaration
                    string? impl = null;
                    for (string? cur = target.Name; cur != null && impl == null; cur = _classes[cur].BaseName)
                    {
                        bool declares = false;
                        foreach (var m in _classes[cur].Methods)
                        {
                            if (m.Name == method.Name && !m.IsStatic) declares = true;
                        }
                        if (declares && SameParameterTypes(_facts.Method(cur, method.Name).Parameters, fact.Parameters))
                            impl = cur;
                    }
                    if (impl == null) continue;
                    var callArgs = new List<string>();
                    callArgs.Add($"({Names.Class(impl)} *)v_self");
                    for (int i = 0; i < fact.Parameters.Count; i++)
                        callArgs.Add($"v_{Names.Id(fact.Parameters[i].Name)}_{i}");
                    for (int i = 0; i < fact.OutParameters.Count; i++)
                        callArgs.Add($"o_{Names.Id(fact.OutParameters[i].Name)}_{i}");
                    var call = Names.Method(impl, method.Name) + "(" +
                        string.Join(", ", callArgs) + ")";
                    Line($"case {Names.TypeId(target.Name)}: " +
                        (CType.Same(fact.ReturnType, CType.Void)
                            ? call + "; return;" : "return " + call + ";"));
                }
                // --vm-host: interface methods implemented by a VM class run in the VM (X-4)
                string? vm = null;
                if (_classes[cls.Name].IsInterface) vm = VmhDispatchBranch(fact, method.Name);
                if (vm != null)
                    Line("default: " + vm + " tcs_fault(\"dispatch\");");
                else
                    Line("default: tcs_fault(\"dispatch\");");
                Line("}");
                _indent--;
                Line("}");
                Line();
            }
    }

    // Whether the parameter type lists are the same (Select(p => p.Type).SequenceEqual in the C# version)
    private static bool SameParameterTypes(List<ParameterFact> a, List<ParameterFact> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!CType.Same(a[i].Type, b[i].Type)) return false;
        }
        return true;
    }

    // ==== tcs2c/CEmitter.Creation.cs ====
    // Construction expressions: new of class / struct, List / Dictionary literals (IlTable),
    // fixed-length arrays (IlNewArray).

    private string RenderNew(IlNewObj creation)
    {
        if (IsRandomTypeName(creation.TypeName)) return RenderRandomNew(creation);
        if (_facts.Structs.ContainsKey(creation.TypeName))
        {
            // Zero value of a struct. Explicit ctor calls (S.ctor) go through IlCall, so
            // they never arrive here with args
            if (creation.Args.Count != 0)
            {
                CFail.Report(
                    "struct constructor is not supported by the C backend: " +
                    creation.TypeName);
                return "";
            }
            return "((" + CType.Struct(creation.TypeName).CName + "){0})";
        }
        ExClass? cls;
        if (!_classes.TryGetValue(creation.TypeName, out cls))
        {
            CFail.Report($"unknown class: {creation.TypeName}");
            return "";
        }
        if (cls.IsInterface)
        {
            CFail.Report($"cannot instantiate interface: {cls.Name}");
            return "";
        }
        ExCtor? ctor = null;
        foreach (var c in CtorsOrDefault(cls))
        {
            string ctorName = c != null ? c.Name : "new";
            if (ctorName == creation.Ctor)
            {
                ctor = c;
                break;
            }
        }
        if (ctor == null && !(creation.Ctor == "new" && cls.Ctors.Count == 0))
        {
            CFail.Report($"unknown constructor: {cls.Name}.{creation.Ctor}");
            return "";
        }
        var function = Names.New(CtorSymbol(cls, ctor));
        var paramFacts = CtorParamFacts(ctor);
        var args = CompleteArguments(paramFacts, creation.Args, $"constructor {cls.Name}");
        if (args.Count == 0) return $"{function}()";
        var values = new List<TypedValue>();
        for (int i = 0; i < args.Count; i++)
        {
            CheckAssignable(paramFacts[i].Type, args[i],
                $"constructor argument {i} of {cls.Name}");
            values.Add(new TypedValue(paramFacts[i].Type, RenderCoerced(args[i], paramFacts[i].Type)));
        }
        return RenderOrderedCall(function, CType.Ref(cls.Name), values);
    }

    private CType TypeOfTable(IlTable table)
    {
        if (table.ObjectType != null) return _facts.MapType(table.ObjectType);
        bool hasKey = false;
        bool hasNameKey = false;
        foreach (var e in table.Entries)
        {
            if (e.Key != null) hasKey = true;
            if (e.NameKey != null) hasNameKey = true;
        }
        if (table.KeyType != null || hasKey)
            return TypeOfDictTable(table);
        if (hasNameKey)
        {
            CFail.Report("option-table IlTable is not supported");
            return CType.Void;
        }
        CType? element = null;
        if (table.ElementType != null) element = _facts.MapType(table.ElementType);
        foreach (var entry in table.Entries)
        {
            // If the contract has an element type, each item only needs to be assignable to it (including derived → base
            // upcasts). If there is none, infer the common type of the items
            if (element != null && table.ElementType != null)
            {
                CheckAssignable(element, entry.Value, "IlTable item");
                continue;
            }
            var itemType = TypeOf(entry.Value);
            element = element == null ? itemType : CommonType(element, itemType, "IlTable items");
        }
        if (element != null && !IsStorageType(element))
        {
            CFail.Report("unsupported List element type: " + element.Display());
            return CType.Void;
        }
        if (table.IsArray)
        {
            if (element == null)
            {
                CFail.Report("array literal needs an element type");
                return CType.Void;
            }
            return CType.Array(element);
        }
        return CType.List(element);
    }

    // Fixed-length array literal (new T[] { ... }): allocate by element count and store in order
    private string RenderArrayLiteral(IlTable table, CType type)
    {
        var array = Temp("array");
        var statements = new List<string>();
        statements.Add(
            $"TcsArray *{array} = tcs_typed(tcs_array_new({table.Entries.Count}, " +
            $"sizeof({type.ElementCName}), {LayoutRef(type.Element!)}), {RuntimeTypeId(type)}); ");
        for (int i = 0; i < table.Entries.Count; i++)
            statements.Add($"(({type.ElementCName} *){array}->data)[{i}] = " +
                RenderCoerced(table.Entries[i].Value, type.Element!) + "; ");
        return "({ " + string.Join("", statements) + array + "; })";
    }

    private string RenderTable(IlTable table)
    {
        if (table.ObjectType != null)
            return RenderObjectInitializer(table, TypeOfTable(table));
        bool hasKey = false;
        foreach (var e in table.Entries)
        {
            if (e.Key != null) hasKey = true;
        }
        if (table.KeyType != null || hasKey)
            return RenderDictTable(table);
        var type = TypeOfTable(table);
        if (type.Kind == CTypeKind.Array) return RenderArrayLiteral(table, type);
        if (type.Kind != CTypeKind.List) return "";
        var list = Temp("list");
        var elementSize = type.Element == null ? "0" : $"sizeof({type.ElementCName})";
        var layout = type.Element == null ? "NULL" : LayoutRef(type.Element);
        var statements = new List<string>();
        statements.Add(
            $"TcsList *{list} = tcs_typed(tcs_list_new({elementSize}, {layout}), " +
            $"{RuntimeTypeId(type)}); ");
        foreach (var entry in table.Entries)
        {
            var value = Temp("list_item");
            statements.Add(type.ElementCName);
            statements.Add(" ");
            statements.Add(value);
            statements.Add(" = ");
            statements.Add(RenderCoerced(entry.Value, type.Element!));
            statements.Add("; ");
            statements.Add("tcs_list_add(");
            statements.Add(list);
            statements.Add(", &");
            statements.Add(value);
            statements.Add(", sizeof(");
            statements.Add(value);
            statements.Add("), ");
            statements.Add(layout);
            statements.Add("); ");
        }
        return "({ " + string.Join("", statements) + list + "; })";
    }

    private CType TypeOfNewQueue(IlNewQueue queue)
    {
        var type = _facts.MapType(queue.Type);
        EnsureSupportedStorageType(type, "Queue element type");
        return type;
    }

    private string RenderNewQueue(IlNewQueue queue)
    {
        var type = TypeOfNewQueue(queue);
        if (type.Element == null) return "";
        return $"tcs_typed(tcs_queue_new(sizeof({type.Element!.CName}), " +
            $"{LayoutRef(type.Element!)}), {RuntimeTypeId(type)})";
    }

    private CType TypeOfNewHashSet(IlNewHashSet set)
    {
        var type = _facts.MapType(set.Type);
        EnsureSupportedStorageType(type, "HashSet element type");
        return type;
    }

    private string RenderNewHashSet(IlNewHashSet set)
    {
        var type = TypeOfNewHashSet(set);
        if (type.Element == null) return "";
        return $"tcs_typed(tcs_hashset_new({_facts.KeyKind(type.Element!)}, sizeof({type.Element!.CName}), " +
            $"{LayoutRef(type.Element!)}), {RuntimeTypeId(type)})";
    }

    private CType TypeOfNewArray(IlNewArray array)
    {
        RequireType(CType.I32, TypeOf(array.Length), "array length");
        var element = _facts.MapType(array.ElementType);
        var result = CType.Array(element);
        EnsureSupportedStorageType(result, "IlNewArray element type");
        return result;
    }

    private string RenderNewArray(IlNewArray array)
    {
        var type = TypeOfNewArray(array);
        return $"tcs_typed(tcs_array_new(" + RenderExpr(array.Length) + ", " +
            $"sizeof({type.ElementCName}), {LayoutRef(type.Element!)}), {RuntimeTypeId(type)})";
    }

    // ---- Foreign ----

    // ==== CEmitter.Foreign.cs ====

    // foreign (stub declarations from --ref): static methods / static
    // fields / enum constants implemented by the host in C, and data classes shared with the host (IsExternal). IL
    // carries only the signatures (IlExport.Foreign), and C declares `extern` tcs_host_<path>
    // and calls it. out parameters are passed by pointer, nullable scalars by value (TcsOpt*),
    // and reference types pass the tcs object pointer as is (the host only borrows it during the call;
    // to keep it across boundaries, use tcs_lib_hold).

    private static string HostName(string name)
    {
        var parts = new List<string>();
        foreach (var part in name.Split('.')) parts.Add(Names.Id(part));
        return "tcs_host_" + string.Join("_", parts);
    }

    private ExForeignMethod? ForeignMethod(string name)
    {
        foreach (var m in _program.ForeignMethods)
        {
            if (m.Name == name) return m;
        }
        return null;
    }

    // Walk the receiver's class chain from the bottom; if the nearest declaration is a stub's, it is a host function.
    // If a user class along the way declares the same name, leave it to a normal method call
    private ExForeignMethod? ForeignInstanceMethod(CType receiver, string method)
    {
        if (receiver.Kind != CTypeKind.Ref) return null;
        string? cur = receiver.Name;
        while (cur != null)
        {
            foreach (var m in _classes[cur].Methods)
            {
                if (m.Name == method) return null;
            }
            foreach (var m in _program.ForeignMethods)
            {
                if (m.Receiver == cur && m.Name.EndsWith("." + method)) return m;
            }
            cur = _classes[cur].BaseName;
        }
        return null;
    }

    private static string ShortName(ExForeignMethod method)
    {
        var u = new Utf8Bytes(method.Name);
        int dot = -1;
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] == '.') dot = i;
        }
        return ContractFacts.Sub(method.Name, dot + 1, u.Length);
    }

    // IlInvoke resolves by runtime type (il-reference §9). For each user subclass that redeclares a stub instance method
    // with the same signature, type_id → nearest implementation
    private List<ForeignOverrider> ForeignOverriders(ExForeignMethod method)
    {
        var result = new List<ForeignOverrider>();
        string? receiverName = method.Receiver;
        if (receiverName == null) return result;
        string owner = receiverName;
        var name = ShortName(method);
        var parameters = ForeignParameters(method);
        var returnType = _facts.MapType(method.ReturnType);
        foreach (var target in _program.Classes)
        {
            if (target.IsInterface || target.Name == owner || !IsAncestorOrSame(owner, target.Name)) continue;
            string? cur = target.Name;
            while (cur != owner)
            {
                string link = cur!;
                if (HasInstanceMethodNamed(_classes[link], name))
                {
                    var fact = _facts.Method(link, name);
                    if (CType.Same(fact.ReturnType, returnType) && SameParameterTypes(fact.Parameters, parameters))
                    {
                        result.Add(new ForeignOverrider(target.Name, link));
                        break;
                    }
                }
                cur = _classes[link].BaseName;
            }
        }
        return result;
    }

    private static bool HasInstanceMethodNamed(ExClass cls, string name)
    {
        foreach (var m in cls.Methods)
        {
            if (m.Name == name && !m.IsStatic) return true;
        }
        return false;
    }

    private string ForeignDispatcherSignature(ExForeignMethod method)
    {
        string head = "static " + _facts.MapType(method.ReturnType).CName + " "
            + Names.Dispatch(method.Receiver!, ShortName(method)) + "(";
        var parts = new List<string>();
        parts.Add(CType.Ref(method.Receiver!).CName + " v_self");
        var parameters = ForeignParameters(method);
        for (int i = 0; i < parameters.Count; i++) parts.Add(parameters[i].Type.CName + " v_" + i);
        return head + string.Join(", ", parts) + ")";
    }

    private void EmitForeignDispatchers()
    {
        foreach (var method in _program.ForeignMethods)
        {
            var overriders = ForeignOverriders(method);
            if (overriders.Count == 0) continue;
            var args = new List<string>();
            for (int i = 0; i < method.Parameters.Count; i++) args.Add("v_" + i);
            bool isVoid = CType.Same(_facts.MapType(method.ReturnType), CType.Void);
            Line(ForeignDispatcherSignature(method));
            Line("{");
            _indent++;
            Line("switch (((TcsObjectHeader *)v_self)->type_id) {");
            foreach (var o in overriders)
            {
                Line("case " + Names.TypeId(o.Target) + ": " + ForeignDispatchReturn(isVoid, ForeignDispatchCall(
                    Names.Method(o.Impl, ShortName(method)), "(" + Names.Class(o.Impl) + " *)v_self", args)));
            }
            Line("default: " + ForeignDispatchReturn(isVoid, ForeignDispatchCall(HostName(method.Name), "v_self", args)));
            Line("}");
            _indent--;
            Line("}");
            Line();
        }
    }

    private static string ForeignDispatchCall(string callee, string self0, List<string> args)
    {
        var all = new List<string>();
        all.Add(self0);
        foreach (var a in args) all.Add(a);
        return callee + "(" + string.Join(", ", all) + ")";
    }

    private static string ForeignDispatchReturn(bool isVoid, string call)
    {
        return isVoid ? call + "; return;" : "return " + call + ";";
    }

    private ExForeignValue? ForeignValue(IlField field)
    {
        if (field.Recv is IlVar receiver && TryResolve(receiver.Name) == null)
        {
            string key = receiver.Name + "." + field.Name;
            foreach (var v in _program.ForeignValues)
            {
                if (v.Name == key) return v;
            }
        }
        return null;
    }

    private ExForeignMethod? ForeignCallee(IlExpr callee)
    {
        if (callee is IlField field && field.Recv is IlVar receiver && TryResolve(receiver.Name) == null)
            return ForeignMethod(receiver.Name + "." + field.Name);
        return null;
    }

    private List<ParameterFact> ForeignParameters(ExForeignMethod method)
    {
        var list = new List<ParameterFact>();
        foreach (var p in method.Parameters) list.Add(new ParameterFact(p.Name, _facts.MapType(p.Type), p.Default));
        return list;
    }

    private void EmitForeignPrototypes()
    {
        foreach (var method in _program.ForeignMethods)
        {
            string returnName = _facts.MapType(method.ReturnType).CName;
            var parameters = new List<string>();
            if (method.Receiver != null) parameters.Add(CType.Ref(method.Receiver).CName);
            foreach (var p in method.Parameters) parameters.Add(_facts.MapType(p.Type).CName + (p.IsOut ? " *" : ""));
            Line("extern " + returnName + " " + HostName(method.Name) + "("
                + (parameters.Count == 0 ? "void" : string.Join(", ", parameters)) + ");");
            if (ForeignOverriders(method).Count > 0)
                Line(ForeignDispatcherSignature(method) + ";");
        }
        foreach (var value in _program.ForeignValues)
        {
            if (value.HasConstant) continue;
            Line("extern " + _facts.MapType(value.Type).CName + " " + HostName(value.Name) + "(void);");
        }
        Line();
    }

    private CType TypeOfForeignCall(ExForeignMethod method, List<IlExpr> args)
    {
        foreach (var p in method.Parameters)
        {
            if (p.IsOut)
            {
                CFail.Report("foreign out call requires assignment: " + method.Name);
                return CType.Void;
            }
        }
        var parameters = ForeignParameters(method);
        args = CompleteArguments(parameters, args, method.Name);
        for (int i = 0; i < args.Count && i < parameters.Count; i++)
            CheckAssignable(parameters[i].Type, args[i], "argument " + i + " of " + method.Name);
        return _facts.MapType(method.ReturnType);
    }

    // An instance method in IlCall("<class>.<method>", [self, args...]) is an explicit base
    // call: the first argument is the receiver, and it goes to the host implementation without the dispatcher
    private IlExpr? SplitForeignCall(ExForeignMethod method, List<IlExpr> args, out List<IlExpr> rest)
    {
        rest = args;
        string? owner = method.Receiver;
        if (owner == null) return null;
        if (args.Count == 0)
        {
            CFail.Report(method.Name + ": missing receiver");
            return null;
        }
        CheckAssignable(CType.Ref(owner), args[0], "receiver of " + method.Name);
        var tail = new List<IlExpr>();
        for (int i = 1; i < args.Count; i++) tail.Add(args[i]);
        rest = tail;
        return args[0];
    }

    private CType TypeOfForeignIlCall(ExForeignMethod method, List<IlExpr> args)
    {
        SplitForeignCall(method, args, out var rest);
        return TypeOfForeignCall(method, rest);
    }

    private string RenderForeignIlCall(ExForeignMethod method, List<IlExpr> args)
    {
        var receiver = SplitForeignCall(method, args, out var rest);
        return RenderForeignCall(method, rest, receiver, false);
    }

    private string RenderForeignCall(ExForeignMethod method, List<IlExpr> args, IlExpr? receiver = null, bool dispatch = true)
    {
        var result = TypeOfForeignCall(method, args);
        var parameters = ForeignParameters(method);
        args = CompleteArguments(parameters, args, method.Name);
        var values = new List<TypedValue>();
        if (receiver != null)
        {
            var type = CType.Ref(method.Receiver!);
            values.Add(new TypedValue(type, "(" + type.CName + ")" + RenderExpr(receiver)));
        }
        for (int i = 0; i < args.Count && i < parameters.Count; i++)
            values.Add(new TypedValue(parameters[i].Type, RenderCoerced(args[i], parameters[i].Type)));
        string callee = receiver != null && dispatch && ForeignOverriders(method).Count > 0
            ? Names.Dispatch(method.Receiver!, ShortName(method)) : HostName(method.Name);
        return RenderOrderedCall(callee, result, values, null, receiver != null);
    }

    // Returns the C expression of the value and puts its type in type
    private string RenderForeignValue(ExForeignValue value, out CType type)
    {
        var mapped = _facts.MapType(value.Type);
        if (value.HasConstant)
        {
            type = CType.I32;
            return Constants.I32(value.Constant);
        }
        type = mapped;
        return HostName(value.Name) + "()";
    }

    // External data class created with an object initializer (new Options { X = ... })
    private string RenderObjectInitializer(IlTable table, CType type)
    {
        if (type.Kind == CTypeKind.StructVal) return RenderStructInitializer(table, type);
        if (type.Kind != CTypeKind.Ref)
        {
            CFail.Report("object initializer of non-class type: " + type.Display());
            return "";
        }
        var cls = _classes[type.Name!];
        if (cls.Ctors.Count > 0)
        {
            CFail.Report("object initializer needs a parameterless class: " + cls.Name);
            return "";
        }
        var name = Temp("object");
        var text = new List<string>();
        text.Add(type.CName + " " + name + " = " + Names.New(cls.Name) + "(); ");
        foreach (var entry in table.Entries)
        {
            string? key = entry.NameKey;
            if (key == null)
            {
                CFail.Report("object initializer entry needs a member name");
                return "";
            }
            var field = FieldInChain(cls.Name, key);
            CheckAssignable(field.Type, entry.Value, "initializer of " + cls.Name + "." + key);
            text.Add(name + "->" + Names.Field(key) + " = " + RenderCoerced(entry.Value, field.Type) + "; ");
        }
        return "({ " + string.Join("", text) + name + "; })";
    }

    // Value of a --ref struct created with an object initializer (new Vec2 { X = ... }): start from the zero value and set the fields in the written order
    private string RenderStructInitializer(IlTable table, CType type)
    {
        var name = Temp("value");
        var text = new List<string>();
        text.Add(type.CName + " " + name + " = {0}; ");
        foreach (var entry in table.Entries)
        {
            string? key = entry.NameKey;
            if (key == null)
            {
                CFail.Report("object initializer entry needs a member name");
                return "";
            }
            var fieldType = _facts.StructField(type.Name!, key);
            CheckAssignable(fieldType, entry.Value, "initializer of " + type.Name + "." + key);
            text.Add(name + "." + Names.Field(key) + " = " + RenderCoerced(entry.Value, fieldType) + "; ");
        }
        return "({ " + string.Join("", text) + name + "; })";
    }

    // Foreign call with out parameters: (a, b) = api.poll(args), or (ret, a, b) = api.try(args) if there is a return value.
    // out is passed as a pointer to the variable (C# T is C T, and out T is T *)
    private bool EmitForeignMultiAssign(IlMultiAssign multi)
    {
        ExForeignMethod? method = null;
        List<IlExpr>? callArgs = null;
        if (multi.Values.Count == 1)
        {
            if (multi.Values[0] is IlCall call)
            {
                method = ForeignMethod(call.Callee);
                callArgs = call.Args;
            }
            else if (multi.Values[0] is IlDynCall dyn)
            {
                method = ForeignCallee(dyn.Callee);
                callArgs = dyn.Args;
            }
        }
        if (method == null || callArgs == null) return false;
        var returnType = _facts.MapType(method.ReturnType);
        int results = CType.Same(returnType, CType.Void) ? 0 : 1;
        var inputs = new List<ParameterFact>();
        int outCount = 0;
        foreach (var p in method.Parameters)
        {
            if (p.IsOut) outCount++;
            else inputs.Add(new ParameterFact(p.Name, _facts.MapType(p.Type), p.Default));
        }
        var args = CompleteArguments(inputs, callArgs, method.Name);
        RequireArity(method.Name, multi.Targets.Count, results + outCount);
        if (multi.Targets.Count != results + outCount) return true;
        var values = new List<string>();
        int input = 0;
        int output = results;
        foreach (var parameter in method.Parameters)
        {
            var type = _facts.MapType(parameter.Type);
            if (parameter.IsOut)
            {
                var targetExpr = multi.Targets[output];
                output++;
                if (targetExpr is IlVar target)
                {
                    if (target.Name == "_" && TryResolve(target.Name) == null)
                    {
                        var discard = Temp("discard");
                        Line(type.CName + " " + discard + ";");
                        values.Add("&" + discard);
                        continue;
                    }
                    Variable variable = multi.Declare && TryResolve(target.Name) == null
                        ? DeclareLocal(target.Name, type) : Resolve(target.Name);
                    RequireType(type, variable.Type, method.Name);
                    values.Add(variable.Boxed ? variable.CName : "&" + variable.CName);
                    if (variable.Boxed && NeedsBarrier(type)) Line("tcs_wb(" + variable.CName + ");");
                }
                else
                {
                    CFail.Report("foreign out target must be a local");
                    return true;
                }
            }
            else
            {
                if (input >= args.Count) return true;
                var value = Temp("host_arg");
                CheckAssignable(type, args[input], "argument " + input + " of " + method.Name);
                Line(type.CName + " " + value + " = " + RenderCoerced(args[input], type) + ";");
                input++;
                values.Add(value);
            }
        }
        string hostCall = HostName(method.Name) + "(" + string.Join(", ", values) + ")";
        if (results == 0)
        {
            Line(hostCall + ";");
            return true;
        }
        if (!(multi.Targets[0] is IlVar retTarget))
        {
            CFail.Report("foreign return target must be a local");
            return true;
        }
        if (retTarget.Name == "_" && TryResolve(retTarget.Name) == null)
        {
            Line(hostCall + ";");
            return true;
        }
        Variable ret = multi.Declare && TryResolve(retTarget.Name) == null
            ? DeclareLocal(retTarget.Name, returnType) : Resolve(retTarget.Name);
        RequireType(returnType, ret.Type, method.Name);
        Line(ret.Boxed ? "(*" + ret.CName + ") = " + hostCall + ";" : ret.CName + " = " + hostCall + ";");
        if (ret.Boxed && NeedsBarrier(returnType)) Line("tcs_wb(" + ret.CName + ");");
        return true;
    }

    // ==== CEmitter.Host.cs ====

    // Entry points of the shipping form: --lib external-linkage functions (init / entry / GC boundary / hold /
    // stats) and main for the executable form.

    // Statically linked shipping form (--lib): has no main, and exports initialization and each static void method
    // (arguments are int / float / bool and external data classes) with external linkage. An entry
    // automatically passes a frame boundary at the point where the outermost call returns (the C# stack is empty)
    // (tcs_gc_frame). A tcs pointer the host holds outside an entry
    // can cross a boundary only through a slot registered with tcs_lib_hold.
    // For external data class arguments, the host creates them with tcs_new_<type>(), fills the fields, and passes them without a boundary (tcs_lib_gc / tcs_lib_collect / another entry) in between (nursery objects move or are freed at a boundary).
    // Host pointers are invalid after returning from an entry, and records kept by the game are promoted at the boundary
    private void EmitLibEntryPoints()
    {
        // Type tags the host uses to check the element types of array / List / Dict foreign arguments and to create values
        // of the same type
        Line("#define TCS_TYPE_ARRAY_F32 " + RuntimeTypeId(CType.Array(CType.F32)));
        Line("#define TCS_TYPE_ARRAY_I32 " + RuntimeTypeId(CType.Array(CType.I32)));
        Line("#define TCS_TYPE_LIST_F32 " + RuntimeTypeId(CType.List(CType.F32)));
        Line("#define TCS_TYPE_LIST_I32 " + RuntimeTypeId(CType.List(CType.I32)));
        Line("#define TCS_TYPE_DICT_STRING_OBJECT "
            + RuntimeTypeId(CType.Dict(CType.String, CType.Object)));
        Line("void");
        Line("tcs_lib_init(void)");
        Line("{");
        _indent++;
        Line("tcs_gc_call_depth++;");
        Line("tcs_init_statics();");
        Line("if (--tcs_gc_call_depth == 0) tcs_gc_frame();");
        _indent--;
        Line("}");
        Line();
        // Explicit frame boundary (an empty frame if it follows the automatic entry boundary)
        Line("void");
        Line("tcs_lib_gc(void)");
        Line("{");
        _indent++;
        Line("if (tcs_gc_call_depth == 0) tcs_gc_frame();");
        _indent--;
        Line("}");
        Line();
        // Boundary + full old-generation GC (when the host wants to collect explicitly)
        Line("void");
        Line("tcs_lib_collect(void)");
        Line("{");
        _indent++;
        Line("if (tcs_gc_call_depth != 0) return;");
        Line("tcs_gc_frame();");
        Line("tcs_gc_collect();");
        _indent--;
        Line("}");
        Line();
        Line("size_t");
        Line("tcs_lib_heap_bytes(void)");
        Line("{");
        _indent++;
        Line("return tcs_gc_old_bytes + tcs_gc_nursery_bytes;");
        _indent--;
        Line("}");
        Line();
        Line("size_t");
        Line("tcs_lib_heap_objects(void)");
        Line("{");
        _indent++;
        Line("return tcs_gc_object_count + tcs_gc_nursery_objects;");
        _indent--;
        Line("}");
        Line();
        // Slots for tcs objects the host keeps across frames (updated to the promoted location at a boundary)
        Line("void");
        Line("tcs_lib_hold(void **slot)");
        Line("{");
        _indent++;
        Line("tcs_gc_hold(slot);");
        _indent--;
        Line("}");
        Line();
        Line("void");
        Line("tcs_lib_release(void **slot)");
        Line("{");
        _indent++;
        Line("tcs_gc_release(slot);");
        _indent--;
        Line("}");
        Line();
        // Fault hook. The handler receives kind, and if it returns, the process exits.
        // To continue, the handler longjmps out to the host frame that called the entry, and calls tcs_lib_unwind before the next entry
        Line("void");
        Line("tcs_lib_on_fault(void (*handler)(const char *kind))");
        Line("{");
        _indent++;
        Line("tcs_fault_handler = handler;");
        _indent--;
        Line("}");
        Line();
        // Cleanup after an entry aborted by a fault: the C# stack is now empty, so reset the nesting depth and pass a boundary
        Line("void");
        Line("tcs_lib_unwind(void)");
        Line("{");
        _indent++;
        Line("tcs_gc_call_depth = 0;");
        Line("tcs_gc_frame();");
        _indent--;
        Line("}");
        Line();
        foreach (var cls in _program.Classes)
        {
            if (cls.IsInterface) continue;
            foreach (var method in cls.Methods)
            {
                if (!method.IsStatic) continue;
                var fact = _facts.Method(cls.Name, method.Name);
                if (!CType.Same(fact.ReturnType, CType.Void)
                    || fact.OutParameters.Count > 0
                    || !AllEntryParameters(fact))
                    continue;
                Line("void");
                Line("tcs_entry_" + Names.Id(cls.Name) + "_" + Names.Id(method.Name) + "(" + ParameterList(fact) + ")");
                Line("{");
                _indent++;
                Line("tcs_gc_call_depth++;");
                var argList = new List<string>();
                for (int i = 0; i < fact.Parameters.Count; i++) argList.Add("v_" + Names.Id(fact.Parameters[i].Name) + "_" + i);
                Line(Names.Method(cls.Name, method.Name) + "(" + string.Join(", ", argList) + ");");
                Line("if (--tcs_gc_call_depth == 0) tcs_gc_frame();");
                _indent--;
                Line("}");
                Line();
            }
        }
    }

    private bool AllEntryParameters(MethodFact fact)
    {
        foreach (var p in fact.Parameters)
        {
            if (!IsEntryParameter(p.Type)) return false;
        }
        return true;
    }

    private bool IsEntryParameter(CType type)
    {
        return type.Kind == CTypeKind.I32 || type.Kind == CTypeKind.F32 || type.Kind == CTypeKind.Bool
            || type.Kind == CTypeKind.Ref && _facts.IsDeclaredExternal(type.Name!);
    }

    private void EmitEntryPoint(EntryPoint? entry)
    {
        Line("int");
        Line("main(void)");
        Line("{");
        _indent++;
        // In the executable form, all of Main is one frame (there is no boundary, so the GC does not run)
        Line("tcs_init_statics();");
        if (_digestF32) Line("tcs_digest = UINT32_C(2166136261);");
        if (entry != null)
            Line(Names.Method(entry.Class.Name, entry.Method.Name) + "();");
        if (_digestF32)
            Line("printf(\"%08\" PRIx32 \"\\n\", tcs_digest);");
        Line("return 0;");
        _indent--;
        Line("}");
    }

    // ==== CEmitter.VmHost.cs ====

    // Per-module mixing (docs/tcs/object-model.md §10): --lib --vm-host emits glue (tcs_vm_register) that registers the public
    // static methods of this module (the side fixed in C, such as the editor) as tcs VM host functions. The VM module (content)
    // takes this module's source as --ref, compiles it, and calls them by the same names (IlExport.HostSurface).
    // There is one heap and one GC (shared heap, docs/tcs/object-model.md §10.4). Values crossing the boundary are int / float / bool / enum, string
    // (not copied; §10.6), class and object (as is; type numbers are matched too), delegate and interface (§10.5).
    // Structs that can be passed as a blob (fields are int / float / bool / enum and structs made only of those) and `T?` pass as `S{type}` in the VM host ABI, and out passes as a `&` item (docs/tcs/vm-design.md §8.5).
    // Methods with shapes that cannot be called (structs with reference fields, List / Dictionary, out on interface methods) are not registered (the reason goes in a C comment).

    public List<ExHostFunction>? VmHostSurface;

    // The class for h.TypeDisplay (looked up by display name or IL name)
    private ExClass? VmhHostClass(ExHostFunction h)
    {
        foreach (var c in _program.Classes)
        {
            if (c.DisplayName == h.TypeDisplay || c.Name == h.TypeDisplay) return c;
        }
        return null;
    }

    // Whether there is a method named name (anyInstance to also count instance methods)
    private static bool VmhHasMethod(ExClass cls, string name, bool anyInstance)
    {
        foreach (var m in cls.Methods)
        {
            if (m.Name == name && (anyInstance || m.IsStatic)) return true;
        }
        return false;
    }

    // Reason it cannot be registered (null if it can). Interface methods call the dispatcher, so they cannot have out
    private string? VmhWhy(MethodFact fact, bool iface)
    {
        if (iface && fact.OutParameters.Count > 0) return "out";
        foreach (var bad in fact.Parameters)
        {
            if (Letter(bad.Type, false) == null) return "parameter " + bad.Name + ": " + bad.Type.Display();
        }
        foreach (var bad in fact.OutParameters)
        {
            if (bad.Type.Kind == CTypeKind.Closure || Letter(bad.Type, false) == null) return "out " + bad.Name + ": " + bad.Type.Display();
        }
        if (!CType.Same(fact.ReturnType, CType.Void) && Letter(fact.ReturnType, true) == null) return "return " + fact.ReturnType.Display();
        return null;
    }

    private void EmitVmHost(List<ExHostFunction> surface)
    {
        Line("/* ---- tcs VM host functions (--vm-host) ---- */");
        var registered = new List<VmhRegistration>();
        // Wrap a VM delegate in a tcs2c closure (X-4): a trampoline is made per function type. cells[0] is the VM closure
        _vmhWrappers = new Dictionary<string, string>();
        _vmhInvokers.Clear();
        foreach (var h in surface)
        {
            var cls = VmhHostClass(h);
            if (cls == null || !VmhHasMethod(cls, h.Method, false)) continue;
            foreach (var p in _facts.Method(cls.Name, h.Method).Parameters)
            {
                if (p.Type.Kind == CTypeKind.Closure) VmhWrapper(p.Type);
            }
            var rt = _facts.Method(cls.Name, h.Method).ReturnType;
            if (rt.Kind == CTypeKind.Closure && VmhCallable(rt))
                VmhInvoker(rt);
        }
        foreach (var h in surface)
        {
            var cls = VmhHostClass(h);
            // Interface method (X-4): the VM calls a method of a --ref interface. It calls this module's dispatcher
            bool iface = cls != null && cls.IsInterface;
            if (cls == null || !VmhHasMethod(cls, h.Method, iface)) continue;
            var fact = _facts.Method(cls.Name, h.Method);
            string? why = VmhWhy(fact, iface);
            if (why != null)
            {
                Line("/* " + h.Name + ": not a VM host function (" + why + ") */");
                continue;
            }
            var fn = "tcs_vmh_" + registered.Count;
            int at = iface ? 1 : 0;
            // Items are in C# declaration order (out gets &). The C function lists the out `T *` after the value arguments
            var tokens = new List<string>();
            var argParts = new List<string>();
            var outParts = new List<string>();
            foreach (var name in fact.Metadata.Parameters)
            {
                string k = (at + tokens.Count).ToString();
                var o = VmhParam(fact.OutParameters, name);
                if (o != null)
                {
                    tokens.Add("&" + Letter(o.Type, false)!);
                    outParts.Add("(" + o.Type.CName + " *)a[" + k + "].p");
                    continue;
                }
                var p = VmhParam(fact.Parameters, name)!;
                tokens.Add(Letter(p.Type, false)!);
                switch (p.Type.Kind)
                {
                    case CTypeKind.I32: argParts.Add("a[" + k + "].i"); break;
                    case CTypeKind.F32: argParts.Add("a[" + k + "].f"); break;
                    case CTypeKind.Bool: argParts.Add("a[" + k + "].i != 0"); break;
                    case CTypeKind.String: argParts.Add("tcs_vmh_str_in(a[" + k + "].p)"); break;
                    case CTypeKind.Closure: argParts.Add(VmhWrapper(p.Type) + "(a[" + k + "].p)"); break;
                    // The VM passes struct and T? as a pointer to a blob in C layout
                    case CTypeKind.StructVal:
                    case CTypeKind.Nullable:
                        argParts.Add("*(const " + p.Type.CName + " *)a[" + k + "].p");
                        break;
                    default: argParts.Add("(" + p.Type.CName + ")a[" + k + "].p"); break;
                }
            }
            foreach (var o in outParts) argParts.Add(o);
            var ps = (iface ? "o" : "") + string.Join("", tokens);
            string rs = CType.Same(fact.ReturnType, CType.Void) ? "" : Letter(fact.ReturnType, true)!;
            Line("static TvmStatus");
            Line(fn + "(TvmVm *vm, TvmSlot *a, TvmSlot *r, void *user)");
            Line("{");
            _indent++;
            Line("(void)vm; (void)a; (void)r; (void)user;");
            Line("tcs_gc_call_depth++;");
            string args = string.Join(", ", argParts);
            string call = iface
                ? Names.Dispatch(cls.Name, h.Method) + "((" + Names.Class(cls.Name) + " *)tcs_nonnull(a[0].p)" + (args != "" ? ", " + args : "") + ")"
                : Names.Method(cls.Name, h.Method) + "(" + args + ")";
            // A returned string is copied to a VM string before the frame boundary (where the nursery moves)
            switch (fact.ReturnType.Kind)
            {
                case CTypeKind.Void: Line(call + ";"); break;
                case CTypeKind.I32: Line("r[0].i = " + call + ";"); break;
                case CTypeKind.F32: Line("r[0].f = " + call + ";"); break;
                case CTypeKind.Bool: Line("r[0].i = " + call + " ? 1 : 0;"); break;
                case CTypeKind.Ref:
                case CTypeKind.Object:
                    Line("r[0].p = " + call + ";");
                    break;
                case CTypeKind.Closure: Line("r[0].p = " + call + ";"); break;
                // struct and T? return values are written to the blob the VM passes
                case CTypeKind.StructVal:
                case CTypeKind.Nullable:
                    Line("*(" + fact.ReturnType.CName + " *)r[0].p = " + call + ";");
                    break;
                default:
                    Line("r[0].p = " + call + "; /* TcsString has the same byte layout as a VM string */");
                    break;
            }
            Line("if (--tcs_gc_call_depth == 0) tcs_gc_frame();");
            Line("return TVM_OK;");
            _indent--;
            Line("}");
            Line();
            registered.Add(new VmhRegistration(h.Name, ps, rs, fn));
        }
        EmitVmSharedHeap();
        Line("/* Registers the public static methods of this module as VM host functions, and makes this module's runtime use the VM heap");
        Line("   (shared heap: one heap and one GC. docs/tcs/object-model.md §10.2). Call it before tvm_load. Returns the number of registered functions */");
        Line("int");
        Line("tcs_vm_register(TvmVm *vm)");
        Line("{");
        _indent++;
        Line("int n = 0;");
        Line("tcs_gc_foreign_refs = tvm_heap_refs;");
        Line("tcs_gc_root_hook = tvm_heap_roots;");
        Line("tvm_use_heap(vm, &tcs_vmh_heap);");
        Line("tcs_vmh_vm = vm;");
        // Match type numbers (object-model §10.6): objects of this module's classes have the same type_id even when created in the VM,
        // and VM type tests check implementations of this module's interfaces with tcs_is_*
        foreach (var cls in _program.Classes)
        {
            Line(cls.IsInterface
                ? "tvm_host_iface(vm, \"" + cls.Name + "\", " + Names.InterfaceCheck(cls.Name) + ");"
                : "tvm_host_type(vm, \"" + cls.Name + "\", " + Names.TypeId(cls.Name) + ");");
        }
        foreach (var inv in _vmhInvokers)
            Line("tvm_host_delegate(vm, \"" + inv.Sig + "\", " + inv.Fn + ");");
        foreach (var reg in registered)
            Line("n += tvm_host(vm, \"" + reg.Name + "\", \"" + reg.Params + "\", \"" + reg.Rets + "\", " + reg.Fn + ", NULL);");
        Line("return n;");
        _indent--;
        Line("}");
        Line();
    }

    // shared heap: function table that delegates VM allocation and collection to this module's runtime (nursery and old generation, write barrier)
    private void EmitVmSharedHeap()
    {
        Line("static void *tcs_vmh_alloc(uint32_t kind, size_t size) { return tcs_gc_alloc(kind, NULL, size); }");
        Line("static void tcs_vmh_enter(void) { tcs_gc_call_depth++; }");
        Line("static void tcs_vmh_leave(void) { tcs_gc_call_depth--; }");
        Line("static void tcs_vmh_barrier(const void *owner) { tcs_wb(owner); }");
        Line("static void tcs_vmh_frame(void) { if (tcs_gc_call_depth == 0) tcs_gc_frame(); }");
        // Rewiring for reload (X-3): passes all old-generation objects and the locations of this module's static and hold references
        Line("static void");
        Line("tcs_vmh_visit_value(void *value, const TcsLayout *layout, TvmSlotFn fn)");
        Line("{");
        _indent++;
        Line("uint32_t i;");
        Line("if (layout == NULL) return;");
        Line("for (i = 0; i < layout->count; i++) fn((void **)((unsigned char *)value + layout->offsets[i]));");
        _indent--;
        Line("}");
        Line("static void");
        Line("tcs_vmh_visit_object(TcsGcHeader *hdr, TvmSlotFn fn)");
        Line("{");
        _indent++;
        Line("void *payload = TCS_GC_PAYLOAD(hdr);");
        Line("size_t i;");
        Line("switch (hdr->kind) {");
        Line("case TCS_KIND_OBJECT: tcs_vmh_visit_value(payload, hdr->layout, fn); break;");
        Line("case TCS_KIND_ARRAY: {");
        Line("    TcsArray *array = payload;");
        Line("    for (i = 0; hdr->layout != NULL && i < array->length; i++)");
        Line("        tcs_vmh_visit_value(array->data + i * array->element_size, hdr->layout, fn);");
        Line("    break;");
        Line("}");
        Line("case TCS_KIND_LIST: {");
        Line("    TcsList *list = payload;");
        Line("    for (i = 0; hdr->layout != NULL && list->data != NULL && i < list->length; i++)");
        Line("        tcs_vmh_visit_value((unsigned char *)list->data + i * list->element_size, hdr->layout, fn);");
        Line("    break;");
        Line("}");
        Line("case TCS_KIND_DICT_NODE: {");
        Line("    TcsDictNode *node = payload;");
        Line("    fn((void **)&node->key_s);");
        Line("    tcs_vmh_visit_value(node->value, hdr->layout, fn);");
        Line("    break;");
        Line("}");
        Line("case TCS_KIND_CLOSURE: {");
        Line("    TcsClosure *closure = payload;");
        Line("    size_t cells = (hdr->size - sizeof(TcsClosure)) / sizeof(void *);");
        Line("    for (i = 0; i < cells; i++) fn(&closure->cells[i]);");
        Line("    break;");
        Line("}");
        Line("case TCS_KIND_FOREIGN: tvm_heap_refs(payload, fn); break;");
        Line("default: break;");
        Line("}");
        _indent--;
        Line("}");
        Line("static void");
        Line("tcs_vmh_visit_all(TvmSlotFn fn)");
        Line("{");
        _indent++;
        Line("TcsGcHeader *hdr;");
        Line("size_t i;");
        foreach (var cls in StaticOwners)
        {
            foreach (var field in cls.Fields)
            {
                if (!field.IsStatic) continue;
                var fact = _facts.Field(cls.Name, field.Name);
                var name = Names.StaticField(cls.Name, field.Name);
                if (IsPointerType(fact.Type))
                    Line("fn((void **)&" + name + ");");
                else if (fact.Type.Kind == CTypeKind.StructVal
                    || fact.Type.Kind == CTypeKind.Nullable && fact.Type.Element!.Kind == CTypeKind.StructVal)
                    Line("tcs_vmh_visit_value(&" + name + ", " + LayoutRef(fact.Type) + ", fn);");
            }
        }
        Line("for (i = 0; i < tcs_gc_hold_count; i++) fn(tcs_gc_holds[i]);");
        Line("for (hdr = tcs_gc_objects; hdr != NULL; hdr = hdr->next) tcs_vmh_visit_object(hdr, fn);");
        _indent--;
        Line("}");
        Line("static void");
        Line("tcs_vmh_each(void (*fn)(void *payload, uint32_t kind, void *ctx), void *ctx)");
        Line("{");
        _indent++;
        Line("TcsGcHeader *hdr;");
        Line("for (hdr = tcs_gc_objects; hdr != NULL; hdr = hdr->next) fn(TCS_GC_PAYLOAD(hdr), hdr->kind, ctx);");
        _indent--;
        Line("}");
        Line("static const TvmHeap tcs_vmh_heap = { tcs_vmh_alloc, tcs_vmh_enter, tcs_vmh_leave, tcs_vmh_barrier,");
        Line("    tcs_vmh_frame, tcs_vmh_visit_all, tcs_vmh_each };");
        Line();
    }

    private Dictionary<string, string> _vmhWrappers = new Dictionary<string, string>();
    private List<VmhInvokerEntry> _vmhInvokers = new List<VmhInvokerEntry>();

    private static string VmhSigLetter(CType t)
    {
        switch (t.Kind)
        {
            case CTypeKind.I32: return "i";
            case CTypeKind.F32: return "f";
            case CTypeKind.Bool: return "b";
            case CTypeKind.String: return "s";
        }
        return "o";
    }

    /// <summary>Invoke through which the VM calls a C closure (function type t) (X-4). The VM's CALLD looks it up by sig.</summary>
    private void VmhInvoker(CType t)
    {
        var letters = new List<string>();
        foreach (var p in t.Parameters!) letters.Add(VmhSigLetter(p));
        var sig = string.Join("", letters) + ":" + (t.Element!.Kind == CTypeKind.Void ? "" : VmhSigLetter(t.Element));
        foreach (var x in _vmhInvokers)
        {
            if (x.Sig == sig) return;
        }
        var fn = "tcs_vmh_inv_" + _vmhInvokers.Count;
        _vmhInvokers.Add(new VmhInvokerEntry(sig, fn));
        Line("static TvmStatus");
        Line(fn + "(void *closure, const TvmSlot *a, TvmSlot *r)");
        Line("{");
        _indent++;
        Line("TcsClosure *c = closure;");
        Line("(void)a; (void)r;");
        var argParts = new List<string>();
        for (int i = 0; i < t.Parameters.Count; i++) argParts.Add(", " + VmhFromSlot(t.Parameters[i], "a[" + i + "]"));
        var call = "((" + ClosureFnPtrType(t) + ")c->fn)(c->cells" + string.Join("", argParts) + ")";
        Line("tcs_gc_call_depth++;");
        if (t.Element.Kind == CTypeKind.Void) Line(call + ";");
        else if (t.Element.Kind == CTypeKind.String)
        {
            Line("TcsString *s = " + call + ";");
            Line(VmhToSlot(t.Element, "s", "r[0]"));
        }
        else Line(VmhToSlot(t.Element, call, "r[0]"));
        Line("tcs_gc_call_depth--;");
        Line("return TVM_OK;");
        _indent--;
        Line("}");
    }

    private static bool VmhValue(CType t, ContractFacts facts)
    {
        return t.Kind == CTypeKind.I32 || t.Kind == CTypeKind.F32 || t.Kind == CTypeKind.Bool || t.Kind == CTypeKind.String || t.Kind == CTypeKind.Object
            || t.Kind == CTypeKind.Ref && !facts.IsDeclaredExternal(t.Name!);
    }

    /// <summary>Statement that writes the C value expr to VM slot a[i].</summary>
    private static string VmhToSlot(CType t, string expr, string slot)
    {
        switch (t.Kind)
        {
            case CTypeKind.I32: return slot + ".i = " + expr + ";";
            case CTypeKind.F32: return slot + ".f = " + expr + ";";
            case CTypeKind.Bool: return slot + ".i = " + expr + " ? 1 : 0;";
            case CTypeKind.String: return slot + ".p = " + expr + ";";
        }
        return slot + ".p = " + expr + ";";
    }

    /// <summary>Expression that turns a VM slot into a value of C type t.</summary>
    private static string VmhFromSlot(CType t, string slot)
    {
        switch (t.Kind)
        {
            case CTypeKind.I32: return slot + ".i";
            case CTypeKind.F32: return slot + ".f";
            case CTypeKind.Bool: return "(" + slot + ".i != 0)";
            case CTypeKind.String: return "tcs_vmh_str_in(" + slot + ".p)";
        }
        return "(" + t.CName + ")" + slot + ".p";
    }

    /// <summary>Branch of an interface dispatcher to a VM object (kind FOREIGN) (X-4). null if the shape cannot be called.</summary>
    private string? VmhDispatchBranch(MethodFact fact, string method)
    {
        if (VmHostSurface == null || fact.OutParameters.Count > 0) return null;
        foreach (var p in fact.Parameters)
        {
            if (!VmhValue(p.Type, _facts)) return null;
        }
        if (!CType.Same(fact.ReturnType, CType.Void) && !VmhValue(fact.ReturnType, _facts)) return null;
        var sb = new List<string>();
        int n = fact.Parameters.Count;
        sb.Add("if (TCS_GC_HEADER(v_self)->kind == TCS_KIND_FOREIGN) { ");
        sb.Add("TvmSlot a[" + (n > 1 ? n : 1) + "], r[1]; ");
        for (int i = 0; i < n; i++)
            sb.Add(VmhToSlot(fact.Parameters[i].Type, "v_" + Names.Id(fact.Parameters[i].Name) + "_" + i, "a[" + i + "]") + " ");
        bool hasRet = !CType.Same(fact.ReturnType, CType.Void);
        sb.Add("if (tvm_invoke_method(tcs_vmh_vm, v_self, \"" + method + "\", a, " + n + ", r, " + (hasRet ? "1" : "0") + ") != TVM_OK) ");
        sb.Add("{ fprintf(stderr, \"%s\\n\", tvm_error(tcs_vmh_vm)); tcs_fault(\"vm-method\"); } ");
        sb.Add(hasRet ? "return " + VmhFromSlot(fact.ReturnType, "r[0]") + "; }" : "return; }");
        return string.Join("", sb);
    }

    // Value types that can cross the boundary (scalar / string / object / classes that are not external data)
    private bool VmhCallableKind(CType p, bool allowVoid)
    {
        return allowVoid && p.Kind == CTypeKind.Void
            || p.Kind == CTypeKind.I32 || p.Kind == CTypeKind.F32 || p.Kind == CTypeKind.Bool || p.Kind == CTypeKind.String
            || p.Kind == CTypeKind.Object || p.Kind == CTypeKind.Ref && !_facts.IsDeclaredExternal(p.Name!);
    }

    /// <summary>Function types that can wrap a VM delegate (parameters and return value are scalar / string / class / object).</summary>
    private bool VmhCallable(CType t)
    {
        foreach (var p in t.Parameters!)
        {
            if (!VmhCallableKind(p, false)) return false;
        }
        return VmhCallableKind(t.Element!, true);
    }

    /// <summary>Name of the function that wraps a VM delegate of function type t in a tcs2c closure (if missing, it is emitted along with the trampoline).</summary>
    private string VmhWrapper(CType t)
    {
        var key = ClosureFnPtrType(t);
        string? existing;
        if (_vmhWrappers.TryGetValue(key, out existing)) return existing;
        if (!VmhCallable(t)) return "";
        int n = _vmhWrappers.Count;
        string name = "tcs_vmh_wrap_" + n;
        _vmhWrappers[key] = name;
        var ps = t.Parameters!;
        var paramParts = new List<string>();
        for (int i = 0; i < ps.Count; i++) paramParts.Add(", " + ps[i].CName + " p" + i);
        string paramList = string.Join("", paramParts);
        Line("static " + t.Element!.CName);
        Line("tcs_vmh_tramp_" + n + "(void **cells" + paramList + ")");
        Line("{");
        _indent++;
        Line("TvmSlot args[" + (ps.Count > 1 ? ps.Count : 1) + "], rets[1];");
        for (int i = 0; i < ps.Count; i++)
        {
            switch (ps[i].Kind)
            {
                case CTypeKind.I32: Line("args[" + i + "].i = p" + i + ";"); break;
                case CTypeKind.F32: Line("args[" + i + "].f = p" + i + ";"); break;
                case CTypeKind.Bool: Line("args[" + i + "].i = p" + i + " ? 1 : 0;"); break;
                case CTypeKind.String: Line("args[" + i + "].p = p" + i + ";"); break;
                default: Line("args[" + i + "].p = p" + i + ";"); break;
            }
        }
        bool hasRet = t.Element.Kind != CTypeKind.Void;
        Line("if (tvm_invoke(tcs_vmh_vm, cells[0], args, " + ps.Count + ", rets, " + (hasRet ? "1" : "0") + ") != TVM_OK) {");
        Line("    fprintf(stderr, \"%s\\n\", tvm_error(tcs_vmh_vm));");
        Line("    tcs_fault(\"vm-delegate\");");
        Line("}");
        if (hasRet)
        {
            switch (t.Element.Kind)
            {
                case CTypeKind.I32: Line("return rets[0].i;"); break;
                case CTypeKind.F32: Line("return rets[0].f;"); break;
                case CTypeKind.Bool: Line("return rets[0].i != 0;"); break;
                case CTypeKind.String: Line("return tcs_vmh_str_in(rets[0].p);"); break;
                default: Line("return (" + t.Element.CName + ")rets[0].p;"); break;
            }
        }
        _indent--;
        Line("}");
        Line("static TcsClosure *");
        Line(name + "(void *closure)");
        Line("{");
        _indent++;
        Line("TcsClosure *c;");
        Line("if (closure == NULL) return NULL;");
        Line("c = tcs_new_closure((void *)tcs_vmh_tramp_" + n + ", 1);");
        Line("c->cells[0] = closure;");
        Line("return c;");
        _indent--;
        Line("}");
        Line();
        return name;
    }

    /// <summary>Kind character of a VM host function. null for types that cannot cross the boundary.</summary>
    private string? Letter(CType t, bool ret)
    {
        switch (t.Kind)
        {
            case CTypeKind.I32: return "i";
            case CTypeKind.F32: return "f";
            case CTypeKind.Bool: return "b";
            case CTypeKind.String: return "s";
            case CTypeKind.Ref: return !_facts.IsDeclaredExternal(t.Name!) ? "o" : null;
            case CTypeKind.Object: return "o";
            case CTypeKind.Closure: return VmhCallable(t) ? "o" : null;
            case CTypeKind.StructVal:
            case CTypeKind.Nullable:
                return VmhBlittable(t, 0) ? "S{" + VmhStructName(t) + "}" : null;
        }
        return null;
    }

    // Name in the VM type table (the VM module takes this module's source as --ref, so structs have the same IL names; T? has a trailing ?)
    private static string VmhStructName(CType t)
    {
        if (t.Kind != CTypeKind.Nullable) return t.Name!;
        switch (t.Element!.Kind)
        {
            case CTypeKind.I32: return "int?";
            case CTypeKind.F32: return "float?";
            case CTypeKind.Bool: return "bool?";
        }
        string inner = t.Element.Name!;
        return inner + "?";
    }

    // Values the VM can pass as a blob in C layout: structs whose fields are int / float / bool / enum or structs made only of those (T? too)
    private bool VmhBlittable(CType t, int depth)
    {
        if (depth > 32) return false;
        switch (t.Kind)
        {
            case CTypeKind.I32:
            case CTypeKind.F32:
            case CTypeKind.Bool:
                return true;
            case CTypeKind.Nullable:
                return VmhBlittable(t.Element!, depth + 1);
            case CTypeKind.StructVal:
                {
                    ExStruct? st;
                    if (!_facts.Structs.TryGetValue(t.Name!, out st)) return false;
                    foreach (var f in st.Fields)
                    {
                        if (!f.IsStatic && !VmhBlittable(_facts.MapType(f.Type), depth + 1)) return false;
                    }
                    return true;
                }
        }
        return false;
    }

    private static ParameterFact? VmhParam(List<ParameterFact> list, string name)
    {
        foreach (var p in list)
        {
            if (p.Name == name) return p;
        }
        return null;
    }

    // ==== CEmitter.Gc.cs ====

    // Generated code for the GC: pointer maps of class / struct (TcsLayout), static field
    // root scan functions, and string literals as static objects. The runtime-side
    // contract is CRuntime.Gc (precise trace by kind + layout, conservative scan of the stack).

    private const string LiteralsMarker = "/*__TCS2C_LITERALS__*/";
    private Dictionary<string, string> _literals = new Dictionary<string, string>();
    private List<string> _literalDecls = new List<string>();

    // Element / value layout reference passed to the runtime at allocation
    private string LayoutRef(CType type)
    {
        switch (type.Kind)
        {
            case CTypeKind.I32:
            case CTypeKind.F32:
            case CTypeKind.Bool:
                return "NULL";
            case CTypeKind.String:
            case CTypeKind.Ref:
            case CTypeKind.Array:
            case CTypeKind.List:
            case CTypeKind.Queue:
            case CTypeKind.HashSet:
            case CTypeKind.Dict:
            case CTypeKind.Closure:
            case CTypeKind.Random:
            case CTypeKind.Object:
                return "&tcs_layout_ptr";
            case CTypeKind.StructVal:
                return "&" + Names.StructLayout(type.Name!);
            case CTypeKind.Nullable:
                return type.Element!.Kind == CTypeKind.StructVal
                    ? "&" + Names.NullableLayout(type.Element.Name!) : "NULL";
        }
        CFail.Report("type has no storage layout: " + type.Display());
        return "";
    }

    private static bool IsPointerType(CType type)
    {
        return type.Kind == CTypeKind.String || type.Kind == CTypeKind.Ref || type.Kind == CTypeKind.Array || type.Kind == CTypeKind.List
            || type.Kind == CTypeKind.Queue || type.Kind == CTypeKind.HashSet || type.Kind == CTypeKind.Dict || type.Kind == CTypeKind.Closure
            || type.Kind == CTypeKind.Random || type.Kind == CTypeKind.Object;
    }

    // Pointer slots in one struct value (byte offset expressions). struct-in-struct is
    // flattened by adding offsetof values
    private List<string> StructPointerSlots(string structName)
    {
        var result = new List<string>();
        var st = _facts.Structs[structName];
        var cName = Names.Class(structName);
        foreach (var field in st.Fields)
        {
            var type = _facts.MapType(field.Type);
            var at = "offsetof(" + cName + ", " + Names.Field(field.Name) + ")";
            if (IsPointerType(type))
                result.Add(at);
            else if (type.Kind == CTypeKind.StructVal)
            {
                foreach (var inner in StructPointerSlots(type.Name!))
                    result.Add(at + " + " + inner);
            }
            else if (type.Kind == CTypeKind.Nullable && type.Element!.Kind == CTypeKind.StructVal)
            {
                foreach (var inner in StructPointerSlots(type.Element.Name!))
                    result.Add(at + " + offsetof(" + type.CName + ", v) + " + inner);
            }
        }
        return result;
    }

    private List<string> ClassPointerSlots(string className)
    {
        var result = new List<string>();
        var cName = Names.Class(className);
        foreach (var link in ChainRootFirst(className))
        {
            foreach (var field in link.Fields)
            {
                if (field.IsStatic) continue;
                var type = _facts.Field(link.Name, field.Name).Type;
                var at = "offsetof(" + cName + ", " + Names.Field(field.Name) + ")";
                if (IsPointerType(type))
                    result.Add(at);
                else if (type.Kind == CTypeKind.StructVal)
                {
                    foreach (var inner in StructPointerSlots(type.Name!))
                        result.Add(at + " + " + inner);
                }
                else if (type.Kind == CTypeKind.Nullable && type.Element!.Kind == CTypeKind.StructVal)
                {
                    foreach (var inner in StructPointerSlots(type.Element.Name!))
                        result.Add(at + " + offsetof(" + type.CName + ", v) + " + inner);
                }
            }
        }
        return result;
    }

    private void EmitLayout(string layoutName, string cName, List<string> slots)
    {
        if (slots.Count == 0)
        {
            Line("static const TcsLayout " + layoutName + " = { sizeof(" + cName + "), 0, NULL };");
            return;
        }
        Line("static const uint32_t " + layoutName + "_offsets[] = {");
        _indent++;
        foreach (var slot in slots) Line(slot + ",");
        _indent--;
        Line("};");
        Line("static const TcsLayout " + layoutName + " = "
            + "{ sizeof(" + cName + "), " + slots.Count + ", " + layoutName + "_offsets };");
    }

    // Called after the struct typedefs (EmitStructTypedefs) and class definitions
    private void EmitLayouts()
    {
        foreach (var st in _program.Structs)
        {
            EmitLayout(Names.StructLayout(st.Name), Names.Class(st.Name), StructPointerSlots(st.Name));
            var option = CType.Nullable(CType.Struct(st.Name));
            var optionSlots = new List<string>();
            foreach (var inner in StructPointerSlots(st.Name))
                optionSlots.Add("offsetof(" + option.CName + ", v) + " + inner);
            EmitLayout(Names.NullableLayout(st.Name), option.CName, optionSlots);
        }
        foreach (var cls in _program.Classes)
            EmitLayout(Names.ClassLayout(cls.Name), Names.Class(cls.Name), ClassPointerSlots(cls.Name));
        Line();
    }

    // Storage types that can contain references (stores need a write barrier)
    private static bool NeedsBarrier(CType type)
    {
        return IsPointerType(type) || type.Kind == CTypeKind.StructVal
            || type.Kind == CTypeKind.Nullable && type.Element!.Kind == CTypeKind.StructVal;
    }

    // Whether we are inside a struct method body (the owner of self is the v_owner argument)
    private bool _structSelfOwner;

    // Root scan of static fields (old-generation mark-sweep) and promotion at boundaries
    private void EmitStaticRoots()
    {
        Line("static void");
        Line("tcs_gc_forward_statics(void)");
        Line("{");
        _indent++;
        foreach (var cls in StaticOwners)
        {
            foreach (var field in cls.Fields)
            {
                if (!field.IsStatic) continue;
                var fact = _facts.Field(cls.Name, field.Name);
                var name = Names.StaticField(cls.Name, field.Name);
                if (IsPointerType(fact.Type))
                    Line(name + " = tcs_gc_promote(" + name + ");");
                else if (fact.Type.Kind == CTypeKind.StructVal
                    || fact.Type.Kind == CTypeKind.Nullable && fact.Type.Element!.Kind == CTypeKind.StructVal)
                    Line("tcs_gc_forward_value(&" + name + ", " + LayoutRef(fact.Type) + ");");
            }
        }
        _indent--;
        Line("}");
        Line();
        Line("static void");
        Line("tcs_gc_mark_statics(void)");
        Line("{");
        _indent++;
        foreach (var cls in StaticOwners)
        {
            foreach (var field in cls.Fields)
            {
                if (!field.IsStatic) continue;
                var fact = _facts.Field(cls.Name, field.Name);
                var name = Names.StaticField(cls.Name, field.Name);
                if (IsPointerType(fact.Type))
                    Line("tcs_gc_mark_ptr(" + name + ");");
                else if (fact.Type.Kind == CTypeKind.StructVal
                    || fact.Type.Kind == CTypeKind.Nullable && fact.Type.Element!.Kind == CTypeKind.StructVal)
                    Line("tcs_gc_mark_value(&" + name + ", " + LayoutRef(fact.Type) + ");");
            }
        }
        _indent--;
        Line("}");
        Line();
    }

    // String literals are not allocated on each evaluation; they share a TCS_GC_STATIC file-scope object
    // (layout-compatible with TcsString: { header; length; data }). bytes is a sequence of bytes 0-255
    private string InternStringLiteral(List<int> bytes)
    {
        var hex = new List<string>();
        foreach (var b in bytes) hex.Add(LiteralHex2(b));
        var key = string.Join("", hex);
        string? existing;
        if (_literals.TryGetValue(key, out existing)) return existing;
        var name = "tcs_lit_" + _literals.Count;
        var escaped = new List<string>();
        foreach (var h in hex) escaped.Add("\\x" + h);
        int length = bytes.Count;
        _literalDecls.Add(
            "static const struct { TcsGcHeader h; size_t length; "
            + "unsigned char data[" + (length + 1) + "]; } " + name + " = "
            + "{ { NULL, NULL, sizeof(size_t) + " + length + ", TCS_KIND_RAW, "
            + "TCS_GC_STATIC, TCS_TYPE_STRING }, " + length + ", \"" + string.Join("", escaped) + "\" };");
        var reference = "((TcsString *)&" + name + ".length)";
        _literals[key] = reference;
        return reference;
    }

    // A byte as 2 lowercase hex digits (C# `{b:x2}`)
    private static string LiteralHex2(int b)
    {
        string digits = "0123456789abcdef";
        var u = new Utf8Bytes(digits);
        return u.Slice(b >> 4 & 15, 1).ToString() + u.Slice(b & 15, 1).ToString();
    }

    // ==== CEmitter.TypeTable.cs ====

    // Put the type descriptors (docs/tcs/object-model.md §4) in the generated C as the same byte sequence as the VM: tcs_type_table.
    // In TCS_VERIFY_TYPES builds, runtime/model reads this table at initialization and checks that the field offsets computed by the C struct rules (§5)
    // match the offsetof values of the generated structs (M-2).
    // The table is also an input to the heap walker (§7) and to reload planning (§6).

    private void EmitTypeTable()
    {
        var b = new TypeTableBuilder();
        foreach (var st in StructInfos)
            StructEntry(b, CType.Struct(st.Name));
        foreach (var cls in _program.Classes)
        {
            if (!cls.IsInterface) ClassEntry(b, cls);
        }

        // String table and type table (same shape as the VM's .tcsb)
        var strings = new TypeTableStrings();
        foreach (var name in TypeTable.Names(b.Entries)) strings.Str(name);
        var data = new List<int>();
        TypeTable.WriteI32(data, strings.Strings.Count);
        foreach (var s in strings.Strings)
        {
            var bytes = new Utf8Bytes(s);
            TypeTable.WriteI32(data, bytes.Length);
            for (int k = 0; k < bytes.Length; k++) data.Add(bytes[k]);
        }
        TypeTable.Write(data, b.Entries, strings);

        Line("/* Type descriptors (docs/tcs/object-model.md §4. Same format as the types in a VM .tcsb) */");
        Line("static const unsigned char tcs_type_table[" + (data.Count > 1 ? data.Count : 1) + "] = {");
        _indent++;
        for (int i = 0; i < data.Count; i += 24)
        {
            var row = new List<string>();
            for (int j = i; j < i + 24 && j < data.Count; j++) row.Add(data[j].ToString());
            Line(string.Join(", ", row) + ",");
        }
        _indent--;
        Line("};");
        Line("#ifdef TCS_VERIFY_TYPES");
        _output.Add(RuntimeModel());
        Line("static void");
        Line("tcs_verify_types(void)");
        Line("{");
        _indent++;
        Line("static const struct { uint16_t type, field; uint32_t offset; } expect[] = {");
        _indent++;
        foreach (var c in b.Checks)
            Line("{ " + c.Type + ", " + c.Field + ", (uint32_t)offsetof(" + c.Owner + ", " + c.Member + ") },");
        Line("{ 0xFFFF, 0, 0 }");
        _indent--;
        Line("};");
        Line("TcsReader r = { tcs_type_table, tcs_type_table + sizeof tcs_type_table, 0 };");
        Line("char **strings = NULL;");
        Line("uint32_t nstrings = 0, ntypes = 0;");
        Line("TcsType *types = NULL;");
        Line("const char *err = tcs_model_read_strings(&r, &strings, &nstrings);");
        Line("if (!err) err = tcs_model_read(&r, (const char *const *)strings, nstrings, &ntypes, &types);");
        // A class payload starts after type_id (uint32_t) (§3, §5)
        Line("if (!err) err = tcs_model_layout(types, ntypes, (uint32_t)sizeof(uint32_t), (uint32_t)sizeof(void *));");
        Line("if (err) { fprintf(stderr, \"type table: %s\\n\", err); abort(); }");
        Line("for (size_t i = 0; expect[i].type != 0xFFFF; i++) {");
        _indent++;
        Line("const TcsField *f = &types[expect[i].type].fields[expect[i].field];");
        Line("if (f->offset != expect[i].offset) {");
        _indent++;
        Line("fprintf(stderr, \"type table: %s.%s at %u, the C struct has %u\\n\", types[expect[i].type].name, f->name, (unsigned)f->offset, (unsigned)expect[i].offset);");
        Line("abort();");
        _indent--;
        Line("}");
        _indent--;
        Line("}");
        Line("tcs_model_free(types, ntypes);");
        Line("tcs_model_free_strings(strings, nstrings);");
        _indent--;
        Line("}");
        Line("#endif");
        Line();
    }

    private TypeTableInfo FieldInfo(TypeTableBuilder b, CType t)
    {
        switch (t.Kind)
        {
            case CTypeKind.I32: return new TypeTableInfo(TypeTable.KindI32, TypeTable.NoType);
            case CTypeKind.F32: return new TypeTableInfo(TypeTable.KindF32, TypeTable.NoType);
            case CTypeKind.Bool: return new TypeTableInfo(TypeTable.KindBool, TypeTable.NoType);
            case CTypeKind.StructVal:
            case CTypeKind.Nullable:
                return new TypeTableInfo(TypeTable.KindStruct, StructEntry(b, t));
        }
        return new TypeTableInfo(TypeTable.KindRef, TypeTable.NoType);
    }

    /// <summary>Descriptor of a value type (user struct, T?). User structs are checked with offsetof.</summary>
    private int StructEntry(TypeTableBuilder b, CType t)
    {
        var key = t.CName;
        int found;
        if (b.Index.TryGetValue(key, out found)) return found;
        var fieldNames = new List<string>();
        var fieldTypes = new List<CType>();
        var fieldMembers = new List<string>();
        ExStruct? info = null;
        if (t.Kind == CTypeKind.Nullable)
        {
            fieldNames.Add("HasValue");
            fieldTypes.Add(CType.Bool);
            fieldMembers.Add("has");
            fieldNames.Add("Value");
            fieldTypes.Add(t.Element!);
            fieldMembers.Add("v");
        }
        else
        {
            ExStruct? st = null;
            foreach (var s in StructInfos)
            {
                if (s.Name == t.Name)
                {
                    st = s;
                    break;
                }
            }
            if (st == null)
            {
                CFail.Report("type table: no fields for " + t.CName);
                return TypeTable.NoType;
            }
            info = st;
            foreach (var f in st.Fields)
            {
                if (f.IsStatic) continue;
                fieldNames.Add(f.Name);
                fieldTypes.Add(_facts.MapType(f.Type));
                fieldMembers.Add(Names.Field(f.Name));
            }
        }
        var entryFields = new List<TypeTableField>();
        for (int k = 0; k < fieldNames.Count; k++)
            entryFields.Add(new TypeTableField(fieldNames[k], FieldInfo(b, fieldTypes[k])));
        int i = b.Entries.Count;
        var entry = new TypeTableEntry(TypeTable.Struct, t.Name != null ? t.Name : t.CName);
        entry.LayoutHash = TypeTable.ParseLayoutHash(info != null ? info.LayoutHash : null);
        entry.Flags = info != null && info.IsRecord ? TypeTable.FlagRecord : 0;
        entry.Fields = entryFields;
        b.Entries.Add(entry);
        b.Index[key] = i;
        for (int k = 0; k < fieldNames.Count; k++) b.Checks.Add(new TypeTableCheck(i, k, t.CName, fieldMembers[k]));
        return i;
    }

    private void ClassEntry(TypeTableBuilder b, ExClass cls)
    {
        // An external data class shared with the host has host_value at the start of its payload (§3; FlagHostValue in the type table)
        bool external = false;
        foreach (var c in ChainRootFirst(cls.Name))
        {
            if (c.IsExternal) external = true;
        }
        var links = new List<ExClass>();
        var fields = new List<ExField>();
        foreach (var link in ChainRootFirst(cls.Name))
        {
            foreach (var f in link.Fields)
            {
                if (f.IsStatic) continue;
                links.Add(link);
                fields.Add(f);
            }
        }
        var entryFields = new List<TypeTableField>();
        for (int k = 0; k < fields.Count; k++)
            entryFields.Add(new TypeTableField(fields[k].Name, FieldInfo(b, _facts.Field(links[k].Name, fields[k].Name).Type)));
        int i = b.Entries.Count;
        bool onReload = false;
        foreach (var m in cls.Methods)
        {
            if (m.Name == "OnReload" && !m.IsStatic) onReload = true;
        }
        var entry = new TypeTableEntry(TypeTable.Class, cls.Name);
        entry.LayoutHash = TypeTable.ParseLayoutHash(cls.LayoutHash);
        entry.Flags = (cls.IsRecord ? TypeTable.FlagRecord : 0) | (onReload ? TypeTable.FlagHasOnReload : 0)
            | (external ? TypeTable.FlagHostValue : 0);
        entry.Fields = entryFields;
        b.Entries.Add(entry);
        b.Index["class:" + cls.Name] = i;
        for (int k = 0; k < fields.Count; k++)
            b.Checks.Add(new TypeTableCheck(i, k, "struct " + Names.Class(cls.Name), Names.Field(fields[k].Name)));
    }
}

// ---- Core ----

public sealed class Variable
{
    public string CName;
    public CType Type;
    public bool Boxed;
    // Copy of a foreach KeyValuePair (a node that is a GC object). Closures capture this pointer directly, without a cell
    public bool Snapshot;

    public Variable(string cName, CType type)
    {
        CName = cName;
        Type = type;
    }
}

public sealed class TypeRange
{
    public int First;
    public int Last;

    public TypeRange(int first, int last)
    {
        First = first;
        Last = last;
    }
}

public sealed class IifeFrame
{
    public string Result;
    public string Label;
    public CType Type;

    public IifeFrame(string result, string label, CType type)
    {
        Result = result;
        Label = label;
        Type = type;
    }
}

public sealed class EntryPoint
{
    public ExClass Class;
    public ExMethod Method;

    public EntryPoint(ExClass cls, ExMethod method)
    {
        Class = cls;
        Method = method;
    }
}

// ---- Expressions ----

// Argument of RenderOrderedCall (the (CType Type, string Value) pair in C#)
public sealed class TypedValue
{
    public CType Type;
    public string Value;

    public TypedValue(CType type, string value)
    {
        Type = type;
        Value = value;
    }
}

// ---- Intrinsics ----

// Result of CEmitter.EnumTable (the (Names, Values, Count, Flags) tuple in the C# version)
public sealed class EnumTableInfo
{
    public string Names;
    public string Values;
    public int Count;
    public bool Flags;
    // A uint enum (a value not in the table is written as an unsigned number)
    public bool Unsigned;

    public EnumTableInfo(string names, string values, int count, bool flags, bool isUnsigned)
    {
        Names = names;
        Values = values;
        Count = count;
        Flags = flags;
        Unsigned = isUnsigned;
    }
}

// ---- Closures ----

// State of CEmitter's closure tree walk (Walk / WalkStat). Replaces the local functions and Actions of the C# version.
// Captured means CapturedByClosuresIn (collects names captured by closures into CapturedSet); otherwise FreeNames (into Free in order of first appearance)
public sealed class ClosureWalk
{
    public bool Captured;
    public HashSet<string> CapturedSet = new HashSet<string>();
    public List<string> Free = new List<string>();

    public ClosureWalk(bool captured)
    {
        Captured = captured;
    }
}

// ---- Foreign ----

// One entry of CEmitter.ForeignOverriders: sends Target's type_id to Impl's implementation
public sealed class ForeignOverrider
{
    public string Target;
    public string Impl;

    public ForeignOverrider(string target, string impl)
    {
        Target = target;
        Impl = impl;
    }
}

// Host function registered by CEmitter.EmitVmHost (name, kind characters of the arguments and return value, C function)
public sealed class VmhRegistration
{
    public string Name;
    public string Params;
    public string Rets;
    public string Fn;

    public VmhRegistration(string name, string ps, string rs, string fn)
    {
        Name = name;
        Params = ps;
        Rets = rs;
        Fn = fn;
    }
}

// Invoke made by CEmitter.VmhInvoker (sig of the function type and C function)
public sealed class VmhInvokerEntry
{
    public string Sig;
    public string Fn;

    public VmhInvokerEntry(string sig, string fn)
    {
        Sig = sig;
        Fn = fn;
    }
}

// Intermediate state of CEmitter.EmitTypeTable
public sealed class TypeTableBuilder
{
    public List<TypeTableEntry> Entries = new List<TypeTableEntry>();
    public Dictionary<string, int> Index = new Dictionary<string, int>();
    /// <summary>Offset checks: (type number, field number, C type name, C field name).</summary>
    public List<TypeTableCheck> Checks = new List<TypeTableCheck>();
}

public sealed class TypeTableCheck
{
    public int Type;
    public int Field;
    public string Owner;
    public string Member;

    public TypeTableCheck(int type, int field, string owner, string member)
    {
        Type = type;
        Field = field;
        Owner = owner;
        Member = member;
    }
}

// ---- TypeTable ----

/// <summary>Type descriptors shared by the VM and tcs2c (docs/tcs/object-model.md §4). A backend maps its types
/// to this shape, and <see cref="TypeTable.Write"/> turns them into a byte sequence (v1). The C runtime/model reads it.</summary>
public static class TypeTable
{
    public const int KindI32 = 0;
    public const int KindF32 = 1;
    public const int KindBool = 2;
    public const int KindRef = 3;
    public const int KindStruct = 4;
    public const int Class = 1;
    public const int Struct = 2;
    public const int Array = 3;
    public const int String = 4;
    public const int List = 5;
    public const int Dict = 6;
    public const int Func = 7;
    public const int Iface = 8;
    public const int Object = 9;
    public const int Box = 10;
    public const int FlagRecord = 1;
    public const int FlagHasOnReload = 2;
    // External data class shared with the host: the payload has a uint64_t host_value aligned to 8 bytes after type_id (docs/tcs/object-model.md §3)
    public const int FlagHostValue = 4;
    public const int NoType = 0xFFFF;

    /// <summary>Converts IlExport's LayoutHash (8 hex digits) to a number (puts the 32-bit pattern into an int).</summary>
    public static int ParseLayoutHash(string? hex)
    {
        if (hex == null || hex == "") return 0;
        var u = new Utf8Bytes(hex);
        int value = 0;
        for (int i = 0; i < u.Length; i++)
        {
            int c = u[i];
            int d;
            if (c >= '0' && c <= '9') d = c - '0';
            else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
            else
            {
                CFail.Report("invalid layout hash: " + hex);
                return 0;
            }
            value = value << 4 | d;
        }
        return value;
    }

    /// <summary>Writes the type table. Names become string table indices via str (the caller writes that table first).</summary>
    public static void Write(List<int> w, List<TypeTableEntry> types, TypeTableStrings str)
    {
        WriteI32(w, types.Count);
        foreach (var t in types)
        {
            WriteU8(w, t.TKind);
            WriteI32(w, str.Str(t.Name));
            WriteI32(w, t.LayoutHash);
            WriteU8(w, t.Flags);
            if (t.TKind == Class || t.TKind == Struct)
            {
                WriteU16(w, t.Fields.Count);
                foreach (var f in t.Fields)
                {
                    WriteI32(w, str.Str(f.Name));
                    WriteU8(w, f.Value.Kind);
                    WriteU16(w, f.Value.Type);
                }
                if (t.TKind == Class)
                {
                    WriteU16(w, t.Interfaces.Count);
                    foreach (var i in t.Interfaces) WriteU16(w, i);
                    WriteU16(w, t.Itable.Count);
                    foreach (var x in t.Itable)
                    {
                        WriteU16(w, x.Selector);
                        WriteU16(w, x.Func);
                    }
                }
            }
            else if (t.TKind == Array || t.TKind == List)
            {
                WriteInfo(w, t.Element);
            }
            else if (t.TKind == Dict)
            {
                WriteInfo(w, t.Key);
                WriteInfo(w, t.Element);
            }
            else if (t.TKind == Func)
            {
                WriteU16(w, t.Params.Count);
                foreach (var p in t.Params) WriteInfo(w, p);
                WriteU16(w, t.Rets.Count);
                foreach (var r in t.Rets) WriteInfo(w, r);
            }
            else if (t.TKind == Box)
            {
                WriteU8(w, t.Element.Kind);
            }
        }
    }

    /// <summary>Collects names first (so the string table is written before the type table).</summary>
    public static List<string> Names(List<TypeTableEntry> types)
    {
        var names = new List<string>();
        foreach (var t in types) names.Add(t.Name);
        foreach (var t in types)
        {
            foreach (var f in t.Fields) names.Add(f.Name);
        }
        return names;
    }

    private static void WriteInfo(List<int> w, TypeTableInfo info)
    {
        WriteU8(w, info.Kind);
        WriteU16(w, info.Type);
    }

    // Append little-endian bytes (ints 0-255) to w, same as BinaryWriter
    public static void WriteU8(List<int> w, int v)
    {
        w.Add(v & 255);
    }

    public static void WriteU16(List<int> w, int v)
    {
        w.Add(v & 255);
        w.Add(v >> 8 & 255);
    }

    public static void WriteI32(List<int> w, int v)
    {
        w.Add(v & 255);
        w.Add(v >> 8 & 255);
        w.Add(v >> 16 & 255);
        w.Add(v >> 24 & 255);
    }
}

/// <summary>Value kind and type number (the referent for REF, the struct for STRUCT). (TypeTable.Info in the C# version)</summary>
public sealed class TypeTableInfo
{
    public int Kind;
    public int Type;

    public TypeTableInfo(int kind, int type)
    {
        Kind = kind;
        Type = type;
    }
}

// TypeTable.Field in the C# version
public sealed class TypeTableField
{
    public string Name;
    public TypeTableInfo Value;

    public TypeTableField(string name, TypeTableInfo value)
    {
        Name = name;
        Value = value;
    }
}

// One entry (Selector, Func) of TypeTable.Itable in the C# version
public sealed class TypeTableItable
{
    public int Selector;
    public int Func;

    public TypeTableItable(int selector, int func)
    {
        Selector = selector;
        Func = func;
    }
}

// TypeTable.Entry in the C# version. init properties are public fields
public sealed class TypeTableEntry
{
    public int TKind;
    public string Name;
    public int LayoutHash;
    public int Flags;
    public List<TypeTableField> Fields = new List<TypeTableField>();
    public List<int> Interfaces = new List<int>();
    public List<TypeTableItable> Itable = new List<TypeTableItable>();
    public TypeTableInfo Element = new TypeTableInfo(0, 0);
    public TypeTableInfo Key = new TypeTableInfo(0, 0);
    public List<TypeTableInfo> Params = new List<TypeTableInfo>();
    public List<TypeTableInfo> Rets = new List<TypeTableInfo>();

    public TypeTableEntry(int tkind, string name)
    {
        TKind = tkind;
        Name = name;
    }
}

// String table (name → index). The str of TypeTable.Write (Func<string, int> in the C# version)
public sealed class TypeTableStrings
{
    public List<string> Strings = new List<string>();
    public Dictionary<string, int> Index = new Dictionary<string, int>();

    public int Str(string s)
    {
        int i;
        if (Index.TryGetValue(s, out i)) return i;
        Index[s] = Strings.Count;
        Strings.Add(s);
        return Strings.Count - 1;
    }
}
