using System.Collections.Generic;

namespace Eng
{
    public class Opts { public int W; public float Scale = 1f; public string Title; }
    public class Ev { public int Kind; public float X; }
    // host の resource の handle (host は object の host_value に番号を持つ)
    public class Tex { public int Version; }
    // stub の継承 (基底の field が先) と、host の data class の instance method
    public class Shape { public int Kind; }
    public class Circle : Shape
    {
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
        // out の引数 (host は戻り値の葉と out の葉を順に rets に書く)
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
}
