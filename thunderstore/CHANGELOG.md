## 0.9.14
- **Everything is kept on the floor, not just placed there.** Enemies, the fungi, statues, spell cores and pickups are now watched after they spawn too. Anything that ends up on top of a rock or past the edge (a knock-back, a physics bounce, a bad event spot) and stays there for about a second is put back on the nearest floor spot. Enemies are moved with their navigation, so they walk on normally.
- More things are recognised as "on the ground" (some enemies keep the parts the mod looks for on a child object, so they were skipped before).
- The log now says which BiggerMap the host runs, and warns if it's older than yours: the host's game decides where things spawn, so the host needs the latest version for these fixes to work.

## 0.9.13
- **Spell cores stuck on the rocks come back down.** The cores in "Secure the Unstable Spell Cores" roll on physics, and a hard push into a rock could leave one on top where nobody could reach it. A core that sits up on a rock (or past the edge) for a few seconds is now put back on the nearest floor spot.
- **The line of enemies isn't squeezed into a column any more.** Spots of the line that fall outside the edge are left out instead of being moved inside, which had packed the line's ends into a dense column along the edge.
- Spawns that land on a tall rock now find the floor below them (the search looked at the rock's height and could miss the floor).

## 0.9.12
- **Enemies, event items and pickups land on the arena floor.** Several of the game's events place enemies by distance alone (the mob, the converging circle, the line of dogs), so in the bigger map some came up on top of the border rocks or outside the edge. Everything that lives on the ground (enemies, the fungi, the statues, pickups) is now checked as it spawns: a spot outside the edge, or on a rock that has no way down to the floor, is moved to the nearest floor spot.

## 0.9.11
- **Fix: the host's game crashed when a line or mob event started** (the row of dogs, the mob). 0.9.9 hooked the game's "start event" code to size those events for the bigger map, and that hook couldn't pass one of the game's values through correctly, so the game read bad memory and closed. The events are now sized without that hook: the line is stretched where its enemy positions are worked out, and the spawn distances are set on the events' settings directly.

## 0.9.10
- Smoother: the extra objective time is now given when an objective starts, instead of searching the whole scene for objectives every second (that caused a small hitch each second).

## 0.9.9
- **Nothing spawns outside the map any more.** The ground outside the invisible edge (and the top of the edge) is no longer walkable, so enemies, scrolls and event spots can't be placed out there. Wave enemies and the game's own spots for objectives and pickups that would land outside are pulled back inside.
- **The line event fills the whole map again.** The row of enemies sweeping across the map (the dogs in Verdant Meadows) was sized for the original map; it's now as wide as the bigger one. The mob event starts the right distance out, too.
- **Timed objectives get more time** (herding the fungi, the statues, capture zones...), as everything in them is farther apart. New setting `Map.ObjectiveExtraTime` (1 = in step with the map size, 0 = off).

## 0.9.8
- When a search for a free spot near a player finds nothing (near the edge or against rocks), it's widened and nudged towards the middle instead of failing. That stops the remaining "No available candidates found around position in the grid" errors, and the thing lands near the player instead of anywhere on the map.

## 0.9.7
- **No more invisible walls inside the map.** After spreading, every solid obstacle is checked from above: if most of it has nothing visible there (a merged rock that couldn't be replaced, a collision-only blocker whose visible parts spread apart, a leftover blocker around the original smaller arena), it's switched off. The ground, hazards, objectives and other gameplay objects are never touched. If a realm looks wrong to the check (too many pieces would go), it leaves everything alone and says so in the log. New setting `Map.RemoveInvisibleBlockers` (on).

## 0.9.6
- **The invisible edge now sits at the moved border rocks.** It was measured before the game had caught up with the rocks' new positions, so it stood at the original, smaller border and cut the bigger map down. It's now measured at the rock face itself (rays against each rock, not their bounding boxes), so the rocks you see are where you stop, and you can't walk through them or the gaps between them.
- Fixed "No available candidates found around position in the grid" errors. The game's list of free spots is for the original layout, so the mod now asks it in original-map terms and moves the answer back out. Objectives, events and spawns also land across the whole bigger map now.

## 0.9.5
- **Everyone uses the host's settings.** While you're in someone's game, this mod reads the host's config instead of yours, from the lobby on. Your config file isn't changed, and your own values are back when you leave. If the host doesn't have the mod, it stays off for you in that game.

## 0.9.4
- The invisible edge now follows the border's real shape. It stands right against the inner face of the border rocks and only bridges the gaps; before, it was a circle at the border's closest point and cut off up to 20 m of playable ground.
- Picks the actual border to follow, so a ring of rocks or trees inside the arena can't be mistaken for it.
- Fixed 'No available candidates found around position in the grid' errors when players are in the outer part of the bigger map. The game's objective-placement grid doesn't reach that far, so the request is kept inside it.

## 0.9.3
- Players and enemies can no longer slip out of the bigger arena. An invisible wall runs just inside the moved border, and enemy pathing only covers the area inside it.
- Border gaps are filled more reliably on layouts where a few border pieces sit further in.

## 0.9.2
- Fixed a crash when a realm loads (0.9.1). The ground is now stretched by widening the terrain itself (hills and ground textures together), which uses the game engine's normal settings.

## 0.9.1
- Fixed walking under the ground: the realm's ground (terrain hills) is now stretched outward along with the rest of the map, so the hills sit where the moved border and scenery are instead of rising through the bigger arena.

## 0.9.0
- First (experimental) release: spreads each realm's layout out by a configurable scale, fills border gaps, thickens grass, and rebuilds enemy navigation. The host's size is synced to every player.
