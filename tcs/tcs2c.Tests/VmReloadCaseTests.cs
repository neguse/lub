namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// Individual edits for VM hot reload (tvm_apply, doc/object-model.md §6). v1's Game.Setup creates the state, and after the reload v2's Game.Report
/// writes the migrated state (overloads, struct statics, record struct, enum, namespaces, nested types etc., which the VmReloadTests fuzz scenarios do not cover).
/// </summary>
public class VmReloadCaseTests
{
    private const string Header = "using System;\nusing System.Collections.Generic;\n";

    private static string Game(string setup, string report, string fields = "") => $$"""
        public static class Game
        {
            {{fields}}
            public static void Setup() { {{setup}} }
            public static void Frame() { }
            public static void Report() { {{report}} }
        }
        """;

    private static string Reload(string v1, string v2)
    {
        var (code, stdout, stderr) = Backends.RunVmReload([Header + v1], [Header + v2], "Game");
        Assert.True(code == 0, $"exit {code}\n{stdout}\n{stderr}");
        return stdout;
    }

    // Renaming a method: the callers are also rebuilt in the same build
    [Fact]
    public void MethodRename_CallersUseTheNewMethod()
    {
        if (!Backends.VmEnabled) return;
        const string fields = "static G g;";
        var v1 = """
            public class G
            {
                public int Use(int v) { return v; }
                public int Twice(int v) { return Use(v) * 2; }
            }
            """ + Game("g = new G();", "", fields);
        var v2 = """
            public class G
            {
                public int Fetch(int v) { return v + 1; }
                public int Twice(int v) { return Fetch(v) * 2; }
            }
            """ + Game("g = new G();", "Console.WriteLine(g.Twice(2) + \" \" + g.Fetch(1));", fields);
        Assert.Equal("6 2", Reload(v1, v2));
    }

    // A static initializer calls the constructor of its own class. Retained statics keep their identity and values, and
    // added statics are created by v2's initializer (whose constructor writes to the retained statics)
    [Fact]
    public void StaticInitializedFromOwnConstructor_KeepsIdentity_AndAddedStaticRunsItsInitializer()
    {
        if (!Backends.VmEnabled) return;
        const string fields = "static V? zero;";
        var v1 = """
            public class V
            {
                public static V Zero = new V(0, 0);
                public static int Made = Count();
                public int X;
                public int Y;
                public V(int x, int y) { X = x; Y = y; Made = Made + 1; }
                public static int Count() { return 100; }
            }
            """ + Game("V.Zero.X = 9; zero = V.Zero;", "", fields);
        var v2 = """
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
            """ + Game("", "Console.WriteLine($\"{V.Zero == zero} {V.Zero.X} {V.One.Sum()} {V.Made}\");", fields);
        Assert.Equal("True 9 2 101", Reload(v1, v2));
    }

    // Constructors with different argument types are replaced the same way as methods (removed constructors disappear)
    [Fact]
    public void OverloadedConstructors_AreReplaced()
    {
        if (!Backends.VmEnabled) return;
        var v1 = """
            public class C
            {
                public int X;
                public C() { X = 1; }
                public C(int x) { X = x; }
                public C(string s) { X = 3; }
            }
            """ + Game("", "");
        var v2 = """
            public class C
            {
                public int X;
                public C() { X = 10; }
                public C(int x) { X = x * 10; }
                public C(float f) { X = 7; }
            }
            """ + Game("", "Console.WriteLine(new C().X + \" \" + new C(2).X + \" \" + new C(1f).X);");
        Assert.Equal("10 20 7", Reload(v1, v2));
    }

    // Statics of struct type also keep their live values, as for classes, and added statics get v2's initial values
    [Fact]
    public void StructStatics_AreRetainedAndAdded()
    {
        if (!Backends.VmEnabled) return;
        var v1 = """
            public struct S
            {
                public int V;
                public static int Count = 1;
                public static S Last;
                public static int Gone = 3;
                public static int Next() { Count = Count + 1; return Count; }
            }
            """ + Game("S.Next(); S.Last.V = 7;", "");
        var v2 = """
            public struct S
            {
                public int V;
                public static int Count = 1;
                public static S Last;
                public static int Added = 9;
                public static int Next() { Count = Count + 10; return Count; }
            }
            """ + Game("", "Console.WriteLine($\"{S.Count} {S.Last.V} {S.Added} {S.Next()}\");");
        Assert.Equal("2 7 9 12", Reload(v1, v2));
    }

    // Struct values in struct statics are also rebuilt when the layout changes (also when the owner is a struct).
    // Keep also makes heap objects hold a Vec2
    [Fact]
    public void StructStaticOfStructType_IsRelaidOut()
    {
        if (!Backends.VmEnabled) return;
        const string fields = "static Keep keep = new Keep();";
        var v1 = """
            public struct Vec2 { public float X; public float Y; public static Vec2 Last; }
            public struct Holder { public static Vec2 Pos; }
            public class Keep { public Vec2 V; }
            """ + Game("keep.V.X = 1; Holder.Pos.X = 3; Holder.Pos.Y = 4; Vec2.Last.X = 5; Vec2.Last.Y = 6;", "", fields);
        var v2 = """
            public struct Vec2 { public float X; public float Z; public static Vec2 Last; }
            public struct Holder { public static Vec2 Pos; }
            public class Keep { public Vec2 V; }
            """ + Game("", "Console.WriteLine($\"{Holder.Pos.X} {Holder.Pos.Z} {Vec2.Last.X} {Vec2.Last.Z} {keep.V.X}\");", fields);
        Assert.Equal("3 0 5 0 1", Reload(v1, v2));
    }

    // Even if heap objects do not hold structs, struct values in statics are rebuilt by field name (#451)
    [Fact]
    public void StructStaticOfStructType_IsRelaidOutWithoutHeapHolder()
    {
        if (!Backends.VmEnabled) return;
        var v1 = """
            public struct Vec2 { public float X; public float Y; }
            public static class Holder { public static Vec2 Pos; }
            """ + Game("Holder.Pos.X = 3; Holder.Pos.Y = 4;", "");
        var renamed = """
            public struct Vec2 { public float X; public float Z; }
            public static class Holder { public static Vec2 Pos; }
            """ + Game("", "Console.WriteLine($\"{Holder.Pos.X} {Holder.Pos.Z}\");");
        Assert.Equal("3 0", Reload(v1, renamed));
        var added = """
            public struct Vec2 { public float X; public float Y; public int N; }
            public static class Holder { public static Vec2 Pos; }
            """ + Game("", "Console.WriteLine($\"{Holder.Pos.X} {Holder.Pos.Y} {Holder.Pos.N}\");");
        Assert.Equal("3 4 0", Reload(v1, added));
    }

    // Positional parameters (fields) of record structs are also rebuilt
    [Fact]
    public void RecordStructField_IsRelaidOut()
    {
        if (!Backends.VmEnabled) return;
        const string fields = "static Player? p;";
        var v1 = """
            public record struct Vec2(float X, float Y);
            public class Player { public Vec2 Pos; }
            """ + Game("p = new Player(); p.Pos = new Vec2(3, 4);", "", fields);
        var v2 = """
            public record struct Vec2(float X, float Z);
            public class Player { public Vec2 Pos; }
            """ + Game("", "Console.WriteLine($\"{p!.Pos.X} {p.Pos.Z}\");", fields);
        Assert.Equal("3 0", Reload(v1, v2));
    }

    // Added enum and struct fields get default values
    [Fact]
    public void AddedEnumAndStructFields_StartAtDefault()
    {
        if (!Backends.VmEnabled) return;
        const string fields = "static Actor? a;";
        var v1 = """
            public enum State { Idle, Run }
            public struct Vec2 { public float X; public float Y; }
            public class Actor { public int Hp = 10; }
            """ + Game("a = new Actor();", "", fields);
        var v2 = """
            public enum State { Idle, Run }
            public struct Vec2 { public float X; public float Y; }
            public class Actor { public int Hp = 10; public State S; public Vec2 Pos; }
            """ + Game("", "Console.WriteLine($\"{a!.Hp} {(int)a.S} {a.S} {a.Pos.X} {a.Pos.Y}\");", fields);
        Assert.Equal("10 0 Idle 0 0", Reload(v1, v2));
    }

    // Replaces the bodies of both overloads of the same operator
    [Fact]
    public void OverloadedOperatorBodies_AreSwapped()
    {
        if (!Backends.VmEnabled) return;
        const string run = """
            public class P
            {
                public static int Run() { V v = new V(3); v *= 2; return (v * new V(5)).X; }
            }
            """;
        var v1 = """
            public class V
            {
                public int X;
                public V(int x) { X = x; }
                public static V operator *(V a, V b) => new V(a.X * b.X);
                public static V operator *(V a, int s) => new V(a.X * s);
            }
            """ + run + Game("Console.WriteLine(P.Run());", "");
        var v2 = """
            public class V
            {
                public int X;
                public V(int x) { X = x; }
                public static V operator *(V a, V b) => new V(a.X * b.X + 1);
                public static V operator *(V a, int s) => new V(a.X * s + 100);
            }
            """ + run + Game("", "Console.WriteLine(P.Run());");
        Assert.Equal("30\n531", Reload(v1, v2));
    }

    // An added static without an initializer gets the type's default value. Existing statics without initializers keep their live values (including values written by an initializer)
    [Fact]
    public void AddedStaticWithoutInitializer_GetsDefault()
    {
        if (!Backends.VmEnabled) return;
        var v1 = """
            public class Counter
            {
                public static int First = Init();
                public static int Later;
                static int Init() { Later = 7; return 3; }
            }
            """ + Game("Console.WriteLine(Counter.Later); Counter.Later = 20;", "");
        var v2 = """
            public class Counter
            {
                public static int First = Init();
                public static int Later;
                public static int Hits;
                public static bool Seen;
                static int Init() { Later = 7; return 3; }
                public static void Bump() { Hits++; Later++; }
            }
            """ + Game("", "Counter.Bump(); Console.WriteLine($\"{Counter.Hits} {Counter.Seen} {Counter.Later}\");");
        Assert.Equal("7\n1 False 21", Reload(v1, v2));
    }

    // Adding an enum member is applied live (saved values stay as they are)
    [Fact]
    public void EnumMemberAdded_IsLive()
    {
        if (!Backends.VmEnabled) return;
        const string fields = "static Kind k;";
        var v1 = "public enum Kind { A, B }\n" + Game("k = Kind.B;", "", fields);
        var v2 = "public enum Kind { A, B, C }\n" + Game("", "Console.WriteLine($\"{(int)Kind.C} {k}\");", fields);
        Assert.Equal("2 B", Reload(v1, v2));
    }

    // If the function type of a live closure (the layout of the struct it returns) changes, a restart is requested instead of applying it live
    [Fact]
    public void LiveClosureReturningARelaidOutStruct_RequiresRestart()
    {
        if (!Backends.VmEnabled) return;
        var v1 = """
            public struct V { public int X; public V(int x) { X = x; } }
            public class Maker { public static Func<V> Make = () => new V(1); }
            """ + Game("Console.WriteLine(Maker.Make().X);", "");
        var v2 = """
            public struct V
            {
                public int X; public int Y;
                public V(int x) { X = x; Y = 7; }
            }
            public class Maker
            {
                public static Func<V> Make = () => new V(1);
                public static int Get() => Make().X * 100 + Make().Y;
            }
            """ + Game("", "Console.WriteLine(Maker.Get());");
        var (code, stdout, stderr) = Backends.RunVmReload([Header + v1], [Header + v2], "Game");
        Assert.Equal(3, code);
        Assert.Equal("1", stdout);
        Assert.Contains("live closure runs Maker.", stderr);
    }

    // Editing the body of a method of a nested type replaces only the methods of the type that has that method
    private const string Nested = """
        public class Outer
        {
            public static int Value() { return 1; }
            public class Inner
            {
                public static int Value() { return 10; }
                public static int Read() { return Value(); }
            }
            public struct Part
            {
                public int V;
                public int Value() { return V + 100; }
            }
            public static int Run()
            {
                var p = new Part();
                return Value() + Inner.Read() + p.Value();
            }
        }
        """;

    [Theory]
    [InlineData("return 10;", "return 20;", "111\n121")]
    [InlineData("return V + 100;", "return V + 200;", "111\n211")]
    [InlineData("return 1;", "return 2;", "111\n112")]
    public void NestedTypeBodyEdit_ReplacesTheMethodOfTheOwningType(string before, string after, string expected)
    {
        if (!Backends.VmEnabled) return;
        var v1 = Nested + Game("Console.WriteLine(Outer.Run());", "");
        var v2 = Nested.Replace(before, after) + Game("", "Console.WriteLine(Outer.Run());");
        Assert.Equal(expected, Reload(v1, v2));
    }

    // Even if types with the same name exist in different namespaces, the instances of each are migrated
    [Fact]
    public void SameNamedTypesInNamespaces_MigrateIndependently()
    {
        if (!Backends.VmEnabled) return;
        const string fields = "static A.Color? a; static B.Color? b;";
        var v1 = """
            namespace A { public class Color { public int V = 1; } }
            namespace B { public class Color { public int V = 2; public int Old = 5; } }
            """ + Game("a = new A.Color(); b = new B.Color(); a.V = 3; b.V = 4;", "", fields);
        var v2 = """
            namespace A { public class Color { public int V = 1; public int Extra = 11; } }
            namespace B { public class Color { public int V = 2; public int Added = 22; } }
            """ + Game("", "Console.WriteLine($\"{a!.V} {a.Extra} {b!.V} {b.Added}\");", fields);
        Assert.Equal("3 11 4 22", Reload(v1, v2));
    }

    [Fact]
    public void NamespacedStructField_IsRelaidOut()
    {
        if (!Backends.VmEnabled) return;
        const string fields = "static Holder? h;";
        var v1 = """
            namespace A { public struct Vec { public int X; } }
            public class Holder { public A.Vec Pos; }
            """ + Game("h = new Holder(); h.Pos.X = 4;", "", fields);
        var v2 = """
            namespace A { public struct Vec { public int X; public int Y; } }
            public class Holder { public A.Vec Pos; }
            """ + Game("", "Console.WriteLine($\"{h!.Pos.X} {h.Pos.Y}\");", fields);
        Assert.Equal("4 0", Reload(v1, v2));
    }

    // Even if a type with the same simple name exists outside the namespace, only the bodies of the type inside the namespace are replaced
    [Fact]
    public void BodyEditInQualifiedType_ReplacesThatTypeOnly()
    {
        if (!Backends.VmEnabled) return;
        const string source = """
            public class Color { public static int F() { return 1; } }
            namespace A { public class Color { public static int F() { return 2; } } }
            """;
        var report = "Console.WriteLine($\"{Color.F()} {A.Color.F()}\");";
        Assert.Equal("1 2\n1 3", Reload(source + Game(report, ""), source.Replace("return 2;", "return 3;") + Game("", report)));
    }

    // Even if the callee's body starts writing to a struct parameter, the caller's value does not change
    [Fact]
    public void CalleeStartsWritingStructParameter_CallerValueIsKept()
    {
        if (!Backends.VmEnabled) return;
        const string callee = """
            public struct V { public int X; }
            public static class M
            {
                public static int Read(V v)
                {
                    return v.X;
                }
            }
            public static class Play
            {
                public static int Run()
                {
                    var a = new V();
                    a.X = 3;
                    return M.Read(a) * 10 + a.X;
                }
            }
            """;
        var v1 = callee + Game("Console.WriteLine(Play.Run());", "");
        var v2 = callee.Replace("return v.X;", "v.X = 9; return v.X;") + Game("", "Console.WriteLine(Play.Run());");
        Assert.Equal("33\n93", Reload(v1, v2));
    }

    // The static OnReload of the entry type is not called on the first load, and is called once on reload
    [Fact]
    public void EntryStaticOnReload_RunsOncePerReload()
    {
        if (!Backends.VmEnabled) return;
        // In a module that has an entry (Main), call the static OnReload of the entry type
        const string game = """
            public static class Game
            {
                static int marks;
                public static void Main() { }
                public static void OnReload() { marks++; }
                public static void Setup() { }
                public static void Frame() { }
                public static void Report() { Console.WriteLine("BODY " + marks); }
            }
            """;
        Assert.Equal("v2 1", Reload(game.Replace("BODY", "v1"), game.Replace("BODY", "v2")));
    }

    // Editing a body that cannot become IL (sizeof) is an error, and the last successful build is kept (the next edit is a diff from that build)
    [Fact]
    public void Session_IlBuildError_KeepsTheLastGoodBuild()
    {
        if (!Backends.VmEnabled) return;
        var session = new TinyCs.Vm.VmSession(null, ["Game.*"], []);
        const string v1 = Header + "public static class Game { static int n; public static void Frame() { n++; } public static void Report() { Console.WriteLine(\"v1 \" + n); } }";
        const string bad = Header + "public static class Game { static int n; public static void Frame() { n += sizeof(int); } public static void Report() { } }";
        const string v2 = Header + "public static class Game { static int n; public static void Frame() { n++; } public static void Report() { Console.WriteLine(\"v2 \" + n); } }";
        var first = session.Update([v1]);
        Assert.Equal("module", first.Kind);
        var error = session.Update([bad]);
        Assert.Equal("error", error.Kind);
        Assert.Contains(error.Errors!, e => e.Contains("SizeOfExpression"));
        var second = session.Update([v2]);
        Assert.Equal("batch", second.Kind);
        var (code, stdout, stderr) = Backends.RunVmBatch(first.Module!, second.Batch!, "Game", 3);
        Assert.True(code == 0, stderr);
        Assert.Equal("v2 3", stdout);
    }
}
