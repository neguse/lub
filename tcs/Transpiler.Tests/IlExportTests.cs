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

    [Fact]
    public void Export_UnsupportedBodyIsNull()
    {
        var result = IlExport.Export(["""
            using System;
            public class T
            {
                public static void M() { }
                public static object Grab()
                {
                    Action a = M; // method group 参照は IL 未対応
                    return a;
                }
            }
            """]);
        var grab = result.Classes[0].Methods.Single(m => m.Name == "Grab");
        Assert.Null(grab.Body);
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
}
