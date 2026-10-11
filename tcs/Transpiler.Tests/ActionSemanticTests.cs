namespace TinyCs.Tests;

public class ActionSemanticTests
{
    [Fact]
    public void Action_NoArgs_Invoke()
    {
        var result = TestHelper.TranspileAndRun("""
            using System;
            public class T
            {
                public static int Test()
                {
                    int x = 0;
                    Action a = () => { x = 42; };
                    a();
                    return x;
                }
            }
            """, "T.Test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void Action_WithArg_Callback()
    {
        var result = TestHelper.TranspileAndRun("""
            using System;
            public class T
            {
                public static void RunAction(Action<int> action, int value)
                {
                    action(value);
                }

                public static int Test()
                {
                    int result = 0;
                    RunAction(x => { result = x * 2; }, 21);
                    return result;
                }
            }
            """, "T.Test()");
        Assert.Equal("42", result);
    }
}
