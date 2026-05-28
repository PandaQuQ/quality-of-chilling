# Quality of Chilling

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.7.2-blueviolet.svg)](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net472)
[![BepInEx](https://img.shields.io/badge/BepInEx-5-orange.svg)](https://github.com/BepInEx/BepInEx)
[![Claude Code](https://img.shields.io/badge/Claude%20Code-Co--Authored-brown.svg?logo=anthropic)](https://github.com/anthropics/claude-code)
[![Antigravity CLI](https://img.shields.io/badge/Antigravity%20CLI-Co--Authored-blue?logo=google&logoColor=white)](https://github.com/google/antigravity)

**Quality of Chilling** is a modular BepInEx utility suite designed for the game *Chill with You: Lo-Fi Story*. 

The core module, `QualityOfChilling.Weather`, integrates real-world weather states and day/night transitions into the game. It provides a connection between your real-world environment and the lo-fi workspace.

---

## Key Features

- **Real-Time Weather Synchronization**: Fetches real-world meteorological data from public APIs without requiring individual API keys.
- **Dynamic IP Location & Manual Override**: Resolves your location via IP geolookup, or allows a manual override to sync weather to any specific region.
- **UI Integration**: Injects the active weather description and temperature directly into the game's native date UI.
- **Astronomical Day/Night Mapping**: Tracks real-world sunrise and sunset times to dynamically trigger game environment changes (Day, Sunset, Night).
- **Zero-Redundancy Cache System**: Stores a local 24-hour weather forecast to minimize API calls, enabling fast startup times and background updates only when cross-midnight dates occur.
- **Native Settings Menu Integration**: Adds customization options ("Enable Real-Time Weather" and "Auto IP Location") directly into the game's native General Settings tab, fully integrated with game localization, audio cues, and hover effects.

---

## Project Structure

```text
├── artifacts/                     # Output directory for compiled binaries
├── src/
│   └── QualityOfChilling.Weather/ # Real-time Weather Sync BepInEx plugin
│       ├── src/
│       │   ├── MiniJson.cs        # Lightweight JSON parser
│       │   ├── NativeLifecyclePatches.cs # Patches for settings UI & game lifecycle
│       │   └── RealTimeWeatherPlugin.cs  # Core weather manager and cache service
│       └── QualityOfChilling.Weather.csproj
└── QualityOfChilling.sln
```

---

## Local Development

### Prerequisites
- .NET SDK 8.0 or newer.
- *Chill with You: Lo-Fi Story* installed via Steam.

### Compilation
Configure the environment variable `CHILL_GAME_DIR` to point to your Steam installation, or let it default to the standard Steam library path:

```bash
# Set your game path (optional if using the default Steam path)
export CHILL_GAME_DIR="C:/program files (x86)/steam/steamapps/common/Chill with You Lo-Fi Story"

# Build the project in Release configuration
dotnet build src/QualityOfChilling.Weather/QualityOfChilling.Weather.csproj -c Release
```

### Deployment
Copy the compiled plugin DLL to your BepInEx plugins folder:

```bash
cp artifacts/QualityOfChilling.Weather/RealTimeWeatherForChill.dll "C:/program files (x86)/steam/steamapps/common/Chill with You Lo-Fi Story/BepInEx/plugins/"
```

---

## Release Workflow

The project uses GitHub Actions for CI/CD:
- Releases are automatically triggered by tags matching `v*` (e.g., `v1.0.0`).
- The build pipeline downloads public BepInEx 5 dependency references and compiles the binaries in a clean environment, removing any reliance on proprietary game DLLs or private signing keys.

---

## License

This project is licensed under the [MIT License](LICENSE).
