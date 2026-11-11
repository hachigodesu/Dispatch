using System.Text;
using Dispatch.Core.Safety;
using Dispatch.Core.Sii;

namespace Dispatch.Core.Profiles;

/// <summary>Saves a new load order to a profile through <see cref="SafeWriter"/>.</summary>
public static class LoadOrderService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <param name="topFirst">The new list, top of the in-game list (highest priority) first.</param>
    /// <param name="loaded">What Dispatch read earlier; used to refuse the save if the file changed since.</param>
    public static Backup Save(SafeWriter writer, ProfileInfo profile, LoadOrderRead loaded, IReadOnlyList<ActiveModEntry> topFirst,
                              BackupReason reason = BackupReason.LoadOrderSave)
    {
        // Decode the current file again (it's identical to what was loaded, or SafeWriter refuses below).
        byte[] current = File.ReadAllBytes(profile.ProfileSiiPath);
        string text = SiiFileCodec.Decode(current).Text;

        // active_mods[0] is the bottom of the in-game list (spike S3), so the file order is the reverse.
        var fileOrder = topFirst.Reverse().ToList();
        string newText = ProfileText.WriteActiveMods(text, fileOrder);
        byte[] content = Utf8NoBom.GetBytes(newText);

        var plan = new WritePlan(
            profile,
            reason,
            [new FileWrite("profile.sii", content, loaded.SourceHash)],
            AlsoBackUp: ["profile.bak.sii"],
            Verify: (_, written) =>
            {
                var check = ProfileText.ReadActiveMods(SiiFileCodec.Decode(written).Text);
                if (!check.SequenceEqual(fileOrder))
                {
                    throw new InvalidDataException("Verification failed: the written mod list doesn't match.");
                }
            });
        return writer.Commit(plan);
    }
}
