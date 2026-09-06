namespace TinyCs.Tests;

// hot reload runtime — 同一 VM で v1 実行状態へ v2 を適用する
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
            p.hp = 42
            local before = p
            local beforeClass = Player
            """,
            V2,
            """
            assert(p == before, "instance identity")
            assert(Player == beforeClass, "class identity")
            assert(p.hp == 42, "retained field keeps live value")
            assert(p.mana == 5, "added field gets initializer")
            assert(p.speed == nil, "discarded field dropped")
            assert(p:level() == 2, "method body swapped")
            local q = Player.new()
            assert(q.mana == 5, "post-reload construction uses v2 shape")
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
            Counter.bump()
            Counter.bump()
            Counter.bump()
            """,
            V2,
            """
            assert(Counter.count == 3, "retained static keeps live value")
            assert(Counter.max == 99, "added static initialized")
            assert(Counter.legacy == nil, "discarded static dropped")
            Counter.bump()
            assert(Counter.count == 5, "swapped static method sees retained state")
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
            d.age = 9
            """,
            V2,
            """
            assert(d.legs == 4, "base-added field reaches derived instance")
            assert(d.age == 9, "inherited retained field keeps value")
            assert(d.bark == 2, "derived fields untouched")
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
            p.pos.x = 3.0
            p.pos.y = 4.0
            """,
            V2,
            """
            assert(p.pos.x == 3.0, "retained struct field keeps value")
            assert(p.pos.y == nil, "discarded struct field dropped")
            assert(p.pos.z == 0, "added struct field zeroed")
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
            poly.points = { Vec2.new(), Vec2.new() }
            poly.points[1].x = 1.0
            poly.points[2].x = 2.0
            """,
            V2,
            """
            assert(#poly.points == 2, "array length preserved")
            assert(poly.points[1].x == 1.0, "element retained field")
            assert(poly.points[1].z == 0, "element added field zeroed")
            assert(poly.points[2].x == 2.0, "second element retained")
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
            assert(p.pos ~= nil, "added struct field present")
            assert(p.pos.x == 0 and p.pos.y == 0, "added struct field zeroed")
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
            p.hp = 40
            """,
            V2,
            """
            assert(p.hp == 45, "OnReload runs after field migration")
            print("ok")
            """));
    }
}
