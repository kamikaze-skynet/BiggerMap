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
