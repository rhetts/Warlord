# Warlord — Architecture & Planning

A Shogun: Total War–style game with a **3D real-time battle mode** and a **2D turn-based strategic campaign map**.

## Engine & language

**Godot 4** (MIT-licensed, native 2D + 3D in one project — the .NET/C# build). Revisit Unity + ECS only if we commit to 5,000+ animated soldiers on screen simultaneously.

**Language: C#.** Chosen for the heavy simulation load (two AIs, battle resolution, pathfinding), first-class SQLite via `Microsoft.Data.Sqlite`, static typing for a large growing codebase, and existing team fluency. Small GDScript files are acceptable for trivial scene/UI glue, but core simulation, `SaveStore`, and AI stay in C#.

## Core principle

The **simulation (game state) is separate from the two presentation modes.** Neither the 3D battle nor the 2D map owns the game state. They communicate only through a roster-in / result-out contract, which also enables statistical auto-resolve using the same interface.

```
┌─────────────────────────────────────────────┐
│         Game State Model (pure data)          │
│  factions, provinces, armies, units, economy, │
│  diplomacy, tech — serializable, no rendering  │
└───────────────┬───────────────────┬───────────┘
                │                   │
      ┌─────────▼────────┐  ┌───────▼──────────┐
      │  Campaign Mode   │  │   Battle Mode    │
      │  (2D map scene)  │  │  (3D battle scene)│
      │  turn-based      │  │  real-time        │
      └─────────┬────────┘  └───────┬──────────┘
                │                   │
                └────────┬──────────┘
                    Battle Resolver
        (takes two armies → runs 3D battle →
         returns casualties/result to state)
```

## Mode connection flow

1. Campaign map (2D) is turn-based. Player moves armies, manages provinces.
2. When two armies meet, hand the participating **unit rosters** to the battle scene.
3. Battle scene (3D, real-time) loads terrain + spawns units from the roster, plays out, returns a **result object** (winner, survivor counts, per-unit casualties).
4. Result is applied back to shared game state; unload the 3D scene, return to the map.

The 3D battle never touches campaign logic; the 2D map never simulates combat.

## Recommended structure (Godot)

- **`GameState` (autoload singleton)** — authoritative model; save/load serializes just this.
- **`CampaignScene`** — 2D map, turn manager, strategic faction AI.
- **`BattleScene`** — 3D terrain, formations, real-time combat, tactical AI.
- **`BattleResolver`** — the bridge; also holds auto-resolve math.
- **Data-driven definitions** — units, buildings, factions in JSON/Godot Resources, not hardcoded.

## Project structure

Two C# projects. The key move: **`Warlord.Core` has zero Godot dependency** — it's a plain .NET class library, so the entire simulation is unit-testable without launching the engine.

```
C:\Work\Warlord\
├── Warlord.sln
├── docs\
│   └── ARCHITECTURE.md
├── Warlord.Core\                 # pure C# — NO Godot reference
│   ├── Warlord.Core.csproj
│   ├── State\                    # GameState, Faction, Province, Army, Unit (to_dict/from_dict)
│   ├── Rules\                    # BattleResolver + auto-resolve math, turn resolution
│   ├── AI\
│   │   ├── Strategic\            # campaign AI: economy, diplomacy, expansion
│   │   └── Tactical\             # battle AI: formations, flanking
│   ├── Data\                     # static definition loaders (unit/faction/building JSON)
│   └── Persistence\              # SaveStore, SQLite schema, migrations
├── Warlord.Game\                 # Godot project — references Warlord.Core
│   ├── Warlord.Game.csproj
│   ├── project.godot
│   ├── scenes\
│   │   ├── Campaign\             # 2D map scene + scripts
│   │   └── Battle\              # 3D battle scene + scripts
│   ├── autoload\                 # GameState singleton bridge
│   └── assets\                   # models, textures, audio
├── Warlord.Tests\                # xUnit — tests Warlord.Core directly
└── data\                         # authored JSON definitions (copied to res:// on build)
```

Dependency direction is one-way: `Warlord.Game → Warlord.Core`. Core never references Godot, so simulation, AI, and persistence can be tested and debugged as a normal .NET library. Godot scripts are thin: read/write `GameState`, drive scenes, delegate all decisions to Core.

## Hard problems (plan for these from day one)

1. **3D battle unit counts.** Use instanced rendering (`MultiMeshInstance3D`). A "unit" is a *formation* with one brain and shared animation, not N independent agents. Flow-field pathfinding for the group, not per-soldier A*.
2. **Two separate AIs.** Strategic AI (turn-based: economy, diplomacy, expansion) and tactical AI (real-time: formations, flanking) are different problems and different systems.

## Persistence

**Static game data** (unit/building/faction/tech definitions) → read-only JSON or Godot Resources under `res://data/`. Authored, not saved.

**Save games** → **SQLite**, one `.db` file per save slot. Chosen for atomic/crash-safe transactional writes, partial reads/writes as campaigns grow, and queryable state.

Rules:
- Only `GameState` is authoritative and persisted. Scenes rebuild from it on load; the 3D battle and 2D map are never saved.
- Every save carries a `schema_version` (in a `meta` table) so old saves can be migrated, not silently broken.
- Access goes through a `SaveStore` layer exposing `save(GameState)` / `load() -> GameState`, built on each state object's `to_dict()` / `from_dict()`. Game logic never sees SQL; the storage backend stays swappable.
- Wrap each save in a single transaction so a crash mid-save can't corrupt the slot.

Sketch schema:
```
meta(schema_version, turn, saved_at)
factions(id, name, treasury, ...)
provinces(id, name, owner_faction_id, ...)
armies(id, faction_id, province_id, x, y, ...)
units(id, army_id, type, strength, experience, ...)
diplomacy(faction_a, faction_b, stance, ...)
```

## Open decisions

- [ ] Max on-screen unit count target (drives rendering approach)
- [ ] Map representation: province tilemap vs. mesh regions
- [ ] Multiplayer? (affects state authority model)

## Status

- Project home: `C:\Work\Warlord\`
- Engine: Godot 4.4.1 .NET installed at `C:\Work\Godot\`
- Phase: vertical slice of the campaign map

Built so far:
- `Warlord.Core` — engine-free: `Faction`, `Province`, `Cell`, `RgbColor`, `CampaignMapState`, `SaveStore` (SQLite).
- `Warlord.Game` — Godot project; `IsoMap.cs` draws the map isometrically from Core state, colored by faction owner, with province borders + hover HUD.
- `Warlord.Tests` — xUnit, 12 tests over Core + SaveStore roundtrip, no Godot.
- Persistence working: map loads from `user://warlord.db`, seeded on first run. Schema versioned (`SchemaVersion = 1`).

Not yet done: interaction (click to change ownership), real TileMap + art, armies/turns, battle mode, AI.
