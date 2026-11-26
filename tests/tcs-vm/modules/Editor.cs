// The "side fixed in C" of the per-module mixing test (tcs2c --lib --vm-host). content (VM) takes this source as --ref.
namespace Ed
{
    // A class that crosses the boundary (content creates it, editor reads and writes it). Fields are only scalars and classes of this module
    public class Transform
    {
        public float X;
        public float Y;
        public int Layer;
        public Transform Parent;
    }

    // A struct that crosses the boundary as a block in the VM host ABI (fields are scalars and structs made only of those)
    public struct Span
    {
        public float Lo;
        public float Hi;
        public bool Open;
    }

    // X-4: a content class implements this interface and the C module calls it
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
        // The C module returns a delegate and the VM calls it
        public static System.Func<int, int> Scaler(int k) => x => x * k;
        public static System.Func<string, string> Shout() => s => s + "!";
        // Passes a C delegate to VM runtime functions (List.Sort / FindIndex / RemoveAll)
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
        // shared heap (X-2): the C module holds VM objects across frames
        static Transform selected;
        static object kept;
        // X-4: the C module holds and calls a content delegate
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

        // A C function writes a field of a VM object
        public static void Nudge(Transform t, float dx)
        {
            t.X += dx;
            t.Layer++;
        }

        public static string Describe(Transform t) => "(" + t.X + "," + t.Y + ") L" + t.Layer;
        public static bool Visible(Transform t, int maxLayer) => t.Layer <= maxLayer;

        public static void Select(Transform t) { selected = t; }
        public static Transform Selected() => selected;
        // Type numbers (object-model §10.6): a C type test inspects an object created in the VM
        public static string Classify(object o)
        {
            if (o is Transform t) return "transform L" + t.Layer;
            if (o is ITool tool) return "tool " + tool.Name();
            return "other";
        }
        // Returns an object created in C to the VM
        public static Transform Spawn(float x) => new Transform { X = x, Layer = 7 };
        // C writes a reference into a VM object (write barrier)
        public static void Attach(Transform child, Transform parent) { child.Parent = parent; }
        // Objects of content types can also be held as object (repointed on reload)
        public static void Keep(object o) { kept = o; }
        public static object Kept() => kept;

        // Struct values and return values, out and T? (the VM passes C-layout blocks and pointers)
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
