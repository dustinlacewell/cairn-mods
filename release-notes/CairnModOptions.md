## CairnModOptions v0.1.0

- New mod: surfaces every mod's settings as a native Mods page in the game's Settings menu.
- One mod shown at a time via a selector dropdown; changing it swaps the page to that mod's options.
- Typed options: toggle, slider, text field, list (arrows), dropdown, double-button, action button, and info label.
- Back each option with a MelonPreferences entry or your own get/set; changes apply and persist immediately.
- Built on CairnAPI's SettingsPage + Fields primitives; consumer mods just call ModOptions.Register(name, options).
