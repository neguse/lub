// Commands from key names (the default keymap of Lite XL's core/keymap.lua).
// Combines the pressed key with the modifier keys into a name ("ctrl+shift+left") and tries the listed commands in order until one takes effect.
// Key names are looked up by lub's EventData.Plain (the character without modifiers), so even on JIS, Ctrl+[ works with the key labeled [.
// The table is the default bindings with the user's changes layered on top. The changes are read and written as text with one "key command" per line (a line without a command unbinds that key).
using System;
using System.Collections.Generic;

namespace LiderEditor;

public static class Keymap
{
    static readonly Dictionary<string, string> map = new Dictionary<string, string>();
    // Default bindings and the user's changes (key to list of commands; empty unbinds that key)
    static readonly Dictionary<string, string> defaults = new Dictionary<string, string>();
    static readonly Dictionary<string, string> changes = new Dictionary<string, string>();

    static void Add(string stroke, string commands)
    {
        defaults[stroke] = commands;
        map[stroke] = commands;
    }

    // Layers the changes on top of the default bindings
    static void Rebuild()
    {
        map.Clear();
        foreach (var pair in defaults)
            if (!changes.ContainsKey(pair.Key))
                map[pair.Key] = pair.Value;
        foreach (var pair in changes)
            if (pair.Value != "")
                map[pair.Key] = pair.Value;
    }

    // Sets the binding of stroke to commands (unbinds if empty). If it becomes the same as the default, removes it from the changes
    static void Change(string stroke, string commands)
    {
        var same = defaults.TryGetValue(stroke, out var was) ? was == commands : commands == "";
        if (same)
            changes.Remove(stroke);
        else
            changes[stroke] = commands;
    }

    // Binds command name to stroke. Other keys bound only to that command are unbound, and the previous binding of stroke is gone
    public static void Bind(string stroke, string name)
    {
        Init();
        var old = new List<string>();
        foreach (var pair in map)
            if (pair.Value == name && pair.Key != stroke)
                old.Add(pair.Key);
        foreach (var key in old)
            Change(key, "");
        Change(stroke, name);
        Rebuild();
    }

    // Discards the changes and goes back to the default bindings
    public static void Reset()
    {
        Init();
        changes.Clear();
        Rebuild();
    }

    // Reloads the changes from the lines of text
    public static void Load(string text)
    {
        Init();
        changes.Clear();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "" || line.StartsWith("#"))
                continue;
            var stroke = "";
            var commands = new List<string>();
            foreach (var word in line.Split(' '))
            {
                if (word == "")
                    continue;
                if (stroke == "")
                    stroke = word;
                else
                    commands.Add(word);
            }
            Change(stroke, string.Join(" ", commands));
        }
        Rebuild();
    }

    // Lines of the changes
    public static List<string> Lines()
    {
        var lines = new List<string>();
        foreach (var pair in changes)
            lines.Add(pair.Value == "" ? pair.Key : pair.Key + " " + pair.Value);
        return lines;
    }

    // List of commands bound to stroke ("" if none)
    public static string CommandsOf(string stroke)
    {
        Init();
        return map.TryGetValue(stroke, out var commands) ? commands : "";
    }

    // Whether the key can be bound to a command.
    // Keys without modifiers or with only Shift are input to the content, so only keys with Ctrl or Alt and F keys can be bound
    public static bool Assignable(string stroke)
    {
        if (stroke.Contains("ctrl+") || stroke.Contains("alt+"))
            return true;
        var key = stroke.Replace("shift+", "");
        return key.StartsWith("f") && int.TryParse(key.Replace("f", ""), out _);
    }

    // How key names are written when shown to people (names not here are uppercased)
    static readonly Dictionary<string, string> shownNames = new Dictionary<string, string>
    {
        { "ctrl", "Ctrl" }, { "shift", "Shift" }, { "alt", "Alt" }, { "altgr", "AltGr" },
        { "return", "Enter" }, { "escape", "Esc" }, { "tab", "Tab" }, { "space", "Space" },
        { "backspace", "Backspace" }, { "delete", "Delete" }, { "insert", "Insert" },
        { "home", "Home" }, { "end", "End" }, { "pageup", "PageUp" }, { "pagedown", "PageDown" },
        { "left", "←" }, { "right", "→" }, { "up", "↑" }, { "down", "↓" },
    };

    // Turns stroke into the form shown to people ("ctrl+shift+left" becomes "Ctrl+Shift+←")
    public static string Shown(string stroke)
    {
        if (stroke == "")
            return "";
        var parts = new List<string>();
        foreach (var part in stroke.Split('+'))
        {
            // When the key is +, as in "ctrl++"
            if (part == "")
            {
                if (parts.Count == 0 || parts[parts.Count - 1] != "+")
                    parts.Add("+");
                continue;
            }
            parts.Add(shownNames.TryGetValue(part, out var shown) ? shown : part.ToUpper());
        }
        return string.Join("+", parts);
    }

    // Display form of the key bound to command name ("" if none)
    public static string ShownOf(string name)
    {
        return Shown(StrokeOf(name));
    }

    public static void Init()
    {
        if (defaults.Count > 0)
            return;
        // lider search (files, declarations, and commands). When running the editor alone, it becomes the editor's file search
        Add("ctrl+p", "search:open core:find-file");
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

        // Game time and focus (operations pressed repeatedly, bound to a single unmodified key)
        Add("f1", "focus:game");
        Add("f5", "game:pause");
        Add("shift+f5", "game:restart");
        Add("f6", "game:step-back");
        Add("f7", "game:step-forward");
        // Operations on the content
        Add("ctrl+b", "code:go-to-definition");
        Add("ctrl+-", "code:back");
        Add("ctrl+e", "code:next-error");
        Add("ctrl+w", "window:close");
        Add("ctrl+tab", "tab:next");
        Add("ctrl+shift+tab", "tab:previous");
        // Operations on windows and screens
        Add("alt+left", "window:focus-left");
        Add("alt+right", "window:focus-right");
        Add("alt+up", "window:focus-up");
        Add("alt+down", "window:focus-down");
        Add("shift+alt+left", "window:move-back");
        Add("shift+alt+right", "window:move-forward");
        Add("shift+alt+up", "window:move-back");
        Add("shift+alt+down", "window:move-forward");
        for (var i = 1; i <= 9; i++)
            Add("alt+" + i, "window:focus-" + i);
        Add("alt+pageup", "window:grow");
        Add("alt+pagedown", "window:shrink");
        Add("shift+alt+pageup", "window:widen");
        Add("shift+alt+pagedown", "window:narrow");
        Add("ctrl+alt+left", "tab:move-left");
        Add("ctrl+alt+right", "tab:move-right");
        Add("alt+return", "window:zen");
        Add("alt+l", "arrange:next");
        Add("alt+i", "inspector:toggle");
        Add("alt+d", "game:compare");
    }

    // Key bound to command name ("" if none). Shown in search and help
    public static string StrokeOf(string name)
    {
        Init();
        // Looks first at keys bound only to that command
        foreach (var pair in map)
            if (pair.Value == name)
                return pair.Key;
        foreach (var pair in map)
            foreach (var command in pair.Value.Split(' '))
                if (command == name)
                    return pair.Key;
        return "";
    }

    // Whether pressing key with the current modifier keys gives command name
    public static bool Binds(string key, string name)
    {
        Init();
        if (!map.TryGetValue(Stroke(key), out var commands))
            return false;
        foreach (var command in commands.Split(' '))
            if (command == name)
                return true;
        return false;
    }

    // Changes state from a modifier key name. true if it is a modifier key
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
            // AltGr on Windows arrives as Ctrl+Alt
            if (down)
                EditorCore.Ctrl = false;
        }
        else
            return false;
        return true;
    }

    // Name prefixed with the held modifier keys
    public static string Stroke(string key)
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

    // A key was pressed. true if it became a command
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

    // The left button was pressed (clicks is the number of consecutive presses). true if it did not become a command
    public static bool OnMousePressed(float x, float y, int clicks)
    {
        var n = (clicks - 1) % 3 + 1;
        return !(Perform(Stroke(n + "lclick"), x, y, clicks) || Perform(Stroke("lclick"), x, y, clicks)
            || Perform(Stroke(n + "click"), x, y, clicks) || Perform(Stroke("click"), x, y, clicks));
    }
}
