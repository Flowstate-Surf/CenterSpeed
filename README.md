# CenterSpeed

**CenterSpeed** is a SwiftlyS2 plugin for Counter-Strike 2 that displays the player's current horizontal speed in the centre of their screen using a particle-based digit HUD.

---

## Features

- Real-time 2D speed display (XY velocity, no Z)
- Particle-based 4-digit HUD � only visible to the owning player
- Per-player position, scale, and enable/disable settings
- Interactive `!cs` menu for live adjustments
- Persistent settings via the **Cookies** plugin (optional) with local JSON fallback
- Config file with server-wide defaults

---

## Requirements

- [SwiftlyS2](https://github.com/swiftlys2/swiftlys2) v1.2.2+
- [AddonsManager](https://github.com/SwiftlyS2-Plugins/AddonsManager/releases/tag/v2.0.1) v2.0.1+ � required for workshop particle asset delivery
- *(Optional)* [Cookies](https://github.com/swiftlys2/cookies) plugin for database-backed settings

---

## Installation

1. Build the project (`dotnet build`) targeting `net10.0`.
2. Copy the output to:
   ```
   game/csgo/addons/swiftlys2/plugins/CenterSpeed/
   ```
3. Upload the `workshop/` folder from this repo to Steam Workshop as your addon. The compiled particle (`.vpcf_c`) and texture (`.vtex_c`) are included.
4. Mount the addon in your server so it is available as the `flowassets` addon name.
5. Restart the server

A config file is auto-generated on first load at:
```
game/csgo/addons/swiftlys2/configs/plugins/CenterSpeed/config.jsonc
```

---

## Config (`config.jsonc`)

| Key | Default | Description |
|-----|---------|-------------|
| `ConfigVersion` | `"1.0.1"` | Schema version � do not modify |
| `DefaultScale` | `0.04` | HUD scale for new players |
| `DefaultYOffset` | `-1.0` | Vertical offset for new players |
| `DefaultDigitOffsets` | `[-1.5, -0.5, 0.5, 1.5]` | Horizontal digit positions for new players |
| `EnableDatabase` | `true` | `true` = Cookies plugin, `false` = local JSON |
| `Debug` | `false` | Verbose console logging |

---

## ConVars

| Name | Default | Description |
|------|---------|-------------|
| `cs_speed_particle` | `flowassets/particles/digits_x.vpcf` | Particle file used for HUD digits |

---

## Commands

| Command | Description |
|---------|-------------|
| `!cs` | Open the interactive HUD settings menu |
| `!hudsettings toggle` | Enable / disable the HUD |
| `!hudsettings info` | Print current settings to chat |
| `!hudsettings scale <value>` | Set HUD scale (e.g. `0.001` � `0.500`) |
| `!hudsettings yoffset <value>` | Set vertical offset (`-10` to `10`) |
| `!hudsettings offset <1-4> <value>` | Set individual digit horizontal offset |

---

## TODO

```

├── Colors
│   └── Flash Green/Red with loss/gain velocity
│   └── Select base color
│   └── Add to hud menu
├── Font Change
│   └── Add to hud menu
│   └── Transparency
├── VIP
│   └── VIP Permission set
```

---

## Authors

- Lethal
- Retro
- Ported to swiftlyS2 by Low
