using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace LubTest;

/// <summary>Compares two PNG captures within a tolerance. A real GPU does not reproduce a software rasterizer byte for byte, so the macOS and iOS golden checks
/// compare with the Linux goldens with this instead of bytes. The difference of a pixel is the largest absolute difference over R, G and B.
/// The images match when the mean difference is at most MeanLimit and at most OutlierLimit percent of the pixels differ by more than 16.
/// Only what stb_image_write produces is read: 8-bit, non-interlaced PNG.</summary>
public static class PngDiff
{
    public const double MeanLimit = 3.0;
    public const double OutlierLimit = 1.0;

    private sealed record Image(int Width, int Height, int Channels, byte[] Pixels);

    /// <summary>Whether the images match, and the measured figures as text.</summary>
    public static (bool Ok, string Metrics) Compare(string a, string b)
    {
        var ia = Load(a);
        var ib = Load(b);
        if (ia.Width != ib.Width || ia.Height != ib.Height) return (false, $"size {ia.Width}x{ia.Height} != {ib.Width}x{ib.Height}");
        long total = 0;
        int worst = 0, outliers = 0;
        int pixels = ia.Width * ia.Height;
        for (int i = 0; i < pixels; i++)
        {
            int d = 0;
            for (int plane = 0; plane < 3; plane++)
                d = Math.Max(d, Math.Abs(Sample(ia, i, plane) - Sample(ib, i, plane)));
            total += d;
            worst = Math.Max(worst, d);
            if (d > 16) outliers++;
        }
        double mean = (double)total / pixels, percent = 100.0 * outliers / pixels;
        bool ok = mean <= MeanLimit && percent <= OutlierLimit;
        return (ok, FormattableString.Invariant($"mean={mean:0.000} max={worst} over16={percent:0.00}% ({(ok ? "ok" : "differs")})"));
    }

    // Gray (with or without alpha) is treated as R = G = B
    private static int Sample(Image image, int pixel, int plane) => image.Pixels[pixel * image.Channels + (image.Channels < 3 ? 0 : plane)];

    private static Image Load(string path)
    {
        var data = File.ReadAllBytes(path);
        int pos = 8, width = 0, height = 0, channels = 0;
        using var idat = new MemoryStream();
        while (pos < data.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            var kind = Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = data.AsSpan(pos + 8, length);
            pos += 12 + length;
            if (kind == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                int depth = body[8], color = body[9], interlace = body[12];
                if (depth != 8 || interlace != 0 || color is not (0 or 2 or 4 or 6)) throw new TestFailed($"{path}: unsupported PNG (depth {depth}, color {color})");
                channels = color switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
            }
            else if (kind == "IDAT") idat.Write(body);
        }
        idat.Position = 0;
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        var bytes = raw.ToArray();
        int stride = width * channels;
        var pixels = new byte[stride * height];
        var previous = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = bytes[y * (stride + 1)];
            var line = bytes.AsSpan(y * (stride + 1) + 1, stride);
            for (int i = 0; i < stride; i++)
            {
                int left = i >= channels ? line[i - channels] : 0;
                int up = previous[i];
                int upLeft = i >= channels ? previous[i - channels] : 0;
                int predicted = filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => 0,
                };
                line[i] = (byte)(line[i] + predicted);
            }
            line.CopyTo(pixels.AsSpan(y * stride));
            line.CopyTo(previous);
        }
        return new Image(width, height, channels, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
