# LoupixDeck.Plugin.Audio

Windows audio integration plugin for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck),
built against [LoupixDeck.PluginSdk](https://github.com/RadiatorTwo/LoupixDeck.PluginSdk).

Windows only.

## Commands

`Audio.OutputDevices` / `Audio.InputDevices` — open a touch-screen folder
listing the active audio endpoints; a sub-folder per device shows the live
volume and a mute toggle (the first rotary adjusts volume).

## Build & deploy

```bash
dotnet build LoupixDeck.Plugin.Audio.csproj -c Release
```

Copy the build output together with `plugin.json` into
`LoupixDeck/plugins/audio/`.
