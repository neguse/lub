namespace TinyCs.Tcs2c.Tests;

// game source が要求した言語面 (user-defined operator など) の differential。
public partial class DifferentialTests
{
    // user-defined operator: IL は素の IlBin / IlUn。C は operand の静的型で
    // overload を選ぶ (Lua は metamethod が実行時型で分岐)。同じ operator の
    // overload (Vec*Vec / Vec*float / float*Vec)、戻り型違い (dot)、単項 -、
    // 複合代入、int operand の float 昇格、左→右の評価順
    [CFact]
    public void UserOperators_Overloads()
    {
        Backends.AssertParity("""
            using System;
            public class Vec
            {
                public float X, Y;
                public Vec(float x, float y) { X = x; Y = y; }
                public static Vec operator +(Vec a, Vec b) => new Vec(a.X + b.X, a.Y + b.Y);
                public static Vec operator -(Vec a, Vec b) => new Vec(a.X - b.X, a.Y - b.Y);
                public static Vec operator -(Vec a) => new Vec(-a.X, -a.Y);
                public static float operator *(Vec a, Vec b) => a.X * b.X + a.Y * b.Y;
                public static Vec operator *(Vec a, float n) => new Vec(a.X * n, a.Y * n);
                public static Vec operator *(float n, Vec a) => new Vec(a.X * n + 1, a.Y * n + 1);
                public static Vec operator /(Vec a, float n) { return new Vec(a.X / n, a.Y / n); }
                public static Vec operator %(Vec a, float n) => new Vec(a.X % n, a.Y % n);
            }
            public class P
            {
                static Vec Log(string tag, Vec v) { Console.WriteLine(tag); return v; }
                static string S(Vec v) => v.X + "," + v.Y;
                public static void Main()
                {
                    var a = new Vec(1.5f, 2);
                    var b = new Vec(4, -3);
                    Console.WriteLine(S(a + b) + ":" + S(a - b) + ":" + S(-a) + ":" + (a * b));
                    Console.WriteLine(S(a * 2f) + ":" + S(2f * a) + ":" + S(a * 3) + ":" + S(b / 2) + ":" + S(b % 2.5f));
                    var c = a;
                    c += b;
                    c *= 0.5f;
                    c -= a * (a * b);
                    Console.WriteLine(S(c) + ":" + S(a));
                    Console.WriteLine(S(Log("l", a) + Log("r", b) * 2f - -b));
                    float d = (a + b) * (a - b) + 1;
                    Console.WriteLine(d);
                }
            }
            """, "P");
    }

    // 基底 class の operator を派生 class の値で呼ぶ (#23)。IL は operator の
    // static 関数への IlCall なので、両 backend とも派生 → 基底の upcast 引数で
    // そのまま通る。int を返す `/` は整数除算に化けない
    [CFact]
    public void UserOperators_BaseClassWithDerivedOperands()
    {
        Backends.AssertParity("""
            using System;
            public class V
            {
                public int X;
                public V(int x) { X = x; }
                public static V operator +(V a, V b) => new V(a.X + b.X);
                public static V operator -(V a) => new V(-a.X);
                public static V operator *(V a, V b) => new V(a.X * b.X);
                public static V operator *(V a, int s) => new V(a.X * s * 10);
                public static int operator /(V a, V b) => a.X / b.X;
            }
            public class D : V { public D(int x) : base(x) { } }
            public class P
            {
                public static void Main()
                {
                    D a = new D(7);
                    D b = new D(2);
                    V sum = a + b;
                    V neg = -a;
                    V vv = a * b;
                    V vs = a * 3;
                    V acc = new D(1);
                    acc += b;
                    acc *= 2;
                    Console.WriteLine(sum.X + ":" + neg.X + ":" + vv.X + ":" + vs.X + ":" + acc.X + ":" + (a / b));
                }
            }
            """, "P");
    }

    // 三項の片腕が null で結果が T? (`cond ? x : null`): 両腕を結果型へ揃える
    [CFact]
    public void Ternary_NullableWithNullArm()
    {
        Backends.AssertParity("""
            using System;
            public class Ver { public int Version = 7; public float Scale = 1.5f; }
            public class P
            {
                static string Show(int? v) => v.HasValue ? "v" + v.Value : "none";
                static string ShowF(float? v) => v == null ? "none" : "f" + v.Value;
                public static void Main()
                {
                    var ver = new Ver();
                    for (int i = 0; i < 2; i++)
                    {
                        bool on = i == 1;
                        Console.WriteLine(Show(on ? ver.Version : null) + ":" + Show(on ? null : 3)
                            + ":" + ShowF(on ? ver.Scale : null) + ":" + ShowF(on ? null : ver.Version));
                        int? local = on ? null : ver.Version + 1;
                        Console.WriteLine(Show(local));
                    }
                }
            }
            """, "P");
    }

    // 3 段の override を中間 class 型の receiver から呼ぶ (dispatcher は最上位の
    // 宣言 class が持つ)
    [CFact]
    public void VirtualDispatch_MiddleClassReceiver()
    {
        Backends.AssertParity("""
            using System;
            public class A { public virtual string Name(int n) => "A" + n; public virtual int Tag => 1; }
            public class B : A { public override string Name(int n) => "B" + n; public override int Tag => 2; }
            public class C : B { public override string Name(int n) => "C" + n; public override int Tag => 3; }
            public class Holder<T> where T : B { public T Item; public string Call() => Item.Name(5) + Item.Tag; }
            public class P
            {
                public static void Main()
                {
                    B b = new C();
                    B plain = new B();
                    A a = b;
                    Console.WriteLine(b.Name(1) + plain.Name(2) + a.Name(3) + b.Tag + plain.Tag + a.Tag);
                    var h = new Holder<B> { Item = new C() };
                    var hc = new Holder<C> { Item = new C() };
                    Console.WriteLine(h.Call() + hc.Call());
                }
            }
            """, "P");
    }

    // static field initializer の closure (lifted 関数は初期化関数の外に出す)
    [CFact]
    public void StaticFieldInitializer_Closures()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public static class Table
            {
                public static int Base = 10;
                public static Func<int, int> Twice = x => x * 2 + Base;
                public static Func<int, int>[] Ops = new Func<int, int>[] { x => x + 1, x => x * x };
                public static List<Action> Log = new List<Action> { () => Console.WriteLine("log") };
                static int Helper(int x) => x - 1;
                public static Func<int, int> Group = Helper;
            }
            public class P
            {
                public static void Main()
                {
                    Console.WriteLine(Table.Twice(4) + ":" + Table.Ops[0](4) + ":" + Table.Ops[1](4) + ":" + Table.Group(4));
                    Table.Log[0]();
                }
            }
            """, "P");
    }

    // ctor 本体の closure が this / ctor 引数 / ctor local を捕捉する
    [CFact]
    public void Constructor_ClosureCaptures()
    {
        Backends.AssertParity("""
            using System;
            public class Base
            {
                public Func<int> Get;
                public Base(Func<int> get) { Get = get; }
            }
            public class Stage : Base
            {
                public int Count = 3;
                public Action<int>[] Funcs;
                public Func<int, int> Scale;
                public Stage(int factor) : base(() => factor * 100)
                {
                    int local = factor + 1;
                    Funcs = new Action<int>[2];
                    Funcs[0] = (int n) => { Add(n); };
                    Funcs[1] = (int n) => { Count += n * factor + local; local++; };
                    Scale = x => x * factor + Count;
                    factor += 10;
                }
                void Add(int n) { Count += n; }
            }
            public class P
            {
                public static void Main()
                {
                    var s = new Stage(2);
                    s.Funcs[0](5);
                    s.Funcs[1](1);
                    s.Funcs[1](1);
                    Console.WriteLine(s.Count + ":" + s.Scale(3) + ":" + s.Get());
                }
            }
            """, "P");
    }

    // 非 nullable の bool を null と比べる生成コード由来の式
    // (`(!(x != null)) != null` は C# では常に true)
    [CFact]
    public void BoolComparedWithNull()
    {
        Backends.AssertParity("""
            using System;
            public class Item { public int V = 1; }
            public class P
            {
                static string Check(Item e)
                {
            #pragma warning disable CS0472
                    if ((!((e) != null)) != null) return "always";
                    return "never";
            #pragma warning restore CS0472
                }
                public static void Main()
                {
                    bool flag = false;
                    int n = 3;
            #pragma warning disable CS0472
                    Console.WriteLine(Check(null) + ":" + Check(new Item()) + ":" + (flag == null) + ":" + (n != null) + ":" + ((n > 2) == null));
                    int? maybe = n > 2 ? 4 : null;
                    Console.WriteLine(((n) + maybe) + ":" + ((n) < maybe) + ":" + (maybe == (n)));
            #pragma warning restore CS0472
                }
            }
            """, "P");
    }

    // 派生 class が基底の virtual と同名・別シグネチャの method を持つ (C# では
    // override ではなく別 method)。dispatcher はシグネチャが一致する実装だけを
    // override として扱う
    [CFact]
    public void Dispatch_SameNameDifferentSignature()
    {
        Backends.AssertParity("""
            using System;
            public class Pool
            {
                public int Total;
                public virtual void Update() { Total += 1; }
            }
            public class FastPool : Pool
            {
                public override void Update() { Total += 10; }
            }
            public class TurretPool : Pool
            {
                public void Update(int id, float scale) { Total += (int)(id * scale); }
            }
            public class P
            {
                public static void Main()
                {
                    Pool plain = new Pool();
                    Pool fast = new FastPool();
                    var turrets = new TurretPool();
                    plain.Update();
                    fast.Update();
                    turrets.Update(4, 2.5f);
                    Console.WriteLine(plain.Total + ":" + fast.Total + ":" + turrets.Total);
                }
            }
            """, "P");
    }

    // for の制御変数が float で初期値が整数 literal (`for (float y = -1; ...)`)
    [CFact]
    public void ForLoop_FloatVariableWithIntInit()
    {
        Backends.AssertParity("""
            using System;
            public class P
            {
                public static void Main()
                {
                    float sum = 0;
                    for (float y = -1; y < 1.1f; y += 0.5f) sum += y;
                    for (float x = 0, w = 2; x < w; x += 0.75f) sum += x * w;
                    Console.WriteLine(sum);
                }
            }
            """, "P");
    }

    [CFact]
    public void JaggedArray_SizedOuterDimension()
    {
        Backends.AssertParity("""
            using System;
            public class Cell { public int V; }
            public class P
            {
                public static void Main()
                {
                    int[][] grid = new int[3][];
                    Cell[][] cells = new Cell[2][];
                    for (int i = 0; i < 3; i++)
                    {
                        grid[i] = new int[4];
                        grid[i][3] = i * 10;
                    }
                    cells[1] = new Cell[2];
                    cells[1][0] = new Cell { V = 7 };
                    Console.WriteLine(grid[2][3] + ":" + grid[0][0] + ":" + cells[1][0].V + ":" + (cells[0] == null));
                }
            }
            """, "P");
    }

    [Fact]
    public void CompileErrors_AreReported()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Backends.EmitC([
            "public class P { public static Action hook; public static void Main() { if (hook != null) hook(); } }",
        ], "P"));
        Assert.Contains("error CS0246", error.Message);
    }
    private const string SameNamedInterfaces = """
        using System;
        public interface IValue { int Read(); }
        namespace A { public interface IValue { int Read(); } }
        public class C : A.IValue { public int Read() => 7; }
        public class G : IValue { public int Read() => 3; }
        """;

    [CFact]
    public void Namespaces_SameNamedInterfacesStayDistinct()
    {
        Backends.AssertParity(SameNamedInterfaces + """
            public class P
            {
                public static void Main()
                {
                    object c = new C();
                    object g = new G();
                    Console.WriteLine(((A.IValue)c).Read() + ((IValue)g).Read());
                }
            }
            """, "P");
    }

    [CFact]
    public void Namespaces_CastToSameNamedGlobalInterfaceFaults()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Backends.RunC([
            SameNamedInterfaces + """
            public class P
            {
                public static void Main()
                {
                    object o = new C();
                    Console.WriteLine(((IValue)o).Read());
                }
            }
            """,
        ], "P"));
        Assert.Contains("invalid-cast", error.Message);
    }
}
