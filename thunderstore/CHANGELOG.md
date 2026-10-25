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
