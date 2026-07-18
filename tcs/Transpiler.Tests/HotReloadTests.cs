namespace TinyCs.Tests;

// T220(b): hot reload runtime — 同一 VM で v1 実行状態へ v2 を適用する
// (il-design §6: weak registry + eager migration、instance identity 保持)。
// テストは 1 つの Lua chunk として v1 → 状態構築 → reload chunk → 検証を実行する
public class HotReloadTests
{
    private static string Compose(string v1, string state, string v2,
        string asserts) =>
        $"{Transpiler.Transpile([v1])}\n{state}\n" +
        $"{HotReload.EmitReloadChunk([v1], [v2])}\n{asserts}";

    private static void RunOk(string script) =>
        Assert.Equal("ok", TestHelper.RunLua(script).Trim());

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

    [Fact]
    public void Reload_MigratesInheritedFieldsOnDerivedInstances()
    {
        const string V1 = """
            public class Animal { public int Age = 1; }
            public class Dog : Animal { public int Bark = 2; }
            """;
        const string V2 = """
            public class Animal { public int Age = 1; public int Legs = 4; }
            public class Dog : Animal { public int Bark = 2; }
            """;
        RunOk(Compose(V1,
            """
            local d = Dog.new()
            d.Age = 9
            """,
            V2,
            """
            assert(d.Legs == 4, "base-added field reaches derived instance")
            assert(d.Age == 9, "inherited retained field keeps value")
            assert(d.Bark == 2, "derived fields untouched")
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
    public void Reload_CallsOnReloadHookAfterMigration()
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
            assert(p.Hp == 45, "OnReload runs after field migration")
            print("ok")
            """));
    }
}
