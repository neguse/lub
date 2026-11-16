// 指している物に対して、そのとき効く操作の案内 (ボタンと、押すと何が起きるか)。
// grip でつかむような、見て分かる操作は出さない。
using System.Collections.Generic;

namespace LiderCore;

public enum HintIcon
{
    // 丸の中に Key の字 (コントローラーの A や X)
    Face,
    // trigger を引く操作と、stick を上下か左右に倒す操作。Key は手 (L か R)
    Trigger,
    StickUpDown,
    StickLeftRight,
    // key cap の中に Key の字
    KeyCap,
    MouseLeft,
    MouseWheel,
    // 絵の無い文字だけの行
    None,
}

public enum HintTarget
{
    // 何も無い所 (ゲームの物)
    Nothing,
    Window,
    // 窓の閉じる印
    Close,
    Inspect,
    // 窓の右か下の端
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
    // インスペクタの行を指しているとき、その行を開けるか、開いているか
    public bool Openable;
    // 止めている間に、書き換えられる値を指している
    public bool Editable;
    public bool Open;
    // 宣言へ飛ぶ印、絞り込む印、パンくずを指しているか
    public bool Jump;
    public bool Scope;
    public bool Crumb;
    // 窓の class の行の、個体の印か、続きを出す印を指しているか
    public bool InstanceMark;
    public bool MoreMark;
    public bool InspectShown;
    // focus のある窓の本文がゲームの動いている版と違うか、動いている版を左右に並べるか
    public bool Differs;
    public bool SideBySide;
    public bool Paused;
    // 飛べる compile error があるか
    public bool Errors;
    // 音と演出の強さ (Lider.Juice)
    public int Juice = 2;
    // 指しているゲームの物を描いたコード (内側から)
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
                // ゲームの物を調べたり開いたりできるのは、止めている間だけ。
                // 何かを指しているときは、それが何かと開き方だけを出す
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
        // key を割り当てていない命令の行は出さない
        var bound = new List<Hint>();
        foreach (var hint in hints)
            if (hint.Icon != HintIcon.KeyCap || hint.Key != "")
                bound.Add(hint);
        return bound;
    }

    // 命令に割り当てた key の、案内に出す書き方 (どれにも割り当てが無ければ "")。
    // 修飾 key が同じ矢印は 1 つにまとめ ("Alt+←→")、修飾 key の無い key は "/" で、ほかは空白でつなぐ
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
