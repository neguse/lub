// 配置の規則: スクリーンの上にウィンドウを並べる。
// ウィンドウの順番とスクリーンの大きさから、各ウィンドウの Bounds (スクリーンの面の上の場所) を決める。
namespace LiderCore;

public interface ILayout
{
    void Arrange(Screen screen);
}

// 横一列。ウィンドウは自分の大きさ (Window.W、H と Scale) を持ち、間を Gap だけ空けて左から並べる。
// 最初のウィンドウの真ん中を、スクリーンの真ん中に置く。
// つかんでいるウィンドウ (Screen.Held) は動かさず、隣のウィンドウの真ん中を越えたら並びを入れ替える
public class Strip : ILayout
{
    // ウィンドウどうしの間 (メートル)
    public const float Gap = 0.06f;

    public void Arrange(Screen screen)
    {
        var windows = screen.Windows;
        var held = screen.Held;
        if (held != null && held.Bounds != null && windows.Contains(held))
        {
            var at = windows.IndexOf(held);
            if (at + 1 < windows.Count && windows[at + 1].Bounds != null && held.Bounds.X > windows[at + 1].Bounds!.X)
                Swap(windows, at, at + 1);
            else if (at > 0 && windows[at - 1].Bounds != null && held.Bounds.X < windows[at - 1].Bounds!.X)
                Swap(windows, at, at - 1);
        }
        var right = 0.0f;
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var half = window.W * window.Scale * 0.5f;
            var center = i == 0 ? 0 : right + half;
            right = center + half + Gap;
            if (window == held)
                continue;
            window.Bounds = new Bounds(center, 0, half * 2, window.H * window.Scale);
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
        var w = screen.Width;
        var h = screen.Height;
        for (var i = 0; i < n; i++)
            screen.Windows[i].Bounds = Rows
                ? new Bounds(0, h * 0.5f - h * (i + 0.5f) / n, w, h / n)
                : new Bounds(-w * 0.5f + w * (i + 0.5f) / n, 0, w / n, h);
    }
}

// focus のある (無ければ最初の) ウィンドウだけを、スクリーンいっぱいに出す。ほかのウィンドウは出さない
public class Full : ILayout
{
    public void Arrange(Screen screen)
    {
        var shown = screen.Focus ?? (screen.Windows.Count > 0 ? screen.Windows[0] : null);
        foreach (var window in screen.Windows)
            window.Bounds = window == shown ? new Bounds(0, 0, screen.Width, screen.Height) : null;
    }
}
