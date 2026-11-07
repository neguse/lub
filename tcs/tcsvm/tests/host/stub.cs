using System.Collections.Generic;

namespace Eng
{
    public class Opts { public int W; public float Scale = 1f; public string Title; }
    public class Ev { public int Kind; public float X; }
    // host の resource の handle (host は object の host_value に番号を持つ)
    public class Tex { public int Version; }
    // host の data class の instance method
    public class Circle
    {
        public int Kind;
        public float R;
        public float Area() => 0;
    }
    public static class Gfx
    {
        public static int MainTex;
        public static void Begin(Opts o) { }
        public static int Add(int a, int b) => 0;
        public static float Len(float x, float y) => 0;
        public static string Name() => "";
        // out の引数 (host は args の pointer の先に書く。rets は戻り値だけ)
        public static void Load(string path, out string? text, out int version) { text = null; version = 0; }
        public static bool Try(int x, out float y) { y = 0; return false; }
        // host_value と、host が作る / 読む List と配列
        public static Tex Make(int handle) => null;
        public static int Handle(Tex t) => 0;
        public static List<int> Range(int n) => null;
        public static int Sum(List<int> xs) => 0;
        public static List<Opts> Many(int n) => null;
        public static int[] Squares(int n) => null;
        public static Circle Ring(float r) => null;
    }
    // host と C の layout で渡す struct (doc/object-model.md §5)。入れ子の struct、bool と enum の field、record struct
    public enum Layer { Back, Front = 5 }
    public struct Vec2 { public float X; public float Y; }
    public struct Box2 { public Vec2 Min; public Vec2 Max; public bool Solid; public Layer Layer; }
    public readonly record struct Hit(int Id, float T);
    // 参照の field を持つ struct は塊にできない (Reject.cs が読み込みの拒否を確かめる)
    public struct Named { public string Name; public float W; }
    public static class Phys
    {
        public static float Dot(Vec2 a, Vec2 b) => 0;
        public static Vec2 Center(Box2 b) => default;
        public static void Size(Box2 b, out Vec2 size, out bool solid) { size = default; solid = false; }
        public static bool Pick(int id, out Hit hit) { hit = default; return false; }
        public static Hit? Cast(Vec2 from, float limit) => null;
        public static int Score(Hit? h, int bias) => 0;
        // host が delegate を呼び、その中で別の struct の host の関数を呼ぶ (入れ子)
        public static Vec2 Apply(System.Func<Vec2, Vec2> f, Vec2 v) => default;
        public static float Weight(Named n) => 0;
    }
}
