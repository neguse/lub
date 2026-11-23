// Hand-written base used by the .NET execution facade (Lub.g.cs, generated code).
// C API basic types, memory that lives only during a call (arena), string and view conversion, callback boxes, and turning errors into exceptions.
// The generated code only does the repacking for the C API.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Lub;

/// <summary>The C API returned LUB_ERROR (message from lub_last_error).
/// The host catches it at the callback boundary and passes it to the entry's OnError.
/// </summary>
public sealed class LubException : Exception
{
    public LubException(string message) : base(message)
    {
    }
}

/// <summary>A name and value pair passed to draw.
/// Held only in managed arrays and copied into the arena at draw time.
/// </summary>
public sealed unsafe class Bindings
{
    private struct Item
    {
        public string Name;
        public int Handle;
        public int Offset;
        public int Count;
        public int Cap;
    }

    private Item[] items = new Item[6];
    private int n;
    private float[] values = new float[36];
    private int used;

    private ref Item Find(string name)
    {
        var all = items;
        for (var i = 0; i < n; i++)
            if ((object)all[i].Name == name || all[i].Name == name) return ref all[i];
        if (n == all.Length) Array.Resize(ref items, n * 2);
        ref var it = ref items[n++];
        it = default;
        it.Name = name;
        return ref it;
    }

    private Span<float> Slot(string name, int count)
    {
        ref var it = ref Find(name);
        if (it.Cap < count)
        {
            if (used + count > values.Length) Array.Resize(ref values, Math.Max(values.Length * 2, used + count));
            it.Offset = used;
            it.Cap = count;
            used += count;
        }
        it.Handle = 0;
        it.Count = count;
        return values.AsSpan(it.Offset, count);
    }

    internal void SetTexture(string name, int handle) => SetHandle(name, handle);

    internal void SetBuffer(string name, int handle) => SetHandle(name, handle);

    internal void SetFrameRange(string name, int handle) => SetHandle(name, handle);

    private void SetHandle(string name, int handle)
    {
        ref var it = ref Find(name);
        it.Handle = handle;
        it.Count = 0;
    }

    internal void SetFloats(string name, List<float> v)
    {
        var d = Slot(name, v.Count);
        for (var i = 0; i < d.Length; i++) d[i] = v[i];
    }

    internal void SetFloatArray(string name, float[] v) => v.AsSpan().CopyTo(Slot(name, v.Length));

    internal void Set4(string name, int count, float x, float y, float z, float w)
    {
        if (count < 1 || count > 4) throw new LubException($"bindings: count must be 1 to 4 (got {count})");
        var d = Slot(name, count);
        d[0] = x;
        if (count > 1) d[1] = y;
        if (count > 2) d[2] = z;
        if (count > 3) d[3] = w;
    }

    internal void Set16(string name, float m00, float m01, float m02, float m03, float m10, float m11, float m12, float m13,
        float m20, float m21, float m22, float m23, float m30, float m31, float m32, float m33)
    {
        var d = Slot(name, 16);
        d[0] = m00; d[1] = m01; d[2] = m02; d[3] = m03;
        d[4] = m10; d[5] = m11; d[6] = m12; d[7] = m13;
        d[8] = m20; d[9] = m21; d[10] = m22; d[11] = m23;
        d[12] = m30; d[13] = m31; d[14] = m32; d[15] = m33;
    }

    internal void Clear()
    {
        n = 0;
        used = 0;
    }

    internal LubNative.LubBinding* ToNative(LubRuntime.Arena a, out int count)
    {
        count = n;
        var p = a.Alloc<LubNative.LubBinding>(n);
        var f = a.Alloc<float>(used);
        values.AsSpan(0, used).CopyTo(new Span<float>(f, used));
        for (var i = 0; i < n; i++)
        {
            ref readonly var it = ref items[i];
            p[i].name = a.Str(it.Name);
            p[i].handle = it.Handle;
            if (it.Handle == 0)
            {
                p[i].values = f + it.Offset;
                p[i].count = it.Count;
            }
        }
        return p;
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
    internal static extern int lub_gfx_draw_items(void* ctx, int count, LubBinding* bindings, int bindings_count, LubDrawOpts* opts, int instances);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_gfx_dispatch_items(void* ctx, int x, int y, int z, LubBinding* bindings, int bindings_count, LubDispatchOpts* opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte* lub_last_error(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_frame_index(void* ctx);
}

internal static unsafe class LubRuntime
{
    internal const string LibName = "lub";

    /// <summary>The context the host created.
    /// The API is called on it.
    /// </summary>
    internal static void* Ctx;

    private static bool resolverSet;

    // Location of the shared library: the environment variable LUB_NATIVE_LIB (full path) takes top priority.
    // Next, the directory the game's project recorded as the runtime config option Lub.NativeDir (LubNativeDir of templates/game/Game.csproj).
    // The library is loaded where it is, so the libraries it depends on (SDL3, slang) are found next to it.
    // If neither, the default search (next to the executable, the system library path).
    // The host (App.Run) calls this before the first P/Invoke.
    // On Windows, unless the path is passed with \ separators, dependent DLLs (SDL3.dll etc.) are not searched for in the library's dir, so GetFullPath converts it to OS separators.
    internal static void EnsureNative()
    {
        if (resolverSet) return;
        resolverSet = true;
        NativeLibrary.SetDllImportResolver(typeof(LubRuntime).Assembly, (name, asm, path) =>
        {
            if (name != LibName) return IntPtr.Zero;
            var env = Environment.GetEnvironmentVariable("LUB_NATIVE_LIB");
            if (!string.IsNullOrEmpty(env)) return NativeLibrary.Load(Path.GetFullPath(env));
            if (AppContext.GetData("Lub.NativeDir") is string dir && dir.Length > 0)
            {
                var file = OperatingSystem.IsWindows() ? "lub.dll" : OperatingSystem.IsMacOS() ? "liblub.dylib" : "liblub.so";
                var full = Path.GetFullPath(Path.Combine(dir, file));
                if (File.Exists(full)) return NativeLibrary.Load(full);
            }
            return IntPtr.Zero;
        });
    }

    // The message is lub_last_error as is (the same string as the tcs VM player's error).
    internal static void Check(int status)
    {
        if (status == LubNative.LUB_OK) return;
        throw new LubException(Ctx == null ? "no context" : Str(LubNative.lub_last_error(Ctx)));
    }

    // ------------------------------------------------------------ strings

    internal static string Str(LubNative.LubStr s) =>
        s.ptr == null || s.len <= 0 ? "" : Encoding.UTF8.GetString(s.ptr, s.len);

    internal static string Str(byte* z) => z == null ? "" : Marshal.PtrToStringUTF8((IntPtr)z) ?? "";

    internal static string? StrOrNull(LubNative.LubStr s) => s.ptr == null ? null : Str(s);

    // 64 bit bit mask.
    // Same rules as the tcs VM and tcs2c bindings: null is no value, 1 to 16 hex digits (0x prefix allowed) are read, and empty or 17 or more digits is an error.
    internal static ulong Bits(string? s)
    {
        if (s == null) return 0;
        var t = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        if (t.Length == 0) throw new LubException("bit mask: empty hex string");
        if (t.Length > 16) throw new LubException($"bit mask: more than 16 hex digits '{s}'");
        // ulong.TryParse skips a trailing NUL, so digits are checked one character at a time
        ulong v = 0;
        foreach (var c in t)
        {
            var d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (d < 0) throw new LubException($"bit mask: invalid hex '{s}'");
            v = v << 4 | (uint)d;
        }
        return v;
    }

    internal static string BitsStr(ulong v) => v.ToString("x16");

    // ------------------------------------------------------------- views

    internal static void CheckView(int frame)
    {
        if (Ctx == null) return;
        var now = LubNative.lub_frame_index(Ctx);
        if (now != frame)
            throw new LubException($"stale view: returned in frame {frame}, now frame {now} (a view is valid until the end of its frame; copy what you keep)");
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

    // Copies into a fixed-length array (C's T x[n]).
    // Returns the number read.
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

    /// <summary>A box for passing a game delegate to the runtime.
    /// The C user pointer is this GCHandle.
    /// Freed when the runtime releases it (user_release).
    /// </summary>
    internal sealed class CallbackBox
    {
        public readonly Delegate?[] Slots;
        private readonly bool[] logged;

        public CallbackBox(Delegate?[] slots)
        {
            Slots = slots;
            logged = new bool[slots.Length];
        }

        // The first exception from the query's visitor.
        // Once it is recorded, the visitor is not called for the rest of that query.
        public Exception? Failure;

        public static CallbackBox From(void* user) =>
            (CallbackBox)GCHandle.FromIntPtr((IntPtr)user).Target!;

        // Callback exceptions are stopped at the boundary, logged only once per slot, and execution continues with the default value
        public void Fail(int slot, string name, Exception e)
        {
            if (logged[slot]) return;
            logged[slot] = true;
            Console.Error.WriteLine($"lub: callback {name} failed: {e}");
        }
    }

    // If the query's visitor throws, logs the message and returns true (the query returns null instead of faulting; same as the tcs VM player).
    internal static bool VisitorFailed(void* user, string what)
    {
        var e = user == null ? null : CallbackBox.From(user).Failure;
        if (e == null) return false;
        Console.Error.WriteLine($"lub: {what} visitor: {e}");
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static void ReleaseUser(void* user)
    {
        if (user != null) GCHandle.FromIntPtr((IntPtr)user).Free();
    }

    // -------------------------------------------------------------- arena

    /// <summary>Memory that lives only during a call.
    /// Generated functions call Begin on entry and End on exit (nesting is safe).
    /// </summary>
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
            // Rewinds per block (within the same block, only rewinds used)
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

        // Copies a List<float[]> into [n][width] floats.
        // An empty list is passed as none (NULL) (the C side detects absence by whether the pointer is set).
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

        // Passes the array without copying, pinned only for the duration of the call.
        public float* PinFloats(float[]? l, int count)
        {
            if (l == null) return null;
            if ((uint)count > (uint)l.Length)
                throw new LubException($"count {count} is out of range for an array of {l.Length}");
            var h = GCHandle.Alloc(l, GCHandleType.Pinned);
            transient.Add(h);
            return (float*)h.AddrOfPinnedObject();
        }

        // A callback that lives only during the call (query visitor).
        // Freed at End.
        public void* Callback(Delegate d)
        {
            var h = GCHandle.Alloc(new CallbackBox(new[] { d }));
            transient.Add(h);
            return (void*)GCHandle.ToIntPtr(h);
        }

        // A callback attached to a declarative object.
        // The runtime frees it with user_release.
        public void* CallbackBox(Delegate?[] slots)
        {
            var any = false;
            foreach (var s in slots) any |= s != null;
            if (!any) return null;
            var h = GCHandle.Alloc(new CallbackBox(slots));
            return (void*)GCHandle.ToIntPtr(h);
        }

    }
}
