using Tcs;
namespace TinyCs.Tests;

// Verification of the input contract for the IL→C backend (IlExport)
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

        // Fields and auto properties in declaration order (the order the initializers run)
        Assert.Collection(player.Fields,
            f => { Assert.Equal(("X", "float", false), (f.Name, f.Type, f.IsStatic)); },
            f => { Assert.Equal(("Y", "float", false), (f.Name, f.Type, f.IsStatic)); },
            f => { Assert.Equal(("Hp", "int", false), (f.Name, f.Type, f.IsStatic)); },
            f => { Assert.Equal(("Count", "int", true), (f.Name, f.Type, f.IsStatic)); });

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
        // Changing a static field does not affect the layout
        var staticChanged = IlExport.Export([Source.Replace(
            "public static int Count;", "public static int Count2;")])
            .Classes[0].LayoutHash;
        Assert.Equal(baseHash, staticChanged);
        // Renaming an instance field changes the layout (il-spec §14)
        var renamed = IlExport.Export([Source.Replace(
            "public float Y;", "public float Y2;")]).Classes[0].LayoutHash;
        Assert.NotEqual(baseHash, renamed);
    }

    // Struct values are re-serialized through their owner on reload (il-design §6), so
    // layout changes inside a struct must propagate to the hash of the class that embeds it
    [Fact]
    public void Export_LayoutHashExpandsEmbeddedStructLayouts()
    {
        const string V1 = """
            public struct Vec2 { public float X; public float Y; }
            public class Player { public Vec2 Pos; public int Hp; }
            """;
        var baseHash = IlExport.Export([V1]).Classes[0].LayoutHash;

        // Adding a field to a struct changes the owner class's hash
        var structGrown = IlExport.Export([V1.Replace(
            "public float Y; }", "public float Y; public float Z; }")])
            .Classes[0].LayoutHash;
        Assert.NotEqual(baseHash, structGrown);

        // Renaming a field inside a struct also propagates
        var structRenamed = IlExport.Export([V1.Replace(
            "public float Y; }", "public float Y2; }")])
            .Classes[0].LayoutHash;
        Assert.NotEqual(baseHash, structRenamed);

        // Stable if the layout is the same
        var same = IlExport.Export([V1]).Classes[0].LayoutHash;
        Assert.Equal(baseHash, same);
    }

    // struct in struct also propagates transitively
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
    public void Export_UnsupportedSyntaxIsAnError()
    {
        // Instance method groups are outside the subset
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
        // Unsupported syntax stops with the same error as the Lua output (no partial IL is returned)
        Assert.Empty(result.Classes);
        Assert.Contains(result.Diagnostics,
            d => d.Contains("InstanceMethodGroup"));
    }

    // Contract for type information, field initializers, and array creation
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

    // Contract for the class skeleton (ctor / custom property accessors)
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
        Assert.Single(timer.Ctors);
        Assert.Equal("speed", Assert.Single(timer.Ctors[0].Parameters));
        Assert.Equal("float", Assert.Single(timer.Ctors[0].ParameterTypes));
        Assert.NotNull(timer.Ctors[0].Body);
        var getter = timer.Methods.Single(m => m.Name == "get_Speed");
        Assert.IsType<IlReturn>(Assert.Single(getter.Body!.Stats));
        Assert.Equal("float", getter.ReturnType);
        var setter = timer.Methods.Single(m => m.Name == "set_Speed");
        Assert.Equal("value", Assert.Single(setter.Parameters));
        Assert.IsType<IlAssign>(Assert.Single(setter.Body!.Stats));
    }

    // Contract for top-level statements and operators
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
        Assert.True(result.TopLevel!.Stats.Count >= 2);
        var add = result.Classes.Single().Methods
            .Single(m => m.Name == "__add");
        Assert.True(add.IsStatic);
        Assert.NotNull(add.Body);
        Assert.Equal(2, add.ParameterTypes.Count);
    }

    // Overloads of the same operator are `__mul_1` / `__mul_2` in declaration order (the same names as the Lua
    // output). Call sites become an IlCall of the overload that Roslyn chose
    [Fact]
    public void Export_OverloadedOperators_HaveUniqueNamesAndStaticCallSites()
    {
        var result = IlExport.Export(["""
            public class Vec
            {
                public float X;
                public Vec(float x) { X = x; }
                public static Vec operator *(Vec a, Vec b) => new Vec(a.X * b.X);
                public static Vec operator *(Vec a, float s) => new Vec(a.X * s);
                public static Vec operator -(Vec a) => new Vec(-a.X);
                public static Vec Scale(Vec v) => -v * 2.0f;
            }
            """]);
        var cls = result.Classes.Single();
        Assert.Equal(["__mul_1", "__mul_2", "__unm"],
            cls.Methods.Where(m => m.Name.StartsWith("__")).Select(m => m.Name));
        var scale = cls.Methods.Single(m => m.Name == "Scale");
        var ret = Assert.IsType<IlReturn>(scale.Body!.Stats.Single());
        var mul = Assert.IsType<IlCall>(ret.Value);
        Assert.Equal("Vec.__mul_2", mul.Callee);
        var neg = Assert.IsType<IlCall>(mul.Args[0]);
        Assert.Equal("Vec.__unm", neg.Callee);
    }

    [Fact]
    public void Export_RecordClass_PositionalFieldsAndCtor()
    {
        var result = IlExport.Export(["""
            public record Pt(int X, int Y)
            {
                public int Sum() => X + Y;
            }
            """]);
        Assert.Empty(result.Diagnostics);
        var pt = Assert.Single(result.Classes, c => c.Name == "Pt");
        Assert.True(pt.IsRecord);
        Assert.Equal(["X", "Y"], pt.Fields.Select(f => f.Name));
        Assert.Equal(["int", "int"], pt.Fields.Select(f => f.Type));
        Assert.Single(pt.Ctors);
        Assert.Equal(["X", "Y"], pt.Ctors[0].Parameters.ToArray());
        Assert.Collection(pt.Ctors[0].Body!.Stats,
            s => Assert.Equal("X", Assert.IsType<IlField>(Assert.IsType<IlAssign>(s).Target).Name),
            s => Assert.Equal("Y", Assert.IsType<IlField>(Assert.IsType<IlAssign>(s).Target).Name));
        Assert.Contains(pt.Methods, m => m.Name == "Sum" && m.Body != null);
    }

    // struct / record struct put fields (including auto properties / positional ones) +
    // instance members + ctor into the contract (il-spec §10). The record struct
    // layout hash includes positional parameters
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
        Assert.Equal(["N", "Step", "Tag"], counter.Fields.Select(f => f.Name));
        Assert.NotNull(counter.Fields[1].Init);
        Assert.Equal(["get_Clamped", "set_Clamped", "get_Doubled", "Inc"],
            counter.Methods.Select(m => m.Name));
        Assert.All(counter.Methods, m => Assert.False(m.IsStatic));
        Assert.All(counter.Methods, m => Assert.NotNull(m.Body));
        Assert.Single(counter.Ctors);
        Assert.Equal(["n"], counter.Ctors[0].Parameters.ToArray());
        Assert.Equal(["int"], counter.Ctors[0].ParameterTypes.ToArray());

        var point = Assert.Single(result.Structs, s => s.Name == "Point");
        Assert.True(point.IsRecord);
        Assert.Equal(["X", "Y"], point.Fields.Select(f => f.Name));
        Assert.Equal(["X", "Y"], point.Ctors[0].Parameters.ToArray());
        Assert.Collection(point.Ctors[0].Body!.Stats,
            s => Assert.Equal("X", Assert.IsType<IlField>(Assert.IsType<IlAssign>(s).Target).Name),
            s => Assert.Equal("Y", Assert.IsType<IlField>(Assert.IsType<IlAssign>(s).Target).Name));
        Assert.Contains(point.Methods, m => m.Name == "Manhattan");

        // Changing a positional field of a record struct propagates to the owner class's hash
        var ownerHash = Assert.Single(result.Classes, c => c.Name == "Owner").LayoutHash;
        var grown = IlExport.Export(["""
            public record struct Point(int X, int Y, int Z);
            public class Owner { public Point P; }
            """]);
        Assert.NotEqual(ownerHash, Assert.Single(grown.Classes).LayoutHash);
    }
}
