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
    internal static GameLanguage CurrentLanguage => GameLanguageProvider.CurrentLanguage;
    private static readonly string DebugLogPath = Path.Combine(Paths.BepInExRootPath, "RealTimeWeatherForChill.debug.log");

    private Harmony? harmony;
    private WeatherConfig weatherConfig = null!;
    private WeatherClient weatherClient = null!;
    private WeatherApplier weatherApplier = null!;
    private NativeGameBridge nativeBridge = null!;
    internal WeatherSnapshot? LastWeather => lastWeather;
    internal string UiWeatherString => lastWeather == null ? string.Empty : $"{WeatherLocalizer.WeatherText(lastWeather.Code, CurrentLanguage)} {lastWeather.TemperatureCelsius}°C";
    private WeatherSnapshot? lastWeather;
    private WeatherRuntime? runtime;
    private string status = "未刷新";
    private bool fallbackRefreshStarted;
    private bool refreshInProgress;
    private bool quitting;
    private float nextFallbackTickLogTime;
    private float nextAllowedRefreshTime;

    // Cache fields
    private static readonly string CacheFilePath = Path.Combine(Paths.ConfigPath, "panda.chillwithyou.realtimeweather.cache.json");
    private Dictionary<string, object>? cachedForecastRoot;
    private GeoPoint? cachedPoint;
    private DateTime? cachedFetchedAtUtc;
    private float nextCacheUpdateTime;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        weatherConfig = new WeatherConfig(Config);
        weatherClient = new WeatherClient(weatherConfig);
        weatherApplier = new WeatherApplier(weatherConfig);
        nativeBridge = new NativeGameBridge(weatherConfig);
        RestoreCachedWeather();

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
        GameLanguageProvider.Tick();
        UpdateWeatherFromCache();
        RefreshLocalizedWeatherString();
        nativeBridge.Tick(lastWeather);
        weatherApplier.Apply(lastWeather);
    }

    internal static void NotifyGameLanguageChanged(object? languageValue)
    {
        GameLanguageProvider.SetFromGameValue(languageValue);
        if (!ReferenceEquals(Instance, null))
        {
            Instance.RefreshLocalizedWeatherString();
        }
    }

    internal void RefreshLocalizedWeatherString()
    {
        if (lastWeather != null)
        {
            CurrentUiWeatherString = UiWeatherString;
            status = $"{lastWeather.Location} / {CurrentUiWeatherString}";
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        WriteDebugLog($"插件实例收到场景加载：{scene.name} ({mode})。");
        Logger.LogInfo($"场景已加载：{scene.name} ({mode})，触发实时天气扫描兜底。");
        EnsureRuntime();
        nativeBridge.ForceScan();
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
                Logger.LogInfo("执行首次兜底场景扫描。天气刷新等待游戏主 UI 就绪后触发。");
            }

            nativeBridge.ForceScan();
            yield return new WaitForSecondsRealtime(30f);
        }
    }

    private void LogPatchTargets()
    {
        Logger.LogInfo($"Patch 目标 FacilityEnvironment.Setup: {AccessTools.TypeByName("Bulbul.FacilityEnvironment")?.GetMethod("Setup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null}");
        Logger.LogInfo($"Patch 目标 CurrentDateAndTimeUI.UpdateDateAndTime: {AccessTools.TypeByName("Bulbul.CurrentDateAndTimeUI")?.GetMethod("UpdateDateAndTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null}");
    }

    internal void TriggerRefreshFromGameReady()
    {
        StartRefreshIfNeeded(force: true);
    }

    internal void CaptureWindowViewService(object service)
    {
        nativeBridge.CaptureWindowViewService(service);
    }

    private void StartRefreshIfNeeded(bool force)
    {
        if (!weatherConfig.Enabled.Value || refreshInProgress)
        {
            return;
        }

        bool needFetch = force || lastWeather == null || cachedForecastRoot == null;
        if (!needFetch && cachedForecastRoot != null)
        {
            var lastForecastTime = GetLastForecastTime(cachedForecastRoot);
            if (lastForecastTime.HasValue && DateTime.Now.Date > lastForecastTime.Value.Date)
            {
                needFetch = true;
                Logger.LogInfo($"当前日期 {DateTime.Now:yyyy-MM-dd} 已跨越缓存预报日期 {lastForecastTime.Value:yyyy-MM-dd}，需要刷新天气数据。");
            }
        }

        if (!force && Time.unscaledTime < nextAllowedRefreshTime)
        {
            return;
        }

        if (needFetch)
        {
            EnsureRuntime();
            runtime?.StartPluginCoroutine(RefreshWeather());
        }
    }

    private DateTime? GetLastForecastTime(Dictionary<string, object>? forecastRoot)
    {
        try
        {
            var hourly = forecastRoot?.GetDict("hourly");
            var times = hourly?.GetList("time");
            if (times == null || times.Count == 0)
            {
                return null;
            }

            var lastTimeStr = Convert.ToString(times[times.Count - 1], System.Globalization.CultureInfo.InvariantCulture);
            if (DateTime.TryParse(lastTimeStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var lastTime))
            {
                return lastTime;
            }
        }
        catch
        {
        }
        return null;
    }

    private void RestoreCachedWeather()
    {
        var cached = TryLoadCachedWeather();
        if (cached == null)
        {
            return;
        }

        ApplyWeatherSnapshot(cached);
        if (cachedFetchedAtUtc.HasValue)
        {
            Logger.LogInfo($"已恢复缓存天气：{status}，缓存时间={cachedFetchedAtUtc.Value:O}");
        }
    }

    internal void SaveCache(string forecastJson, GeoPoint point)
    {
        try
        {
            var fetchedAtUtc = DateTime.UtcNow;
            var autoIp = weatherConfig.AutoIpLocation.Value;
            var locationQuery = autoIp ? "auto_ip" : weatherConfig.ManualLocation.Value;
            
            var json = $"{{\n" +
                       $"  \"fetched_at_utc\": \"{fetchedAtUtc:O}\",\n" +
                       $"  \"auto_ip\": {(autoIp ? "true" : "false")},\n" +
                       $"  \"location_query\": \"{EscapeJson(locationQuery)}\",\n" +
                       $"  \"location_name\": \"{EscapeJson(point.Name)}\",\n" +
                       $"  \"latitude\": {point.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n" +
                       $"  \"longitude\": {point.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n" +
                       $"  \"forecast_json\": \"{EscapeJson(forecastJson)}\"\n" +
                       $"}}";

            var configDir = Path.GetDirectoryName(CacheFilePath);
            if (configDir != null && !Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }
            File.WriteAllText(CacheFilePath, json);

            cachedForecastRoot = MiniJson.Deserialize(forecastJson) as Dictionary<string, object>;
            cachedPoint = point;
            cachedFetchedAtUtc = fetchedAtUtc;

            Logger.LogInfo("实时天气缓存已成功保存。");
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"保存实时天气缓存失败：{ex.Message}");
        }
    }

    internal WeatherSnapshot? TryLoadCachedWeather()
    {
        try
        {
            if (!File.Exists(CacheFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(CacheFilePath);
            var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (root == null)
            {
                return null;
            }

            var fetchedAtStr = root.GetString("fetched_at_utc", string.Empty);
            if (!DateTime.TryParse(fetchedAtStr, out var fetchedAtUtc))
            {
                return null;
            }

            var autoIp = root.GetBool("auto_ip", false);
            var locationQuery = root.GetString("location_query", string.Empty);
            if (autoIp != weatherConfig.AutoIpLocation.Value)
            {
                Logger.LogInfo("缓存已失效：AutoIpLocation 配置发生变更。");
                return null;
            }

            if (!autoIp && locationQuery != weatherConfig.ManualLocation.Value)
            {
                Logger.LogInfo("缓存已失效：ManualLocation 配置发生变更。");
                return null;
            }

            var forecastJson = root.GetString("forecast_json", string.Empty);
            if (string.IsNullOrEmpty(forecastJson))
            {
                return null;
            }

            var latitude = root.GetDouble("latitude", 0d);
            var longitude = root.GetDouble("longitude", 0d);
            var locationName = root.GetString("location_name", "缓存位置");
            var point = new GeoPoint(latitude, longitude, locationName);

            var forecastRoot = MiniJson.Deserialize(forecastJson) as Dictionary<string, object>;
            if (forecastRoot == null)
            {
                return null;
            }

            var snapshot = GetSnapshotFromForecast(forecastRoot, point, DateTime.Now);
            if (snapshot != null)
            {
                cachedForecastRoot = forecastRoot;
                cachedPoint = point;
                cachedFetchedAtUtc = fetchedAtUtc;
                return snapshot;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"读取或解析天气缓存失败：{ex.Message}");
        }

        return null;
    }

    private WeatherSnapshot? GetSnapshotFromForecast(Dictionary<string, object> forecastRoot, GeoPoint point, DateTime targetTime)
    {
        try
        {
            var hourly = forecastRoot.GetDict("hourly");
            if (hourly == null)
            {
                return null;
            }

            var times = hourly.GetList("time");
            var temp2m = hourly.GetList("temperature_2m");
            var codes = hourly.GetList("weather_code");

            if (times == null || temp2m == null || codes == null || times.Count == 0)
            {
                return null;
            }

            int closestHourIndex = -1;
            double minDiffMinutes = double.MaxValue;

            for (int i = 0; i < times.Count; i++)
            {
                var timeStr = Convert.ToString(times[i], System.Globalization.CultureInfo.InvariantCulture);
                if (DateTime.TryParse(timeStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedTime))
                {
                    var diff = Math.Abs((parsedTime - targetTime).TotalMinutes);
                    if (diff < minDiffMinutes)
                    {
                       minDiffMinutes = diff;
                       closestHourIndex = i;
                    }
                }
            }

            if (closestHourIndex == -1 || minDiffMinutes > 180d)
            {
                Logger.LogInfo($"缓存预报数据未覆盖当前时间：closestHourIndex={closestHourIndex}, diff={minDiffMinutes:F1}分钟");
                return null;
            }

            var code = Convert.ToInt32(codes[closestHourIndex], System.Globalization.CultureInfo.InvariantCulture);
            var temp = Mathf.RoundToInt((float)Convert.ToDouble(temp2m[closestHourIndex], System.Globalization.CultureInfo.InvariantCulture));

            var daily = forecastRoot.GetDict("daily");
            DateTime? sunrise = null;
            DateTime? sunset = null;

            if (daily != null)
            {
                var dailyTimes = daily.GetList("time");
                var sunrises = daily.GetList("sunrise");
                var sunsets = daily.GetList("sunset");

                if (dailyTimes != null && sunrises != null && sunsets != null)
                {
                    int closestDailyIndex = -1;
                    double minDailyDiff = double.MaxValue;

                    for (int j = 0; j < dailyTimes.Count; j++)
                    {
                        var dTimeStr = Convert.ToString(dailyTimes[j], System.Globalization.CultureInfo.InvariantCulture);
                        if (DateTime.TryParse(dTimeStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
                        {
                            var diff = Math.Abs((parsedDate.Date - targetTime.Date).TotalDays);
                            if (diff < minDailyDiff)
                            {
                                minDailyDiff = diff;
                                closestDailyIndex = j;
                            }
                        }
                    }

                    if (closestDailyIndex != -1 && minDailyDiff < 2d)
                    {
                        var sunriseStr = Convert.ToString(sunrises[closestDailyIndex], System.Globalization.CultureInfo.InvariantCulture);
                        var sunsetStr = Convert.ToString(sunsets[closestDailyIndex], System.Globalization.CultureInfo.InvariantCulture);

                        if (DateTime.TryParse(sunriseStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var riseTime))
                        {
                            sunrise = riseTime;
                        }
                        if (DateTime.TryParse(sunsetStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var setTime))
                        {
                            sunset = setTime;
                        }
                    }
                }
            }

            return new WeatherSnapshot(point.Name, WeatherLocalizer.WeatherText(code, GameLanguage.English), code, temp, point.Latitude, point.Longitude, targetTime, sunrise, sunset);
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"从预报 JSON 提取快照失败：{ex.Message}");
            return null;
        }
    }

    private void UpdateWeatherFromCache()
    {
        if (cachedForecastRoot == null || cachedPoint == null)
        {
            return;
        }

        if (Time.unscaledTime < nextCacheUpdateTime)
        {
            return;
        }
        nextCacheUpdateTime = Time.unscaledTime + 60f;

        var snapshot = GetSnapshotFromForecast(cachedForecastRoot, cachedPoint, DateTime.Now);
        if (snapshot != null)
        {
            if (lastWeather == null ||
                lastWeather.Code != snapshot.Code ||
                lastWeather.TemperatureCelsius != snapshot.TemperatureCelsius ||
                lastWeather.SolarPhase != snapshot.SolarPhase)
            {
                ApplyWeatherSnapshot(snapshot);
                Logger.LogInfo($"根据缓存预报更新天气：{status}");
            }
        }
        else
        {
            Logger.LogInfo("缓存预报已失效或未覆盖当前时间，触发网络刷新。");
            StartRefreshIfNeeded(force: false);
        }
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    private void ApplyWeatherSnapshot(WeatherSnapshot snapshot)
    {
        lastWeather = snapshot;
        CurrentUiWeatherString = UiWeatherString;
        status = $"{lastWeather.Location} / {CurrentUiWeatherString}";
        nativeBridge.ApplyWeather(lastWeather);
        weatherApplier.RebindSceneObjects();
    }

    internal IEnumerator RefreshLoop()
    {
        Logger.LogInfo("实时天气刷新循环已启动。");
        while (true)
        {
            StartRefreshIfNeeded(force: false);
            var minutes = Mathf.Max(1, weatherConfig.RefreshMinutes.Value);
            yield return new WaitForSeconds(minutes * 60f);
        }
    }

    internal IEnumerator RefreshWeather()
    {
        if (refreshInProgress || Time.unscaledTime < nextAllowedRefreshTime)
        {
            yield break;
        }

        refreshInProgress = true;
        nextAllowedRefreshTime = Time.unscaledTime + 10f;
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
            refreshInProgress = false;
            yield break;
        }

        yield return runtime.RunNestedCoroutine(weatherClient.Fetch(snapshot => result = snapshot));

        if (result is { Weather: not null })
        {
            lastWeather = result.Weather;
            CurrentUiWeatherString = UiWeatherString;
            status = $"{lastWeather.Location} / {CurrentUiWeatherString}";
            
            if (result.RawJson != null && result.Point != null)
            {
                SaveCache(result.RawJson, result.Point);
            }

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

        refreshInProgress = false;
    }

    private static string FormatOptionalTime(DateTime? value)
    {
        return value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm") : "fallback-solar-elevation";
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
        if (plugin != null)
        {
            StartCoroutine(plugin.RefreshLoop());
        }
    }

    private void Update()
    {
        plugin?.Tick();
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
                  "&current=temperature_2m,weather_code&hourly=temperature_2m,weather_code&daily=sunrise,sunset&timezone=auto&forecast_days=1";
        using var request = UnityWebRequest.Get(url);
        request.timeout = Mathf.Clamp(config.TimeoutSeconds.Value, 2, 15);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            done(Result.Fail($"天气请求失败：{request.error}"));
            yield break;
        }

        var snapshot = PublicApiParser.ParseOpenMeteoWeather(request.downloadHandler.text, point);
        done(snapshot == null ? Result.Fail("天气响应解析失败。") : Result.Ok(snapshot, request.downloadHandler.text, point));
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
        internal string? RawJson { get; }
        internal GeoPoint? Point { get; }

        private Result(WeatherSnapshot? weather, string? error, string? rawJson = null, GeoPoint? point = null)
        {
            Weather = weather;
            Error = error;
            RawJson = rawJson;
            Point = point;
        }

        internal static Result Ok(WeatherSnapshot weather, string rawJson, GeoPoint point) => new(weather, null, rawJson, point);
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

internal enum GameLanguage
{
    Japanese,
    English,
    ChineseSimplified,
    ChineseTraditional,
    Portuguese,
    Korean,
    Russian
}

internal static class GameLanguageProvider
{
    private static object? languageSupplier;
    private static PropertyInfo? languageProperty;
    private static float nextScanTime;
    private static bool loggedFallback;

    internal static GameLanguage CurrentLanguage { get; private set; } = GameLanguage.English;

    internal static void Tick()
    {
        if (Time.unscaledTime < nextScanTime)
        {
            return;
        }

        if (languageSupplier == null || languageProperty == null)
        {
            ScanLanguageSupplier();
            if (languageSupplier == null || languageProperty == null)
            {
                nextScanTime = Time.unscaledTime + 1f;
                return;
            }
        }

        nextScanTime = Time.unscaledTime + 15f;
        SetFromGameValue(languageProperty?.GetValue(languageSupplier));
    }

    internal static void SetFromGameValue(object? value)
    {
        if (value == null)
        {
            return;
        }

        object actualValue = value;
        try
        {
            var type = value.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName.StartsWith("R3.ReadOnlyReactiveProperty"))
            {
                var valueProp = type.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                if (valueProp != null)
                {
                    actualValue = valueProp.GetValue(value) ?? value;
                }
            }
        }
        catch
        {
        }

        if (Enum.TryParse<GameLanguage>(actualValue.ToString(), out var language))
        {
            if (CurrentLanguage != language)
            {
                CurrentLanguage = language;
                RealTimeWeatherPlugin.Log.LogInfo($"游戏语言已切换：{language}");
                
                CurrentDateAndTimeUiPatch.RefreshAll();
                SettingUiInjector.RefreshSettingLabels();
                
                if (RealTimeWeatherPlugin.Instance != null)
                {
                    RealTimeWeatherPlugin.Instance.RefreshLocalizedWeatherString();
                }
            }
        }
    }

    private static void ScanLanguageSupplier()
    {
        foreach (var unityObject in Resources.FindObjectsOfTypeAll<UnityEngine.Object>())
        {
            if (unityObject == null)
            {
                continue;
            }

            if (TryBindLanguageSupplier(unityObject))
            {
                return;
            }

            foreach (var field in unityObject.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType.FullName != "Bulbul.LanguageSupplier")
                {
                    continue;
                }

                var value = field.GetValue(unityObject);
                if (value != null && TryBindLanguageSupplier(value))
                {
                    return;
                }
            }
        }

        if (!loggedFallback)
        {
            loggedFallback = true;
            RealTimeWeatherPlugin.Log.LogInfo("尚未找到 Bulbul.LanguageSupplier，天气文本暂时使用 English。");
        }
    }

    private static bool TryBindLanguageSupplier(object candidate)
    {
        if (candidate.GetType().FullName != "Bulbul.LanguageSupplier")
        {
            return false;
        }

        var property = candidate.GetType().GetProperty("Language", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null)
        {
            return false;
        }

        languageSupplier = candidate;
        languageProperty = property;
        RealTimeWeatherPlugin.Log.LogInfo("已绑定游戏语言供应器 Bulbul.LanguageSupplier。");
        return true;
    }
}

internal static class WeatherLocalizer
{
    internal static string WeatherText(int code, GameLanguage language)
    {
        return language switch
        {
            GameLanguage.Japanese => WeatherTextJa(code),
            GameLanguage.ChineseSimplified => WeatherTextZhHans(code),
            GameLanguage.ChineseTraditional => WeatherTextZhHant(code),
            GameLanguage.Portuguese => WeatherTextPt(code),
            GameLanguage.Korean => WeatherTextKo(code),
            GameLanguage.Russian => WeatherTextRu(code),
            _ => WeatherTextEn(code)
        };
    }

    internal static string GetEnableWeatherText(GameLanguage language)
    {
        return language switch
        {
            GameLanguage.ChineseSimplified => "启用实时天气",
            GameLanguage.ChineseTraditional => "啟用即時天氣",
            GameLanguage.Japanese => "リアルタイム天気同期",
            GameLanguage.Korean => "실시간 날씨 동기화",
            GameLanguage.Portuguese => "Tempo em Tempo Real",
            GameLanguage.Russian => "Реальная погода",
            _ => "Real-time Weather"
        };
    }

    internal static string GetAutoLocText(GameLanguage language)
    {
        return language switch
        {
            GameLanguage.ChineseSimplified => "自动 IP 定位",
            GameLanguage.ChineseTraditional => "自動 IP 定位",
            GameLanguage.Japanese => "自動IP取得",
            GameLanguage.Korean => "자동 IP 위치",
            GameLanguage.Portuguese => "Localização por IP",
            GameLanguage.Russian => "Автоопределение IP",
            _ => "Auto IP Location"
        };
    }

    private static string WeatherTextEn(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 => "Snow",
        77 => "Snow grains",
        80 or 81 or 82 => "Showers",
        85 or 86 => "Snow showers",
        95 or 96 or 99 => "Thunderstorm",
        _ => "Unknown"
    };

    private static string WeatherTextZhHans(int code) => code switch
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
        _ => "未知"
    };

    private static string WeatherTextZhHant(int code) => code switch
    {
        0 => "晴",
        1 => "大致晴朗",
        2 => "多雲",
        3 => "陰",
        45 or 48 => "霧",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "凍毛毛雨",
        61 or 63 or 65 => "雨",
        66 or 67 => "凍雨",
        71 or 73 or 75 => "雪",
        77 => "雪粒",
        80 or 81 or 82 => "陣雨",
        85 or 86 => "陣雪",
        95 or 96 or 99 => "雷暴",
        _ => "未知"
    };

    private static string WeatherTextJa(int code) => code switch
    {
        0 => "晴れ",
        1 => "ほぼ晴れ",
        2 => "一部曇り",
        3 => "曇り",
        45 or 48 => "霧",
        51 or 53 or 55 => "霧雨",
        56 or 57 => "着氷性霧雨",
        61 or 63 or 65 => "雨",
        66 or 67 => "着氷性雨",
        71 or 73 or 75 => "雪",
        77 => "細雪",
        80 or 81 or 82 => "にわか雨",
        85 or 86 => "にわか雪",
        95 or 96 or 99 => "雷雨",
        _ => "不明"
    };

    private static string WeatherTextPt(int code) => code switch
    {
        0 => "Céu limpo",
        1 => "Predom. limpo",
        2 => "Parcialmente nublado",
        3 => "Nublado",
        45 or 48 => "Nevoeiro",
        51 or 53 or 55 => "Chuvisco",
        56 or 57 => "Chuvisco congelante",
        61 or 63 or 65 => "Chuva",
        66 or 67 => "Chuva congelante",
        71 or 73 or 75 => "Neve",
        77 => "Grãos de neve",
        80 or 81 or 82 => "Aguaceiros",
        85 or 86 => "Aguaceiros de neve",
        95 or 96 or 99 => "Trovoada",
        _ => "Desconhecido"
    };

    private static string WeatherTextKo(int code) => code switch
    {
        0 => "맑음",
        1 => "대체로 맑음",
        2 => "구름 조금",
        3 => "흐림",
        45 or 48 => "안개",
        51 or 53 or 55 => "이슬비",
        56 or 57 => "어는 이슬비",
        61 or 63 or 65 => "비",
        66 or 67 => "어는 비",
        71 or 73 or 75 => "눈",
        77 => "싸락눈",
        80 or 81 or 82 => "소나기",
        85 or 86 => "소낙눈",
        95 or 96 or 99 => "뇌우",
        _ => "알 수 없음"
    };

    private static string WeatherTextRu(int code) => code switch
    {
        0 => "Ясно",
        1 => "Преим. ясно",
        2 => "Переменная облачность",
        3 => "Облачно",
        45 or 48 => "Туман",
        51 or 53 or 55 => "Морось",
        56 or 57 => "Ледяная морось",
        61 or 63 or 65 => "Дождь",
        66 or 67 => "Ледяной дождь",
        71 or 73 or 75 => "Снег",
        77 => "Снежные зерна",
        80 or 81 or 82 => "Ливни",
        85 or 86 => "Снежные ливни",
        95 or 96 or 99 => "Гроза",
        _ => "Неизвестно"
    };
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
            return new WeatherSnapshot(point.Name, WeatherLocalizer.WeatherText(code, GameLanguage.English), code, temperature, point.Latitude, point.Longitude, localTime, sunrise, sunset);
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
}

internal sealed class NativeGameBridge
{
    private readonly WeatherConfig config;
    private bool loggedMissingService;
    private object? windowViewService;
    private MethodInfo? changeWeatherAndTimeMethod;
    private float nextScanTime;
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
                RealTimeWeatherPlugin.Log.LogInfo($"已调用原生 ChangeWeatherAndTime：{candidate} ({WeatherLocalizer.WeatherText(weather.Code, RealTimeWeatherPlugin.CurrentLanguage)}, {weather.SolarPhase})");
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

        if (!loggedMissingService && windowViewService == null)
        {
            loggedMissingService = true;
            RealTimeWeatherPlugin.Log.LogInfo("尚未在当前场景找到 Bulbul.WindowViewService，会继续扫描。");
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
        if (!value || ReferenceEquals(RealTimeWeatherPlugin.Instance, null))
        {
            return;
        }

        var name = __instance.name.ToLowerInvariant();
        if ((name.Contains("setting") || name.Contains("option") || name.Contains("general") || name.Contains("设置") || name.Contains("常规")) && Time.unscaledTime - lastLogTime > 5f)
        {
            lastLogTime = Time.unscaledTime;
            RealTimeWeatherPlugin.Log.LogInfo($"检测到可能的设置菜单对象：{GetPath(__instance.transform)}。当前版本通过 BepInEx 配置调整实时天气参数。");
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
