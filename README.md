<div align="center">

# Real Glass & Water

**Realistic glass and water for Cities: Skylines.**

Adds a fully customizable glass shader to every building (Procedural Objects included) and
vehicle, mirroring the city around them and a live sky. It also adds reflections and foam to the
water, and rooms behind the windows at night.

<!-- Hero image: a skyline of glass towers over the water at sunset, e.g.
<img src="Workshop/Screenshots/hero.jpg" width="720" alt="Glass towers reflected in the water"> -->

[![Cities: Skylines](https://img.shields.io/badge/Cities%3A%20Skylines-mod-01ABF8)](https://store.steampowered.com/app/255710/Cities_Skylines/)
[![Steam Workshop](https://img.shields.io/badge/Steam%20Workshop-subscribe-1b2838?logo=steam)](https://steamcommunity.com/sharedfiles/filedetails/?id=3823419476)
[![Requires Harmony](https://img.shields.io/badge/requires-Harmony-blue)](https://steamcommunity.com/sharedfiles/filedetails/?id=2040656402)
![Platforms](https://img.shields.io/badge/platforms-Windows%20%C2%B7%20Mac%20%C2%B7%20Linux-lightgrey)
![Languages](https://img.shields.io/badge/languages-EN%20%C2%B7%20ES%20%C2%B7%20FR%20%C2%B7%20ZH-informational)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

[Features](#features) · [Getting started](#getting-started) · [Controls](#controls) · [Guide](#guide) · [For asset creators](#for-asset-creators) · [Compatibility](#compatibility) · [How it works](#how-it-works) · [Building](#building)

</div>

## Features

### Glass

- Glass on every building, at every distance, wherever the asset's own textures put it
- 10 kinds of real architectural glass (reflective blue by default, silver, bronze, low-E...) or a
  color of your own
- Reflections of the city, the buildings around it, and a live sky with
  [Volumetric Weather](https://steamcommunity.com/sharedfiles/filedetails/?id=3805863507)'s clouds
- Panes that bend the reflections like on a real facade
- Your own glass for any building asset (Alt+G)

### Rooms

- Photographed rooms behind the windows, picked by the building's kind
- Lights that turn on and off with the time of day
- At night, or by day too if you like

### Water

- The sky and the city reflected in the water
- Ocean waves that follow the weather
- Sun and moon glitter
- Foam along the shores, on fast water and on wave fronts
- An underwater view, with a camera mod that goes below the surface

### Vehicles

- Real glass on every vehicle's windows
- A clear coat on the paint

### Mod options

- One panel (F8) for everything, in the game and in the Asset Editor
- 4 panel looks
- English, Spanish, French and Simplified Chinese

## Getting started

1. **Subscribe** to [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2040656402)
   and [Real Glass & Water](https://steamcommunity.com/sharedfiles/filedetails/?id=3823419476),
   and enable both (in Skyve too, if you use it).
2. **Load a city** (new or saved).
3. **Press F8** (or click the mod's button) to change the settings, in the game and in the Asset
   Editor.
4. **Press Alt+G** and click a building to give that building its own glass.

> [!NOTE]
> The default settings were tested on a vanilla game with no LUT, so they might look too strong if
> you use one. Open the panel with F8 and adjust them to your liking.

Not on Steam? Unzip a [release](../../releases) into
`%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\`.

## Controls

| Key | What it does |
| --- | --- |
| F8 | Opens the panel, in the game and in the Asset Editor |
| Alt+G, then click a building | Opens that building's own glass settings |
| Right click, while picking | Stops picking |

Both keys can be changed in the mod's options. On a Mac, F8 is Fn+F8 on most keyboards.

## Guide

### The panel

The Glass, Interiors, Water and Vehicles tabs each have a switch at the top to turn that part of
the mod off, and the small icon at the end of each row puts it back to its default.

| Tab | What's on it |
| --- | --- |
| **Glass** | The **Kind of glass** (10 presets or Custom), its color, how reflective it is head-on and at an angle, how clear it is, and its overall **Strength**. Whether the building keeps its own color and painted detail, and **What counts as glass** (see [For asset creators](#for-asset-creators)). **Show what counts as glass** highlights it. |
| **Reflections** | How bright and smooth the reflections are, the sun's glint, and what shows in the glass: what's on screen, the buildings around, and the sky (the live sky with its clouds, or the game's own). **Reflection quality** goes from Low to Ultra (the default). |
| **Panes** | The pane width and story height, and how the panes bend the reflections: cushioned, tilted, rippled, and moving with the weather. |
| **Interiors** | The rooms behind the windows: by day or only at night, how deep they look, curtains and blinds, and the lights at night (how many are lit, fewer late at night, switching on and off, brightness, colored LED lights). |
| **Water** | The water's color (the map theme's or your own), ripples and ocean waves (wind speed, choppiness, sizes), foam, the sky and the city in the water, sun and moon glitter, and the underwater view (visibility, light shafts, sunlight on the seabed). |
| **Vehicles** | Glass on vehicles, how many have tinted windows, their reflections, and the clear coat on the paint. |

> [!TIP]
> To turn off the water: F8 → Water → **Realistic water**. To turn off the rooms: F8 → Interiors →
> **Parallax interiors**.

### The mod's options

In Pause menu → Options → Real Glass & Water: the **Language**, both keys, the **Panel look**
(Frosted glass, See-through, Blue or Vanilla), **Where to show mod icon** (Unified UI, Toolbar or
Never), **Glass on** (near buildings, near and middle, or the whole city: fewer buildings cost
less), the live sky's detail and how often it updates, **Detailed logging**, and a reset.

### Each building's own glass

- Press **Alt+G** and click a building. Its window shows that asset's glass, and what you change
  applies to every copy of it, in every city.
- You can set its strength, kind of glass, color, reflectivity, clarity, what counts as glass, its
  LOD's glass, and its rooms.
- **Restore settings** puts it back to the Glass tab's settings (or to the creator's, if the asset
  comes with its own). **Pick another building** to go on.
- It's all kept in `RealGlassAssets.xml`, never in your save.

## For asset creators

- **In the Asset Editor, press F8 for the glass panel, and ship your own glass with your asset.**
  It creates a `RealGlassDefinition.xml` file that ships with the asset (players can still change
  it with Alt+G). The panel can also show the asset with and without the glass, what counts as
  glass, and the rooms by day.
- By default the mod counts 70% specular or brighter in your specular map (`_s`) as glass (players
  can change that), so keep metal and other shiny parts below that. A building without a specular
  map doesn't show glass.
- Windows that light up at night in your illumination map (`_i`) also count as glass, even with a
  slightly lower specular, so painting them properly helps.
- For LOD textures, include a specular map for them too (`_lod_s`), otherwise the mod shows the
  glass according to the full-detail model.

## Compatibility

Works on Windows (Direct3D 11 or OpenGL), Mac (Metal) and native Linux (OpenGL 3.3). Proton and
the Steam Deck run the Windows version.

Harmony is required. [Unified UI](https://steamcommunity.com/sharedfiles/filedetails/?id=2966990700)
is optional: the mod's button goes there. With Volumetric Weather on, its clouds show in the
reflected sky.

**You can safely unsubscribe from this mod.** The only things it keeps in your save are the foam
you painted and Dynamic dirt's level, a few KB at most. Without the mod the game just carries
them along, so your city loads fine, and they're back if you subscribe again.

### Performance

Most of the cost is on your graphics card, and it grows with your resolution: the reflections are
traced for every pixel of glass and water on screen.

- **GPU**: the reflections in the glass and in the water cost the most, then the rooms and the foam.
- **Video memory**: the mod keeps several screen-sized copies of the picture for its reflections.
  By my count that's roughly 300 MB at 1080p and close to 1 GB at 4K with everything on.
- **CPU**: one more draw for each building with glass in view, and nothing in the simulation.
- **RAM**: little next to the game's own.
- **Loading**: the first time a city loads, the mod measures each building asset's windows in the
  background while you play (about half a minute for 900 assets on my PC), and remembers them.

I haven't measured it across graphics cards yet, so if it's heavy on yours, let me know. To make it
lighter, in this order:

1. Options → Real Glass & Water → **Glass on** → near buildings only.
2. F8 → Reflections → **Reflection quality** → Low, and turn off **Neighbors' facades** (the tab
   shows with **Show advanced options in the in-game panel** on).
3. F8 → Water → turn off **Water reflections**, or the whole **Realistic water**.
4. F8 → Interiors → turn off **Parallax interiors**.
5. Options → Real Glass & Water → lower **Sky detail** and **Sky updates**.

### Known issues / limitations

- **At night, the rooms don't line up with the windows on some buildings**: not all buildings have
  textures that let the mod add the glass and rooms properly. Press Alt+G, click the building, then
  turn off **Parallax interiors** for it.
- **Glass and water flickering**: this happens with TAA (as in Render It!). If it's too
  distracting, try another anti-aliasing method.
- **Reflections aren't accurate**: they're traced through what's on screen, which is approximate.
  The game doesn't support ray tracing for accurate reflections.
- **Some buildings show glass in the wrong places**: unfortunately this isn't within my control,
  some buildings don't have proper specular textures. Press Alt+G, click the building, and turn
  down **Strength** for it, or change **Glass on LOD**.
- **Glass briefly disappearing on a Mac** (and maybe Linux): I'm not sure if it's my slow Mac
  causing this, but if you run into it, let me know.
- **The glass pauses** while an info view is open, and while the Snapshot tool is in use, so your
  pictures show the asset as the game draws it.
- **Only one copy runs.** If the mod is installed twice (a local copy and the Workshop one), the
  second copy turns itself off and tells you.

### Files and logs

Everything the mod saves is in the game's data folder:

| Windows | Mac | Linux |
| --- | --- | --- |
| `%LOCALAPPDATA%\Colossal Order\Cities_Skylines` | `~/Library/Application Support/Colossal Order/Cities_Skylines` | `~/.local/share/Colossal Order/Cities_Skylines` |

| File | |
| --- | --- |
| `RealGlass.log` | The mod's log, started fresh at every launch. |
| `RealGlass.xml` | Your settings. |
| `RealGlassAssets.xml` | Each building asset's own glass (made once one has some). |

### Reporting a bug

Please [open an issue](../../issues) and include the following:

1. **The game's log**, `output_log.txt`. In Steam, right-click Cities: Skylines → Manage → Browse
   local files, then open the `Cities_Data` folder. On a Mac it's `~/Library/Logs/Unity/Player.log`,
   on Linux `~/.config/unity3d/Colossal Order/Cities_Skylines/Player.log`.
2. **The mod's own log**, `RealGlass.log`, and your `RealGlass.xml`. Tick **Detailed logging**
   (Options → Real Glass & Water) first, then make the problem happen again.
3. **What happened**: what you did and expected, screenshots, your graphics card and your other
   mods.

Both logs start fresh at every launch, so copy them before restarting the game.

### Translations

The texts are in [`RealGlass/Localization`](RealGlass/Localization). The translations were made
with AI, so corrections from native speakers are welcome. To add a language, copy `en.xml` under
the game's code for it (`de`, `ko`, `pl`, `pt` or `ru`) and translate it.

## How it works

The mod follows published work, credited in the notes at the bottom of the page (the full list,
with links, is in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)). Click a topic to read more.

<details>
<summary><b>What counts as glass</b>: the asset's own textures</summary>

<br>

- A pixel is glass where the asset's specular map is at least as shiny as **Shininess needed**
  (70% by default), or where its illumination map lights a window at night.
- A building's LOD (the simpler model the game draws from afar) sometimes has its glass somewhere
  else. When it does, the mod carries the model's glass onto the LOD, after comparing the two.[^lod]
  **Glass on LOD** in a building's window chooses between them.

</details>

<details>
<summary><b>The glass</b>: tints and reflectance of real glass</summary>

<br>

- Each kind of glass uses glass makers' published tints and reflectance, more reflective at a
  grazing angle, the way real glass is.[^glass]
- By day you see a dark, tinted inside behind it. At night the game's own lit windows stay as they
  are, so every asset keeps its own night look, and the reflections go on top.
- The painted detail can be kept through the glass, and its edges are smoothed so they don't
  shimmer.

</details>

<details>
<summary><b>Reflections</b>: the screen, the buildings around, and the sky</summary>

<br>

- Reflections come in layers. First, what's on screen: each reflected ray is traced through the
  picture the game just drew (screen-space reflections), using last frame's finished picture so fog,
  clouds and lights show up too.[^reflections]
- Off screen, the buildings around are found in a height map of the city around the view.
- Behind it all, a live sky captured around the camera, with Volumetric Weather's clouds when it's
  on, and dimming at dusk the way the real sky's light does.

</details>

<details>
<summary><b>Panes</b>: why a real facade isn't a perfect mirror</summary>

<br>

Real double glazing bulges a little between its frames, and tempered glass has a slight ripple from
the rollers it was made on. So each pane bends the reflection a bit differently, and the panes can
follow the windows of the asset.[^panes]

</details>

<details>
<summary><b>Rooms</b>: a photographed room behind every window</summary>

<br>

- Each window shows a room with depth (interior mapping), from photographed rooms picked by the
  building's kind.[^rooms]
- The rooms are fitted to each asset's own rows and columns of windows, found by measuring how
  often they repeat.
- At night, rooms light up by the hour like a real city's windows (fewer late at night), switching
  on and off now and then, with real lamp colors.

</details>

<details>
<summary><b>Water</b>: ripples, ocean waves and glitter</summary>

<br>

- The water reflects the sky like a rough sea does, per ripple up close and averaged further out.
- The ripples are small waves running in a few directions, or with **Ocean waves**, a real ocean
  wave spectrum computed on the GPU. With **Dynamic waves** they grow with the game's weather.[^waves]
- The sun's and the moon's glitter come from the slopes of those waves.

</details>

<details>
<summary><b>The city in the water</b>: traced off the ripples</summary>

<br>

The city is traced through what's on screen off the moving ripples, stretched into streaks like
real reflections on water, and kept steady from one frame to the next.[^citywater]

</details>

<details>
<summary><b>Foam</b>: from the game's own water simulation</summary>

<br>

Foam comes from the game's own water: a band along the shores with waves breaking in groups, foam
on fast water, and on rising, steep wave fronts. Each patch goes through a whitecap's life, with
pale bubbles under it, and drifts along with the water.[^foam]

</details>

<details>
<summary><b>Under the water</b>: haze, light shafts and Snell's window</summary>

<br>

With a camera mod that goes below the surface (ACME, First Person Camera), the water gets a teal
haze that thickens with distance and takes red first, darker the deeper you go and glowing toward
the sun. The surface seen from below shows the sky through Snell's window, and light shafts come in
between the shadows of bridges and buildings, from the game's own shadows (not on a Mac yet).[^under]
Nothing is drawn while the camera is above the water.

</details>

<details>
<summary><b>Vehicles</b>: car glass and clear coat</summary>

<br>

Vehicle windows are real car glass, letting through the light a windscreen must, and the paint
gets a clear coat on top. Each vehicle gets its own random number, so only some of them have tinted
windows.[^vehicles]

</details>

<details>
<summary><b>Picking a building</b>: Alt+G, Procedural Objects too</summary>

<br>

Alt+G works as the game's own tools do, and finds Procedural Objects' buildings too. Each asset's
own glass is kept in a file of its own, never in the save.[^picker]

</details>

## Building

See [BUILDING.md](BUILDING.md) for the details. In short, clone with `--recurse-submodules`, then:

```powershell
.\build-bundle.ps1 -Only win              # only after changing a shader: compiles it with Unity 5.6.6
dotnet build RealGlass/RealGlass.csproj   # builds the mod and deploys it to the game's Mods folder
```

The compiled shaders are in the repo, so a normal build only needs the second line.

## Credits

<sub>The screen-space ray tracer is adapted from Morgan McGuire and Michael Mara's code (BSD 2-Clause);
everything else was written for this mod, after the published work credited in the notes below.
The rooms are cut from [Poly Haven](https://polyhaven.com)'s CC0 panoramas. The icons are based on
[Lucide](https://lucide.dev)'s (ISC). Includes UnifiedUILib by kianzarrin and CitiesHarmony.API by
boformer, thanks to both. Full license info is in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).</sub>

<sub>This mod was made with substantial help from AI. I set the goals, made the design decisions,
and did all of the in-game testing and tuning.</sub>

<sub>Cities: Skylines is © Colossal Order Ltd., published by Paradox Interactive AB. This is an
unofficial fan-made mod; the repository holds no game code or assets.</sub>

## License

[MIT](LICENSE), copyright © 2026 Anthony Ibrahim, except the screen-space ray tracer (BSD 2-Clause,
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

[^lod]: **LOD glass.** Cignoni, Montani, Rocchini and Scopigno's attribute-preserving resampling of simplified meshes (1998); Fujimoto, Tanaka and Iwata's uniform grid (1986); Möller and Trumbore's ray/triangle test (1997); Williams's mip-maps (1983); the two glasses compared by Jaccard's index (1912).
[^glass]: **The glass.** Glass makers' published transmittance and reflectance; a coated tinted lite's reflectance as EN 410 / ISO 9050 work it out; Schlick's Fresnel approximation; specular anti-aliasing (Kaplanyan et al. 2016; Tokuyoshi and Kaplanyan 2019; Filament's documented constants); unsharp masking (Schreiber 1970) over a mipmap level (Williams 1983) for the painted detail; an anti-aliased glass edge (RenderMan's filterstep, Apodaca and Gritz 1999; Golus 2017; Direct3D 11's standard 4x sample pattern).
[^reflections]: **Reflections.** The screen-space ray tracer adapted from M. McGuire and M. Mara, *Efficient GPU Screen-Space Ray Tracing* (JCGT 3(4), 2014), BSD 2-Clause; layered reflections as in Mirror's Edge Catalyst (DICE, GDC 2016); stochastic and hierarchical screen-space reflections (Frostbite, SIGGRAPH 2015); the screen traced over a pyramid of its nearest depths (Uludag, GPU Pro 5, 2014) and farthest (Tevs, Ihrke and Seidel's maximum mipmaps, I3D 2008); the previous frame's finished picture reprojected (Killzone Shadow Fall, GDC 2014); a live environment cubemap as in GTA V, the sky's volumetric clouds captured into it as Unreal's Sky Light does; a top-down height map of the city walked by reflected rays (as CRYENGINE's height-map ambient occlusion and Unreal's heightfield sky occlusion read such maps; Amanatides and Woo's grid traversal, 1987); the sky's light at dusk after Bird and Riordan's clear-sky spectral model (SPCTRAL2, 1986) with Kasten and Young's air mass (1989); xdedss's CS_SSR, read for what the game's camera keeps on screen.
[^panes]: **Panes.** The physics of insulating-glass pillowing and roller-wave distortion.
[^rooms]: **Rooms.** Layered as Forza Horizon 4's windows (Harwood, Playground Games, 2019); van Dongen's interior mapping (2008), each room kept as one picture as SimCity did (Willmott, Maxis, 2013; Golus's write-up); 64 views from 37 of [Poly Haven](https://polyhaven.com)'s CC0 indoor panoramas; fitted to the windows by autocorrelation as Rabiner found the pitch of speech (1977), refined by epoch folding (Leahy et al. 1983); lit windows scattered and offices lit a floor at a time as in Young's procedural city (2009); the share lit by the hour after Richardson et al.'s domestic lighting model (2009), EnergyPlus's office lighting schedule and Dobler et al.'s observations of a city's windows (2015); the lamps' colors from Charity's blackbody table; the pictures' own windows filled by normalized convolution (Knutsson and Westin, 1993).
[^waves]: **Ripples, waves and glitter.** A rough sea's reflectance (Cox and Munk's wave slopes, 1954; Mobley's sea-surface reflectance factor, 1999), per ripple and averaged where filtering takes them (Bruneton, Neyret and Holzschuch 2010); directional sine waves (Finch 2004; tiling and deep water's speeds, Tessendorf 2001; Phillips's equilibrium range, 1958); ocean waves as an inverse FFT in Stockham's self-sorting steps (1966) of a fetch-limited JONSWAP spectrum (Hasselmann et al. 1973) spread by Donelan, Hamilton and Hui (1985), as Horvath gathers them (2015), choppy as Tessendorf's, carried to Elfouhaily et al.'s capillary limit (1997), amplitudes from SplitMix64 (Steele, Lea and Flood 2014); dynamic waves' roughness by Cox and Munk and pace by the Shore Protection Manual's duration-limited growth (U.S. Army Corps of Engineers, 1984); the glitter on LEAN-mapped slopes (Olano and Baker 2010) through Walter et al.'s microfacet model (2007) with Smith's masking (1967), erfc after Abramowitz and Stegun (1964); the idea from web-ocean (Noniv, MIT; only its README was read, none of its code is used).
[^citywater]: **The city in the water.** Traced through the depth buffer and streaked by normalized convolution (Knutsson and Westin, 1993); steadied by reprojected, neighborhood-clamped history (Karis 2014; Pedersen 2016); Sousa's distorted mirror (GPU Gems 2, 2005) and a ray-cone read (Akenine-Möller et al., Ray Tracing Gems, 2019), compiled in but switched off.
[^foam]: **Foam.** Shore foam as Crest's (Bowles et al. 2017) and War Thunder's (Tcheblokov 2015), the distance from the shore by Felzenszwalb and Huttenlocher's distance transform (2012); broken waves at the shallow-water speed in wave groups (McCowan 1894; Longuet-Higgins 1984), the swell spilling here and there (Galvin 1968), steep fronts by Chentanez and Müller's breaking test (2010); a whitecap's life (Monahan and Lu 1990; Potter et al. 2015; Callaghan et al. 2012); the bubbles under it (Gordon et al. 1975; Morel and Prieur 1977; Terrill, Melville and Stramski 2001; Pope and Fry 1997); carried along the flow in two phases (Vlachos 2010); its pattern from Worley's cellular texture (1996), a fractal sum of noise (Perlin 1985) and domain warping (Quilez); as bright as dense sea foam (Koepke 1984).
[^under]: **Under the water.** The underwater image formation of McGlamery (1975) and Jaffe (1990) as Akkaynak and Treibitz revised it (2018); the water's clearness from its visibility (Zaneveld and Pegau 2003); pure water's absorption (Pope and Fry 1997) and scattering (Morel 1974), dissolved organic matter (Bricaud, Morel and Prieur 1981), particles backscattering as Petzold measured (1972); its color (Morel and Prieur 1977; Gordon et al. 1975); the daylight fading with depth (Kirk 1984); the light under the surface (Tyler's measured light fields, 1960; Mobley 1994); its glow toward the sun (Henyey and Greenstein's phase function, 1941); Snell's window with Fresnel's equations and the n-squared law; the surface from below as rough as the glitter sees it from above (LEAN mapping), the sun through its waves by Walter et al.'s microfacet transmission (2007); caustics as the area a beam of sunlight lands on (Watt 1990); light shafts as single scattering with volumetric shadows (Tóth and Umenhoffer 2009) from the game's own shadow map, grabbed as SlightlyMad's Volumetric Lights for Unity does, jittered by Jimenez's interleaved gradient noise (2014).
[^vehicles]: **Vehicles.** Automotive glass as a slab of soda-lime with the light transmittance UN Regulation No. 43 asks of a windscreen; the paint's clear coat as Filament's clear-coat layer (Guy and Agopian) with Autodesk Standard Surface's coat roughness; each vehicle's own random number by Jarzynski and Olano's PCG hash.
[^picker]: **Picking a building.** Kay and Kajiya's slab test (1986) and Möller and Trumbore's ray/triangle test (1997), for Procedural Objects' buildings; algernon's BOB, read for how its building picker works and for keeping per-building data in a file of its own; Network Anarchy's keys, so the picker's key doesn't clash; Procedural Objects, read for how it keeps and draws a converted building. No code from any other mod is included.
