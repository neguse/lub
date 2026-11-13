// key の名前から command (Lite XL の core/keymap.lua の既定の keymap)。
// 押した key を修飾 key と合わせた名前 ("ctrl+shift+left") にし、並べた command を効くまで順に試す。
// key の名前は lub の EventData.Plain (修飾 key なしの文字) で引くので、JIS でも Ctrl+[ は [ の刻印の key で効く。
using System;
using System.Collections.Generic;

namespace LiderEditor;

public static class Keymap
{
    static readonly Dictionary<string, string> map = new Dictionary<string, string>();

    static void Add(string stroke, string commands)
    {
        map[stroke] = commands;
    }

    public static void Init()
    {
        if (map.Count > 0)
            return;
        Add("ctrl+p", "core:find-file");
        Add("ctrl+o", "core:open-file");
        Add("ctrl+n", "core:new-doc");

        Add("ctrl+f", "find-replace:find");
        Add("ctrl+r", "find-replace:replace");
        Add("f3", "find-replace:repeat-find");
        Add("shift+f3", "find-replace:previous-find");
        Add("ctrl+i", "find-replace:toggle-sensitivity");
        Add("ctrl+g", "doc:go-to-line");
        Add("ctrl+s", "doc:save");
        Add("ctrl+shift+s", "doc:save-as");

        Add("ctrl+z", "doc:undo");
        Add("ctrl+y", "doc:redo");
        Add("ctrl+x", "doc:cut");
        Add("ctrl+c", "doc:copy");
        Add("ctrl+v", "doc:paste");
        Add("ctrl+insert", "doc:copy");
        Add("shift+insert", "doc:paste");
        Add("escape", "command:escape doc:select-none dialog:select-no");
        Add("tab", "command:complete doc:indent");
        Add("shift+tab", "doc:unindent");
        Add("backspace", "doc:backspace");
        Add("shift+backspace", "doc:backspace");
        Add("ctrl+backspace", "doc:delete-to-previous-word-start");
        Add("ctrl+shift+backspace", "doc:delete-to-previous-word-start");
        Add("delete", "doc:delete");
        Add("shift+delete", "doc:delete");
        Add("ctrl+delete", "doc:delete-to-next-word-end");
        Add("ctrl+shift+delete", "doc:delete-to-next-word-end");
        Add("return", "command:submit doc:newline dialog:select");
        Add("keypad enter", "command:submit doc:newline dialog:select");
        Add("ctrl+return", "doc:newline-below");
        Add("ctrl+shift+return", "doc:newline-above");
        Add("ctrl+j", "doc:join-lines");
        Add("ctrl+a", "doc:select-all");
        Add("ctrl+d", "find-replace:select-add-next doc:select-word");
        Add("ctrl+f3", "find-replace:select-next");
        Add("ctrl+shift+f3", "find-replace:select-previous");
        Add("ctrl+l", "doc:select-lines");
        Add("ctrl+shift+l", "find-replace:select-add-all doc:select-word");
        Add("ctrl+/", "doc:toggle-line-comments");
        Add("ctrl+shift+/", "doc:toggle-block-comments");
        Add("ctrl+up", "doc:move-lines-up");
        Add("ctrl+down", "doc:move-lines-down");
        Add("ctrl+shift+d", "doc:duplicate-lines");
        Add("ctrl+shift+k", "doc:delete-lines");

        Add("left", "doc:move-to-previous-char dialog:previous-entry");
        Add("right", "doc:move-to-next-char dialog:next-entry");
        Add("up", "command:select-previous doc:move-to-previous-line");
        Add("down", "command:select-next doc:move-to-next-line");
        Add("ctrl+left", "doc:move-to-previous-word-start");
        Add("ctrl+right", "doc:move-to-next-word-end");
        Add("ctrl+[", "doc:move-to-previous-block-start");
        Add("ctrl+]", "doc:move-to-next-block-end");
        Add("home", "doc:move-to-start-of-indentation");
        Add("end", "doc:move-to-end-of-line");
        Add("ctrl+home", "doc:move-to-start-of-doc");
        Add("ctrl+end", "doc:move-to-end-of-doc");
        Add("pageup", "doc:move-to-previous-page");
        Add("pagedown", "doc:move-to-next-page");

        Add("shift+1lclick", "doc:select-to-cursor");
        Add("ctrl+1lclick", "doc:split-cursor");
        Add("1lclick", "doc:set-cursor");
        Add("2lclick", "doc:set-cursor-word");
        Add("3lclick", "doc:set-cursor-line");
        Add("shift+left", "doc:select-to-previous-char");
        Add("shift+right", "doc:select-to-next-char");
        Add("shift+up", "doc:select-to-previous-line");
        Add("shift+down", "doc:select-to-next-line");
        Add("ctrl+shift+left", "doc:select-to-previous-word-start");
        Add("ctrl+shift+right", "doc:select-to-next-word-end");
        Add("ctrl+shift+[", "doc:select-to-previous-block-start");
        Add("ctrl+shift+]", "doc:select-to-next-block-end");
        Add("shift+home", "doc:select-to-start-of-indentation");
        Add("shift+end", "doc:select-to-end-of-line");
        Add("ctrl+shift+home", "doc:select-to-start-of-doc");
        Add("ctrl+shift+end", "doc:select-to-end-of-doc");
        Add("shift+pageup", "doc:select-to-previous-page");
        Add("shift+pagedown", "doc:select-to-next-page");
        Add("ctrl+shift+up", "doc:create-cursor-previous-line");
        Add("ctrl+shift+down", "doc:create-cursor-next-line");
    }

    // 修飾 key の名前から状態を変える。修飾 key なら true
    public static bool TrackModifier(string key, bool down)
    {
        if (key == "left ctrl" || key == "right ctrl")
            EditorCore.Ctrl = down;
        else if (key == "left shift" || key == "right shift")
            EditorCore.Shift = down;
        else if (key == "left alt")
            EditorCore.Alt = down;
        else if (key == "right alt")
        {
            EditorCore.AltGr = down;
            // Windows の AltGr は Ctrl+Alt で届く
            if (down)
                EditorCore.Ctrl = false;
        }
        else
            return false;
        return true;
    }

    // 押している修飾 key を前に付けた名前
    static string Stroke(string key)
    {
        var s = "";
        if (EditorCore.Ctrl)
            s += "ctrl+";
        if (EditorCore.Shift)
            s += "shift+";
        if (EditorCore.Alt)
            s += "alt+";
        if (EditorCore.AltGr)
            s += "altgr+";
        return s + key;
    }

    // key を押した。command になったら true
    public static bool OnKeyPressed(string key)
    {
        if (TrackModifier(key, true))
            return false;
        return Perform(Stroke(key), 0, 0, 0);
    }

    public static void OnKeyReleased(string key)
    {
        TrackModifier(key, false);
    }

    static bool Perform(string stroke, float x, float y, int clicks)
    {
        if (!map.TryGetValue(stroke, out var commands))
            return false;
        foreach (var name in commands.Split(' '))
            if (Commands.Perform(name, x, y, clicks))
                return true;
        return false;
    }

    // 左 button を押した (clicks は続けて押した数)。command にならなければ true
    public static bool OnMousePressed(float x, float y, int clicks)
    {
        var n = (clicks - 1) % 3 + 1;
        return !(Perform(Stroke(n + "lclick"), x, y, clicks) || Perform(Stroke("lclick"), x, y, clicks)
            || Perform(Stroke(n + "click"), x, y, clicks) || Perform(Stroke("click"), x, y, clicks));
    }
}
