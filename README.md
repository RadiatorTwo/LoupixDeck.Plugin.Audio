# LoupixDeck.Plugin.Audio

Audio integration plugin for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck),
built against [LoupixDeck.PluginSdk](https://github.com/RadiatorTwo/LoupixDeck.PluginSdk).

Windows via WASAPI (NAudio), Linux via `pactl` (PulseAudio / pipewire-pulse).
Sound playback on Linux uses `paplay`. Formats libsndfile cannot decode (mp3,
m4a) are decoded by `ffmpeg` and piped into `paplay`, or played by `mpv`; plain
`ffplay` is used only when no specific playback device is selected, because it
offers no way to target one. Selecting a device therefore needs `ffmpeg` plus
`paplay`, or `mpv` — with none of them present the sound is skipped rather than
played on the wrong device.

## Commands

`Audio.OutputDevices` / `Audio.InputDevices` — open a touch-screen folder
listing the active audio endpoints; a sub-folder per device shows the live
volume and a mute toggle (the first rotary adjusts volume).

`Audio.CurrentOutput` — a touch-screen button that shows the active output
device's alias and opens the output-device picker when pressed. The label
refreshes every two seconds, so it can lag a default-device change made
outside the app (from the OS sound settings or `pactl`) by a few seconds, and
reads "No device" when none is set.

`Audio.VolumeUp` / `Audio.VolumeDown` / `Audio.MuteToggle` — act on one
device, assigned from the command menu (Audio → Output/Input Devices). The
volume step is editable per assignment.

`Audio.Mixer` — open a per-application mixer on the touch screen. Tap a tile
to select an application, the first rotary then adjusts it and its press mutes.
It lists the applications playing on **any** output device, not just the
default one, so a virtual mixer that routes each application to a device of its
own does not hide them. The Windows audio engine is left out, and the system
sounds session is listed as "System Sounds".

Each tile shows the application's icon, its level and its name. On Windows the
icon and the name (the executable's file description, for example "Google
Chrome") come from the executable; on Linux, and for applications without an
icon, a speaker is shown. The command has two parameters, so every assignment
can look different (five parameters):

- `layout` — `Top` (icon above level and name, the default), `Left` (icon on the
  left, name over two lines), `Background` (faded icon behind a large level) or
  `Arc` (faded icon inside an arc that shows the level).
- `font` — `Smooth` (the default) draws the text in LoupixDeck's anti-aliased font.
  `Pixel` draws it in a 5x7 bitmap font that sits exactly on the panel's pixels:
  uppercase, 12 characters per line on a 90 px key. A name the bitmap font cannot
  spell (for example Chinese or Cyrillic) is drawn in the smooth font
  automatically.
- `transparent` (on by default) — no tile background: the wallpaper (or the device's black) shows
  through, and the level's track becomes translucent. The selected tile is still
  marked by its frame.
- `outlined` (on by default) — a dark outline around the text, which keeps it legible on a
  transparent tile over a busy wallpaper. In the smooth font the outline is drawn
  by LoupixDeck and is a little heavier than the bitmap font's one pixel.

- `scroll` — which tiles run a name that is wider than the tile: `All` (the
  default), `Selected` (only the tile with the frame) or `Off`. A scrolling name
  moves left at a constant speed and comes back in from the right; a name that is
  not allowed to scroll is cut with an ellipsis. The layout `Left` never scrolls,
  it breaks the name over two lines. Scrolling redraws the folder about ten times a
  second for as long as a name is running.

An assignment saved before these parameters existed uses the defaults (transparent
background, outlined text, everything scrolls); untick the two options for a solid
tile without outline.

`Audio.OutputDevices` and `Audio.InputDevices` open the device list with the same
tiles and take the same five parameters. A tile shows the device's icon (speaker,
or microphone for inputs), its volume and its name; the default device carries the
selection frame, and a muted device is greyed out and struck through. Tapping a tile
opens that device's volume, mute and default controls. The levels follow the
devices while the folder is open. A list saved before the tiles existed picks up the
new look with the defaults.

The tiles are drawn at the size of the key, so they stay sharp on a calibrated key
(74 px, for example). That needs LoupixDeck with Plugin SDK 1.28.0 or newer; an
older LoupixDeck receives a 90 px picture and scales it to the key, which softens
the pixel font.

`Audio.AppVolumeUp` / `Audio.AppVolumeDown` / `Audio.AppMuteToggle` /
`Audio.AppSetVolume` — act on one application, assigned from the command menu
under Audio → Applications. A binding stores the process name, so it survives
that application restarting. "Foreground App" follows the focused window on
Windows, and on Linux for X11 and XWayland windows (resolved with `xprop`, which
must be installed). A native Wayland window carries no `_NET_WM_PID`, so under a
Wayland session the dial stays empty for applications that do not go through
XWayland.

`Audio.SetVolume` / `Audio.AppSetVolume` — set a device or an application to a
fixed level in percent, editable per assignment.

`Audio.SetDefaultDevice` — make one device the system default. On Linux the
already-playing streams are moved across to it as well.

`Audio.PlaySound` — play an audio file. Set a **sound folder** in the plugin
settings; its files then appear in the command menu under Audio → Play Sound,
with sub-folders as sub-menus. Every press starts its own playback, so
repeated presses overlap — unless **stop on second press** is enabled, where
a press while the sound is still running stops it instead.

`Audio.StopSounds` — stop every sound this plugin is currently playing. Works
regardless of the **stop on second press** setting and affects only the
plugin's own playback, not the audio of other applications.

## Settings

- **Sound folder** — folder scanned for `.wav`, `.mp3`, `.flac`, `.m4a` and
  `.aiff` files (`.ogg` on Linux only — Windows has no Vorbis decoder).
- **Stop on second press** — off by default. When on, pressing a sound button
  again stops that sound rather than layering a second playback on top. A sound
  that already ended on its own simply starts again. Buttons sharing the same
  file share the toggle.
- **Playback device** — one toggle per output device; enable exactly one to
  route `Audio.PlaySound` there. With none enabled the system default is used.
  A selected device that is currently unplugged keeps its selection and stays
  listed, so unplugging it does not silently reset the choice.
- **Device aliases** — short names replacing the OS device names.
- **Side-strip volume bars** — vertical bars or stacked horizontal segments.

## Build & deploy

```bash
dotnet build LoupixDeck.Plugin.Audio.csproj -c Release
```

Copy the build output together with `plugin.json` into
`LoupixDeck/plugins/audio/`.
