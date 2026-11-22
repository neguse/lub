// Rand of the implementation library lubx.
// state is kept as an int (32-bit wrap).
// << with int32 wrap matches the xorshift32 definition as is, and the logical shift >>> is reproduced with the arithmetic shift >> and an & mask (0x7FFF keeps the 15 valid bits after the 17-bit shift).
// The goldens depend on this sequence.
// float / int are C# reserved words, hence NextFloat / NextInt.

using Lub;

namespace Lubx;

/// <summary> Deterministic xorshift32 random numbers.
/// With a fixed seed it is reproducible even across hot reload / headless verification (Math.random is not used because its sequence changes on every reload).
/// </summary>
public class Rand
{
    private int state;

    public Rand(int? seed = null)
    {
        int s = seed ?? 0x12345678;
        state = s == 0 ? 0x12345678 : s;
    }

    /// <summary>Uniform random number in [0, 1).
    /// </summary>
    public float NextFloat()
    {
        state = state ^ (state << 13);
        state = state ^ ((state >> 17) & 0x7FFF);
        state = state ^ (state << 5);
        return (state & 0xffff) / 65536.0f;
    }

    /// <summary>Integer in [0, n).
    /// </summary>
    public int NextInt(int n)
    {
        return (int)System.Math.Floor(NextFloat() * n);
    }

    /// <summary>Integer from min to max (both ends inclusive).
    /// A different name from NextInt(n) (tcs2c cannot handle overloads of same-named methods).
    /// </summary>
    public int Between(int min, int max)
    {
        int width = max - min + 1;
        if (width > 0 || max < min)
            return min + NextInt(width);
        // The width (max - min + 1) does not fit in an int.
        // Treat max - min as an unsigned 32-bit value, split it into its upper and lower 16 bits, and compute in integers the floor of the product of the width and 16 bits of a [0, 1) random number (k / 65536).
        // The additions wrap at 32 bits, but the true value is between min and max, so the result is the same value
        int k = (int)(NextFloat() * 65536.0f);
        int d = max - min;
        int hi = (d >> 16) & 0xFFFF;
        int lo = d & 0xFFFF;
        return min + k * hi + (((k * (lo + 1)) >> 16) & 0xFFFF);
    }

    /// <summary>Uniform random number in [min, max).
    /// </summary>
    public float Range(float min, float max)
    {
        return min + NextFloat() * (max - min);
    }
}
