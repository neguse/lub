// FpsMeter of the implementation library lubx.
// fps being read-only is expressed by a field + doc comment.

using Lub;

namespace Lubx;

/// <summary>
/// Small helper that only holds the value of Sys.actual_fps.
/// Keeps returning initialFps until a measurement arrives (right after startup).
/// Call tick() every frame.
/// </summary>
public class FpsMeter
{
    /// <summary>Most recent measured FPS.
    /// Updated by tick() (read-only for the caller).
    /// </summary>
    public float Fps;

    public FpsMeter(float? initialFps = null)
    {
        Fps = initialFps ?? 60.0f;
    }

    public float Tick()
    {
        var measured = Sys.ActualFps();
        if (measured > 0)
        {
            Fps = measured;
        }
        return Fps;
    }
}
