namespace TinyCs.Tests;

// hot reload runtime — 同一 VM で v1 実行状態へ v2 を適用する
// (il-design §6: weak registry + eager migration、instance identity 保持)。
// v1 と reload は別 chunk で実行し、呼び出し側で状態構築・検証を行う
public class HotReloadTests
{
    private static string Compose(string v1, string state, string v2,
        string asserts) => TestHelper.ComposeReload(v1, state, v2, asserts);

    private static void RunOk(string script) =>
        Assert.Equal("ok", TestHelper.RunLua(script).Trim());

    // method の改名: 新しい key を足し、旧 key を消す。改名前の名前で捕まえた
    // 関数値は保証しない (q.md Q17)
    [Fact]
    public void Reload_MethodRename_ReplacesKeyAndDropsOldOne()
    {
        const string V1 = """
            public class G
            {
                public int Use(int v) { return v; }
                public int Twice(int v) { return Use(v) * 2; }
            }
            """;
        const string V2 = """
            public class G
            {
                public int Fetch(int v) { return v + 1; }
                public int Twice(int v) { return Fetch(v) * 2; }
            }
            """;
        RunOk(Compose(V1,
            """
            local g = G.new()
            assert(g:Twice(2) == 4)
            """,
            V2,
            """
            assert(G.Use == nil, "old key removed")
            assert(g.Use == nil, "old key not reachable from instance")
            assert(g:Twice(2) == 6, "callers rebuilt")
            assert(g:Fetch(1) == 2)
            print("ok")
            """));
    }

    [Fact]
    public void Reload_MigratesInstanceFieldsPreservingIdentity()
    {
        const string V1 = """
            public class Player
            {
                public int Hp = 10;
                public int Speed = 7;
                public int Level() { return 1; }
            }
            """;
        const string V2 = """
            public class Player
            {
                public int Hp = 10;
                public int Mana = 5;
                public int Level() { return 2; }
            }
            """;
        RunOk(Compose(V1,
            """
            local p = Player.new()
            p.Hp = 42
            local before = p
            local beforeClass = Player
            """,
            V2,
            """
            assert(p == before, "instance identity")
            assert(Player == beforeClass, "class identity")
            assert(p.Hp == 42, "retained field keeps live value")
            assert(p.Mana == 5, "added field gets initializer")
            assert(p.Speed == nil, "discarded field dropped")
            assert(p:Level() == 2, "method body swapped")
            local q = Player.new()
            assert(q.Mana == 5, "post-reload construction uses v2 shape")
            assert(getmetatable(q) == Player, "post-reload instance links old identity")
            print("ok")
            """));
    }

    [Fact]
    public void Reload_RetainsStaticValuesAndAddsNewStatics()
    {
        const string V1 = """
            public class Counter
            {
                public static int Count = 0;
                public static int Legacy = 1;
                public static void Bump() { Count = Count + 1; }
            }
            """;
        const string V2 = """
            public class Counter
            {
                public static int Count = 0;
                public static int Max = 99;
                public static void Bump() { Count = Count + 2; }
            }
            """;
        RunOk(Compose(V1,
            """
            Counter.Bump()
            Counter.Bump()
            Counter.Bump()
            """,
            V2,
            """
            assert(Counter.Count == 3, "retained static keeps live value")
            assert(Counter.Max == 99, "added static initialized")
            assert(Counter.Legacy == nil, "discarded static dropped")
            Counter.Bump()
            assert(Counter.Count == 5, "swapped static method sees retained state")
            print("ok")
            """));
    }

    // static initializer が自クラスの constructor / static method を呼ぶ
    // (neguse/tcs#15)。reload 後も retained static は live 値を保ち、
    // 追加 static は v2 の initializer で初期化される
    [Fact]
    public void Reload_RetainsStaticInitializedFromOwnConstructor()
    {
        const string V1 = """
            public class V
            {
                public static V Zero = new V(0, 0);
                public static int Made = Count();
                public int X;
                public int Y;
                public V(int x, int y) { X = x; Y = y; Made = Made + 1; }
                public static int Count() { return 100; }
            }
            """;
        const string V2 = """
            public class V
            {
                public static V Zero = new V(0, 0);
                public static V One = new V(1, 1);
                public static int Made = Count();
                public int X;
                public int Y;
                public V(int x, int y) { X = x; Y = y; Made = Made + 1; }
                public static int Count() { return 100; }
                public int Sum() { return X + Y; }
            }
            """;
        RunOk(Compose(V1,
            """
            V.Zero.X = 9
            local zero = V.Zero
            """,
            V2,
            """
            assert(V.Zero == zero, "retained static keeps instance identity")
            assert(V.Zero.X == 9, "retained static keeps live value")
            assert(V.One ~= nil and V.One:Sum() == 2, "added static runs v2 initializer via own ctor")
            assert(V.Made == 100, "initializer order: Zero's ctor bumped pre-zero Made, then Made = Count()")
            print("ok")
            """));
    }

    // struct 値は参照 identity を持たないため owner 経由で再直列化する
    // (il-design §6)。layout 変更 (追加 + 削除) が embed 先へ届くこと
    [Fact]
    public void Reload_ReserializesStructFieldOnStructLayoutChange()
    {
        const string V1 = """
            public struct Vec2 { public float X; public float Y; }
            public class Player { public Vec2 Pos; }
            """;
        const string V2 = """
            public struct Vec2 { public float X; public float Z; }
            public class Player { public Vec2 Pos; }
            """;
        RunOk(Compose(V1,
            """
            local p = Player.new()
            p.Pos.X = 3.0
            p.Pos.Y = 4.0
            """,
            V2,
            """
            assert(p.Pos.X == 3.0, "retained struct field keeps value")
            assert(p.Pos.Y == nil, "discarded struct field dropped")
            assert(p.Pos.Z == 0, "added struct field zeroed")
            print("ok")
            """));
    }

    // record struct も struct 側の契約 (positional parameter が field) で
    // 再直列化される
    [Fact]
    public void Reload_ReserializesRecordStructFieldOnLayoutChange()
    {
        const string V1 = """
            public record struct Vec2(float X, float Y);
            public class Player { public Vec2 Pos; }
            """;
        const string V2 = """
            public record struct Vec2(float X, float Z);
            public class Player { public Vec2 Pos; }
            """;
        RunOk(Compose(V1,
            """
            local p = Player.new()
            p.Pos = Vec2.ctor(3.0, 4.0)
            """,
            V2,
            """
            assert(p.Pos.X == 3.0, "retained positional field keeps value")
            assert(p.Pos.Y == nil, "discarded positional field dropped")
            assert(p.Pos.Z == 0, "added positional field zeroed")
            print("ok")
            """));
    }

    // #14: 追加された enum 型 field の default は 0 (nil ではない)
    [Fact]
    public void Reload_AddedEnumFieldDefaultsToZero()
    {
        const string V1 = """
            public enum State { Idle, Run }
            public class Actor { public int Hp; }
            """;
        const string V2 = """
            public enum State { Idle, Run }
            public class Actor { public int Hp; public State S; }
            """;
        RunOk(Compose(V1,
            """
            local a = Actor.new()
            """,
            V2,
            """
            assert(a.S == 0, "added enum field zeroed")
            print("ok")
            """));
    }

    [Fact]
    public void Reload_ReserializesStructArrayElements()
    {
        const string V1 = """
            public struct Vec2 { public float X; public float Y; }
            public class Poly { public Vec2[] Points; }
            """;
        const string V2 = """
            public struct Vec2 { public float X; public float Y; public float Z; }
            public class Poly { public Vec2[] Points; }
            """;
        RunOk(Compose(V1,
            """
            local poly = Poly.new()
            poly.Points = { Vec2.new(), Vec2.new() }
            poly.Points[1].X = 1.0
            poly.Points[2].X = 2.0
            """,
            V2,
            """
            assert(#poly.Points == 2, "array length preserved")
            assert(poly.Points[1].X == 1.0, "element retained field")
            assert(poly.Points[1].Z == 0, "element added field zeroed")
            assert(poly.Points[2].X == 2.0, "second element retained")
            print("ok")
            """));
    }

    [Fact]
    public void Reload_AddedStructFieldGetsZeroedStruct()
    {
        const string V1 = """
            public struct Vec2 { public float X; public float Y; }
            public class Player { public int Hp = 10; }
            """;
        const string V2 = """
            public struct Vec2 { public float X; public float Y; }
            public class Player { public int Hp = 10; public Vec2 Pos; }
            """;
        RunOk(Compose(V1,
            """
            local p = Player.new()
            """,
            V2,
            """
            assert(p.Pos ~= nil, "added struct field present")
            assert(p.Pos.X == 0 and p.Pos.Y == 0, "added struct field zeroed")
            print("ok")
            """));
    }

    [Fact]
    public void Reload_DoesNotCallInstanceOnReload()
    {
        const string V1 = """
            public class Player { public int Hp = 10; }
            """;
        const string V2 = """
            public class Player
            {
                public int Hp = 10;
                public int Mana = 5;
                public void OnReload() { Hp = Hp + Mana; }
            }
            """;
        RunOk(Compose(V1,
            """
            local p = Player.new()
            p.Hp = 40
            """,
            V2,
            """
            assert(p.Hp == 40, "instance OnReload is not a reload hook")
            print("ok")
            """));
    }

    // operator は overload ごとに別名 (__mul_1 / __mul_2) の static 関数。
    // reload は IlExport の method 名で差し替えるので、Lua 側の関数名と
    // 一致していないと overload 本体が旧版のまま残る
    [Fact]
    public void Reload_SwapsOverloadedOperatorBodies()
    {
        const string V1 = """
            public class V
            {
                public int X;
                public V(int x) { X = x; }
                public static V operator *(V a, V b) => new V(a.X * b.X);
                public static V operator *(V a, int s) => new V(a.X * s);
            }
            public class P
            {
                public static int Run() { V v = new V(3); v *= 2; return (v * new V(5)).X; }
            }
            """;
        const string V2 = """
            public class V
            {
                public int X;
                public V(int x) { X = x; }
                public static V operator *(V a, V b) => new V(a.X * b.X + 1);
                public static V operator *(V a, int s) => new V(a.X * s + 100);
            }
            public class P
            {
                public static int Run() { V v = new V(3); v *= 2; return (v * new V(5)).X; }
            }
            """;
        RunOk(Compose(V1,
            """
            assert(P.Run() == 30, "v1 operators")
            """,
            V2,
            """
            assert(P.Run() == (3 * 2 + 100) * 5 + 1, "both overload bodies swapped")
            print("ok")
            """));
    }

    // 初期化子のない static の追加は reload 後に型別 default を持ち、既存の
    // 初期化子なし static は live 値のまま (initializer が書いた値も含む)
    [Fact]
    public void Reload_AddedStaticWithoutInitializerGetsDefault()
    {
        const string V1 = """
            public class Counter
            {
                public static int First = Init();
                public static int Later;
                static int Init() { Later = 7; return 3; }
            }
            """;
        const string V2 = """
            public class Counter
            {
                public static int First = Init();
                public static int Later;
                public static int Hits;
                public static bool Seen;
                static int Init() { Later = 7; return 3; }
                public static void Bump() { Hits++; Later++; }
            }
            """;
        RunOk(Compose(V1,
            """
            assert(Counter.Later == 7, "initializer write survives")
            Counter.Later = 20
            """,
            V2,
            """
            Counter.Bump()
            assert(Counter.Hits == 1, "added static starts at default")
            assert(Counter.Seen == false, "added bool static starts at false")
            assert(Counter.Later == 21, "retained static keeps live value")
            print("ok")
            """));
    }

    // watch は build ごとの IL を次の reload の v1 に使い回す: 前の plan の v2 の IL
    // (ReloadPlan.Export) を次の plan に渡しても、chunk は同じ移行をする。
    // 入力は前の build で検査済み (validated) なので、IL と chunk の作成は検査を繰り返さない
    [Fact]
    public void Reload_ChainWithPreviousExport_MigratesEachStep()
    {
        const string V1 = """
            public class G
            {
                public int A = 1;
                public int F() { return A; }
            }
            """;
        const string V2 = """
            public class G
            {
                public int A = 1;
                public int B = 5;
                public int F() { return A + B; }
            }
            """;
        const string V3 = """
            public class G
            {
                public int B = 5;
                public int C = 7;
                public int F() { return B + C; }
            }
            """;
        var p2 = HotReload.PlanReload([V1], [V2], validated: true);
        Assert.NotNull(p2.Export);
        var p3 = HotReload.PlanReload([V2], [V3], v1Export: p2.Export, validated: true);
        RunOk($"""
            {TestHelper.LoadChunk(Transpiler.Transpile([V1], instanceRegistry: true), "v1")}
            local g = G.new()
            {TestHelper.LoadChunk(p2.Chunk!, "v2")}
            assert(g:F() == 6)
            {TestHelper.LoadChunk(p3.Chunk!, "v3")}
            assert(g:F() == 12 and g.A == nil)
            print("ok")
            """);
    }
}
