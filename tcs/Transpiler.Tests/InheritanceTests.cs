namespace TinyCs.Tests;

public class InheritanceTests
{
    [Fact]
    public void BaseClass_MethodInherited()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Animal
            {
                public string Name;

                public Animal(string name) { this.Name = name; }

                public string Speak() { return this.Name; }
            }

            public class Dog : Animal
            {
                public Dog(string name) : base(name) { }
            }
            """, """
            -- base() is not yet supported in ctor, so test method inheritance
            (function()
              local d = Dog.new("Rex")
              return d:Speak()
            end)()
            """);
        // Constructor chaining (base()) is not yet implemented,
        // so Dog.new sets fields via its own body. Adjust test accordingly.
        Assert.Equal("Rex", result);
    }

    [Fact]
    public void BaseClass_MethodOverride()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Shape
            {
                public int Area() { return 0; }
            }

            public class Square : Shape
            {
                public int Side;

                public Square(int side) { this.Side = side; }

                public new int Area() { return this.Side * this.Side; }
            }
            """, """
            Square.new(5):Area()
            """);
        Assert.Equal("25", result);
    }

    [Fact]
    public void BaseMethodCall_CallsParentImplementation()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Animal
            {
                public string Name;

                public Animal(string name) { this.Name = name; }

                public string Speak() { return this.Name + " speaks"; }
            }

            public class Dog : Animal
            {
                public Dog(string name) : base(name) { }

                public new string Speak()
                {
                    return base.Speak() + " and barks";
                }
            }
            """, """
            Dog.new("Rex"):Speak()
            """);

        Assert.Equal("Rex speaks and barks", result);
    }

    // T149: 継承 link は宣言順・ファイル順に依存しない
    [Fact]
    public void Inheritance_DerivedDeclaredBeforeBase_SameFile()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Dog : Animal
            {
                public string Bark() => Speak() + "!";
            }
            public class Animal
            {
                public string Speak() => "sound";
            }
            """, """
            Dog.new():Bark()
            """);

        Assert.Equal("sound!", result);
    }

    [Fact]
    public void Inheritance_MultiLevelChain_ReversedDeclarationOrder()
    {
        var result = TestHelper.TranspileAndRun("""
            public class C : B
            {
                public int Three() => Two() + 1;
            }
            public class B : A
            {
                public int Two() => One() + 1;
            }
            public class A
            {
                public int One() => 1;
            }
            """, """
            C.new():Three()
            """);

        Assert.Equal("3", result);
    }

    [Fact]
    public void Inheritance_DerivedInEarlierFileThanBase()
    {
        var lua = Transpiler.Transpile([
            """
            public class Dog : Animal
            {
                public string Bark() => Speak() + "!";
            }
            """,
            """
            public class Animal
            {
                public string Speak() => "sound";
            }
            """]);
        var result = TestHelper.RunLua(lua + "\nprint(Dog.new():Bark())").Trim();

        Assert.Equal("sound!", result);
    }
}
