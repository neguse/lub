// Io.Remove: file と空のディレクトリを消し、無いものは false。
using Lub;

public static class TestIoRemove
{
    public static void OnInit()
    {
        const string dir = "tests/cs/.lub/io_remove";
        Io.SaveText(dir + "/a.txt", "a");
        Check.That(Io.Stat(dir + "/a.txt") != null, "the file was not saved");
        Check.That(Io.Remove(dir + "/a.txt"), "Remove of a file returned false");
        Check.That(Io.Stat(dir + "/a.txt") == null, "the file is still there");
        Check.That(!Io.Remove(dir + "/a.txt"), "Remove of a missing file returned true");
        Check.That(Io.Remove(dir), "Remove of an empty directory returned false");
        Check.Pass();
    }

    public static void OnFrame(float dt) { }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
