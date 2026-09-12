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

`Audio.VolumeUp` / `Audio.VolumeDown` / `Audio.MuteToggle` — act on one
device, assigned from the command menu (Audio → Output/Input Devices). The
volume step is editable per assignment.

`Audio.Mixer` — open a per-application mixer on the touch screen. Tap a tile
to select an application, the first rotary then adjusts it and its press mutes.
It lists the applications playing on **any** output device, not just the
default one, so a virtual mixer that routes each application to a device of its
own does not hide them. The Windows audio engine is left out, and the system
sounds session is listed as "System Sounds".

`Audio.AppVolumeUp` / `Audio.AppVolumeDown` / `Audio.AppMuteToggle` /
`Audio.AppSetVolume` — act on one application, assigned from the command menu
under Audio → Applications. A binding stores the process name, so it survives
that application restarting. "Foreground App" follows the focused window on
Windows; on Linux it does nothing, because focus detection there needs X11.

`Audio.SetVolume` / `Audio.AppSetVolume` — set a device or an application to a
fixed level in percent, editable per assignment.

`Audio.SetDefaultDevice` — make one device the system default. On Linux the
already-playing streams are moved across to it as well.

`Audio.PlaySound` — play an audio file. Set a **sound folder** in the plugin
settings; its files then appear in the command menu under Audio → Play Sound,
with sub-folders as sub-menus. Every press starts its own playback, so
repeated presses overlap — unless **stop on second press** is enabled, where
a press while the sound is still running stops it instead.

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
