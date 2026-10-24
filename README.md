# BiggerMap

A [BepInEx](https://github.com/BepInEx/BepInEx) IL2CPP mod for **The Spell Brigade** that makes each realm's arena bigger. It is experimental.

See [`thunderstore/README.md`](thunderstore/README.md) for the player-facing description and config.

## How it works

- Each realm scene has `<Realm>Environment/WorldVariantManager` with three layout variants. `ActivateCorrectVariant` (called from `Awake`) turns one on, on every player's game. A postfix spreads that variant out:
  - `FloorCollider`: its box (or, when it has a visible mesh, its transform) is widened by the scale.
  - Containers are objects without a renderer or collider whose children are spread out. The mod walks down through them, and every placed object gets its XZ distance from the centre multiplied by the scale. A big mesh at the centre is stretched instead.
  - Rings (≥8 pieces, all ≥30 m out) get `ceil(scale) − 1` rotated copies. Dense fields with no colliders (≥100 pieces) get `round(scale²) − 1` copies. Only plain scenery is ever copied: no scripts, spawn points or network objects.
  - Static batching: renderers whose `MeshFilter.sharedMesh` is a "Combined Mesh" ignore their transform. They are hidden, and a child is added with the mesh and materials of the same object in an unbatched variant, matched by name and parent name.
- `TerrainStretch`: the terrain's `TerrainData.size` (x, z) is multiplied by the scale, which stretches its hills and textures, and the terrain is moved so the centre stays put. The original size is cached per terrain asset, because the asset stays in memory between loads. (Writing the height map through the `Internal_SetHeights_Injected` icall crashed in 0.9.1, because that icall wants the native object pointer.)
- `Wall`: the innermost ring group with colliders (the border) gives a centre and an inner radius (the 1/7 percentile of the pieces' distances, minus 2.5 m). 96 invisible `BoxCollider`s, 40 m tall, go on that radius, on the border colliders' layer. The navmesh build is then limited to a `Volume` just around that circle.
- On the next frame, `Physics.SyncTransforms()` runs and every `NavMeshSurface` is rebuilt with `BuildNavMesh()`. Surfaces set to `Volume` are enlarged first.
- `Sync` sends the host's settings every second as a Netcode named message. A client that hasn't heard from the host when the realm loads waits for it, and leaves the map alone if the host doesn't have the mod.
- The variant's `WorldVariantSpawnableArea` rectangles (clean-zone objectives) and any Cinemachine confiner bounds are scaled too. Confiners are scaled from their original size.
- Navigation is built from physics colliders (`useGeometry = PhysicsColliders`), because the floor is an invisible box.
