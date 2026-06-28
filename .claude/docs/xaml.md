# WinUI 3 XAML conventions

- Settings UI is WinUI 3 (`Microsoft.WindowsAppSDK`). `SettingsWindow` uses `MicaBackdrop`,
  `ExtendsContentIntoTitleBar` + a custom 48px title-bar grid, and a `NavigationView` +
  `Frame`. Page switching is a `Tag`→`typeof(Page)` `switch` in `GetPageTypeFromTag` — no MVVM
  routing framework.
- Pages are `Page` objects (not `UserControl`), shaped as
  `ScrollViewer Padding="32,16,32,32"` → `StackPanel Spacing="16" MaxWidth="~760"`, content
  grouped in card `Border`s (`CardBackgroundFillColorDefaultBrush` / `CardStrokeColorDefaultBrush`,
  `CornerRadius="8" Padding="20"`).
- Use built-in styles: `TitleTextBlockStyle`, `SubtitleTextBlockStyle`, `CaptionTextBlockStyle`,
  `AccentButtonStyle`. Icons via `FontIcon Glyph="&#xExxx;"` (Segoe Fluent Icons) or `SymbolIcon`.
- In page code-behind, fully-qualify `Microsoft.UI.Xaml.Visibility` and `FocusState` (they
  collide with WinRT `Windows.UI.Xaml.*`). `global::` does not parse inside interpolated
  strings — assign to a local first.
- Keep strings inline for now (single app, en-US). If localization is added later, move to a
  `Resources/Localization/Dictionary-en-US.xaml` ResourceDictionary like LittleLauncher.
