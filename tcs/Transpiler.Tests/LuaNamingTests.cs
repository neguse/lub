namespace TinyCs.Tests;

// Lua 出力の名前規則 (LuaNaming)。user の型の member は C# の識別子のまま
// (Lua の予約語だけ `end_`)、参照専用型 (--ref、host) は snake_case / UPPER_SNAKE
// に写し、小文字パスで参照する。
public class LuaNamingTests
{
    [Theory]
    [InlineData("BeginPass", "BeginPass")]
    [InlineData("hp", "hp")]
    [InlineData("_maxHp", "_maxHp")]
    [InlineData("XMLValue", "XMLValue")]
    [InlineData("RGBA8", "RGBA8")]
    [InlineData("End", "End")]
    [InlineData("end", "end_")]
    [InlineData("local", "local_")]
    [InlineData("global", "global_")]
    public void UserMember_KeepsCSharpName(string csharp, string lua)
    {
        Assert.Equal(lua, LuaNaming.UserMember(csharp));
    }

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
        Assert.Equal(lua, LuaNaming.Member(csharp));
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
        Assert.Equal(lua, LuaNaming.Const(csharp));
    }

    [Fact]
    public void UserMembers_EmitCSharpNames()
    {
        var result = TestHelper.TranspileAndRun("""
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
            """,
            "Game.Run() .. ',' .. Player.new().Hp .. ',' .. Player.new().maxHp .. ',' .. Player.Twice(2)");
        Assert.Equal("27,3,10,4", result);
    }

    [Fact]
    public void CustomProperty_AccessorsKeepPropertyName()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Box
            {
                private int _w;
                public int Width
                {
                    get { return _w; }
                    set { _w = value * 2; }
                }
            }
            """,
            "(function() local b = Box.new() b:set_Width(4) return b:get_Width() .. ',' .. b._w end)()");
        Assert.Equal("8,8", result);
    }

    [Fact]
    public void EnumMembers_EmitCSharpNames()
    {
        var result = TestHelper.TranspileAndRun("""
            public enum Direction { Up, Down, LeftSide, RIGHT }

            public class Nav
            {
                public static int GetDir() { return (int)Direction.LeftSide; }
            }
            """,
            "Nav.GetDir() .. ',' .. Direction.LeftSide .. ',' .. Direction.RIGHT");
        Assert.Equal("2,2,3", result);
    }

    [Fact]
    public void Record_PositionalFieldsKeepCSharpNames()
    {
        var result = TestHelper.TranspileAndRun("""
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
            """,
            "Geo.Sum() .. ',' .. Point.new(3, 4).PosX .. ',' .. Point.new(3, 4).posY");
        Assert.Equal("12,3,4", result);
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
            result.EnumTypes.Single(e => e.Name == "Kind").Members.Select(m => m.Name));
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

    private const string LubRuntime = """
        lub = { gfx = { RGBA8 = 1, DEPTH24_STENCIL8 = 10, DONT_CARE = 3 } }
        lub.gfx.main_tex = { version = 7 }
        local calls = {}
        lub.config = function(opts) calls[#calls + 1] = "config:" .. tostring(opts.width) end
        lub.gfx.begin_pass = function(opts)
          calls[#calls + 1] = "pass:" .. tostring(opts.target.version)
            .. ":" .. table.concat(opts.clear_color, "/") .. ":" .. tostring(opts.load)
        end
        lub.gfx.readback = function()
          return { read_texture = function(self, tex) return tex.version * 2 end }
        end
        lub.gfx.size = function() return 320, 240 end
        function calls_text() return table.concat(calls, ";") end
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
        var result = Transpiler.TranspileWithDiagnostics([source], null, [LubStub]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);

        var script = $"{LubRuntime}\n{result.Lua}\nprint(Game.Run() .. '|' .. calls_text())";
        var output = TestHelper.RunLua(script).Trim();
        Assert.Equal("14,320,240,10,pending|config:640;pass:7:0.5/1.0:3", output);
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
        var result = Transpiler.TranspileWithDiagnostics([source], null, [LubStub]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        var script = $"{LubRuntime}\n{result.Lua}\nprint(Game.Run())";
        Assert.Equal("8", TestHelper.RunLua(script).Trim());
    }

    [Fact]
    public void ConstField_IsInlined()
    {
        var result = TestHelper.TranspileAndRun("""
            public static class Limits
            {
                public const int MaxHp = 99;
                public const string Name = "hero";
                public static string Describe() { return Name + ":" + MaxHp; }
            }
            """,
            "Limits.Describe()");
        Assert.Equal("hero:99", result);
    }

    [Fact]
    public void SameLuaName_InOneType_IsError()
    {
        var source = """
            public class Dup
            {
                public int end;
                public int end_;
            }
            """;
        var result = Transpiler.TranspileWithDiagnostics([source]);
        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            e => e.Contains("'end' and 'end_' both map to Lua 'end_'"));
    }
}
