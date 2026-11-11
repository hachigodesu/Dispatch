using System.Diagnostics;
using Microsoft.Win32;

namespace Dispatch.Core.Platform;

/// <summary>
/// The few things Dispatch asks Windows for. Tests replace this with a fake so detection logic
/// can run against a folder tree in a temp directory.
/// </summary>
public interface IPlatform
{
    /// <summary>The user's Documents folder (Known Folder API, so OneDrive redirection is honoured).</summary>
    string DocumentsFolder { get; }

    /// <summary>%LOCALAPPDATA%</summary>
    string LocalAppDataFolder { get; }

    /// <summary>Steam install folder from the registry, or null if Steam isn't installed.</summary>
    string? SteamPath { get; }

    /// <summary>Account ID of the logged-in Steam user, or null when Steam isn't running.</summary>
    uint? ActiveSteamAccountId { get; }

    bool IsProcessRunning(string processName);
}

public sealed class WindowsPlatform : IPlatform
{
    public string DocumentsFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string LocalAppDataFolder => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string? SteamPath
    {
        get
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                // SteamPath uses forward slashes and lower case, e.g. "c:/program files (x86)/steam".
                if (key?.GetValue("SteamPath") is string p && Directory.Exists(p))
                {
                    return PathCase.Canonical(Path.GetFullPath(p));
                }
            }

            using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            return machine?.GetValue("InstallPath") is string m && Directory.Exists(m) ? PathCase.Canonical(Path.GetFullPath(m)) : null;
        }
    }

    public uint? ActiveSteamAccountId
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            return key?.GetValue("ActiveUser") is int id && id != 0 ? (uint)id : null;
        }
    }

    public bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        bool running = processes.Length > 0;
        foreach (var p in processes)
        {
            p.Dispose();
        }

        return running;
    }
}
