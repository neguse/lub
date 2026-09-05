// .NET 実行の host。lub の host API (include/lub/lub_host.h) の上で loop を
// 回し、entry class の OnInit / OnEvent / OnFrame / OnQuit を呼ぶ。
// Lub.Run(typeof(Game), args) が入口 (テンプレート templates/game/host/Program.cs)。
#nullable enable
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

internal static unsafe partial class LubNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct LubHostOpts
    {
        public LubStr backend;
        public float fixed_dt;
        public LubStr capture_path;
        public int capture_frame;
        public bool digest;
    }

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void* lub_host_create(LubHostOpts* opts);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_host_start(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_host_poll_event(void* ctx, LubEventData* e);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_host_frame_begin(void* ctx, float* dt);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_host_frame_end(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_host_quit_requested(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_host_destroy(void* ctx);
}

public static unsafe partial class Lub
{
    /// <summary>entry class (static class) の static メソッドで runtime を回す。
    /// OnFrame は必須、OnInit / OnEvent / OnQuit は任意。args は player と同じ
    /// (--backend、--fixed-dt、--capture、--capture-frame、--digest)。</summary>
    public static int Run(Type entry, string[]? args = null)
    {
        var flags = BindingFlags.Public | BindingFlags.Static;
        var onInit = entry.GetMethod("OnInit", flags, Type.EmptyTypes);
        var onEvent = entry.GetMethod("OnEvent", flags, new[] { typeof(EventData) });
        var onFrame = entry.GetMethod("OnFrame", flags, new[] { typeof(double) })
            ?? throw new ArgumentException($"{entry.Name}: static OnFrame(double) is required");
        var onQuit = entry.GetMethod("OnQuit", flags, Type.EmptyTypes);
        return Run(
            onInit == null ? null : (Action)Delegate.CreateDelegate(typeof(Action), onInit),
            onEvent == null ? null : (Action<EventData>)Delegate.CreateDelegate(typeof(Action<EventData>), onEvent),
            (Action<double>)Delegate.CreateDelegate(typeof(Action<double>), onFrame),
            onQuit == null ? null : (Action)Delegate.CreateDelegate(typeof(Action), onQuit),
            args);
    }

    /// <summary>assembly の中から entry class (static OnFrame を持つ唯一の class)
    /// を探して回す。</summary>
    public static int Run(Assembly assembly, string[]? args = null)
    {
        Type? found = null;
        foreach (var t in assembly.GetTypes())
        {
            if (!t.IsClass || !t.IsAbstract || !t.IsSealed) continue; // static class
            if (t.GetMethod("OnFrame", BindingFlags.Public | BindingFlags.Static, new[] { typeof(double) }) == null) continue;
            if (found != null)
                throw new ArgumentException($"entry class is ambiguous: {found.Name} and {t.Name}");
            found = t;
        }
        if (found == null) throw new ArgumentException("no static class with OnFrame(double)");
        return Run(found, args);
    }

    public static int Run(Action? onInit, Action<EventData>? onEvent, Action<double> onFrame,
        Action? onQuit, string[]? args = null)
    {
        LubRuntime.EnsureNative();
        var a = LubRuntime.Arena.Begin();
        void* ctx;
        try
        {
            LubNative.LubHostOpts opts = default;
            args ??= Array.Empty<string>();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--backend" when i + 1 < args.Length:
                        opts.backend = a.Str(args[++i]);
                        break;
                    case "--fixed-dt" when i + 1 < args.Length:
                        opts.fixed_dt = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--capture" when i + 1 < args.Length:
                        opts.capture_path = a.Str(args[++i]);
                        if (opts.capture_frame == 0) opts.capture_frame = 30;
                        break;
                    case "--capture-frame" when i + 1 < args.Length:
                        opts.capture_frame = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--digest":
                        opts.digest = true;
                        break;
                    default:
                        Console.Error.WriteLine($"lub: unknown argument {args[i]}");
                        return 2;
                }
            }
            ctx = LubNative.lub_host_create(&opts);
        }
        finally
        {
            a.End();
        }
        if (ctx == null)
        {
            Console.Error.WriteLine("lub: runtime init failed");
            return 1;
        }
        LubRuntime.Ctx = ctx;
        try
        {
            Guard(onInit, "OnInit");
            if (LubNative.lub_host_start(ctx) != LubNative.LUB_OK)
            {
                Console.Error.WriteLine("lub: " + LubRuntime.Str(LubNative.lub_last_error(ctx)));
                return 1;
            }
            while (LubNative.lub_host_quit_requested(ctx) == 0)
            {
                LubNative.LubEventData e;
                while (LubNative.lub_host_poll_event(ctx, &e) != 0)
                {
                    if (e.kind == (int)EventKind.Quit) break;
                    if (onEvent != null)
                    {
                        var ev = LubNative.From_LubEventData(&e);
                        Guard(() => onEvent(ev), "OnEvent");
                    }
                }
                if (LubNative.lub_host_quit_requested(ctx) != 0) break;
                float dtRaw = 0f;
                if (LubNative.lub_host_frame_begin(ctx, &dtRaw) == 0)
                {
                    Thread.Sleep(16);
                    continue;
                }
                double dt = dtRaw;
                Guard(() => onFrame(dt), "OnFrame");
                LubNative.lub_host_frame_end(ctx);
            }
            Guard(onQuit, "OnQuit");
        }
        finally
        {
            LubRuntime.Ctx = null;
            LubNative.lub_host_destroy(ctx);
        }
        return 0;
    }

    // ゲームの例外は frame 境界で止めて log する (Lua の player と同じ)。
    private static void Guard(Action? f, string what)
    {
        if (f == null) return;
        try
        {
            f();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"lub: error in {what}: {e}");
        }
    }
}
