namespace TinyCs.Tests;

public class IlObjectValueTests
{
    [Fact]
    public void InterfaceContractsAndImplementedTypesAreExported()
    {
        var program = IlExport.Export(["""
            public interface Target { int Read(); }
            public class Body : Target { public int Read() => 7; }
            """]);
        Assert.Empty(program.Diagnostics);
        var iface = program.Classes.Single(c => c.IsInterface);
        Assert.Equal("Target", iface.Name);
        Assert.True(iface.Methods.Single().IsAbstract);
        Assert.Contains("Target", program.Classes.Single(c => c.Name == "Body").Interfaces);
    }

    [Fact]
    public void ReferenceCastKeepsDestinationType()
    {
        var program = IlExport.Export(["""
            public class Read { public static float[] Values(object value) => (float[])value; }
            """]);
        var cast = Assert.IsType<IlRefCast>(Assert.IsType<IlReturn>(
            program.Classes.Single().Methods.Single().Body!.Stats.Single()).Value);
        Assert.Equal("float[]", cast.TargetType);
    }
}
