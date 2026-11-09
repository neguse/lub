namespace TinyCs.Tcs2c.Tests;

// delegate (Action / Func) への `?.Invoke(...)` は null 判定つきの呼び出し。
// 戻り値の無い文、`T?` に wrap する戻り値 (struct も)、`??` の式、field /
// auto property / static field / `?.` の連鎖、明示の `f.Invoke(...)` を見る。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void ConditionalInvoke_Delegate()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct V { public int X; }
            public class C { public Action A; public Func<int, int> F; public Func<int> Prop { get; set; } }
            public class P
            {
                static Action sa;
                public static void Main()
                {
                    Action a = () => Console.WriteLine("a");
                    Action n = null;
                    a?.Invoke();
                    n?.Invoke();
                    a.Invoke();
                    Func<int> f = () => 5;
                    Func<int> fn = null;
                    int? r1 = f?.Invoke();
                    int? r2 = fn?.Invoke();
                    Console.WriteLine("r:" + (r1 ?? -1) + ":" + (r2 ?? -1));
                    Console.WriteLine("q:" + (f?.Invoke() ?? -1) + ":" + (fn?.Invoke() ?? -1));
                    Func<string, string> g = s => s + "!";
                    Func<string, string> gn = null;
                    Console.WriteLine("s:" + (g?.Invoke("x") ?? "none") + ":" + (gn?.Invoke("x") ?? "none"));
                    var c = new C();
                    c.A?.Invoke();
                    c.A = () => Console.WriteLine("ca");
                    c.A?.Invoke();
                    c.F = x => x * 2;
                    Console.WriteLine("cf:" + (c.F?.Invoke(4) ?? -1));
                    C cn = null;
                    cn?.A?.Invoke();
                    Console.WriteLine("chain:" + (cn?.F?.Invoke(1) ?? -1) + ":" + (c?.F?.Invoke(3) ?? -1));
                    sa?.Invoke();
                    sa = () => Console.WriteLine("sa");
                    sa?.Invoke();
                    Func<int, int> k = x => x + 1;
                    c.Prop?.Invoke();
                    c.Prop = () => 7;
                    Console.WriteLine("prop:" + (c.Prop?.Invoke() ?? -1) + ":" + c.Prop.Invoke());
                    Func<V> mv = () => new V { X = 3 };
                    Func<V> mn = null;
                    V? v1 = mv?.Invoke();
                    V? v2 = mn?.Invoke();
                    Console.WriteLine("struct:" + B(v1.HasValue) + ":" + v1.Value.X + ":" + B(v2.HasValue) + ":" + (mv?.Invoke().X ?? -1));
                    Action<int> log = x => Console.WriteLine("log:" + x);
                    log?.Invoke(1);
                    log.Invoke(2);
                    Console.WriteLine("cnt:" + Call(k) + ":" + Call(null));
                }
                static int Call(Func<int, int> h) => h?.Invoke(10) ?? 0;
                static int B(bool b) => b ? 1 : 0;
            }
            """, "P");
        Assert.Equal("""
            a
            a
            r:5:-1
            q:5:-1
            s:x!:none
            ca
            cf:8
            chain:-1:6
            sa
            prop:7:7
            struct:1:3:0:3
            log:1
            log:2
            cnt:11:0
            """.ReplaceLineEndings("\n"), output);
    }
}
