// jamo (src/Jamo.ref.cs) で日本語を打つ。
// かなの mode は Ctrl+Space で切り替える (US と JIS のどちらの配列にもある key)。
// かなの mode で打ったローマ字は jamo に入り、変換中の文字は本文の cursor の所に inline で入る (選んだ状態)。
//   入力中: Space で変換、Return で確定、Escape で取消、Backspace / Delete / 左右は変換中の文字の中で動く
//   変換中: Space と Down で次の候補、Up で前の候補、1〜9 で選ぶ、左右で文節を移り Shift と左右で文節を伸び縮み、
//           Return で確定、Escape と Backspace でかなに戻す。ほかの文字を打つと確定して次を始める
//   入力中と変換中: F6 でひらがな、F7 でカタカナ、F9 で全角英字、F10 で半角英字にする
// Ctrl を押すと確定する (変換中の文字は本文に入っているので、Ctrl+S などがそのまま効く)。
// 学習は env LIDER_JAMO_USER の file (無ければ XDG_DATA_HOME か ~/.local/share の lider-jamo.dat) に、開くときに読み、確定のたびに書く。
// jamo は src/Jamo.ref.cs の native の拡張で、lider の player が静的に link する (native と web)。
using System;
using System.Collections.Generic;

namespace LiderEditor;

public static class Ime
{
    const int NBest = 9;
    const int Page = 9;

    public static bool Enabled;
    public static bool Composing;
    static string? dict;
    static bool opened;
    static bool ctrl;
    static bool shift;

    // 変換中の文字の所 (候補の箱を出す所)
    public static float LocX;
    public static float LocY;
    public static float LocW;
    public static float LocH;

    static string? UserPath()
    {
        var path = Environment.GetEnvironmentVariable("LIDER_JAMO_USER");
        if (path != null && path != "")
            return path;
        var dir = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (dir == null || dir == "")
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (home == null || home == "")
                return null;
            dir = home + "/.local/share";
        }
        return dir + "/lider-jamo.dat";
    }

    // 辞書の path を受け取り、裏で読み始める。jamo が無ければ false
    public static bool SetDict(string path)
    {
        if (!Jamo.Available())
        {
            LogMode();
            return false;
        }
        dict = path;
        Jamo.Preload(path);
        LogMode();
        return true;
    }

    static bool Ensure()
    {
        if (opened)
            return true;
        if (dict == null)
            return false;
        opened = Jamo.Open(dict);
        var user = UserPath();
        if (opened && user != null)
            Jamo.LoadUserData(user);
        return opened;
    }

    public static void SetLocation(float x, float y, float w, float h)
    {
        LocX = x;
        LocY = y;
        LocW = w;
        LocH = h;
    }

    // OS の IME の変換中の文字 (start と length は文字の数)。jamo の変換中の文字もここを通る
    public static void OnTextEditing(string text, int start, int length)
    {
        if (!EditorCore.ImeEditing && text == "")
            return;
        var bytes = Str.Len(text);
        var startByte = 0;
        var lengthBytes = 0;
        if (text == "")
            EditorCore.ImeEditing = false;
        else
        {
            EditorCore.ImeEditing = true;
            if (start < 0)
                startByte = bytes;
            else
            {
                startByte = CharOffset(text, start);
                if (length > 0)
                {
                    var end = CharOffset(text, start + length);
                    lengthBytes = Math.Max(0, end - startByte);
                }
            }
        }
        EditorCore.ActiveDocView()?.OnImeTextEditing(text, startByte, lengthBytes);
        EditorCore.Redraw = true;
    }

    // n 文字目の前の byte の位置 (文字の数より先は末尾)
    static int CharOffset(string text, int n)
    {
        var u = new TinySystem.Text.Utf8Bytes(text);
        var at = 0;
        for (var i = 0; i < n && at < u.Length; i++)
            at += Math.Max(1, Str.RuneBytes(u[at]));
        return Math.Min(at, u.Length);
    }

    static void SetPreedit(string text, int from, int len)
    {
        OnTextEditing(text, from, len);
    }

    static void Sync()
    {
        // tcs の Lua の出力は、bool を返す host の関数の戻り値を捨てると壊れる (戻り値を使う)
        var text = Jamo.Preedit(out var preedit) ? preedit ?? "" : "";
        if (text == "")
        {
            if (Composing)
                SetPreedit("", 0, 0);
            Composing = false;
            return;
        }
        Composing = true;
        Jamo.FocusedRange(out var from, out var len);
        SetPreedit(text, from, len);
    }

    // 確定した文字を本文に入れる (変換中の文字は先に消す)
    static void Insert(string text)
    {
        if (Composing)
            SetPreedit("", 0, 0);
        Composing = false;
        if (text != "")
            EditorCore.InsertText(text);
    }

    public static void Commit()
    {
        if (!Composing)
            return;
        var text = Jamo.Commit(out var committed) ? committed ?? "" : "";
        Insert(text);
        var user = UserPath();
        if (user != null)
            Jamo.SaveUserData(user);
    }

    public static void Cancel()
    {
        if (!Composing)
            return;
        Jamo.Reset();
        SetPreedit("", 0, 0);
        Composing = false;
    }

    public static void Toggle()
    {
        if (Enabled)
        {
            Commit();
            Enabled = false;
        }
        else if (Ensure())
            Enabled = true;
        LogMode();
    }

    // LIDER_IME_LOG=1 なら、変換を始めたときと文節を移すか伸び縮みしたときに、標準出力へ
    // 「lider_ime 文節の数 focus の文節 候補1<TAB>候補2...」と書く (録画の道具が、打ちたい字の候補を選ぶため)
    static readonly bool imeLog = Environment.GetEnvironmentVariable("LIDER_IME_LOG") == "1";

    // LIDER_IME_LOG=1 なら、起動したとき (辞書を受け取ったとき) と Ctrl+Space のたびに、かなの mode を標準出力へ
    // 「lider_ime_mode on」か「lider_ime_mode off」と書く (変わらなかったときも書く。録画の道具が、試し打ちせずに mode を知るため)
    static void LogMode()
    {
        if (imeLog)
            Console.WriteLine("lider_ime_mode " + (Enabled ? "on" : "off"));
    }

    static void LogCandidates()
    {
        if (!imeLog)
            return;
        var f = Jamo.Focus();
        var list = new List<string>();
        for (var j = 0; j < Jamo.CandidateCount(f); j++)
            list.Add(Jamo.Candidate(f, j, out var c) ? c ?? "" : "");
        Console.WriteLine("lider_ime " + Jamo.ClauseCount() + " " + f + " " + string.Join("\t", list));
    }

    static void MoveCandidate(int delta)
    {
        var f = Jamo.Focus();
        var n = Jamo.CandidateCount(f);
        if (n > 0)
        {
            var sel = Math.Max(0, Jamo.Selected(f));
            Jamo.Select(f, ((sel + delta) % n + n) % n);
            Sync();
        }
    }

    static bool KeyInput(string k)
    {
        if (k == "space")
        {
            if (Jamo.Convert(NBest))
            {
                Sync();
                LogCandidates();
            }
        }
        else if (k == "return")
            Commit();
        else if (k == "escape")
            Cancel();
        else if (k == "backspace")
        {
            Jamo.Backspace();
            Sync();
        }
        else if (k == "delete")
        {
            Jamo.DeleteForward();
            Sync();
        }
        else if (k == "left")
        {
            Jamo.MoveCursor(-1);
            Sync();
        }
        else if (k == "right")
        {
            Jamo.MoveCursor(1);
            Sync();
        }
        else if (k != "up" && k != "down")
            return false;
        return true;
    }

    static bool KeyConverting(string k)
    {
        var f = Jamo.Focus();
        if (k == "space" || k == "down")
            MoveCandidate(1);
        else if (k == "up")
            MoveCandidate(-1);
        else if (k == "return")
            Commit();
        else if (k == "escape" || k == "backspace")
        {
            Jamo.Revert();
            Sync();
        }
        else if (k == "left" || k == "right")
        {
            var dir = k == "left" ? -1 : 1;
            if (shift)
                Jamo.Resize(dir, NBest);
            else
                Jamo.SetFocus(Math.Max(0, Math.Min(f + dir, Jamo.ClauseCount() - 1)));
            Sync();
            LogCandidates();
        }
        else if (Str.Len(k) == 1 && k != "0" && "123456789".Contains(k))
        {
            var j = Math.Max(0, Jamo.Selected(f)) / Page * Page + int.Parse(k) - 1;
            if (j < Jamo.CandidateCount(f))
            {
                Jamo.Select(f, j);
                Sync();
            }
        }
        else
            return false;
        return true;
    }

    // F6、F7、F9、F10 の書き換え (Jamo.Transform の kind。無ければ -1)
    static int TransformOf(string k)
    {
        if (k == "f6")
            return 0;
        if (k == "f7")
            return 1;
        if (k == "f9")
            return 3;
        if (k == "f10")
            return 2;
        return -1;
    }

    // key を押した。IME が使った key なら true (エディタには渡さない)
    public static bool Key(string k)
    {
        if (k == "left ctrl" || k == "right ctrl")
        {
            ctrl = true;
            Commit();
            return false;
        }
        if (k == "left shift" || k == "right shift")
        {
            shift = true;
            return false;
        }
        if (k == "space" && ctrl)
        {
            Toggle();
            return true;
        }
        if (!Enabled || !Composing)
            return false;
        var kind = TransformOf(k);
        if (kind >= 0)
        {
            Jamo.Transform(kind);
            Sync();
            return true;
        }
        if (!Jamo.Converting())
            return KeyInput(k);
        return KeyConverting(k);
    }

    public static void Release(string k)
    {
        if (k == "left ctrl" || k == "right ctrl")
            ctrl = false;
        if (k == "left shift" || k == "right shift")
            shift = false;
    }

    // 変換を始める文字 (英字、数字、ローマ字の表にある記号)
    static bool Starts(string text)
    {
        if (Str.Len(text) != 1)
            return false;
        var b = Str.Byte(text, 0);
        if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9'))
            return true;
        return ",.-?!/[]~".Contains(text);
    }

    // 打った文字。IME が使った文字なら true
    public static bool Text(string text, bool viewHasDoc)
    {
        if (!Enabled)
            return false;
        if (Composing && Jamo.Converting())
            Commit();
        if (!Composing && (!viewHasDoc || !Starts(text)))
            return false;
        Jamo.Input(text);
        Sync();
        return true;
    }

    public static bool Converting()
    {
        return Composing && Jamo.Converting();
    }

    // 描き直す所が変わったかを見る文字列
    public static string Signature()
    {
        if (!Composing)
            return Enabled ? "on" : "off";
        var text = Jamo.Preedit(out var preedit) ? preedit ?? "" : "";
        var f = Jamo.Focus();
        return text + "|" + (Jamo.Converting() ? "c" : "i") + "|" + f + "|" + Jamo.Selected(f);
    }

    // 変換中の文節の候補の一覧を、変換中の文字の下 (入らなければ上) に描く
    public static void DrawCandidates(float w, float h)
    {
        if (!Converting())
            return;
        var f = Jamo.Focus();
        var n = Jamo.CandidateCount(f);
        if (n < 2)
            return;
        var sel = Math.Max(0, Jamo.Selected(f));
        var before = sel / Page * Page;
        var lh = DrawList.FontHeight();
        var texts = new List<string>();
        var indices = new List<int>();
        var width = 0.0f;
        for (var j = before; j < Math.Min(n, before + Page); j++)
        {
            var c = Jamo.Candidate(f, j, out var candidate) ? candidate ?? "" : "";
            var t = (j - before + 1) + " " + c;
            texts.Add(t);
            indices.Add(j);
            width = Math.Max(width, DrawList.Width(t));
        }
        width += Style.PaddingX * 2;
        var boxH = texts.Count * lh;
        var x = Math.Max(Math.Min(LocX, Math.Max(0, w - width)), 0);
        var y = LocY + LocH;
        if (y + boxH > h)
            y = LocY - boxH;
        DrawList.Rect(x, y, width, boxH, Style.Background3);
        for (var i = 0; i < texts.Count; i++)
        {
            var iy = y + i * lh;
            var selected = indices[i] == sel;
            if (selected)
                DrawList.Rect(x, iy, width, lh, Style.Selection);
            DrawList.DrawText(Style.CodeFont, texts[i], x + Style.PaddingX, iy, selected ? Style.Accent : Style.Text);
        }
    }
}
