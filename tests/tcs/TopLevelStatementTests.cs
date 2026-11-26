namespace TinyCs.Tests;

public class TopLevelStatementTests
{
    [Fact]
    public void TopLevelStatement_ConsoleWriteLine()
    {
        var result = TestHelper.CompileAndRunVmProgram(["""
            using System;

            var answer = 40 + 2;
            Console.WriteLine(answer);
            """]);

        Assert.Equal("42", result);
    }

    [Fact]
    public void TopLevelStatement_CanUseClassDeclaredLater()
    {
        var result = TestHelper.CompileAndRunVmProgram(["""
            using System;

            var player = new Player("Rex");
            Console.WriteLine(player.Name);

            public class Player
            {
                public string Name;

                public Player(string name)
                {
                    Name = name;
                }
            }
            """]);

        Assert.Equal("Rex", result);
    }

    [Fact]
    public void TopLevelStatement_CanUseClassFromAnotherFile()
    {
        var result = TestHelper.CompileAndRunVmProgram([
            """
            using System;

            var player = new Player("Mina");
            Console.WriteLine(player.Name);
            """,
            """
            public class Player
            {
                public string Name;

                public Player(string name)
                {
                    Name = name;
                }
            }
            """
        ]);

        Assert.Equal("Mina", result);
    }
}
