namespace TinyCs.Tests;

// T217 (M2): IL→C backend 向け入力契約 (IlExport) の検証
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
            f => { Assert.Equal(("X", "float", false), (f.Name, f.Type, f.IsStatic)); },
            f => { Assert.Equal(("Y", "float", false), (f.Name, f.Type, f.IsStatic)); },
            f => { Assert.Equal(("Count", "int", true), (f.Name, f.Type, f.IsStatic)); },
            f => { Assert.Equal(("Hp", "int", false), (f.Name, f.Type, f.IsStatic)); });

        Assert.All(player.Methods, m => Assert.NotNull(m.Body));
        var move = player.Methods.Single(m => m.Name == "Move");
        var assign = Assert.IsType<IlAssign>(
            Assert.Single(move.Body!.Stats));
        var target = Assert.IsType<IlField>(assign.Target);
        Assert.Equal("X", target.Name);

        var twice = player.Methods.Single(m => m.Name == "Twice");
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
        var grab = result.Classes[0].Methods.Single(m => m.Name == "Grab");
        Assert.Null(grab.Body);
        Assert.Contains(result.Diagnostics,
            d => d.Contains("InstanceMethodGroup"));
        var m = result.Classes[0].Methods.Single(x => x.Name == "M");
        Assert.NotNull(m.Body);
    }

    // T228: 型情報・field initializer・配列生成の契約
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
        var dt = k.Fields.Single(f => f.Name == "Dt");
        Assert.IsType<IlBin>(dt.Init);
        var make = k.Methods.Single(m => m.Name == "Make");
        Assert.Equal("float[]", make.ReturnType);
        Assert.Equal("int", Assert.Single(make.ParameterTypes));
        var local = Assert.IsType<IlLocal>(make.Body!.Stats[0]);
        var arr = Assert.IsType<IlNewArray>(local.Init);
        Assert.Equal("float", arr.ElementType);
        Assert.Equal("n", Assert.IsType<IlVar>(arr.Length).Name);
    }

    // T224: class 骨格 (ctor / custom property accessor) の契約
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
        var getter = timer.Methods.Single(m => m.Name == "get_Speed");
        Assert.IsType<IlReturn>(Assert.Single(getter.Body!.Stats));
        Assert.Equal("float", getter.ReturnType);
        var setter = timer.Methods.Single(m => m.Name == "set_Speed");
        Assert.Equal("value", Assert.Single(setter.Parameters));
        Assert.IsType<IlAssign>(Assert.Single(setter.Body!.Stats));
    }

    // T224 後半: top-level 文と operator の契約
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
}
