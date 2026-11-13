# Changelog

All notable changes to Real Glass & Water. The version is `Mod.Version` in `RealGlass/Mod.cs`, shown after
the mod's name in the game.

## 1.1.9 — unreleased

### New

- **Profiles**, the way Volumetric Weather has them: save the whole look under a name at the top of
  the in-game panel and switch between looks in a click. Save writes your changes into the picked
  profile, Revert throws them away, + saves the look as a new profile and - deletes one. "(mod
  defaults)" brings every default back, and the panel says when you have unsaved changes (and asks
  before a pick would lose them). A row's reset button goes back to the picked profile's value.
  Each profile is a file in the RealGlassProfiles folder beside RealGlass.xml, so you can share one
  or edit it by hand, even with the game running. A profile holds every setting of the panel's tabs,
  not your computer's (quality, language, keys, the button, the panels' look), the glass mask view,
  the shore brush, your buildings' own glass or a city's painted shores.
- **Paint shores** (Water tab, Foam): a brush on the map paints where the surf breaks and where
  it doesn't. Paint it out of harbors, marinas and lagoons, or into a lake or reservoir the surf
  never reaches. Ctrl paints a place back to automatic; Ctrl+Z and Ctrl+Y step back and forward.
  While you paint, the in-game panel steps aside for a small brush window (size, strength, hardness,
  clearing the city's painted shores, Done) and comes back when you're done ([ and ] change the size too).
  While you paint, the water shows the waterline, how far the surf reaches, your paint and each kind
  of foam. Saved with the city (a few KB); a city you never paint is unchanged.
- **Underwater color** (Water tab, Water color): the tint the seabed takes under the water, which
  used to come only from the map theme (or Theme Mixer). The theme's stays by default; turn off Use
  the map theme's underwater color to pick your own.
- **Glass on** (Glass tab): switch the glass off on any kind of building: residential, commercial,
  offices, industrial, city services, transport, unique buildings, monuments, parks and plazas, and
  everything else. All on by default. A building you gave a strength of its own keeps its glass.
- **New rooms behind the windows**: 24 offices (open plan, desk benches, cubicles, meeting rooms,
  private offices, receptions, break rooms), 15 shops (grocery, clothing, electronics, pharmacy, café,
  bank, books) and 11 more homes, rendered for the mod in Blender with Poly Haven's CC0 furniture and
  plants. Offices used to borrow photos of a studio and a hotel room. 112 rooms in all, up from 64.
- **Commercial RGB lights** (Interiors tab): workplaces (offices, shops, factories, public buildings)
  get their own share of colored lights, 0% by default. The old slider, now Residential RGB lights,
  covers homes, hotels, bars and restaurants.
- **Show advanced options in the in-game panel** (options page): the Reflections and Panes tabs, and
  the Glass tab's What counts as glass, now show only with this on. Their settings still apply when
  it's off.
- **Glass and water on props**: the building picker (Alt+G) now picks props too, and the building
  window has a Props button listing everything the building carries. Each prop can be Off, Glass or
  Water, with the building window's settings: strength, kind of glass, color, reflectivity, clarity
  and shininess needed (0%, the default, covers the whole prop). It applies to every copy of the
  prop in every city and is saved in RealGlassAssets.xml with your buildings. Glass starts as clear
  low-E; water reflects like still water and ripples with the Water tab's ripples. Made for the
  see-through glass and water props from the Workshop (glass panes and roofs, pools, water
  surfaces). Decals and props the game bends over the ground or the water can't take it.
  Procedural Objects' props too: the picker finds them, and a prop made glass or water there gets it
  wherever Procedural Objects draws it.
  Each surface shows only its own settings. New ones: Frosted (glass), sun glint, and
  ripple strength, size and speed (water). Water's reflection is now its
  Strength, 100% being real still water, up to 300%; water takes the Water tab's color unless you give
  it one, so a pool reads from above.
  Intersection Marking Tool's props get it too, and the picker finds them.
  **Ctrl+Space** while picking steps through everything under the cursor, nearest first, so a prop under
  a tree or behind another can be picked (the key is on the options page).
- **Detect per asset** (Glass tab, on by default): each building asset's "Shininess needed" read from
  its own textures, where they clearly part glass from wall (by the windows the game lights at night,
  or by the shine alone). Assets whose walls are painted shiny lose their glass walls, assets whose
  windows are painted dull get their glass. Most assets keep the slider's value (about one in eight
  changes, in a survey of 1,458 Workshop assets); a value you set in the building window still wins.
  The first load after the update measures every
  asset in the city once more (a minute or so, spread over the first frames), then it's remembered.

### Changed

- The panel grows with each tab, up to the screen's height, instead of keeping the first tab's height
  for every tab (304 px at most screens, everything else scrolling). It's also a little wider.
- Clearer names in the panels: Silvery edge-on is now Silver at an angle; in the building window,
  "This asset will have its own settings" is Asset settings and Follow its illumination map is Use
  asset's illumination map.
- The building window opens straight onto its buttons and settings: the lines above them (copies in
  the city, what its LOD shows, its rooms, its detected shininess) are gone.

### Fixed

- The glass flickers less under TAA (Render It!'s anti-aliasing): its reflections are now worked out
  in a pass of their own over the whole screen and blended with the last frame's, so a reflection no
  longer crawls between a building and the sky from one frame to the next.
- The foam's edges are no longer pointy. The lines of surf follow a smoothed seabed instead of every
  crease of the terrain's grid, and the foam at the water's edge ends in the water with a soft, lobed
  edge of its own, running in and draining back with each wave, instead of tracing the waterline's
  corners in white. The streaks of foam out past the surf end a little more softly too.

## 1.1.8 — 2026-11-04

### Fixed

- The city in the water: dark buildings no longer reflect black with red edges at sunset and at
  night. The reflection's strength (300% by night) also multiplied the sky it replaces, taking away
  more sky than the water showed; now above 100% it brightens the city only, and the sky taken away
  never exceeds what the water shows. Below 100% the reflection fades toward the bare water, as
  before.
- The mod's message windows (installed twice, a part stopped after an error, not supported) showed no
  text: the game's window cut its text at the "&" of the mod's name.

## 1.1.7 — 2026-11-02

### New

- **Where to show mod icon** (the options page): Unified UI, Toolbar (the mod's own button) or Never
  (the panel's key alone opens it). It replaces "Show icon in Unified UI"; with that off, the mod's
  own button stays.

### Fixed

- The mod's button shows its name when the mouse rests on it, in Unified UI's bar and on its own:
  neither had a tooltip.

## 1.1.6 — 2026-10-31

### New

- **Under the water** (the Water tab, on by default): with a camera mod that takes the camera below
  the surface (ACME, First Person Camera), the water is water from inside -- a teal haze that
  thickens with distance and takes red first, darker the deeper you go and glowing toward the sun,
  while what lies under the water stays in sight; light shafts where the sun comes in between the
  shadows of bridges and buildings (from the game's own shadows; not on a Mac yet); the surface
  seen from below as moving waves, with the sky bent into Snell's window, the sun shining through
  the waves, and a mirror of the depths around it. A camera half in the water sees the waterline.
  Rows for the visibility (60 m by default), the light shafts, and sunlight from the waves on the
  seabed (off by default). Nothing is drawn while the camera is above the water.

### Fixed

- **No red sea at sunset and sunrise.** The sky the water mirrors is the game's own, and that sky
  colours its lowest few degrees, and everything below the horizon, a deep red all round from shortly
  before sunset until night. On screen the game's fog covers that band, but the water mirrored the
  bare sky, so distant and choppy water turned red even away from the sun. The water now mirrors the
  sky with the game's fog over it, as you see it.
- **Window frames in front of the glass stay frames from afar.** On buildings whose window frames are
  their own geometry (modelled, or an alpha-mapped plane) standing a few centimetres in front of the
  glass, the glass was drawn over the frames once the camera zoomed out, so they turned faint and
  glass-colored. The glass no longer reaches in front of its own surface.
- **No square of floor and seats through car windows.** On vehicles whose windows are painted only
  partly shiny, the game's floor texture behind the window, a hard-edged square, still showed through
  the glass, glowing at night. The glass now covers those windows fully.
- **No lit window under the rooms at night.** On buildings whose illumination map marks its lit
  windows with a low value (around 50%, a common one), the room behind the glass covered only part
  of each window when the game lit it, and the game's own light showed through under the room. The
  room now covers every window the game can light, by the game's own rule.
- **The city in the water with two dynamic-resolution mods.** With Lumina's dynamic resolution and
  the older Dynamic Resolution mod both on, the game draws the city twice a frame, and the second
  picture, the one on screen, had no city reflected in the water. Both pictures now have it. (Lumina
  already includes dynamic resolution, so the older mod is not needed beside it.)
- **Tall waves no longer see-through.** The game draws its water without telling the effects that
  come after it (the fog, Volumetric Weather's clouds) where the water is, so they took each water
  pixel for whatever lies behind it: a tsunami wave standing against the sky was filled with fog and
  covered with clouds, as if it were not there, with a hard line at the horizon, and the fog over the
  water showed the outlines of piers and turbine bases hidden under it. The water's surface is now
  handed to those effects right after it is drawn (Windows and Linux; on a Mac the game already does).

### Changed

- **Glass on vehicles keeps less memory.** Every vehicle model that came near used to reserve room
  for 500 copies at once (about 34 KB) for the rest of the session, whether one or a hundred of it
  were ever in view. The room now grows with the copies actually in view, and the number of models
  remembered at once is capped, so a long session with many vehicle models costs a few megabytes at
  most. Nothing changes in how the glass looks. (Measured before the change on a highway with
  thousands of vehicles a second: memory did not grow with the glass on; this takes out the fixed
  reservation per model.)
- **A new button icon, on a round button.** The mod's button (and its Unified UI icon) shows a white
  mirror glyph in place of the window picture, and the button is the game's round one in every panel
  look, matching Play It!'s and Render It's buttons beside it. It was a rounded square in the
  See-through, Blue and Frosted looks.

## 1.1.5 — 2026-10-26

### Fixed

- **The water reflects the city on the Mac again.** On the Mac (Metal) the water showed the sky but
  never the city, boats or anything else above it, in every version since 1.0.0: the step that finds
  where the water is on screen read the scene's depth after the game's water was already in it, and
  took every water pixel for something hidden behind the water itself. It now reads the scene's depth
  as it stood before the water is drawn. Every other shader is byte for byte that of 1.1.4.

## 1.1.4 — 2026-10-25

### New

- **Glass on vehicles**, on a tab of its own (Vehicles). The windows of every vehicle close enough to
  see get real car glass: the street, the buildings and the sky in them, a dark inside behind them.
  Exactly the windows the game itself treats as windows on each vehicle, nothing else. Its lamps --
  headlights, brake lights, indicators -- get a lens over the game's own light. At night the windows
  stay dark. The tab has the share of vehicles with tinted windows (5% by default; each such car its
  own shade of tint film, the rest clear glass) and how strongly the glass and paint reflect. Drawn
  once per vehicle model in view (GPU instancing), not once per vehicle.
- **A clear coat on vehicle paint** (the Vehicles tab, on by default): the paint mirrors the sky and
  the street and catches the sun, as a car's lacquer does -- most of all on dark cars. Only the paint
  the game recolours gets it: tyres, wheels, trim, grilles and lights stay as they are. Vehicles
  painted in a fixed livery (no recolouring) don't get it yet.
- **For asset creators: an asset's glass ships with it.** In the Asset Editor the mod's key and
  button open a window made for creators: the glass of the building being made -- strength, kind of
  glass, color, reflectivity, clarity, what counts as glass, lit windows, its rooms, its LOD's glass
  -- shown on it as it is set. Its preview starts without the mod's glass (how subscribers without
  the mod see the asset); switched on, the glass shows at the mod's default settings, with what
  counts as glass and the rooms by day (the Asset Editor has no night) to hand. Saving
  the asset writes its glass into `RealGlassDefinition.xml` beside its .crp; sharing it from the
  Content Manager puts that file into the upload. Opening the asset again brings its glass back.
- **Players get the creators' glass**: every setting an asset's creator set is that asset's, in
  every city; the rest follow the player's settings. A player's own settings for an asset (Alt+G)
  come first, and the building window says when a creator set its glass.
- **Opacity by day** for the rooms (the Interiors tab, under "Show the rooms by day"): how much
  of the rooms shows behind the glass in daylight, from none (the plain dark inside) to the rooms in
  full. 50% by default.
- **Curtains per building asset.** The building window (Alt+G) and the creator window in the Asset
  Editor have a *Curtains or blinds* switch under Interiors. Off, that asset's rooms have no curtains
  or blinds -- for glass that is no room's window, such as a fire station's doors; on, as many as the
  Interiors tab gives. A creator's choice ships with the asset, like the rest of its glass.
- **Rooms lit by the asset's illumination map** (the Interiors tab, under "Lights at night"; off by
  default). On, a building's rooms light where and when the game would light its windows: the
  windows its asset's illumination map marks, on the game's own timing, every building switching on
  at dusk and off at dawn. The rooms themselves stay the mod's: their pictures, lamps and curtains.
  Off, the mod picks which rooms are lit (*Lit rooms* and the rows under it). The building window
  (Alt+G) and the creator window in the Asset Editor have the same switch per asset, *Follow its
  illumination map*: a creator who painted the asset's lit windows can ship it on, like the rest of
  its glass. On a Mac, each building's light color and the moment it switches on at dusk are the
  mod's own (the game's would take one texture more than a Mac's shader can hold).
- **Spanish, French and Simplified Chinese.** The panel, the building and creator windows, the
  options page and the messages are now in Spanish, French and Simplified Chinese as well as
  English, as in Volumetric Weather. A game set to one of them shows it automatically; *Show this
  mod in* on the options page picks one by hand. These translations were made with AI, so some
  wording may be off -- corrections are welcome. Any text not yet translated shows in English.
- **Foam in layers along the shores.** The surf is now a band some 70 m wide along the sea's shores,
  however steeply the seabed drops (before, on the game's steep coasts, the shore foam was one thin
  line), following the shore's shape in bays with a flat floor too, where it would otherwise close
  into rings. Waves break in it one after another and run in, in sets of bigger and smaller ones, each
  with a bright, churning crest, leaving a sheet of lace that thins and tears into streaks over the
  next seconds, the older waves' foam still under the newer; under the foam a cloud of bubbles turns
  the water paler, in the water's own color. Out beyond the surf the swell coming in breaks here
  and there in short white streaks, fewer the farther out, each wave in other places. The Foam
  amount means what it did: the share of the foam; a breaking crest stays white from about a third
  up.

### Changed

- **The Asset Editor is the creators'**: the creator window replaces the F8 panel there, and the
  building picker and the water stay out of it. The rooms are fitted to the windows of the building
  being made, measured again when its model or textures change.
- **Other mods come first.** Where the mod hooks into the game's drawing of vehicles and water, every
  other mod's change to the same code is now made first, on the game's own code, exactly as if this
  mod weren't installed, whatever order the mods start in; this mod fits around what they leave. An
  older mod that looks for the game's exact code finds it. If a mod takes a draw away, those vehicles
  or that water just go without this mod's glass or foam.
- **A part that stops after an error says so.** The glass on buildings, the glass on vehicles and
  the water each switch themselves off after an error, as before, and the rest of the mod runs on;
  now a message says which part stopped and where the log is, once per session, instead of the log
  alone.
- **Installed twice, one copy runs.** With two different copies enabled -- the Workshop item and one
  in `Addons\Mods` -- the copy that starts second switches itself off and says so. Before, both ran:
  the glass was drawn twice and the water's hook failed.

### Fixed

- **Tall waves are no longer see-through.** In a tsunami, the water behind a high wave -- its crest,
  its back and the sea beyond -- showed through its face: the mod's sky reflection and foam were
  drawn on every water surface along the view, not only the nearest. They now take the same depth
  test as the game's own water, so they lie exactly on the water the game shows.
- **Foam on wave fronts no longer speckles the open sea**, jumping to new places every second. It
  foamed wherever the water rose fast between two of the game's water frames, which the calm sea
  does here and there all the time; now only where that water is also steep, as a tsunami's or a
  flood's face is.

## 1.1.3 — 2026-10-22

### Fixed

- **The mod runs on the Mac again.** In 1.1.2 it stayed off on every Mac, with a "not supported"
  message: for the rooms fitted to each building's windows, the glass shader read two more
  textures, and on the Mac (Metal) that took it past the 16 a shader may use, so the game refused
  it. On the Mac the rooms now stand on the storey grid -- they show, but are not fitted to each
  building's windows there yet. Windows and Linux run exactly the code of 1.1.2, byte for byte.

## 1.1.2 — 2026-10-22

1.1.0 and 1.1.1 were released on GitHub only: on the Workshop, 1.1.2 brings everything of them
below, and this.

### Fixed

- **The rooms behind the windows on the Mac and on Linux.** They never showed there, since 1.0.0:
  Unity 5.6 translates the mod's shaders for Metal and OpenGL, and turned one test -- "are the rooms
  on for this building?" -- into one that always said no. Windows (Direct3D 11) runs exactly the
  code it ran before, byte for byte.
- **The city's reflection on the water flickers less on the Mac and on Linux**: the same fault
  skipped the step that smooths it from frame to frame there.

### Changed

- **The rooms behind the windows follow each building's own windows.** They stood on one grid for
  every building -- 3.8 m storeys from the ground, rooms 3.6 to 8 m wide -- and almost no asset has
  3.8 m storeys (most have 2.7 to 3.5 m), so the rooms' floors, ceilings and walls cut through the
  windows at a different height on every floor: stripes. Now each building asset's own windows are
  measured from its model, in the background once the city is on screen: the height of its storeys
  and where its floors are, how tall its ground floor is, and on each side how far apart its windows
  are -- and the rooms are laid out on that, the same up close and from afar. The building window
  (Alt+G) says what was found for the asset. What was measured is remembered in
  `RealGlassRoomFits.txt` (beside RealGlass.xml; safe to delete), so later loads measure only the
  assets new to them, or changed.
- **No rooms where none could stand**: on sloped glass (glass roofs, pyramids, solar panels), on
  curved or smooth-shaded glass (round towers: the rooms came out in slanted stripes), on the narrow
  facets of something round, and behind windows that are only thin strips. There the game's own
  window shows, as without rooms. A building window's *Parallax interiors* switch still puts them on.

## 1.1.1 — 2026-10-21

1.1.0 was released on GitHub only: on the Workshop, 1.1.1 brings everything of 1.1.0 below.

### Fixed

- The *Vanilla* panel look: the Water tab no longer runs off the bottom of the screen. 1.1.0's new
  rows made it taller than the screen, and its last rows (the city reflection's) were out of reach.
  A tab taller than the screen now scrolls, with the mouse wheel or the bar at its side; every other
  tab, and the other looks, are as they were.

## 1.1.0 — 2026-10-21

### New

- **Procedural Objects' buildings get the glass.** A building converted with Procedural Objects
  keeps its asset's specular map, and now gets the glass wherever Procedural Objects draws it,
  reshaped, stacked or turned. It takes its asset's own glass from the building window and its
  rooms, and other glass mirrors it as a building around. The building picker (Alt+G) finds a
  converted building by its shape, so it is picked like any other. A converted building given a
  texture or text of its own keeps the game's glass. Nothing of Procedural Objects is changed.
- **LODs show the model's glass** (the Glass tab, on by default): a building whose low-detail
  model (what the game draws from the middle distance on) has no glass, or has it in other places,
  no longer loses its glass and its rooms as you zoom out. Once a city, every LOD in it is
  compared with its full model at the same points of the building, whatever the layout of its
  textures; where the two disagree, the model's glass is carried onto the LOD by the building's
  shape, so it shows the glass the building has up close. An LOD whose glass agrees with its
  model's keeps its own, and one shaped too unlike its model to compare is left as it is. The
  building window (Alt+G) has "Glass on its LOD" -- Automatic, the model's, or the LOD's own --
  and its status line says what the asset's LOD shows. The log lists what was found.
- **Dynamic waves** (on by default): the sea follows the game's weather. On a dry day it is the sea
  you know, the Wind speed's default of 7 m/s. When it rains the wind rises, up to 15 m/s in the
  heaviest rain and storms, and the waves grow longer and rougher. They take a few game hours to
  build up and longer to calm down after, as a real sea does, and they hold still while the game is
  paused. Volumetric Weather's storms are the game's own rain, so they raise the sea too; it is not
  needed. Turn it off to set the wind yourself, as before. Wind speed is greyed out while it is on.
- **Moon glitter**: the moon's road of light on the water at night has its own strength. Until now
  the Sun glitter row set both.
- **Sun glitter size** and **Moon glitter size**: how wide each glitter spreads. A smaller glitter
  is narrower, not dimmer.
- **Folding headings** in the in-game panel: every tab's settings are grouped under headings, and a
  click on a heading folds its settings away or opens them again. Each tab starts with its first
  heading open (the glass, the reflections, the panes, the rooms, the water's color); the panel
  remembers which are open, in a new line of `RealGlass.xml`, `PanelOpenGroups`. The *Vanilla*
  look keeps plain headings, as the game's own panels have.
- **Tooltips** on the settings whose names leave a question: what 100% is, what turning one off
  gives back, what a switch really changes. Hover over a setting's name or its control. The rest
  have none: their names say it.

### Changed

- The panel's settings are in a new order under their headings. The Reflections tab has three
  (the reflections, the city in the glass, the sky in the glass), the Panes tab two, the Interiors
  tab two (curtains are with the rooms now), and the Water tab two new ones: the water's color, and
  the sun and moon glitter. No setting changes, and `RealGlass.xml` keeps every name.
- *Water controls* is now *Realistic water*, like *Realistic glass*.
- The Glass tab's status line shows only when it has something to say (the glass off, paused or
  stopped); it no longer leaves an empty line at the top.
- The glitter is half as wide by default (both sizes 50%): at 1.0.0's width the sun's reflection
  covered a third of the screen from a city camera. Existing settings files get the new sizes
  too, and Moon glitter starts at 35% whatever Sun glitter is set to.

### Fixed

- The rooms behind the windows no longer fade into a flat glow when a building turns its lights
  on at night. The game lights a building's windows all at once, and wherever a room covered only
  part of a window (a frame painted on the glass, a window only partly glass), the lit window shone
  through it. The room now fills the whole window, and the painted frames stay dark, as painted.

## 1.0.0 — 2026-10-20

The first Workshop release: realistic glass on every building, at every distance, and realistic
water. Published on the Steam Workshop as item 3823419476.

### New

- **The glass**: drawn over each building's own glass -- what the asset's author painted shiny,
  and optionally the windows it lights up at night -- near, middle-distance and far.
- **Kinds of glass**: reflective blue (the default), silver reflective, clear low-E, aqua, ocean
  and sky blue, green, bronze, grey and gold reflective, with real products' tints, reflectance and
  transmittance; or a colour of your own, and as much of the building's own painted colour as you
  like. A tinted glass mirrors the city and the sky in its own tint, as a coated lite does.
- **Reflections**: the city on screen -- the finished picture, with its haze, clouds, lamps and
  the other glass -- the buildings around where the screen cannot show them (a map of the city's
  roof heights, walked by every ray that misses the screen: the towers across a street, not open
  sky), the live sky with Volumetric Weather's clouds in it, its distant clouds included (or the
  game's own sky picture), the sun's glint, steady far away and hidden where the clouds cover the
  sun; below the horizon, the screen mirrored at its edge. More reflective edge-on, and silvery
  there as coated glass is. At sunrise and sunset the mirrored sky dims as a clear sky's light
  does in the sun's last ten degrees. The screen is traced over a pyramid of its depths, so
  reflections reach across a 4K screen.
- **Panes** that bend the reflections: cushioned, tilted, rippled, bulging with the weather; up
  close, each pane is one of the windows the building painted.
- **Painted detail kept**: a full-glass tower's painted floors, slab bands and frames show through
  where they stand out; the glass between keeps its reflections.
- **Behind the glass**: by day the dark inside of a building, through the glass's colour and
  clarity; by night each window exactly as the game and its asset light it -- the glass only adds
  the reflections.
- **Rooms behind the windows** (the Interiors tab, on by default): at night every window shows a
  real room, chosen by what the building is, with its own lamp and curtain; how many are lit
  follows the hour and changes slowly through the night; room depth, brightness, coloured lights
  and curtains are sliders. An asset can be left out with the building picker.
- **Glass per asset**: Alt+G (rebindable) turns a building picker on; click a building, and a
  window of its own gives its asset -- every copy of it, in every city -- its own strength, painted
  detail, kind of glass and say on what counts as glass. Kept in RealGlassAssets.xml, never in a
  save.
- **Water** (its own tab, on by default, experimental): the map's water in your own colour or the
  theme's, the sky reflected as real water reflects it (worked out per ripple), the city mirrored
  in it by day and by night, moving ripples in place of the game's frozen ones -- by default ocean
  waves raised by the wind you set, with their choppiness, running with the game's own wind, and
  the sun's glitter (the moon's at night) on them -- and foam along the shores, on fast water and
  on wave fronts.
- **Quality in one place**: every costly part of the glass (the walk over the city, the searches
  for painted panes, the depth pyramid) takes its budget from one table of four tiers, ready for
  a quality choice; the mod runs the highest.
- **The panel on F8** (or its Unified UI button), five tabs, every setting of the mod. (F7, the
  first choice, is the game's own debug-output key.) Its look, the building window's and the
  button's is picked on the options page: Frosted glass (the default, over a blurred copy of the
  city), See-through, Blue, or Vanilla, the game's own. In the new looks the tabs are icons, every
  tab is one height with the settings scrolling inside it, choices are a row of buttons (a list
  when the names don't fit), and every setting has a reset icon at the end of its row.
- **In the editors**: the Asset Editor (the edited building, its sub-meshes and, zoomed out, its
  LOD) and the Scenario Editor as well as cities. The glass pauses while the Snapshot tool is in
  use, so an asset's thumbnails and snapshots show it as the game draws it.
- **RealGlass.xml**: every setting in a file you can read and edit while the game runs.
