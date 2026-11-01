using System.Collections.Generic;

namespace TcsSelf;

// session の 1 つのファイル。parse したままの木を持ち、内容が変わるまで parse し直さない
public sealed class SessionFile
{
    public string Path;
    public string Text;
    // 0 = user のソース、1 = --ref の宣言、2 = tcs と一緒に配る library
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

// 増分の compile の session。ファイルを足し、差し替え、外してから compile する。
// parse は内容が変わったファイルだけをやり直し、宣言、束縛、出力は parse した木の複製で毎回やり直す (束縛は木を書き換える)。
// compile は Step に分かれる: 変わったファイルの parse を 1 つずつ、準拠検査と木の組み立て、宣言、束縛、出力。
// host は Step を繰り返し呼び、Step の間で他の仕事 (新しい編集の受け取り、tcs2c の GC の frame の境界) をはさめる
public sealed class Session
{
    private readonly List<SessionFile> _files = new List<SessionFile>();
    private string _entry = "";

    // compile の途中の状態
    private int _phase;
    private Compilation? _c;
    private Declarer? _declarer;
    private CompileResult _result = new CompileResult();
    // 宣言の前の診断の数
    private int _bindMark;
    // 出力の段でだけ報告する準拠検査の診断
    private List<Diagnostic> _deferred = new List<Diagnostic>();

    // 直前の compile で parse したファイルの数 (増分の効き目を測る)
    public int ParsedFiles;
    // 何回 compile を終えたか
    public int Revision;
    // 単一の実装へ移す途中: 参照実装を写した経路 (LuaEmit + IlBuild + LuaGen) で出力する。出力は prelude を含む chunk
    public bool UseLuaEmit = true;
    // hot reload 用の weak な instance の表 (`__tcs_instances`) を出す
    public bool InstanceRegistry;
    // --trace: path がこの prefix に segment 単位で一致するファイルの文に実行の記録を出す
    public List<string> TracePrefixes = new List<string>();
    // 型を chunk の local に置く (false なら global を引く)
    public bool CacheTypeLocals = true;
    // --module: 定義した型の table を返す
    public bool Module;
    // Lua の代わりに tcs2c への入力 (型の metadata と本文の IL) を作る
    public bool ExportOnly;

    private const int PhaseIdle = 0;
    private const int PhaseParse = 1;
    private const int PhaseDeclare = 3;
    private const int PhaseBind = 4;
    private const int PhaseEmit = 5;

    public Session(string entry)
    {
        _entry = entry;
    }

    public CompileResult Result => _result;

    public bool Busy => _phase != PhaseIdle;

    // user のソースを足すか差し替える (内容が同じなら parse し直さない)
    public void SetSource(string path, string text) => Set(path, text, 0);

    public void SetReference(string path, string text) => Set(path, text, 1);

    public void SetLibrary(string path, string text) => Set(path, text, 2);

    // 同じ path があっても別のファイルとして足す (path を持たない入力を並べる host 用)
    public void Add(string path, string text, int role)
    {
        _files.Add(new SessionFile(path, text, role));
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

    // compile を始める (前の compile の途中なら捨ててやり直す)
    public void Start()
    {
        _phase = PhaseParse;
        _c = null;
        _declarer = null;
        ParsedFiles = 0;
    }

    // compile を 1 段進める。終わったら true を返し、Result に結果を置く
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
                // parse し直すファイルが無くなったら、同じ段で組み立てる
                Assemble();
                // 構文の誤り (CSxxxx) があれば束縛しない。サブセットの外の構文 (TCS1001) は木を作り直して読んだので、型の検査も続ける
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
                // サブセットの外の構文や API があると、宣言と束縛の C# の誤りはその巻き添えのことがある
                // (読み飛ばした local function の呼び出し、展開しない params の呼び出しなど)。サブセットの診断を先に報告する
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
                Finish();
                _phase = PhaseIdle;
                Revision++;
                return true;
        }
        return true;
    }

    // Start から終わりまで Step を回す
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

    // user のソース、--ref、使われる library の順に、parse した木の複製を compilation に並べる
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
                if (role == 0) CollectNamespaces(f.Unit, "", userNamespaces, userUsings);
            }
        }
        // library は user が namespace を using するときだけ読み、user が同じ namespace を宣言するなら読まない (参照実装と同じ)
        foreach (var f in _files)
        {
            if (f.Role != 2) continue;
            // System.ValueTuple の宣言はタプルの構文があるときだけ読む
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
        // 構文だけで決まる準拠検査 (TCS1001)。--ref の宣言と library は対象外
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

    // 読み飛ばした構文や、写していない API の巻き添えで出うる C# の誤り (名前の解決、overload、変換、到達、実装の不足)
    private static bool IsCascadeError(string code)
    {
        switch (code)
        {
            case "CS0019":
            case "CS0022":
            case "CS0029":
            case "CS0103":
            case "CS0117":
            case "CS0123":
            case "CS0150":
            case "CS0161":
            case "CS0165":
            case "CS0234":
            case "CS0246":
            case "CS0266":
            case "CS0428":
            case "CS0535":
            case "CS1061":
            case "CS1501":
            case "CS1503":
            case "CS1660":
                return true;
        }
        return false;
    }

    private static bool HasSubsetError(Diagnostics diags)
    {
        foreach (var d in diags.Items)
        {
            if (d.IsError && (d.Code == "TCS1001" || d.Code == "TCS1002" || d.Code == "TCS1900")) return true;
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

    // タプルの型 / 式の構文 (var (a, b) の宣言の形は含まない)
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

    private static void Merge(Diagnostics into, Diagnostics from)
    {
        foreach (var d in from.Items) into.Items.Add(d);
        into.ErrorCount += from.ErrorCount;
    }

    private void Finish()
    {
        var c = _c!;
        var result = new CompileResult();
        if (c.Diags.ErrorCount == 0)
        {
            if (ExportOnly)
            {
                result.Export = new IlExporter(c).Export();
            }
            else if (UseLuaEmit)
            {
                var le = new LuaEmit(c);
                le.InstanceRegistry = InstanceRegistry;
                le.TracePrefixes = TracePrefixes;
                le.CacheTypeLocals = CacheTypeLocals;
                result.Lua = le.EmitProgram(_entry);
                if (Module && le.Errors.Count == 0) result.Lua = result.Lua + le.ModuleReturn();
                result.SourceMap = le.G.SourceMap;
                result.EmittedTypes = le.EmittedTypes;
                foreach (var err in le.Errors) result.Diagnostics.Add(err);
                foreach (var ie in le.G.InternalErrors) result.Diagnostics.Add("internal: " + ie);
                if (le.Errors.Count > 0 || le.G.InternalErrors.Count > 0) c.Diags.ErrorCount++;
            }
            else
            {
                var e = new Emitter(c);
                result.Lua = e.EmitProgram(_entry);
            }
        }
        foreach (var diag in c.Diags.Ordered()) result.Diagnostics.Add(diag.ToString());
        result.ErrorCount = c.Diags.ErrorCount;
        result.Ok = c.Diags.ErrorCount == 0;
        _result = result;
        _c = null;
        _declarer = null;
    }

    // 宣言する namespace と using する namespace
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
