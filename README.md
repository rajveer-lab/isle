# Isle: your own dynamic island for Windows

A lightweight, iPhone / MacBook-style dynamic island built only for this PC, now with your Pixel built in. It's a native WPF app on .NET Framework 4.8, which is already part of Windows.

| | Isle | Dynamic Edge (measured on this PC) |
|---|---|---|
| CPU | ~0.03-0.07 % | ~0.63 % |
| Private memory | ~105-120 MB | ~228 MB |

## Install (like an .apk)

**`dist\IsleSetup.exe`** is a single 230 KB file with the whole app inside. Double-click it and choose **Install**.

- **Where it goes:** `%LOCALAPPDATA%\Programs\Isle`. No admin rights are needed.
- **Shortcuts:** it adds a Start menu entry, and optionally a desktop shortcut and auto-start.
- **Removing it:** use **Settings › Apps › Installed apps › Isle › Uninstall**. Your settings are kept unless you tick "delete my settings".
- **Updating:** running a newer IsleSetup.exe updates Isle in place.
- **Silent install / uninstall:** `IsleSetup.exe /quiet`, and `"%LOCALAPPDATA%\Programs\Isle\Uninstall Isle.exe" /uninstall /quiet`

## Using it

- **It doesn't block Chrome.** While small, the pill is click-through, so clicks land on the tab underneath.
- **Open it** by resting the cursor on the pill for half a second, or press **Ctrl+Alt+I**, or click the tray icon.
- It closes as soon as the cursor leaves.

## What it shows

- **Music:** album art and live bars in the pill. Opened, it has a seek bar, controls, volume, and a glow tinted with the art colors.
- **Calls:**
  - **Ongoing:** green phone icon and timer, plus Mute (mutes the mic for every app), Open and End.
  - **Incoming:** caller, Decline and a pulsing Accept.
  - Covers WhatsApp, Telegram, Discord, Teams, Zoom, browser calls, and **Pixel calls through Phone Link**.
- **Privacy dot:** orange next to the pill when the microphone is in use, green for the camera.
- **HUDs:**
  - volume
  - brightness
  - mic key
  - charging
  - headphones connected
  - Pixel connected / disconnected
  - clipboard from the phone
- **File shelf:** drag files onto the pill and hold. Drop them on **Keep on shelf** to park them, or on **Send to Pixel 8** to send them to the phone, like AirDrop.
- **Quick apps**, and a clock / battery view when nothing plays.
- **Fullscreen:** hides in fullscreen video, games and slides, except for incoming calls.

## The Isle app window

Opening **Isle from the Start menu** (or the tray's *Open Isle*) shows the app window:
- island settings: style, idle look, start with Windows, fullscreen
- your phone: status, battery, Ring, Send clipboard, Send files, Messages, Phone Link
- the history of text copied on your phone, with **Copy** buttons
- **Get the phone app**, Edit quick apps, Quit

Closing the window keeps Isle running.

## Isle for Android (Isle Link, Bluetooth, no third-party apps)

`dist\Isle-Phone.apk` (also installed next to Isle.exe) is Isle's own small phone app. It is built offline by `android\build-apk.ps1` with the Android SDK, with no Gradle and no libraries.

**Install it once:**
- Send the APK to the Pixel with Quick Share, open it, and tap Install.
- Open **Isle** on the phone, allow Bluetooth + notifications, and pick **FROSTBYTE**.
- Tap *Let Isle run in the background*.

After that it reconnects to the laptop over Bluetooth by itself, even after restarts. Isle on the laptop listens with no other app.

- **Phone → laptop clipboard:** copy anything, then tap **Copy to laptop** in Isle's notification, use the Quick Settings tile, or Share → Isle. Press **Ctrl+V** on the laptop. Isle shows "From Pixel 8 …".
- **Laptop → phone:** *Send clipboard* in the island or the app window.
- **Battery:** the Pixel's battery and charging state show in the island and the app window.
- **Ring:** rings the phone at full alarm volume, even on silent. Stop it from the phone's notification.

## Earbuds and volume

- **Galaxy Buds Live (or any Bluetooth earbuds/headphones):** when they connect, the island drops an AirPods-style card with an animated battery ring. Windows reads the buds' battery over Bluetooth.
- **Volume keys:** Isle takes them over (`own_volume_popup = true`), so only Isle's island pop-up shows, not Windows' own.
- **Brightness:** Isle's pop-up shows too. Windows' own brightness pop-up can't be switched off.

## Your Pixel

Isle works with what's already on this PC: **KDE Connect** (paired with Pixel 8), **Bluetooth**, **Phone Link** and **Quick Share**. The phone row in the open island has:

| Button | What it does |
|---|---|
| status | Connected via Wi-Fi (KDE Connect) and/or Bluetooth. Click it to copy the last phone clipboard again. |
| 🔔 | Find my phone: rings the Pixel, even on silent |
| 📋 | Sends this PC's clipboard to the phone |
| ➤ | Sends files to the phone |
| 💬 | Messages (KDE Connect SMS, otherwise Phone Link) |
| ↗ | Opens Phone Link (calls, photos) |

Isle starts KDE Connect in the background (`start_kdeconnect = true`). On the phone, open the **KDE Connect** app once on the same Wi-Fi so it connects.

### Paste on the laptop what you copied on the phone

KDE Connect puts the phone's clipboard on the laptop's clipboard, and Isle shows **"From Pixel 8: … Ctrl+V"**. Just press Ctrl+V.

Android 10+ only lets an app read the clipboard in the background with one extra permission:

- **Without the permission:** after copying, tap **Send clipboard** in KDE Connect's notification on the phone.
- **Automatic (one-time setup):** enable *USB debugging* on the Pixel (Settings › About phone, tap *Build number* 7×, then Settings › System › Developer options › USB debugging). Plug it in and run:

```
"%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" -d shell pm grant org.kde.kdeconnect_tp android.permission.READ_LOGS
"%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" -d shell appops set org.kde.kdeconnect_tp SYSTEM_ALERT_WINDOW allow
"%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe" -d shell am force-stop org.kde.kdeconnect_tp
```

These are KDE's own documented steps (userbase.kde.org/KDEConnect › Clipboard › Auto-sync on Android 10+).

## Settings

`%APPDATA%\Isle\settings.ini` (tray → *Edit settings & quick apps…*, then tray → *Reload settings*):

- `style = notch | island`, `idle = clock | minimal`
- `hover_delay_ms` (default 500), `collapse_delay_ms`, `hide_in_fullscreen`, `scroll_volume`, `track_peek`
- `compact_title = true`: song title always in the pill
- `phone = auto | off`, `phone_name`, `start_kdeconnect`
- `[apps]`: `Name = AppID`, a full path, or a URL (`Get-StartApps` lists AppIDs)

Logs go to `%APPDATA%\Isle\isle.log`. Incoming-call windows are logged there for tuning.

## Build

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

This builds `bin\Isle.exe` and `dist\IsleSetup.exe`. Debug helpers: `--pin-expanded`, and `--snapshot=<folder>`, which renders every state to PNGs.

## Code map

| File | What |
|---|---|
| `src/IslandWindow.cs` | Window, click-through + dwell hover, spring morph, every state and HUD |
| `src/Island.xaml` | Layout and styles |
| `src/MediaService.cs` | Windows media sessions, album art, accent color |
| `src/CallService.cs` | Mic/camera usage, incoming calls, Accept/Decline/End via UI Automation |
| `src/Phone.cs` | Pixel: KDE Connect, Bluetooth, clipboard from phone, Phone Link / Quick Share |
| `src/AudioService.cs` | Volume, peak meter, mic mute, output-device changes |
| `src/Extras.cs` | Brightness (WMI), file shelf |
| `setup/Setup.cs`, `setup/Setup.xaml` | The single-file installer / uninstaller |
