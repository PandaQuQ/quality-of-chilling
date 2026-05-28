using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;
using Bulbul;

namespace RealTimeWeatherForChill;

internal static class RealTimeWeatherBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AfterSceneLoad()
    {
        RealTimeWeatherPlugin.BootstrapAfterSceneLoad("RuntimeInitializeOnLoadMethod.AfterSceneLoad");
    }
}

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class RealTimeWeatherPlugin : BaseUnityPlugin
{
    private const string PluginGuid = "panda.chillwithyou.realtimeweather";
    private const string PluginName = "Real Time Weather for Chill With You";
    private const string PluginVersion = "0.1.0";

    internal static RealTimeWeatherPlugin? Instance { get; private set; }
    internal static string CurrentUiWeatherString { get; private set; } = string.Empty;
    internal static ManualLogSource Log { get; private set; } = null!;
    private static readonly string DebugLogPath = Path.Combine(Paths.BepInExRootPath, "RealTimeWeatherForChill.debug.log");

    private Harmony? harmony;
    private WeatherConfig weatherConfig = null!;
    private WeatherClient weatherClient = null!;
    private WeatherApplier weatherApplier = null!;
    private NativeGameBridge nativeBridge = null!;
    internal WeatherSnapshot? LastWeather => lastWeather;
    internal string UiWeatherString => lastWeather == null ? string.Empty : $"{lastWeather.Text} {lastWeather.TemperatureCelsius}°C";
    private WeatherSnapshot? lastWeather;
    private WeatherRuntime? runtime;
    private string status = "未刷新";
    private string manualLocationBuffer = string.Empty;
    private bool fallbackRefreshStarted;
    private bool quitting;
    private float nextFallbackTickLogTime;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        weatherConfig = new WeatherConfig(Config);
        weatherClient = new WeatherClient(weatherConfig);
        weatherApplier = new WeatherApplier(weatherConfig);
        nativeBridge = new NativeGameBridge(weatherConfig);
        manualLocationBuffer = weatherConfig.ManualLocation.Value;

        LogPatchTargets();
        harmony = new Harmony(PluginGuid);
        harmony.PatchAll();
        Logger.LogInfo("Harmony PatchAll 已执行。");

        EnsureRuntime();
        SceneManager.sceneLoaded += OnSceneLoaded;
        runtime?.StartPluginCoroutine(PluginFallbackLoop());
        WriteDebugLog("插件 Awake 完成。已注册场景监听和兜底刷新循环。");
        Logger.LogInfo("实时天气插件已加载。自动 IP 定位会访问外部定位/天气 API，可在配置或调试窗口中关闭。");
    }

    private void OnDestroy()
    {
        WriteDebugLog("插件 MonoBehaviour OnDestroy 被调用。保留隐藏运行时对象用于跨场景继续运行。");
        if (quitting)
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Instance = null;
        }
    }

    private void OnApplicationQuit()
    {
        quitting = true;
        WriteDebugLog("游戏退出，解除 Harmony patch。");
        harmony?.UnpatchSelf();
    }

    private void Update()
    {
        Tick();
        if (Time.unscaledTime >= nextFallbackTickLogTime)
        {
            nextFallbackTickLogTime = Time.unscaledTime + 30f;
            Logger.LogInfo($"插件 Update 兜底运行中。当前场景={SceneManager.GetActiveScene().name}, 天气状态={status}");
            WriteDebugLog($"插件 Update 心跳。场景={SceneManager.GetActiveScene().name}, 状态={status}, runtime={(runtime == null ? "null" : "alive")}");
        }
    }

    internal void Tick()
    {
        nativeBridge.Tick(lastWeather);
        weatherApplier.Apply(lastWeather);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        WriteDebugLog($"插件实例收到场景加载：{scene.name} ({mode})。");
        Logger.LogInfo($"场景已加载：{scene.name} ({mode})，触发实时天气扫描兜底。");
        EnsureRuntime();
        nativeBridge.ForceScan();
        TriggerRefreshFromGameReady();
    }

    internal static void BootstrapAfterSceneLoad(string reason)
    {
        WriteDebugLog($"BootstrapAfterSceneLoad: {reason}, Instance={(Instance == null ? "null" : "alive")}。");
        Instance?.EnsureRuntime();
    }

    private void EnsureRuntime()
    {
        if (runtime != null)
        {
            return;
        }

        var existing = Resources.FindObjectsOfTypeAll<WeatherRuntime>().FirstOrDefault(item => item != null);
        if (existing != null)
        {
            runtime = existing;
            runtime.Initialize(this);
            WriteDebugLog("复用已存在的 RealTimeWeatherRuntime。");
            return;
        }

        var runtimeObject = new GameObject("RealTimeWeatherRuntime");
        runtimeObject.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(runtimeObject);
        runtime = runtimeObject.AddComponent<WeatherRuntime>();
        runtime.Initialize(this);
        WriteDebugLog("已创建 DontDestroyOnLoad 隐藏 Runner。");
    }

    internal static void WriteDebugLog(string message)
    {
        try
        {
            File.AppendAllText(DebugLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private IEnumerator PluginFallbackLoop()
    {
        Logger.LogInfo("插件自身兜底刷新循环已启动。");
        yield return new WaitForSecondsRealtime(5f);

        while (true)
        {
            if (!fallbackRefreshStarted)
            {
                fallbackRefreshStarted = true;
                Logger.LogInfo("执行首次兜底天气刷新。若原生 UI Patch 未触发，也会继续获取天气并扫描对象。");
                yield return RefreshWeather();
            }

            nativeBridge.ForceScan();
            yield return new WaitForSecondsRealtime(30f);
        }
    }

    private void LogPatchTargets()
    {
        Logger.LogInfo($"Patch 目标 FacilityEnvironment.Setup: {typeof(FacilityEnvironment).GetMethod("Setup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null}");
        Logger.LogInfo($"Patch 目标 CurrentDateAndTimeUI.UpdateDateAndTime: {typeof(CurrentDateAndTimeUI).GetMethod("UpdateDateAndTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null}");
    }

    internal void TriggerRefreshFromGameReady()
    {
        EnsureRuntime();
        runtime?.StartPluginCoroutine(RefreshWeather());
    }

    internal void CaptureWindowViewService(object service)
    {
        nativeBridge.CaptureWindowViewService(service);
    }

    internal void DrawGui()
    {
    }

    internal void EnsureOverlay()
    {
    }

    internal IEnumerator RefreshLoop()
    {
        Logger.LogInfo("实时天气刷新循环已启动。");
        yield return new WaitForSeconds(2f);

        while (true)
        {
            if (weatherConfig.Enabled.Value)
            {
                EnsureRuntime();
                if (runtime != null)
                {
                    yield return runtime.RunNestedCoroutine(RefreshWeather());
                }
            }

            var minutes = Mathf.Max(1, weatherConfig.RefreshMinutes.Value);
            yield return new WaitForSeconds(minutes * 60f);
        }
    }

    internal IEnumerator RefreshWeather()
    {
        Logger.LogInfo("开始刷新实时天气。");
        status = "刷新中...";
        WeatherClient.Result? result = null;
        if (runtime == null)
        {
            EnsureRuntime();
        }

        if (runtime == null)
        {
            status = "刷新失败：运行时组件不存在";
            Logger.LogWarning(status);
            yield break;
        }

        yield return runtime.RunNestedCoroutine(weatherClient.Fetch(snapshot => result = snapshot));

        if (result is { Weather: not null })
        {
            lastWeather = result.Weather;
            CurrentUiWeatherString = UiWeatherString;
            status = $"{lastWeather.Location} / {lastWeather.Text} / {lastWeather.TemperatureCelsius}°C";
            nativeBridge.ApplyWeather(lastWeather);
            weatherApplier.RebindSceneObjects();
            Logger.LogInfo($"天气已更新：{status}");
            Logger.LogInfo($"昼夜状态：local={lastWeather.LocalTime:yyyy-MM-dd HH:mm}, sunrise={FormatOptionalTime(lastWeather.SunriseTime)}, sunset={FormatOptionalTime(lastWeather.SunsetTime)}, phase={lastWeather.SolarPhase}, lat={lastWeather.Latitude:0.####}, lon={lastWeather.Longitude:0.####}");
        }
        else
        {
            status = result?.Error ?? "刷新失败";
            Logger.LogWarning(status);
        }
    }

    private static string FormatOptionalTime(DateTime? value)
    {
        return value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm") : "fallback-solar-elevation";
    }

    private void DrawWindow(int id)
    {
        GUILayout.Label($"状态：{status}");
        GUILayout.Label(lastWeather == null ? "天气：无" : $"天气：{lastWeather.Text} ({lastWeather.Kind})");

        var enabled = GUILayout.Toggle(weatherConfig.Enabled.Value, "启用实时天气");
        if (enabled != weatherConfig.Enabled.Value)
        {
            weatherConfig.Enabled.Value = enabled;
            Config.Save();
        }

        var autoLocation = GUILayout.Toggle(weatherConfig.AutoIpLocation.Value, "自动 IP 定位");
        if (autoLocation != weatherConfig.AutoIpLocation.Value)
        {
            weatherConfig.AutoIpLocation.Value = autoLocation;
            Config.Save();
        }

        GUILayout.Label("手动城市/位置（关闭自动定位时使用）：");
        manualLocationBuffer = GUILayout.TextField(manualLocationBuffer, 64);
        if (GUILayout.Button("保存手动位置"))
        {
            weatherConfig.ManualLocation.Value = manualLocationBuffer.Trim();
            Config.Save();
        }

        GUILayout.Label($"刷新间隔：{weatherConfig.RefreshMinutes.Value} 分钟");
        var refresh = Mathf.RoundToInt(GUILayout.HorizontalSlider(weatherConfig.RefreshMinutes.Value, 1, 180));
        if (refresh != weatherConfig.RefreshMinutes.Value)
        {
            weatherConfig.RefreshMinutes.Value = refresh;
            Config.Save();
        }

        GUILayout.Label($"效果强度：{weatherConfig.IntensityScale.Value:0.00}");
        var intensity = GUILayout.HorizontalSlider(weatherConfig.IntensityScale.Value, 0f, 2f);
        if (Math.Abs(intensity - weatherConfig.IntensityScale.Value) > 0.01f)
        {
            weatherConfig.IntensityScale.Value = intensity;
            Config.Save();
        }

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("立即刷新"))
        {
            StartCoroutine(RefreshWeather());
        }

        if (GUILayout.Button("重新绑定场景"))
        {
            weatherApplier.RebindSceneObjects();
        }
        GUILayout.EndHorizontal();

        GUILayout.Label("说明：天气使用无需 Key 的公共 API。自动 IP 定位会访问 ipwho.is；天气和城市搜索会访问 Open-Meteo。", GUILayout.ExpandHeight(true));
        GUI.DragWindow();
    }
}

internal sealed class WeatherRuntime : MonoBehaviour
{
    private RealTimeWeatherPlugin? plugin;
    private bool started;

    internal void Initialize(RealTimeWeatherPlugin plugin)
    {
        this.plugin = plugin;
        RealTimeWeatherPlugin.Log.LogInfo("实时天气运行时组件已启动。");
        RealTimeWeatherPlugin.WriteDebugLog("WeatherRuntime.Initialize 已调用。");
        if (!started)
        {
            started = true;
            StartCoroutine(StartAfterFirstFrame());
        }
    }

    internal void StartPluginCoroutine(IEnumerator routine)
    {
        StartCoroutine(routine);
    }

    internal Coroutine RunNestedCoroutine(IEnumerator routine)
    {
        return StartCoroutine(routine);
    }

    private IEnumerator StartAfterFirstFrame()
    {
        yield return null;
        plugin?.EnsureOverlay();
        if (plugin != null)
        {
            StartCoroutine(plugin.RefreshLoop());
        }
    }

    private void Update()
    {
        plugin?.Tick();
    }

    private void OnGUI()
    {
        plugin?.DrawGui();
    }
}

internal sealed class WeatherConfig
{
    internal ConfigEntry<bool> Enabled { get; }
    internal ConfigEntry<bool> AutoIpLocation { get; }
    internal ConfigEntry<string> ManualLocation { get; }
    internal ConfigEntry<int> RefreshMinutes { get; }
    internal ConfigEntry<int> TimeoutSeconds { get; }
    internal ConfigEntry<float> IntensityScale { get; }
    internal ConfigEntry<bool> InjectNativeDateTimeUI { get; }
    internal ConfigEntry<bool> UseNativeWindowWeather { get; }
    internal ConfigEntry<bool> ShowDebugWindow { get; }
    internal ConfigEntry<KeyboardShortcut> ToggleWindowKey { get; }

    internal WeatherConfig(ConfigFile config)
    {
        Enabled = config.Bind("General", "Enabled", true, "启用实时天气同步。");
        AutoIpLocation = config.Bind("Location", "AutoIpLocation", false, "使用 IP 自动定位。启用后会访问外部定位/天气 API。");
        ManualLocation = config.Bind("Location", "ManualLocation", "beijing", "关闭自动定位时用于公共地理编码 API 的城市名、拼音或经纬度，格式可为 39.9,116.4。");
        RefreshMinutes = config.Bind("Weather", "RefreshMinutes", 30, new ConfigDescription("天气刷新间隔（分钟）。", new AcceptableValueRange<int>(1, 180)));
        TimeoutSeconds = config.Bind("Weather", "TimeoutSeconds", 5, new ConfigDescription("网络请求超时（秒）。", new AcceptableValueRange<int>(2, 15)));
        IntensityScale = config.Bind("Effects", "IntensityScale", 1f, new ConfigDescription("fallback 天气效果强度倍率。", new AcceptableValueRange<float>(0f, 2f)));
        InjectNativeDateTimeUI = config.Bind("Native", "InjectNativeDateTimeUI", true, "把天气信息追加到游戏现有日期/时间 UI。参考 RealTimeWeatherMod 的 UI 注入方式。");
        UseNativeWindowWeather = config.Bind("Native", "UseNativeWindowWeather", true, "尝试调用游戏原生 WindowViewService.ChangeWeatherAndTime 切换窗口天气/时间。");
        ShowDebugWindow = config.Bind("Debug", "ShowDebugWindow", false, "显示 F8 可切换的调试设置窗口。默认关闭，主要用游戏原生 UI 显示天气。 ");
        ToggleWindowKey = config.Bind("Debug", "ToggleWindowKey", new KeyboardShortcut(KeyCode.F8), "切换实时天气调试窗口的快捷键。");
    }
}

internal sealed class WeatherClient
{
    private readonly WeatherConfig config;

    internal WeatherClient(WeatherConfig config)
    {
        this.config = config;
    }

    internal IEnumerator Fetch(Action<Result> done)
    {
        GeoPoint? point = null;
        string? error = null;

        if (config.AutoIpLocation.Value)
        {
            yield return FetchIpLocation((value, message) =>
            {
                point = value;
                error = message;
            });
        }
        else
        {
            var location = config.ManualLocation.Value.Trim();
            if (string.IsNullOrWhiteSpace(location))
            {
                done(Result.Fail("未配置手动位置。"));
                yield break;
            }

            if (TryParseLatLon(location, out var manualPoint))
            {
                point = manualPoint;
            }
            else
            {
                yield return FetchGeocoding(location, (value, message) =>
                {
                    point = value;
                    error = message;
                });
            }
        }

        if (point == null)
        {
            done(Result.Fail(error ?? "定位失败。"));
            yield break;
        }

        yield return FetchOpenMeteo(point, done);
    }

    private IEnumerator FetchIpLocation(Action<GeoPoint?, string?> done)
    {
        using var request = UnityWebRequest.Get("https://ipwho.is/");
        request.timeout = Mathf.Clamp(config.TimeoutSeconds.Value, 2, 15);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            done(null, $"IP 定位失败：{request.error}");
            yield break;
        }

        done(PublicApiParser.ParseIpWhoIs(request.downloadHandler.text), "IP 定位响应解析失败。");
    }

    private IEnumerator FetchGeocoding(string location, Action<GeoPoint?, string?> done)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search" +
                  $"?name={UnityWebRequest.EscapeURL(location)}" +
                  "&count=1&language=zh&format=json";
        using var request = UnityWebRequest.Get(url);
        request.timeout = Mathf.Clamp(config.TimeoutSeconds.Value, 2, 15);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            done(null, $"城市定位失败：{request.error}");
            yield break;
        }

        done(PublicApiParser.ParseOpenMeteoGeocoding(request.downloadHandler.text), "城市定位响应解析失败。");
    }

    private IEnumerator FetchOpenMeteo(GeoPoint point, Action<Result> done)
    {
        var url = "https://api.open-meteo.com/v1/forecast" +
                  $"?latitude={point.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  $"&longitude={point.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  "&current=temperature_2m,weather_code&daily=sunrise,sunset&timezone=auto";
        using var request = UnityWebRequest.Get(url);
        request.timeout = Mathf.Clamp(config.TimeoutSeconds.Value, 2, 15);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            done(Result.Fail($"天气请求失败：{request.error}"));
            yield break;
        }

        var snapshot = PublicApiParser.ParseOpenMeteoWeather(request.downloadHandler.text, point);
        done(snapshot == null ? Result.Fail("天气响应解析失败。") : Result.Ok(snapshot));
    }

    private static bool TryParseLatLon(string location, out GeoPoint point)
    {
        point = null!;
        var parts = location.Split(',');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon))
        {
            return false;
        }

        point = new GeoPoint(lat, lon, location);
        return true;
    }

    internal sealed class Result
    {
        internal WeatherSnapshot? Weather { get; }
        internal string? Error { get; }

        private Result(WeatherSnapshot? weather, string? error)
        {
            Weather = weather;
            Error = error;
        }

        internal static Result Ok(WeatherSnapshot weather) => new(weather, null);
        internal static Result Fail(string error) => new(null, error);
    }
}

internal sealed class WeatherSnapshot
{
    internal string Location { get; }
    internal string Text { get; }
    internal int Code { get; }
    internal int TemperatureCelsius { get; }
    internal WeatherKind Kind { get; }
    internal double Latitude { get; }
    internal double Longitude { get; }
    internal DateTime LocalTime { get; }
    internal DateTime? SunriseTime { get; }
    internal DateTime? SunsetTime { get; }
    internal SolarPhase SolarPhase { get; }

    internal WeatherSnapshot(string location, string text, int code, int temperatureCelsius, double latitude, double longitude, DateTime localTime, DateTime? sunriseTime, DateTime? sunsetTime)
    {
        Location = location;
        Text = text;
        Code = code;
        TemperatureCelsius = temperatureCelsius;
        Kind = WeatherClassifier.Classify(code, text);
        Latitude = latitude;
        Longitude = longitude;
        LocalTime = localTime;
        SunriseTime = sunriseTime;
        SunsetTime = sunsetTime;
        SolarPhase = sunriseTime.HasValue && sunsetTime.HasValue
            ? SolarPhaseClassifier.Classify(localTime, sunriseTime.Value, sunsetTime.Value)
            : SolarPhaseClassifier.ClassifyBySunElevation(latitude, longitude, DateTime.UtcNow);
    }
}

internal enum SolarPhase
{
    Day,
    Sunset,
    Night
}

internal enum WeatherKind
{
    Clear,
    Cloudy,
    Rain,
    Snow,
    Fog,
    Storm,
    Unknown
}

internal static class SolarPhaseClassifier
{
    internal static SolarPhase Classify(DateTime localTime, DateTime sunriseTime, DateTime sunsetTime)
    {
        if (localTime >= sunriseTime && localTime < sunsetTime.AddMinutes(-30d))
        {
            return SolarPhase.Day;
        }

        if (localTime >= sunsetTime.AddMinutes(-30d) && localTime < sunsetTime.AddMinutes(30d))
        {
            return SolarPhase.Sunset;
        }

        return SolarPhase.Night;
    }

    internal static SolarPhase ClassifyBySunElevation(double latitude, double longitude, DateTime utcTime)
    {
        var elevation = CalculateSolarElevation(latitude, longitude, utcTime);
        if (elevation > 6d)
        {
            return SolarPhase.Day;
        }

        return elevation >= -6d ? SolarPhase.Sunset : SolarPhase.Night;
    }

    private static double CalculateSolarElevation(double latitude, double longitude, DateTime utcTime)
    {
        var dayOfYear = utcTime.DayOfYear;
        var hour = utcTime.Hour + utcTime.Minute / 60d + utcTime.Second / 3600d;
        var gamma = 2d * Math.PI / 365d * (dayOfYear - 1 + (hour - 12d) / 24d);
        var declination = 0.006918d
            - 0.399912d * Math.Cos(gamma)
            + 0.070257d * Math.Sin(gamma)
            - 0.006758d * Math.Cos(2d * gamma)
            + 0.000907d * Math.Sin(2d * gamma)
            - 0.002697d * Math.Cos(3d * gamma)
            + 0.00148d * Math.Sin(3d * gamma);
        var equationOfTime = 229.18d * (0.000075d
            + 0.001868d * Math.Cos(gamma)
            - 0.032077d * Math.Sin(gamma)
            - 0.014615d * Math.Cos(2d * gamma)
            - 0.040849d * Math.Sin(2d * gamma));
        var trueSolarTime = (hour * 60d + equationOfTime + 4d * longitude) % 1440d;
        if (trueSolarTime < 0d)
        {
            trueSolarTime += 1440d;
        }

        var hourAngle = trueSolarTime / 4d - 180d;
        var latitudeRad = latitude * Math.PI / 180d;
        var hourAngleRad = hourAngle * Math.PI / 180d;
        var cosZenith = Math.Sin(latitudeRad) * Math.Sin(declination) + Math.Cos(latitudeRad) * Math.Cos(declination) * Math.Cos(hourAngleRad);
        cosZenith = Math.Max(-1d, Math.Min(1d, cosZenith));
        return 90d - Math.Acos(cosZenith) * 180d / Math.PI;
    }
}

internal static class WeatherClassifier
{
    internal static WeatherKind Classify(int code, string text)
    {
        if (code is 95 or 96 or 99)
        {
            return WeatherKind.Storm;
        }

        if (code is 51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82)
        {
            return WeatherKind.Rain;
        }

        if (code is 71 or 73 or 75 or 77 or 85 or 86)
        {
            return WeatherKind.Snow;
        }

        if (code is 45 or 48)
        {
            return WeatherKind.Fog;
        }

        if (code is 2 or 3)
        {
            return WeatherKind.Cloudy;
        }

        if (code is 0 or 1)
        {
            return WeatherKind.Clear;
        }

        if (text.Contains("雪")) return WeatherKind.Snow;
        if (text.Contains("雷")) return WeatherKind.Storm;
        if (text.Contains("雨")) return WeatherKind.Rain;
        if (text.Contains("雾") || text.Contains("霾")) return WeatherKind.Fog;
        if (text.Contains("云") || text.Contains("阴")) return WeatherKind.Cloudy;
        if (text.Contains("晴")) return WeatherKind.Clear;
        return WeatherKind.Unknown;
    }
}

internal sealed class GeoPoint
{
    internal double Latitude { get; }
    internal double Longitude { get; }
    internal string Name { get; }

    internal GeoPoint(double latitude, double longitude, string name)
    {
        Latitude = latitude;
        Longitude = longitude;
        Name = name;
    }
}

internal static class PublicApiParser
{
    internal static GeoPoint? ParseIpWhoIs(string json)
    {
        try
        {
            var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (root == null || !root.GetBool("success", false))
            {
                return null;
            }

            var city = root.GetString("city", "当前位置");
            var country = root.GetString("country", string.Empty);
            var name = string.IsNullOrWhiteSpace(country) ? city : $"{city}, {country}";
            return new GeoPoint(root.GetDouble("latitude", 0d), root.GetDouble("longitude", 0d), name);
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"解析 IP 定位 JSON 失败：{ex.Message}");
            return null;
        }
    }

    internal static GeoPoint? ParseOpenMeteoGeocoding(string json)
    {
        try
        {
            var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
            var results = root?.GetList("results");
            var first = results?.Count > 0 ? results[0] as Dictionary<string, object> : null;
            if (first == null)
            {
                return null;
            }

            var name = first.GetString("name", "手动位置");
            var admin = first.GetString("admin1", string.Empty);
            var country = first.GetString("country", string.Empty);
            var displayName = string.Join(", ", new[] { name, admin, country }.Where(part => !string.IsNullOrWhiteSpace(part)));
            return new GeoPoint(first.GetDouble("latitude", 0d), first.GetDouble("longitude", 0d), displayName);
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"解析城市定位 JSON 失败：{ex.Message}");
            return null;
        }
    }

    internal static WeatherSnapshot? ParseOpenMeteoWeather(string json, GeoPoint point)
    {
        try
        {
            var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
            var current = root?.GetDict("current");
            if (current == null)
            {
                return null;
            }

            var code = current.GetInt("weather_code", -1);
            var temperature = Mathf.RoundToInt((float)current.GetDouble("temperature_2m", 0d));
            var localTime = ParseLocalTime(current.GetString("time", string.Empty), DateTime.Now);
            var daily = root?.GetDict("daily");
            var sunrise = ParseFirstDailyTime(daily, "sunrise");
            var sunset = ParseFirstDailyTime(daily, "sunset");
            return new WeatherSnapshot(point.Name, OpenMeteoWeatherText(code), code, temperature, point.Latitude, point.Longitude, localTime, sunrise, sunset);
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"解析天气 JSON 失败：{ex.Message}");
            return null;
        }
    }

    private static DateTime ParseLocalTime(string value, DateTime fallback)
    {
        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;
    }

    private static DateTime? ParseFirstDailyTime(Dictionary<string, object>? daily, string key)
    {
        var values = daily?.GetList(key);
        if (values == null || values.Count == 0 || values[0] == null)
        {
            return null;
        }

        var value = Convert.ToString(values[0], System.Globalization.CultureInfo.InvariantCulture);
        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static string OpenMeteoWeatherText(int code)
    {
        return code switch
        {
            0 => "晴",
            1 => "大部晴朗",
            2 => "多云",
            3 => "阴",
            45 or 48 => "雾",
            51 or 53 or 55 => "毛毛雨",
            56 or 57 => "冻毛毛雨",
            61 or 63 or 65 => "雨",
            66 or 67 => "冻雨",
            71 or 73 or 75 => "雪",
            77 => "雪粒",
            80 or 81 or 82 => "阵雨",
            85 or 86 => "阵雪",
            95 or 96 or 99 => "雷暴",
            _ => "未知天气"
        };
    }
}

internal sealed class NativeGameBridge
{
    private readonly WeatherConfig config;
    private readonly List<Text> dateTimeTexts = new();
    private readonly List<TMP_Text> dateTimeTmpTexts = new();
    private readonly Dictionary<Text, string> originalTexts = new();
    private readonly Dictionary<TMP_Text, string> originalTmpTexts = new();
    private bool loggedMissingUi;
    private bool loggedMissingService;
    private object? windowViewService;
    private MethodInfo? changeWeatherAndTimeMethod;
    private float nextScanTime;
    private float nextUiUpdateTime;
    private bool loggedFirstUiInjection;
    private string? lastAppliedEnvironmentKey;

    internal NativeGameBridge(WeatherConfig config)
    {
        this.config = config;
    }

    internal void ForceScan()
    {
        nextScanTime = 0f;
        ScanNativeObjects();
    }

    internal void Tick(WeatherSnapshot? weather)
    {
        if (Time.unscaledTime >= nextScanTime)
        {
            ScanNativeObjects();
        }

        if (weather != null && Time.unscaledTime >= nextUiUpdateTime)
        {
            nextUiUpdateTime = Time.unscaledTime + 1f;
        }
    }

    internal void CaptureWindowViewService(object service)
    {
        var method = service.GetType().GetMethod("ChangeWeatherAndTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null)
        {
            return;
        }

        windowViewService = service;
        changeWeatherAndTimeMethod = method;
        RealTimeWeatherPlugin.Log.LogInfo("已通过 FacilityEnvironment.Setup 捕获原生 WindowViewService.ChangeWeatherAndTime。");
    }

    internal void ApplyWeather(WeatherSnapshot weather)
    {
        var environmentKey = $"{weather.Kind}:{weather.SolarPhase}";
        if (!config.UseNativeWindowWeather.Value || lastAppliedEnvironmentKey == environmentKey)
        {
            return;
        }

        ScanNativeObjects();
        if (windowViewService == null || changeWeatherAndTimeMethod == null)
        {
            RealTimeWeatherPlugin.Log.LogInfo("未找到 WindowViewService.ChangeWeatherAndTime，暂时使用 fallback 效果。");
            return;
        }

        foreach (var candidate in GetWindowViewCandidates(weather))
        {
            try
            {
                changeWeatherAndTimeMethod.Invoke(windowViewService, new object[] { candidate });
                lastAppliedEnvironmentKey = environmentKey;
                RealTimeWeatherPlugin.Log.LogInfo($"已调用原生 ChangeWeatherAndTime：{candidate} ({weather.Text}, {weather.SolarPhase})");
                return;
            }
            catch (TargetInvocationException ex)
            {
                RealTimeWeatherPlugin.Log.LogDebug($"ChangeWeatherAndTime 候选 {candidate} 调用失败：{ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                RealTimeWeatherPlugin.Log.LogDebug($"ChangeWeatherAndTime 候选 {candidate} 调用失败：{ex.Message}");
            }
        }

        RealTimeWeatherPlugin.Log.LogInfo($"未能把 {environmentKey} 映射到原生 WindowViewType，继续使用 fallback 效果。");
    }

    private void ScanNativeObjects()
    {
        nextScanTime = Time.unscaledTime + 10f;
        ScanWindowViewService();
        ScanDateTimeTexts();

        if (!loggedMissingService && windowViewService == null)
        {
            loggedMissingService = true;
            RealTimeWeatherPlugin.Log.LogInfo("尚未在当前场景找到 Bulbul.WindowViewService，会继续扫描。");
        }

        if (!loggedMissingUi && dateTimeTexts.Count == 0 && dateTimeTmpTexts.Count == 0)
        {
            loggedMissingUi = true;
            RealTimeWeatherPlugin.Log.LogInfo("尚未在当前场景找到日期时间 UI 文本，会继续扫描。若一直没有，建议使用 UnityExplorer MCP 查看实际 UI 对象。");
        }
    }

    private void ScanWindowViewService()
    {
        if (windowViewService != null && changeWeatherAndTimeMethod != null)
        {
            return;
        }

        foreach (var component in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (component == null)
            {
                continue;
            }

            var type = component.GetType();
            if (type.FullName != "Bulbul.WindowViewService")
            {
                continue;
            }

            var method = type.GetMethod("ChangeWeatherAndTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                continue;
            }

            windowViewService = component;
            changeWeatherAndTimeMethod = method;
            RealTimeWeatherPlugin.Log.LogInfo("已绑定原生 Bulbul.WindowViewService.ChangeWeatherAndTime。");
            return;
        }
    }

    private void ScanDateTimeTexts()
    {
        if (!config.InjectNativeDateTimeUI.Value)
        {
            return;
        }

        foreach (var component in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (component == null || component.GetType().FullName != "Bulbul.CurrentDateAndTimeUI")
            {
                continue;
            }

            foreach (var text in component.GetComponentsInChildren<Text>(true))
            {
                if (IsDateText(text.transform))
                {
                    AddDateTimeText(text);
                }
            }

            foreach (var text in component.GetComponentsInChildren<TMP_Text>(true))
            {
                if (IsDateText(text.transform))
                {
                    AddDateTimeText(text);
                }
            }
        }

        if (dateTimeTexts.Count == 0 && dateTimeTmpTexts.Count == 0)
        {
            foreach (var text in Resources.FindObjectsOfTypeAll<Text>())
            {
                if (!IsSceneObject(text.gameObject))
                {
                    continue;
                }

                if (IsDateText(text.transform))
                {
                    AddDateTimeText(text);
                }
            }

            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (!IsSceneObject(text.gameObject))
                {
                    continue;
                }

                if (IsDateText(text.transform))
                {
                    AddDateTimeText(text);
                }
            }
        }
    }

    private void AddDateTimeText(Text text)
    {
        if (dateTimeTexts.Contains(text))
        {
            return;
        }

        dateTimeTexts.Add(text);
        originalTexts[text] = StripWeatherSuffix(text.text);
        RealTimeWeatherPlugin.Log.LogInfo($"已绑定原生日期时间 UI(Text)：{GetPath(text.transform)}");
    }

    private void AddDateTimeText(TMP_Text text)
    {
        if (dateTimeTmpTexts.Contains(text))
        {
            return;
        }

        dateTimeTmpTexts.Add(text);
        originalTmpTexts[text] = StripWeatherSuffix(text.text);
        RealTimeWeatherPlugin.Log.LogInfo($"已绑定原生日期时间 UI(TMP)：{GetPath(text.transform)}");
    }

    private void InjectWeatherText(WeatherSnapshot weather)
    {
        if (!config.InjectNativeDateTimeUI.Value || (dateTimeTexts.Count == 0 && dateTimeTmpTexts.Count == 0))
        {
            return;
        }

        nextUiUpdateTime = Time.unscaledTime + 1f;
        var suffix = $" | {weather.Text} {weather.TemperatureCelsius}°C";
        var injectedCount = 0;
        for (var i = dateTimeTexts.Count - 1; i >= 0; i--)
        {
            var text = dateTimeTexts[i];
            if (text == null)
            {
                dateTimeTexts.RemoveAt(i);
                continue;
            }

            var baseText = StripWeatherSuffix(text.text);
            if (!originalTexts.ContainsKey(text) || !string.IsNullOrWhiteSpace(baseText))
            {
                originalTexts[text] = baseText;
            }

            text.text = originalTexts[text] + suffix;
            injectedCount++;
        }

        for (var i = dateTimeTmpTexts.Count - 1; i >= 0; i--)
        {
            var text = dateTimeTmpTexts[i];
            if (text == null)
            {
                dateTimeTmpTexts.RemoveAt(i);
                continue;
            }

            var baseText = StripWeatherSuffix(text.text);
            if (!originalTmpTexts.ContainsKey(text) || !string.IsNullOrWhiteSpace(baseText))
            {
                originalTmpTexts[text] = baseText;
            }

            text.text = originalTmpTexts[text] + suffix;
            injectedCount++;
        }

        if (!loggedFirstUiInjection && injectedCount > 0)
        {
            loggedFirstUiInjection = true;
            RealTimeWeatherPlugin.Log.LogInfo($"已向 {injectedCount} 个日期时间 UI 文本写入天气：{suffix}");
        }
    }

    private IEnumerable<object> GetWindowViewCandidates(WeatherSnapshot weather)
    {
        var enumType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Bulbul.WindowViewType", false))
            .FirstOrDefault(type => type != null);
        if (enumType == null || !enumType.IsEnum)
        {
            yield break;
        }

        var keywords = GetEnvironmentKeywords(weather).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var names = Enum.GetNames(enumType);
        foreach (var keyword in keywords)
        {
            foreach (var name in names.Where(name => name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                yield return Enum.Parse(enumType, name);
            }
        }
    }

    private static IEnumerable<string> GetEnvironmentKeywords(WeatherSnapshot weather)
    {
        if (weather.SolarPhase == SolarPhase.Sunset)
        {
            yield return "Sunset";
            yield return "Evening";
            yield return "Dusk";
        }
        else if (weather.SolarPhase == SolarPhase.Night)
        {
            yield return "Night";
        }

        foreach (var keyword in GetWeatherKeywords(weather.Kind))
        {
            yield return keyword;
        }

        if (weather.SolarPhase == SolarPhase.Day)
        {
            yield return "Day";
            yield return "Sun";
            yield return "Sunny";
            yield return "Morning";
        }
        else if (weather.SolarPhase == SolarPhase.Sunset)
        {
            yield return "Day";
            yield return "Night";
        }
        else
        {
            yield return "Day";
        }
    }

    private static IEnumerable<string> GetWeatherKeywords(WeatherKind kind)
    {
        return kind switch
        {
            WeatherKind.Clear => new[] { "Sun", "Sunny" },
            WeatherKind.Cloudy => new[] { "Cloud", "Cloudy" },
            WeatherKind.Rain => new[] { "Rain", "Rainy" },
            WeatherKind.Snow => new[] { "Snow", "Snowy" },
            WeatherKind.Fog => new[] { "Fog", "Cloud" },
            WeatherKind.Storm => new[] { "Storm", "Thunder", "Rain" },
            _ => new[] { "Day", "Night" }
        };
    }

    private static string StripWeatherSuffix(string value)
    {
        var pipeIndex = value.IndexOf(" | ", StringComparison.Ordinal);
        if (pipeIndex >= 0)
        {
            return value.Substring(0, pipeIndex);
        }

        var lineIndex = value.IndexOf("\n", StringComparison.Ordinal);
        return lineIndex >= 0 ? value.Substring(0, lineIndex) : value;
    }

    private static bool IsDateText(Transform transform)
    {
        return transform.name.Equals("DateText", StringComparison.OrdinalIgnoreCase)
            || GetPath(transform).EndsWith("/DateText", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSceneObject(GameObject gameObject)
    {
        return gameObject.scene.IsValid() && gameObject.hideFlags == HideFlags.None;
    }

    private static string GetPath(Transform transform)
    {
        var parts = new Stack<string>();
        var current = transform;
        while (current != null)
        {
            parts.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", parts.ToArray());
    }
}

internal sealed class WeatherApplier
{
    private readonly WeatherConfig config;
    private readonly List<ParticleSystem> rainParticles = new();
    private readonly List<ParticleSystem> snowParticles = new();
    private readonly List<AudioSource> rainAudioSources = new();
    private readonly List<Light> lights = new();
    private float nextRebindTime;

    internal WeatherApplier(WeatherConfig config)
    {
        this.config = config;
    }

    internal void Apply(WeatherSnapshot? weather)
    {
        if (Time.unscaledTime >= nextRebindTime)
        {
            RebindSceneObjects();
        }

        if (weather == null || !config.Enabled.Value)
        {
            return;
        }

        var rain = weather.Kind is WeatherKind.Rain or WeatherKind.Storm;
        var snow = weather.Kind == WeatherKind.Snow;
        SetParticles(rainParticles, rain, weather.Kind == WeatherKind.Storm ? 1.4f : 1f);
        SetParticles(snowParticles, snow, 0.8f);
        SetRainAudio(rain);
        SetLights(weather.Kind);
    }

    internal void RebindSceneObjects()
    {
        nextRebindTime = Time.unscaledTime + 30f;
        rainParticles.Clear();
        snowParticles.Clear();
        rainAudioSources.Clear();
        lights.Clear();

        foreach (var particle in Resources.FindObjectsOfTypeAll<ParticleSystem>())
        {
            if (!IsSceneObject(particle.gameObject))
            {
                continue;
            }

            var name = particle.gameObject.name.ToLowerInvariant();
            if (name.Contains("rain") || name.Contains("雨"))
            {
                rainParticles.Add(particle);
            }
            else if (name.Contains("snow") || name.Contains("雪"))
            {
                snowParticles.Add(particle);
            }
        }

        foreach (var source in Resources.FindObjectsOfTypeAll<AudioSource>())
        {
            if (!IsSceneObject(source.gameObject))
            {
                continue;
            }

            var name = source.gameObject.name.ToLowerInvariant();
            if (name.Contains("rain") || name.Contains("雨"))
            {
                rainAudioSources.Add(source);
            }
        }

        foreach (var light in Resources.FindObjectsOfTypeAll<Light>())
        {
            if (IsSceneObject(light.gameObject))
            {
                lights.Add(light);
            }
        }
    }

    private void SetParticles(IEnumerable<ParticleSystem> particles, bool active, float multiplier)
    {
        foreach (var particle in particles)
        {
            var emission = particle.emission;
            emission.enabled = active;
            emission.rateOverTimeMultiplier = Mathf.Max(0.01f, config.IntensityScale.Value * multiplier);

            if (active && !particle.isPlaying)
            {
                particle.Play(true);
            }
            else if (!active && particle.isPlaying)
            {
                particle.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }

    private void SetRainAudio(bool active)
    {
        foreach (var source in rainAudioSources)
        {
            if (active)
            {
                source.volume = Mathf.Clamp01(config.IntensityScale.Value);
                if (!source.isPlaying)
                {
                    source.Play();
                }
            }
            else if (source.isPlaying)
            {
                source.Stop();
            }
        }
    }

    private void SetLights(WeatherKind kind)
    {
        var target = kind switch
        {
            WeatherKind.Clear => 1f,
            WeatherKind.Cloudy => 0.85f,
            WeatherKind.Rain => 0.7f,
            WeatherKind.Snow => 0.8f,
            WeatherKind.Fog => 0.65f,
            WeatherKind.Storm => 0.55f,
            _ => 0.9f
        };

        foreach (var light in lights)
        {
            if (light.type is LightType.Directional or LightType.Spot)
            {
                light.intensity = Mathf.Lerp(light.intensity, target, Time.deltaTime * 0.2f);
            }
        }
    }

    private static bool IsSceneObject(GameObject gameObject)
    {
        return gameObject.scene.IsValid() && gameObject.hideFlags == HideFlags.None;
    }
}

[HarmonyPatch]
internal static class SettingsMenuPatches
{
    private static float lastLogTime;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameObject), nameof(GameObject.SetActive))]
    private static void GameObjectSetActivePostfix(GameObject __instance, bool value)
    {
        if (!value || RealTimeWeatherPlugin.Instance == null)
        {
            return;
        }

        var name = __instance.name.ToLowerInvariant();
        if ((name.Contains("setting") || name.Contains("option") || name.Contains("general") || name.Contains("设置") || name.Contains("常规")) && Time.unscaledTime - lastLogTime > 5f)
        {
            lastLogTime = Time.unscaledTime;
            RealTimeWeatherPlugin.Log.LogInfo($"检测到可能的设置菜单对象：{GetPath(__instance.transform)}。当前版本使用 F8 调试窗口调整实时天气参数。");
        }
    }

    private static string GetPath(Transform transform)
    {
        var parts = new Stack<string>();
        var current = transform;
        while (current != null)
        {
            parts.Push(current.name);
            current = current.parent;
        }

        return string.Join("/", parts.ToArray());
    }
}

internal static class DictionaryExtensions
{
    internal static Dictionary<string, object>? GetDict(this Dictionary<string, object> dict, string key)
    {
        return dict.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;
    }

    internal static List<object>? GetList(this Dictionary<string, object> dict, string key)
    {
        return dict.TryGetValue(key, out var value) ? value as List<object> : null;
    }

    internal static string GetString(this Dictionary<string, object> dict, string key, string fallback)
    {
        return dict.TryGetValue(key, out var value) ? Convert.ToString(value) ?? fallback : fallback;
    }

    internal static int GetInt(this Dictionary<string, object> dict, string key, int fallback)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value switch
        {
            long l => (int)l,
            int i => i,
            double d => (int)d,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => fallback
        };
    }

    internal static double GetDouble(this Dictionary<string, object> dict, string key, double fallback)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value switch
        {
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback
        };
    }

    internal static bool GetBool(this Dictionary<string, object> dict, string key, bool fallback)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => fallback
        };
    }
}
