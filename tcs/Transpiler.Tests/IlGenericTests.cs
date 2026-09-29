namespace TinyCs.Tests;

public class IlGenericTests
{
    [Fact]
    public void RecursiveClosedReferencesShareOneSpecialization()
    {
        var program = IlExport.Export(["""
            public class Node<T> { public T Value; public Node<T> Next; }
            public class Use { public static Node<int> Root = new Node<int>(); }
            """], specializeGenerics: true);
        Assert.Empty(program.Diagnostics);
        Assert.Equal(2, program.Classes.Length);
        var node = program.Classes.Single(c => c.Name != "Use");
        Assert.Equal(node.Name, node.Fields[1].Type);
    }

    [Fact]
    public void ClosedClassesSpecializeFieldsMethodsAndBaseTypes()
    {
        var program = IlExport.Export(["""
            public class Box<T> {
                public T Value;
                public Box(T value) { Value = value; }
                public T Read() => Value;
            }
            public class Derived<T> : Box<T> {
                public Derived(T value) : base(value) {}
            }
            public class Use {
                public static int Read() => new Derived<int>(7).Read();
                public static float Other() => new Box<float>(1.5f).Read();
            }
            """], specializeGenerics: true);
        Assert.Empty(program.Diagnostics);
        Assert.DoesNotContain(program.Classes, c => c.Name is "Box" or "Derived");
        var boxes = program.Classes.Where(c => c.Fields.Length == 1).ToArray();
        Assert.Equal(2, boxes.Length);
        Assert.Contains(boxes, c => c.Fields[0].Type == "int");
        Assert.Contains(boxes, c => c.Fields[0].Type == "float");
        var derived = program.Classes.Single(c => c.BaseName != null);
        Assert.Equal(boxes.Single(c => c.Fields[0].Type == "int").Name, derived.BaseName);
        Assert.All(boxes, c => Assert.Equal(c.Fields[0].Type, c.Methods.Single().ReturnType));
    }
}
