namespace TinyCs.Tests;

// 名前規則 (LuaNames)。user の型の member は C# の識別子のまま、
// 参照専用型 (--ref、host) は snake_case / UPPER_SNAKE に写し、小文字パスで参照する (IL の foreign の名前、C の host 関数の名前の元)。
public class LuaNamingTests
{
    [Theory]
    [InlineData("BeginPass", "begin_pass")]
    [InlineData("beginPass", "begin_pass")]
    [InlineData("ColorEdit3", "color_edit3")]
    [InlineData("Fnv1a64", "fnv1a64")]
    [InlineData("OverlapAabb", "overlap_aabb")]
    [InlineData("InterleavePncmw", "interleave_pncmw")]
    [InlineData("X", "x")]
    [InlineData("_hp", "_hp")]
    [InlineData("_maxHp", "_max_hp")]
    [InlineData("End", "end_")]
    [InlineData("Do", "do_")]
    [InlineData("Rgba32f", "rgba32f")]
    public void HostMember_MapsToSnakeCase(string csharp, string lua)
    {
        Assert.Equal(lua, TcsSelf.LuaNames.HostMember(csharp));
    }

    [Theory]
    [InlineData("Depth24Stencil8", "DEPTH24_STENCIL8")]
    [InlineData("Rgba32f", "RGBA32F")]
    [InlineData("TriangleStrip", "TRIANGLE_STRIP")]
    [InlineData("DontCare", "DONT_CARE")]
    [InlineData("None", "NONE")]
    [InlineData("End", "END")]
    public void HostConst_MapsToUpperSnake(string csharp, string lua)
    {
        Assert.Equal(lua, TcsSelf.LuaNames.HostConst(csharp));
    }

    [Fact]
    public void UserMembers_EmitCSharpNames()
    {
        const string source = """
            public class Player
            {
                public int Hp = 3;
                public int maxHp { get; set; } = 10;
                public int XMLValue => Hp + maxHp;
                public int AddHp(int n) { Hp += n; return Hp; }
                public static int Twice(int n) { return n * 2; }
            }

            public static class Game
            {
                public static int Run()
                {
                    var p = new Player();
                    p.AddHp(2);
                    p.maxHp = 20;
                    return p.XMLValue + Player.Twice(1);
                }
            }
            """;
        Assert.Equal("27,3,10,4", TestHelper.CompileAndRunVm(source, "Game.Run() + \",\" + new Player().Hp + \",\" + new Player().maxHp + \",\" + Player.Twice(2)"));
    }

    [Fact]
    public void CustomProperty_AccessorsKeepPropertyName()
    {
        const string source = """
            public class Box
            {
                private int _w;
                public int Width
                {
                    get { return _w; }
                    set { _w = value * 2; }
                }
            }
            """;
        Assert.Equal("8", TestHelper.CompileAndRunVmMain(source, "var b = new Box(); b.Width = 4; System.Console.WriteLine(b.Width);"));
    }

    [Fact]
    public void EnumMembers_EmitCSharpNames()
    {
        const string source = """
            public enum Direction { Up, Down, LeftSide, RIGHT }

            public class Nav
            {
                public static int GetDir() { return (int)Direction.LeftSide; }
            }
            """;
        Assert.Equal("2,2,3", TestHelper.CompileAndRunVm(source, "Nav.GetDir() + \",\" + (int)Direction.LeftSide + \",\" + (int)Direction.RIGHT"));
    }

    [Fact]
    public void Record_PositionalFieldsKeepCSharpNames()
    {
        const string source = """
            public record Point(int PosX, int posY);

            public static class Geo
            {
                public static int Sum()
                {
                    var p = new Point(1, 2);
                    var q = p with { posY = 5 };
                    var (a, b) = q;
                    return p.PosX + q.posY + a + b;
                }
            }
            """;
        Assert.Equal("12,3,4", TestHelper.CompileAndRunVm(source, "Geo.Sum() + \",\" + new Point(3, 4).PosX + \",\" + new Point(3, 4).posY"));
    }

    // IL の名前 (tcs2c の C 名の元) も C# の識別子のまま
    [Fact]
    public void IlExport_UsesCSharpNames()
    {
        var result = IlExport.Export(["""
            public enum Kind { BigValue, small }
            public class Player
            {
                public int Hp;
                public static int MaxHp = 9;
                public int Level { get; set; }
                public int Clamped { get => Hp; set { Hp = value; } }
                public int AddHp(int n) { Hp += n; return Hp; }
                public void OnFrame() { }
            }
            public record Point(int PosX, int posY);
            """]);
        Assert.Empty(result.Diagnostics);
        var player = result.Classes.Single(c => c.Name == "Player");
        Assert.Equal(["Hp", "MaxHp", "Level"], player.Fields.Select(f => f.Name));
        Assert.Equal(["get_Clamped", "set_Clamped", "AddHp", "OnFrame"],
            player.Methods.Select(m => m.Name));
        Assert.Equal(["PosX", "posY"],
            result.Classes.Single(c => c.Name == "Point").Fields.Select(f => f.Name));
        Assert.Equal(["BigValue", "small"],
            result.EnumTypes.Single(e => e.Name == "Kind").MemberNames);
    }

    private const string LubStub = """
        public static class Lub
        {
            public const string Pending = "pending";

            public static void Config(ConfigOpts opts) { }

            public static class Gfx
            {
                public enum PixelFormat { Rgba8 = 1, Depth24Stencil8 = 10 }
                public enum LoadAction { Clear = 1, DontCare = 3 }
                public static TextureRef? MainTex;
                public static void BeginPass(PassOpts opts) { }
                public static Readback? Readback() { return null; }
                public static void Size(out int w, out int h) { w = 0; h = 0; }
            }
        }

        public class ConfigOpts { public int? Width; }
        public class TextureRef { public int Version; }
        public class PassOpts
        {
            public TextureRef? Target;
            public float[]? ClearColor;
            public int? Load;
        }
        public class Readback
        {
            public int ReadTexture(TextureRef tex) { return 0; }
        }
        """;

    // --ref の型は host の wire format (snake_case) のまま。user の型 (Game) の
    // member は C# の名前
    [Fact]
    public void RefType_NestedStaticAccess_LowercasePath()
    {
        var source = """
            using static Lub;

            public static class Game
            {
                public static string Run()
                {
                    Config(new ConfigOpts { Width = 640 });
                    Gfx.BeginPass(new PassOpts
                    {
                        Target = Gfx.MainTex,
                        ClearColor = new float[] { 0.5f, 1.0f },
                        Load = (int)Gfx.LoadAction.DontCare,
                    });
                    var rb = Gfx.Readback();
                    Gfx.Size(out var w, out var h);
                    var fmt = (int)Gfx.PixelFormat.Depth24Stencil8;
                    return rb.ReadTexture(Gfx.MainTex) + "," + w + "," + h
                        + "," + fmt + "," + Lub.Pending;
                }
            }
            """;
        var result = Checker.Check([source], null, [LubStub]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);
        var export = IlExport.Export([source], referenceSources: [LubStub]);
        Assert.Empty(export.Diagnostics);
        var names = export.ForeignMethods.Select(m => m.Name).ToList();
        Assert.Contains("lub.config", names);
        Assert.Contains("lub.gfx.begin_pass", names);
        Assert.Contains("lub.gfx.readback", names);
        Assert.Contains("lub.gfx.size", names);
        Assert.EndsWith(".read_texture", export.ForeignMethods.Single(m => m.Receiver == "Readback").Name);
    }

    [Fact]
    public void RefType_QualifiedAccess_LowercasePath()
    {
        var source = """
            public static class Game
            {
                public static int Run()
                {
                    return Lub.Gfx.MainTex.Version
                        + (int)Lub.Gfx.PixelFormat.Rgba8;
                }
            }
            """;
        var result = Checker.Check([source], null, [LubStub]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ConstField_IsInlined()
    {
        const string source = """
            public static class Limits
            {
                public const int MaxHp = 99;
                public const string Name = "hero";
                public static string Describe() { return Name + ":" + MaxHp; }
            }
            """;
        Assert.Equal("hero:99", TestHelper.CompileAndRunVm(source, "Limits.Describe()"));
    }
}
