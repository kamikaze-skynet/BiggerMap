# BiggerMap (experimental)

A bigger arena in every realm.

When a realm loads, its layout spreads out from the centre:

- The border ring moves out. Copies of its pieces fill the gaps that opens up.
- Rocks, trees, props and the scenery behind the border move out. They keep their size; only their distance from the centre changes.
- Objective and artifact spawn points move out too, so objectives end up further apart.
- The ground itself (the terrain's hills) is stretched outward to match, and the invisible floor is widened. Grass and flowers get extra copies so the bigger ground doesn't look bare.
- Enemy pathing (the navigation mesh) is rebuilt for the new layout, so enemies can chase you everywhere.

**Everyone needs the mod.** The host's size is used for everyone: the host sends it to players every second, lobby included. If the host doesn't have the mod, the map stays normal for everyone.

## Config (`girthy_mods.BiggerMap.cfg`)

| Setting | Default | What it does |
|---|---|---|
| `Map.Enabled` | `true` | Turns the bigger arena on or off. |
| `Map.Scale` | `1.5` | How much wider the arena is (1–1.8). 1.5 is about 2.25× the area. |
| `Map.FillBorderGaps` | `true` | Adds border pieces so the bigger ring is closed. |
| `Map.FillGrass` | `true` | Adds extra grass and flowers. |
| `Map.MaxGrassCopies` | `2` | The most extra copies of each grass field. |
| `Map.InvisibleEdge` | `true` | Adds an invisible wall just inside the moved border, so nobody slips out through gaps. |
| `Map.RebuildNavigation` | `true` | Rebuilds enemy pathing. Turning it off leaves enemies stuck in the original area. |

## Known limits (please report anything odd)

- A few layouts had their rocks merged into one mesh when the game was built. The mod swaps in a copy of the same rock from another layout; if it can't find one, that rock becomes invisible but stays solid (an invisible wall). The log reports how many rocks it swapped and how many it couldn't.
- Rebuilding enemy pathing can cause a short hitch while the realm loads.
- Ground textures, roads and rivers don't stretch, so the outer area looks plainer than the middle.
- The log lists what was changed in each realm. Look for `BiggerMap` lines in `BepInEx/LogOutput.log`.
