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

    // VM の host の ABI の塊で境界を越える struct (field は scalar と、それだけの struct)
    public struct Span
    {
        public float Lo;
        public float Hi;
        public bool Open;
    }

    // X-4: content の class がこの interface を実装し、C の module が呼ぶ
    public interface ITool
    {
        string Name();
        int Use(int x);
    }

    public class Ruler : ITool
    {
        public string Name() => "ruler";
        public int Use(int x) => x;
    }

    public static class Editor
    {
        static System.Collections.Generic.List<ITool> tools = new System.Collections.Generic.List<ITool> { new Ruler() };

        public static void Register(ITool tool) { tools.Add(tool); }
        // C の module が delegate を返し、VM が呼ぶ
        public static System.Func<int, int> Scaler(int k) => x => x * k;
        public static System.Func<string, string> Shout() => s => s + "!";
        // C の delegate を VM の runtime の関数 (List.Sort / FindIndex / RemoveAll) に渡す
        public static System.Comparison<int> Descending() => (a, b) => b - a;
        public static System.Predicate<int> IsEven() => x => x % 2 == 0;

        public static int ToolCount() => tools.Count;
        public static ITool ToolAt(int i) => tools[i];

        public static string RunTools(int x)
        {
            var s = "";
            foreach (var t in tools) s += t.Name() + "=" + t.Use(x) + " ";
            return s;
        }

        static int logged;
        static string last = "";
        // shared heap (X-2): C の module が VM の object を frame を跨いで持つ
        static Transform selected;
        static object kept;
        // X-4: content の delegate を C の module が持って呼ぶ
        static System.Action<int> onTick;
        static System.Func<string, string> decorate;

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
        // 型の番号 (object-model §10.6): C の型の test が VM で作った object を調べる
        public static string Classify(object o)
        {
            if (o is Transform t) return "transform L" + t.Layer;
            if (o is ITool tool) return "tool " + tool.Name();
            return "other";
        }
        // C で作った object を VM に返す
        public static Transform Spawn(float x) => new Transform { X = x, Layer = 7 };
        // C が VM の object に参照を書く (write barrier)
        public static void Attach(Transform child, Transform parent) { child.Parent = parent; }
        // content の型の object も object として持てる (reload で張り替わる)
        public static void Keep(object o) { kept = o; }
        public static object Kept() => kept;

        // struct の値と戻り値、out と T? (VM は C の layout の塊と pointer で渡す)
        public static float Width(Span s) => s.Hi - s.Lo;
        public static Span Widen(Span s, float d) => new Span { Lo = s.Lo - d, Hi = s.Hi + d, Open = !s.Open };
        public static bool Find(int key, out string name, out Span span)
        {
            if (key <= 0)
            {
                name = "";
                span = default;
                return false;
            }
            name = "span" + key;
            span = new Span { Lo = key, Hi = key * 2, Open = true };
            return true;
        }
        public static int? Half(int x)
        {
            if (x % 2 != 0) return null;
            return x / 2;
        }

        public static void OnTick(System.Action<int> f) { onTick = f; }
        public static void Decorate(System.Func<string, string> f) { decorate = f; }

        public static string Tick(int n)
        {
            if (onTick != null) onTick(n);
            return decorate == null ? "" : decorate("tick" + n);
        }
    }
}
