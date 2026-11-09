public class BaseOptions { public int? Version; public int Scale(int n) { return 0; } }
public class Options : BaseOptions { public float[] Data; }
public class Resource { public int Version; public int Get(int index) { return 0; } public void Touch() { } public virtual int Peek(int i) { return 0; } }
public class Handle { }
public static class Api
{
    public enum Mode { Fast = 3 }
    public static Resource Current;
    public static Handle Body(int n) { return null; }
    public static float Read(Options options, Mode mode, int? version = null) { return -1; }
    public static void Poll(out string topic, out string payload) { topic = null; payload = null; }
    public static void Discard(out int count, out string text, out float dt) { count = 0; text = null; dt = 0; }
}
// host と C の struct で渡す値: 入れ子の struct、bool と enum の field、record struct、T?
public enum Layer { Back, Front = 5 }
public struct Vec2 { public float X; public float Y; }
public struct Box2 { public Vec2 Min; public Vec2 Max; public bool Solid; public Layer Layer; }
public readonly record struct Hit(int Id, float T);
public static class Phys
{
    public static float Dot(Vec2 a, Vec2 b) { return 0; }
    public static Vec2 Center(Box2 b) { return default; }
    public static void Size(Box2 b, out Vec2 size, out bool solid) { size = default; solid = false; }
    public static bool Pick(int id, out Hit hit) { hit = default; return false; }
    public static bool Probe(int x, out float y) { y = 0; return false; }
    public static Hit? Cast(Vec2 from, float limit) { return null; }
    public static int Score(Hit? h, int bias) { return 0; }
}
