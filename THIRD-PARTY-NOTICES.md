# Third-Party Notices

Real Glass & Water is an unofficial mod for Cities: Skylines 1. This file records what in the repository
is not original work, and what is credited rather than incorporated.

The repository contains no game code and no game assets. The game's DLLs are referenced from
your own installation at build time and are never copied into the repository or the
distribution.

## Shipped with the mod

These are redistributed alongside `RealGlass.dll`, which is the standard pattern in this
ecosystem. This file ships in the mod's folder with them, so the notices below travel with every
copy.

| Component | Author | Licence | Use |
| --- | --- | --- | --- |
| [`UnifiedUILib.dll`](https://github.com/kianzarrin/UnifiedUI) | kianzarrin | MIT | Registers the mod's button with Unified UI when the Unified UI mod (or another mod's Unified UI panel) is there; otherwise the mod shows a button of its own |
| [`CitiesHarmony.API.dll`](https://github.com/boformer/CitiesHarmony) | boformer | MIT | Bootstrap shim: waits for the Harmony mod and prompts the player to install it if it is missing |

Their licences, as published in their repositories:

```
UnifiedUILib -- MIT License

Copyright (c) 2022 UnifiedUI

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

```
CitiesHarmony.API -- MIT License

Copyright (c) 2017 Felix Schmidt

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Code adapted into the shaders

**McGuire & Mara screen-space ray tracer, BSD 2-Clause.** The glass shaders' screen-space ray
tracer (in `UnityProject/Assets/RealGlass/`, compiled into the shipped shader bundles) is adapted
from the code published with Morgan McGuire and Michael Mara, *Efficient GPU Screen-Space Ray
Tracing*, Journal of Computer Graphics Techniques 3(4), 2014
([JCGT](https://jcgt.org/published/0003/04/04/),
[code](http://casual-effects.blogspot.com/2014/08/screen-space-ray-tracing.html)), "released as
open source under the BSD 2-Clause License":

```
Copyright (c) 2014, Morgan McGuire and Michael Mara
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of
   conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials provided
   with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Artwork

| Asset | Source | Licence | Use |
| --- | --- | --- | --- |
| `RealGlass/Resources/GlassIcon.png` (listed in `GlassIcon.lucide.txt`) | Drawn by ModsLibCS1's `tools/make-button-icon.ps1` from the path data of the [Lucide](https://lucide.dev) icon mirror-rectangular (lucide-static 1.66.0) | ISC, as the panels' icons below; its notice follows | The mod's button icon (its own button and its Unified UI button), embedded in the DLL |
| `RealGlass/Resources/rooms-night.bytes`, `rooms-day.bytes` | 62 views cut from 36 of [Poly Haven](https://polyhaven.com/hdris/indoor)'s indoor HDRI panoramas (2k), each listed with its direction in `tools/interiors/rooms.txt`; reprojected onto box rooms, graded for day and night and compressed by `tools/interiors/build-rooms.ps1`. And 50 rooms of the mod's own (offices, shops, homes; `tools/interiors/render-offices.py`), rendered in Blender and furnished in part with 35 of [Poly Haven](https://polyhaven.com/models)'s 3D models (1k glTF: plants, sofas, armchairs, beds, tables, cabinets, a ceiling lamp, vases; listed in `tools/interiors/models.txt`) | CC0 1.0 ([Poly Haven's licence](https://polyhaven.com/license): "You can redistribute them ... even in a product you sell"; no attribution required) | The pictures of the rooms behind the windows (the Interiors tab), embedded in the DLL; the models themselves are not shipped, only the rendered pictures. Views with people in them, walls of paintings and readable brand signs were left out |
| The panels' icons: the tab icons (`RealGlass/Resources/PanelIcons.png`, listed in `PanelIcons.lucide.txt`) and the library's glyphs (`ModsLibCS1/sprites/sheet.png`) | Drawn by the ModsLibCS1 UI library's tools (the library is compiled into the DLL) from the path data of [Lucide](https://lucide.dev) icons (lucide-static 0.460.0): building-2, sparkles, grid-2x2, sofa, waves, car (the tabs); rotate-ccw, chevron-down, chevron-right, x (reset, fold, dropdown, close) | ISC (copyright Lucide Contributors; portions Cole Bemis, as part of Feather, MIT); its notice follows | The tab icons, the reset icons, the close cross and the dropdown arrow of the F8 panel and the building window, embedded in the DLL |

Lucide's licence, as published in its repository:

```
ISC License

Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of Feather (MIT). All other copyright (c) for Lucide are held by Lucide Contributors 2022.

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

## Build-time only

| Component | Author | Licence | Use |
| --- | --- | --- | --- |
| [`CitiesHarmony.Harmony.dll`](https://github.com/boformer/CitiesHarmony) | boformer; a repackaging of [Harmony](https://github.com/pardeike/Harmony) by Andreas Pardeike | MIT | Compile-time reference only. The real assembly is provided at runtime by the Harmony mod (Workshop 2040656402) and is never shipped by us. |
| [`Microsoft.NETFramework.ReferenceAssemblies.net35`](https://www.nuget.org/packages/Microsoft.NETFramework.ReferenceAssemblies.net35) | Microsoft | MIT | net35 reference assemblies, so the project builds without an old framework installed |
| [Blender](https://www.blender.org) | Blender Foundation | GPL (the program); "What you create with Blender is your sole property" ([licence](https://www.blender.org/about/license/)) | Runs `tools/interiors/render-rooms.py` (its own Python, numpy, its HDR reader and PNG writer) to make the rooms' pictures. Nothing of Blender is shipped |

## Techniques credited, not copied

The glass follows published work -- layered reflections as in Mirror's Edge Catalyst (DICE, GDC
2016), stochastic and hierarchical screen-space reflections (Frostbite, SIGGRAPH 2015), the
screen traced over a pyramid of its nearest depths (Uludag, GPU Pro 5, 2014) and farthest
(Tevs, Ihrke and Seidel's maximum mipmaps, I3D 2008), the
previous frame's finished picture reprojected for the reflections (Killzone Shadow Fall, GDC
2014), the reflections worked out in a screen pass of their own and steadied by a reprojected,
neighbourhood-clamped history (Karis 2014), the pane normals kept octahedrally (Cigolle et al.,
JCGT 2014), each asset's glass threshold read from its own textures by Otsu's method (1979) and by
the windows the game lights at night, glass and water on decals projected onto the scene's depth as
screen-space decals are (Krassnigg 2010; Kim 2012), a live environment cubemap as in GTA V with the sky's volumetric clouds captured into it
as Unreal's Sky Light does, a top-down height map of the city round the view walked by reflected
rays (as CRYENGINE's height-map ambient occlusion and Unreal's heightfield sky occlusion read
such maps; Amanatides and Woo's grid traversal, 1987), specular anti-aliasing (Kaplanyan et al.
2016, Tokuyoshi and Kaplanyan 2019, with Filament's documented constants), Schlick's Fresnel
approximation, glass makers' published transmittance and reflectance, a coated tinted lite's
reflectance as EN 410 / ISO 9050 work it out (the tint crossed twice), the physics of
insulating-glass pillowing and roller-wave distortion, unsharp masking (Schreiber 1970) over
a mipmap level (Williams 1983) for the painted detail it keeps, and an anti-aliased glass edge
(RenderMan's filterstep, Apodaca and Gritz 1999; Golus 2017; Direct3D 11's standard 4x sample
pattern), and for the water tab a rough sea's reflectance (Cox and Munk's wave slopes, 1954;
Mobley's sea-surface reflectance factor, 1999), per ripple where the ripples are seen and averaged
where filtering takes them (Bruneton, Neyret and Holzschuch 2010), moving ripples made of directional sine waves
(Finch 2004; tiling and deep water's speeds, Tessendorf 2001; Phillips's equilibrium range, 1958),
or as ocean waves an inverse FFT in Stockham's self-sorting steps (1966) of a fetch-limited
JONSWAP spectrum (Hasselmann et al. 1973) spread by Donelan, Hamilton and Hui (1985), as Horvath
gathers them (2015), choppy as Tessendorf's, carried below the texture to Elfouhaily et al.'s
capillary limit (1997), its amplitudes from SplitMix64 (Steele, Lea and Flood 2014), with dynamic
waves its wind from the game's rain, its roughness by Cox and Munk and its pace by the Shore
Protection Manual's duration-limited growth (U.S. Army Corps of Engineers, 1984), and the sun's
glitter on LEAN-mapped slopes (Olano and Baker 2010) through Walter et al.'s microfacet model
(2007) with Smith's masking (1967), erfc after Abramowitz and Stegun (1964) -- the idea from
web-ocean (Noniv, MIT; only its README was read, none of its code is used),
and the city traced through the depth buffer off those ripples and streaked by normalized
convolution (Knutsson and Westin, 1993), steadied frame to frame by reprojected, neighbourhood-clamped
history (Karis 2014; Pedersen 2016; with Sousa's distorted mirror, GPU Gems 2, 2005, and a
ray-cone read, Akenine-Möller et al., Ray Tracing Gems, 2019, compiled in but switched off), and
foam from the game's own water simulation -- along
shores as Crest's (Bowles et al. 2017) and War Thunder's (Tcheblokov 2015) shore foam, a band
measured from the shore as War Thunder phases its shore waves (that distance held near the true one,
mapped by Felzenszwalb and Huttenlocher's distance transform, 2012), broken waves running in at the
shallow-water speed in wave groups (McCowan 1894; Longuet-Higgins 1984) over a seabed smoothed by a
cubic B-spline (Sigg and Hadwiger 2005) with softly rounded limits (Quilez's smooth minimum), the
swell beyond them spilling here and there (Galvin 1968), each streak's end set by its distance to
the noise's level set (Taubin 1994), on rising, steep fronts by Chentanez and Mueller's breaking
test (2010) -- in the layers of a whitecap's life (Monahan and Lu 1990; Potter et al. 2015; Callaghan et al. 2012), the bubbles under
it as Crest's and as the water's own colour made paler (Gordon et al. 1975; Morel and Prieur 1977;
Terrill, Melville and Stramski 2001; Pope and Fry 1997), carried along the flow in two phases
(Vlachos 2010), its pattern Worley's cellular texture (1996), a fractal sum of noise (Perlin 1985)
and domain warping (Quilez), as bright as dense sea foam (Koepke 1984) -- and the rooms behind the windows: layered as
Forza Horizon 4's windows (Harwood, Playground Games, 2019), box rooms by van Dongen's interior
mapping (2008) kept as one picture each as SimCity did (Willmott, Maxis, 2013; Golus's write-up),
lit windows scattered at random and offices a floor at a time as in
Young's procedural city (2009), the share lit by the hour after Richardson et al.'s domestic
lighting model (2009), EnergyPlus's office lighting schedule and Dobler et al.'s observations of
a city's windows (2015), the lamps' colours from Charity's blackbody table, the pictures' own
windows filled by normalized convolution (Knutsson and Westin, 1993) -- and the reflected sky's
light at dusk after Bird and Riordan's clear-sky spectral model (SPCTRAL2, 1986) with Kasten and
Young's air mass (1989) -- and for picking Procedural Objects' buildings, Kay and Kajiya's slab
test (1986) and Möller and Trumbore's ray/triangle test (1997) -- and, for LODs whose glass is not
their model's, the model's carried onto the LOD after Cignoni, Montani, Rocchini and Scopigno's
attribute-preserving resampling of simplified meshes (1998), through Fujimoto, Tanaka and Iwata's
uniform grid (1986), Möller and Trumbore's test and Williams's mip-maps (1983), the two glasses
compared by Jaccard's index (1912) -- and, to fit the rooms to each asset's own windows, the
period of its rows and columns of windows by autocorrelation as Rabiner found the pitch of speech
(1977), refined by epoch folding (Leahy et al. 1983) -- and for vehicles, automotive glass as a
slab of soda-lime with the light transmittance UN Regulation No. 43 asks of a windscreen, and the
paint's clear coat as Filament's clear-coat layer (Guy and Agopian) with Autodesk Standard
Surface's coat roughness, each vehicle's own random number by Jarzynski and Olano's PCG hash --
and, under the water, the underwater image formation of McGlamery (1975) and Jaffe (1990) as
Akkaynak and Treibitz revised it (2018), the water's clearness from its visibility (Zaneveld and
Pegau 2003), what coastal water holds -- pure water's absorption (Pope and Fry 1997) and
scattering (Morel 1974), dissolved organic matter (Bricaud, Morel and Prieur 1981), particles
backscattering as Petzold measured (1972) -- and its colour from those (Morel and Prieur 1977;
Gordon et al. 1975), the daylight fading with depth after Kirk (1984), the light under the surface
after Tyler's measured light fields (1960) and Mobley (1994), its glow toward the sun by Henyey and
Greenstein's phase function (1941), Snell's window with Fresnel's equations and the n-squared law,
the surface seen from below as rough as the sun's glitter sees it from above (LEAN mapping) and the
sun through its waves by Walter et al.'s microfacet transmission (2007), caustics as the area a
beam of sunlight lands on (Watt 1990), and light shafts as single scattering with volumetric
shadows (Tóth and Umenhoffer 2009) from the game's own shadow map of the sun, grabbed after the
light renders it as SlightlyMad's Volumetric Lights for Unity does, its steps jittered by Jimenez's
interleaved gradient noise (2014) --
listed with links under *References* below. Apart from the ray tracer above, the implementations
were written for this mod.

Other mods were read to understand the game's behaviour and to stay compatible with them:
xdedss's CS_SSR (its note that the game's camera keeps no glossiness in any screen buffer), and
Volumetric Weather, Volumetric Explosions and RealFlag, by the same author, whose foundations
(settings, panel, logging, localization, tools) this mod's are. When Volumetric Weather runs,
Real Glass & Water draws that mod's own cloud volume and distant clouds (found by their objects' names)
into its reflected sky; nothing of it is copied. algernon's BOB was read for how its building picker works (a tool
of the game's kind, Escape handled before the game's own) and for keeping its per-building data in
a file of its own rather than in the save; Network Anarchy's keys, so the picker's default key
does not clash with its grid key. Procedural Objects was read for how it keeps and draws a building
converted with it; when it runs, Real Glass & Water reads its objects each frame (their meshes,
places and whether it draws them) and draws the glass over its buildings, changing nothing of it.
Intersection Marking Tool was read for where it draws the props of its markings; when it runs, Real
Glass & Water draws over the props it draws (after each of its draws, its own calls unchanged). The
picker's key for the next thing under the cursor follows Node Controller's Ctrl+Space (only the key;
nothing of it was read). No code from any other mod is included.

### References

Every technique here has shipped in a game or is published:

1. **Mirror's Edge Catalyst**, "Lighting the City of Glass" (Fabien Christin, DICE, GDC 2016): a
   city of glass towers; reflections layered as screen-space reflections, then local cubemaps
   updated one face per frame, then a sky-visibility mask.
   [slides](https://www.slideshare.net/slideshow/lighting-the-city-of-glass/60383382),
   [GDC Vault](https://gdcvault.com/play/1023284/Lighting-the-City-of-Glass)
2. Tomasz Stachowiak, **Stochastic Screen-Space Reflections** (Frostbite, SIGGRAPH 2015 Advances
   in Real-Time Rendering). [Frostbite](https://www.ea.com/frostbite/news/stochastic-screen-space-reflections),
   [course](https://advances.realtimerendering.com/s2015/)
3. Morgan McGuire and Michael Mara, **Efficient GPU Screen-Space Ray Tracing**, JCGT 3(4), 2014:
   the tracer here is adapted from its code (BSD 2-Clause; see *Code adapted into the shaders*
   above).
   [JCGT](https://jcgt.org/published/0003/04/04/),
   [code](http://casual-effects.blogspot.com/2014/08/screen-space-ray-tracing.html)
4. Adrian Courrèges, **GTA V -- Graphics Study** (2015): an environment cubemap rendered every
   frame, 128 x 128 per face, of low-poly scenery.
   [article](https://www.adriancourreges.com/blog/2015/11/02/gta-v-graphics-study/)
5. **Marvel's Spider-Man** (Insomniac, 2018): building reflections blended from cube maps by
   height (Digital Foundry's analysis).
   [analysis](https://www.resetera.com/threads/marvels-spider-man-the-complete-digital-foundry-analysis.66364/)
6. Christophe Schlick, *An Inexpensive BRDF Model for Physically-based Rendering* (1994) --
   Fresnel; the glass presets use architectural glass makers' published visible transmittance
   and reflectance: Vitro Architectural Glass (formerly PPG), *Vistacool Subtly Reflective and
   Solarcool Reflective Glasses*, product data sheet, 2017
   ([PDF](https://www.vitroglazings.com/media/ljwdoown/vitro_vc-sc_datasheet_20981vitro7161_feb2017_1.pdf)),
   and [its blue-green tinted glasses](https://www.vitroglazings.com/media/eshbl2wn/vitro-blue-green-datasheet.pdf).
   The colour of a coated, tinted lite's reflection -- the coating's share through the tint
   twice, the outer surface's 4% untinted -- is the reflectance of a lite with a coating on its
   inner face as the glazing standards work it out: **EN 410**, *Glass in building --
   Determination of luminous and solar characteristics of glazing*, and **ISO 9050**, *Glass in
   building -- Determination of light transmittance, solar direct transmittance, total solar
   energy transmittance, ultraviolet transmittance and related glazing factors*.
7. Why real facades warp reflections: insulating glass units "pillow" with pressure and
   temperature ([research](https://www.researchgate.net/publication/335891891)); heat-treated
   glass carries roller wave distortion ([Lerch Bates](https://www.lerchbates.com/news/minimizing-roller-wave-distortion-in-heat-treated-glass/),
   [Vitro](https://glassed.vitroglazings.com/understanding-glass-distortion-and-anisotropy));
   a pillowed pane's shape is a plate's first mode under uniform pressure (Navier's solution).
8. xdedss, [**CS_SSR**](https://github.com/xdedss/CS_SSR): a screen-space reflection experiment
   for this game, whose note that no screen buffer says what is shiny is why the glass here is
   drawn per building, from each asset's own maps.
9. Reflections of the previous frame's finished picture, reprojected: Michal Valient, **Taking
    Killzone Shadow Fall Image Quality into the Next Generation** (Guerrilla, GDC 2014).
    [Guerrilla](https://www.guerrilla-games.com/read/taking-killzone-shadow-fall-image-quality-into-the-next-generation-1)
    Clouds in the reflected sky: captured into the reflection cube as Unreal's
    [Sky Light](https://dev.epicgames.com/documentation/en-us/unreal-engine/sky-lights-in-unreal-engine)
    captures its [volumetric clouds](https://dev.epicgames.com/documentation/en-us/unreal-engine/volumetric-cloud-component-in-unreal-engine).
10. The buildings around, from a map of the city seen from above: CRYENGINE's height-map ambient
    occlusion (3.8.4) draws "a top-down depth map of the world around the camera" and gathers
    occlusion from it at "hundreds-of-metres scale"
    ([documentation](https://docs.cryengine.com/pages/viewpage.action?pageId=26215324)); Unreal's
    distance-field ambient occlusion "also provides approximate specular occlusion on Sky Lights",
    heightfields included
    ([documentation](https://dev.epicgames.com/documentation/unreal-engine/distance-field-ambient-occlusion-in-unreal-engine));
    the walk from cell to cell is John Amanatides and Andrew Woo, **A Fast Voxel Traversal
    Algorithm for Ray Tracing**, Eurographics 1987 ([paper](http://www.cse.yorku.ca/~amana/research/grid.pdf)).
11. Steady glints, specular anti-aliasing: Anton Kaplanyan, Stephen Hill, Anjul Patney and Aaron
    Lefohn, **Filtering Distributions of Normals for Shading Antialiasing** (HPG 2016); Yusuke
    Tokuyoshi and Anton Kaplanyan, **Improved Geometric Specular Antialiasing** (I3D 2019,
    [paper](https://yusuketokuyoshi.com/papers/2019/ImprovedGeometricSpecularAA.pdf)); the
    constants (variance 0.15, threshold 0.2) are Filament's
    ([materials guide](https://google.github.io/filament/Materials.md.html)).
12. The screen traced over a depth pyramid: Yasin Uludag, **Hi-Z Screen-Space Cone-Traced
    Reflections**, GPU Pro 5 (CRC Press, 2014) -- each level the nearest depth of the four below
    it, whole cells crossed where the ray stays in front; beside it the farthest, so cells a ray
    passes behind are crossed too, as Art Tevs, Ivo Ihrke and Hans-Peter Seidel's **Maximum
    Mipmaps for Fast, Accurate, and Scalable Dynamic Height Field Rendering** (I3D 2008) bound a
    height field from both sides.
13. The painted lines kept: unsharp masking -- each pixel against a blurred copy of the picture
    -- after William F. Schreiber, **Wirephoto Quality Improvement by Unsharp Masking**, Pattern
    Recognition 2(2), 1970; the blurred copy, a storey wide, is a coarser level of the asset's own
    mipmap: Lance Williams, **Pyramidal Parametrics**, SIGGRAPH 1983.
14. A steady edge where the glass meets the painted frames: the asset's shine against the glass
    threshold is anti-aliased as an alpha test is -- a filtered step a pixel wide, RenderMan's
    `filterstep` (A. A. Apodaca and Larry Gritz, **Advanced RenderMan: Creating CGI for
    Motion Pictures**, Morgan Kaufmann 1999, "Shader Antialiasing") and Ben Golus,
    **Anti-aliased Alpha Test: The Esoteric Alpha To Coverage** (2017,
    [article](https://bgolus.medium.com/anti-aliased-alpha-test-the-esoteric-alpha-to-coverage-8b177335ae4f))
    -- over four reads of the pixel placed as Direct3D 11's standard 4x multisample pattern
    ([documentation](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ne-d3d11-d3d11_standard_multisample_quality_levels)).
15. The water's reflectance: the sea surface's slopes, Charles Cox and Walter Munk,
    **Measurement of the Roughness of the Sea Surface from Photographs of the Sun's Glitter**,
    Journal of the Optical Society of America 44(11), 1954 (mean square slope 0.003 + 0.00512 U
    for a wind of U m/s); the sky's light reflected by such a sea, Curtis D. Mobley,
    **Estimation of the Remote-Sensing Reflectance from Above-Surface Measurements**, Applied
    Optics 38(36), 1999; Schlick's Fresnel (6) with water's index, 1.333, on each ripple, and that
    rough sea's reflectance where the ripples' texture filtering has averaged them away -- Eric
    Bruneton, Fabrice Neyret and Nicolas Holzschuch, **Real-time Realistic Ocean Lighting using
    Seamless Transitions from Geometry to BRDF**, Computer Graphics Forum 29(2), 2010. The ripples are a sum of
    directional sine waves -- Mark Finch, **Effective Water Simulation from Physical Models**, GPU
    Gems (2004), chapter 1
    ([online](https://developer.nvidia.com/gpugems/gpugems/part-i-natural-effects/chapter-1-effective-water-simulation-physical-models))
    -- with whole wave numbers across the texture so that it tiles, at deep water's speeds,
    omega^2 = g k (Jerry Tessendorf, **Simulating Ocean Water**, SIGGRAPH course notes, 2001),
    every octave of wavelength with the same slope as in the equilibrium range (O. M. Phillips,
    **The equilibrium range in the spectrum of wind-generated waves**, Journal of Fluid Mechanics
    4(4), 1958). As **ocean waves** (the default) the ripples are instead an inverse FFT of a wind
    sea's spectrum, as Tessendorf (above) builds an ocean -- two cascades tiling the texture, the
    crests made choppy by the sideways displacement's Jacobian, the transform in radix-2 self-sorting
    steps (Thomas G. Stockham Jr., **High-speed convolution and correlation**, AFIPS Spring Joint
    Computer Conference, 1966) -- the spectrum fetch-limited JONSWAP (K. Hasselmann et al.,
    **Measurements of wind-wave growth and swell decay during the Joint North Sea Wave Project
    (JONSWAP)**, Deutsche Hydrographische Zeitschrift, Supplement A8(12), 1973) spread over directions as
    M. A. Donelan, J. Hamilton and W. H. Hui measured (**Directional spectra of wind-generated
    waves**, Philosophical Transactions of the Royal Society A 315, 1985), both as Christopher J.
    Horvath gathers them for graphics (**Empirical directional wave spectra for computer graphics**,
    DigiPro 2015); the amplitudes drawn with SplitMix64 (Guy L. Steele Jr., Doug Lea and Christine
    H. Flood, **Fast Splittable Pseudorandom Number Generators**, OOPSLA 2014); the waves too short
    for the texture carried on down to the gravity-capillary limit (T. Elfouhaily, B. Chapron, K.
    Katsaros and D. Vandemark, **A unified directional spectrum for long and short wind-driven
    waves**, Journal of Geophysical Research 102(C7), 1997). With **dynamic waves** (the default)
    the wind rises with the game's rain and the waves' roughness with it as Cox and Munk (above)
    measured, and the sea takes the time to follow that a wind needs to raise its fetch-limited sea
    (U.S. Army Corps of Engineers, **Shore Protection Manual**, 4th edition, 1984, chapter 3). The sun's glitter is the sun mirrored
    by a sea of facets whose slopes are Gaussian about the ripple texture's own, as wide as its
    mipmaps averaged away (Marc Olano and Dan Baker, **LEAN Mapping**, I3D 2010) and the waves too
    short for it make them -- the microfacet model of Bruce Walter, Stephen R. Marschner, Hongsong
    Li and Kenneth E. Torrance (**Microfacet Models for Refraction through Rough Surfaces**, EGSR
    2007) with the masking of Gaussian slopes by Bruce G. Smith (**Geometrical shadowing of a random
    rough surface**, IEEE Transactions on Antennas and Propagation 15(5), 1967), as Bruneton et al.
    (above) light the sun on the sea; erfc as Milton Abramowitz and Irene A. Stegun give it
    (**Handbook of Mathematical Functions**, 1964, 7.1.26). The idea came from web-ocean
    ([github.com/Noniv/web-ocean](https://github.com/Noniv/web-ocean), MIT), whose README names
    these techniques; none of its code is used. The city in the water is traced through the depth buffer as in (3), from those
    ripples as the game's own water shader reads them; its streaks are a Gaussian up
    and down weighted so only water is averaged -- normalized convolution, Hans Knutsson and
    Carl-Fredrik Westin, **Normalized and Differential Convolution**, CVPR 1993. Against the
    flicker of a reflection traced off moving ripples, each frame's trace is blended with the last
    frame's, reprojected to where the water's point was and clamped to its neighbours' range --
    Brian Karis, **High-Quality Temporal Supersampling** (SIGGRAPH 2014 Advances in Real-Time
    Rendering, [slides](https://advances.realtimerendering.com/s2014/)) and Lasse Jon Fuglsang
    Pedersen, **Temporal Reprojection Anti-Aliasing in INSIDE** (GDC 2016). Two alternatives
    are compiled into the same shader but switched off: the flat mirror's picture distorted by the
    ripples' normals (Tiago Sousa, **Generic Refraction Simulation**, GPU Gems 2, chapter 19,
    2005) and the reflected picture read as wide as a ray cone spreads (Tomas Akenine-Möller et
    al., **Texture Level of Detail Strategies for Real-Time Ray Tracing**, Ray Tracing Gems,
    chapter 20, 2019).
16. The foam: where it goes, from the game's own water simulation -- shoreline foam where the water
    is shallow, as in Crest (Huw Bowles, Daniel Zimmermann et al., **Crest: Novel Ocean
    Rendering Techniques in an Open Source Framework**, SIGGRAPH 2017 Advances in Real-Time
    Rendering), its lines of broken waves phased by the depth as War Thunder phases its shore waves
    by the distance to the shore (Tim Tcheblokov, **Ocean simulation and rendering in War
    Thunder**, NVIDIA, CGDC 2015), and fronts where the water rises fast, the breaking test of Nuttapong
    Chentanez and Matthias Mueller, **Real-time Simulation of Large Bodies of Water with Small Scale
    Details**, Symposium on Computer Animation 2010. It moves along the flow in two phases half a
    cycle apart -- Alex Vlachos, **Water Flow in Portal 2**, SIGGRAPH 2010, the game's own way with
    its ripples. Its pattern: Steven Worley, **A Cellular Texture Basis Function**, SIGGRAPH 1996,
    for the lace of bubble walls, and a fractal sum of noise (Ken Perlin, **An Image Synthesizer**,
    SIGGRAPH 1985) for the patches, each equalized so that a share a of foam leaves exactly a of
    the pattern white. Its brightness: dense sea foam's reflectance, Peter Koepke, **Effective
    reflectance of oceanic whitecaps**, Applied Optics 23(11), 1984.
    Its layers, as a whitecap ages: the foam of a crest breaking now and the sheet it leaves
    (stages A and B: Edward Monahan and Ming Lu, **Acoustically relevant bubble assemblages and their
    dependence on meteorological parameters**, IEEE Journal of Oceanic Engineering 15(4), 1990), how
    long each lasts (Jamie Potter et al., **Magnitude and spatial variability of the whitecap foam
    lifetime**, 2015; Adrian Callaghan et al., **Observed physical and environmental causes of scatter
    in whitecap coverage values in a fetch-limited coastal zone**, Journal of Geophysical Research
    117, 2012, [paper](https://data-ww3.ifremer.fr/BIB/Callaghan_etal_JGR2012.pdf)), and the bubbles a
    break pushes under the surface (Eric Terrill, W. Kendall Melville and Dariusz Stramski, **Bubble
    entrainment by breaking waves and their influence on optical scattering in the upper ocean**,
    Journal of Geophysical Research 106(C8), 2001), seen as the water's own colour made paler -- its
    body reflectance f b_b / (a + b_b) (Howard Gordon, Otis Brown and Michael Jacobs, **Computed
    relationships between the inherent and apparent optical properties of a flat homogeneous ocean**,
    Applied Optics 14(2), 1975; André Morel and Louis Prieur, **Analysis of variations in ocean
    color**, Limnology and Oceanography 22(4), 1977) through the water above them (Robin Pope and
    Edward Fry, **Absorption spectrum (380-700 nm) of pure water**, Applied Optics 36(33), 1997) --
    drawn under the white with a parallax, and the fresh foam lit as if it had volume, as Crest's
    foam bubbles and foam lighting are ([documentation](https://crest.readthedocs.io/en/stable/user/water-appearance.html)).
    The surf: a band along the shore measured in distance from it, as War Thunder phases its shore
    waves by the distance to the shore (Tcheblokov, above) -- the distance here the depth over the
    seabed's slope, held within a corridor round the true distance from the waterline, mapped over
    the whole map by the exact Euclidean distance transform of Pedro Felzenszwalb and Daniel
    Huttenlocher, **Distance Transforms of Sampled Functions**, Theory of Computing 8, 2012
    ([paper](https://theoryofcomputing.org/articles/v008a019/)) -- broken waves running in at the shallow-water speed, the bigger breaking
    farther out, as deep as their height allows (James McCowan, **On the highest wave of permanent
    type**, Philosophical Magazine 38, 1894), in sets (Michael Longuet-Higgins, **Statistical
    properties of wave groups in a random sea state**, Philosophical Transactions of the Royal Society
    A 312, 1984); beyond the surf the swell's crests breaking only here and there, gently, the
    spilling breakers of Cyril Galvin, **Breaker type classification on three laboratory beaches**,
    Journal of Geophysical Research 73(12), 1968. The lace torn into streaks by domain warping (Inigo Quilez, **Domain warping**,
    [article](https://iquilezles.org/articles/warp/)). The surf's lines without corners: the seabed
    they are measured over read as a cubic B-spline, its height and its slope from the same reads
    (Christian Sigg and Markus Hadwiger, **Fast Third-Order Texture Filtering**, GPU Gems 2, chapter
    20, 2005, [chapter](https://developer.nvidia.com/gpugems/gpugems2/part-iii-high-quality-rendering/chapter-20-fast-third-order-texture-filtering)),
    its limits rounded by the polynomial smooth minimum (Inigo Quilez, **Smooth minimum**,
    [article](https://iquilezles.org/articles/smin/)); the end of a streak beyond the surf set by how
    far it lies inside the noise's level set, the noise over its own slope (Gabriel Taubin,
    **Distance approximations for rasterizing implicit curves**, ACM Transactions on Graphics 13(1),
    1994).
17. The rooms behind the windows: layered as **Forza Horizon 4**'s windows are -- the room, a
    curtain or blind of its own that lets the room's light through at night, a picture per room
    from a group of its building's kind, one flat picture far away (Gareth Harwood, Playground
    Games, **A window into Playground Games' latest shader development**, Game Developer, 2019,
    [article](https://www.gamedeveloper.com/design/game-tech-deep-dive-a-window-into-playground-games-latest-shader-development));
    each room a box behind its window whose walls, floor and ceiling the view's ray meets -- Joost
    van Dongen's **Interior Mapping**, CGI 2008 ([article](https://www.gamedeveloper.com/programming/interior-mapping-rendering-real-rooms-without-geometry))
    -- kept as one picture of the box seen from in front of the window, as SimCity (Maxis, 2013;
    Andrew Willmott) kept its rooms and Ben Golus wrote it up
    ([Unity forums](https://discussions.unity.com/t/interior-mapping/424676)); each box painted
    from its panorama's middle, as an interior cubemap is.
    Lit windows scattered at random, some office buildings a whole floor at a time: Shamus Young's
    procedural city (2009, [article](https://www.shamusyoung.com/twentysidedtale/?p=2954)). How many
    are lit at each hour: homes after the shape of Ian Richardson, Murray Thomson, David Infield and
    Alice Delahunty, **Domestic lighting: A high-resolution energy demand model**, Energy and
    Buildings 41(7), 2009; offices after EnergyPlus's office lighting schedule, with a share left on
    all night as Gregory Dobler et al. observed of commercial windows (**Dynamics of the urban
    lightscape**, Information Systems 54, 2015). The lamps' colours: Mitchell Charity's blackbody
    colour table ([vendian.org](http://www.vendian.org/mncharity/dir3/blackbody/)). The pictures are
    cut from [Poly Haven](https://polyhaven.com/hdris/indoor)'s indoor panoramas (CC0), the rooms'
    own daylit windows filled from round them by normalized convolution (Knutsson and Westin, 15);
    the offices, shops and some homes are rooms of the mod's own, rendered in Blender as the same box
    rooms and furnished in part with [Poly Haven](https://polyhaven.com/models)'s 3D models (CC0).
18. The sky at dusk: how a clear sky's light falls in the sun's last ten degrees, from Richard Bird
    and Carol Riordan, **Simple Solar Spectral Model for Direct and Diffuse Irradiance on Horizontal
    and Tilted Planes at the Earth's Surface for Cloudless Atmospheres** (SPCTRAL2), Journal of
    Climate and Applied Meteorology 25(1), 1986, with Fritz Kasten and Andrew Young's air mass
    (**Revised optical air mass tables and approximation formula**, Applied Optics 28(22), 1989).
    The sun's glint behind a cloud: its direct beam through the cloud's opacity (Beer-Lambert).
19. Picking a building of Procedural Objects with the mouse: the ray against its box, the slab
    test of Timothy Kay and James Kajiya, **Ray Tracing Complex Scenes**, SIGGRAPH 1986; then
    against the triangles of the nearest boxes, Tomas Möller and Ben Trumbore, **Fast, Minimum
    Storage Ray/Triangle Intersection**, Journal of Graphics Tools 2(1), 1997.
20. The model's glass on its LOD: each texel of the simplified mesh's texture met with the full
    mesh along its normal and given the full mesh's value there -- Paolo Cignoni, Claudio Montani,
    Claudio Rocchini and Roberto Scopigno, **A General Method for Preserving Attribute Values on
    Simplified Meshes**, IEEE Visualization 1998 (the game's own asset importer bakes an LOD's
    textures from its model this way when the author gives none); the full mesh's triangles found
    through a uniform grid, Akira Fujimoto, Takayuki Tanaka and Kansei Iwata, **ARTS: Accelerated
    Ray-Tracing System**, IEEE Computer Graphics and Applications 6(4), 1986, and met by Möller and
    Trumbore's test (19); its shine read from a mip as wide as the texel (Lance Williams,
    **Pyramidal Parametrics**, SIGGRAPH 1983); whether the LOD's own glass agrees with its model's,
    by the share of the two glasses that is both (Paul Jaccard, **The Distribution of the Flora in
    the Alpine Zone**, New Phytologist 11(2), 1912).
21. The rooms fitted to each asset's own windows: the shine of its model's upright faces summed by
    height, and along each side, and the rhythm of each found as the lag of its strongest
    autocorrelation -- as Lawrence Rabiner found the pitch of speech (**On the Use of
    Autocorrelation Analysis for Pitch Detection**, IEEE Transactions on Acoustics, Speech, and
    Signal Processing 25(1), 1977) -- then refined to the millimetre by epoch folding, the period
    whose folded profile varies most (D. A. Leahy et al., **On searches for pulsed emission with
    application to four globular cluster X-ray sources**, The Astrophysical Journal 266, 1983).
22. Vehicles: their glass a slab of soda-lime (n 1.52, its two surfaces and the light passing
    between them summed as a slab's multiple reflections), clear by the at least 70% light
    transmittance **UN Regulation No. 43** asks of a windscreen
    ([UNECE](https://unece.org/transport/vehicle-regulations-wp29/standards/addenda-1958-agreement-regulations-41-60));
    their paint under a clear coat as **Filament**'s clear-coat layer -- lacquer of n 1.5, the paint
    beneath dimmed by the coat's Fresnel reflectance (Romain Guy and Mathias Agopian, **Physically
    Based Rendering in Filament**, [documentation](https://google.github.io/filament/Filament.html))
    -- at the default coat roughness of **Autodesk Standard Surface**
    ([specification](https://autodesk.github.io/standard-surface/)); each vehicle's own random
    number (whether its windows are tinted, and how dark) from its id by the PCG hash (Mark
    Jarzynski and Marc Olano, **Hash Functions for GPU Rendering**, Journal of Computer Graphics
    Techniques 9(3), 2020, [paper](https://jcgt.org/published/0009/03/02/)).
23. Under the water: what the camera sees as the light from what it looks at, dimmed along the
    way, plus the light the water scatters toward it -- the model of B. L. McGlamery (**Computer
    analysis and simulation of underwater camera system performance**, Scripps Institution of
    Oceanography, 1975) and Jules S. Jaffe (**Computer modeling and the design of optimal underwater
    imaging systems**, IEEE Journal of Oceanic Engineering 15(2), 1990), as revised by Derya Akkaynak
    and Tali Treibitz, **A Revised Underwater Image Formation Model**, CVPR 2018
    ([paper](https://openaccess.thecvf.com/content_cvpr_2018/html/Akkaynak_A_Revised_Underwater_CVPR_2018_paper.html)).
    How far one sees: J. Ronald V. Zaneveld and W. Scott Pegau, **Robust underwater visibility
    parameter**, Optics Express 11(23), 2003 ([doi](https://doi.org/10.1364/OE.11.002997)). What the
    water holds: pure water's absorption (Pope and Fry 1997, item 16 above) and scattering (André
    Morel, **Optical properties of pure water and pure sea water**, in *Optical Aspects of
    Oceanography*, Academic Press, 1974); dissolved organic matter, Annick Bricaud, André Morel and
    Louis Prieur, **Absorption by dissolved organic matter of the sea (yellow substance) in the UV
    and visible domains**, Limnology and Oceanography 26(1), 1981
    ([doi](https://doi.org/10.4319/lo.1981.26.1.0043)); particles backscattering as Theodore J.
    Petzold measured (**Volume Scattering Functions for Selected Ocean Waters**, Scripps Institution
    of Oceanography, 1972) -- Morel's and Petzold's numbers as Curtis D. Mobley tabulates them in
    **Light and Water: Radiative Transfer in Natural Waters**, 1994, and his
    [Ocean Optics Web Book](https://www.oceanopticsbook.info/); the colour they give, Gordon et al.
    1975 and Morel and Prieur 1977 (item 16 above). The daylight fading with depth: John T. O. Kirk,
    **Dependence of relationship between inherent and apparent optical properties of water on solar
    altitude**, Limnology and Oceanography 29(2), 1984
    ([doi](https://doi.org/10.4319/lo.1984.29.2.0350)); the light under the surface brighter toward
    the horizon than toward the depths: John E. Tyler, **Radiance distribution as a function of
    depth in an underwater environment**, Bulletin of the Scripps Institution of Oceanography 7,
    1960; the glow toward the sun: Louis G. Henyey and Jesse L. Greenstein, **Diffuse radiation in
    the Galaxy**, Astrophysical Journal 93, 1941
    ([ADS](https://ui.adsabs.harvard.edu/abs/1941ApJ....93...70H)); the surface seen from below,
    Snell's window and its rim softened by the waves' averaged-away slopes, as the sun's glitter
    sees them from above (Olano and Baker's LEAN mapping, item 15), the sun through the waves by
    the transmission half of Walter et al.'s microfacet model (item 15), radiance gaining n^2 into
    the water (Mobley 1994, above); the caustics, the area a beam of
    sunlight through the waves lands on: Mark Watt, **Light-water interaction using backward beam
    tracing**, SIGGRAPH 1990 ([SIGGRAPH archive](https://history.siggraph.org/?p=122082)). The light
    shafts: single scattering with volumetric shadows, Balázs Tóth and Tamás Umenhoffer, **Real-time
    Volumetric Lighting in Participating Media**, Eurographics 2009 short papers
    ([doi](https://doi.org/10.2312/egs.20091048)); the light's shadow map taken right after it is
    rendered (LightEvent.AfterShadowMap) as **Volumetric Lights** for Unity does (SlightlyMad, BSD-3,
    [repository](https://github.com/SlightlyMad/VolumetricLights); read, nothing copied); the steps'
    starts spread by interleaved gradient noise, Jorge Jimenez, **Next Generation Post Processing in
    Call of Duty: Advanced Warfare**, SIGGRAPH 2014 Advances in Real-Time Rendering course
    ([slides](http://www.iryoku.com/next-generation-post-processing-in-call-of-duty-advanced-warfare)).
24. The glass's reflections in a screen pass of their own: the glass drawn first into a thin
    G-buffer, its pane normals in octahedral form (Zina H. Cigolle, Sam Donow, Daniel Evangelakos,
    Michael Mara, Morgan McGuire and Quirin Meyer, **A Survey of Efficient Representations for
    Independent Unit Vectors**, JCGT 3(2), 2014, [jcgt.org](https://jcgt.org/published/0003/02/01/));
    each frame's reflection blended with the last, fetched where its mirror image (the view ray
    extended by the reflected distance) was on last frame's screen and clamped to its neighbours'
    range (Karis 2014, as the water's history above). Each asset's glass threshold read from its
    textures: by the windows the game lights at night as the label, the least balanced error between
    the two classes, or else by the shine alone with Nobuyuki Otsu, **A Threshold Selection Method
    from Gray-Level Histograms**, IEEE Transactions on Systems, Man, and Cybernetics 9(1), 1979
    ([doi](https://doi.org/10.1109/TSMC.1979.4310076)).
25. Props: a decal's glass or water laid where the decal is, the point read from the depth buffer
    and taken into the decal's box, as screen-space (deferred) decals are drawn: Jan Krassnigg,
    **A Deferred Decal Rendering Technique**, in *Game Engine Gems 1*, Jones and Bartlett, 2010,
    and Pope Kim, **Screen Space Decals in Warhammer 40,000: Space Marine**, SIGGRAPH 2012 Talks
    ([doi](https://doi.org/10.1145/2343045.2343053)).

## Trademarks

Cities: Skylines is © Colossal Order Ltd. and published by Paradox Interactive AB; the names
are trademarks of their respective owners. Unity is a trademark of Unity Technologies. This
project is unofficial, fan-made, and not affiliated with or endorsed by any of them.
