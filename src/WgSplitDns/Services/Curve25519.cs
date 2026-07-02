using System.Numerics;
using System.Security.Cryptography;

namespace WgSplitDns.Services;

// Minimal X25519 (RFC 7748) — only used to derive a public key from a private key.
public static class Curve25519
{
    static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    const int A24 = 121665;

    public static byte[] GeneratePrivateKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Clamp(key);
        return key;
    }

    public static byte[] GetPublicKey(byte[] privateKey)
    {
        var k = (byte[])privateKey.Clone();
        Clamp(k);
        var result = ScalarMult(DecodeLittleEndian(k), new BigInteger(9));
        return EncodeLittleEndian(result);
    }

    static void Clamp(byte[] k)
    {
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;
    }

    static BigInteger ScalarMult(BigInteger k, BigInteger u)
    {
        BigInteger x1 = u, x2 = 1, z2 = 0, x3 = u, z3 = 1;
        int swap = 0;
        for (int t = 254; t >= 0; t--)
        {
            int kt = (int)((k >> t) & 1);
            swap ^= kt;
            if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            swap = kt;
            var a = Mod(x2 + z2);
            var aa = Mod(a * a);
            var b = Mod(x2 - z2);
            var bb = Mod(b * b);
            var e = Mod(aa - bb);
            var c = Mod(x3 + z3);
            var d = Mod(x3 - z3);
            var da = Mod(d * a);
            var cb = Mod(c * b);
            x3 = Mod(BigInteger.Pow(da + cb, 2));
            z3 = Mod(x1 * BigInteger.Pow(da - cb, 2));
            x2 = Mod(aa * bb);
            z2 = Mod(e * (aa + A24 * e));
        }
        if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
        return Mod(x2 * BigInteger.ModPow(z2, P - 2, P));
    }

    static BigInteger Mod(BigInteger v)
    {
        v %= P;
        return v < 0 ? v + P : v;
    }

    static BigInteger DecodeLittleEndian(byte[] b)
    {
        var buf = new byte[33];
        Array.Copy(b, buf, 32);
        return new BigInteger(buf);
    }

    static byte[] EncodeLittleEndian(BigInteger v)
    {
        var bytes = v.ToByteArray();
        var result = new byte[32];
        Array.Copy(bytes, result, Math.Min(32, bytes.Length));
        return result;
    }
}
