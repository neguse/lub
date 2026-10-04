namespace TinyCs.Tcs2c.Tests;

// 言語機能別の 2 backend differential (続き): record / Nullable / Random /
// struct member / シフト / char。DifferentialTests のファイル長上限のため分割
public partial class DifferentialTests
{
    [CFact]
    public void Records_EqualityWithInheritanceAndDeconstruction()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public record Pt(int X, int Y)
            {
                public int Manhattan() => (X < 0 ? -X : X) + (Y < 0 ? -Y : Y);
            }
            public record Named(string Name, Pt Pos);
            public record Shape(string Kind);
            public record Circle(string Kind, float R) : Shape(Kind);
            public class P
            {
                public static void Main()
                {
                    var a = new Pt(1, 2);
                    var b = new Pt(1, 2);
                    var c = new Pt(2, 1);
                    Pt none = null;
                    Console.WriteLine((a == b) + ":" + (a != b) + ":" + (a == c) + ":" + (a == none) + ":" + (none == null));
                    var d = a with { X = 5 };
                    Console.WriteLine(d.X + "," + d.Y + ":" + a.X + ":" + (d == a) + ":" + d.Manhattan());
                    var n1 = new Named("p", new Pt(3, 4));
                    var n2 = new Named("p", new Pt(3, 4));
                    var n3 = n2 with { Pos = new Pt(0, 0) };
                    Console.WriteLine((n1 == n2) + ":" + (n1 == n3) + ":" + n3.Pos.X + ":" + n1.Pos.Manhattan());
                    Shape s1 = new Circle("c", 1.5f);
                    Shape s2 = new Circle("c", 1.5f);
                    Shape s3 = new Shape("c");
                    var s4 = s1 with { Kind = "k" };
                    Console.WriteLine((s1 == s2) + ":" + (s1 == s3) + ":" + (s4 is Circle) + ":" + s4.Kind + ":" + ((Circle)s4).R);
                    var (px, py) = c;
                    Console.WriteLine(px + ":" + py);
                    var pts = new List<Pt> { a, b, c, d };
                    Console.WriteLine(pts.Count(p => p == a) + ":" + pts.Contains(new Pt(2, 1)) + ":" + pts.IndexOf(new Pt(5, 2)));
                    var byName = new Dictionary<string, Pt> { ["a"] = a };
                    Console.WriteLine(byName["a"] == b);
                    Console.WriteLine(pts.Sum(p => p.X));
                }
            }
            """, "P");
    }

    [CFact]
    public void Nullable_LiftedOperatorsAndMembers()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct Vec { public int X; public int Y; }
            public class Slot { public int? Hp; public float? Speed; public bool? Flag; public Vec? Pos; public static int? Counter; }
            public class P
            {
                public static int Calls;
                public static int Side() { Calls++; return 7; }
                public static string Show(int? v) => v.HasValue ? "v=" + v.Value.ToString() : "none";
                public static int? Pick(bool take) => take ? 5 : null;
                public static void Main()
                {
                    int? a = 10;
                    int? n = null;
                    int b = 3;
                    Console.WriteLine(Show(a) + "|" + Show(n) + "|" + Show(Pick(true)) + "|" + Show(Pick(false)));
                    Console.WriteLine((a == null) + ":" + (n == null) + ":" + (a != null) + ":" + (n != null) + ":" + (n is null) + ":" + (a is not null));
                    Console.WriteLine((a ?? 0) + ":" + (n ?? -1) + ":" + n.GetValueOrDefault() + ":" + n.GetValueOrDefault(9) + ":" + a.GetValueOrDefault(9));
                    int? s1 = a + b;
                    int? s2 = n + b;
                    int? s3 = a * a - b;
                    int? s4 = a / 3;
                    int? s5 = a % 3;
                    int? s6 = -a;
                    int? s7 = n * 2;
                    Console.WriteLine(Show(s1) + "|" + Show(s2) + "|" + Show(s3) + "|" + Show(s4) + "|" + Show(s5) + "|" + Show(s6) + "|" + Show(s7));
                    Console.WriteLine((a == 10) + ":" + (n == 10) + ":" + (a != n) + ":" + (n == n) + ":" + (a < 20) + ":" + (n < 20) + ":" + (a >= 10) + ":" + (n >= 0) + ":" + (a > n));
                    float? f = 1.5f;
                    float? g = null;
                    Console.WriteLine((f * 2f) + "|" + (g * 2f) + "|" + (f + a) + "|" + (f / 4f) + "|" + (f % 1f) + "|" + (f == 1.5f) + "|" + (g == null));
                    bool? t = true;
                    bool? u = false;
                    bool? v = null;
                    Console.WriteLine((t & v) + "|" + (u & v) + "|" + (t | v) + "|" + (u | v) + "|" + (v & v) + "|" + (!v) + "|" + (!t) + "|" + (t ^ u) + "|" + (t ^ v));
                    int? c = 1;
                    c++;
                    c += 5;
                    c = c << 2;
                    c--;
                    n++;
                    n += 1;
                    Console.WriteLine(Show(c) + "|" + Show(n) + "|" + Show(c & 6) + "|" + Show(c | 1) + "|" + Show(~c) + "|" + Show(c ^ 3));
                    var sl = new Slot();
                    Console.WriteLine(Show(sl.Hp) + "|" + sl.Speed.HasValue + "|" + (sl.Flag ?? true) + "|" + sl.Pos.HasValue);
                    sl.Hp = 4;
                    var pos = new Vec(); pos.X = 2; pos.Y = 3;
                    sl.Pos = pos;
                    Slot.Counter = sl.Hp * 2;
                    Console.WriteLine(Show(sl.Hp) + "|" + sl.Pos.Value.X + "," + sl.Pos.Value.Y + "|" + Show(Slot.Counter) + "|" + (sl.Pos == null));
                    sl.Hp = null;
                    Console.WriteLine(Show(sl.Hp) + "|" + $"[{sl.Hp}][{a}][{f}][{g}][{t}][{v}]" + "|" + ("x" + n + a));
                    Calls = 0;
                    var w1 = a ?? Side();
                    var w2 = n ?? Side();
                    var w3 = a.GetValueOrDefault(Side());
                    Console.WriteLine(w1 + ":" + w2 + ":" + w3 + ":" + Calls);
                    var list = new List<int?> { 1, 3 };
                    list.Add(Pick(true));
                    var sum = 0;
                    foreach (var item in list) sum += item ?? 100;
                    Console.WriteLine(sum + ":" + list.Count + ":" + (list[1] == 3));
                    var dict = new Dictionary<string, int?>();
                    dict["b"] = 2;
                    Console.WriteLine(Show(dict["b"]) + ":" + (dict["b"] ?? 0));
                    int? sw = 2;
                    var label = sw switch { 1 => "one", 2 => "two", null => "nil", _ => "other" };
                    Console.WriteLine(label);
                    Console.WriteLine((a ?? 0) + (s1 ?? 0));
                }
            }
            """, "P");
    }

    [CFact]
    public void Random_SeededSequencesMatchLua()
    {
        // Lua 5.5 の xoshiro256** (LUA_32BITS 構成) を C に移植した合意 PRNG。
        // seed 固定で Next / Next(max) / Next(min, max) / Range / NextFloat が
        // bit 一致する
        Backends.AssertParity("""
            using TinySystem;
            public class P
            {
                public static void Main()
                {
                    Random.Seed(12345);
                    var s = "";
                    for (int i = 0; i < 6; i++) s += Random.Shared.Next(100) + ",";
                    System.Console.WriteLine(s);
                    s = "";
                    for (int i = 0; i < 6; i++) s += Random.Shared.Range(-5, 5) + ",";
                    System.Console.WriteLine(s);
                    s = "";
                    for (int i = 0; i < 4; i++) s += Random.Shared.NextFloat() + ",";
                    System.Console.WriteLine(s);
                    s = "";
                    for (int i = 0; i < 4; i++) s += Random.Shared.Next(1000, 2000) + "," + Random.Shared.Next() % 1000 + ";";
                    System.Console.WriteLine(s);
                    Random.Seed(-7);
                    System.Console.WriteLine(Random.Shared.Next(1 << 30) + ":" + Random.Shared.Next(3) + ":" + Random.Shared.Range(0, 0) + ":" + Random.Shared.Next(2147483647));
                    Random.Seed(12345);
                    System.Console.WriteLine(Random.Shared.Next(100));
                }
            }
            """, "P");
    }

    // struct / record struct の member (il-spec §10): 明示 ctor + field
    // initializer (`new S()` は zero のまま)、変数 receiver (local / 配列要素
    // / class field) の method 変異はその場に残り、List indexer (rvalue) は
    // copy への変異、custom / auto / expression-bodied property、record
    // struct の positional ctor / ==/!= / with / 入れ子、List.Contains /
    // IndexOf の値等価、`new S[n]` の zero 要素、default(S)
    [CFact]
    public void Structs_MembersCtorPropertiesAndRecordStruct()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct Vec
            {
                public float X;
                public float Y;
                public Vec(float x, float y) { X = x; Y = y; }
                public void Scale(float k) { X = X * k; Y = Y * k; }
                public float Len2() => X * X + Y * Y;
                public Vec Plus(Vec o) { var r = new Vec(X + o.X, Y + o.Y); return r; }
            }
            public struct Counter
            {
                public int N;
                public int Step = 2;
                public string Tag { get; set; }
                public int Doubled => N * 2;
                public int Clamped
                {
                    get { return N > 10 ? 10 : N; }
                    set { N = value < 0 ? 0 : value; }
                }
                public Counter(int n) { N = n; Tag = "c" + n.ToString(); }
                public void Inc() { N = N + Step; }
                public void Reset() { if (N == 0) return; N = 0; }
            }
            public struct Body
            {
                public Vec Pos;
                public Vec Vel;
                public void Step() { Pos = Pos.Plus(Vel); Vel.Scale(0.5f); }
            }
            public record struct Point(int X, int Y)
            {
                public int Manhattan() => (X < 0 ? -X : X) + (Y < 0 ? -Y : Y);
            }
            public readonly record struct Pair(Point A, string Name);
            public class Holder { public Counter C; public Vec V = new Vec(1f, 2f); }
            public class P
            {
                public static Counter Make(int n) { var c = new Counter(n); c.Inc(); return c; }
                public static void Main()
                {
                    var v = new Vec(3f, 4f);
                    v.Scale(2f);
                    Console.WriteLine(v.X + ":" + v.Y + ":" + v.Len2());
                    var arr = new Vec[2];
                    arr[1] = v;
                    arr[1].Scale(0.5f);
                    Console.WriteLine(arr[1].X + ":" + arr[0].X + ":" + v.X);
                    var list = new List<Vec>();
                    list.Add(v);
                    list[0].Scale(10f);
                    Console.WriteLine(list[0].X);
                    var c = new Counter(5);
                    c.Inc();
                    Console.WriteLine(c.N + ":" + c.Step + ":" + c.Tag + ":" + c.Doubled + ":" + c.Clamped);
                    c.Clamped = -3;
                    Console.WriteLine(c.N + ":" + c.Clamped);
                    c.N = 50; Console.WriteLine(c.Clamped);
                    var z = new Counter();
                    Console.WriteLine(z.N + ":" + z.Step + ":" + (z.Tag == null));
                    z.Reset(); z.Inc(); Console.WriteLine(z.N);
                    var h = new Holder();
                    h.C.Inc(); h.C.Inc();
                    h.V.Scale(3f);
                    Console.WriteLine(h.C.N + ":" + h.V.X + ":" + h.V.Y);
                    var m = Make(1);
                    Console.WriteLine(m.N + ":" + m.Tag);
                    var b = new Body();
                    b.Vel = new Vec(2f, 2f);
                    b.Step(); b.Step();
                    Console.WriteLine(b.Pos.X + ":" + b.Vel.X);
                    var p = new Point(3, -4);
                    var q = p with { Y = 4 };
                    Console.WriteLine(p.Manhattan() + ":" + q.Manhattan() + ":" + (p == q) + ":" + (p == new Point(3, -4)) + ":" + (p != q));
                    var pair = new Pair(p, "a");
                    var pair2 = pair with { Name = "a" };
                    Console.WriteLine((pair == pair2) + ":" + (pair == new Pair(q, "a")) + ":" + pair.A.X + ":" + pair2.Name);
                    var d = new Dictionary<string, Point>();
                    d["k"] = p;
                    Console.WriteLine(d["k"].Manhattan() + ":" + d["k"].X);
                    var init = new Counter(1) { N = 7, Tag = "t" };
                    Console.WriteLine(init.N + ":" + init.Tag + ":" + init.Step);
                    var pts = new List<Point> { new Point(1, 1), new Point(2, 2) };
                    Console.WriteLine(pts.Contains(new Point(2, 2)) + ":" + pts.IndexOf(new Point(1, 1)));
                    var dp = default(Point);
                    Console.WriteLine(dp.X + ":" + (dp == new Point(0, 0)));
                }
            }
            """, "P");
    }

    // `?.` (il-spec §3): `S?` receiver は HasValue / Value の明示ノード
    // (method は .Value の copy に対して呼ぶ)、参照型 receiver の値型 member は
    // T? に wrap、nested `?.`、`??=`、bool? の文字列化
    [CFact]
    public void GenericMethods_Specialized()
    {
        Backends.AssertParity("""
            using System;
            public class Item { public int V; }
            public class Pool<T> where T : Item
            {
                public T[] Items;
                public Pool(int n, Func<T> create) { Items = Arrays.Make(n, create); }
            }
            public static class Arrays
            {
                public static T[] Make<T>(int n, Func<T> create)
                {
                    var a = new T[n];
                    for (int i = 0; i < n; i++) a[i] = create();
                    return a;
                }
                public static T First<T>(T[] a) => a[0];
            }
            public class P
            {
                public T Pick<T>(T a, T b, bool first) => first ? a : b;
                public static void Main()
                {
                    int[] ints = Arrays.Make(3, () => 7);
                    float[] floats = Arrays.Make<float>(2, () => 1.5f);
                    var pool = new Pool<Item>(2, () => new Item { V = 4 });
                    var p = new P();
                    Console.WriteLine(ints[2] + ":" + floats[1] + ":" + pool.Items[1].V + ":" + Arrays.First(ints));
                    Console.WriteLine(p.Pick(1, 2, false) + ":" + p.Pick("a", "b", true));
                }
            }
            """, "P");
    }

    [CFact]
    public void Interfaces_CrossCast()
    {
        Backends.AssertParity("""
            using System;
            public interface IDraw { int Draw(); }
            public interface IHit { int Hit(); }
            public class Both : IDraw, IHit { public int Draw() => 1; public int Hit() => 2; }
            public class P
            {
                public static void Main()
                {
                    IDraw d = new Both();
                    IHit h = (IHit)d;
                    Both b = (Both)h;
                    Console.WriteLine(h.Hit() + ":" + b.Draw() + ":" + ((IDraw)h).Draw());
                }
            }
            """, "P");
    }

    [CFact]
    public void Nullable_ImplicitNumericConversion()
    {
        Backends.AssertParity("""
            using System;
            public class Opts { public float? Volume; }
            public class P
            {
                public static float Half(float? v) => (v ?? 1) / 2;
                public static void Main()
                {
                    int n = 3;
                    float? a = 0;
                    float? b = n;
                    var opts = new Opts { Volume = 2 };
                    opts.Volume = n;
                    Console.WriteLine(a.Value + 0.5f);
                    Console.WriteLine(b.Value / 2);
                    Console.WriteLine(opts.Volume.Value / 2);
                    Console.WriteLine(Half(5));
                }
            }
            """, "P");
    }

    [CFact]
    public void Nullable_ConditionalAccessAndCoalesceAssign()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct Vec { public int X; public int Y; public int Sum() => X + Y; public void Bump() { X = X + 100; } public int Twice => X * 2; }
            public readonly record struct Ro(int A) { public int Dbl() => A * 2; }
            public class Node { public int Hp; public int? Mana; public Node Next; public string Name = "n"; public Vec Pos; public int Len() => Name.Length; public Node Self() => this; }
            public class P
            {
                public static string Show(int? v) => v.HasValue ? "v=" + v.Value.ToString() : "none";
                public static void Main()
                {
                    Vec? v = new Vec { X = 1, Y = 2 };
                    Vec? nv = null;
                    int? a = v?.X;
                    int? b = nv?.X;
                    int? c = v?.Sum();
                    int? d = nv?.Sum();
                    int? t = v?.Twice;
                    v?.Bump();
                    Console.WriteLine(Show(a) + "|" + Show(b) + "|" + Show(c) + "|" + Show(d) + "|" + Show(t) + "|" + Show(v?.X));
                    Node n = new Node { Hp = 3 };
                    n.Pos.X = 9;
                    Node nn = null;
                    int? e = n?.Hp;
                    int? f = nn?.Hp;
                    int? g = n?.Len();
                    string h = nn?.Name;
                    string h2 = n?.Name;
                    int? m = n?.Mana;
                    int? k = n?.Next?.Hp;
                    int? k2 = n?.Self()?.Hp;
                    int? px = n?.Pos.X;
                    Vec? pv = nn?.Pos;
                    Console.WriteLine(Show(e) + "|" + Show(f) + "|" + Show(g) + "|" + (h == null) + "|" + h2 + "|" + Show(m) + "|" + Show(k) + "|" + Show(k2) + "|" + Show(px) + "|" + pv.HasValue);
                    int? q = null; q ??= 4;
                    int? r = 9; r ??= 1;
                    bool? bq = null;
                    bool? bt = true;
                    Ro? ro = new Ro(5);
                    Console.WriteLine(Show(q) + "|" + Show(r) + "|" + bq + "|" + bt + "|" + Show(ro?.Dbl()) + "|" + (nv?.Sum() == null) + "|" + (v?.Sum() > 2));
                    var list = new List<int?> { 1, 2 };
                    Console.WriteLine(Show(list?.Count));
                }
            }
            """, "P");
    }

    // Random instance (new Random / new Random(seed)) と Shared: seed 固定で
    // instance と Shared の列が一致、List / class field に保持した instance
    // (GC object)、null 比較、Shared の identity
    [CFact]
    public void Random_InstancesMatchSharedStream()
    {
        Backends.AssertParity("""
            using System.Collections.Generic;
            using TinySystem;
            public class G { public Random R = new Random(99); public int Roll() => R.Next(6); }
            public class P
            {
                public static void Main()
                {
                    Random.Seed(12345);
                    var s = "";
                    for (int i = 0; i < 5; i++) s += Random.Shared.Next(100) + ",";
                    var r = new Random(12345);
                    var t = "";
                    for (int i = 0; i < 5; i++) t += r.Next(100) + ",";
                    System.Console.WriteLine(s + "|" + t + "|" + (s == t));
                    var a = new Random(7);
                    var b = new Random(7);
                    System.Console.WriteLine(a.Next() == b.Next());
                    System.Console.WriteLine(a.NextFloat() + ":" + b.NextSingle() + ":" + a.Range(-3, 3) + ":" + b.Range(-3, 3) + ":" + a.Next(10, 20) + ":" + b.Next(10, 20));
                    var auto = new Random();
                    var x = auto.Next(10);
                    System.Console.WriteLine(x >= 0 && x < 10);
                    var list = new List<Random> { new Random(1), new Random(2) };
                    System.Console.WriteLine(list[0].Next(50) + ":" + list[1].Next(50));
                    Random held = null;
                    System.Console.WriteLine(held == null);
                    var g = new G();
                    System.Console.WriteLine(g.Roll() + ":" + g.Roll() + ":" + g.R.Next(6));
                    Random.Seed(-7);
                    System.Console.WriteLine(Random.Shared.Next(1 << 30) + ":" + new Random(-7).Next(1 << 30) + ":" + Random.Shared.NextFloat());
                    var sh = Random.Shared;
                    System.Console.WriteLine((sh == Random.Shared) + ":" + sh.Next(3));
                }
            }
            """, "P");
    }

    // シフトの C# 意味論 (負数 >> は算術、count は 31 でマスク) を両 backend で
    [CFact]
    public void Shifts_ArithmeticRightShiftAndMaskedCount()
    {
        Backends.AssertParity("""
            using System;
            public class P
            {
                public static void Main()
                {
                    int a = -8; int b = -1; int c = 1; int n = 33; int m = -1;
                    int x = -1024; x >>= 2;
                    int y = 3; y <<= 34;
                    int? q = -16; int? r = q >> 2;
                    Console.WriteLine((a >> 1) + "|" + (b >> 31) + "|" + (c << n) + "|" + (a >> n) + "|" + (c << m) + "|" + (b >> 0) + "|" + x + "|" + y + "|" + (-2147483648 >> 31) + "|" + r);
                    var s = 0;
                    for (int i = 0; i < 40; i++) s += (0x12345678 >> i) ^ (-0x12345678 >> i) ^ (1 << i);
                    Console.WriteLine(s);
                }
            }
            """, "P");
    }

    // 式位置の ++ / --: 前置は更新後、後置は更新前の値。条件 / 引数 / 添字 /
    // 代入右辺 / return / field / custom property / 副作用 receiver / Nullable
    [CFact]
    public void IncrementAsExpression_PrefixAndPostfixValues()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Box
            {
                public int N;
                public int Sets;
                private int _v;
                public int V { get { return _v; } set { Sets++; _v = value; } }
            }
            public class P
            {
                public static int Counter;
                public static int Calls;
                public static Box B = new Box();
                public static Box Get() { Calls++; return B; }
                public static int Idx() { Calls++; return 1; }
                public static int Twice(int x) => x * 2;
                public static int Next() => Counter++;
                public static int Bump() => ++Counter;
                public static int F(int n) { int k = 0; for (int i = 0; i < n; i++) { if (++k > 3) break; } return k; }
                public static void Main()
                {
                    int i = 5;
                    int a = Twice(i++);
                    int[] buf = new int[3];
                    int pos = 0;
                    buf[pos++] = 7;
                    buf[pos++] = 8;
                    int j = 2;
                    int b = buf[--j];
                    int x = 1;
                    int y = x++ + 10;
                    int z = --x * 2;
                    Console.WriteLine(F(10) + "|" + a + "|" + i + "|" + pos + "|" + buf[0] + "," + buf[1] + "|" + b + "|" + j + "|" + x + "|" + y + "|" + z);
                    Console.WriteLine(Next() + "," + Bump() + "," + Next() + "|" + Counter);
                    var box = new Box();
                    box.N = 3;
                    int c = box.N++;
                    int d = --box.N;
                    box.V = 10;
                    int e = box.V++;
                    int f = ++box.V;
                    Console.WriteLine(c + "|" + d + "|" + box.N + "|" + e + "|" + f + "|" + box.V + "|" + box.Sets);
                    var data = new List<int> { 1, 2, 3 };
                    int g = Get().N++;
                    int h = ++Get().N;
                    int k1 = data[Idx()]++;
                    int k2 = --data[Idx()];
                    Console.WriteLine(g + "|" + h + "|" + B.N + "|" + k1 + "|" + k2 + "|" + data[1] + "|" + Calls);
                    int? n = 5;
                    int? none = null;
                    int? p1 = n++;
                    int? p2 = ++n;
                    int? p3 = none++;
                    Console.WriteLine(p1 + "|" + p2 + "|" + n + "|" + p3.HasValue + "|" + none.HasValue);
                    float fl = 1.5f;
                    float fo = fl++;
                    Console.WriteLine(fo + "|" + fl + "|" + (--fl));
                }
            }
            """, "P");
    }

    // char は整数 code unit (il-spec §3): literal / s[i] / 算術 / Char.* /
    // string method の char 引数 / foreach string / 文字列化
    [CFact]
    public void Chars_IntegerCodeUnits()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class P
            {
                public const char Sep = ',';
                public static void Main()
                {
                    char c = 'a';
                    char d = (char)(c + 1);
                    int code = c;
                    int diff = 'z' - c;
                    bool range = c >= 'a' && c <= 'z';
                    char u = char.ToUpper(c);
                    string s = "Hello, World 42";
                    char first = s[0];
                    int digits = 0, letters = 0, spaces = 0;
                    foreach (char ch in s)
                    {
                        if (char.IsDigit(ch)) digits++;
                        else if (char.IsLetter(ch)) letters++;
                        else if (char.IsWhiteSpace(ch)) spaces++;
                    }
                    var parts = s.Split(Sep);
                    var idx = s.IndexOf('W');
                    var has = s.Contains('4');
                    var rep = s.Replace('l', 'L');
                    var sw = "";
                    switch (first) { case 'H': sw = "h"; break; case 'x': sw = "x"; break; default: sw = "?"; break; }
                    var kind = c is >= 'a' and <= 'z' ? "lower" : "other";
                    var list = new List<char> { 'x', 'y' };
                    var dict = new Dictionary<char, int>();
                    dict['k'] = 5;
                    char def = default(char);
                    char z = '\0';
                    var sb = "";
                    for (char k = 'a'; k <= 'e'; k++) sb += k;
                    Console.WriteLine(d + ":" + code + ":" + diff + ":" + range + ":" + u + ":" + first + ":" + digits + "," + letters + "," + spaces);
                    Console.WriteLine(parts.Length + ":" + idx + ":" + has + ":" + rep + ":" + sw + ":" + kind + ":" + list[1] + ":" + dict['k'] + ":" + (int)def + ":" + (z == 0) + ":" + sb + ":" + c.ToString() + ":" + $"{u}{d}" + ":" + (s[1] == 'e') + ":" + char.ToLower('Q') + ":" + char.IsUpper(first) + ":" + s.StartsWith('H') + ":" + s.EndsWith('2'));
                }
            }
            """, "P");
    }
}
