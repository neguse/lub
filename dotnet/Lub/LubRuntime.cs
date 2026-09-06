// .NET 実行の facade (Lub.g.cs、生成物) が使う手書きの土台。C API の基本型、
// 呼び出しの間だけ生きる memory (arena)、文字列と view の変換、callback の
// 箱、エラーの例外化。生成物は C API への詰め替えだけを持つ。
#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>C API が LUB_ERROR を返した (lub_last_error の message)。host が
/// frame 境界で捕まえて log し、次の frame へ進む。</summary>
public sealed class LubException : Exception
{
    public LubException(string message) : base(message)
    {
    }
}

internal static unsafe partial class LubNative
{
    internal const int LUB_OK = 0;
    internal const int LUB_ERROR = 1;
    internal const int LUB_NOT_FOUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubStr
    {
        public byte* ptr;
        public int len;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubView
    {
        public byte* ptr;
        public int len;
        public int frame;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LubBinding
    {
        public LubStr name;
        public int handle;
        public float* values;
        public int count;
    }

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte* lub_last_error(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_frame_index(void* ctx);
}

internal static unsafe class LubRuntime
{
    internal const string LibName = "lub";

    /// <summary>host が create した context。API はこれに対して呼ぶ。</summary>
    internal static void* Ctx;

    private static bool resolverSet;

    // 共有 library の場所: 環境変数 LUB_NATIVE_LIB (full path) が最優先。
    // 無ければ既定の探索 (実行ファイルの隣、system の library path)。
    // 最初の P/Invoke より前に host (Lub.Run) が呼ぶ。
    internal static void EnsureNative()
    {
        if (resolverSet) return;
        resolverSet = true;
        NativeLibrary.SetDllImportResolver(typeof(LubRuntime).Assembly, (name, asm, path) =>
        {
            if (name != LibName) return IntPtr.Zero;
            var env = Environment.GetEnvironmentVariable("LUB_NATIVE_LIB");
            if (!string.IsNullOrEmpty(env)) return NativeLibrary.Load(env);
            return IntPtr.Zero;
        });
    }

    internal static void Check(int status, string fn)
    {
        if (status == LubNative.LUB_OK) return;
        var msg = Ctx == null ? "no context" : Str(LubNative.lub_last_error(Ctx));
        throw new LubException(fn + ": " + msg);
    }

    // ------------------------------------------------------------ strings

    internal static string Str(LubNative.LubStr s) =>
        s.ptr == null || s.len <= 0 ? "" : Encoding.UTF8.GetString(s.ptr, s.len);

    internal static string Str(byte* z) => z == null ? "" : Marshal.PtrToStringUTF8((IntPtr)z) ?? "";

    internal static string? StrOrNull(LubNative.LubStr s) => s.ptr == null ? null : Str(s);

    // 64 bit の bit mask。Lua 面と同じ 16 桁の hex 文字列 (0x 付きも読む)。
    internal static ulong Bits(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var t = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return Convert.ToUInt64(t, 16);
    }

    internal static string BitsStr(ulong v) => v.ToString("x16");

    // ------------------------------------------------------------- views

    internal static void CheckView(int frame)
    {
        if (Ctx != null && LubNative.lub_frame_index(Ctx) != frame)
            throw new LubException("stale view (returned in an earlier frame)");
    }

    internal static Bytes? View(LubNative.LubView v) =>
        v.ptr == null ? null : new Bytes(v.ptr, v.len, v.frame);

    internal static LubNative.LubView ViewOf(Bytes? b)
    {
        LubNative.LubView v = default;
        if (b == null) return v;
        CheckView(b.Frame);
        v.ptr = b.Ptr;
        v.len = b.Length;
        v.frame = b.Frame;
        return v;
    }

    internal static int ResourceVersion(int handle)
    {
        LubNative.LubStr key = default;
        int version = 0;
        LubNative.lub_gfx_resource_info(Ctx, handle, &key, &version);
        return version;
    }

    // ------------------------------------------------------- lists (out)

    internal static List<float> FloatList(float* p, int n)
    {
        var l = new List<float>(n);
        for (var i = 0; i < n; i++) l.Add(p[i]);
        return l;
    }

    internal static List<int> IntList(int* p, int n)
    {
        var l = new List<int>(n);
        for (var i = 0; i < n; i++) l.Add(p[i]);
        return l;
    }

    internal static List<T> EnumList<T>(int* p, int n) where T : unmanaged, Enum
    {
        var l = new List<T>(n);
        for (var i = 0; i < n; i++) l.Add(Unsafe.As<int, T>(ref p[i]));
        return l;
    }

    internal static List<bool> BoolList(bool* p, int n)
    {
        var l = new List<bool>(n);
        for (var i = 0; i < n; i++) l.Add(p[i]);
        return l;
    }

    internal static List<string> StrList(LubNative.LubStr* p, int n)
    {
        var l = new List<string>(n);
        for (var i = 0; i < n; i++) l.Add(Str(p[i]));
        return l;
    }

    internal static List<T> HandleList<T>(int* p, int n, Func<int, T?> make) where T : class
    {
        var l = new List<T>(n);
        for (var i = 0; i < n; i++) l.Add(make(p[i])!);
        return l;
    }

    internal static List<T> RecordList<T, N>(N* p, int n, delegate*<N*, T> from) where N : unmanaged
    {
        var l = new List<T>(n);
        for (var i = 0; i < n; i++) l.Add(from(&p[i]));
        return l;
    }

    internal static List<float[]> FloatRowList(float* p, int n, int width)
    {
        var l = new List<float[]>(n);
        for (var i = 0; i < n; i++)
        {
            var row = new float[width];
            for (var j = 0; j < width; j++) row[j] = p[i * width + j];
            l.Add(row);
        }
        return l;
    }

    internal static float[] FloatsArray(float* p, int len)
    {
        var a = new float[len];
        for (var i = 0; i < len; i++) a[i] = p[i];
        return a;
    }

    internal static int[] IntsArray(int* p, int len)
    {
        var a = new int[len];
        for (var i = 0; i < len; i++) a[i] = p[i];
        return a;
    }

    // 固定長配列 (C の T x[n]) に写す。読んだ個数を返す。
    internal static int FixedFloats(IReadOnlyList<float>? l, float* dst, int cap)
    {
        var n = l == null ? 0 : Math.Min(l.Count, cap);
        for (var i = 0; i < cap; i++) dst[i] = i < n ? (float)l![i] : 0f;
        return n;
    }

    internal static int FixedInts(IReadOnlyList<int>? l, int* dst, int cap)
    {
        var n = l == null ? 0 : Math.Min(l.Count, cap);
        for (var i = 0; i < cap; i++) dst[i] = i < n ? l![i] : 0;
        return n;
    }

    // ---------------------------------------------------------- callbacks

    /// <summary>ゲームの delegate を runtime に渡すための箱。C の user pointer は
    /// この GCHandle。runtime が手放す (user_release) と解放する。</summary>
    internal sealed class CallbackBox
    {
        public readonly Delegate?[] Slots;
        private readonly bool[] logged;

        public CallbackBox(Delegate?[] slots)
        {
            Slots = slots;
            logged = new bool[slots.Length];
        }

        public static CallbackBox From(void* user) =>
            (CallbackBox)GCHandle.FromIntPtr((IntPtr)user).Target!;

        // callback の例外は境界で止め、slot ごとに 1 回だけ log して既定値で続行
        public void Fail(int slot, string name, Exception e)
        {
            if (logged[slot]) return;
            logged[slot] = true;
            Console.Error.WriteLine($"lub: callback {name} failed: {e}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void ReleaseUser(void* user)
    {
        if (user != null) GCHandle.FromIntPtr((IntPtr)user).Free();
    }

    // -------------------------------------------------------------- arena

    /// <summary>呼び出しの間だけ生きる memory。生成した関数は入口で Begin、出口で
    /// End する (入れ子も安全)。</summary>
    internal sealed class Arena
    {
        [ThreadStatic] private static Arena? current;

        private readonly List<IntPtr> blocks = new();
        private byte* cur;
        private int used, cap;
        private readonly List<int> marks = new();
        private readonly List<int> marksBlocks = new();
        private readonly List<GCHandle> transient = new();
        private readonly List<int> transientMarks = new();

        public static Arena Begin()
        {
            var a = current ??= new Arena();
            a.marks.Add(a.used);
            a.marksBlocks.Add(a.blocks.Count);
            a.transientMarks.Add(a.transient.Count);
            return a;
        }

        public void End()
        {
            var last = marks.Count - 1;
            var tm = transientMarks[last];
            for (var i = transient.Count - 1; i >= tm; i--)
            {
                transient[i].Free();
                transient.RemoveAt(i);
            }
            transientMarks.RemoveAt(last);
            // block 単位で戻す (同じ block なら used だけ戻す)
            var blockCount = marksBlocks[last];
            if (blocks.Count == blockCount)
            {
                used = marks[last];
            }
            else
            {
                for (var i = blocks.Count - 1; i >= blockCount; i--)
                {
                    Marshal.FreeHGlobal(blocks[i]);
                    blocks.RemoveAt(i);
                }
                used = 0;
                cap = 0;
                cur = null;
            }
            marks.RemoveAt(last);
            marksBlocks.RemoveAt(last);
        }

        public T* Alloc<T>(int count) where T : unmanaged
        {
            var bytes = sizeof(T) * Math.Max(count, 1);
            var align = 16;
            var start = (used + align - 1) & ~(align - 1);
            if (cur == null || start + bytes > cap)
            {
                cap = Math.Max(bytes + align, 64 * 1024);
                cur = (byte*)Marshal.AllocHGlobal(cap);
                blocks.Add((IntPtr)cur);
                used = 0;
                start = 0;
            }
            var p = cur + start;
            used = start + bytes;
            new Span<byte>(p, bytes).Clear();
            return (T*)p;
        }

        public LubNative.LubStr Str(string? s)
        {
            LubNative.LubStr r = default;
            if (s == null) return r;
            var n = Encoding.UTF8.GetByteCount(s);
            var p = Alloc<byte>(n + 1);
            Encoding.UTF8.GetBytes(s, new Span<byte>(p, n));
            p[n] = 0;
            r.ptr = p;
            r.len = n;
            return r;
        }

        public float* Floats(IReadOnlyList<float>? l, out int n)
        {
            if (l == null || l.Count == 0)
            {
                n = 0;
                return null;
            }
            n = l.Count;
            var p = Alloc<float>(n);
            for (var i = 0; i < n; i++) p[i] = (float)l[i];
            return p;
        }

        public int* Ints<T>(IReadOnlyList<T>? l, out int n) where T : unmanaged
        {
            if (l == null || l.Count == 0)
            {
                n = 0;
                return null;
            }
            n = l.Count;
            var p = Alloc<int>(n);
            for (var i = 0; i < n; i++)
            {
                var v = l[i];
                p[i] = Unsafe.As<T, int>(ref v);
            }
            return p;
        }

        public bool* Bools(IReadOnlyList<bool>? l, out int n)
        {
            if (l == null || l.Count == 0)
            {
                n = 0;
                return null;
            }
            n = l.Count;
            var p = Alloc<bool>(n);
            for (var i = 0; i < n; i++) p[i] = l[i];
            return p;
        }

        public LubNative.LubStr* Strs(IReadOnlyList<string>? l, out int n)
        {
            if (l == null || l.Count == 0)
            {
                n = 0;
                return null;
            }
            n = l.Count;
            var p = Alloc<LubNative.LubStr>(n);
            for (var i = 0; i < n; i++) p[i] = Str(l[i]);
            return p;
        }

        public int* Handles<T>(IReadOnlyList<T>? l, out int n, Func<T, int> handle)
        {
            if (l == null || l.Count == 0)
            {
                n = 0;
                return null;
            }
            n = l.Count;
            var p = Alloc<int>(n);
            for (var i = 0; i < n; i++) p[i] = handle(l[i]);
            return p;
        }

        // List<float[]> を [n][width] の float に写す。空の list は無し (NULL) と
        // して渡す (C 側は pointer の有無で欠損を見る)。
        public float* FloatRows(IReadOnlyList<float[]>? l, out int n, int width)
        {
            if (l == null || l.Count == 0)
            {
                n = 0;
                return null;
            }
            n = l.Count;
            var p = Alloc<float>(n * width);
            for (var i = 0; i < n; i++)
            {
                var row = l[i];
                for (var j = 0; j < width; j++) p[i * width + j] = j < row.Length ? (float)row[j] : 0f;
            }
            return p;
        }

        public N* Records<T, N>(IReadOnlyList<T>? l, out int n, delegate*<T, Arena, N*, void> to) where N : unmanaged
        {
            if (l == null || l.Count == 0)
            {
                n = 0;
                return null;
            }
            n = l.Count;
            var p = Alloc<N>(n);
            for (var i = 0; i < n; i++) to(l[i], this, &p[i]);
            return p;
        }

        // 呼び出しの間だけの callback (query の visitor)。End で解放する。
        public void* Callback(Delegate d)
        {
            var h = GCHandle.Alloc(new CallbackBox(new[] { d }));
            transient.Add(h);
            return (void*)GCHandle.ToIntPtr(h);
        }

        // 宣言型の object に付く callback。runtime が user_release で解放する。
        public void* CallbackBox(Delegate?[] slots)
        {
            var any = false;
            foreach (var s in slots) any |= s != null;
            if (!any) return null;
            var h = GCHandle.Alloc(new CallbackBox(slots));
            return (void*)GCHandle.ToIntPtr(h);
        }

        // draw / dispatch の bindings。上段は handle (texture / buffer)、
        // "uniforms" の下は数値 (float の列)。
        public LubNative.LubBinding* Bindings(Dictionary<string, object>? dict, out int n)
        {
            n = 0;
            if (dict == null) return null;
            var items = new List<LubNative.LubBinding>();
            foreach (var (k, v) in dict)
            {
                if (k == "uniforms" && v is Dictionary<string, object> uniforms)
                {
                    foreach (var (uk, uv) in uniforms)
                    {
                        LubNative.LubBinding b = default;
                        b.name = Str(uk);
                        b.values = Numbers(uv, out b.count, uk);
                        items.Add(b);
                    }
                    continue;
                }
                LubNative.LubBinding hb = default;
                hb.name = Str(k);
                hb.handle = v switch
                {
                    TextureRef t => t.H,
                    BufferRef bf => bf.H,
                    _ => throw new LubException($"bindings.{k}: buffer or texture expected"),
                };
                items.Add(hb);
            }
            n = items.Count;
            var p = Alloc<LubNative.LubBinding>(n);
            for (var i = 0; i < n; i++) p[i] = items[i];
            return p;
        }

        private float* Numbers(object v, out int count, string key)
        {
            switch (v)
            {
                case float f:
                    {
                        var p = Alloc<float>(1);
                        p[0] = f;
                        count = 1;
                        return p;
                    }
                case int i:
                    {
                        var p = Alloc<float>(1);
                        p[0] = i;
                        count = 1;
                        return p;
                    }
                case IReadOnlyList<float> l:
                    return Floats(l, out count);
                default:
                    throw new LubException($"bindings.uniforms.{key}: number or number array expected");
            }
        }
    }
}
