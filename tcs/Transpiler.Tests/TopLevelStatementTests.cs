namespace TinyCs.Tests;

public class TopLevelStatementTests
{
    [Fact]
    public void TopLevelStatement_ConsoleWriteLine()
    {
        var lua = Transpiler.Transpile("""
            using System;

            var answer = 40 + 2;
            Console.WriteLine(answer);
            """);

        var result = TestHelper.RunLua(lua).Trim();

        Assert.Equal("42", result);
    }

    [Fact]
    public void TopLevelStatement_CanUseClassDeclaredLater()
    {
        var lua = Transpiler.Transpile("""
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
            """);

        var result = TestHelper.RunLua(lua).Trim();

        Assert.Equal("Rex", result);
    }

    [Fact]
    public void TopLevelStatement_CanUseClassFromAnotherFile()
    {
        var lua = Transpiler.Transpile([
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

        var result = TestHelper.RunLua(lua).Trim();

        Assert.Equal("Mina", result);
    }
}
