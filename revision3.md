# Revision 3 — Merged MkvPlayer into this project as one app

Previously MkvPlayer (WPF, `D:\Multimedia\MkvPlayer`) and this project (WinForms, "Video Grid
Studio") were two separate apps. This revision merges them into one process, with this project
(`VideoGridStudio.csproj`) as the primary project: MkvPlayer's source moved in, its `MainWindow`
became the startup window, and a new **VideoCreator** menu item opens this project's own former
entry point (`LauncherForm`) as a secondary window. `D:\Multimedia\MkvPlayer` itself was left
untouched — only copied from, not modified or deleted.

## 1. Copied MkvPlayer's source into this project

`App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `PreferencesWindow.xaml(.cs)`, `MediaItem.cs` to the
project root; `Audio/`, `Equalizer/`, `Settings/`, `Resources/background.png` as new folders;
`Controls/OscilloscopeControl.cs` and `OscilloscopeLineControl.cs` into the existing `Controls/`
folder alongside `VideoCellControl.cs` (different namespaces -- `MkvPlayer.Controls` vs.
`VideoGridStudio.Controls` -- so no clash). Not copied: `MkvPlayer.csproj` itself (merging into
this one instead), `Properties/PublishProfiles/` (ClickOnce config specific to the standalone
app), `MkvPlayer/README.md` (superseded by this project's own README), and a stray duplicate
`background.png` that wasn't actually referenced by anything.

Checked before copying: the two codebases have **no type-name collisions** (`MkvPlayer.*` vs.
`VideoGridStudio.*`), so nothing needed renaming.

## 2. One project, two UI frameworks

`VideoGridStudio.csproj` gained `<UseWPF>true</UseWPF>` alongside its existing
`<UseWindowsForms>true</UseWindowsForms>` -- an officially supported combination. Added
`LibVLCSharp.WPF` (matching the already-pinned `LibVLCSharp` 3.9.4, rather than MkvPlayer's older
3.8.*) for `MainWindow`'s video tab, and `NAudio` for its Audio/Voice Record tabs. Added
`<Resource Include="Resources\background.png" />` for the tab-control background image.

`Program.cs` (the old WinForms `[STAThread] static void Main()`, which called
`Application.Run(new LauncherForm(libVlc))`) was deleted. `App.xaml`'s
`StartupUri="MainWindow.xaml"` (an `ApplicationDefinition`, auto-detected by the SDK) is now the
sole entry point, so the app boots straight into MkvPlayer's window.

**Gotcha hit and fixed**: with both `UseWPF` and `UseWindowsForms` true and `ImplicitUsings`
enabled, the SDK's implicit global usings for the WinForms side (`System.Windows.Forms`,
`System.Drawing`) collided project-wide with WPF's own types with the same short names
(`Application`, `Color`, `Pen`, `DragEventArgs`, `KeyEventArgs`...), breaking every WPF file that
explicitly used `System.Windows`/`System.Windows.Media`/`System.Windows.Input`. Every file that
actually needs the WinForms/Drawing types already has its own explicit `using` for them (neither
codebase relied on the implicit ones), so the fix was to drop just those two from the implicit set
project-wide:
```xml
<Using Remove="System.Windows.Forms" />
<Using Remove="System.Drawing" />
```
That removal also happened to drop `System.IO` from the generated implicit-usings set for this
particular combined-SDK scenario (confirmed by inspecting the generated `GlobalUsings.g.cs` before
and after), which broke `Path`/`File`/`Directory` usage across several files that relied on it
implicitly -- fixed by explicitly re-adding `<Using Include="System.IO" />` in the same
`ItemGroup`.

A second, related gotcha: the SDK-generated `ApplicationConfiguration.Initialize()` helper (what
`Application.Run()` used to call implicitly to set up WinForms high-DPI mode/visual styles) wasn't
reliably generated across every build pass of this combined project (a WPF build runs the C#
compiler more than once, through an intermediate "_wpftmp" project, to resolve XAML-referenced
types). Rather than depend on that generated class, `App.xaml.cs`'s `OnStartup` now calls the
three WinForms bootstrap methods directly and fully-qualified:
```csharp
System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
System.Windows.Forms.Application.EnableVisualStyles();
System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
```
(Fully-qualified rather than a new `using System.Windows.Forms;` in that file, since it would make
the bare `Application` in `App : Application` ambiguous again.)

## 3. The VideoCreator menu

`MainWindow.xaml` gained a new top-level, directly-clickable menu item (no submenu) next to
File/Tools/Help:
```xml
<MenuItem Header="_VideoCreator" Foreground="White" Click="VideoCreatorMenuItem_Click"/>
```
`MainWindow.xaml.cs`'s handler creates its own `LibVLC` instance (separate from the one
`MainWindow`'s own video tab owns -- the two engines were already independent, and this avoids
touching MkvPlayer's proven `MainWindow_Loaded`/`_Closed` lifecycle at all) using the same
try/catch-and-show-a-MessageBox shape the old `Program.cs` used for its own LibVLC bootstrap, then
shows `LauncherForm` **non-modally** (`.Show()`, not `.ShowDialog()`) so the player stays usable
while the grid tools are open. The `LibVLC` instance is disposed once that flow's `LauncherForm`
fully closes (`FormClosed`).

Showing a WinForms `Form` from a WPF event handler works without ever calling
`System.Windows.Forms.Application.Run()` -- that call is only a convenience wrapper around "show a
form and pump messages until it closes"; the WPF `Dispatcher` already pumps the same underlying
Win32 message loop on that thread, so `Form.Show()`/`.ShowDialog()` behave correctly on their own,
as long as the WinForms bootstrap (step 2, above) has already run once.

## Validated

- `dotnet build VideoGridStudio.sln -c Debug` -- succeeds, 0 errors (only the pre-existing
  unrelated `WFAC010` high-DPI warning, now emitted once per build pass).
- Ran the built app: it opens directly on **MKV/MP4 Player** (not a picker); the menu bar reads
  File / Tools / Help / VideoCreator.
- Clicked **VideoCreator**: **Video Grid Studio**'s `LauncherForm` opened on top, with the player
  window still visible and open behind it (non-modal, as intended).
- Clicked **Play Together (Grid)** from there: the full 4x4 tile grid opened and rendered
  correctly, confirming the WinForms side works unchanged inside the merged process.
