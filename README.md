# BiggerMap

A [BepInEx](https://github.com/BepInEx/BepInEx) IL2CPP mod for **The Spell Brigade** that makes each realm's arena bigger. It is experimental.

See [`thunderstore/README.md`](thunderstore/README.md) for the player-facing description and config.

## How it works

- Each realm scene has `<Realm>Environment/WorldVariantManager` with three layout variants. `ActivateCorrectVariant` (called from `Awake`) turns one on, on every player's game. A postfix spreads that variant out:
  - `FloorCollider`: its box (or, when it has a visible mesh, its transform) is widened by the scale.
  - Containers are objects without a renderer or collider whose children are spread out. The mod walks down through them, and every placed object gets its XZ distance from the centre multiplied by the scale. A big mesh at the centre is stretched instead.
  - Rings (≥8 pieces, all ≥30 m out) get `ceil(scale) − 1` rotated copies. Dense fields with no colliders (≥100 pieces) get `round(scale²) − 1` copies. Only plain scenery is ever copied: no scripts, spawn points or network objects.
  - Static batching: renderers whose `MeshFilter.sharedMesh` is a "Combined Mesh" ignore their transform. They are hidden, and a child is added with the mesh and materials of the same object in an unbatched variant, matched by name and parent name.
- On the next frame, `Physics.SyncTransforms()` runs and every `NavMeshSurface` is rebuilt with `BuildNavMesh()`. Surfaces set to `Volume` are enlarged first.
- `Sync` sends the host's settings every 3 s as a Netcode named message. The variant is laid out as the realm loads, before the run starts.
