using System.Security.Cryptography;
using System.Text;

namespace Papira.Pdf;

/// <summary>
/// Standard security handler with AES-256 (revision 6, PDF 2.0): encrypts every string and stream of the
/// document with one file key, which the user and owner passwords unlock.
/// </summary>
internal sealed class PdfEncryptor
{
    private readonly byte[] _fileKey;

    public PdfEncryptor(string userPassword, string ownerPassword, int permissions)
    {
        Permissions = permissions;
        _fileKey = RandomNumberGenerator.GetBytes(32);

        var user = Password(userPassword);
        var owner = Password(ownerPassword);

        var userValidationSalt = RandomNumberGenerator.GetBytes(8);
        var userKeySalt = RandomNumberGenerator.GetBytes(8);
        U = [.. Hash(user, userValidationSalt, []), .. userValidationSalt, .. userKeySalt];
        UserKey = Encrypt(Hash(user, userKeySalt, []), _fileKey);

        var ownerValidationSalt = RandomNumberGenerator.GetBytes(8);
        var ownerKeySalt = RandomNumberGenerator.GetBytes(8);
        O = [.. Hash(owner, ownerValidationSalt, U), .. ownerValidationSalt, .. ownerKeySalt];
        OwnerKey = Encrypt(Hash(owner, ownerKeySalt, U), _fileKey);

        // The permissions block is stored encrypted so a reader can detect tampering.
        var block = new byte[16];
        BitConverter.TryWriteBytes(block, permissions);
        block[4] = block[5] = block[6] = block[7] = 0xFF;
        block[8] = (byte)'T'; // metadata is encrypted too
        block[9] = (byte)'a';
        block[10] = (byte)'d';
        block[11] = (byte)'b';
        RandomNumberGenerator.Fill(block.AsSpan(12));

        // The specification asks for this one block to be encrypted on its own, with no chaining and no
        // padding (ISO 32000-2, 7.6.4.4.3). It is a single block of mostly random bytes that a reader
        // decrypts to check the permissions against, so it carries nothing that could repeat.
        using var aes = Aes.Create();
        aes.Key = _fileKey;
        Perms = aes.EncryptEcb(block, PaddingMode.None);
    }

    public int Permissions { get; }

    /// <summary>The /O, /U, /OE, /UE and /Perms entries of the encryption dictionary.</summary>
    public byte[] O { get; }

    public byte[] U { get; }

    public byte[] OwnerKey { get; }

    public byte[] UserKey { get; }

    public byte[] Perms { get; }

    /// <summary>Encrypts a string or stream with AES-256-CBC; the random initialisation vector is prefixed.</summary>
    public byte[] EncryptData(ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = _fileKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        var iv = RandomNumberGenerator.GetBytes(16);
        aes.IV = iv;
        return [.. iv, .. aes.EncryptCbc(data, iv)];
    }

    /// <summary>Passwords are UTF-8 and at most 127 bytes long (ISO 32000-2, 7.6.4.3.3).</summary>
    private static byte[] Password(string password)
    {
        var bytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
        return bytes.Length <= 127 ? bytes : bytes[..127];
    }

    /// <summary>Hardened password hash of revision 6 (ISO 32000-2, algorithm 2.B).</summary>
    private static byte[] Hash(byte[] password, byte[] salt, byte[] userData)
    {
        var k = SHA256.HashData([.. password, .. salt, .. userData]);

        // Rounds are counted from one, as the specification does.
        for (var round = 1; ; round++)
        {
            var block = new byte[(password.Length + k.Length + userData.Length) * 64];
            var single = (byte[])[.. password, .. k, .. userData];
            for (var i = 0; i < 64; i++)
                single.CopyTo(block, i * single.Length);

            using var aes = Aes.Create();
            aes.Key = k[..16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            var encrypted = aes.EncryptCbc(block, k[16..32], PaddingMode.None);

            var sum = 0;
            for (var i = 0; i < 16; i++)
                sum += encrypted[i];

            k = (sum % 3) switch
            {
                0 => SHA256.HashData(encrypted),
                1 => SHA384.HashData(encrypted),
                _ => SHA512.HashData(encrypted),
            };

            if (round >= 64 && encrypted[^1] <= round - 32)
                return k[..32];
        }
    }

    private static byte[] Encrypt(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        return aes.EncryptCbc(data, new byte[16], PaddingMode.None);
    }
}
