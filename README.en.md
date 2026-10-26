# ADOFAI Mod Helper

[한국어](README.md) | **English**

A Windows app for people new to A Dance of Fire and Ice mods.
It takes you from installing Unity Mod Manager (UMM) to installing, updating and toggling mods, all with buttons.

- **One exe**: no installer, just double-click
- **Automatic UMM install**: downloads the latest UMM from its official source and installs it the same way the official installer does
- **One-click mods**: pick from the list to get the latest version, required libraries (JALib etc.) included
- **Management**: enable/disable, delete (to Recycle Bin), update all, uninstall UMM, game update detection
- **Fixes bad installs**: mods put in `Mods/Mods/…`, duplicate mods and zips that were never extracted, with one button
- **Copy diagnostics**: a status summary to paste when asking for help
- Korean / English

## Requirements

- Windows 10 (1903 or later) or Windows 11
- ADOFAI 3.x on Steam
- An internet connection (to download UMM and mods)

You don't need to install .NET or anything else. The app uses .NET Framework 4.8, which ships with Windows.

## How to install

1. **Download `AdofaiModHelper.exe` from the [latest release](https://github.com/newmouse11/adofai-mod-helper/releases/latest).**
   Put it in any folder, such as your desktop. It doesn't need to be in the game folder.
2. **Run it.** If a blue "Windows protected your PC" window appears, click **More info → Run anyway** ([why?](#windows-protected-your-pc-appears)).
3. **Follow the first-run wizard.**
   1. **Find game**: finds ADOFAI in your Steam library automatically. If it can't, click **[Locate]** and pick `A Dance of Fire and Ice.exe`.
   2. **Install UMM**: click **[Install UMM]** and it downloads and installs it. If it's already installed, click **[Next]**.
   3. **Pick mods**: check the mods you want and click **[Install selected]**. Click **[Skip]** to do it later.
4. **Start the game and press `Ctrl + F10`** to open the UMM mod menu.

## Using the app

| Area | What it does |
|---|---|
| Status bar (top) | Game folder and UMM status. **[Change]** to pick another game folder, **[Update]/[Repair]/[Install UMM]**, **[Launch game]** |
| Yellow notice bar | Appears only when something is wrong. Click its button (**[Fix]**, **[Repair]**, …) to solve it |
| **Installed** tab | Your mods. Switch to enable/disable, trash icon to delete, **[Update]** when a newer version exists |
| **Browse mods** tab | Mods you can install. One **[Install]** also installs required libraries |
| Bottom | **[Copy diagnostics]**, **[Uninstall UMM]**, app version on the right (with a download link when a new version is out) |
| Top right | 한국어 / English switch |

> Mod files can't be changed while the game is running. Close the game before installing, toggling or deleting mods.

## FAQ

### "Windows protected your PC" appears
The app isn't code-signed (that needs a paid certificate), so Windows says it's an unfamiliar file. It is not a malware detection.
Click **More info → Run anyway**. If you want to be sure, check that the hash matches `SHA256SUMS.txt` from the release:
```bat
certutil -hashfile AdofaiModHelper.exe SHA256
```

### It can't find my game
In wizard step 1, or with **[Change]** at the top, pick `A Dance of Fire and Ice.exe` yourself.
To find it, right-click ADOFAI in Steam → **Manage → Browse local files**.

### "This tool can't write to the game folder"
This can happen when the game is in a protected folder such as `C:\Program Files`. Click **[Restart as administrator]** in that dialog.

### `Ctrl + F10` doesn't open the menu in game
1. On a laptop, try holding `Fn` as well (`Ctrl + Fn + F10`).
2. Check that the top of the app shows UMM as **installed**. If it says **Broken**, click **[Repair]**.
3. If it still doesn't work, see [Getting help](#getting-help).

### I added a mod but it doesn't show up in game
ADOFAI only reads folders **directly** inside `Mods`. A mod one level deeper (like `Mods/Mods/ModName`) or a zip that wasn't extracted is ignored.
The app shows "N mods are installed incorrectly" in the yellow bar. Click **[Fix]** to move them into place. If the same mod exists twice, it keeps the higher version and sends the rest to the Recycle Bin.

### The game won't start after a game update
The app tells you when the game was updated. Try these in order:
1. **[Check updates]** in the notice bar. The mods may have been updated for the new version.
2. If it still won't start, **[Disable all mods]**. If the game starts, turn mods back on one at a time to find the culprit.
3. If it still won't start, **[Uninstall UMM]** at the bottom. The game goes back to its unmodded state.

### What happens when I disable a mod?
Its dll is renamed to `name.dll.disabled`, so UMM doesn't load it. Enabling renames it back, and the mod's settings stay as they were.
Disabling a library (JALib etc.) also stops the mods that use it, so the app tells you which mods are affected.

### I want a mod that isn't in the list
Extract the mod zip and put the folder that contains `Info.json` into the game's `Mods` folder. If you put the zip in as-is, the app's **[Fix]** extracts and installs it.
After that you can enable, disable and delete it in this app. Mods you'd like added to the list can be requested in [Issues](https://github.com/newmouse11/adofai-mod-helper/issues).

### "MelonLoader (or BepInEx) is also installed"
Using two mod loaders together can cause conflicts. This app is for UMM, so we recommend using only one.

### "GitHub request limit reached"
GitHub allows 60 requests per hour without signing in. Try again after the time shown. The app reuses results from the last 15 minutes, so this rarely happens.

### "The game path contains non-English characters"
UMM can have trouble with paths that contain non-English characters. If everything works, you can ignore it. If not, move your Steam library to an English-only path (for example `D:\SteamLibrary`).

### I want to remove mods completely and go back to the original game
Click **[Uninstall UMM]** at the bottom. Only UMM's files go to the Recycle Bin; your mod folders and on/off settings stay, so reinstalling brings everything back.
To remove the mods too, delete the game's `Mods` folder. Save data is never touched.

### I want to see the wizard again
Close the app, delete the `%LocalAppData%\AdofaiModHelper` folder and start it again. This only removes the app's settings and logs; the game is untouched.

### Getting help
Click **[Copy diagnostics]** at the bottom and paste it into [Issues](https://github.com/newmouse11/adofai-mod-helper/issues) or your Discord question.
It contains the game path, UMM status, mod list and recent logs, which makes the cause easier to find. It contains no passwords, but the path can show your Windows user name, so take a look before pasting.

---

## Maintaining the mod list (`catalog/mods.json`)

The app reads `catalog/mods.json` from this repository at startup. **Edit and push this one file to change the list without releasing a new exe.**
(If it can't be downloaded, the app uses the copy built into the exe.)

```jsonc
{
  "id": "TimingShow",                    // must match the mod's Info.json Id to detect installs and updates
  "name": "TimingShow",                  // name shown in the app
  "author": "SleepingCui",
  "repo": "SleepingCui/TimingShow",      // GitHub owner/repository
  "asset": "^TimingShow\\.zip$",         // regex that picks the UMM zip among release files
  "prerelease": false,                   // true only for repos without stable releases (JALib)
  "library": false,                      // true hides it from the wizard and installs it only when needed
  "requires": ["JALib"],                 // mod Ids to install with it
  "description": { "ko": "…", "en": "…" }
}
```

- Don't add MelonLoader-only mods (this app supports UMM only).
- After adding an entry, `dotnet run --project tests/SelfTest -c Release -- --online` **downloads every mod and checks the Id matches**.

## Development

Requires Windows and the .NET SDK (verified with 10.0.402; the build targets .NET Framework 4.8).

```bash
dotnet build src/AdofaiModHelper -c Release
```

Output: `src/AdofaiModHelper/bin/Release/net48/AdofaiModHelper.exe` (ship this single file)

```bash
dotnet run --project tests/SelfTest -c Release
```

The self test checks UMM install/diagnosis/removal and mod install/toggle/fixing on a fake game folder. Add `-- --online` to also download and install the real UMM and every mod in the list.

Releases: bump `<Version>` and push a `v<version>` tag. GitHub Actions builds, runs the self test and publishes the exe with `SHA256SUMS.txt`.

| Path | Contents |
|---|---|
| `src/AdofaiModHelper/Core/` | Logic independent of the UI (game detection, UMM install, mod management, catalog, diagnostics) |
| `src/AdofaiModHelper/MainWindow.xaml(.cs)` | Wizard and main screen |
| `src/AdofaiModHelper/Theme.xaml` | Colors, fonts, control styles |
| `src/AdofaiModHelper/Strings.*.xaml` | Korean/English strings |
| `catalog/mods.json` | One-click mod list |

## How it works

It reproduces the DoorstopProxy install of the official UMM installer (`UnityModManagerApp/Form.cs`).

- `game\winhttp.dll` ← `winhttp_x64.dll` (or x86) from the UMM zip / `game\doorstop_config.ini`
- `game\A Dance of Fire and Ice_Data\Managed\UnityModManager\` ← `UnityModManager.dll`, `0Harmony.dll`, `dnlib.dll`, `UnityModManager.xml`, `Config.xml`
- Disabling a mod renames its dll (`AssemblyName` in Info.json) to `name.dll.disabled` and sets it off in UMM's `Params.xml`. Enabling restores both. A disabled mod stays disabled when updated.
- Anything removed goes to the Recycle Bin. The save folder (`User`) is never touched.

## License

- Bundled fonts: IBM Plex Sans, Noto Sans KR (SIL Open Font License 1.1) — [`licenses/`](licenses/)
- UMM is not bundled; it's downloaded from its official source at run time (UMM: MIT, newman55).
- Mods are downloaded from each author's repository and follow their own licenses.
- This is an unofficial tool, not affiliated with 7th Beat Games. The developers aren't responsible for problems caused by mods, so if the game won't start, disable mods or uninstall UMM first.
