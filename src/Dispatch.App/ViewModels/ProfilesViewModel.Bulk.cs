using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Dispatch.App.Resources;
using Dispatch.Core.Profiles;
using Dispatch.Core.Safety;
using Serilog;

namespace Dispatch.App.ViewModels;

/// <summary>Deleting profiles, recycling old whole-profile backups, and adding/removing mods from the Mods tab.</summary>
public sealed partial class ProfilesViewModel
{
    /// <summary>Profiles the Mods tab can add mods to (local and readable; Steam Cloud ones are read-only).</summary>
    public IReadOnlyList<ProfileItemViewModel> EditableProfiles => Items.Where(i => !i.IsCloud && i.LoadOrder is not null).ToList();

    // ---- delete -------------------------------------------------------------------------------

    private bool CanDelete() => CanEdit && !IsGameRunning && !IsWorking;

    /// <summary>Moves the profile folder into Dispatch's backups (restorable from Settings).</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteProfileAsync()
    {
        var item = Selected!;
        string question = string.Format(CultureInfo.CurrentCulture, Strings.ConfirmDeleteProfile,
                                        item.Name, item.LoadOrder?.TopFirst.Count ?? 0, item.Profile.SaveCount);
        if (!_dialogs.Confirm(Strings.ConfirmDeleteProfileTitle, question))
        {
            return;
        }

        IsDirty = false; // edits to a profile being deleted don't matter
        await RunProfileOperation(() =>
        {
            var backup = ProfileRemover.Delete(_writer(), _backups(), item.Profile);
            Log.Information("Deleted profile {Folder}; kept as backup {Backup}", item.Profile.FolderName, backup.Id);
            return ("", string.Format(CultureInfo.CurrentCulture, Strings.DeletedStatus, item.Name), false);
        });
    }

    // ---- recycle old whole-profile backups ----------------------------------------------------

    private bool CanRecycle() => SelectedFullBackup is not null && !IsWorking;

    /// <summary>Frees the disk space of a whole-profile backup by sending it to the Windows Recycle Bin.</summary>
    [RelayCommand(CanExecute = nameof(CanRecycle))]
    private void RecycleFullBackup()
    {
        var item = SelectedFullBackup!;
        string question = string.Format(CultureInfo.CurrentCulture, Strings.ConfirmRecycle, item.Backup.ProfileName, item.When,
                                        $"{item.Backup.SizeBytes / 1048576.0:F1} MB");
        if (!_dialogs.Confirm(Strings.ConfirmRecycleTitle, question))
        {
            return;
        }

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.Backup.Path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            Log.Information("Moved whole-profile backup {Backup} to the Recycle Bin", item.Backup.Id);
            FullBackups.Remove(item);
            SelectedFullBackup = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _dialogs.ShowError(ex.Message);
        }
    }

    // ---- copy load order to other profiles (v1) -----------------------------------------------

    private bool CanCopyLoadOrder() =>
        Selected?.LoadOrder is not null && !IsDirty && !IsGameRunning && !IsWorking && EditableProfiles.Any(p => p != Selected);

    /// <summary>Replaces other profiles' load orders with this one (each target backed up first).</summary>
    [RelayCommand(CanExecute = nameof(CanCopyLoadOrder))]
    private async Task CopyLoadOrderAsync()
    {
        var source = Selected!;
        var order = source.LoadOrder!.TopFirst;
        var targets = _dialogs.PickProfiles(Strings.CopyLoadOrderTitle,
            string.Format(CultureInfo.CurrentCulture, Strings.CopyLoadOrderPrompt, source.Name, order.Count),
            EditableProfiles.Where(p => p.Profile.MainDir != source.Profile.MainDir).ToList());
        if (targets.Count == 0)
        {
            return;
        }

        IsWorking = true;
        int done = 0;
        try
        {
            foreach (var target in targets)
            {
                var current = ProfileRepository.ReadLoadOrder(target.Profile); // fresh: the game may have saved
                var backup = LoadOrderService.Save(_writer(), target.Profile, current, order);
                Log.Information("Copied load order of {Source} to {Target} ({Count} mods), backup {Backup}",
                                source.Profile.FolderName, target.Profile.FolderName, order.Count, backup.Id);
                done++;
            }
        }
        catch (Exception ex) when (ex is GameRunningException or ConcurrentChangeException or InvalidOperationException
                                       or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(ex.Message);
        }
        finally
        {
            IsWorking = false;
        }

        _keepSelection = source.Profile.MainDir;
        await _refresh();
        Message = string.Format(CultureInfo.CurrentCulture, Strings.CopiedStatus, source.Name, done);
    }

    // ---- add / remove from the Mods tab --------------------------------------------------------

    /// <summary>
    /// Adds mods to the top of a profile. If that profile is open in the editor with unsaved changes, they're added
    /// there instead (so nothing is lost); otherwise they're saved straight away through the safe save.
    /// </summary>
    public async Task<string?> AddModsAsync(IReadOnlyList<ModItemViewModel> mods, ProfileItemViewModel target) =>
        await ChangeProfileAsync(target, mods.Count,
            editor: () => InsertMany(mods, 0),
            change: current => LoadOrderEdits.AddToTop(current, mods.Select(m => new ActiveModEntry(m.PackageId, m.DisplayName))),
            Strings.AddedToProfileStatus, Strings.AddedToUnsavedStatus);

    public async Task<string?> RemoveModsAsync(IReadOnlyList<ModItemViewModel> mods, ProfileItemViewModel target)
    {
        var ids = mods.Select(m => m.PackageId).ToHashSet(StringComparer.Ordinal);
        return await ChangeProfileAsync(target, mods.Count,
            editor: () =>
            {
                foreach (var row in LoadOrder.Where(l => ids.Contains(l.PackageId)).ToList())
                {
                    LoadOrder.Remove(row);
                }
            },
            change: current => LoadOrderEdits.Remove(current, ids),
            Strings.RemovedFromProfileStatus, Strings.RemovedFromUnsavedStatus);
    }

    private async Task<string?> ChangeProfileAsync(ProfileItemViewModel target, int count, Action editor,
                                                   Func<IReadOnlyList<ActiveModEntry>, List<ActiveModEntry>> change,
                                                   string savedFormat, string unsavedFormat)
    {
        if (Selected?.Profile.MainDir == target.Profile.MainDir && IsDirty)
        {
            editor();
            return Message = string.Format(CultureInfo.CurrentCulture, unsavedFormat, count, target.Name);
        }

        return await SaveChangeAsync(target, change, string.Format(CultureInfo.CurrentCulture, savedFormat, count, target.Name));
    }

    /// <summary>
    /// A change to a profile that isn't open with unsaved edits (the health report's fixes). Refused, with a message,
    /// while that profile has unsaved changes in the editor, so nothing the user is working on gets mixed in.
    /// </summary>
    internal async Task<string?> FixProfileAsync(ProfileItemViewModel target, Func<IReadOnlyList<ActiveModEntry>, List<ActiveModEntry>> change, string done)
    {
        if (target.IsCloud)
        {
            return null;
        }

        if (Selected?.Profile.MainDir == target.Profile.MainDir && IsDirty)
        {
            _dialogs.ShowError(string.Format(CultureInfo.CurrentCulture, Strings.HealthUnsavedFirst, target.Name));
            return null;
        }

        return await SaveChangeAsync(target, change, done);
    }

    private async Task<string?> SaveChangeAsync(ProfileItemViewModel target, Func<IReadOnlyList<ActiveModEntry>, List<ActiveModEntry>> change, string done)
    {
        try
        {
            // Read fresh (the game may have saved since the last refresh), change, save with a backup.
            var current = ProfileRepository.ReadLoadOrder(target.Profile);
            var updated = change(current.TopFirst);
            if (updated.Select(e => e.PackageId).SequenceEqual(current.TopFirst.Select(e => e.PackageId)))
            {
                return string.Format(CultureInfo.CurrentCulture, Strings.NothingToAdd, target.Name);
            }

            var backup = LoadOrderService.Save(_writer(), target.Profile, current, updated);
            Log.Information("Changed load order of {Folder} ({Before} → {After} mods), backup {Backup}",
                            target.Profile.FolderName, current.TopFirst.Count, updated.Count, backup.Id);
            _keepSelection = Selected?.Profile.MainDir;
            await _refresh();
            return Message = done;
        }
        catch (Exception ex) when (ex is GameRunningException or ConcurrentChangeException or InvalidOperationException
                                       or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Changing {Folder} from the Mods tab failed", target.Profile.FolderName);
            _dialogs.ShowError(ex.Message);
            return null;
        }
    }
}
