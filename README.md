# LoupixDeck.Plugin.Audio

Audio integration plugin for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck),
built against [LoupixDeck.PluginSdk](https://github.com/RadiatorTwo/LoupixDeck.PluginSdk).

Windows via WASAPI (NAudio), Linux via `pactl` (PulseAudio / pipewire-pulse).
Sound playback on Linux uses `paplay`, falling back to `ffplay` or `mpv` for
formats libsndfile cannot decode (mp3, m4a).

## Commands

`Audio.OutputDevices` / `Audio.InputDevices` — open a touch-screen folder
listing the active audio endpoints; a sub-folder per device shows the live
volume and a mute toggle (the first rotary adjusts volume).

`Audio.VolumeUp` / `Audio.VolumeDown` / `Audio.MuteToggle` — act on one
device, assigned from the command menu (Audio → Output/Input Devices). The
volume step is editable per assignment.

`Audio.PlaySound` — play an audio file. Set a **sound folder** in the plugin
settings; its files then appear in the command menu under Audio → Play Sound,
with sub-folders as sub-menus. Every press starts its own playback, so
repeated presses overlap.

## Settings

- **Sound folder** — folder scanned for `.wav`, `.mp3`, `.flac`, `.m4a` and
  `.aiff` files (`.ogg` on Linux only — Windows has no Vorbis decoder).
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
