namespace TinyCs.Tests;

// T222: `is` / switch 型判定は C# では「T またはその派生」(il-spec §9)。
// getmetatable の完全一致比較だと派生インスタンスへの `is Base` が偽になる。
public class InheritanceTypeTestTests
{
    [Fact]
    public void IsBase_OnDerivedInstance_True()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Animal { }
            public class Dog : Animal { }
            public class T
            {
                public static string Test()
                {
                    object d = new Dog();
                    return $"{d is Animal}|{d is Dog}";
                }
            }
            """, "T.Test()");
        Assert.Equal("true|true", result);
    }

    [Fact]
    public void IsBase_OnGrandchildInstance_True()
    {
        var result = TestHelper.TranspileAndRun("""
            public class A { }
            public class B : A { }
            public class C : B { }
            public class T
            {
                public static string Test()
                {
                    object c = new C();
                    return $"{c is A}|{c is B}|{c is C}";
                }
            }
            """, "T.Test()");
        Assert.Equal("true|true|true", result);
    }

    [Fact]
    public void IsUnrelatedOrNull_False()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Animal { }
            public class Rock { }
            public class T
            {
                public static string Test()
                {
                    object r = new Rock();
                    object? n = null;
                    return $"{r is Animal}|{n is Animal}";
                }
            }
            """, "T.Test()");
        Assert.Equal("false|false", result);
    }

    [Fact]
    public void SwitchExpression_BaseArm_MatchesDerived()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Animal { }
            public class Dog : Animal { }
            public class Cat : Animal { }
            public class T
            {
                public static string Classify(object x) => x switch
                {
                    Cat => "cat",
                    Animal => "animal",
                    _ => "other",
                };
                public static string Test() =>
                    $"{Classify(new Dog())}|{Classify(new Cat())}|{Classify(42)}";
            }
            """, "T.Test()");
        Assert.Equal("animal|cat|other", result);
    }

    [Fact]
    public void IsPattern_WithDesignation_BindsDerivedAsBase()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Animal { public virtual string Name() { return "animal"; } }
            public class Dog : Animal { public override string Name() { return "dog"; } }
            public class T
            {
                public static string Test()
                {
                    object d = new Dog();
                    if (d is Animal a) return a.Name();
                    return "no";
                }
            }
            """, "T.Test()");
        Assert.Equal("dog", result);
    }
}
