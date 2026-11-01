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

    // 直前の compile で parse したファイルの数 (増分の効き目を測る)
    public int ParsedFiles;
    // 何回 compile を終えたか
    public int Revision;
    // 単一の実装へ移す途中: 参照実装を写した経路 (LuaEmit + IlBuild + LuaGen) で出力する。出力は prelude を含む chunk
    public bool UseLuaEmit = true;

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
                _phase = _c!.Diags.ErrorCount == 0 ? PhaseDeclare : PhaseEmit;
                return false;
            case PhaseDeclare:
                _declarer = new Declarer(_c!);
                _declarer.DeclareAll();
                _phase = PhaseBind;
                return false;
            case PhaseBind:
                new Binder(_c!, _declarer!).BindAll();
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
            if (UseLuaEmit)
            {
                var le = new LuaEmit(c);
                result.Lua = le.EmitProgram(_entry);
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
        foreach (var diag in c.Diags.Items) result.Diagnostics.Add(diag.ToString());
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
