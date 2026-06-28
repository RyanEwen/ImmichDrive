Add a new user setting.

1. In `ImmichDrive/ViewModels/UserSettings.cs`, add an `[ObservableProperty]` partial property
   (PascalCase). Give it a sensible default. It auto-serializes to `settings.json`.
2. If it needs a side-effect when changed, add a partial `On<Name>Changed(value)` method and
   guard it with `if (_initializing) return;` so deserialization doesn't trigger it.
3. If user-visible, bind a control on the relevant page to `SettingsManager.Current.<Name>`
   `Mode=TwoWay`, and call `SettingsManager.SaveSettings()` after meaningful edits.
4. If the setting must be excluded from JSON, mark it `[JsonIgnore]`.
5. Document it (one line) in `.claude/docs/user-settings.md` if it's notable.
