public class Options { public int? Version; public float[] Data; }
public class Resource { public int Version; }
public static class Api
{
    public enum Mode { Fast = 3 }
    public static Resource Current;
    public static float Read(Options options, Mode mode, int? version = null) { return -1; }
    public static void Poll(out string topic, out string payload) { topic = null; payload = null; }
}
