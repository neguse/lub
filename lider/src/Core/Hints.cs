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
    public bool Open;
    // 宣言へ飛ぶ印、絞り込む印、パンくずを指しているか
    public bool Jump;
    public bool Scope;
    public bool Crumb;
    // 窓の class の行の、個体の印か、続きを出す印を指しているか
    public bool InstanceMark;
    public bool MoreMark;
    public bool InspectShown;
    // focus のある窓の本文がゲームの動いている版と違うか、動いている版を左右に並べるか (F9)
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

public static class Hints
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
                press.Label = c.InstanceMark ? "この個体に絞り込む・やめる" : c.MoreMark ? "続きの個体を並べる" : "カーソルを置く・引いて選ぶ";
                hints.Add(press);
                if (!c.Vr)
                    hints.Add(new Hint(HintIcon.KeyCap, "右ドラッグ", "窓の並びを変える (ホイールで大きさ)"));
                hints.Add(scroll);
                if (!c.Vr)
                {
                    hints.Add(new Hint(HintIcon.KeyCap, "Ctrl+P", "file と宣言を探す・作る"));
                    hints.Add(new Hint(HintIcon.KeyCap, "F12 Ctrl+-", "定義へ飛ぶ・戻る"));
                    hints.Add(new Hint(HintIcon.KeyCap, "Ctrl+Tab", "隣のタブへ"));
                    hints.Add(new Hint(HintIcon.KeyCap, "Ctrl+Alt+←→", "タブを隣の窓へ移す"));
                    hints.Add(new Hint(HintIcon.KeyCap, "Alt+←→", "隣の窓へ"));
                    hints.Add(new Hint(HintIcon.KeyCap, "Alt+Del", "file を消す"));
                    hints.Add(new Hint(HintIcon.KeyCap, "Esc Ctrl+W", "タブを閉じる"));
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
                hints.Add(scroll);
                break;
            default:
                foreach (var w in c.What)
                    hints.Add(new Hint(HintIcon.None, "", w));
                // 何かを指しているときは、それが何かと開き方だけを出す
                press.Label = c.What.Count > 0 ? "このコードを開く" : "指した物を描いたコードを開く";
                hints.Add(press);
                if (c.What.Count > 0)
                    return hints;
                if (!c.Vr)
                {
                    hints.Add(new Hint(HintIcon.KeyCap, "右ドラッグ", "窓の列を動かす"));
                    hints.Add(new Hint(HintIcon.KeyCap, "Ctrl+P", "file と宣言を探す・作る"));
                    hints.Add(new Hint(HintIcon.KeyCap, "F4", c.Juice == 2 ? "音を切る" : c.Juice == 1 ? "演出を抑える" : "音と演出を戻す"));
                    hints.Add(new Hint(HintIcon.KeyCap, "F1", "ゲームを触る (Shift+F1 でゲームだけ)"));
                }
                break;
        }
        var tree = c.InspectShown ? "インスペクタをしまう" : "インスペクタを出す";
        hints.Add(c.Vr ? new Hint(HintIcon.Face, "X", tree) : new Hint(HintIcon.KeyCap, "F2", tree));
        if (!c.Vr && c.Differs)
            hints.Add(new Hint(HintIcon.KeyCap, "F9", c.SideBySide ? "動いている版を行の間に出す" : "動いている版を左に並べる"));
        if (!c.Vr)
            hints.Add(new Hint(HintIcon.KeyCap, "F11", c.Zen ? "窓を元の大きさに戻す" : "窓を画面いっぱいに広げる"));
        if (!c.Vr)
            hints.Add(new Hint(HintIcon.KeyCap, "F5", c.Paused ? "ゲームを続ける" : "ゲームを止める"));
        if (!c.Vr)
            hints.Add(new Hint(HintIcon.KeyCap, "Shift+F5", "ゲームを最初から作り直す"));
        if (!c.Vr && c.Errors)
            hints.Add(new Hint(HintIcon.KeyCap, "F8", "エラーの行へ"));
        if (c.Paused)
            hints.Add(c.Vr ? new Hint(HintIcon.StickLeftRight, "L", "1 frame 戻す・進める") : new Hint(HintIcon.KeyCap, "F6/F7", "1 frame 戻す・進める"));
        return hints;
    }
}
