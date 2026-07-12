namespace TinyCs.Tests;

// Lua joins a statement that starts with `(` onto a preceding statement that
// ends in a callable expression: `local b = t[k]` + `(function() ... end)()`
// parses as `t[k](function() ... end)()`. Statements emitted as IIFEs
// (List.Clear, conditional access, ...) must stay separate statements.
public class StatementSeparatorTests
{
    [Fact]
    public void ListClear_AfterIndexerLocal_RunsAsSeparateStatement()
    {
        var output = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;

            var buckets = new Dictionary<string, List<int>>();
            buckets["x"] = new List<int> { 1, 2, 3 };
            var b = buckets["x"];
            b.Clear();
            var count = b.Count;
            """, "count");

        Assert.Equal("0", output);
    }

    [Fact]
    public void ConditionalAccessStatement_AfterIndexerLocal_RunsAsSeparateStatement()
    {
        var output = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;

            var list = new List<Counter> { new Counter() };
            var c = list[0];
            c?.Bump();
            var n = c.N;

            public class Counter
            {
                public int N;
                public void Bump() { N = N + 1; }
            }
            """, "n");

        Assert.Equal("1", output);
    }

    [Fact]
    public void ListClear_AfterCallStatement_RunsAsSeparateStatement()
    {
        var output = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;

            var log = new List<int>();
            log.Add(1);
            log.Clear();
            var count = log.Count;
            """, "count");

        Assert.Equal("0", output);
    }

    [Fact]
    public void ConditionalAccessStatement_InsideLambdaBlock_AfterIndexerLocal()
    {
        var output = TestHelper.TranspileAndRun("""
            using System;
            using System.Collections.Generic;

            Action tick = () =>
            {
                var items = new List<Counter> { new Counter() };
                var c = items[0];
                c?.Bump();
                Result.Total = c.N;
            };
            tick();
            var total = Result.Total;

            public static class Result
            {
                public static int Total;
            }

            public class Counter
            {
                public int N;
                public void Bump() { N = N + 1; }
            }
            """, "total");

        Assert.Equal("1", output);
    }
}
