// Types Japanese with jamo (src/Jamo.ref.cs).
// Kana mode is toggled with Ctrl+Space (a key on both US and JIS layouts).
// Romaji typed in kana mode goes into jamo, and the composing text goes inline at the text cursor (selected).
//   While inputting: Space converts, Return commits, Escape cancels, Backspace / Delete / left/right move within the composing text
//   While converting: Space and Down for the next candidate, Up for the previous candidate, 1-9 to choose, left/right to move between clauses, Shift+left/right to extend or shrink a clause,
//           Return commits, Escape and Backspace go back to kana. Typing any other character commits and starts the next
//   While inputting and converting: F6 for hiragana, F7 for katakana, F9 for full-width Latin, F10 for half-width Latin
// Pressing Ctrl commits (the composing text is already in the text, so Ctrl+S and the like work as is).
// Learning data is read on open and written on every commit, in the file given by env LIDER_JAMO_USER (if missing, lider-jamo.dat in XDG_DATA_HOME or ~/.local/share).
// jamo is the native extension in src/Jamo.ref.cs, statically linked by the lider player (native and web).
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

    // Position of the composing text (where the candidate box goes)
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

    // Receives the dictionary path and starts loading it in the background. false if there is no jamo
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

    // Composing text of the OS IME (start and length are in characters). jamo's composing text also goes through here
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

    // Byte position before character n (the end if beyond the character count)
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
        // tcs's Lua output breaks when the return value of a host function returning bool is discarded (so the return value is used)
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

    // Inserts committed text into the text (removes the composing text first)
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

    // If LIDER_IME_LOG=1, when conversion starts and when a clause is moved, extended, or shrunk, writes
    // "lider_ime clause-count focused-clause candidate1<TAB>candidate2..." to standard output (so the recording tool can choose the candidate for the text it wants to type)
    static readonly bool imeLog = Environment.GetEnvironmentVariable("LIDER_IME_LOG") == "1";

    // If LIDER_IME_LOG=1, at startup (when the dictionary is received) and on every Ctrl+Space, writes the kana mode to standard output
    // as "lider_ime_mode on" or "lider_ime_mode off" (also when it did not change, so the recording tool knows the mode without test typing)
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

    // Transforms for F6, F7, F9, F10 (the kind for Jamo.Transform; -1 if none)
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

    // A key was pressed. true if the IME used the key (it is not passed to the editor)
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

    // Characters that start conversion (letters, digits, and symbols in the romaji table)
    static bool Starts(string text)
    {
        if (Str.Len(text) != 1)
            return false;
        var b = Str.Byte(text, 0);
        if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9'))
            return true;
        return ",.-?!/[]~".Contains(text);
    }

    // Typed text. true if the IME used it
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

    // String used to check whether anything that needs redrawing changed
    public static string Signature()
    {
        if (!Composing)
            return Enabled ? "on" : "off";
        var text = Jamo.Preedit(out var preedit) ? preedit ?? "" : "";
        var f = Jamo.Focus();
        return text + "|" + (Jamo.Converting() ? "c" : "i") + "|" + f + "|" + Jamo.Selected(f);
    }

    // Draws the candidate list of the clause being converted below the composing text (above if it does not fit)
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
