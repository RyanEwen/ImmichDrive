Add a new settings page.

1. Create `ImmichDrive/Pages/<Name>Page.xaml` + `.xaml.cs` as a WinUI 3 `Page` (see existing
   pages — `ScrollViewer` + `StackPanel`, `MaxWidth` ~760, card `Border`s).
2. In `SettingsWindow.xaml`, add a `NavigationViewItem` with `Tag="<Name>Page"` and an icon
   (`SymbolIcon` or `FontIcon` glyph).
3. In `SettingsWindow.xaml.cs`, add the `Tag`→`typeof(<Name>Page)` case to `GetPageTypeFromTag`.
4. Fully-qualify `Microsoft.UI.Xaml.Visibility` / `FocusState` in page code-behind if used.
