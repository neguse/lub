// 配置の規則: スクリーンの上にウィンドウを並べる。
// ウィンドウの順番とスクリーンの大きさから、各ウィンドウの面の上の位置 (Position の X と Y) と大きさ (Size) を決める。
// 奥行き (Position の Z) は変えない。
using System;
using Lub;

namespace LiderCore;

public interface ILayout
{
    void Arrange(Screen screen);
    // window の端のうち、引いて大きさを変えられる物 (右なら 1、下なら 2 の和)
    int Edges(Screen screen, Window window);
    // window の幅を dx、高さを dy メートルだけ大きくする (負なら小さくする)。どの境目を動かすかは、規則が決める
    void Grow(Screen screen, Window window, float dx, float dy);
    // 規則への知らせ (規則ごとの設定を変える)。知らない知らせは何もせず false を返す
    bool Tell(string message);
}

// 知らせの名前
public static class LayoutMessage
{
    // 先頭に 1 枚ずつ並べるウィンドウを増やす、減らす
    public const string MoreMains = "mains+";
    public const string FewerMains = "mains-";
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

    // 自分の大きさを持つのは Code のウィンドウ
    public int Edges(Screen screen, Window window)
    {
        return window.Content is Code ? 3 : 0;
    }

    public void Grow(Screen screen, Window window, float dx, float dy)
    {
        if (!(window.Content is Code))
            return;
        var perPx = PanelView.BaseW / PanelView.BasePxW * window.Scale;
        window.Resize(window.WidthPx + (int)Math.Round(dx / perPx), window.HeightPx + (int)Math.Round(dy / perPx));
    }

    public bool Tell(string message)
    {
        return false;
    }

    static void Swap(System.Collections.Generic.List<Window> windows, int a, int b)
    {
        var w = windows[a];
        windows[a] = windows[b];
        windows[b] = w;
    }
}

// スクリーンを区画に割る規則が共通に使う: 区画の真ん中と大きさを、並びの順にウィンドウへ割り当てる。
// つかんでいるウィンドウ (Screen.Held) は動かさず、その真ん中がほかの区画に入ったら、その区画のウィンドウと並びを入れ替える
public static class Tiles
{
    public static void Assign(Screen screen, System.Collections.Generic.List<Vec2> centers, System.Collections.Generic.List<Vec2> sizes)
    {
        var windows = screen.Windows;
        var held = screen.Held;
        var at = held != null ? windows.IndexOf(held) : -1;
        if (held != null && held.Position != null && at >= 0)
        {
            var p = held.Position.Value;
            for (var i = 0; i < windows.Count; i++)
            {
                if (i == at || System.Math.Abs(p.X - centers[i].X) > sizes[i].X * 0.5f || System.Math.Abs(p.Y - centers[i].Y) > sizes[i].Y * 0.5f)
                    continue;
                windows[at] = windows[i];
                windows[i] = held;
                break;
            }
        }
        for (var i = 0; i < windows.Count; i++)
        {
            windows[i].Size = sizes[i];
            if (windows[i] != held || windows[i].Position == null)
                windows[i].Place(centers[i].X, centers[i].Y);
        }
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
        var centers = new System.Collections.Generic.List<Vec2>();
        var sizes = new System.Collections.Generic.List<Vec2>();
        for (var i = 0; i < n; i++)
        {
            centers.Add(Rows ? new Vec2(0, h * 0.5f - h * (i + 0.5f) / n) : new Vec2(-w * 0.5f + w * (i + 0.5f) / n, 0));
            sizes.Add(Rows ? new Vec2(w, h / n) : new Vec2(w / n, h));
        }
        Tiles.Assign(screen, centers, sizes);
    }

    public int Edges(Screen screen, Window window)
    {
        return 0;
    }

    public void Grow(Screen screen, Window window, float dx, float dy)
    {
    }

    public bool Tell(string message)
    {
        return false;
    }
}

// 先頭の Mains 枚を、左から 1 枚ずつの列に並べ、残りを右の端の列に上から積む。
// 列の幅と、積んだウィンドウの高さは、重み (Widths、Heights) の割合で分ける。重みを書いていない所は、書いてある重みの平均にする。
// 残りが無ければ、先頭のウィンドウだけでスクリーンを分ける
public class MainStack : ILayout
{
    public int Mains = 1;
    // 列の幅の重み (左から)、積んだウィンドウの高さの重み (上から)
    public System.Collections.Generic.List<float> Widths = new System.Collections.Generic.List<float>();
    public System.Collections.Generic.List<float> Heights = new System.Collections.Generic.List<float>();
    // 1 つの列、1 枚のウィンドウに残す割合の下限
    const float MinShare = 0.1f;

    // 1 枚ずつの列に並べる枚数と、列の数 (積む列も 1 つに数える)
    static int MainsOf(int mains, int n)
    {
        return System.Math.Max(0, System.Math.Min(mains, n));
    }

    static int ColumnsOf(int mains, int n)
    {
        return MainsOf(mains, n) + (n > MainsOf(mains, n) ? 1 : 0);
    }

    // 重み weights の先頭 n 個の割合。重みを書いていない所は、書いてある重みの平均 (1 つも無ければ等分) にする
    static System.Collections.Generic.List<float> Shares(System.Collections.Generic.List<float> weights, int n)
    {
        var known = 0.0f;
        var count = 0;
        for (var i = 0; i < n && i < weights.Count; i++)
            if (weights[i] > 0)
            {
                known += weights[i];
                count++;
            }
        var rest = count > 0 ? known / count : 1;
        var shares = new System.Collections.Generic.List<float>();
        var sum = 0.0f;
        for (var i = 0; i < n; i++)
        {
            shares.Add(i < weights.Count && weights[i] > 0 ? weights[i] : rest);
            sum += shares[i];
        }
        for (var i = 0; i < n; i++)
            shares[i] /= sum;
        return shares;
    }

    public void Arrange(Screen screen)
    {
        var n = screen.Windows.Count;
        var w = screen.Size.X;
        var h = screen.Size.Y;
        if (n == 0)
            return;
        var mains = MainsOf(Mains, n);
        var widths = Shares(Widths, ColumnsOf(Mains, n));
        var heights = Shares(Heights, n - mains);
        var centers = new System.Collections.Generic.List<Vec2>();
        var sizes = new System.Collections.Generic.List<Vec2>();
        var left = -w * 0.5f;
        for (var i = 0; i < mains; i++)
        {
            centers.Add(new Vec2(left + w * widths[i] * 0.5f, 0));
            sizes.Add(new Vec2(w * widths[i], h));
            left += w * widths[i];
        }
        var top = h * 0.5f;
        for (var i = mains; i < n; i++)
        {
            var share = heights[i - mains];
            centers.Add(new Vec2(left + w * widths[mains] * 0.5f, top - h * share * 0.5f));
            sizes.Add(new Vec2(w * widths[mains], h * share));
            top -= h * share;
        }
        Tiles.Assign(screen, centers, sizes);
    }

    // 右の端を引けるのは、右に列があるウィンドウ。下の端を引けるのは、下にウィンドウが積んであるウィンドウ
    public int Edges(Screen screen, Window window)
    {
        var n = screen.Windows.Count;
        var at = screen.Windows.IndexOf(window);
        if (at < 0 || window.Position == null)
            return 0;
        var mains = MainsOf(Mains, n);
        var column = System.Math.Min(at, mains);
        return (column + 1 < ColumnsOf(Mains, n) ? 1 : 0) + (at >= mains && at + 1 < n ? 2 : 0);
    }

    // 横は、window の列の右の境目を動かす (右の端の列は、左の境目を動かす)。縦は、積んだウィンドウの下の境目を動かす (一番下は、上の境目を動かす)
    public void Grow(Screen screen, Window window, float dx, float dy)
    {
        var n = screen.Windows.Count;
        var at = screen.Windows.IndexOf(window);
        if (at < 0 || screen.Size.X <= 0 || screen.Size.Y <= 0)
            return;
        var mains = MainsOf(Mains, n);
        var columns = ColumnsOf(Mains, n);
        var column = System.Math.Min(at, mains);
        if (dx != 0 && columns > 1)
            Widths = Moved(Shares(Widths, columns), column, dx / screen.Size.X);
        if (dy != 0 && at >= mains && n - mains > 1)
            Heights = Moved(Shares(Heights, n - mains), at - mains, dy / screen.Size.Y);
    }

    // at 番目を by (全体に対する割合) だけ大きくし、その分を隣 (次。最後なら前) から取った割合
    static System.Collections.Generic.List<float> Moved(System.Collections.Generic.List<float> shares, int at, float by)
    {
        var other = at + 1 < shares.Count ? at + 1 : at - 1;
        var both = shares[at] + shares[other];
        var grown = System.Math.Max(MinShare, System.Math.Min(both - MinShare, shares[at] + by));
        shares[at] = grown;
        shares[other] = both - grown;
        return shares;
    }

    public bool Tell(string message)
    {
        if (message == LayoutMessage.MoreMains)
            Mains++;
        else if (message == LayoutMessage.FewerMains)
            Mains = System.Math.Max(0, Mains - 1);
        else
            return false;
        return true;
    }
}

// スクリーンの端から、ウィンドウを縦に積む。大きさは、ウィンドウが持っている物のまま。
// 横は AlignX (-1 左の端、0 真ん中、1 右の端) に寄せ、縦は FromTop なら上の端から下へ、そうでなければ下の端から上へ積む
public class Stack : ILayout
{
    public float AlignX = 1;
    public bool FromTop;
    // 端とウィンドウの間、ウィンドウどうしの間
    public float Margin = 0.03f;

    public void Arrange(Screen screen)
    {
        var edge = screen.Size.Y * 0.5f - Margin;
        foreach (var window in screen.Windows)
        {
            var x = AlignX * (screen.Size.X * 0.5f - Margin - window.Size.X * 0.5f);
            window.Place(x, FromTop ? edge - window.Size.Y * 0.5f : -edge + window.Size.Y * 0.5f);
            edge -= window.Size.Y + Margin;
        }
    }
    public int Edges(Screen screen, Window window)
    {
        return 0;
    }

    public void Grow(Screen screen, Window window, float dx, float dy)
    {
    }

    public bool Tell(string message)
    {
        return false;
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
    public int Edges(Screen screen, Window window)
    {
        return 0;
    }

    public void Grow(Screen screen, Window window, float dx, float dy)
    {
    }

    public bool Tell(string message)
    {
        return false;
    }
}
