# Changelog — Volumetric Ground FX (VGFX)

## 1.1.0 — Volumetric Ground FX

Highlights since 1.0.1 (including the 1.0.2 development builds):

- New public name: **Volumetric Ground FX (VGFX)**, formerly GroundBlastFx. The installation folder is now `Volumetric Ground FX`; saved marks and settings remain compatible.
- Larger, detailed launch-pad steam: puffs move out of the trench, slow down, grow and form billowing clouds.
- The near-outlet jet blends locally into the cloud, without shrinking the outer steam cloud. Existing steam keeps moving and dissipates after cutoff instead of disappearing immediately at the outlet.
- Smaller, lower flame origin at the trench mouth, better aligned with the physical opening.
- A brief blast reacts to booster ignition and sharp increases in actual engine thrust. Slow throttle ramps do not trigger it.
- User controls for **effect brightness** (50–150%, default 85%) and **ignition blast strength** (0–200%, default 100%), with a reset for these two controls. Clouds keep their volume and opacity.
- Warm flame lighting on steam by day and night; slightly lower default brightness to reduce overexposure.
- Rendering fixes for Scatterer at low camera angles and for later-drawn smoke behind opaque pad clouds.
- Engine geometry detection excludes Waterfall flame meshes and inactive branches, and respects scaled part dimensions.
- Kerbal Konstructs outlet data refreshes when pad geometry changes, even if outlet count stays the same.
- Stock ground visuals are restored if the renderer becomes unavailable. Cloud detail follows camera zoom correctly.
- Added a short, capped log of steam-feed and ignition-blast changes to help diagnose bug reports.

## 1.0.2 — development builds

- New launch pad steam: the flame trench now throws out big puffs of steam that slow down, grow and pile up into a lumpy cauliflower cloud sitting on the ground. Big rockets make a bigger cloud with tall towers. The cloud still fades out about 30 s after the rocket leaves.
- The launch pad cloud now glows with the fire of the engines: warm cream and orange near the flames by day, the whole cloud glowing orange at night.
- Fix: with Scatterer, the launch clouds disappeared when the sky was behind them (camera low, horizon in view). Scatterer's sky was drawn over them.
- Fix: smoke from other mods drawn after ours (for example volumetric booster trails) no longer shows through the launch pad cloud when it is behind or inside it.

## 1.0.1

- Fix: dust and launch pad steam did not show up with Scatterer (with EVE integration on). Scatterer was drawing over them. Water spray and the Mun effects were fine.
- Fix: at liftoff, a second pair of pad jets could show up going the wrong way (along the crawlerway).
- Fix: the smoke could turn with the camera (camera locked on a rolling rocket, camera shake at launch). The clouds now use the camera position at the exact moment the frame is drawn.

## 1.0.0 — first public release

- Volumetric dust, launch pad steam, water spray and snow clouds simulated on the GPU and anchored to the ground; they
  drift with the wind, follow the terrain and dissipate over time.
- Launch pads: steam jets out of the pad's real flame-trench outlets (stock KSC pads, Kerbal Konstructs pads with
  declared smoke outlets), sized and sped up by thrust, then billows into large clouds.
- Dust color read from the ground on screen (with or without Parallax) plus body/biome palettes for the stock system.
- Thin air (Duna): low cloud and fast grain sheet. Airless bodies: streaked ejecta sheet, GPU grains, blast mark.
- Scorch marks depending on ground and propellant, saved with the game.
- Cloud shadows, flame light by day and night, gravel and droplet particles.
- Stock ground dust hidden while the mod runs.
- Simple window: effect density and visible range. Interface in 9 languages, following the game language.
- GroundBlastFx tab in KSP's game settings (Difficulty options): density, range, each effect on/off, keep marks in the
  save, hide stock ground dust.

(Development history before the public release: the mod was developed as "GroundEffects", versions 1.0 to 1.9.2.)

- Ground mark controls in the flight window: enable/disable, keep in save, and clear all marks. Marks remain enabled by default.
