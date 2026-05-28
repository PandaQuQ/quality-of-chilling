# Quality of Chilling

Quality of Chilling is a BepInEx mod suite for **Chill with You: Lo-Fi Story**.

The first module, `QualityOfChilling.Weather`, syncs real-world weather and local day/night state into the game:

- public weather APIs, no weather API key required
- manual location or IP-based location
- weather text injected into the game's native date UI
- native window/time switching through the game's `WindowViewService`
- sunrise/sunset-aware day, sunset, and night mapping

Future modules may add system audio status sync, external player control, radio/m3u8 playback, and WebSocket integrations.

## Build locally

Install .NET SDK 8 or newer.

Set `CHILL_GAME_DIR` to your local game directory, or rely on the default Steam path used by the project file:

```bash
export CHILL_GAME_DIR="C:/program files (x86)/steam/steamapps/common/Chill with You Lo-Fi Story"
dotnet build src/QualityOfChilling.Weather/QualityOfChilling.Weather.csproj -c Release
```

The DLL is written to:

```text
artifacts/QualityOfChilling.Weather/RealTimeWeatherForChill.dll
```

Install it by copying the DLL to:

```text
<game>/BepInEx/plugins/RealTimeWeatherForChill.dll
```

## Release workflow

Releases are tag-triggered. Push a tag matching `v*` to run `.github/workflows/release.yml`.

The workflow downloads BepInEx 5 references from the official BepInEx GitHub release and uses public Unity reference packages, so no private game DLLs or GitHub secrets are required for release builds.

## License

MIT
