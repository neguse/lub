namespace TinyCs.Tests;

// float の key の Dictionary はサブセット外 (TCS1001 UnsupportedDictionaryKey(float))。HashSet<float> と同じく作る地点で止める
[Collection(ConsoleCollection.Name)]
public class DictFloatKeyTests
{
    private const string Source = """
        using System.Collections.Generic;
        public static class P
        {
            public static int Test()
            {
                var d = new Dictionary<float, int>();
                Dictionary<float, string> t = new();
                var i = new Dictionary<int, float>();
                return d.Count + t.Count + i.Count;
            }
        }
        """;

    [Fact]
    public void Check_FloatKeyExits1()
    {
        var dir = Directory.CreateTempSubdirectory("tcs_dict_float_key_");
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
                "input.cs(6,17): error TCS1001: unsupported syntax: UnsupportedDictionaryKey(float)",
                "input.cs(7,39): error TCS1001: unsupported syntax: UnsupportedDictionaryKey(float)",
            ], errors.Select(e => e[(e.LastIndexOf('/') + 1)..]).ToList());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
