# How Dispatch is put together (Milestone 1)

A tour for someone new to WPF. Read it with the code open; every name here is a real file.

## The three layers

```
Dispatch.App   (WPF)      Views (.xaml) ⇄ ViewModels (.cs) ⇄ Services
                                 │
Dispatch.Core  (no UI)    Detection · Mods · Profiles · Sii · Safety · Platform
```

`Dispatch.Core` never references WPF, which is why almost everything can be unit-tested
(`tests/Dispatch.Core.Tests`, 112 tests) without opening a window.

## What happens when you start Dispatch

1. **[App.xaml.cs](../src/Dispatch.App/App.xaml.cs)**, `OnStartup`, is the *composition root*: the one place that creates
   the services (`WindowsPlatform`, `AppPaths`, `AppSettings`), sets up logging (Serilog → `%LOCALAPPDATA%\Dispatch\logs`)
   and creates the window with its view model. There's no DI container, so you can read the whole wiring in a few lines.
2. It calls `RefreshCommand` on **[MainViewModel](../src/Dispatch.App/ViewModels/MainViewModel.cs)**, which runs
   **[GameSnapshot.Load](../src/Dispatch.App/Services/GameSnapshot.cs)** on a background thread (`Task.Run`), so the window
   stays responsive. That one method:
   - **detects** the game: `GameDetector.Detect` ([GameEnvironment.cs](../src/Dispatch.Core/Detection/GameEnvironment.cs)),
     `SteamLocator`, and `Vdf` for Steam's files;
   - **scans** mods: `ModScanner` ([ModScanner.cs](../src/Dispatch.Core/Mods/ModScanner.cs)), using the cache in `%LOCALAPPDATA%\Dispatch\cache`;
   - **reads** profiles: `ProfileRepository` ([ProfileRepository.cs](../src/Dispatch.Core/Profiles/ProfileRepository.cs));
   - **checks** compatibility: `CompatibilityChecker` ([Compatibility.cs](../src/Dispatch.Core/Mods/Compatibility.cs)).
3. Back on the UI thread, `MainViewModel` hands the snapshot to the three tab view models
   ([TabViewModels.cs](../src/Dispatch.App/ViewModels/TabViewModels.cs)): `ProfilesViewModel`, `ModsViewModel`, `SettingsViewModel`.
4. The views redraw automatically. That's data binding, explained next.

## MVVM in Dispatch

- A **View** is XAML that describes layout, e.g. [ProfilesView.xaml](../src/Dispatch.App/Views/ProfilesView.xaml).
  It contains no logic, only *bindings* like `ItemsSource="{Binding Items}"` and `Text="{Binding Name}"`.
- A **ViewModel** is a plain C# class with the properties the view binds to. Dispatch uses the
  **CommunityToolkit.Mvvm** library:
  - `[ObservableProperty] private string _status;` generates a `Status` property that tells the view when it changes;
  - `[RelayCommand] private async Task RefreshAsync()` generates `RefreshCommand`, which a button binds to
    (`Command="{Binding RefreshCommand}"`). While it runs, the button disables itself.
  - `partial void OnSelectedChanged(...)` runs code when a property changes, e.g. loading a profile's load order when
    you pick a profile.
- **ObservableCollection** (`Items`, `LoadOrder`) is a list that tells the view when items are added or removed.
- **Item view models** ([ItemViewModels.cs](../src/Dispatch.App/ViewModels/ItemViewModels.cs)) wrap Core records
  (`ModInfo`, `ProfileInfo`) and add display text, for example `CompatLabel`, `SizeLabel` and `ListSubtitle`.
  Core stays free of UI wording.
- **Converters** ([Converters.cs](../src/Dispatch.App/Converters/Converters.cs)) turn values into something visual:
  compatibility status → badge colour, icon path → image, bool → visible/hidden.
- **DataContext** is how a view finds its view model. `MainWindow` gets `MainViewModel`; each tab then passes on a part
  of it (`<v:ProfilesView DataContext="{Binding Profiles}" />`).

## Strings and translation

All visible text is in [Strings.resx](../src/Dispatch.App/Resources/Strings.resx). XAML uses it with
`{x:Static res:Strings.TabProfiles}`, C# with `Strings.TabProfiles`. After adding a string, run:

```
python -I tools/gen-strings.py
```

That regenerates `Strings.Designer.cs`, and a unit test fails if you forget. A translation later is a
`Strings.de.resx` (etc.) next to it.

## Theme

`ThemeMode="System"` in [App.xaml](../src/Dispatch.App/App.xaml) switches on WPF's built-in Fluent theme (.NET 9+). It
follows Windows' light/dark setting. Shared styles (badges, labels) live in `App.xaml`'s resources.

## Checking the UI without clicking

```
Dispatch.exe --screenshot out.png [tab] [profile]
```

This loads everything, renders the window to a PNG and exits (tab: 0 Profiles, 1 Mods, 2 Settings). It's handy for
spotting layout regressions.

## Safety in M1

M1 **only reads**. Game files are opened with shared read access. The only things Dispatch writes are its own
files under `%LOCALAPPDATA%\Dispatch` (settings, cache, logs). Writing profiles arrives in M2 through the single
safe write path described in PLAN.md §3.
