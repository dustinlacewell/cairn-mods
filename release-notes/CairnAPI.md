## CairnAPI v0.2.0

- Settings & pause-menu integration: inject custom nav-rail entries with CairnMenu.Settings/PauseMenu.AddEntry.
- Settings.AddPage + the new SettingsPage primitive: host a native-looking settings page backed by the game's own fieldsUI, with live-swappable content (stub page-button, dropdown stay-open, scroll-gap, and hover quirks handled for you).
- New Fields factory: build native field rows (toggle, slider, text, list, dropdown, double-button, action, label) from plain values and callbacks.
- Mod-settings registry moved out into the new CairnModOptions mod; consumers now reference CairnModOptions for ModOptions/ModOption (was CairnAPI).
- Nav-rail visual and FieldUI hover fixes so custom entries slide, highlight, and clear focus like the game's own buttons.
