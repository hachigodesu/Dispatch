using System.Text;

namespace Dispatch.Core.Detection;

/// <summary>A node of a Valve KeyValues (VDF/ACF) text file: either a string value or a set of children.</summary>
public sealed class VdfNode
{
    private readonly List<KeyValuePair<string, VdfNode>> _children = [];

    public string? Value { get; private init; }

    public IReadOnlyList<KeyValuePair<string, VdfNode>> Children => _children;

    /// <summary>Case-insensitive child lookup (Steam's key casing varies between versions).</summary>
    public VdfNode? this[string key] =>
        _children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    public string? GetString(string key) => this[key]?.Value;

    internal static VdfNode Leaf(string value) => new() { Value = value };

    internal void Add(string key, VdfNode node) => _children.Add(new(key, node));
}

/// <summary>
/// Minimal reader for Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf, loginusers.vdf):
/// quoted or bare tokens, nested braces, // comments, \" and \\ escapes. Conditionals like [$WIN32] are skipped.
/// </summary>
public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        int pos = 0;
        ParseChildren(text, ref pos, root, topLevel: true);
        return root;
    }

    public static VdfNode ParseFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    private static void ParseChildren(string s, ref int pos, VdfNode parent, bool topLevel)
    {
        while (true)
        {
            string? key = NextToken(s, ref pos, out bool isBrace);
            if (key is null)
            {
                if (!topLevel)
                {
                    throw new FormatException("Unexpected end of VDF data.");
                }

                return;
            }

            if (isBrace && key == "}")
            {
                if (topLevel)
                {
                    throw new FormatException("Unexpected '}' in VDF data.");
                }

                return;
            }

            if (isBrace)
            {
                throw new FormatException("Unexpected '{' in VDF data.");
            }

            string? value = NextToken(s, ref pos, out isBrace);
            if (value is null)
            {
                throw new FormatException($"Key '{key}' has no value.");
            }

            if (isBrace && value == "{")
            {
                var child = new VdfNode();
                ParseChildren(s, ref pos, child, topLevel: false);
                parent.Add(key, child);
            }
            else if (isBrace)
            {
                throw new FormatException($"Key '{key}' is followed by '}}'.");
            }
            else
            {
                parent.Add(key, VdfNode.Leaf(value));
            }

            SkipConditional(s, ref pos);
        }
    }

    private static string? NextToken(string s, ref int pos, out bool isBrace)
    {
        isBrace = false;
        SkipWhitespaceAndComments(s, ref pos);
        if (pos >= s.Length)
        {
            return null;
        }

        char c = s[pos];
        if (c is '{' or '}')
        {
            isBrace = true;
            pos++;
            return c.ToString();
        }

        var sb = new StringBuilder();
        if (c == '"')
        {
            pos++;
            while (pos < s.Length && s[pos] != '"')
            {
                if (s[pos] == '\\' && pos + 1 < s.Length)
                {
                    pos++;
                    sb.Append(s[pos] switch { 'n' => '\n', 't' => '\t', _ => s[pos] });
                }
                else
                {
                    sb.Append(s[pos]);
                }

                pos++;
            }

            pos++; // closing quote
            return sb.ToString();
        }

        while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] is not ('{' or '}' or '"'))
        {
            sb.Append(s[pos++]);
        }

        return sb.ToString();
    }

    private static void SkipWhitespaceAndComments(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            if (char.IsWhiteSpace(s[pos]) || s[pos] == '﻿')
            {
                pos++;
            }
            else if (s[pos] == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n')
                {
                    pos++;
                }
            }
            else
            {
                return;
            }
        }
    }

    private static void SkipConditional(string s, ref int pos)
    {
        int save = pos;
        while (pos < s.Length && s[pos] is ' ' or '\t')
        {
            pos++;
        }

        if (pos < s.Length && s[pos] == '[')
        {
            int end = s.IndexOf(']', pos);
            pos = end < 0 ? s.Length : end + 1;
        }
        else
        {
            pos = save;
        }
    }
}
