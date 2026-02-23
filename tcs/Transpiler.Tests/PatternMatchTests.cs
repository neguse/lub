namespace TinyCs.Tests;

public class PatternMatchTests
{
    // T71: Declaration pattern (is Type name)
    [Fact]
    public void DeclarationPattern_InIf()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Animal { public int Legs = 4; }
            public class Dog : Animal { public int Legs = 4; }
            public class T
            {
                public static int Test()
                {
                    Animal a = new Dog();
                    if (a is Dog d)
                    {
                        return d.Legs;
                    }
                    return 0;
                }
            }
            """, "T.Test()");
        Assert.Equal("4", result);
    }

    [Fact]
    public void DeclarationPattern_NoMatch()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Animal { }
            public class Dog : Animal { }
            public class Cat : Animal { }
            public class T
            {
                public static int Test()
                {
                    Animal a = new Cat();
                    if (a is Dog d)
                    {
                        return 1;
                    }
                    return 0;
                }
            }
            """, "T.Test()");
        Assert.Equal("0", result);
    }

    // T72: Relational pattern (is > 0)
    [Fact]
    public void RelationalPattern_GreaterThan()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    int x = 5;
                    return x is > 0;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void RelationalPattern_LessThanOrEqual()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    int x = 10;
                    return x is <= 10;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    // T73: and/or pattern
    [Fact]
    public void AndPattern_Range()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    int x = 50;
                    return x is > 0 and < 100;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void OrPattern_Values()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    int x = 2;
                    return x is 1 or 2 or 3;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void OrPattern_NoMatch()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static bool Test()
                {
                    int x = 5;
                    return x is 1 or 2 or 3;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("false", result);
    }
}
