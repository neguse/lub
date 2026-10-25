namespace TinyCs.Tests;

// generic method の method group は、型引数の既定値を足して呼ぶ closure になる
// (C を含む 3 経路は DifferentialTests.GenericRecordStruct が押さえる)
public class GenericDefaultTests
{
    [Fact]
    public void GenericMethodGroup_PassesTypeArgumentDefault()
    {
        var result = TestHelper.TranspileAndRun("""
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
