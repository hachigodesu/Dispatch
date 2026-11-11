using System.Diagnostics;

namespace Dispatch.Core.Safety;

/// <summary>Blocks writes while the game runs (safety rule 2 in PLAN.md).</summary>
public static class GameProcessGuard
{
    public static readonly string[] Ets2ProcessNames = ["eurotrucks2"];

    public static bool IsRunning(IEnumerable<string>? processNames = null)
    {
        foreach (var name in processNames ?? Ets2ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            bool any = processes.Length > 0;
            foreach (var p in processes)
            {
                p.Dispose();
            }

            if (any)
            {
                return true;
            }
        }

        return false;
    }

    /// <exception cref="InvalidOperationException">The game is running.</exception>
    public static void ThrowIfRunning()
    {
        if (IsRunning())
        {
            throw new InvalidOperationException("Euro Truck Simulator 2 is running. Close the game before changing profiles.");
        }
    }
}
