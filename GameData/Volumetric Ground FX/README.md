# Volumetric Ground FX (VGFX) 1.1.0

VGFX adds volumetric dust, launch-pad steam, water spray and surface marks to rocket engines in KSP. The effects respond to engine thrust, distance from the ground, surface type and atmosphere. Launch-pad steam travels out of the flame trench and forms clouds that keep moving after the engines stop.

Formerly **GroundBlastFx**. Version 1.1.0 adds more detailed pad steam, a smoother transition from the outlet jet to the cloud, and a brief blast when boosters ignite or thrust rises sharply.

## Requirements

- KSP 1.12.x on Windows, using Direct3D 11.
- A GPU with compute shader support.
- No other mod is required. OpenGL, Metal, macOS and Linux are not supported.

## Installation and updates

Copy `GameData/Volumetric Ground FX` from `VGFX-1.1.0.zip` into your KSP folder. For an update from GroundBlastFx, close KSP and rename the existing `GameData/GroundBlastFx` folder to `GameData/Volumetric Ground FX` before copying the new files. Keep `PluginData/Settings.cfg` and `PluginData/LaunchSites_user.cfg` if they exist. Saved surface marks remain in your game save.

Install one copy in `GameData/Volumetric Ground FX`. Do not leave another copy in `GameData/GroundBlastFx`.

## Settings

Open the VGFX window from the flight toolbar, or the **Volumetric Ground FX (VGFX)** section in KSP's Difficulty options.

- **Effect density** adjusts the amount of dust and steam.
- **Visible range** sets how far away clouds are drawn.
- **Effect brightness** ranges from 50% to 150%, with a default of 85%. It changes the light, keeping the same cloud shape and opacity.
- **Ignition blast** ranges from 0% to 200%, with a default of 100%. It changes the short burst at ignition or a sudden increase in thrust, without changing vessel thrust.
- **Reset brightness and blast** restores those two defaults.

The game settings also include individual effects, saved surface marks and stock ground-dust replacement. The interface follows the game language: English, French, German, Spanish, Italian, Portuguese, Russian, Simplified Chinese or Japanese.

## Effects

- Launch-pad steam flows through the declared trench outlets, then expands into large billowing clouds.
- Dust follows the terrain and takes its colour from the ground, with body and biome palettes as a fallback. Snow and ice produce pale clouds; thin atmospheres produce lower clouds and fast ground-hugging grains.
- Water produces spray and droplets. Airless bodies produce a low ejecta sheet and a surface mark instead of floating smoke.
- Scorch marks depend on the surface and propellant, and can be saved with the game.
- Cloud shadows and warm flame light help the effects sit in the scene by day and at night.

## Compatibility and performance

Development has used Waterfall, Scatterer, EVE, Parallax Continued, Kopernicus, TUFX, Deferred, SmokeScreen, RealPlume and Firefly. Kerbal Konstructs pads are supported when they declare smoke outlets. RSS/RP-1 has not been tested.

Another mod that adds its own engine dust or pad steam may draw overlapping effects. Avoid enabling both ground-effect systems at once. VGFX restores stock ground effects if its renderer is unavailable.

Large clouds can be demanding. Performance depends on your GPU, screen resolution and the scene. Density and visible range can reduce the amount of work.

## Bug reports

Include your KSP version, the other visual mods involved, a screenshot and `KSP.log` plus `GameData/Volumetric Ground FX/PluginData/GroundBlastFx.log`. Check logs for personal paths or other private information before sharing them.

## Licence

MIT, by Warnix. See `LICENSE` and `CREDITS.md`.

---

**Français :** effets au sol volumétriques pour KSP 1.12.x, Windows/DX11. Copier `GameData/Volumetric Ground FX` dans le dossier KSP. Pour une mise à jour, conserver les réglages de `PluginData` et installer une seule copie. Le bouton VGFX ouvre les réglages de densité, portée, luminosité et souffle ; la langue suit celle du jeu.

Ground marks are enabled by default. In the VGFX window, disable **Ground marks** to hide existing marks and stop creating new ones. **Keep marks in save** controls persistence between sessions. **Clear all ground marks** removes the current game's marks; the removal is recorded on the next normal save. Older quicksaves and backups can restore their own marks. Running engines can create fresh marks after clearing.
