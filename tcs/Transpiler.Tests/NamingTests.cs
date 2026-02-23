namespace TinyCs.Tests;

public class NamingTests
{
    [Fact]
    public void PascalCase_Class_NoWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["public class MyClass { }"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LowerCase_Class_Warning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["public class myClass { }"]);
        Assert.Contains(result.Warnings, w => w.Contains("class 'myClass'") && w.Contains("PascalCase"));
    }

    [Fact]
    public void PascalCase_Method_NoWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class Foo { public void DoStuff() { } }"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LowerCase_Method_Warning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class Foo { public void doStuff() { } }"]);
        Assert.Contains(result.Warnings, w => w.Contains("method 'doStuff'") && w.Contains("PascalCase"));
    }

    [Fact]
    public void PascalCase_PublicField_NoWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class Foo { public int Health; }"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LowerCase_PublicField_Warning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class Foo { public int health; }"]);
        Assert.Contains(result.Warnings, w => w.Contains("public field 'health'") && w.Contains("PascalCase"));
    }

    [Fact]
    public void CamelCase_Parameter_NoWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class Foo { public void Do(int count) { } }"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void PascalCase_Parameter_Warning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class Foo { public void Do(int Count) { } }"]);
        Assert.Contains(result.Warnings, w => w.Contains("parameter 'Count'") && w.Contains("camelCase"));
    }

    [Fact]
    public void CamelCase_Local_NoWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class Foo { public void Do() { int myVar = 1; } }"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void PascalCase_Local_Warning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class Foo { public void Do() { int MyVar = 1; } }"]);
        Assert.Contains(result.Warnings, w => w.Contains("local variable 'MyVar'") && w.Contains("camelCase"));
    }

    [Fact]
    public void PascalCase_Enum_And_Members_NoWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public enum Color { Red, Green, Blue }"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LowerCase_EnumMember_Warning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public enum Color { red, Green }"]);
        Assert.Contains(result.Warnings, w => w.Contains("enum member 'red'") && w.Contains("PascalCase"));
    }

    [Fact]
    public void Interface_I_Prefix_NoWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public interface IMovable { void Move(); }"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Interface_No_I_Prefix_Warning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public interface Movable { void Move(); }"]);
        Assert.Contains(result.Warnings, w => w.Contains("interface 'Movable'") && w.Contains("IPascalCase"));
    }

    [Fact]
    public void PrivateField_Underscore_NoWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class Foo { int _count; }"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void PrivateField_CamelCase_NoWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class Foo { int count; }"]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void PrivateField_PascalCase_Warning()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class Foo { int Count; }"]);
        Assert.Contains(result.Warnings, w => w.Contains("private field 'Count'") && w.Contains("camelCase"));
    }

    [Fact]
    public void Warning_Includes_Location()
    {
        var result = Transpiler.TranspileWithDiagnostics(
            ["public class bad { }"], ["test.cs"]);
        var w = result.Warnings.First(w => w.Contains("class 'bad'"));
        Assert.Contains("test.cs", w);
        Assert.Contains("(1,", w);
    }

    [Fact]
    public void CLI_Shows_Warnings()
    {
        // Verify transpilation still succeeds (warnings don't block)
        var result = Transpiler.TranspileWithDiagnostics([
            "public class bad { public void doIt(int Bad) { int X = 1; } }"]);
        Assert.True(result.Success);
        Assert.True(result.Warnings.Count >= 4); // class, method, param, local
    }
}
