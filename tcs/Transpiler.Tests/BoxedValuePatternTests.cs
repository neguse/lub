namespace TinyCs.Tests;

// Type tests on numbers boxed in object, and patterns that read the values of numbers and bools boxed in object, are outside the subset (TCS1001 BoxedValuePattern(T)).
// check exits 1, and tcs vm / tcs c / the analyzer also stop with the same compiler diagnostic. The value is extracted with a cast (`(int)o`)
[Collection(ConsoleCollection.Name)]
public class BoxedValuePatternTests
{
    private const string Source = """
        public static class P
        {
            public static int Test(object o, object b, int? m)
            {
                var r = 0;
                if (o is int x) r += x;
                r += o switch { float => 1, 5 => 2, _ => 0 };
                if (b is true) r++;
                if (o is int and > 3) r++;
                if (b is bool && (bool)b) r += (int)o;
                if (m is int y) r += y;
                return r;
            }
        }
        """;

    [Fact]
    public void Check_BoxedValuePatternExits1()
    {
        var dir = Directory.CreateTempSubdirectory("tcs_boxed_value_pattern_");
        try
        {
            var input = Path.Combine(dir.FullName, "input.cs");
            File.WriteAllText(input, Source);
            var (exitCode, _, stderr) =
                ConsoleCapture.Run(() => Program.Main(["check", input]));
            Assert.Equal(1, exitCode);
            var errors = stderr.Split('\n').Where(l => l.Contains("error ")).Select(l => l.Trim()).ToList();
            Assert.Equal(
            [
                "input.cs(6,18): error TCS1001: unsupported syntax: BoxedValuePattern(int)",
                "input.cs(7,25): error TCS1001: unsupported syntax: BoxedValuePattern(float)",
                "input.cs(7,37): error TCS1001: unsupported syntax: BoxedValuePattern(int)",
                "input.cs(8,18): error TCS1001: unsupported syntax: BoxedValuePattern(bool)",
                "input.cs(9,18): error TCS1001: unsupported syntax: BoxedValuePattern(int)",
            ], errors.Select(e => e[(e.LastIndexOf('/') + 1)..]).ToList());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
