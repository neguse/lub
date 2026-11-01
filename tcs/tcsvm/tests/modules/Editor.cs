// module 単位の混在の試験の「C で固定する側」(tcs2c --lib --vm-host)。content (VM) はこの source を --ref に取る。
namespace Ed
{
    // 境界を越える class (content が作り、editor が読み書きする)。field は scalar と、この module の class だけ
    public class Transform
    {
        public float X;
        public float Y;
        public int Layer;
        public Transform Parent;
    }

    public static class Editor
    {
        static int logged;
        static string last = "";
        // shared heap (X-2): C の module が VM の object を frame を跨いで持つ
        static Transform selected;
        static object kept;

        public static void Log(string message)
        {
            logged++;
            last = message;
            System.Console.WriteLine("[editor] " + message);
        }

        public static int Logged() => logged;
        public static string Last() => last;

        // C の関数が VM の object の field を書く
        public static void Nudge(Transform t, float dx)
        {
            t.X += dx;
            t.Layer++;
        }

        public static string Describe(Transform t) => "(" + t.X + "," + t.Y + ") L" + t.Layer;
        public static bool Visible(Transform t, int maxLayer) => t.Layer <= maxLayer;

        public static void Select(Transform t) { selected = t; }
        public static Transform Selected() => selected;
        // C で作った object を VM に返す
        public static Transform Spawn(float x) => new Transform { X = x, Layer = 7 };
        // C が VM の object に参照を書く (write barrier)
        public static void Attach(Transform child, Transform parent) { child.Parent = parent; }
        // content の型の object も object として持てる (reload で張り替わる)
        public static void Keep(object o) { kept = o; }
        public static object Kept() => kept;
    }
}
