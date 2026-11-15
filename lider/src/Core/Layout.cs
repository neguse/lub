// 配置の規則: スクリーンの上にウィンドウを並べる。
// ウィンドウの順番とスクリーンの大きさから、各ウィンドウの面の上の位置 (Position の X と Y) と大きさ (Size) を決める。
// 奥行き (Position の Z) は変えない。
using Lub;

namespace LiderCore;

public interface ILayout
{
    void Arrange(Screen screen);
}

// 横一列。ウィンドウは自分の大きさ (Window.UnscaledSize と Scale) を持ち、間を Gap だけ空けて左から並べる。
// Screen.Origin のウィンドウ (無ければ最初のウィンドウ) の真ん中を、スクリーンの真ん中に置く。
// つかんでいるウィンドウ (Screen.Held) は動かさず、隣のウィンドウの真ん中を越えたら並びを入れ替える
public class Strip : ILayout
{
    // ウィンドウどうしの間 (メートル)
    public const float Gap = 0.06f;

    public void Arrange(Screen screen)
    {
        var windows = screen.Windows;
        var held = screen.Held;
        if (held != null && held.Position != null && windows.Contains(held))
        {
            var at = windows.IndexOf(held);
            var x = held.Position.Value.X;
            if (at + 1 < windows.Count && windows[at + 1].Position != null && x > windows[at + 1].Position!.Value.X)
                Swap(windows, at, at + 1);
            else if (at > 0 && windows[at - 1].Position != null && x < windows[at - 1].Position!.Value.X)
                Swap(windows, at, at - 1);
        }
        // 最初のウィンドウの真ん中を 0 として並べたときの、Origin の真ん中
        var origin = 0.0f;
        var right = 0.0f;
        for (var i = 0; i < windows.Count; i++)
        {
            var half = windows[i].UnscaledSize.X * windows[i].Scale * 0.5f;
            var center = i == 0 ? 0 : right + half;
            right = center + half + Gap;
            if (windows[i] == screen.Origin)
                origin = center;
        }
        right = 0.0f;
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var size = window.UnscaledSize * window.Scale;
            var half = size.X * 0.5f;
            var center = i == 0 ? 0 : right + half;
            right = center + half + Gap;
            if (window == held)
                continue;
            window.Place(center - origin, 0);
            window.Size = size;
        }
    }

    static void Swap(System.Collections.Generic.List<Window> windows, int a, int b)
    {
        var w = windows[a];
        windows[a] = windows[b];
        windows[b] = w;
    }
}

// ウィンドウを並べた順に、列 (Rows なら行) に等分して出す
public class Even : ILayout
{
    public bool Rows;

    public void Arrange(Screen screen)
    {
        var n = screen.Windows.Count;
        var w = screen.Size.X;
        var h = screen.Size.Y;
        for (var i = 0; i < n; i++)
        {
            var window = screen.Windows[i];
            if (Rows)
            {
                window.Place(0, h * 0.5f - h * (i + 0.5f) / n);
                window.Size = new Vec2(w, h / n);
            }
            else
            {
                window.Place(-w * 0.5f + w * (i + 0.5f) / n, 0);
                window.Size = new Vec2(w / n, h);
            }
        }
    }
}

// 最初のウィンドウを左に大きく出し、残りを右に上から積む。
// Ratio は、スクリーンの幅のうち最初のウィンドウに使う割合。ウィンドウが 1 つなら、スクリーンいっぱいに出す
public class MainStack : ILayout
{
    public float Ratio = 0.5f;

    public void Arrange(Screen screen)
    {
        var n = screen.Windows.Count;
        var w = screen.Size.X;
        var h = screen.Size.Y;
        if (n == 0)
            return;
        var main = n == 1 ? w : w * Ratio;
        screen.Windows[0].Place(-w * 0.5f + main * 0.5f, 0);
        screen.Windows[0].Size = new Vec2(main, h);
        for (var i = 1; i < n; i++)
        {
            screen.Windows[i].Place(-w * 0.5f + main + (w - main) * 0.5f, h * 0.5f - h * (i - 0.5f) / (n - 1));
            screen.Windows[i].Size = new Vec2(w - main, h / (n - 1));
        }
    }
}

// focus のある (無ければ最初の) ウィンドウだけを、スクリーンいっぱいに出す。ほかのウィンドウは出さない
public class Full : ILayout
{
    public void Arrange(Screen screen)
    {
        var shown = screen.Focus ?? (screen.Windows.Count > 0 ? screen.Windows[0] : null);
        foreach (var window in screen.Windows)
        {
            if (window != shown)
            {
                window.Position = null;
                continue;
            }
            window.Place(0, 0);
            window.Size = screen.Size;
        }
    }
}
