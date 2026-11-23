// XrAnchor in the implementation library lubx.
// The viewpoint Xr.View returns is in the OpenXR / WebXR LOCAL space (origin at the head position at startup, meters, forward -Z).
// Games want to place things in their own space, so this takes a reference head position and horizontal orientation and maps later eye matrices by multiplying them in.
// The runtime holds no state, and the game decides which frame to use as the reference.
// Matrices are passed as row-major float[16], the same as XrView.ViewProjection.

using System;
using Lub;

namespace Lubx;

/// <summary>Maps the XR viewpoint into a space whose origin is the midpoint of both eyes at the time of Recenter and whose forward -Z is the horizontal orientation at that time.
/// Pass the XrView to ViewProjection every frame.
/// Stays in LOCAL space until Recenter.
/// </summary>
public class XrAnchor
{
    readonly float[] matrix = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
    bool set;

    /// <summary>Whether Recenter has been done.
    /// </summary>
    public bool IsSet() => set;

    /// <summary>Takes the midpoint of both eyes and the horizontal orientation of the left eye as the reference.
    /// </summary>
    public void Recenter(XrView left, XrView right)
    {
        var o = left.Orientation;
        var p = left.Position;
        var q = right.Position;
        var yaw = (float)Math.Atan2(2 * (o[0] * o[2] + o[1] * o[3]),
            1 - 2 * (o[0] * o[0] + o[1] * o[1]));
        var c = (float)Math.Cos(yaw);
        var s = (float)Math.Sin(yaw);
        for (var i = 0; i < 16; i++)
        {
            matrix[i] = 0;
        }
        matrix[0] = c;
        matrix[2] = s;
        matrix[5] = 1;
        matrix[8] = -s;
        matrix[10] = c;
        matrix[15] = 1;
        matrix[3] = (p[0] + q[0]) * 0.5f;
        matrix[7] = (p[1] + q[1]) * 0.5f;
        matrix[11] = (p[2] + q[2]) * 0.5f;
        set = true;
    }

    /// <summary>Discards the reference and returns to LOCAL space.
    /// </summary>
    public void Reset()
    {
        for (var i = 0; i < 16; i++)
        {
            matrix[i] = i % 5 == 0 ? 1 : 0;
        }
        set = false;
    }

    /// <summary>Matrix from the reference space to LOCAL space (row-major, translation in [3] [7] [11]).
    /// </summary>
    public float[] ToLocal() => matrix;

    /// <summary>Writes the view's projection * view multiplied by the reference into result (16 elements).
    /// Row-major, so coordinates in the reference space can be drawn as is.
    /// </summary>
    public void ViewProjection(XrView view, float[] result)
    {
        var vp = view.ViewProjection;
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                result[row * 4 + col] = vp[row * 4] * matrix[col]
                    + vp[row * 4 + 1] * matrix[4 + col]
                    + vp[row * 4 + 2] * matrix[8 + col]
                    + vp[row * 4 + 3] * matrix[12 + col];
            }
        }
    }
}
