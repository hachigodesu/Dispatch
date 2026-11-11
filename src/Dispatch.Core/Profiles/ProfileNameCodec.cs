using System.Text;

namespace Dispatch.Core.Profiles;

/// <summary>
/// Profile folder names are the profile name's UTF-8 bytes as UPPERCASE hex
/// (e.g. "Promods" → 50726F6D6F6473, "日本" → E697A5E69CAC). Reading accepts either case.
/// </summary>
public static class ProfileNameCodec
{
    public static string ToFolderName(string profileName) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(profileName));

    public static bool TryFromFolderName(string folderName, out string profileName)
    {
        profileName = string.Empty;
        if (folderName.Length == 0 || folderName.Length % 2 != 0 || !folderName.All(Uri.IsHexDigit))
        {
            return false;
        }

        try
        {
            byte[] bytes = Convert.FromHexString(folderName);
            profileName = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
