# HearthBelow

![](https://cdn.hexium.gg/upload/424/images/aac68dd55141d1eb9d924746.png)

Dig caves. Build underground. Bring the hearth below.

HearthBelow transforms Valheim’s terrain wherever you dig, allowing you to carve real tunnels, hollow out mountains, and
build permanent underground homes.

Digging remains grounded in Valheim’s progression: pickaxes carve terrain gradually, stronger tools reach greater
depths, creatures can follow you underground, and anything built without proper support can still collapse. Existing
worlds are supported, untouched terrain remains unchanged, and all cave edits persist through Valheim’s normal
world-saving and multiplayer systems.

> ## ⚠️ BACK UP YOUR WORLD ⚠️
> It does not modify vanilla save data or worldgen, but it
> changes how terrain looks and behaves in modified zones, and underground builds only make sense
> while the mod is installed (see **Removing the mod** below). **Make backups before using it.**

## What it does

---

- **Pickaxe digs caves.** Any pickaxe carves material out of the terrain instead of lowering it -
  dig sideways into a hillside, sink a shaft, hollow out a mountain. Digging is gradual like
  vanilla (each swing bites in the direction you're swinging) and drops stone like vanilla.
  Better pickaxes dig deeper: depth is capped by tool tier (antler/stone 8m, bronze 16m, iron
  48m, blackmetal 128m by default), so modded pickaxes fit in automatically. Digging at a
  tool's cap leaves a flat, even floor - handy for lining up builds.
- **Faithful terrain conversion.** The first carve in a 64×64m zone converts that zone's
  heightmap (including existing terrain edits) into a voxel volume and swaps rendering +
  collision over seamlessly. Untouched zones are unaffected, and vanilla terrain tools keep
  working on voxelized zones.
- **Live underground.** The voxel surface behaves like normal terrain: building, ground support,
  grass, biome detection, footsteps, and fires all work. The hoe works underground too - raise
  ground fills material back in, level/smooth shape the cave floor, paths/paving paint it. Deep
  enough down, a dungeon-style ambience fades in (configurable per player, or off). Log out in a
  cave and you log back in there; tombstones, drops, and loot stay down instead of popping
  topside.
- **Creatures follow you in.** The navmesh is built from the voxel terrain, so monsters path
  into your tunnels. Sealing the entrance with raised ground still works as a defense.
- **Multiplayer + persistence.** Works on existing worlds. Carves are tiny operations stored on
  the same networked object vanilla uses for terrain edits (the zone's TerrainComp ZDO), so they
  save with the world and sync through vanilla's own machinery. No custom save format, no extra
  files.

## Installation - required on server AND all clients

---

This is a terrain-consistency mod. Everyone in the world must have it, same version:

- **Dedicated server:** required. Enforces the version handshake (mismatched clients are kicked)
  and is the config authority via ServerSync.
- **All clients:** required. A player without the mod would see no caves and walk on phantom
  terrain above your tunnels.
- **Crossplay:** console players cannot install mods - run your server without crossplay.

Singleplayer / local hosting works out of the box.

## Console commands

---

- `hearthbelow carve [radius]` - carve at the crosshair (up to 100m).
- `hearthbelow fill [radius]` - fill material back in at the crosshair.
- `hearthbelow flatten [radius]` - level the floor at the crosshair and carve headroom above it.
- `hearthbelow smooth [radius]` - blend the floor toward the crosshair height with falloff.
- `hearthbelow restore` - remove **all** voxel edits in the zone you are standing in (including
  digs crossing in over its borders) and restore the vanilla heightmap. Careful: entombs
  anything built in that zone's caves.
- `hearthbelow info` - voxelization info for your current zone.

## Config (synced from server, except where noted)

---

| Setting                       | Default             | Description                                                                                                    |
|-------------------------------|---------------------|----------------------------------------------------------------------------------------------------------------|
| Voxel Digging                 | On                  | Pickaxe carves caves instead of lowering terrain                                                               |
| Dig Mode                      | Gradual             | Gradual = shallow vanilla-like bites per hit, Blast = full radius at once                                      |
| Dig Depth Per Hit             | 0.75                | How deep each hit bites into the surface (Gradual mode)                                                        |
| Carve Radius                  | 1.6                 | Width of the dig per pickaxe hit                                                                               |
| Dig Shape                     | Cube                | Cube = flat floors/walls/ceilings, Sphere = round organic caves                                                |
| Max Cave Depth                | 128                 | How far below the original surface you can dig, no matter the tool. Higher = more memory per dug zone          |
| Tool Depth Limits             | On                  | Cap dig depth by pickaxe tool tier (digging at a cap leaves a flat, even floor)                                |
| Tool Tier Depth Limits        | 0:8,1:16,2:48,3:128 | `Tier:meters` pairs (antler 0 … blackmetal 3); unlisted tiers are uncapped                                     |
| Max Carves Per Zone           | 5000                | Safety cap on stored operations per 64×64m zone                                                                |
| Underground Environment       | Darklands_dark      | Cave ambience (also: Crypt, SunkenCrypt, Caves, InfectedMine; empty = outside weather). Per player, not synced |
| Underground Environment Depth | 16                  | Meters below the surface before the ambience fades in (default = bronze's dig limit). Per player, not synced   |
| Underground Painting          | On                  | Hoe paths/paving paint cave floors (also shows on the surface above - see limitations)                         |

Avoid changing `Max Cave Depth` on a world that already has deep caves - existing carves below
the new limit will be clamped away.

## Removing the mod

---

Your world stays fully loadable - terrain reverts to the untouched heightmap and caves disappear
until the mod is reinstalled (the data survives). **However:** anything built inside a cave ends
up buried under the restored terrain and will likely collapse. Don't uninstall casually on a
world with underground builds.

## Good to know

---

- **Undermining is real.** Dig the ground out from under a building and it collapses, exactly
  like vanilla. Ore deposits and boulders are static props though - digging under one leaves it
  hanging in place.
- **No cave-ins.** Terrain has no gravity or structural simulation - overhangs and undercuts
  stay put.
- **Protected locations stay protected.** Digging is blocked inside no-build locations (boss
  altars, the trader, and so on). Vanilla dungeon interiors live far outside the terrain, so a
  tunnel can never break into one.
- **Smoke is honest.** A cave fire behaves like a fire in a sealed house - give the smoke
  somewhere to go or it will smother the fire, and then you.
- **Dug your own grave?** The hoe's raise ground builds a ramp back out. Worst case, the vanilla
  `die` console command works without devcommands.

<details>
<summary>How it works (for the curious)</summary>

Vanilla terrain is a heightmap: one height value per (x,z), and the ground mesh is that grid
stitched into a surface. A tunnel would need two surfaces at the same (x,z), which the data
structure simply cannot express - that's why vanilla has no caves.

HearthBelow replaces that, per zone and only when needed, with a **density grid**: a 3D array
sampled every 1m where positive = solid rock and negative = air. The terrain surface isn't
stored anywhere - it's wherever density crosses zero. A cave is just a pocket of negative
values with positive values above and below it, so overhangs and tunnels come for free.

- **Lazy conversion.** Nothing is voxel until you dig. The first carve in a 64×64m zone samples
  its heightmap (including all existing terrain edits) into the density grid, meshes it, and
  swaps the meshes in for the vanilla renderer + collider in one go. The meshing is budgeted at
  a few ms per frame, so there's no hitch, and the result is shape-identical to the original
  terrain.
- **Surface nets meshing.** The mesh is generated by surface nets (a marching-cubes cousin):
  every grid cell where density flips sign gets one vertex, placed from that cell's edge
  crossings, and neighboring cells' vertices are stitched into quads. The result lands on the
  same layer with the same material as vanilla terrain, so building, grass, biome detection,
  navmesh, and footsteps don't notice the difference.
- **Digging is subtraction.** A pickaxe hit subtracts a sphere/cube of density; the hoe adds it
  back. Zones are meshed in 16×16×16 chunks and only the chunks that changed are remeshed,
  which is what keeps digging cheap.
- **Persistence is replay.** The grid itself is never saved - that would be megabytes. Instead,
  each dig is stored as a tiny operation on the zone's TerrainComp ZDO (the same networked
  object vanilla uses for terrain edits), and the grid is rebuilt from heightmap + replayed ops
  on load. That's also the whole multiplayer story: a carve is a few dozen bytes, the per-zone
  list is compressed on top, and it all syncs through vanilla's own machinery.

The cost scales with what you dig: an untouched zone costs nothing, a dug zone holds its
density grid in memory (a few MB at the default 128m depth) plus its chunk meshes. Loading a
dug zone replays its stored ops over the freshly sampled heightmap (a one-off few
milliseconds) and then meshes on a ~5ms-per-frame budget until the zone swaps over. The
vanilla terrain stays active until everything is ready, so there's no pop-in and no hitch.
Dedicated servers do the same work (creatures need the collision), with the same per-frame
budget. Want real numbers from your own setup? Enable `Debug` in BepInEx's log config and
every zone conversion prints its build, replay, and meshing times to the log.
</details>

## Known limitations

---

- Digging below the water level (y≈30) does not flood or block water - avoid digging under
  shorelines unless you enjoy weirdness.
- Tar pits use the game's liquid system, which this mod does not touch yet. Digging under or
  right next to one is untested - dig elsewhere for now.
- Creatures need a few seconds to "learn" freshly dug tunnels (the navmesh rebuilds on
  vanilla's own cycle).
- Terrain paint is a flat 2D layer shared between a cave floor and the surface above it, so
  painting one paints the other. Turn off `Underground Painting` if clean surfaces matter more
  to you than paintable cave floors.
- Where a voxelized zone borders untouched terrain, a faint lip can show on extreme slopes; it
  disappears once the neighboring zone is dug into.
- Two players editing the exact same spot simultaneously can briefly see slightly different
  results; it reconciles when the area reloads.
- Digging near a zone border voxelizes the neighboring zone too (needed for seamless tunnels
  across borders).

---

For questions or comments, find me in the Hexium, Odin Plus Team Discord or in my own:

<table width="100%">
  <tr>
    <td align="center">
      <a href="https://hexium.gg">
        <img
          src="https://hexium.gg/assets/Logo.png"
          alt="Hexium"
          width="64"/>
      </a>
    </td>

<td align="center">
      <a href="https://discord.gg/Pb6bVMnFb2">
        <img
          src="https://i.imgur.com/XXP6HCU.png"
          alt="Odin Plus Discord"
          width="64"/>
      </a>
    </td>
<td align="center">
      <a href="https://discord.gg/pdHgy6Bsng">
        <img
          src="https://i.imgur.com/Xlcbmm9.png"
          alt="Azumatt's Discord"
          width="64"/>
      </a>
    </td>
  </tr>
</table>