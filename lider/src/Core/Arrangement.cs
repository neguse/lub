// 配置: どのスクリーンがどこにどの規則で在り、どのウィンドウがどのスクリーンにどの順で居るか。
// 名前を付けて控え、戻せる。重ねる形も左右に並ぶ形も、配置の 1 つ。
// ウィンドウは key で見分ける: コードは "code:" と開いている file (Root からの path を "|" で繋ぐ)、ゲームの絵は "game:saved" と "game:draft"、インスペクタは "inspector"。
// "code:*" は、ほかの行に無いコードのウィンドウ全部を指し、戻した後に開くコードのウィンドウも、それのあるスクリーンに置く。
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public class ScreenSpec
{
    public string Name = "";
    public ScreenShape Shape = ScreenShape.Plane;
    public ScreenAnchor Anchor = ScreenAnchor.View;
    public Vec3 Position = new Vec3(0, 0, 1);
    public Vec2 Size = new Vec2(1, 1);
    public float Opacity = 1;
    // 規則 (Layouts.Describe の書き方)
    public string Layout = "Full";
    public List<string> Windows = new List<string>();
}

public class Arrangement
{
    public const string AllCode = "code:*";

    public string Name = "";
    public List<ScreenSpec> Screens = new List<ScreenSpec>();

    // 今の配置を控える
    public static Arrangement Capture(string name, Workspace w)
    {
        var a = new Arrangement { Name = name };
        var home = w.Home ?? w.CodeScreen;
        foreach (var screen in w.Screens)
        {
            // 端から積む規則のスクリーン (案内、検索) は、決まった物を出す所なので、控えない
            if (screen.Layout is Stack)
                continue;
            var spec = new ScreenSpec
            {
                Name = screen.Name,
                Shape = screen.Shape,
                Anchor = screen.Anchor,
                Position = screen.Position,
                Size = screen.Size,
                Opacity = screen.Opacity,
                Layout = Layouts.Describe(screen.Layout),
            };
            foreach (var window in screen.Windows)
            {
                var key = KeyOf(w, window);
                if (key != "")
                    spec.Windows.Add(key);
            }
            if (screen == home)
                spec.Windows.Add(AllCode);
            a.Screens.Add(spec);
        }
        return a;
    }

    // 配置に戻す。名前の合うスクリーンの形と規則を変え、ウィンドウを控えた順に載せ直す。
    // 配置に無いウィンドウは、今のスクリーンに居るまま、控えた物の後ろに並ぶ
    public void Apply(Workspace w)
    {
        front.Clear();
        var all = new List<Window>();
        foreach (var screen in w.Screens)
            if (!(screen.Layout is Stack))
                foreach (var window in screen.Windows)
                    all.Add(window);
        var placed = new List<Window>();
        Screen? home = null;
        // "code:*" のすぐ後に並べたウィンドウ (新しく開くコードの窓は、この前に置く)
        Window? after = null;
        var afterAll = false;
        // key を名指しで控えた行の無いコードのウィンドウは、"code:*" の所に並べる
        var named = new List<string>();
        foreach (var spec in Screens)
            foreach (var key in spec.Windows)
                named.Add(key);
        foreach (var spec in Screens)
        {
            var screen = ScreenNamed(w, spec.Name);
            if (screen == null)
                continue;
            screen.Shape = spec.Shape;
            screen.Anchor = spec.Anchor;
            screen.Position = spec.Position;
            screen.Size = spec.Size;
            screen.Opacity = spec.Opacity;
            screen.Layout = Layouts.Parse(spec.Layout);
            var order = new List<Window>();
            foreach (var key in spec.Windows)
            {
                if (key == AllCode)
                {
                    home = screen;
                    afterAll = true;
                    foreach (var window in all)
                        if (!placed.Contains(window) && window.Content is Code && !named.Contains(KeyOf(w, window)))
                        {
                            order.Add(window);
                            placed.Add(window);
                        }
                    continue;
                }
                foreach (var window in all)
                    if (!placed.Contains(window) && KeyOf(w, window) == key)
                    {
                        order.Add(window);
                        placed.Add(window);
                        if (afterAll && after == null)
                            after = window;
                        break;
                    }
            }
            afterAll = false;
            Put(w, screen, order);
        }
        w.Home = home == w.CodeScreen ? null : home;
        w.HomeBefore = after;
    }

    // windows を、この順に screen の並びへ載せる (ほかのスクリーンに居た物は移す)。
    // 前に載せた物の後ろに続け、配置に無いウィンドウはその後ろに残る
    void Put(Workspace w, Screen screen, List<Window> windows)
    {
        var at = front.TryGetValue(screen, out var n) ? n : 0;
        foreach (var window in windows)
        {
            if (w.ScreenOf(window) != screen)
                w.MoveTo(window, screen);
            screen.Windows.Remove(window);
            screen.Windows.Insert(Math.Min(at, screen.Windows.Count), window);
            at++;
        }
        front[screen] = at;
    }

    // Apply の間、スクリーンごとに、並びの前から何枚を載せたか
    readonly Dictionary<Screen, int> front = new Dictionary<Screen, int>();

    static Screen? ScreenNamed(Workspace w, string name)
    {
        foreach (var screen in w.Screens)
            if (screen.Name == name)
                return screen;
        return null;
    }

    // ウィンドウを見分ける key (配置で見分けない中身は "")
    public static string KeyOf(Workspace w, Window window)
    {
        if (window.Content is Code code)
        {
            var paths = new List<string>();
            foreach (var doc in code.Tabs)
                paths.Add(w.Relative(doc.Path));
            return "code:" + string.Join("|", paths);
        }
        if (window.Content is GameView view)
            return view.Version == GameVersion.Saved ? "game:saved" : "game:draft";
        if (window.Content is Inspector)
            return "inspector";
        return "";
    }

    // file に書く行: "arrangement 名前"、スクリーンごとに "screen 名前 形 固定先 x y z 幅 高さ 透過度 規則"、ウィンドウごとに "window key"
    public List<string> Lines()
    {
        var lines = new List<string> { "arrangement " + Name };
        foreach (var s in Screens)
        {
            lines.Add("screen " + s.Name + " " + (s.Shape == ScreenShape.Plane ? "plane" : "cylinder") + " " + (s.Anchor == ScreenAnchor.View ? "view" : "world")
                + " " + s.Position.X + " " + s.Position.Y + " " + s.Position.Z + " " + s.Size.X + " " + s.Size.Y + " " + s.Opacity + " " + s.Layout);
            foreach (var key in s.Windows)
                lines.Add("window " + key);
        }
        return lines;
    }

    // Lines の書き方の行の並びから、配置を読む (読めない行は飛ばす)
    public static List<Arrangement> Read(string text)
    {
        var all = new List<Arrangement>();
        Arrangement? a = null;
        ScreenSpec? spec = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("arrangement "))
            {
                a = new Arrangement { Name = Utf8.Sub(line, 12) };
                spec = null;
                all.Add(a);
            }
            else if (line.StartsWith("screen ") && a != null)
            {
                var f = line.Split(' ');
                if (f.Length < 11)
                    continue;
                spec = new ScreenSpec
                {
                    Name = f[1],
                    Shape = f[2] == "plane" ? ScreenShape.Plane : ScreenShape.Cylinder,
                    Anchor = f[3] == "view" ? ScreenAnchor.View : ScreenAnchor.World,
                    Position = new Vec3(Number(f[4]), Number(f[5]), Number(f[6])),
                    Size = new Vec2(Number(f[7]), Number(f[8])),
                    Opacity = Number(f[9]),
                };
                var layout = f[10];
                for (var i = 11; i < f.Length; i++)
                    layout += " " + f[i];
                spec.Layout = layout;
                a.Screens.Add(spec);
            }
            else if (line.StartsWith("window ") && spec != null)
                spec.Windows.Add(Utf8.Sub(line, 7));
        }
        return all;
    }

    static float Number(string s)
    {
        return float.TryParse(s, out var v) ? v : 0;
    }
}

// 規則と、その書き方 ("MainStack 0.5" のように、名前と値)
public static class Layouts
{
    public static string Describe(ILayout layout)
    {
        if (layout is Strip)
            return "Strip";
        if (layout is Even even)
            return even.Rows ? "Even rows" : "Even";
        if (layout is MainStack main)
            return "MainStack " + main.Ratio;
        if (layout is Stack stack)
            return "Stack " + stack.AlignX + " " + (stack.FromTop ? "top" : "bottom") + " " + stack.Margin;
        return "Full";
    }

    public static ILayout Parse(string text)
    {
        var f = text.Split(' ');
        if (f[0] == "Strip")
            return new Strip();
        if (f[0] == "Even")
            return new Even { Rows = f.Length > 1 && f[1] == "rows" };
        if (f[0] == "MainStack")
            return new MainStack { Ratio = f.Length > 1 && float.TryParse(f[1], out var ratio) ? ratio : 0.5f };
        if (f[0] == "Stack" && f.Length > 3)
            return new Stack { AlignX = float.TryParse(f[1], out var x) ? x : 1, FromTop = f[2] == "top", Margin = float.TryParse(f[3], out var m) ? m : 0.03f };
        return new Full();
    }
}
