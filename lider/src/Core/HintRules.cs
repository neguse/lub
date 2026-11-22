// Hints for the actions that work on the pointed thing at that moment (the button, and what pressing it does).
// Obvious actions, such as grabbing with grip, are not shown.
using System.Collections.Generic;

namespace LiderCore;

public enum HintIcon
{
    // The Key letter in a circle (A or X on a controller)
    Face,
    // Pulling a trigger, and tilting a stick up/down or left/right. Key is the hand (L or R)
    Trigger,
    StickUpDown,
    StickLeftRight,
    // The Key letter in a key cap
    KeyCap,
    MouseLeft,
    MouseWheel,
    // A text-only line without an icon
    None,
}

public enum HintTarget
{
    // An empty spot (the game's objects)
    Nothing,
    Window,
    // A window's close mark
    Close,
    Inspect,
    // The right or bottom edge of a window
    Edge,
}

public class Hint
{
    public HintIcon Icon;
    public string Key = "";
    public string Label = "";

    public Hint(HintIcon icon, string key, string label)
    {
        Icon = icon;
        Key = key;
        Label = label;
    }
}

public class HintContext
{
    public HintTarget Target;
    // When pointing at an inspector line, whether that line can be opened and whether it is open
    public bool Openable;
    // Pointing at a value that can be edited while paused
    public bool Editable;
    public bool Open;
    // Whether pointing at a jump-to-declaration mark, a scope-in mark, or a breadcrumb
    public bool Jump;
    public bool Scope;
    public bool Crumb;
    // Whether pointing at an instance mark or the show-more mark on a class line in the window
    public bool InstanceMark;
    public bool MoreMark;
    public bool InspectShown;
    // Whether the focused window's text differs from the version the game is running, and whether the running version is shown side by side
    public bool Differs;
    public bool SideBySide;
    public bool Paused;
    // Whether there is a compile error to jump to
    public bool Errors;
    // Strength of sounds and effects (Lider.Juice)
    public int Juice = 2;
    // Code that drew the pointed game object (innermost first)
    public List<string> What = new List<string>();
    public bool Zen;
    public bool Vr;
}

public static class HintRules
{
    public static List<Hint> For(HintContext c)
    {
        var hints = new List<Hint>();
        var press = c.Vr ? new Hint(HintIcon.Trigger, "R", "") : new Hint(HintIcon.MouseLeft, "", "");
        var scroll = c.Vr ? new Hint(HintIcon.StickUpDown, "R", "scroll") : new Hint(HintIcon.MouseWheel, "", "scroll");
        switch (c.Target)
        {
            case HintTarget.Edge:
                press.Label = "引いて窓の大きさを変える";
                hints.Add(press);
                return hints;
            case HintTarget.Window:
                press.Label = c.InstanceMark ? "この個体に絞り込む・やめる" : c.MoreMark ? "続きの個体を並べる" : c.Editable ? "値を書き換える" : "カーソルを置く・引いて選ぶ";
                hints.Add(press);
                if (!c.Vr)
                    hints.Add(new Hint(HintIcon.KeyCap, "右ドラッグ", "窓の並びを変える (ホイールで大きさ)"));
                hints.Add(scroll);
                if (!c.Vr)
                {
                    hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "search:open" }), "file と宣言と命令を探す"));
                    hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "code:go-to-definition", "code:back" }), "定義へ飛ぶ・戻る"));
                    hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "tab:next" }), "隣のタブへ"));
                    hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "tab:move-left", "tab:move-right" }), "タブを隣の窓へ移す"));
                    hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "window:focus-left", "window:focus-right", "window:focus-up", "window:focus-down" }), "隣のウィンドウへ"));
                    hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "window:close" }), "タブを閉じる"));
                }
                break;
            case HintTarget.Close:
                press.Label = "窓を閉じる";
                hints.Add(press);
                break;
            case HintTarget.Inspect:
                if (c.Crumb)
                {
                    press.Label = "ここまで戻る";
                    hints.Add(press);
                }
                else if (c.Jump)
                {
                    press.Label = "宣言へ飛ぶ";
                    hints.Add(press);
                }
                else if (c.Scope)
                {
                    press.Label = "ここに絞り込む";
                    hints.Add(press);
                }
                else if (c.Openable)
                {
                    press.Label = c.Open ? "閉じる" : "開く";
                    hints.Add(press);
                }
                else if (c.Editable)
                {
                    press.Label = "値を書き換える";
                    hints.Add(press);
                }
                hints.Add(scroll);
                break;
            default:
                foreach (var w in c.What)
                    hints.Add(new Hint(HintIcon.None, "", w));
                // Game objects can be inspected or opened only while paused.
                // When pointing at something, shows only what it is and how to open it
                press.Label = c.What.Count > 0 ? "このコードを開く" : "指した物を描いたコードを開く";
                if (c.Paused || c.What.Count > 0)
                    hints.Add(press);
                if (c.What.Count > 0)
                    return hints;
                if (!c.Vr)
                {
                    hints.Add(new Hint(HintIcon.KeyCap, "右ドラッグ", "窓の列を動かす"));
                    hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "search:open" }), "file と宣言と命令を探す"));
                    hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "focus:game" }), "ゲームを触る"));
                }
                break;
        }
        var tree = c.InspectShown ? "インスペクタへ移る (もう一度でしまう)" : "インスペクタを出す";
        hints.Add(c.Vr ? new Hint(HintIcon.Face, "X", tree) : new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "inspector:toggle" }), tree));
        if (!c.Vr)
            hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "window:zen" }), c.Zen ? "窓を元の大きさに戻す" : "窓を画面いっぱいに広げる"));
        if (!c.Vr)
            hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "game:pause" }), c.Paused ? "ゲームを続ける" : "ゲームを止める"));
        if (!c.Vr)
            hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "game:restart" }), "ゲームを最初から作り直す"));
        if (!c.Vr && c.Errors)
            hints.Add(new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "code:next-error" }), "エラーの行へ"));
        if (c.Paused)
            hints.Add(c.Vr ? new Hint(HintIcon.StickLeftRight, "L", "1 frame 戻す・進める") : new Hint(HintIcon.KeyCap, KeysOf(new List<string> { "game:step-back", "game:step-forward" }), "1 frame 戻す・進める"));
        // Lines for commands with no key assigned are not shown
        var bound = new List<Hint>();
        foreach (var hint in hints)
            if (hint.Icon != HintIcon.KeyCap || hint.Key != "")
                bound.Add(hint);
        return bound;
    }

    // How the keys assigned to commands are written in hints ("" if none is assigned).
    // Arrows with the same modifiers are combined into one ("Alt+←→"), keys without modifiers are joined with "/", and the others with spaces
    static string KeysOf(List<string> names)
    {
        var text = "";
        var last = "";
        foreach (var name in names)
        {
            var shown = LiderEditor.Keymap.ShownOf(name);
            if (shown == "")
                continue;
            var parts = shown.Split('+');
            var key = parts[parts.Length - 1];
            var mods = "";
            for (var i = 0; i + 1 < parts.Length; i++)
                mods += parts[i] + "+";
            var arrow = key == "←" || key == "→" || key == "↑" || key == "↓";
            if (text == "")
                text = shown;
            else if (arrow && mods == last && mods != "")
                text += key;
            else if (mods == "" && last == "")
                text += "/" + shown;
            else
                text += " " + shown;
            last = arrow || mods == "" ? mods : "\n";
        }
        return text;
    }
}
