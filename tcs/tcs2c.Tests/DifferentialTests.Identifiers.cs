namespace TinyCs.Tcs2c.Tests;

// user の識別子は C# の綴りのまま両 backend の名前になる (q.md Q16)。生成
// コードの予約と重なる綴りは、両 backend とも shared facts の同じ診断で IL を作る
// 前に止まる
public partial class DifferentialTests
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

    // C の entry 名 (`tcs_entry_<Class>_<Method>`) と member の C 名も C# の綴り
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

    // C# の予約語でない名前は、他の言語 (Lua、C) の予約語と同じ綴りでも member、型、local に使える
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
