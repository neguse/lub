namespace TinyCs.Tests;

// A method group of a generic method becomes a closure that calls it with the default values of the type arguments added
// (the three paths including C are covered by DifferentialTests.GenericRecordStruct)
public class GenericDefaultTests
{
    [Fact]
    public void GenericMethodGroup_PassesTypeArgumentDefault()
    {
        var result = TestHelper.CompileAndRunVm("""
            using System;
            using System.Collections.Generic;
            public static class G
            {
                public static T Pick<T>(List<T> list, int i) => i < list.Count ? list[i] : default!;
                public static int Test()
                {
                    Func<List<int>, int, int> pick = Pick<int>;
                    return pick(new List<int> { 4 }, 0) + pick(new List<int>(), 3) + 1;
                }
            }
            """, "G.Test()");
        Assert.Equal("5", result);
    }
}
