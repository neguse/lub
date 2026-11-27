namespace TinyCs.Tests;

// Named sessions (Tcs.Sessions): the requests that the lider player sends and the replies it reads.
public class SessionsTests
{
    private const string V1 = "public static class Game { static int n; public static void Frame() { n += 1; } }";
    private const string V2 = "public static class Game { static int n; public static void Frame() { n += 2; } }";

    // Sends one request the way a host does: arguments, then Call. The reply is what Call writes to standard output
    private static string[] Call(params string[] args)
    {
        var (_, stdout, _) = ConsoleCapture.Run(() => TcsHost.Run(args, Tcs.Sessions.Call, TcsHost.SetDone));
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void OpenUpdateClose_ReplyWithTheKindAndWriteTheModuleAndTheBatch()
    {
        var dir = Directory.CreateTempSubdirectory("tcs-sessions-").FullName;
        try
        {
            var module = Path.Combine(dir, "game.tcsb");
            var batch = Path.Combine(dir, "game.batch");
            string[] open = ["open", "s", "Game", module, batch, "0", "0", "0"];

            Assert.Equal(["module", "revision 1"], Call([.. open, "game.cs", V1]));
            var first = File.ReadAllBytes(module);
            Assert.False(File.Exists(batch));

            Assert.Equal(["batch", "revision 2"], Call("update", "s", "0", "game.cs", V2));
            Assert.NotEqual(first, File.ReadAllBytes(module));
            Assert.True(File.Exists(batch));

            // An edit with a diagnostic keeps the last build
            var failed = Call("update", "s", "0", "game.cs", "public static class Game { public static void Frame() { missing(); } }");
            Assert.Equal("error", failed[0]);
            Assert.Equal("revision 2", failed[1]);
            Assert.StartsWith("error game.cs(1,", failed[2]);

            // A file can be added, and after it is removed its declarations are gone
            const string usesMore = "public static class Game { static int n; public static void Frame() { n += More.Step; } }";
            const string more = "public static class More { public const int Step = 3; }";
            Assert.NotEqual("error", Call("update", "s", "0", "game.cs", usesMore, "more.cs", more)[0]);
            Assert.Equal("error", Call("update", "s", "1", "more.cs")[0]);
            Assert.NotEqual("error", Call("update", "s", "0", "game.cs", V2)[0]);

            Assert.Equal(["closed"], Call("close", "s"));
            Assert.Equal(["error", "revision 0", "error no session named s"], Call("update", "s", "0"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
