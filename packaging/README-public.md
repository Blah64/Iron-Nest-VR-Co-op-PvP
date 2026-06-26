# IRON NEST VR
> This package contains **only the mod**. You install BepInEx yourself first
> (step 1) — it's a one-time setup.

## 1. Install BepInEx 6 (IL2CPP, x64)
This is an IL2CPP Unity game, so it needs **BepInEx 6 (IL2CPP)** — *not*
BepInEx 5 (Mono).

1. Download the latest **BepInEx 6 bleeding-edge** build for **Unity IL2CPP /
   win-x64** from <https://builds.bepinex.dev/projects/bepinex_be> (the
   `BepInEx-Unity.IL2CPP-win-x64` artifact, which bundles the .NET runtime).
   *(Developed against build `6.0.0-be.764`; any recent IL2CPP x64 build works.)*
2. Open the game folder: in Steam, right-click the demo → **Manage → Browse
   local files**.
3. Extract the BepInEx zip into that folder so **`winhttp.dll`** and the
   **`BepInEx`** folder sit next to `Iron Nest Heavy Turret Simulator.exe`.
4. Launch the game once, wait ~1–3 min at the menu (BepInEx builds its support
   files), then quit.

## 2. Install this mod
Copy everything inside this package's **`game-files`** folder into that same game
folder (say **Yes** to merge). This adds:
- `openxr_loader.dll` in the game root — the OpenXR loader the VR layer needs, and
- the mod under `BepInEx\plugins\IronNestVR\`.

## 3. Set launch options (required)
Steam → right-click the demo → **Properties → Launch Options**:
```
-force-d3d11 -force-gfx-direct
```

## 4. Play
Start your OpenXR VR runtime (SteamVR / Meta / WMR / etc.), then launch from
Steam. No headset? It also runs in flatscreen.

### Controls
- **F7** — toggle the on-screen co-op lobby browser (Create / Refresh / Join / Leave).
- In VR: click **both thumbsticks together** to open the in-headset menu.
   -'A'/'X' on the controller holding the clipboard brings up the pencil colors.
   -'A'/X' while looking at the map opens a zoomed map screen for easier reading.
   -Left menu button is 'esc'.
   -'B' on right controller deletes map lines.
   -Left thumbstick to move
   -Right thumstick to rotate
   -'Grab' grabs on to clipboards and Iron Nest controls.
   -Trigger interacts with map, maps pieces, punchcards, record player discs and coffee.  It is also a back up in case you do not like grabbing and pulling levers.


## Uninstall
Delete `openxr_loader.dll` from the game root and the
`BepInEx\plugins\IronNestVR` folder.

## Credits / licenses
- Khronos **OpenXR loader** (`openxr_loader.dll`) — Apache-2.0.
- **Silk.NET** — MIT. Runs under **BepInEx** — LGPL-2.1.
