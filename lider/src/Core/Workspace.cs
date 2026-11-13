// lider の状態: 対象の file の一覧、開いた Document、円筒に並べた窓とどれに focus があるか、入力を取っているか。
// 編集の key と文字は、focus のある窓の エディタの view に渡す (エディタの keymap が編集、選択、undo、検索をする)。
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

// 飛ぶ前にいた場所
public class Place
{
    public string Path = "";
    public int Line;
    public int Col;
}

// 円筒に並べた、file を 1 つ開いた窓
public class CodeWindow
{
    // タブ (左から) と、出しているタブ
    public readonly List<Document> Tabs = new List<Document>();
    public int Tab;
    public Document Doc => Tabs[Tab];
    // 出しているタブの エディタの view
    public int ViewId => Doc.Id;
    // 円筒の角度 (Workspace.Arrange が並びから決める。null はまだ決めていない窓)
    public float? Angle;
    // 中心の高さ (メートル。つかんでいる間だけ 0 でない) と、大きさの倍率
    public float Y;
    public float Scale = 1;
    // 非 XR で読める大きさにしたか
    public bool Sized;
    // 利用者が決めた大きさ (px) と、今の大きさ (Zen の間だけ違う)
    public int WidthPx = PanelView.BasePxW;
    public int HeightPx = PanelView.BasePxH;
    public int PxW = PanelView.BasePxW;
    public int PxH = PanelView.BasePxH;
    public float W => PanelView.BaseW * PxW / PanelView.BasePxW;
    public float H => PanelView.BaseW * PxH / PanelView.BasePxW;

    public const int MinPxW = 320;
    public const int MinPxH = 160;
    public const int MaxPx = 2048;

    // 利用者の大きさを変える (px)。文字の桁に揃えなくても エディタが桁を決める
    public void Resize(int pxW, int pxH)
    {
        WidthPx = Math.Max(MinPxW, Math.Min(MaxPx, pxW));
        HeightPx = Math.Max(MinPxH, Math.Min(MaxPx, pxH));
    }

    public CodeWindow(Document doc)
    {
        Tabs.Add(doc);
    }
}

public class Workspace
{
    public readonly string Root;
    // Root の下の .cs (path の ordinal 順)
    public List<string> Files = new List<string>();
    // 画像と音の file の一覧
    public List<string> AssetFiles = new List<string>();
    // 外で書き換わった file を、編集していない窓に写す (web で保存の後に整形した版)
    public bool FollowDisk = Environment.GetEnvironmentVariable("LIDER_FOLLOW_DISK") == "1";
    // 保存していない本文の下書き
    public Drafts? Drafts = Environment.GetEnvironmentVariable("LIDER_DRAFTS") != null ? new Drafts(Environment.GetEnvironmentVariable("LIDER_DRAFTS")!) : null;
    // 板に出している画像か音の file ("" で出さない)
    public string ShownAsset = "";
    // 足した file と消した file を拾うため、Tick はこの秒数ごとに探し直す
    public const float RescanSeconds = 2;
    float sinceScan;
    public readonly Dictionary<string, Document> Docs = new Dictionary<string, Document>();
    // 横一列の並び (左から)
    public readonly List<CodeWindow> Windows = new List<CodeWindow>();
    // 入力が入る窓
    public CodeWindow? FocusWindow;
    public Document? Focus => FocusWindow?.Doc;
    // 見ている窓に focus を移すのは、LookSeconds 見続け、その間 QuietSeconds 打っていないとき
    // (打ちながら脇を見ても移らない)
    public const float LookSeconds = 0.35f;
    public const float QuietSeconds = 0.5f;
    CodeWindow? looked;
    float lookedFor;
    float sinceKey = QuietSeconds;
    public readonly TraceLog Trace = new TraceLog();
    // lider がキーボードとマウスを取っているか (取っている間、ゲームには入力が届かない)
    public bool Active;
    public bool Shift;
    public bool Ctrl;
    public bool Alt;
    // 押している key (lub の KeyDown は押しっぱなしの繰り返しも届くので、押し始めと分ける)
    readonly Dictionary<int, bool> held = new Dictionary<int, bool>();

    public Workspace(string root)
    {
        Root = root;
        Rescan();
    }

    public void Rescan()
    {
        var files = new List<string>();
        AssetFiles = new List<string>();
        Walk(Root, files, 0);
        if (!AssetFiles.Contains(ShownAsset))
            ShownAsset = "";
        // 挿入ソート (path の ordinal 順)
        for (var i = 1; i < files.Count; i++)
        {
            var f = files[i];
            var j = i - 1;
            while (j >= 0 && Before(f, files[j]))
            {
                files[j + 1] = files[j];
                j--;
            }
            files[j + 1] = f;
        }
        Files = files;
    }

    // lider を出している間、毎 frame 呼ぶ
    public void Tick(float dt)
    {
        TakeDeleted();
        if (FollowDisk)
            foreach (var pair in Docs)
                EditorHost.FollowDisk(pair.Value.Id);
        Drafts?.Tick(this, dt);
        sinceKey += dt;
        sinceScan += dt;
        if (sinceScan < RescanSeconds)
            return;
        sinceScan = 0;
        Rescan();
    }

    // a が b より ordinal (byte) の順で前か
    static bool Before(string a, string b)
    {
        var n = Math.Min(Utf8.Bytes(a), Utf8.Bytes(b));
        for (var i = 0; i < n; i++)
            if (Utf8.At(a, i) != Utf8.At(b, i))
                return Utf8.At(a, i) < Utf8.At(b, i);
        return Utf8.Bytes(a) < Utf8.Bytes(b);
    }

    // dotnet の既定と同じく bin / obj と '.' で始まるディレクトリは辿らない
    void Walk(string dir, List<string> files, int depth)
    {
        if (depth > 8)
            return;
        foreach (var e in Disk.ListDir(dir))
        {
            if (e.Name.StartsWith("."))
                continue;
            var path = dir == "." ? e.Name : dir + "/" + e.Name;
            if (e.IsDir)
            {
                if (e.Name != "bin" && e.Name != "obj")
                    Walk(path, files, depth + 1);
            }
            else if (e.Name.EndsWith(".cs"))
            {
                files.Add(path);
            }
            else if (AssetView.IsAsset(e.Name))
            {
                AssetFiles.Add(path);
            }
        }
    }

    public Document? Open(string path)
    {
        if (Docs.TryGetValue(path, out var open))
            return open;
        var doc = Document.Open(path);
        if (doc == null)
            return null;
        Docs[path] = doc;
        return doc;
    }

    // Root の直下の file の path (Walk と同じ書き方)
    public string PathOf(string name)
    {
        return Root == "." ? name : Root + "/" + name;
    }

    // type の宣言だけの file を作って窓に開く
    public void Create(string path, string typeName)
    {
        Io.SaveText(path, "public class " + typeName + "\n{\n}\n");
        Rescan();
        FocusOn(path);
    }

    // 板に出している file か、focus のある窓の file を消すか尋ねる
    public void AskDeleteFocus()
    {
        if (ShownAsset != "")
            EditorHost.AskDeleteFile(ShownAsset);
        else if (FocusWindow != null)
            EditorHost.AskDelete(FocusWindow.ViewId);
    }

    // 消すと答えた file の窓と Document を片付ける
    void TakeDeleted()
    {
        for (var id = EditorHost.TakeDeleted(); id != 0; id = EditorHost.TakeDeleted())
        {
            Rescan();
            string? key = null;
            foreach (var pair in Docs)
                if (pair.Value.Id == id)
                    key = pair.Key;
            if (key == null)
                continue;
            var window = WindowOf(Docs[key]);
            if (window != null)
                CloseTab(window, window.Tabs.IndexOf(Docs[key]));
            Docs.Remove(key);
            EditorHost.Close(id);
            Drafts?.Forget(this, key);
            Rescan();
        }
    }

    // 開いた Document に エディタの本文の変化と保存を拾わせる。
    // 名前の無い doc に名前を付けて保存したら、その file の path にする
    public void Sync()
    {
        var named = new List<string>();
        foreach (var pair in Docs)
        {
            pair.Value.Sync();
            if (pair.Value.Path == "" && EditorHost.Path(pair.Value.Id) != "")
                named.Add(pair.Key);
        }
        // 新しい file は一覧にまだ無いので、探し直してから一覧の path にする
        if (named.Count > 0)
            Rescan();
        foreach (var key in named)
        {
            var doc = Docs[key];
            var full = EditorHost.Path(doc.Id);
            doc.Path = Resolve(full) ?? full;
            Docs.Remove(key);
            Docs[doc.Path] = doc;
        }
    }

    // lub が reload chunk を当てた (OnReload)
    public void Reloaded()
    {
        foreach (var doc in Docs.Values)
            doc.Reloaded();
        if (Errors.Count > 0)
        {
            Errors = new List<BuildError>();
            ErrorsVersion++;
            Fixed++;
        }
        if (InitChanged.Count > 0)
        {
            InitChanged = new List<string>();
            ErrorsVersion++;
        }
    }

    // 当てた差分のうち、動いている個体には入らなかった field の初期値の変更 ("Type.field")。次の reload まで
    public List<string> InitChanged = new List<string>();

    // lub が差分を当てた後の知らせ (OnReloadNotes。Reloaded の後に呼ばれる)
    public void ReloadNotes(string text)
    {
        const string initChanged = "instance field initializer changed: ";
        foreach (var raw in text.Split('\n'))
            if (raw.StartsWith(initChanged))
                InitChanged.Add(Utf8.Sub(raw, Utf8.Bytes(initChanged)).Trim());
        ErrorsVersion++;
    }

    // 最後に保存した版の compile error (直した版の reload が当たるまで)。
    // Errors と InitChanged が変わるたびに ErrorsVersion が上がる
    public List<BuildError> Errors = new List<BuildError>();
    public int ErrorsVersion;
    // compile error を受けた回数と、error が直って reload が当たった回数
    public int Failures;
    public int Fixed;
    // 検索や履歴で別の場所へ飛んだ回数
    public int Jumps;

    // lub が tcs の compile error を知らせた (OnBuildFailed)。error の path は一覧の path に直す
    public void BuildFailed(string text)
    {
        Errors = BuildError.Parse(text);
        foreach (var e in Errors)
            if (e.Path != "")
                e.Path = Resolve(e.Path) ?? e.Path;
        ErrorsVersion++;
        Failures++;
    }

    // カーソルのある行の error の、並びの次の error (最後の次は最初。カーソルの行に無ければ最初)。一覧に無い file の error は飛ばす
    public BuildError? NextError()
    {
        var places = new List<BuildError>();
        foreach (var e in Errors)
            if (e.Line >= 0 && Files.Contains(e.Path))
                places.Add(e);
        if (places.Count == 0)
            return null;
        var here = Here();
        for (var i = 0; i < places.Count; i++)
            if (here != null && places[i].Path == here.Path && places[i].Line == here.Line)
                return places[(i + 1) % places.Count];
        return places[0];
    }

    // path の file の error
    public List<BuildError> ErrorsIn(string path)
    {
        var found = new List<BuildError>();
        foreach (var e in Errors)
            if (e.Path == path)
                found.Add(e);
        return found;
    }

    // path のタブを出して focus を移す (タブが無ければ開いて、focus のある窓のタブに足す)
    public void FocusOn(string path)
    {
        var doc = Open(path);
        if (doc != null)
            Show(doc);
    }

    // doc のタブを出して focus を移す (タブが無ければ focus のある窓の今のタブの右に足す。窓が無ければ窓を作る)
    public void Show(Document doc)
    {
        var window = WindowOf(doc);
        if (window != null)
        {
            window.Tab = window.Tabs.IndexOf(doc);
            FocusWindow = window;
            return;
        }
        if (FocusWindow == null)
        {
            AddWindow(new CodeWindow(doc), Windows.Count);
            return;
        }
        FocusWindow.Tabs.Insert(FocusWindow.Tab + 1, doc);
        FocusWindow.Tab++;
    }

    // 窓を並びの at 番目に足し、focus を移す
    void AddWindow(CodeWindow window, int at)
    {
        Windows.Insert(at, window);
        FocusWindow = window;
    }

    // focus のある窓で、by だけ隣のタブを出す (端から反対の端へ回る)
    public void NextTab(int by)
    {
        var window = FocusWindow;
        if (window != null)
            window.Tab = (window.Tab + by + window.Tabs.Count) % window.Tabs.Count;
    }

    // focus のある窓の今のタブを、by (1 か -1) の側の隣の窓へ移す (端なら新しい窓を作る)。
    // 空になった窓は閉じる
    public void MoveTab(int by)
    {
        var from = FocusWindow;
        if (from == null || (from.Tabs.Count == 1 && (by < 0 ? Windows.IndexOf(from) == 0 : Windows.IndexOf(from) == Windows.Count - 1)))
            return;
        var doc = from.Doc;
        var at = Windows.IndexOf(from) + by;
        RemoveTab(from, from.Tab);
        if (at < 0 || at >= Windows.Count + (from.Tabs.Count == 0 ? 1 : 0))
        {
            AddWindow(new CodeWindow(doc), at < 0 ? 0 : Windows.Count);
            if (from.Tabs.Count == 0)
                Windows.Remove(from);
            return;
        }
        var to = Windows[at];
        if (from.Tabs.Count == 0)
            Windows.Remove(from);
        to.Tabs.Insert(to.Tab + 1, doc);
        to.Tab++;
        FocusWindow = to;
    }

    // focus のある窓の今のタブを閉じる
    public void CloseTab()
    {
        if (FocusWindow != null)
            CloseTab(FocusWindow, FocusWindow.Tab);
    }

    // 窓の i 番目のタブを閉じる (view は Doc が持ち続けるので、未保存の本文は残る)。最後のタブなら窓も閉じる
    public void CloseTab(CodeWindow window, int i)
    {
        if (i < 0 || i >= window.Tabs.Count)
            return;
        if (window.Tabs.Count == 1)
        {
            Close(window);
            return;
        }
        RemoveTab(window, i);
    }

    // タブを外す (今のタブなら、右隣 (無ければ左隣) を出す)
    static void RemoveTab(CodeWindow window, int i)
    {
        window.Tabs.RemoveAt(i);
        if (window.Tab > i || window.Tab == window.Tabs.Count)
            window.Tab = Math.Max(0, window.Tab - 1);
    }

    // 飛ぶ前にいた場所 (新しい物が後ろ)
    public readonly List<Place> History = new List<Place>();
    const int MaxHistory = 100;

    // focus のある窓のカーソルの場所
    public Place? Here()
    {
        var doc = Focus;
        return doc == null || doc.Path == "" ? null : new Place { Path = doc.Path, Line = doc.Line, Col = doc.Col };
    }

    // 飛ぶ前の場所を覚える
    public void Remember(Place? place)
    {
        if (place == null)
            return;
        if (History.Count == MaxHistory)
            History.RemoveAt(0);
        History.Add(place);
    }

    // 覚えた場所へ戻る (窓を閉じていれば開き直す)
    public void Back()
    {
        if (History.Count == 0)
            return;
        var place = History[History.Count - 1];
        History.RemoveAt(History.Count - 1);
        FocusOn(place.Path);
        if (Focus == null)
            return;
        Focus.MoveTo(place.Line, place.Col);
        Jumps++;
        EditorHost.ScrollToLine(Focus.Id, Math.Max(1, place.Line + 1 - ContextLines));
    }

    // 飛んだ先の行の上に見せる行の数
    public const int ContextLines = 5;

    // 窓どうしの間 (メートル)
    public const float WindowGap = 0.06f;

    // 窓を横一列に並べる (最初の窓の中心が角度 0)。
    // つかんでいる窓 (held) はそのまま指す点に付いていき、隣の窓の中心を越えると並びが入れ替わる
    public void Arrange(CodeWindow? held)
    {
        if (held != null && held.Angle != null && Windows.Contains(held))
        {
            var at = Windows.IndexOf(held);
            if (at + 1 < Windows.Count && Windows[at + 1].Angle != null && held.Angle.Value > Windows[at + 1].Angle!.Value)
                Swap(at, at + 1);
            else if (at > 0 && Windows[at - 1].Angle != null && held.Angle.Value < Windows[at - 1].Angle!.Value)
                Swap(at, at - 1);
        }
        var right = 0.0f;
        for (var i = 0; i < Windows.Count; i++)
        {
            var window = Windows[i];
            var half = window.W * window.Scale * 0.5f;
            var center = i == 0 ? 0 : right + half;
            right = center + half + WindowGap;
            if (window == held)
                continue;
            window.Angle = center / Space.R;
            window.Y = 0;
        }
    }

    void Swap(int a, int b)
    {
        var w = Windows[a];
        Windows[a] = Windows[b];
        Windows[b] = w;
    }

    // focus を by だけ隣の窓へ移す (端では止まる)
    public void FocusBy(int by)
    {
        if (FocusWindow == null)
            return;
        var at = Windows.IndexOf(FocusWindow) + by;
        if (at >= 0 && at < Windows.Count)
            FocusWindow = Windows[at];
    }

    // focus のある窓を、並びの中で by だけ動かす (端では止まる)
    public void MoveFocus(int by)
    {
        if (FocusWindow == null)
            return;
        var at = Windows.IndexOf(FocusWindow);
        if (at + by >= 0 && at + by < Windows.Count)
            Swap(at, at + by);
    }

    // 左から n 番目 (0 から) の窓に focus を移す
    public void FocusNth(int n)
    {
        if (n >= 0 && n < Windows.Count)
            FocusWindow = Windows[n];
    }

    // 窓をタブごと閉じる (view は Doc が持ち続けるので、未保存の本文は残る)。
    // focus があった窓なら、右隣 (無ければ左隣) に focus を移す
    public void Close(CodeWindow window)
    {
        var at = Windows.IndexOf(window);
        if (at < 0)
            return;
        Windows.Remove(window);
        if (window == FocusWindow)
            FocusWindow = Windows.Count > 0 ? Windows[Math.Min(at, Windows.Count - 1)] : null;
    }

    // 窓の見出しに出す今のタブの名前 (Root からの path と、未保存の印)
    public string Title(CodeWindow window)
    {
        return TabTitle(window.Doc);
    }

    // 窓の見出しに並べるタブの名前
    public List<string> TabTitles(CodeWindow window)
    {
        var titles = new List<string>();
        foreach (var doc in window.Tabs)
            titles.Add(TabTitle(doc));
        return titles;
    }

    string TabTitle(Document doc)
    {
        var path = doc.Path == "" ? "untitled" : Relative(doc.Path);
        return EditorHost.Dirty(doc.Id) ? path + " *" : path;
    }

    // Root からの path
    public string Relative(string path)
    {
        return path.StartsWith(Root + "/") ? Utf8.Sub(path, Utf8.Bytes(Root) + 1) : path;
    }

    // doc のタブを持つ窓
    public CodeWindow? WindowOf(Document doc)
    {
        foreach (var w in Windows)
            if (w.Tabs.Contains(doc))
                return w;
        return null;
    }

    // 毎 frame、今見ている窓 (視線か指している所。無ければ null) を渡す
    public void Look(CodeWindow? window, float dt)
    {
        if (window != looked)
        {
            looked = window;
            lookedFor = 0;
            return;
        }
        lookedFor += dt;
        if (window != null && window != FocusWindow && lookedFor >= LookSeconds && sinceKey >= QuietSeconds)
            FocusWindow = window;
    }

    // 一覧にある file で、path と同じもの (選び方は TraceLog.Match)
    public string? Resolve(string path)
    {
        return TraceLog.Match(path, Files);
    }

    // key の押し離しを覚え、押したままの間に届いた KeyDown (繰り返し) なら true
    public bool Repeated(int key, bool down)
    {
        var was = held.TryGetValue(key, out var h) && h;
        held[key] = down;
        return down && was;
    }

    // 押されている修飾 key (EventData.Mods) を受け、変わった分を エディタに渡す。
    // 入力を取っていない間も渡す (押したまま隠して離すと、押したままになる)
    public void SetMods(int mods)
    {
        Shift = SetMod(Shift, (mods & Keys.ModShift) != 0, Keys.LeftShift);
        Ctrl = SetMod(Ctrl, (mods & Keys.ModCtrl) != 0, Keys.LeftCtrl);
        Alt = SetMod(Alt, (mods & Keys.ModAlt) != 0, Keys.LeftAlt);
    }

    static bool SetMod(bool was, bool now, int key)
    {
        if (was != now && Document.Booted)
            EditorHost.Key(key, now);
        return now;
    }

    // US 配列の key として打つ
    public bool Key(int key, bool down)
    {
        return Input(key, down, Keys.UsChar(key, Shift), Keys.UsChar(key, false));
    }

    // key を focus のある窓の エディタの view に渡し、印字できる key は文字も渡す (Ctrl を押している間は渡さない)。
    // typed と plain は EventData の同じ名前の値。
    // Escape は focus のある窓の今のタブを閉じる (変換中と、エディタの CommandView と NagView が出ている間は エディタに渡す)。
    // 扱った key なら true
    public bool Input(int key, bool down, int typed, int plain)
    {
        // 修飾 key の状態は SetMods で受ける (どの物理の key が修飾 key かは lub が知っている)
        if (key == Keys.LeftShift || key == Keys.RightShift || key == Keys.LeftCtrl || key == Keys.RightCtrl
            || key == Keys.LeftAlt || key == Keys.RightAlt)
            return true;
        if (!Active)
            return false;
        if (down && key == Keys.Escape && ShownAsset != "" && !(Document.Booted && EditorHost.Modal()))
        {
            ShownAsset = "";
            return true;
        }
        if (down && key == Keys.Escape && !(Document.Booted && EditorHost.Modal()))
        {
            CloseTab();
            return true;
        }
        var doc = Focus;
        if (doc == null)
            return false;
        if (down)
            sinceKey = 0;
        EditorHost.Focus(FocusWindow!.ViewId);
        EditorHost.KeyNamed(key, down, plain);
        if (down && !Ctrl && typed != 0)
            EditorHost.Text(Utf8.Encode(typed));
        doc.Sync();
        TakeOpened();
        return true;
    }

    // エディタの command が開いた doc をタブにして focus を移す。
    // タブのある file なら、開いた view を閉じてそのタブを出す
    void TakeOpened()
    {
        for (var id = EditorHost.TakeOpened(); id != 0; id = EditorHost.TakeOpened())
        {
            var full = EditorHost.Path(id);
            var path = full == "" ? "" : (Resolve(full) ?? full);
            if (path != "" && Docs.ContainsKey(path))
            {
                EditorHost.Close(id);
                FocusOn(path);
                continue;
            }
            var doc = Document.Adopt(path, id);
            Docs[path != "" ? path : "untitled:" + id] = doc;
            Show(doc);
        }
    }
}
