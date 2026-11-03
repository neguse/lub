using System.Collections.Generic;
using TcsC;
using TinySystem.Text;

namespace TcsVm;

/// <summary>.tcsb の byte 列の道具。byte は 0〜255 の int の List (little endian の書き込みは TcsC.TypeTable と同じ)。
/// host へは 1 文字が 1 byte の文字列 (ToByteString) で渡す。</summary>
public static class VBytes
{
    public static void U8(List<int> w, int v)
    {
        w.Add(v & 255);
    }

    public static void U16(List<int> w, int v)
    {
        w.Add(v & 255);
        w.Add(v >> 8 & 255);
    }

    public static void I32(List<int> w, int v)
    {
        w.Add(v & 255);
        w.Add(v >> 8 & 255);
        w.Add(v >> 16 & 255);
        w.Add(v >> 24 & 255);
    }

    public static void Bytes(List<int> w, List<int> bytes)
    {
        foreach (var b in bytes) w.Add(b);
    }

    /// <summary>文字列の UTF-8 の byte。</summary>
    public static List<int> Utf8(string s)
    {
        var u = new Utf8Bytes(s);
        var r = new List<int>();
        for (int i = 0; i < u.Length; i++) r.Add(u[i]);
        return r;
    }

    /// <summary>1 文字が 1 byte の文字列 (host の WriteBinaryAtomic と標準出力に渡す形)。</summary>
    public static string ToByteString(List<int> bytes)
    {
        var parts = new List<string>();
        var chunk = new List<string>();
        foreach (var b in bytes)
        {
            chunk.Add(((char)b).ToString());
            if (chunk.Count == 4096)
            {
                parts.Add(string.Join("", chunk));
                chunk = new List<string>();
            }
        }
        parts.Add(string.Join("", chunk));
        return string.Join("", parts);
    }

    private static string Base64Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    private static string B64(int i) => new Utf8Bytes(Base64Chars).Slice(i, 1).ToString();

    /// <summary>base64 (RFC 4648、= の埋め草つき。.NET の Convert.ToBase64String と同じ)。</summary>
    public static string Base64(List<int> bytes)
    {
        var parts = new List<string>();
        int n = bytes.Count;
        int i = 0;
        while (i + 2 < n)
        {
            int v = bytes[i] << 16 | bytes[i + 1] << 8 | bytes[i + 2];
            parts.Add(B64(v >> 18 & 63) + B64(v >> 12 & 63) + B64(v >> 6 & 63) + B64(v & 63));
            i += 3;
        }
        if (n - i == 1)
        {
            int v = bytes[i] << 16;
            parts.Add(B64(v >> 18 & 63) + B64(v >> 12 & 63) + "==");
        }
        else if (n - i == 2)
        {
            int v = bytes[i] << 16 | bytes[i + 1] << 8;
            parts.Add(B64(v >> 18 & 63) + B64(v >> 12 & 63) + B64(v >> 6 & 63) + "=");
        }
        return string.Join("", parts);
    }

    private static string HexDigits = "0123456789abcdef";

    /// <summary>32 bit の並びの 16 進 (8 桁、小文字)。</summary>
    public static string Hex8(int v)
    {
        var parts = new List<string>();
        for (int k = 7; k >= 0; k--) parts.Add(new Utf8Bytes(HexDigits).Slice(v >> (k * 4) & 15, 1).ToString());
        return string.Join("", parts);
    }

    // ---- SHA-256 (FIPS 180-4)。32 bit の値は int に並びとして入れる (加算は 2 の補数で桁あふれする) ----

    private static List<int>? _k;

    private static List<int> K()
    {
        if (_k != null) return _k;
        var k = new List<int>();
        foreach (var s in "1116352408, 1899447441, -1245643825, -373957723, 961987163, 1508970993, -1841331548, -1424204075, -670586216, 310598401, 607225278, 1426881987, 1925078388, -2132889090, -1680079193, -1046744716, -459576895, -272742522, 264347078, 604807628, 770255983, 1249150122, 1555081692, 1996064986, -1740746414, -1473132947, -1341970488, -1084653625, -958395405, -710438585, 113926993, 338241895, 666307205, 773529912, 1294757372, 1396182291, 1695183700, 1986661051, -2117940946, -1838011259, -1564481375, -1474664885, -1035236496, -949202525, -778901479, -694614492, -200395387, 275423344, 430227734, 506948616, 659060556, 883997877, 958139571, 1322822218, 1537002063, 1747873779, 1955562222, 2024104815, -2067236844, -1933114872, -1866530822, -1538233109, -1090935817, -965641998".Split(',')) k.Add(int.Parse(s.Trim()));
        _k = k;
        return k;
    }

    private static int Shr(int x, int n) => x >> n & ((1 << (32 - n)) - 1);

    private static int Rotr(int x, int n) => Shr(x, n) | x << (32 - n);

    /// <summary>SHA-256 の 32 byte の digest。</summary>
    public static List<int> Sha256(List<int> message)
    {
        var k = K();
        var h = new List<int>();
        foreach (var s in "1779033703, -1150833019, 1013904242, -1521486534, 1359893119, -1694144372, 528734635, 1541459225".Split(',')) h.Add(int.Parse(s.Trim()));
        var data = new List<int>();
        foreach (var b in message) data.Add(b);
        int bitLength = message.Count * 8;
        data.Add(128);
        while (data.Count % 64 != 56) data.Add(0);
        // 長さは 64 bit の big endian (上の 32 bit は 0: 512MB 未満)
        for (int i = 0; i < 4; i++) data.Add(0);
        data.Add(bitLength >> 24 & 255);
        data.Add(bitLength >> 16 & 255);
        data.Add(bitLength >> 8 & 255);
        data.Add(bitLength & 255);
        var w = new List<int>();
        for (int i = 0; i < 64; i++) w.Add(0);
        for (int chunk = 0; chunk < data.Count; chunk += 64)
        {
            for (int i = 0; i < 16; i++)
            {
                int p = chunk + i * 4;
                w[i] = data[p] << 24 | data[p + 1] << 16 | data[p + 2] << 8 | data[p + 3];
            }
            for (int i = 16; i < 64; i++)
            {
                int s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ Shr(w[i - 15], 3);
                int s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ Shr(w[i - 2], 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }
            int a = h[0];
            int b2 = h[1];
            int c = h[2];
            int d = h[3];
            int e = h[4];
            int f = h[5];
            int g = h[6];
            int hh = h[7];
            for (int i = 0; i < 64; i++)
            {
                int S1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
                int ch = (e & f) ^ (~e & g);
                int t1 = hh + S1 + ch + k[i] + w[i];
                int S0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
                int maj = (a & b2) ^ (a & c) ^ (b2 & c);
                int t2 = S0 + maj;
                hh = g;
                g = f;
                f = e;
                e = d + t1;
                d = c;
                c = b2;
                b2 = a;
                a = t1 + t2;
            }
            h[0] = h[0] + a;
            h[1] = h[1] + b2;
            h[2] = h[2] + c;
            h[3] = h[3] + d;
            h[4] = h[4] + e;
            h[5] = h[5] + f;
            h[6] = h[6] + g;
            h[7] = h[7] + hh;
        }
        var digest = new List<int>();
        foreach (var x in h)
        {
            digest.Add(x >> 24 & 255);
            digest.Add(x >> 16 & 255);
            digest.Add(x >> 8 & 255);
            digest.Add(x & 255);
        }
        return digest;
    }

    /// <summary>文字列の SHA-256 の先頭 4 byte の 16 進 (lambda の本文の指紋。C# 版の ToHexString(SHA256)[..8] の小文字)。</summary>
    public static string Sha256Hex8(string text)
    {
        var d = Sha256(Utf8(text));
        return Hex8(d[0] << 24 | d[1] << 16 | d[2] << 8 | d[3]);
    }
}
