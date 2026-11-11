using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace Dispatch.App.Services;

/// <summary>Sends files or folders to the Windows Recycle Bin (recoverable until the bin is emptied).</summary>
public static class RecycleBin
{
    public static void Send(string path)
    {
        if (Directory.Exists(path))
        {
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        else if (File.Exists(path))
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }
}
