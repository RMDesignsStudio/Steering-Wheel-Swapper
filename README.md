# Steering Wheel Swapper (F1 25, 2026 season)

Puts any 2026 or 2025 car's steering wheel — paddles, dials and the matching driver hand animation —
into a SERPs template car, and writes a **new** `.serps`. The template you pick is never modified.

Download: the latest `Steering Wheel Swapper vX.Y.Z.zip` from [Releases](../../releases) (single exe, no install, no .NET needed).

Part of **RM Designs Studio**. Not affiliated with or endorsed by EA, Codemasters, Formula 1 or any team; no game files are included. You need your own copy of F1 25 and SERPs templates.

## Use

1. **F1 25 game folder** – auto-detected from Steam; *Browse…* if not.
2. **SERPs template** – *Browse…* or drop a `.serps` onto the window. The tool lists the car entries
   it can change: the team's own chassis, the FOM chassis, MyTeam liveries, and swapped chassis.
3. Tick the cars to change, pick **Steering wheel from** (the 12 2026 cars, or the 10 2025 cars), **Build**.
   **Preview** does the same build without saving anything and opens a 3D view of the new wheel in the browser.
4. Use the new `.serps` in SERPs Studio / the Mod Builder like any template.

The suggested file name ends in the wheel, e.g. `2026 Aston Martin - SERPs Template v1.1 - 26 Ferrari Wheel.serps`
(`25 ...` for a 2025 wheel). Inside the .serps, each livery option folder whose cars all got the wheel is named
the same way (`02--01--Livery - FOM Chassis - 26 Ferrari Wheel`), so SERPs shows which wheel each option has; a
folder that also holds a car that did not get it (e.g. MyTeam's two paints) keeps its name. Re-swapping replaces
the ending instead of adding another.

## What it does (per car entry)

- Resolves the car's real model template-wide (game files, then the template's ERPs, the edited entry
  last), so swapped chassis (ErpProbe swaps, Simplified Swapper outputs) are kept. In Team Simplified templates each
  chassis option has its own folder (e.g. Williams' Release / Post-Patch / FOM / MCL40) and only one is installed, so
  an option never takes another option's model; templates without option folders use all their ERPs.
- Cars are listed under the template's own option names ("Release Default Chassis", "FOM Chassis", ...); "swapped
  chassis" means the car is on another model or another car's geometry. "Already has that steering wheel" compares
  the actual wheel data, so e.g. an older copy of the team's own chassis still gets the new wheel.
- Clones that model under fresh private names, so no other car changes.
- Copies the donor's whole steering cluster (wheel, paddle/dial pivots, hand and button IK targets)
  while keeping the wheel on the target car's own steering column (where the donor team placed the wheel relative
  to its column; each cockpit is built around its own wheel, so this is what fits in game).
- Appends the donor wheel geometry (all detail levels), rebinds it to the target's bones, hides the original wheel.
  Where a detail level of the target has no slot for a donor body piece, the wheel body and carbon pieces fall back to
  each other's slot (vertex layout converted), so no detail level loses its wheel.
- Points the car's in-car driver animation at the donor team's clips, so hands, paddles and buttons line up.
- Embeds the donor's full-resolution steering wheel textures into the textures the car's wheel material uses.
- Fixes known template quirks, e.g. the MyTeam numbers ERP binding the wheel to the Alpine material.
- **2025 wheels** come from the game's `f1_2025_vehicle_package` (no 2025 Cadillac / Audi; no separate 2025 FOM car).
  Their 2025 driver clips (about 24 per team, ~100 KB) are copied into the entry, so the hands work while the game
  runs the 2026 season. CLI ids: `ferrari@2025`, `force_india@2025`, ... Wheels whose shape changed for 2026:
  Ferrari, Aston Martin, Haas, Mercedes; the others mainly differ in artwork. A donor piece the target car has no slot
  for (e.g. the 2025 Haas wheel's small 'detail' piece) is left out, with a warning.
- Validates every changed ERP (references + hashes), then verifies the finished car independently
  (see below). Nothing is written if any check fails.

## Build verification (runs on every build)

`BuildVerifier` re-reads the output and compares it with the untouched template, the stock donor car and the game:

- hashes of every new/changed resource; the private model loads on its own; every private reference resolves
- car-model layers point at the private model and none at the original
- steer_master is unchanged (the wheel stays on this car's column); every other steering-cluster node matches the
  donor (driver position / gear stick keep the donor's world placement); `?model` and `?render` node tables agree
- every submesh stays inside its index and vertex buffers (index count a multiple of 3)
- every new wheel vertex is bound to a steering-cluster bone and is a donor wheel vertex at the donor's position
  relative to its bone; the donor's whole LOD0 wheel body is present
- no triangle of the original wheel is still visible, at any detail level
- each detail level the donor has is covered (warning if not)
- the driver animation uses the donor's clips and every clip exists in the game
- each wheel texture is fully embedded at the donor's resolution and holds the donor's pixels

## Build report

Optional: tick **Save a report** (off by default; the app remembers the choice) and the build also writes
`<output> - report.html` next to the new .serps and shows **Open report**. One
self-contained page (works offline, ~5 MB per car) with, for every car: the wheel before and after (front and side
views drawn from the real game geometry, coloured by part), the four wheel textures before and after, the donor
geometry added per detail level, warnings and notes, the verification result, and the full log. CLI: add `--report`.
Each car also gets the wheel-only 3D preview (see below).
The page's colours are CSS variables at the top of `BuildReport.cs` (same palette as the app).

## Wheel pictures

Hovering a **Steering wheel from** option shows that car's stock wheel at a 3/4 angle. `WheelThumbnail.cs` draws it
with a small software rasteriser straight from the player's own game files (no game art ships in the app) and caches
it in `%LOCALAPPDATA%\SteeringWheelSwapper\wheels` (re-rendered when the team's game file changes); the app renders
all of them in the background at start-up (~0.15 s each). CLI: `wheelswap thumb <donor|all> <out dir>`.

## 3D preview

`WheelPreview.cs` + `PreviewScene.cs` write a self-contained WebGL2 page (no libraries, works offline) from the build
in memory. **Steering wheel** shows the new wheel (After) or the car's own wheel (Before) in steering-wheel space
(Driver's view, Front, Side, 3/4). **Car** shows the whole car around it (Cockpit, Front, Side, 3/4, Top): the built
model's LOD0 body with every vertex placed by its bone, plus the shared tyre model from `teams/common.erp` on the four
`wheel_*` nodes.

Materials come from the car's own material binding (`.emb`: slot name -> material), so the preview shows the
template's real livery, decals and wheel. `EgoMaterial.cs` reads the `.material` layout (shader, float parameters,
samplers). UVs: uv0 is the slot's own UV (paint sheets wrap, so it can run outside 0..1), the second half of the same
stream is the decal sheet (no offset); floating decals (some cars' numbers) also read the decal sheet UV.
Shader kinds: paint (albedo; paint_s R = lacquer mask, G = roughness, B = metallic; paint_c = the metallic areas'
reflection colour; metallic flakes from `me_flakesMap` x `me_flakesStrength` / `me_flakesTile` (microscopic: many repeats, so they average
out with distance), tinted by
`me_flakesColourInfluence`; lacquer roughness `me_lacquerRoughness`; decals with their own decal_s surface), carbon (`cf_pattern` tiled by
`me_carbonTile` and offset by `me_uOffset`/`me_vOffset`: G = roughness, B = metallic, its normal map, decals, lacquer), generic / wheel / steering (diffuse, normal, surface R = baked occlusion, G = roughness, B = metallic, logo on
uv1; `me_cubeMapScale` scales the reflections), glass,
screen, mirror, lights, tyres (rubber with a grazing sheen; tread sheets stack strips as tall as the sheet is wide -
8 on the colour sheet, 4 on the normal map - and a new tyre uses the first strip of each; the sidewall's moulded texture
from the colour sheet's alpha; sidewall lettering = slice 0 of `sidewall_logo_surface`, R x alpha, tinted by
`me_logoColour`, wrapping like the sidewall). Textures are capped by area, so tall strip sheets keep their width. Shadow and rain-bead slots are skipped.
Lookup order: the entry, the template's other ERPs for that option, the model's game files, `common.erp`.

Textures stay in their GPU block format (BC1/3/4/5/7; the browser uploads them as they are), at most 2048 for paint
and decal sheets, 1024 for other colour maps and 512 for normal / surface maps. Every texture and every piece of
geometry is stored once (by content), and all binary data is zlib + base64, so a page is about 10-25 MB per car.
Lighting: GGX shading with a lacquer coat, a key light with a 4096 shadow map (5x5 PCF; the car shadows itself and
the floor, re-rendered only when the shown car / side / mode changes) and a fill light. Car mode uses a dark studio
(overhead softbox, low strip lights) like the game's car viewer; the wheel view uses a sky. A **Studio** toggle picks the
colours from the RM Designs palette: Purple (#1C0040 with a #4300AD glow, default), Grey (#111111, neutral) or White
(#F2F2F2); the choice is remembered in the browser. It is an approximation of the in-game look (no in-game shaders). Numbers are floating decals: every `vehicle_floating_decal*` variant (incl. `_surface_map` and
`_driver_number`) draws its albedo through the decal UV. A **Controls** tab in the bar lists the mouse controls;
double-click goes back to the selected view. The app's **Preview** writes it
to `%TEMP%\SteeringWheelSwapper\`; CLI: `--preview FILE.html`. The report embeds the wheel-only view.

## Re-swapping, damaged files and modded games

- **Re-swapping an output** works: a .serps written by this tool is recognised (its cars show "wheel already
  swapped"), the earlier wheel's private model is removed first, and the result is identical to swapping the
  original template directly — files don't grow with each re-swap. Choosing the wheel the car already has is skipped.
  Choosing the car's own team wheel again puts the stock wheel back (the template's original wheel *textures* can't be
  recovered from a swapped file — use the original template for those).
- **Damaged templates**: a file that isn't a readable .serps (zip) is rejected with a clear message; a damaged or
  truncated car ERP is listed as "not supported" instead of disappearing, and the other cars still work.
- **Saving** goes to a temporary file that replaces the target only when complete; nothing is left behind on failure.
  An output that is open in another program gives a clear message and the old file is kept.
- **Game folder**: missing / wrong folders, missing or damaged car files and missing driver animations stop the build
  with a message saying which file. Mod managers keep the stock file as `X.erp.bak`: if a car's model or steering
  wheel data differs from that backup, the log warns that the wheel is taken from the modded files (handling-only
  mods, e.g. `.vtf` performance changes, don't count).

## Known limits

- The donor wheel's driver number is left out on cars without a separate wheel-number slot (the log says so).
- MyTeam uses the game's generic FOM driver animation set, so any other car using it also gets the new hands.
- Only 2026-package cars are supported. Garage monitor height (FOM chassis) is a separate issue.
- Proven in-game: Aston Martin wheel -> FOM chassis / MyTeam. Other pairs pass every structural check
  but have not all been driven.

## Build from source

```
dotnet publish src/WheelSwap.App -c Release -o dist -p:DebugType=none
```

- `src/WheelSwap.Core` – swap engine (ERP, mesh, node graph, textures, animset, template handling)
- `src/WheelSwap.Cli` – `wheelswap inspect|build <template.serps> --donor <token> (--out <file> | --no-write) [--entries a,b] [--report] [--preview FILE.html]`
- `src/WheelSwap.App` – WPF front end
- `tests/WheelSwap.Tests` – xUnit v3 suite (see Tests); `tests/gui_smoke.ps1` – drives the exe through UI Automation;
  `tests/matrix.ps1` – older quick dry-run matrix through the CLI

## Tests

```
dotnet build tests/WheelSwap.Tests -c Release
tests\WheelSwap.Tests\bin\Release\net10.0\WheelSwap.Tests.exe                    # everything
tests\WheelSwap.Tests\bin\Release\net10.0\WheelSwap.Tests.exe -class WheelSwap.Tests.GoldenTests -class WheelSwap.Tests.VerifierTests   # ~15 s
```

- **GoldenTests** – Aston wheel -> Aston FOM chassis and -> MyTeam must match the v6 build confirmed in-game
  (byte-identical apart from float rounding noise and a JSON-identical animation set).
- **VerifierTests** – a good build passes; each kind of damage (wrong bone, moved vertex, original wheel visible,
  submesh out of bounds, steer_master moved, node tables disagreeing, bad hash, layer not repointed, animation
  not retargeted, texture not swapped) is caught.
- **ReSwapTests** – outputs are recognised; a re-swap equals a direct swap byte for byte; same donor is skipped;
  back to the car's own wheel; chains of re-swaps don't grow.
- **DamagedTemplateTests** – not-a-zip / empty / truncated files, damaged and truncated car ERPs, a layer pointing at
  a missing model, a template without cars, failed and blocked writes, output = template.
- **GameFolderTests** – missing / wrong game folder, missing donor or target car, missing driver animations, damaged
  donor file, a modded donor (warns), handling-only mods (no warning). Uses small fake game folders in temp.
- **ReportTests** – the HTML report is written next to the output, has before/after pictures with real geometry
  for every car, valid PNG texture previews, a 3D section, and loads nothing from the internet.
- **PreviewTests** – wheel pictures are drawn and cached; the 3D page holds the wheel before / after and the whole car with four tyres: valid geometry
  and indices, materials, block textures with whole mip levels, the car's paint; the report's section is wheel-only.
- **Season2025Tests** – 2025 donors: wheel, textures and 2025 clips go in, clips are carried in the entry (the
  verifier fails if not), re-swaps between seasons stay clean.
- **LodCoverageTests** – the LOD3 slot fallback (Mercedes wheel on Red Bull, Red Bull wheel on Mercedes).
- **MatrixTests** – all 22 donors (2026 + 2025) onto every car entry of every stock 2026 template, the `Swapped Templates`
  folder and the user's own chassis-swap builds. Each build is verified, written to a temp .serps, re-opened, and
  re-read by `second_reader.py`, an independent Python ERP reader (needs `py -3` with `zstandard`; skipped otherwise).
  No detail level may be left without donor wheel geometry. About an hour (462 builds).

Inputs are only read. Paths default to this PC's locations; override with `WHEELSWAP_GAME`, `WHEELSWAP_TEMPLATES`,
`WHEELSWAP_SWAPPED`, `WHEELSWAP_GOLDEN`. Tests skip when an input is missing.

## Look (RM Designs)

The app and the report use the RM Designs branding: palette #111111 / #1C0040 / #4300AD / #F2F2F2, Presicav wide caps
and Roboto Mono subtext (both embedded in the exe; licences allow it), the RM wordmark band and icon, team colours on
the donor chips (FOM uses the My Team yellow).

- Theme: `src/WheelSwap.App/App.xaml` (colours, fonts, controls); layout: `MainWindow.xaml`; letter-spaced caps and
  team colours: `Ui.cs`.
- `branding/` (the RM Designs source assets) is not included in this copy. `design/make_logo_xaml.py` regenerates `Brand.xaml` (wordmark +
  icon as WPF drawings) from the SVGs; `design/make_icon.ps1` renders `app.ico`; `design/preview.html` (from
  `make_preview.py`) is the approved style preview.
- Report styling: the CSS constant in `src/WheelSwap.Core/BuildReport.cs`.

`external/EgoEngineLibrary` is a vendored copy of Petar Tasev's MIT-licensed library (see its LICENSE),
with its ImageSharp/glTF parts removed.

## Licence

Copyright (C) 2026 RM Designs Studio. Licensed under the GNU General Public License v3.0; see [LICENSE](LICENSE).
If you share a modified version, share its source under the same licence.

Third-party parts:

- `external/EgoEngineLibrary`: Petar Tasev's EgoEngineLibrary (MIT, see its LICENSE), trimmed.
- `external/EgoEngineLibrary/libzstd.dll`: Zstandard by Meta (BSD licence).
- `src/WheelSwap.App/Fonts/RobotoMono-*.ttf`: Roboto Mono by Google, under its open font licence
  (https://fonts.google.com/specimen/Roboto+Mono/license).

## Notes on this repository

Not included: build output, the author's test files, and the Presicav font files (the published exe has the font
built in; without the files a local build uses a fallback font for headings). Build with the .NET 10 SDK (see Build
from source). Tests need the game and SERPs templates; point them at yours with the WHEELSWAP_* environment variables.
