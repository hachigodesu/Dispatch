using System.Globalization;
using System.Text;

namespace Dispatch.Core.Sii.Text;

/// <summary>
/// Quoting rules for SII text strings, matching what the game itself writes:
/// non-ASCII characters are stored as UTF-8 byte escapes (<c>"日本"</c> → <c>"\xe6\x97\xa5\xe6\x9c\xac"</c>),
/// plus <c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\t</c>, <c>\r</c>.
/// </summary>
public static class SiiString
{
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            switch (b)
            {
                case (byte)'"': sb.Append("\\\""); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                case (byte)'\n': sb.Append("\\n"); break;
                case (byte)'\r': sb.Append("\\r"); break;
                case (byte)'\t': sb.Append("\\t"); break;
                default:
                    if (b is < 0x20 or >= 0x7F)
                    {
                        sb.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append((char)b);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Formats a string value the way the game does: bare when it is a simple word (<c>profile_name: Promods</c>),
    /// quoted otherwise (<c>profile_name: "Tuned EU"</c>). Observed in real profiles (spike S1).
    /// </summary>
    public static string Format(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? value : Quote(value);

    /// <summary>Reverses <see cref="Quote"/>. Accepts quoted or bare input; tolerates raw UTF-8.</summary>
    public static string Unquote(string text)
    {
        ReadOnlySpan<char> s = text.AsSpan().Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            s = s[1..^1];
        }

        var bytes = new List<byte>(s.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char e = s[++i];
                switch (e)
                {
                    case 'n': bytes.Add((byte)'\n'); continue;
                    case 'r': bytes.Add((byte)'\r'); continue;
                    case 't': bytes.Add((byte)'\t'); continue;
                    case 'x' when i + 2 < s.Length &&
                                  byte.TryParse(s.Slice(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte hex):
                        bytes.Add(hex);
                        i += 2;
                        continue;
                    default:
                        c = e; // \" \\ and unknown escapes: keep the character
                        break;
                }
            }

            int n = new Rune(char.IsSurrogate(c) && i + 1 < s.Length ? char.ConvertToUtf32(c, s[++i]) : c).EncodeToUtf8(utf8);
            for (int k = 0; k < n; k++)
            {
                bytes.Add(utf8[k]);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
