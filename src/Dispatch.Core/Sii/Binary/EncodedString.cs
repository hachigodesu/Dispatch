using System.Text;

namespace Dispatch.Core.Sii.Binary;

/// <summary>
/// SII "token" strings packed into a 64-bit number: base 38 over the alphabet 0-9, a-z, _ (index 1..37),
/// least-significant digit = first character. At most 12 characters. Bit 63 is reserved by the game.
/// </summary>
public static class EncodedString
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz_";
    private const ulong ValueMask = 0x7FFF_FFFF_FFFF_FFFFUL;
    public const int MaxLength = 12;

    public static string Decode(ulong value)
    {
        value &= ValueMask;
        var sb = new StringBuilder(MaxLength);
        while (value > 0)
        {
            int digit = (int)(value % 38);
            if (digit == 0)
            {
                throw new InvalidDataException($"Invalid encoded-string digit in 0x{value:X16}.");
            }

            sb.Append(Alphabet[digit - 1]);
            value /= 38;
        }

        return sb.ToString();
    }

    public static ulong Encode(string token)
    {
        if (token.Length > MaxLength)
        {
            throw new ArgumentException($"Token '{token}' is longer than {MaxLength} characters.", nameof(token));
        }

        ulong value = 0;
        for (int i = token.Length - 1; i >= 0; i--)
        {
            int index = Alphabet.IndexOf(token[i], StringComparison.Ordinal);
            if (index < 0)
            {
                throw new ArgumentException($"Character '{token[i]}' is not allowed in a token.", nameof(token));
            }

            value = (value * 38) + (ulong)(index + 1);
        }

        return value;
    }

    public static bool IsToken(string s) =>
        s.Length is > 0 and <= MaxLength && s.All(c => Alphabet.Contains(c, StringComparison.Ordinal));
}
