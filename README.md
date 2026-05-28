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

Because this project must reference proprietary game and Unity assemblies, the GitHub workflow expects a repository secret named `CHILL_GAME_REFS_ZIP_BASE64`. It should be a base64-encoded zip containing this layout:

```text
BepInEx/core/BepInEx.dll
BepInEx/core/0Harmony.dll
Chill With You_Data/Managed/Assembly-CSharp.dll
Chill With You_Data/Managed/Unity.TextMeshPro.dll
Chill With You_Data/Managed/UnityEngine.dll
Chill With You_Data/Managed/UnityEngine.CoreModule.dll
Chill With You_Data/Managed/UnityEngine.IMGUIModule.dll
Chill With You_Data/Managed/UnityEngine.InputLegacyModule.dll
Chill With You_Data/Managed/UnityEngine.ParticleSystemModule.dll
Chill With You_Data/Managed/UnityEngine.UnityWebRequestModule.dll
Chill With You_Data/Managed/UnityEngine.AudioModule.dll
Chill With You_Data/Managed/UnityEngine.UIModule.dll
Chill With You_Data/Managed/UnityEngine.TextRenderingModule.dll
Chill With You_Data/Managed/UnityEngine.UI.dll
```

Do not commit those DLLs to the public repository.

## License

MIT
