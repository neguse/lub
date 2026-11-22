// Host for .NET execution.
// Runs the loop on top of the lub host API (include/lub/lub_host.h) and calls OnInit / OnEvent / OnFrame / OnQuit of the entry class.
// App.Run(typeof(Game), args) is the entry point (template: templates/game/host/Program.cs).
#nullable enable
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lub;

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
        public bool readback_fixed_latency;
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
    internal static extern void lub_host_frame_end(void* ctx, byte frameOk);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte lub_host_quit_requested(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int lub_host_exit_code(void* ctx);

    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void lub_host_destroy(void* ctx);
}

public static unsafe partial class App
{
    /// <summary>Runs the runtime with the static methods of the entry class (a static class).
    /// OnFrame is required; OnInit / OnEvent / OnQuit / OnError are optional.
    /// args are the same as the player's (--backend, --fixed-dt, --capture, --capture-frame, --digest,
    /// --readback-fixed-latency).
    /// Returns the exit code given to Quit.
    /// </summary>
    public static int Run(Type entry, string[]? args = null)
    {
        var flags = BindingFlags.Public | BindingFlags.Static;
        var onInit = entry.GetMethod("OnInit", flags, Type.EmptyTypes);
        var onEvent = entry.GetMethod("OnEvent", flags, new[] { typeof(EventData) });
        var onFrame = entry.GetMethod("OnFrame", flags, new[] { typeof(float) })
            ?? throw new ArgumentException($"{entry.Name}: static OnFrame(float) is required");
        var onQuit = entry.GetMethod("OnQuit", flags, Type.EmptyTypes);
        var onError = entry.GetMethod("OnError", flags, new[] { typeof(string) });
        if (onError != null && onError.ReturnType != typeof(ErrorAction))
            throw new ArgumentException($"{entry.Name}: OnError must return ErrorAction");
        return Run(
            onInit == null ? null : (Action)Delegate.CreateDelegate(typeof(Action), onInit),
            onEvent == null ? null : (Action<EventData>)Delegate.CreateDelegate(typeof(Action<EventData>), onEvent),
            (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), onFrame),
            onQuit == null ? null : (Action)Delegate.CreateDelegate(typeof(Action), onQuit),
            args,
            onError == null ? null : (Func<string, ErrorAction>)Delegate.CreateDelegate(typeof(Func<string, ErrorAction>), onError));
    }

    /// <summary>Finds the entry class (the only class with a static OnFrame) in the assembly and runs it.
    /// If args contain --entry CLASS (MyGame.Game with a namespace), runs that class (same as --entry of the tcs VM player).
    /// </summary>
    public static int Run(Assembly assembly, string[]? args = null)
    {
        args ??= Array.Empty<string>();
        var at = Array.IndexOf(args, "--entry");
        if (at >= 0 && at + 1 < args.Length)
        {
            var name = args[at + 1];
            var rest = new string[args.Length - 2];
            Array.Copy(args, 0, rest, 0, at);
            Array.Copy(args, at + 2, rest, at, args.Length - at - 2);
            return Run(assembly.GetType(name) ?? throw new ArgumentException($"no entry class {name}"), rest);
        }
        Type? found = null;
        foreach (var t in assembly.GetTypes())
        {
            if (!t.IsClass || !t.IsAbstract || !t.IsSealed) continue; // static class
            if (t.GetMethod("OnFrame", BindingFlags.Public | BindingFlags.Static, new[] { typeof(float) }) == null) continue;
            if (found != null)
                throw new ArgumentException($"entry class is ambiguous: {found.Name} and {t.Name}");
            found = t;
        }
        if (found == null) throw new ArgumentException("no static class with OnFrame(float)");
        return Run(found, args);
    }

    public static int Run(Action? onInit, Action<EventData>? onEvent, Action<float> onFrame,
        Action? onQuit, string[]? args = null, Func<string, ErrorAction>? onError = null)
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
                    case "--readback-fixed-latency":
                        opts.readback_fixed_latency = true;
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
            Guard(onInit, "OnInit", onError);
            if (LubNative.lub_host_quit_requested(ctx) == 0 && LubNative.lub_host_start(ctx) != LubNative.LUB_OK)
            {
                Console.Error.WriteLine("lub: " + LubRuntime.Str(LubNative.lub_last_error(ctx)));
                App.Quit(1);
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
                        Guard(() => onEvent(ev), "OnEvent", onError);
                    }
                }
                if (LubNative.lub_host_quit_requested(ctx) != 0) break;
                float dtRaw = 0f;
                if (LubNative.lub_host_frame_begin(ctx, &dtRaw) == 0)
                {
                    Thread.Sleep(16);
                    continue;
                }
                float dt = dtRaw;
                var frameOk = Guard(() => onFrame(dt), "OnFrame", onError);
                LubNative.lub_host_frame_end(ctx, frameOk ? (byte)1 : (byte)0);
            }
            Guard(onQuit, "OnQuit", onError);
            return LubNative.lub_host_exit_code(ctx);
        }
        finally
        {
            LubRuntime.Ctx = null;
            LubNative.lub_host_destroy(ctx);
        }
    }

    // Game exceptions are stopped at the callback boundary and logged, and the entry's OnError decides what to do (Log if there is none).
    // If OnError itself fails or returns anything other than Log, Abort (exits with exit code 1).
    // Same as the tcs VM player.
    // true if f ran to the end.
    private static bool Guard(Action? f, string what, Func<string, ErrorAction>? onError)
    {
        if (f == null) return true;
        try
        {
            f();
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"lub: error in {what}: {e}");
            var action = ErrorAction.Log;
            if (onError != null)
            {
                try
                {
                    action = onError(e.Message);
                }
                catch (Exception e2)
                {
                    Console.Error.WriteLine($"lub: error in OnError: {e2}");
                    action = ErrorAction.Abort;
                }
            }
            if (action != ErrorAction.Log) App.Quit(1);
            return false;
        }
    }
}
