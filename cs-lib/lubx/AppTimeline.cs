// AppTimeline of the implementation library lubx.
// Records the recent frames of an app opened with App.Use, pauses it, and goes back to past frames.

using System;
using Lub;

namespace Lubx;

/// <summary>
/// Steps the opened app instead of App.Step, and for each frame records the state before stepping (App.Save), the input shown (StepInput.Keep), and dt.
/// While paused, Step loads the state of the paused frame, recreates that frame with the recorded input and dt, and draws it (if the code is fixed, it redraws with the fixed code).
/// The records use capacity slot numbers starting at firstSlot. Do not use the same numbers for other App.Save calls.
/// The physics world, sound, and files are not rolled back (same as App.Load).
/// </summary>
public class AppTimeline
{
    readonly int capacity;
    readonly int firstSlot;
    readonly float[] dts;
    // Next position to write, and the number of recorded frames
    int head;
    int count;
    // Paused position (how many frames back; 0 is the most recently recorded frame). -1 if not paused
    int paused = -1;

    public AppTimeline(int capacity, int firstSlot = 0)
    {
        this.capacity = capacity;
        this.firstSlot = firstSlot;
        dts = new float[capacity];
    }

    /// <summary>Number of recorded frames.
    /// </summary>
    public int Count => count;

    /// <summary>Paused position (how many frames back). -1 if not paused.
    /// </summary>
    public int Position => paused;

    int Index(int back) => (head - 1 - back + capacity * 2) % capacity;

    /// <summary>Pauses at the frame back frames ago (0 is the most recently recorded frame). Clamped to the recorded range. Does nothing if nothing is recorded.
    /// </summary>
    public void Pause(int back)
    {
        if (count == 0) return;
        paused = back < 0 ? 0 : back >= count ? count - 1 : back;
    }

    /// <summary>Continues from the paused position. Records after it are discarded.
    /// </summary>
    public void Resume()
    {
        if (paused < 0) return;
        // The next Step recreates the paused frame from the same state and records it again
        head = Index(paused);
        count -= paused + 1;
        resume = true;
        paused = -1;
    }

    // The first Step after Resume starts from the state of the paused frame
    bool resume;

    /// <summary>Advances the app by 1 frame (if paused, recreates the paused frame).
    /// The return value and arguments are the same as App.Step. An app whose state cannot be recorded (App.Save returns false) is stepped without recording.
    /// </summary>
    public bool Step(AppRef app, TextureRef target, float dt, StepInput? input = null)
    {
        if (paused >= 0)
        {
            var at = Index(paused);
            if (!App.Load(app, firstSlot + at, out _)) return false;
            // Show the recorded input, and the current XR (the viewer keeps moving even while paused)
            return App.Step(app, target, dts[at], new StepInput
            {
                Xr = input?.Xr,
                XrTargets = input?.XrTargets,
                XrViewProjections = input?.XrViewProjections,
                Replay = firstSlot + at,
            });
        }
        var slot = firstSlot + head;
        if (resume)
        {
            resume = false;
            App.Load(app, slot, out _);
        }
        if (!App.Save(app, slot, out _))
            return App.Step(app, target, dt, input);
        dts[head] = dt;
        head = (head + 1) % capacity;
        if (count < capacity) count++;
        return App.Step(app, target, dt, new StepInput
        {
            Keyboard = input?.Keyboard,
            Mouse = input?.Mouse,
            Gamepad = input?.Gamepad,
            MouseX = input?.MouseX,
            MouseY = input?.MouseY,
            Xr = input?.Xr,
            XrTargets = input?.XrTargets,
            XrViewProjections = input?.XrViewProjections,
            Replay = input?.Replay,
            Keep = slot,
        });
    }

    /// <summary>Rewrites the state of the paused frame.
    /// change writes values (Sys.InspectSet etc.) into the app, which has loaded the state from before that frame was stepped. It returns true if it wrote them.
    /// If change returns true, the rewritten state is recorded again and the records after it are discarded (the paused position becomes the most recent). The next Step recreates the paused frame from the rewritten state.
    /// If not paused, if the state cannot be read or written, or if change returns false, returns false without changing the records.
    /// </summary>
    public bool Edit(AppRef app, Func<bool> change)
    {
        if (paused < 0) return false;
        var at = Index(paused);
        if (!App.Load(app, firstSlot + at, out _)) return false;
        if (!change()) return false;
        if (!App.Save(app, firstSlot + at, out _)) return false;
        head = (at + 1) % capacity;
        count -= paused;
        paused = 0;
        return true;
    }

    /// <summary>Discards all records.
    /// </summary>
    public void Clear()
    {
        for (var i = 0; i < capacity; i++) App.Drop(firstSlot + i);
        head = 0;
        count = 0;
        paused = -1;
        resume = false;
    }
}
