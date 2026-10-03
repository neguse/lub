namespace TinyCs.Tests;

public class LambdaTests
{
    [Fact]
    public void SimpleLambda_Expression()
    {
        var result = TestHelper.TranspileAndRun("""
            using System;

            public class Fn
            {
                public static int Apply(Func<int, int> f, int x)
                {
                    return f(x);
                }

                public static int Test()
                {
                    return Apply(x => x * 2, 21);
                }
            }
            """,
            "Fn.test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void ParenthesizedLambda()
    {
        var result = TestHelper.TranspileAndRun("""
            using System;

            public class Fn
            {
                public static int Apply(Func<int, int, int> f, int a, int b)
                {
                    return f(a, b);
                }

                public static int Test()
                {
                    return Apply((a, b) => a + b, 10, 32);
                }
            }
            """,
            "Fn.test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void Lambda_AsVariable()
    {
        var result = TestHelper.TranspileAndRun("""
            using System;

            public class Fn
            {
                public static int Test()
                {
                    Func<int, int> doubler = x => x * 2;
                    return doubler(21);
                }
            }
            """,
            "Fn.test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void DelegateField_DirectInvocation_InstanceStaticAndUnqualified()
    {
        // delegate 型 field / auto property を `obj.F()` / `Cls.F()` / 非修飾
        // `F()` で直接呼ぶ (IL 経路: IlDynCall(IlField))
        var result = TestHelper.TranspileAndRun("""
            using System;
            public class Btn
            {
                public Action OnClick;
                public Func<int, int> Map { get; set; }
                public static Func<string> Describe;
                public void Fire() { OnClick(); }
                public string Name() { return Describe(); }
            }
            public static class T
            {
                public static string Test()
                {
                    var b = new Btn();
                    var n = 0;
                    b.OnClick = () => { n++; };
                    b.Map = x => x * 2;
                    Btn.Describe = () => "btn";
                    b.Fire();
                    b.OnClick();
                    return n + ":" + b.Map(21) + ":" + Btn.Describe() + ":" + b.Name();
                }
            }
            """, "T.test()");
        Assert.Equal("2:42:btn:btn", result);
    }
}
