# Jalopy Co-op

**English** | [Português](README.pt-BR.md)

A **2-player co-op** mod for [Jalopy](https://store.steampowered.com/app/446020/Jalopy/).
One player is the **driver** (the host, who owns the save) and the other becomes the **uncle** in the passenger seat.
You both travel in the same car through the same world, and either player can drive.

> **Test version (alpha).** You can already play a full trip, but there are still bugs. Back up your save.

---

## Installation

**Both players** need the mod, and it must be the same version.

1. Download the `.zip` of the latest version from **[Releases](../../releases)**.
2. Close Jalopy.
3. In Steam, right-click Jalopy and go to **Manage → Browse local files**.
4. Extract the zip into that folder, next to `Jalopy.exe`. It contains three items: `winhttp.dll`, `doorstop_config.ini` and the `BepInEx` folder.
5. Start the game from Steam as usual.

The zip already includes [BepInEx 5](https://github.com/BepInEx/BepInEx), the mod loader.

**To uninstall:** delete `winhttp.dll`. To remove everything, also delete `doorstop_config.ini` and the `BepInEx` folder.

## How to play

### Host (driver)
1. Start the game and load your save (**Continue**).
2. Press **F7** and click **Create room** (*Criar sala*).
3. Wait for the uncle to connect.

### Uncle (passenger)
1. Start the game and stay at the **main menu**. Do not click Continue.
2. Press **F7**. The host's room appears under "Rooms on the network" (*Salas na rede*).
3. Click **Connect** (*Conectar*). You join the host's world by yourself.

> The in-game mod menu is currently in Portuguese only.

### Playing over the internet

The easiest way is [Radmin VPN](https://www.radmin-vpn.com/): both players join the same network and the room appears as usual.

Without a VPN:
- The host opens port **7777** (TCP and UDP) on the router.
- The uncle types the host's IP into **Connect by IP** (*Conectar por IP*).

### Keys

| Key | What it does |
|---|---|
| F7 | Opens and closes the co-op window |
| F3 | Hides or shows the co-op status line at the top of the screen |

Every in-game action uses Jalopy's normal interactions: doors, seats, levers and clicks. The mod adds no keyboard shortcuts for these actions.

## Configuration

After the game runs once, the file `BepInEx/config/jalopycoop.cfg` is created. In it you can change:

- `NomeDaSala`: the room name.
- `Porta`: the port, 7777 by default.
- `MostrarLinhaCoop`: shows or hides the co-op status line.
- Fine-tuning of the player models' positions.

## Known issues

- The rain sound outside the car may not play.
- The position of the uncle NPC at the wheel, on the host's screen, still needs adjusting.
- Not synced yet:
  - the petrol pump counter;
  - the water lid;
  - the cigarette lighter;
  - the sun visor;
  - the alarm clock;
  - the motel's curtains, oven and drawers;
  - the home storage.
- Not yet tested in co-op: the countries after the first one, the trip back home and the end of the game.

Found a bug? Open an **[Issue](../../issues)** and attach `BepInEx/LogOutput.log` **from both players**. This file is in the Jalopy folder.

## Building from source

You need:
- the [.NET SDK](https://dotnet.microsoft.com/download);
- a copy of Jalopy with BepInEx 5 installed.

The game's DLLs are **not** included in this repository.

```bash
cd JalopyCoop
dotnet build -c Release -p:GameDir="C:\path\to\your\Jalopy\copy"
```

The plugin is built to `JalopyCoop/bin/Release/JalopyCoop.dll` and also copied to `GameDir\BepInEx\plugins\JalopyCoop`.

How the mod works:
- The host is in charge of everything: the car physics run only on the host.
- Important messages go over **TCP**. Car and player positions go over **UDP**.
- Room discovery uses a UDP broadcast on port **7779**.

## Future Plans

- Chat using the game's own dialogue system: messages show up like the uncle's lines in Jalopy
- 3 to 4 players
- Clothes and skins for the players

## Credits

- Jalopy belongs to **MinskWorks**. This is a fan-made mod with no connection to them. You need the original game.
- Built with [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX).
