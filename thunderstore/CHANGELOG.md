## 0.9.3
- Players and enemies can no longer slip out of the bigger arena. An invisible wall runs just inside the moved border, and enemy pathing only covers the area inside it.
- Border gaps are filled more reliably on layouts where a few border pieces sit further in.

## 0.9.2
- Fixed a crash when a realm loads (0.9.1). The ground is now stretched by widening the terrain itself (hills and ground textures together), which uses the game engine's normal settings.

## 0.9.1
- Fixed walking under the ground: the realm's ground (terrain hills) is now stretched outward along with the rest of the map, so the hills sit where the moved border and scenery are instead of rising through the bigger arena.

## 0.9.0
- First (experimental) release: spreads each realm's layout out by a configurable scale, fills border gaps, thickens grass, and rebuilds enemy navigation. The host's size is synced to every player.
