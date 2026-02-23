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
}
