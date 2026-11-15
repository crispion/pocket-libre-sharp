# PocketLister

A small Windows console application that connects to a Pocket AI voice recorder (PKT01) over Bluetooth Low Energy, lists the recordings stored on it, and downloads them as MP3 files.

> **Unofficial.** The Pocket's Bluetooth protocol is not published by the manufacturer. This app relies on the protocol as documented by the community projects [private-pocket](https://github.com/Incept5-AI/private-pocket) and [pocket-libre](https://github.com/shahcolate/pocket-libre). A firmware update may change the protocol and break the app.

The app's console output is in Swedish.

## Requirements

- Windows 10 (version 2004 / build 19041) or later, with a Bluetooth LE adapter
- .NET 8 SDK or later
- A Pocket recorder, switched on and within range
- The device's 16-character session key (see [Pairing](#pairing))

No NuGet packages are needed; the app uses the Bluetooth APIs built into Windows.

## Build

Place `Program.cs`, `PocketDevice.cs` and `PocketLister.csproj` in the same folder, then:

```
dotnet build
```

The examples below use `dotnet run --`. After building you can also run `PocketLister.exe` from `bin\Debug\net8.0-windows10.0.19041.0\` with the same arguments.

## Before you connect

The Pocket accepts only one Bluetooth connection at a time. Force-quit the Pocket app on your phone (or switch off the phone's Bluetooth) before running any command, otherwise the device will not be found.

## Pairing

The Pocket does not use standard Bluetooth pairing. Instead, every connection must start with a 16-character session key; without the right key the device ignores all commands. Pairing in this app means storing that key. There are two ways to do it.

### Option A: use the key your phone already uses

Choose this if you want to keep using the official phone app alongside PocketLister.

```
dotnet run -- pair --key <16-character key>
```

To find the key on an Android phone:

1. Enable Developer options (Settings → About phone → tap Build number seven times).
2. In Developer options, set **Enable Bluetooth HCI snoop log** to **Enabled**.
3. Turn Bluetooth off and on again.
4. Open the Pocket app and let it connect and sync.
5. In Developer options, choose **Bug report** → **Full report**, and share the resulting zip file to your PC.
6. Extract `FS\data\misc\bluetooth\logs\btsnoop_hci.log` from the zip.
7. In PowerShell, in the folder containing the log:

   ```powershell
   $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes("btsnoop_hci.log"))
   [regex]::Matches($text, 'APP&SK&[A-Za-z0-9]{16}') | % Value | Select -Unique
   ```

   The 16 characters after `APP&SK&` are the key.

Turn the snoop log off again afterwards.

### Option B: pair a factory-reset device with a new key

Choose this if you do not need the official phone app. After a reset, the device adopts the first key it receives as its permanent key, so the phone app will stop working with the device until you pair it there again.

```
dotnet run -- pair
```

The app asks you to reset the device first: triple-click the side button until the LED blinks red, then hold the button until it pulses blue. Press Enter, and the app generates a key and sends it. The device disconnects shortly after pairing; wait a couple of seconds before running the next command.

### Where the key is stored

The key and the device address are saved in `%APPDATA%\PocketLister\config.json`. Treat the key like a password: anyone within Bluetooth range who has it can list and download your recordings.

`pair` refuses to overwrite a saved key unless you add `--force` or supply `--key`.

## Usage

### List recordings

```
dotnet run -- list
```

Shows the device name, battery level and firmware version, followed by every recording grouped by date, with file name and duration.

To list a single day:

```
dotnet run -- list --date 2026-11-14
```

### Download a recording

Use a date and file name exactly as shown by `list`:

```
dotnet run -- download --date 2026-11-14 --file <file name>
```

The file is saved as MP3 in the current folder. To save it elsewhere, add `--out` with either a folder or a full file path:

```
dotnet run -- download --date 2026-11-14 --file <file name> --out C:\Recordings
```

When the download finishes, the app prints the full path, the size and the transfer speed. It compares the received size with the size reported by the device and warns if they differ.

Transfer speed over Bluetooth varies a great deal between reports (from a few kB/s to around 60 kB/s), so long recordings can take a while.

## Command reference

| Command | Description |
|---|---|
| `pair` | Pair a factory-reset device with a newly generated key |
| `pair --key <key>` | Store and verify an existing session key |
| `list` | List all recordings on the device |
| `list --date <YYYY-MM-DD>` | List recordings for one date |
| `download --date <YYYY-MM-DD> --file <name>` | Download one recording |

| Option | Description |
|---|---|
| `--out <folder or file>` | Where `download` saves the file (default: current folder) |
| `--verbose`, `-v` | Print all Bluetooth traffic (commands sent and replies received) |
| `--a3` | Send commands on characteristic `…a3` instead of `…a2` |
| `--force` | Let `pair` overwrite a saved key |

Exit codes: `0` success, `1` error, `2` download completed but the size did not match.

## Troubleshooting

| Problem | What to try |
|---|---|
| No device found | Check that the Pocket is on, that Bluetooth is enabled on the PC, and that the phone app is fully closed. |
| Pocket service not found | Another device is probably connected. Close the phone app and try again. |
| No reply to the session key | The key is wrong (the device then stays silent), or your firmware expects commands on the other characteristic. Try adding `--a3`. |
| Session key rejected | The device is already bound to a different key. Capture that key (Option A) or factory-reset the device (Option B). |
| `list` reports that the device did not answer `LIST_DIRS` | The app falls back to checking the last 30 days. Use `--date` for older recordings. |
| Download stalls | The app aborts after 15 seconds without data and saves nothing. Move closer to the device and retry. |
| Anything else | Run with `--verbose` to see the raw traffic. |

## Limitations

- Windows only.
- One file per `download` call; there is no download-all command.
- Recordings cannot be deleted over Bluetooth.
- The Bluetooth link is unencrypted; the session key and audio travel in clear text.

## Protocol summary

The device exposes a custom GATT service, `001120a0-2233-4455-6677-889912345678`:

| Characteristic | Properties | Use |
|---|---|---|
| `001120a2-…` | write | Commands (ASCII) |
| `001120a3-…` | write without response, notify | Replies and events (ASCII) |
| `001120a1-…` | notify | Audio data (raw MP3) |

Messages are `&`-separated ASCII. The app sends `APP&…`; the device answers `MCU&…`.

| Sent | Reply |
|---|---|
| `APP&SK&<key>` | `MCU&SK&OK` |
| `APP&BAT` | `MCU&BAT&<percent>` |
| `APP&FW` | `MCU&FW&<version>` |
| `APP&LIST_DIRS` | `MCU&DIRS&<date>` per date, then `MCU&DIRS_SUM&<count>` |
| `APP&LIST&<date>` | `MCU&F&<date>&<file>&<seconds>` per file, then `MCU&LIST&<count>` |
| `APP&U&<date>&<file>` | `MCU&U&<bytes>`, audio data on `…a1`, then `MCU&OFF` |

## Legal

This project is not affiliated with or endorsed by the maker of the Pocket recorder. Use it only with a device you own.
