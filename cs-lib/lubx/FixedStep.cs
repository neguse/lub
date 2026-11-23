// FixedStep of the implementation library lubx.
// The pending set is kept as a List of bool parallel to keys.

using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary> Fixed tick driver.
/// Calling frame() every frame from the variable-rate onFrame(dt) separates game progress (per-frame rules, physics, AI) into fixed-Hz ticks independent of the display refresh.
/// Ticks run 0 to maxCatchUp times per frame.
/// Edges such as keyPressed are delivered at tick granularity, and edges in frames with 0 ticks are not lost.
/// Read edges inside the tick callback.
/// The callback is not kept (it is passed to frame() every frame), so after a live hot reload the new code is called from the next frame.
/// </summary>
public class FixedStep
{
    /// <summary>Fixed dt (= 1/hz) in seconds passed to the tick callback.
    /// </summary>
    public float TickDt;

    private int maxCatchUp;
    private float accumulator = 0;
    private bool stopped = false;

    // Key names scanned every frame from the start.
    // Names not listed here (f1..f12, aliases, SDL scancode names) are added to keys the first time KeyPressed / KeyReleased asks about them.
    // TODO: once the runtime exposes timestamped input events, replace this scan
    // with event consumption (without changing the public API).
    private static string[] scanKeys = new string[]
    {
        "space", "enter", "escape", "tab", "backspace",
        "left", "right", "up", "down",
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
    };

    // Key names to scan.
    // Starts with scanKeys, and names that are asked about get added.
    private List<string> keys = new List<string>();

    // Pending tick-granularity edges.
    // Carried over across frames until the next tick consumes them.
    // The element count matches keys (4 for mouse, which does not use index 0).
    private List<bool> pendingKeyPressed = new List<bool>();
    private List<bool> pendingKeyReleased = new List<bool>();
    private List<bool> pendingMousePressed = new List<bool>();
    private List<bool> pendingMouseReleased = new List<bool>();

    /// <summary>hz: tick frequency (positive, default = 60).
    /// maxCatchUp: upper limit of ticks run in one frame() (1 or more, default = 8).
    /// </summary>
    public FixedStep(float? hz = null, int? maxCatchUp = null)
    {
        TickDt = 1.0f / (hz ?? 60.0f);
        this.maxCatchUp = maxCatchUp ?? 8;
        for (int i = 0; i < scanKeys.Length; i++)
        {
            keys.Add(scanKeys[i]);
            pendingKeyPressed.Add(false);
            pendingKeyReleased.Add(false);
        }
        for (int b = 0; b < 4; b++)
        {
            pendingMousePressed.Add(false);
            pendingMouseReleased.Add(false);
        }
    }

    /// <summary>Call every frame from onFrame.
    /// Accumulates the measured dt and runs the fixed tick 0 to maxCatchUp times.
    /// tick is not kept.
    /// </summary>
    public void Frame(float dt, Action<float> tick)
    {
        LatchEdges();
        if (dt > 0)
        {
            accumulator = Math.Min(accumulator + dt, TickDt * maxCatchUp);
        }
        stopped = false;
        int steps = 0;
        while (accumulator + 1e-9f >= TickDt && steps < maxCatchUp && !stopped)
        {
            tick(TickDt);
            ClearPending();
            accumulator = accumulator - TickDt;
            if (accumulator < 0)
            {
                accumulator = 0;
            }
            steps = steps + 1;
        }
    }

    /// <summary>When called from inside the tick callback, discards the remaining catch-up ticks and the accumulated time for this frame and exits frame().
    /// </summary>
    public void Stop()
    {
        stopped = true;
        accumulator = 0;
    }

    /// <summary>Whether the key was pressed since the previous tick.
    /// Read inside the tick callback.
    /// Accepts the same key names as Input.KeyPressed (case-insensitive).
    /// Names other than space, enter, escape, tab, backspace, arrows, a..z, 0..9 carry edges over starting from the frame in which they are first asked about.
    /// </summary>
    public bool KeyPressed(string key)
    {
        return pendingKeyPressed[KeyIndex(key)];
    }

    /// <summary>Whether the key was released since the previous tick.
    /// Read inside the tick callback.
    /// Key names are the same as for KeyPressed.
    /// </summary>
    public bool KeyReleased(string key)
    {
        return pendingKeyReleased[KeyIndex(key)];
    }

    /// <summary>Whether the button was pressed since the previous tick (1=left 2=middle 3=right, default = left).
    /// </summary>
    public bool MousePressed(int? button = null)
    {
        int b = button ?? 1;
        return b >= 1 && b <= 3 && pendingMousePressed[b];
    }

    /// <summary>Whether the button was released since the previous tick (1=left 2=middle 3=right, default = left).
    /// </summary>
    public bool MouseReleased(int? button = null)
    {
        int b = button ?? 1;
        return b >= 1 && b <= 3 && pendingMouseReleased[b];
    }

    /// <summary>Fraction of time elapsed from the latest tick to the next tick (0 to 1).
    /// For interpolation.
    /// </summary>
    public float Alpha()
    {
        return Math.Min(accumulator / TickDt, 1.0f);
    }

    // Position of the key name in keys.
    // If missing, add it and take the edge the runtime latched this frame as the pending value as is (from the next frame on, LatchEdges picks it up).
    // The same key written differently ("R" and "r", "esc" and "escape") gets separate rows, but Input looks both up as the same key
    private int KeyIndex(string key)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i] == key)
            {
                return i;
            }
        }
        keys.Add(key);
        pendingKeyPressed.Add(Input.KeyPressed(key));
        pendingKeyReleased.Add(Input.KeyReleased(key));
        return keys.Count - 1;
    }

    // Every frame, pull in the edges the runtime latched for the frame.
    // This is the carry-over that keeps them from being lost even in frames with 0 ticks.
    private void LatchEdges()
    {
        for (int i = 0; i < keys.Count; i++)
        {
            if (Input.KeyPressed(keys[i]))
            {
                pendingKeyPressed[i] = true;
            }
            if (Input.KeyReleased(keys[i]))
            {
                pendingKeyReleased[i] = true;
            }
        }
        for (int b = 1; b <= 3; b++)
        {
            if (Input.MousePressed(b))
            {
                pendingMousePressed[b] = true;
            }
            if (Input.MouseReleased(b))
            {
                pendingMouseReleased[b] = true;
            }
        }
    }

    private void ClearPending()
    {
        for (int i = 0; i < pendingKeyPressed.Count; i++)
        {
            pendingKeyPressed[i] = false;
            pendingKeyReleased[i] = false;
        }
        for (int b = 0; b < pendingMousePressed.Count; b++)
        {
            pendingMousePressed[b] = false;
            pendingMouseReleased[b] = false;
        }
    }
}
