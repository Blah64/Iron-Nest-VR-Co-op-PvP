# IRON NEST VR — Network Test Build

VR + experimental Steam co-op mod for the free **IRON NEST: Heavy Turret
Simulator Demo** (Steam App 4300500). We each need our own Steam account with
the demo installed, online at the same time. VR headset optional — flatscreen
works too (co-op is cross-play).

## Install
1. In Steam, right-click the demo → **Manage → Browse local files**.
2. Copy everything inside this package's **`game-files`** folder into that game
   folder (say **Yes** to merge).
3. In Steam, right-click the demo → **Properties → Launch Options**, paste:
   `-force-d3d11 -force-gfx-direct`
4. Start your VR runtime (if using VR), then launch from Steam.
   *(First launch takes 1–3 min.)*

## Controls
- **F7** — toggle the on-screen lobby browser (Create / Refresh / Join / Leave).
- In VR: click **both thumbsticks together** to open the menu → **Lobbies** tab.

## Uninstall
Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and the
`BepInEx` and `dotnet` folders from the game directory. (Or Steam → Properties →
Installed Files → Verify integrity.)
