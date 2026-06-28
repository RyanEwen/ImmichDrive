# WinUI 3 XAML conventions

- Settings UI is WinUI 3 (`Microsoft.WindowsAppSDK`). `SettingsWindow` uses `MicaBackdrop`,
  `ExtendsContentIntoTitleBar` + a custom 48px title-bar grid (with an `Image` icon), and a
  `NavigationView` + `Frame`. Page switching is a `Tag`→`typeof(Page)` `switch` in
  `GetPageTypeFromTag` — no MVVM routing framework. Pages live under `Pages/`: **HomePage**,
  **ConnectionPage**, **AboutPage** (in the nav), plus **SettingsPage** (the built-in
  `NavigationView` settings cog, `IsSettingsVisible="True"`).
- Pages are `Page` objects (not `UserControl`), shaped as
  `ScrollViewer Padding="32,16,32,32"` → `StackPanel Spacing` `MaxWidth="~760"`, content
  grouped in card `Border`s (`CardBackgroundFillColorDefaultBrush` / `CardStrokeColorDefaultBrush`,
  `BorderThickness="1"`, `CornerRadius="8"`/`4`, padded ~`16`–`20`).
- **Per-setting cards** (SettingsPage) wrap each setting in a card `Border` holding a two-column
  `Grid` (`Width="*"` label/description column + `Width="Auto"` control column, `ColumnSpacing="16"`),
  so a wrapping description never pushes under the control. SettingsPage currently has just the
  **App theme** and **Start at sign-in** cards; AboutPage uses the same card/Grid shape, including
  a GitHub/Source-code link.
- Use built-in styles: `TitleTextBlockStyle`, `SubtitleTextBlockStyle`, `CaptionTextBlockStyle`,
  `BodyStrongTextBlockStyle`, `AccentButtonStyle`. Icons via `FontIcon Glyph="&#xExxx;"` (Segoe
  Fluent Icons) or `SymbolIcon`.
- **In-app images** (title-bar icon, flyout, About) set their `Source` to the high-res PNG
  (`App.IconImagePath` → `Resources/ImmichDrive.png`), **not** the multi-frame `.ico`, so they
  downscale crisply to 16/22/32px. The `.ico` (`App.IconPath`) is used only for the OS window /
  taskbar / alt-tab icon via `WM_SETICON` + `AppWindow.SetIcon`.
- **Tray status flyout** (`Windows/StatusFlyout.xaml`) is a borderless `Window`:
  `OverlappedPresenter.CreateForContextMenu()` (top-most, light-dismiss), `ExtendsContentIntoTitleBar`,
  a `DesktopAcrylicBackdrop`, and OS rounded corners via the `DWMWA_WINDOW_CORNER_PREFERENCE`
  (`DWMWCP_ROUND`) attribute only — no manual `DWMWA_BORDER_COLOR`. It closes on deactivation.
- Bindable settings are `[ObservableProperty]` partial properties on `UserSettings`, read/written
  through `SettingsManager.Current`. These pages drive them from **code-behind event handlers**
  (e.g. `ThemeCombo_SelectionChanged`, `StartupToggle_Toggled`) rather than `{x:Bind … Mode=TwoWay}`;
  if you do bind a control directly, bind `TwoWay` to `SettingsManager.Current` and guard side-effects
  with the `_initializing` flag.
- In page code-behind, fully-qualify `Microsoft.UI.Xaml.Visibility` and `FocusState` (they
  collide with WinRT `Windows.UI.Xaml.*`). `global::` does not parse inside interpolated
  strings — assign to a local first.
- Keep strings inline for now (single app, en-US). If localization is added later, move to a
  `Resources/Localization/Dictionary-en-US.xaml` ResourceDictionary like LittleLauncher.
