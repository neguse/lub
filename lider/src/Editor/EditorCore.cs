// エディタの core の状態 (Lite XL の core/init.lua の、lider が使う所)。
// 窓ごとの DocView、どの view に入力が入るか (active)、CommandView と NagView、開いた doc、clipboard を持つ。
// active の view は番号で持つ: 1 からは窓の view、CommandId は CommandView、NagId は NagView、0 は無し。
using System;
using System.Collections.Generic;

namespace LiderEditor;

public static class EditorCore
{
    public const int CommandId = -1;
    public const int NagId = -2;
    const int NoView = -1000;

    public static bool Booted;
    public static readonly Dictionary<int, DocView> Views = new Dictionary<int, DocView>();
    static int nextView;
    public static readonly List<Doc> Docs = new List<Doc>();
    public static int Active;
    public static int LastActive;
    static int nextActive = NoView;
    public static CommandView Command = new CommandView();
    public static NagView Nag = new NagView();
    // command (Ctrl+N、Ctrl+O、Ctrl+P) が開いた doc の view (lider がまだ窓にしていないもの)
    public static readonly List<int> Opened = new List<int>();
    // 消すと答えた file の view
    public static readonly List<int> Deleted = new List<int>();
    // どこかに描き直す所ができたか
    public static bool Redraw;
    public static string LastError = "";
    // command view などが新しく開く物の大きさ (最後に描いた大きさ)
    public static float RootW = 800;
    public static float RootH = 600;
    // 押している修飾 key
    public static bool Ctrl;
    public static bool Shift;
    public static bool Alt;
    public static bool AltGr;
    // OS か jamo の変換中の文字が本文にあるか
    public static bool ImeEditing;
    // 前の key が command になったか (その key が打った文字を捨てる)
    public static bool DidKeymap;
    // 切り取りと貼り付け (選択ごとの物と、全体の文字)
    public static string Clipboard = "";
    public static List<string> CursorClipboard = new List<string>();
    public static List<bool> CursorClipboardWholeLine = new List<bool>();
    public static string CursorClipboardFull = "";
    public static readonly List<string> PreviousFind = new List<string>();
    public static readonly List<string> PreviousReplace = new List<string>();
    public static readonly List<string> VisitedFiles = new List<string>();

    public static void Boot()
    {
        if (Booted)
            return;
        Booted = true;
        Paths.Init();
        Keymap.Init();
    }

    public static void Error(string message)
    {
        LastError = message;
    }

    public static bool IsActive(DocView v)
    {
        if (v.Bar)
            return Active == CommandId;
        return Active == v.Id && Active > 0;
    }

    // 入力が入る DocView (CommandView の入力欄も含む)
    public static DocView? ActiveDocView()
    {
        if (Active == CommandId)
            return Command.View;
        if (Active > 0 && Views.TryGetValue(Active, out var v))
            return v;
        return null;
    }

    // 入力が入る窓の DocView (CommandView を含まない)
    public static DocView? ActiveWindowView()
    {
        if (Active > 0 && Views.TryGetValue(Active, out var v))
            return v;
        return null;
    }

    public static DocView? ViewOf(int id)
    {
        if (id > 0 && Views.TryGetValue(id, out var v))
            return v;
        return null;
    }

    public static void SetActive(int view)
    {
        // 変換中の文字は今の view に確定する
        if (Ime.Composing)
            Ime.Commit();
        else if (ImeEditing)
            Ime.OnTextEditing("", 0, 0);
        if (view == Active)
            return;
        if (Active == NagId && Nag.ForceFocus)
        {
            nextActive = view;
            return;
        }
        nextActive = NoView;
        var v = ViewOf(view);
        if (v != null && v.Doc.Filename != null)
            SetVisited(v.Doc.Filename);
        LastActive = Active;
        Active = view;
    }

    // NagView が閉じたときに戻る view
    public static void RestoreActive()
    {
        SetActive(nextActive != NoView ? nextActive : LastActive);
    }

    static void SetVisited(string filename)
    {
        VisitedFiles.Remove(filename);
        VisitedFiles.Insert(0, filename);
    }

    // file の doc (開いていればそれ、無ければ読む。名前が null なら名前の無い新しい doc)
    public static Doc OpenDoc(string? filename)
    {
        var newFile = filename == null || !Paths.Exists(filename);
        string? abs = null;
        if (filename != null)
        {
            filename = Paths.ToProject(filename);
            abs = Paths.Absolute(filename);
            foreach (var d in Docs)
                if (d.AbsFilename != null && d.AbsFilename == abs)
                    return d;
        }
        var doc = Doc.Open(filename, abs, newFile);
        Docs.Add(doc);
        return doc;
    }

    public static int AddView(Doc doc)
    {
        var view = new DocView(doc, false);
        view.SizeX = RootW;
        view.SizeY = RootH;
        nextView++;
        view.Id = nextView;
        Views[nextView] = view;
        return nextView;
    }

    // command が開いた doc を窓の view にして、lider に渡す
    public static void OpenDocView(Doc doc)
    {
        var id = AddView(doc);
        Opened.Add(id);
        EditorHost.Focus(id);
    }

    public static void UpdateViews()
    {
        Command.Update();
        Nag.Update();
        foreach (var pair in Views)
            pair.Value.Update();
    }

    // key を押した。command になったら true
    public static void KeyPressed(string key)
    {
        var res = false;
        // 変換中の文字があれば、key は変換に使う
        if (!ImeEditing)
            res = Keymap.OnKeyPressed(key);
        else
            Keymap.TrackModifier(key, true);
        DidKeymap = res;
        Redraw = true;
        UpdateViews();
    }

    public static void KeyReleased(string key)
    {
        Keymap.OnKeyReleased(key);
        DidKeymap = false;
        Redraw = true;
        UpdateViews();
    }

    public static void TextInput(string text)
    {
        if (DidKeymap)
        {
            DidKeymap = false;
            return;
        }
        InsertText(text);
    }

    // 打った文字を active の view に入れる (前の key を見ない)
    public static void InsertText(string text)
    {
        if (Active == NagId)
            Nag.OnTextInput(text);
        else
            ActiveDocView()?.Doc.TextInput(text);
        DidKeymap = false;
        Redraw = true;
        UpdateViews();
    }

    public static void AddPreviousFind(List<string> list, string text)
    {
        list.Remove(text);
        list.Insert(0, text);
    }
}
