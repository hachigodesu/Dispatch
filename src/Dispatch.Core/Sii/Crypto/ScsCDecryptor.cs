using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Dispatch.Core.Sii.Crypto;

/// <summary>
/// Decrypts "ScsC" files (encrypted SII, e.g. profile.sii and saves).
/// </summary>
/// <remarks>
/// Layout: magic (4) · HMAC-SHA256 (32) · AES IV (16) · decompressed size (4) · ciphertext.
/// The ciphertext is AES-256-CBC; the plaintext is a zlib stream whose content is text or binary SII.
/// The AES key is a fixed, publicly documented value. The HMAC key is not public, which is why
/// Dispatch never writes ScsC files: the game would reject them (see PLAN.md §5.7).
/// </remarks>
public static class ScsCDecryptor
{
    public const int HeaderSize = 4 + 32 + 16 + 4;

    private static readonly byte[] AesKey =
    [
        0x2A, 0x5F, 0xCB, 0x17, 0x91, 0xD2, 0x2F, 0xB6, 0x02, 0x45, 0xB3, 0xD8, 0x36, 0x9E, 0xD0, 0xB2,
        0xC2, 0x73, 0x71, 0x56, 0x3F, 0xBF, 0x1F, 0x3C, 0x9E, 0xDF, 0x6B, 0x11, 0x82, 0x5A, 0x5D, 0x0A,
    ];

    /// <summary>Decrypts and inflates an ScsC file, returning the inner payload bytes.</summary>
    /// <exception cref="InvalidDataException">The data is not a valid ScsC file.</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderSize || SiiFormatDetector.Detect(file) != SiiFormat.Encrypted)
        {
            throw new InvalidDataException("Not an ScsC (encrypted SII) file.");
        }

        ReadOnlySpan<byte> iv = file.Slice(36, 16);
        uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(52, 4));
        ReadOnlySpan<byte> cipherText = file[HeaderSize..];
        if (cipherText.Length == 0 || cipherText.Length % 16 != 0)
        {
            throw new InvalidDataException("ScsC ciphertext length is not a multiple of the AES block size.");
        }

        byte[] compressed;
        using (var aes = Aes.Create())
        {
            aes.Key = AesKey;
            try
            {
                compressed = aes.DecryptCbc(cipherText, iv, PaddingMode.PKCS7);
            }
            catch (CryptographicException)
            {
                // Some writers don't pad; fall back to no padding (zlib knows where its stream ends).
                compressed = aes.DecryptCbc(cipherText, iv, PaddingMode.None);
            }
        }

        // Trust the inflated length, not the declared size: other tools report it can be off.
        using var input = new MemoryStream(compressed, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(capacity: (int)Math.Min(declaredSize + 16u, 64u * 1024 * 1024));
        try
        {
            zlib.CopyTo(output);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("ScsC payload did not inflate (wrong key or corrupt file).", ex);
        }

        return output.ToArray();
    }
}
