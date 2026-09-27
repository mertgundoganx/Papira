namespace Papira.Barcodes;

/// <summary>
/// Reed–Solomon error correction over GF(256) with the QR code field polynomial (x^8 + x^4 + x^3 + x^2 + 1).
/// </summary>
internal static class ReedSolomon
{
    /// <summary>Coefficients of the divisor polynomial for the given number of error correction codewords.</summary>
    public static byte[] Generator(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;

        // The generator is the product of (x - r^i); root is r^i as the loop advances.
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                    result[j] ^= result[j + 1];
            }

            root = Multiply(root, 2);
        }

        return result;
    }

    /// <summary>Error correction codewords of a data block.</summary>
    public static byte[] Remainder(byte[] data, byte[] generator)
    {
        var result = new byte[generator.Length];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;

            for (var i = 0; i < result.Length; i++)
                result[i] ^= Multiply(generator[i], factor);
        }

        return result;
    }

    private static byte Multiply(byte a, byte b)
    {
        var result = 0;
        for (var i = 7; i >= 0; i--)
        {
            result = result << 1 ^ (result >> 7) * 0x11D;
            result ^= (b >> i & 1) * a;
        }

        return (byte)result;
    }
}
