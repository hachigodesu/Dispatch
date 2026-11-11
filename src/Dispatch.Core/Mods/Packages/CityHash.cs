using System.Buffers.Binary;
using System.Text;

namespace Dispatch.Core.Mods.Packages;

/// <summary>
/// CityHash64, original 2011 algorithm (v1.0.x), which is what SCS HashFS uses to hash file paths.
/// (CityHash v1.1 changed the short-input functions and gives different results.)
/// </summary>
public static class CityHash
{
    private const ulong K0 = 0xc3a5c85c97cb3127UL;
    private const ulong K1 = 0xb492b66fbe98f273UL;
    private const ulong K2 = 0x9ae16a3b2f90404fUL;
    private const ulong K3 = 0xc949d7c7509e6557UL;

    public static ulong Hash64(string text) => Hash64(Encoding.UTF8.GetBytes(text));

    public static ulong Hash64(ReadOnlySpan<byte> s)
    {
        int len = s.Length;
        if (len <= 32)
        {
            return len <= 16 ? HashLen0To16(s) : HashLen17To32(s);
        }

        if (len <= 64)
        {
            return HashLen33To64(s);
        }

        ulong x = F64(s, len - 40);
        ulong y = F64(s, len - 16) + F64(s, len - 56);
        ulong z = HashLen16(F64(s, len - 48) + (ulong)len, F64(s, len - 24));
        var v = WeakHashLen32WithSeeds(s[(len - 64)..], (ulong)len, z);
        var w = WeakHashLen32WithSeeds(s[(len - 32)..], y + K1, x);
        x = (x * K1) + F64(s, 0);

        int remaining = (len - 1) & ~63;
        int pos = 0;
        do
        {
            var chunk = s[pos..];
            x = Rotate(x + y + v.First + F64(chunk, 8), 37) * K1;
            y = Rotate(y + v.Second + F64(chunk, 48), 42) * K1;
            x ^= w.Second;
            y += v.First + F64(chunk, 40);
            z = Rotate(z + w.First, 33) * K1;
            v = WeakHashLen32WithSeeds(chunk, v.Second * K1, x + w.First);
            w = WeakHashLen32WithSeeds(chunk[32..], z + w.Second, y + F64(chunk, 16));
            (z, x) = (x, z);
            pos += 64;
            remaining -= 64;
        }
        while (remaining != 0);

        return HashLen16(HashLen16(v.First, w.First) + (ShiftMix(y) * K1) + z,
                         HashLen16(v.Second, w.Second) + x);
    }

    private static ulong F64(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(s[offset..]);

    private static uint F32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(s[offset..]);

    private static ulong Rotate(ulong v, int shift) => shift == 0 ? v : (v >> shift) | (v << (64 - shift));

    private static ulong RotateByAtLeast1(ulong v, int shift) => (v >> shift) | (v << (64 - shift));

    private static ulong ShiftMix(ulong v) => v ^ (v >> 47);

    private static ulong HashLen16(ulong u, ulong v)
    {
        const ulong kMul = 0x9ddfea08eb382d69UL;
        ulong a = (u ^ v) * kMul;
        a ^= a >> 47;
        ulong b = (v ^ a) * kMul;
        b ^= b >> 47;
        b *= kMul;
        return b;
    }

    private static ulong HashLen0To16(ReadOnlySpan<byte> s)
    {
        int len = s.Length;
        if (len > 8)
        {
            ulong a = F64(s, 0);
            ulong b = F64(s, len - 8);
            return HashLen16(a, RotateByAtLeast1(b + (ulong)len, len)) ^ b;
        }

        if (len >= 4)
        {
            ulong a = F32(s, 0);
            return HashLen16((ulong)len + (a << 3), F32(s, len - 4));
        }

        if (len > 0)
        {
            byte a = s[0], b = s[len >> 1], c = s[len - 1];
            uint y = a + ((uint)b << 8);
            uint z = (uint)len + ((uint)c << 2);
            return ShiftMix((y * K2) ^ (z * K3)) * K2;
        }

        return K2;
    }

    private static ulong HashLen17To32(ReadOnlySpan<byte> s)
    {
        int len = s.Length;
        ulong a = F64(s, 0) * K1;
        ulong b = F64(s, 8);
        ulong c = F64(s, len - 8) * K2;
        ulong d = F64(s, len - 16) * K0;
        return HashLen16(Rotate(a - b, 43) + Rotate(c, 30) + d, a + Rotate(b ^ K3, 20) - c + (ulong)len);
    }

    private static ulong HashLen33To64(ReadOnlySpan<byte> s)
    {
        int len = s.Length;
        ulong z = F64(s, 24);
        ulong a = F64(s, 0) + (((ulong)len + F64(s, len - 16)) * K0);
        ulong b = Rotate(a + z, 52);
        ulong c = Rotate(a, 37);
        a += F64(s, 8);
        c += Rotate(a, 7);
        a += F64(s, 16);
        ulong vf = a + z;
        ulong vs = b + Rotate(a, 31) + c;
        a = F64(s, 16) + F64(s, len - 32);
        z = F64(s, len - 8);
        b = Rotate(a + z, 52);
        c = Rotate(a, 37);
        a += F64(s, len - 24);
        c += Rotate(a, 7);
        a += F64(s, len - 16);
        ulong wf = a + z;
        ulong ws = b + Rotate(a, 31) + c;
        ulong r = ShiftMix(((vf + ws) * K2) + ((wf + vs) * K0));
        return ShiftMix((r * K0) + vs) * K2;
    }

    private readonly record struct Pair(ulong First, ulong Second);

    private static Pair WeakHashLen32WithSeeds(ReadOnlySpan<byte> s, ulong a, ulong b) =>
        WeakHashLen32WithSeeds(F64(s, 0), F64(s, 8), F64(s, 16), F64(s, 24), a, b);

    private static Pair WeakHashLen32WithSeeds(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a += w;
        b = Rotate(b + a + z, 21);
        ulong c = a;
        a += x;
        a += y;
        b += Rotate(a, 44);
        return new Pair(a + z, b + c);
    }
}
