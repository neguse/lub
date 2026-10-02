namespace TinyCs.Tests;

public class IlGameCoreTests
{
    [Fact]
    public void IntegralLookingFloatLiteralsKeepTheirNumericType()
    {
        var program = IlExport.Export(["""
            public class Number {
                public static float Read() => 4294967295f;
            }
            """]);
        var value = Assert.IsType<IlLit>(Assert.IsType<IlReturn>(
            program.Classes.Single().Methods.Single().Body!.Stats.Single()).Value);
        Assert.Equal("float", value.Type);
    }

    [Fact]
    public void ArrayInitializersRetainFixedLengthStorageKind()
    {
        var program = IlExport.Export(["""
            using System.Collections.Generic;
            public class Store {
                public static float[] A() => new float[] { 1, 2 };
                public static List<float> L() => new List<float> { 1, 2 };
            }
            """]);
        var methods = program.Classes.Single().Methods;
        var array = Assert.IsType<IlTable>(Assert.IsType<IlReturn>(methods[0].Body!.Stats[0]).Value);
        var list = Assert.IsType<IlTable>(Assert.IsType<IlReturn>(methods[1].Body!.Stats[0]).Value);
        Assert.True(array.IsArray);
        Assert.False(list.IsArray);
        Assert.Equal("float", array.ElementType);
    }

    [Fact]
    public void OptionalArgumentsAreExportedForConstructorsAndMethods()
    {
        var program = IlExport.Export(["""
            public class Point {
                public Point(float x = 2) {}
                public static int Read(int value = 3) => value;
            }
            """]);
        var cls = program.Classes.Single();
        Assert.Equal("2", Assert.IsType<IlLit>(cls.Ctor!.ParameterDefaults[0]).LuaText);
        Assert.Equal("3", Assert.IsType<IlLit>(cls.Methods[0].ParameterDefaults[0]).LuaText);
    }
}
