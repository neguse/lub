namespace TinyCs.Tcs2c.Tests;

// User identifiers become names in both backends with their C# spelling. Spellings
// that collide with names reserved by the generated code stop on both backends with the same shared facts diagnostic before
// IL is produced
public class IdentifiersDifferentialTests
{
    private const string IdentifierProgram = """
        using System;
        namespace Game_Core { public class X { } }
        public class My_Type { public int __hp; }
        public class P
        {
            public void @new() { }
            public static void Main()
            {
                var 速度 = 1;
                Console.WriteLine(速度);
            }
        }
        """;

    [Fact]
    public void ReservedIdentifiers_BothBackendsStopWithDiagnostic()
    {
        foreach (var run in new Func<object>[]
                 {
                     () => Backends.EmitVm([IdentifierProgram], "P"),
                     () => Backends.EmitC([IdentifierProgram], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains("(2,1): error TCS1001: unsupported syntax: UnderscoreNamespaceName(Game_Core)", ex.Message);
            Assert.Contains("(3,1): error TCS1001: unsupported syntax: UnderscoreTypeName(My_Type)", ex.Message);
            Assert.Contains("(3,35): error TCS1001: unsupported syntax: DoubleUnderscoreIdentifier(__hp)", ex.Message);
            Assert.Contains("(6,5): error TCS1001: unsupported syntax: ReservedClassKey(new)", ex.Message);
            Assert.Contains("(9,13): error TCS1001: unsupported syntax: NonAsciiIdentifier(速度)", ex.Message);
        }
    }

    // The C entry name (`tcs_entry_<Class>_<Method>`) and the C names of members also use the C# spelling
    [Fact]
    public void CSharpNames_ReachBothBackends()
    {
        const string program = """
            using System;
            public enum Kind { BigValue, small }
            public class Player
            {
                public int maxHp = 3;
                public int XMLValue { get; set; } = 4;
                public int AddHp(int n) { maxHp += n; return maxHp; }
            }
            public class P
            {
                public static void Main()
                {
                    var p = new Player();
                    Console.WriteLine(p.AddHp(2) + ":" + p.XMLValue + ":" + (int)Kind.small);
                }
            }
            """;
        Assert.Equal("5:4:1", Backends.AssertParity(program, "P"));
        var c = Backends.EmitC([program], "P", lib: true);
        Assert.Contains("tcs_entry_P_Main", c);
        Assert.Contains("AddHp", c);
        Assert.DoesNotContain("add_hp", c);
    }

    // Names that are not C# reserved words can be used for members, types, and locals even if spelled the same as reserved words of other languages (Lua, C)
    [Fact]
    public void NamesLikeOtherLanguageKeywords_ReachBothBackends()
    {
        const string program = """
            using System;
            public record Span(int until);
            public enum Token { nil, then }
            public class local
            {
                public int end_ = 100;
                public int function;
                public int repeat { get; set; }
                public int union;
                public static int typedef = 7;
                public int end() => function + repeat;
                public int elseif(int not) { var auto = not + 1; return auto; }
            }
            public class P
            {
                public static void Main()
                {
                    var end = new local();
                    end.function = 3;
                    end.repeat = 4;
                    end.union = 5;
                    var nil = new Span(6);
                    Console.WriteLine(end.end() + ":" + end.elseif(1) + ":" + nil.until + ":" + (int)Token.then + ":" + end.end_ + ":" + end.union + ":" + local.typedef);
                }
            }
            """;
        Assert.Equal("7:2:6:1:100:5:7", Backends.AssertParity(program, "P"));
    }
}
