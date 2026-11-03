namespace Eng
{
    public class Opts { public int W; public float Scale = 1f; public string Title; }
    public class Ev { public int Kind; public float X; }
    public static class Gfx
    {
        public static int MainTex;
        public static void Begin(Opts o) { }
        public static int Add(int a, int b) => 0;
        public static float Len(float x, float y) => 0;
        public static string Name() => "";
    }
}
