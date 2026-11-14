using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dispatch.Core.Sii.Text;

namespace Dispatch.Core.Profiles;

/// <summary>One <c>active_mods</c> entry: <c>"&lt;PackageId&gt;|&lt;Display Name&gt;"</c>.</summary>
public sealed record ActiveModEntry(string PackageId, string DisplayName)
{
    /// <summary>Splits on the first '|' only; display names may contain more pipes.</summary>
    public static ActiveModEntry Parse(string value)
    {
        int bar = value.IndexOf('|', StringComparison.Ordinal);
        return bar < 0 ? new ActiveModEntry(value, value) : new ActiveModEntry(value[..bar], value[(bar + 1)..]);
    }

    public override string ToString() => PackageId + "|" + DisplayName;
}

/// <summary>
/// Surgical edits on decoded profile.sii text. Only <c>active_mods</c> and <c>profile_name</c> inside the
/// <c>user_profile</c> unit are touched; every other byte (comments, line endings, unknown fields) stays as-is.
/// </summary>
/// <remarks>
/// File order: <c>active_mods[0]</c> is the BOTTOM of the in-game list (lowest priority); confirmed in-game in
/// spike S3. These methods work in file order; UI order (top = highest priority) is the reverse.
/// </remarks>
public static partial class ProfileText
{
    public static string? ReadProfileName(string text)
    {
        var (start, end) = UserProfileBody(text);
        var m = ProfileNameRegex().Match(text, start, end - start);
        return m.Success ? SiiString.Unquote(m.Groups["value"].Value) : null;
    }

    /// <summary>The map the profile plays, e.g. "/map/europe.mbd" or a separate map ("/map/grandutopia.mbd"); null if absent.</summary>
    public static string? ReadMapPath(string text)
    {
        var (start, end) = UserProfileBody(text);
        var m = MapPathRegex().Match(text, start, end - start);
        return m.Success ? SiiString.Unquote(m.Groups["value"].Value) : null;
    }

    /// <summary>Entries in file order (index 0 = bottom of the in-game list).</summary>
    public static IReadOnlyList<ActiveModEntry> ReadActiveMods(string text)
    {
        var (start, end) = UserProfileBody(text);
        return ActiveModItemRegex().Matches(text[start..end])
            .Select(m => (Index: int.Parse(m.Groups["index"].Value, CultureInfo.InvariantCulture), Value: m.Groups["value"].Value))
            .OrderBy(x => x.Index)
            .Select(x => ActiveModEntry.Parse(SiiString.Unquote(x.Value)))
            .ToList();
    }

    /// <summary>Replaces the whole active_mods block (count + items) with <paramref name="fileOrder"/>.</summary>
    public static string WriteActiveMods(string text, IReadOnlyList<ActiveModEntry> fileOrder)
    {
        var (start, end) = UserProfileBody(text);
        string body = text[start..end];
        string nl = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var countMatch = ActiveModCountRegex().Match(body);
        if (!countMatch.Success)
        {
            throw new InvalidDataException("profile.sii has no 'active_mods' count line.");
        }

        string indent = countMatch.Groups["indent"].Value;
        var block = new StringBuilder();
        block.Append(indent).Append("active_mods: ").Append(fileOrder.Count.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < fileOrder.Count; i++)
        {
            block.Append(nl).Append(indent).Append("active_mods[").Append(i.ToString(CultureInfo.InvariantCulture))
                 .Append("]: ").Append(SiiString.Quote(fileOrder[i].ToString()));
        }

        // Remove all existing item lines, then put the new block where the count line was.
        string withoutItems = ActiveModItemLineRegex().Replace(body, "");
        var newCount = ActiveModCountRegex().Match(withoutItems);
        string newBody = string.Concat(withoutItems.AsSpan(0, newCount.Index), block.ToString(),
                                       withoutItems.AsSpan(newCount.Index + newCount.Length));
        return string.Concat(text.AsSpan(0, start), newBody, text.AsSpan(end));
    }

    public static string WriteProfileName(string text, string name)
    {
        var (start, end) = UserProfileBody(text);
        string body = text[start..end];
        var m = ProfileNameRegex().Match(body);
        if (!m.Success)
        {
            throw new InvalidDataException("profile.sii has no 'profile_name' line.");
        }

        var value = m.Groups["value"];
        string newBody = string.Concat(body.AsSpan(0, value.Index), SiiString.Format(name), body.AsSpan(value.Index + value.Length));
        return string.Concat(text.AsSpan(0, start), newBody, text.AsSpan(end));
    }

    /// <summary>Reads a single string-valued attribute of user_profile (e.g. "online_user_name"); null if absent.</summary>
    public static string? ReadField(string text, string key)
    {
        var (start, end) = UserProfileBody(text);
        var m = FieldRegex(key).Match(text[start..end]);
        return m.Success ? SiiString.Unquote(m.Groups["value"].Value) : null;
    }

    /// <summary>Replaces a single attribute's value; leaves the text unchanged if the attribute doesn't exist.</summary>
    public static string WriteField(string text, string key, string value)
    {
        var (start, end) = UserProfileBody(text);
        string body = text[start..end];
        var m = FieldRegex(key).Match(body);
        if (!m.Success)
        {
            return text;
        }

        var g = m.Groups["value"];
        string newBody = string.Concat(body.AsSpan(0, g.Index), SiiString.Format(value), body.AsSpan(g.Index + g.Length));
        return string.Concat(text.AsSpan(0, start), newBody, text.AsSpan(end));
    }

    private static Regex FieldRegex(string key) =>
        new($@"^[ \t]*{Regex.Escape(key)}[ \t]*:[ \t]*(?<value>""(?:[^""\\]|\\.)*""|[^\r\n]*?)[ \t]*\r?$", RegexOptions.Multiline);

    /// <summary>Character range of the user_profile unit's body (between its braces).</summary>
    private static (int Start, int End) UserProfileBody(string text)
    {
        var header = UserProfileHeaderRegex().Match(text);
        if (!header.Success)
        {
            throw new InvalidDataException("profile.sii has no 'user_profile' unit.");
        }

        int start = header.Index + header.Length;
        bool inString = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
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
                return (start, i);
            }
        }

        throw new InvalidDataException("user_profile unit is not closed.");
    }

    [GeneratedRegex(@"^\s*user_profile\s*:\s*[^\s{]+\s*\{", RegexOptions.Multiline)]
    private static partial Regex UserProfileHeaderRegex();

    [GeneratedRegex(@"^[ \t]*profile_name[ \t]*:[ \t]*(?<value>""(?:[^""\\]|\\.)*""|[^\r\n]*?)[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex ProfileNameRegex();

    [GeneratedRegex(@"^[ \t]*map_path[ \t]*:[ \t]*(?<value>""(?:[^""\\]|\\.)*""|[^\r\n]*?)[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex MapPathRegex();

    [GeneratedRegex(@"^(?<indent>[ \t]*)active_mods[ \t]*:[ \t]*\d+[ \t]*(?=\r?$)", RegexOptions.Multiline)]
    private static partial Regex ActiveModCountRegex();

    [GeneratedRegex(@"^[ \t]*active_mods\[(?<index>\d+)\][ \t]*:[ \t]*(?<value>""(?:[^""\\]|\\.)*"")[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex ActiveModItemRegex();

    [GeneratedRegex(@"^[ \t]*active_mods\[\d+\][ \t]*:[^\n]*\n?", RegexOptions.Multiline)]
    private static partial Regex ActiveModItemLineRegex();
}
