# Genesis128: CWC map generator for up to 128 players

This is a rewrite of the Genesis Project / CWC Map Generator 1.0 (Apache 2.0, see LICENSE.txt and NOTICE.txt). It generates
Cold War Crisis skirmish maps for any number of players, 128 by default. It keeps the original libraries
(`CkMp.Data` map reader/writer, terrain, textures, templates) and replaces the parts that only worked for 2–4 players.

## Build and run

```
dotnet build -c Release
bin\Release\net8.0-windows\Genesis128.exe --players 128 --seed 1337 --name "CWC 128 Grid" --out ..\output
```

| option | default | meaning |
|---|---|---|
| `--players` | 128 | number of start positions |
| `--cell` | 76 | cell size in tiles (one base per cell) |
| `--base-radius` | 24 | flat base plateau radius in tiles |
| `--border` | 30 | unplayable border in tiles |
| `--scenery` | Woodland | Woodland, Fall, Highlands or Snow (`Sceneries\*.xml`) |
| `--cliffs`, `--trees` | Medium | Low, Medium or High |
| `--max-trees` | 15000 | upper limit for trees |
| `--time` | Afternoon | Morning, Afternoon, Evening or Night |
| `--tracked-resources` | 24 | flags and fuel depots per type that the CWC AI scripts manage by name |
| `--seed` | random | map id |

Other commands: `Genesis128 verify <file.map>` re-reads a map and checks every AI name, and `Genesis128 dump <file.map|.scb>` prints the scripts.

To install a map, copy the generated folder (for example `CWC 128 Grid\` containing `.map`, `.tga` and `map.ini`) to
`%USERPROFILE%\Documents\Command and Conquer Generals Zero Hour Data\Maps\`.

## Layout

The original generator placed all players on a ring at least 4 base radii apart, so it cannot fit more than a few
bases. This version uses a grid:

* For 128 players there are 12 × 11 cells of 76 tiles. That gives a 972 × 896 tile map (912 × 836 playable), and the 4 corner cells are left empty.
* Each player has a flattened base (radius 24) in the center of its cell. Start positions are numbered row by row.
* **Streets** run along every cell boundary and around the map edge. Each base has **four access lanes** (N/E/S/W)
  that connect it to those streets. Mountains, rocks and trees are never placed on streets or lanes, so ground
  units can always get from every base to every other base.
* **Roads** follow the inner streets and are joined at the crossings. The roads that come with the resource templates are connected to the nearest street.
* **Resources:**
  * Every player gets the same small flag and small fuel depot, in the same position within its cell, so all starts are equal.
  * Every second inner crossing has a large contested flag or fuel depot, at the same distance from four bases.
  * The empty corner cells hold villages.
* **Validation:** a flood fill from base 1 checks that every start position, every AI path waypoint and every
  resource can be reached, using the same impassability flags the engine uses. If a mountain blocks one, a ramp is
  cut through it.

## AI data

The names below are resolved by the engine for each start position N. They come from `AISkirmishPlayer.cpp`,
`ScriptEngine::getQualifiedTriggerAreaByName` and `ScriptActions::doSkirmishFollowApproachPath` in the TSH
GeneralsGameCode source. All of them are generated for every N:

* `Player_N_Start`
* `InnerPerimeterN` and `OuterPerimeterN`. The outer perimeter stops at the cell edge, so neighbours' perimeters don't overlap.
* `CenterN`, `FlankN`, `BackdoorN`. Each path runs from the street, along the access lane, to the edge of the base
  plateau. Center is the lane that faces the map center; Flank and Backdoor are the two lanes perpendicular to it.
  An attacking team takes the closest waypoint of its target's path and follows the path into that base. Base
  defenses are built toward the same paths. The paths stop at the plateau edge rather than the base center because
  the AI uses the direction from its base center to the nearest path waypoint.
* `SkirmishWorld` and `MapCenter`
* `CombatZone`: the whole map minus all outer perimeters. Because the engine uses an even-odd point-in-polygon test, the perimeters are cut out as holes.
* `flagNN`/`fuelNN` with their `_wp` and `_area` objects, for the tracked resources

### CWC script adaptation

The CWC skirmish scripts were written for four start positions. They use `START_POSITION_IS 1..4`, `Player_1..4_Start`,
`player1..4_patrolX`, `flag01..10` and position-specific marker object types. Every AI player gets its own full copy
of its faction's scripts (`Player.cpp`, `duplicateAndQualify`), so copying the scripts 128 times was not an option.
`GridScriptAdapter` makes these changes instead:

* **Tactical classes.** Every start position is assigned one of the 4 original positions, in a 2×2 pattern, so no two
  neighbours (including diagonal ones) share a class. `START_POSITION_IS(k)` becomes a check of the flag `G128 Slot k`.
  A small init group sets that flag once at game start. Flags are qualified per player by the engine.
* **`Player_k_Start` in actions** (rebuilding the command center, AI sensors, E-3): these go through dispatcher
  subroutines, so each AI uses its own real `Player_N_Start`.
* **Patrols:** `playerK_patrolJ` becomes the shared labels `G128_patrol1` and `G128_patrol2`. The engine starts
  following at the waypoint closest to the team, which is always on the team's own loop around its base.
* **Convoys:** CWC spawns supply convoys at `convoy_marker_1`, which the original generator never created. Each player now has `G128_convoy_N` at the nearest map edge.
* **Resources:** the flag and fuel script groups are extended from 10 to `--tracked-resources`. Contested and
  village resources are tracked first. Untracked resources can still be captured but are not managed by name.

The scripts go from 1.9 MB to 3.2 MB per faction.

## Engine requirements (beyond "128 players")

* **`ScriptEngine.h`: `MAX_COUNTERS = 256`, `MAX_FLAGS = 256`.** These arrays are global, and every AI player gets
  its own qualified copies: about 120 counters and 50 flags per AI with the CWC scripts. If the arrays overflow,
  `allocateCounter`/`allocateFlag` return index 0, every extra counter and flag shares that slot, and the AI
  behaves erratically. This happens with any CWC map once there are more than 2 AIs. For 128 AIs, use at least
  16384 counters and 8192 flags.
* Script memory and CPU scale with the number of AI players: 128 copies of ~3 MB of scripts.
* The usual player and slot limits (`MAX_SLOTS`, `MAX_PLAYER_COUNT`, lobby, `MapCache` start-position counting) are
  assumed to be patched already.

## Known compromises

* Players of the same tactical class are at least 2 cells apart, but they share the CWC position-specific marker
  types and named sensors (for example `U.Gene. Infantry 01 Sensor`). The original scripts already share named
  sensors between all players for other groups. Duplicate names are not registered by the engine, so in a rare
  collision a team uses another player's sensor.
* Only `--tracked-resources` flags and fuel depots per type get CWC resource-management scripts.
* None of this has been tested in-game. The map is written by the original, proven `CkMp.Data.Writer` and has been
  verified by reading it back, but run a test game before relying on it.
