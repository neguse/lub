namespace TinyCs.Tests;

// IL→C backend 向け入力契約 (IlExport) の検証
public class IlExportTests
{
    private const string Source = """
        public class Player
        {
            public float X;
            public float Y;
            public int Hp { get; set; }
            public static int Count;

            public void Move(float dx)
            {
                X = X + dx;
            }

            public static int Twice(int v) => v * 2;
        }
        """;

    [Fact]
    public void Export_ClassMetadataAndIlBodies()
    {
        var result = IlExport.Export([Source]);
        Assert.Empty(result.Diagnostics);
        var player = Assert.Single(result.Classes);
        Assert.Equal("Player", player.Name);
        Assert.Null(player.BaseName);

        Assert.Collection(player.Fields,
            f => { Assert.Equal(("x", "float", false), (f.Name, f.Type, f.IsStatic)); },
            f => { Assert.Equal(("y", "float", false), (f.Name, f.Type, f.IsStatic)); },
            f => { Assert.Equal(("count", "int", true), (f.Name, f.Type, f.IsStatic)); },
            f => { Assert.Equal(("hp", "int", false), (f.Name, f.Type, f.IsStatic)); });

        Assert.All(player.Methods, m => Assert.NotNull(m.Body));
        var move = player.Methods.Single(m => m.Name == "move");
        var assign = Assert.IsType<IlAssign>(
            Assert.Single(move.Body!.Stats));
        var target = Assert.IsType<IlField>(assign.Target);
        Assert.Equal("x", target.Name);

        var twice = player.Methods.Single(m => m.Name == "twice");
        Assert.IsType<IlReturn>(Assert.Single(twice.Body!.Stats));
    }

    [Fact]
    public void Export_LayoutHashTracksInstanceFieldsOnly()
    {
        var baseHash = IlExport.Export([Source]).Classes[0].LayoutHash;
        // static field の変更は layout に影響しない
        var staticChanged = IlExport.Export([Source.Replace(
            "public static int Count;", "public static int Count2;")])
            .Classes[0].LayoutHash;
        Assert.Equal(baseHash, staticChanged);
        // instance field の改名は layout を変える (il-spec §14)
        var renamed = IlExport.Export([Source.Replace(
            "public float Y;", "public float Y2;")]).Classes[0].LayoutHash;
        Assert.NotEqual(baseHash, renamed);
    }

    // struct 値は reload 時に owner 経由で再直列化されるため (il-design §6)、
    // struct 内部のレイアウト変更は embed する class の hash に伝播する必要がある
    [Fact]
    public void Export_LayoutHashExpandsEmbeddedStructLayouts()
    {
        const string V1 = """
            public struct Vec2 { public float X; public float Y; }
            public class Player { public Vec2 Pos; public int Hp; }
            """;
        var baseHash = IlExport.Export([V1]).Classes[0].LayoutHash;

        // struct への field 追加は owner class の hash を変える
        var structGrown = IlExport.Export([V1.Replace(
            "public float Y; }", "public float Y; public float Z; }")])
            .Classes[0].LayoutHash;
        Assert.NotEqual(baseHash, structGrown);

        // struct 内 field の改名も伝播する
        var structRenamed = IlExport.Export([V1.Replace(
            "public float Y; }", "public float Y2; }")])
            .Classes[0].LayoutHash;
        Assert.NotEqual(baseHash, structRenamed);

        // 同一レイアウトなら安定
        var same = IlExport.Export([V1]).Classes[0].LayoutHash;
        Assert.Equal(baseHash, same);
    }

    // struct in struct も推移的に伝播する
    [Fact]
    public void Export_LayoutHashExpandsNestedStructLayouts()
    {
        const string V1 = """
            public struct Vec2 { public float X; public float Y; }
            public struct Aabb { public Vec2 Min; public Vec2 Max; }
            public class World { public Aabb Bounds; }
            """;
        var baseHash = IlExport.Export([V1]).Classes[0].LayoutHash;
        var innerGrown = IlExport.Export([V1.Replace(
            "public float Y; }", "public float Y; public float Z; }")])
            .Classes[0].LayoutHash;
        Assert.NotEqual(baseHash, innerGrown);
    }

    [Fact]
    public void Export_UnsupportedBodyIsNull()
    {
        // instance method group (診断対象) は IL 未対応 → Body null
        var result = IlExport.Export(["""
            using System;
            public class T
            {
                public int V;
                public int M() { return V; }
                public object Grab()
                {
                    Func<int> a = M;
                    return a;
                }
            }
            """]);
        var grab = result.Classes[0].Methods.Single(m => m.Name == "grab");
        Assert.Null(grab.Body);
        Assert.Contains(result.Diagnostics,
            d => d.Contains("InstanceMethodGroup"));
        var m = result.Classes[0].Methods.Single(x => x.Name == "m");
        Assert.NotNull(m.Body);
    }

    // 型情報・field initializer・配列生成の契約
    [Fact]
    public void Export_TypesInitializersAndArrays()
    {
        var result = IlExport.Export(["""
            public class K
            {
                public float Dt = 1.0f / 50.0f;
                public static float[] Make(int n)
                {
                    var xs = new float[n];
                    return xs;
                }
            }
            """]);
        var k = result.Classes[0];
        var dt = k.Fields.Single(f => f.Name == "dt");
        Assert.IsType<IlBin>(dt.Init);
        var make = k.Methods.Single(m => m.Name == "make");
        Assert.Equal("float[]", make.ReturnType);
        Assert.Equal("int", Assert.Single(make.ParameterTypes));
        var local = Assert.IsType<IlLocal>(make.Body!.Stats[0]);
        var arr = Assert.IsType<IlNewArray>(local.Init);
        Assert.Equal("float", arr.ElementType);
        Assert.Equal("n", Assert.IsType<IlVar>(arr.Length).Name);
    }

    // class 骨格 (ctor / custom property accessor) の契約
    [Fact]
    public void Export_CtorAndAccessorBodies()
    {
        var result = IlExport.Export(["""
            public class Timer
            {
                public float Elapsed;
                private float _speed;

                public Timer(float speed)
                {
                    _speed = speed;
                }

                public float Speed
                {
                    get { return _speed; }
                    set { _speed = value; }
                }
            }
            """]);
        var timer = result.Classes[0];
        Assert.NotNull(timer.Ctor);
        Assert.Equal("speed", Assert.Single(timer.Ctor!.Parameters));
        Assert.Equal("float", Assert.Single(timer.Ctor.ParameterTypes));
        Assert.NotNull(timer.Ctor.Body);
        var getter = timer.Methods.Single(m => m.Name == "get_speed");
        Assert.IsType<IlReturn>(Assert.Single(getter.Body!.Stats));
        Assert.Equal("float", getter.ReturnType);
        var setter = timer.Methods.Single(m => m.Name == "set_speed");
        Assert.Equal("value", Assert.Single(setter.Parameters));
        Assert.IsType<IlAssign>(Assert.Single(setter.Body!.Stats));
    }

    // top-level 文と operator の契約
    [Fact]
    public void Export_TopLevelAndOperators()
    {
        var result = IlExport.Export(["""
            using System;
            var v = new Vec(1.0f) + new Vec(2.0f);
            Console.WriteLine(v.X);

            public class Vec
            {
                public float X;
                public Vec(float x) { X = x; }
                public static Vec operator +(Vec a, Vec b)
                    => new Vec(a.X + b.X);
            }
            """]);
        Assert.NotNull(result.TopLevel);
        Assert.True(result.TopLevel!.Stats.Length >= 2);
        var add = result.Classes.Single().Methods
            .Single(m => m.Name == "__add");
        Assert.True(add.IsStatic);
        Assert.NotNull(add.Body);
        Assert.Equal(2, add.ParameterTypes.Length);
    }

    [Fact]
    public void Export_RecordClass_PositionalFieldsCtorAndBaseArgs()
    {
        var result = IlExport.Export(["""
            public record Pt(int X, int Y)
            {
                public int Sum() => X + Y;
            }
            public record Shape(string Kind);
            public record Circle(string Kind, float R) : Shape(Kind);
            """]);
        Assert.Empty(result.Diagnostics);
        var pt = Assert.Single(result.Classes, c => c.Name == "Pt");
        Assert.True(pt.IsRecord);
        Assert.Equal(["x", "y"], pt.Fields.Select(f => f.Name));
        Assert.Equal(["int", "int"], pt.Fields.Select(f => f.Type));
        Assert.NotNull(pt.Ctor);
        Assert.Equal(["X", "Y"], pt.Ctor!.Parameters.ToArray());
        Assert.Collection(pt.Ctor.Body!.Stats,
            s => Assert.Equal("x", Assert.IsType<IlField>(Assert.IsType<IlAssign>(s).Target).Name),
            s => Assert.Equal("y", Assert.IsType<IlField>(Assert.IsType<IlAssign>(s).Target).Name));
        Assert.Contains(pt.Methods, m => m.Name == "sum" && m.Body != null);

        var circle = Assert.Single(result.Classes, c => c.Name == "Circle");
        Assert.Equal("Shape", circle.BaseName);
        // Kind は base へ渡すだけ (C# も property を合成しない) → field は R のみ
        Assert.Equal(["r"], circle.Fields.Select(f => f.Name));
        var baseArg = Assert.Single(circle.Ctor!.BaseArgs);
        Assert.Equal("Kind", Assert.IsType<IlVar>(baseArg).Name);
    }

    // struct / record struct は field (auto property / positional 込み) +
    // instance member + ctor を契約に載せる (il-spec §10)。record struct の
    // layout hash は positional parameter を含む
    [Fact]
    public void Export_StructContract_IncludesMembersCtorAndRecordStruct()
    {
        var result = IlExport.Export(["""
            public struct Counter
            {
                public int N;
                public int Step = 2;
                public string Tag { get; set; }
                public int Doubled => N * 2;
                public int Clamped { get { return N > 10 ? 10 : N; } set { N = value; } }
                public Counter(int n) { N = n; }
                public void Inc() { N = N + Step; }
            }
            public record struct Point(int X, int Y)
            {
                public int Manhattan() => X + Y;
            }
            public class Owner { public Point P; }
            """]);
        Assert.Empty(result.Diagnostics);
        var counter = Assert.Single(result.Structs, s => s.Name == "Counter");
        Assert.False(counter.IsRecord);
        Assert.Equal(["n", "step", "tag"], counter.Fields.Select(f => f.Name));
        Assert.NotNull(counter.Fields[1].Init);
        Assert.Equal(["get_clamped", "set_clamped", "get_doubled", "inc"],
            counter.Methods.Select(m => m.Name));
        Assert.All(counter.Methods, m => Assert.False(m.IsStatic));
        Assert.All(counter.Methods, m => Assert.NotNull(m.Body));
        Assert.NotNull(counter.Ctor);
        Assert.Equal(["n"], counter.Ctor!.Parameters.ToArray());
        Assert.Equal(["int"], counter.Ctor.ParameterTypes.ToArray());

        var point = Assert.Single(result.Structs, s => s.Name == "Point");
        Assert.True(point.IsRecord);
        Assert.Equal(["x", "y"], point.Fields.Select(f => f.Name));
        Assert.Equal(["X", "Y"], point.Ctor!.Parameters.ToArray());
        Assert.Collection(point.Ctor.Body!.Stats,
            s => Assert.Equal("x", Assert.IsType<IlField>(Assert.IsType<IlAssign>(s).Target).Name),
            s => Assert.Equal("y", Assert.IsType<IlField>(Assert.IsType<IlAssign>(s).Target).Name));
        Assert.Contains(point.Methods, m => m.Name == "manhattan");

        // record struct の positional field 変更は owner class の hash に伝播する
        var ownerHash = Assert.Single(result.Classes, c => c.Name == "Owner").LayoutHash;
        var grown = IlExport.Export(["""
            public record struct Point(int X, int Y, int Z);
            public class Owner { public Point P; }
            """]);
        Assert.NotEqual(ownerHash, Assert.Single(grown.Classes).LayoutHash);
    }
}
