namespace TinyCs.Tests;

// src/tcs/shipped.txt is the one list of the files shipped with tcs that scripts and builds read. It must name exactly what the compiler reads.
public class ShippedFilesTests
{
    private static List<string> Shipped(string prefix) =>
        [.. File.ReadAllLines(TestHelper.FindProjectFile("src/tcs/shipped.txt"))
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line.Split(' ')[0][prefix.Length..])];

    [Fact]
    public void Runtime_IsWhatTheCBackendEmbeds()
    {
        Assert.Equal(TcsC.CEmitter.RuntimeFileNames().Order(StringComparer.Ordinal), Shipped("runtime/").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Library_IsWhatTheHostHas()
    {
        var library = Shipped("lib/");
        Assert.Equal(["TinySystem/Linq.cs", "TinySystem/ValueTuple.cs"], library);
        foreach (var path in library) Assert.NotNull(TcsFiles.Shipped("lib/" + path));
    }

    [Fact]
    public void Sources_Exist()
    {
        foreach (var line in File.ReadAllLines(TestHelper.FindProjectFile("src/tcs/shipped.txt")))
        {
            if (line.StartsWith('#')) continue;
            TestHelper.FindProjectFile(line.Split(' ')[1]);
        }
    }
}
