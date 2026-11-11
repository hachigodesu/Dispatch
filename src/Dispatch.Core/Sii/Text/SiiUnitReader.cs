using System.Text;

namespace Dispatch.Core.Sii.Text;

/// <summary>One attribute line: <c>key: value</c>, <c>key[]: value</c> or <c>key[3]: value</c>.</summary>
public sealed record SiiField(string Key, bool IsArrayItem, int? Index, string RawValue)
{
    /// <summary>The value with quotes and escapes removed.</summary>
    public string Value => SiiString.Unquote(RawValue);
}

public sealed record SiiUnit(string ClassName, string Name, IReadOnlyList<SiiField> Attributes)
{
    public string? Get(string key) => Attributes.LastOrDefault(a => a.Key == key && !a.IsArrayItem)?.Value;

    /// <summary>Array values in either style (<c>key[]: v</c> or <c>key: N</c> + <c>key[i]: v</c>).</summary>
    public IReadOnlyList<string> GetArray(string key) =>
        Attributes.Where(a => a.Key == key && a.IsArrayItem).Select(a => a.Value).ToList();
}

/// <summary>
/// Forgiving reader for small SII text files (manifest.sii, versions.sii). It extracts units and
/// attributes; it is not the lossless round-trip document used for writing profile.sii.
/// </summary>
public static class SiiUnitReader
{
    public static IReadOnlyList<SiiUnit> Read(string text)
    {
        string clean = StripComments(text);
        var units = new List<SiiUnit>();
        int pos = clean.IndexOf("SiiNunit", StringComparison.Ordinal);
        if (pos < 0)
        {
            throw new InvalidDataException("Missing 'SiiNunit' header.");
        }

        pos = clean.IndexOf('{', pos);
        if (pos < 0)
        {
            throw new InvalidDataException("Missing '{' after 'SiiNunit'.");
        }

        pos++;
        while (true)
        {
            int open = clean.IndexOf('{', pos);
            int close = clean.IndexOf('}', pos);
            if (open < 0 || (close >= 0 && close < open))
            {
                break; // end of SiiNunit
            }

            string header = clean[pos..open].Trim();
            int colon = header.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                throw new InvalidDataException($"Malformed unit header '{header}'.");
            }

            int end = FindUnitEnd(clean, open + 1);
            var attrs = ParseAttributes(clean[(open + 1)..end]);
            units.Add(new SiiUnit(header[..colon].Trim(), header[(colon + 1)..].Trim(), attrs));
            pos = end + 1;
        }

        return units;
    }

    private static int FindUnitEnd(string s, int start)
    {
        bool inString = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '}')
            {
                return i;
            }
        }

        throw new InvalidDataException("Unit is not closed with '}'.");
    }

    private static List<SiiField> ParseAttributes(string body)
    {
        var attrs = new List<SiiField>();
        foreach (var rawLine in body.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('@'))
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            bool isArray = false;
            int? index = null;
            int bracket = key.IndexOf('[', StringComparison.Ordinal);
            if (bracket > 0 && key.EndsWith(']'))
            {
                isArray = true;
                string inner = key[(bracket + 1)..^1];
                index = int.TryParse(inner, out int i) ? i : null;
                key = key[..bracket];
            }

            attrs.Add(new SiiField(key, isArray, index, value));
        }

        return attrs;
    }

    /// <summary>Removes <c>#</c>, <c>//</c> and <c>/* */</c> comments, leaving string contents untouched.</summary>
    internal static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    sb.Append(text[++i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
            }
            else if (c == '#' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/'))
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                sb.Append('\n');
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
