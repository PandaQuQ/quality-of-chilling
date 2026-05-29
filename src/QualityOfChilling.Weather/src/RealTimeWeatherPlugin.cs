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
    private const string PluginVersion = "0.1.1";

    internal static RealTimeWeatherPlugin? Instance { get; private set; }
    internal static string CurrentUiWeatherString { get; private set; } = string.Empty;
    internal static ManualLogSource Log { get; private set; } = null!;
    internal static GameLanguage CurrentLanguage => GameLanguageProvider.CurrentLanguage;
    private static readonly string DebugLogPath = Path.Combine(Paths.BepInExRootPath, "RealTimeWeatherForChill.debug.log");

    private Harmony? harmony;
    private WeatherConfig weatherConfig = null!;
    private WeatherClient weatherClient = null!;
    private NativeGameBridge nativeBridge = null!;
    internal WeatherConfig WeatherConfig => weatherConfig;
    internal WeatherSnapshot? LastWeather => lastWeather;
    internal string UiWeatherString
    {
        get
        {
            if (lastWeather == null) return string.Empty;
            var applied = lastWeather.OverrideConfig(weatherConfig);
            if (!weatherConfig.InjectNativeDateTimeUI.Value || !weatherConfig.SyncWeather.Value)
            {
                return string.Empty;
            }
            string loc = !string.IsNullOrEmpty(currentLocalizedLocation) ? currentLocalizedLocation + " | " : string.Empty;
            return $"{loc}{WeatherLocalizer.WeatherText(applied.Code, CurrentLanguage)} {applied.TemperatureCelsius}°C";
        }
    }
    private WeatherSnapshot? lastWeather;
    private WeatherRuntime? runtime;
    private string? currentLocalizedLocation;
    private UnityEngine.Coroutine? locationNameCoroutine;
    private GameLanguage lastLocationLanguage = (GameLanguage)(-1);
    private double lastLocationLat;
    private double lastLocationLon;
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
        var appliedWeather = lastWeather?.OverrideConfig(weatherConfig);
        nativeBridge.Tick(appliedWeather);
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
            TryUpdateLocalizedLocation();
            var appliedWeather = lastWeather.OverrideConfig(weatherConfig);
            CurrentUiWeatherString = UiWeatherString;
            status = $"{currentLocalizedLocation ?? appliedWeather.Location} / {CurrentUiWeatherString}";
        }
    }

    private void TryUpdateLocalizedLocation()
    {
        if (lastWeather == null) return;
        
        if (lastLocationLanguage == CurrentLanguage && 
            Math.Abs(lastLocationLat - lastWeather.Latitude) < 0.001 &&
            Math.Abs(lastLocationLon - lastWeather.Longitude) < 0.001 &&
            currentLocalizedLocation != null)
        {
            return;
        }

        if (locationNameCoroutine != null)
        {
            runtime?.StopPluginCoroutine(locationNameCoroutine);
        }
        
        lastLocationLanguage = CurrentLanguage;
        lastLocationLat = lastWeather.Latitude;
        lastLocationLon = lastWeather.Longitude;
        locationNameCoroutine = runtime?.StartPluginCoroutine(FetchLocalizedLocation(lastWeather.Latitude, lastWeather.Longitude, CurrentLanguage));
    }

    private IEnumerator FetchLocalizedLocation(double lat, double lon, GameLanguage language)
    {
        string langCode = language switch
        {
            GameLanguage.Japanese => "ja",
            GameLanguage.ChineseSimplified => "zh",
            GameLanguage.ChineseTraditional => "zh",
            GameLanguage.Korean => "ko",
            GameLanguage.Portuguese => "pt",
            GameLanguage.Russian => "ru",
            _ => "en"
        };
        
        var url = $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&localityLanguage={langCode}";
        
        using var request = UnityEngine.Networking.UnityWebRequest.Get(url);
        request.timeout = 5;
        yield return request.SendWebRequest();
        
        if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
        {
            try
            {
                var root = MiniJson.Deserialize(request.downloadHandler.text) as Dictionary<string, object>;
                if (root != null)
                {
                    var city = root.GetString("city", "");
                    var locality = root.GetString("locality", "");
                    
                    string loc = !string.IsNullOrEmpty(city) ? city : locality;
                    if (!string.IsNullOrEmpty(loc))
                    {
                        currentLocalizedLocation = loc;
                        CurrentUiWeatherString = UiWeatherString;
                        if (lastWeather != null)
                        {
                            var appliedWeather = lastWeather.OverrideConfig(weatherConfig);
                            status = $"{loc} / {CurrentUiWeatherString}";
                        }
                        CurrentDateAndTimeUiPatch.RefreshAll();
                    }
                }
            }
            catch { }
        }
    }

    internal void ReapplyCurrentWeather()
    {
        if (lastWeather != null)
        {
            nativeBridge.ResetAppliedState();
            ApplyWeatherSnapshot(lastWeather);
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



    private void StartRefreshIfNeeded(bool force)
    {
        if (refreshInProgress)
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
        RefreshLocalizedWeatherString();
        var appliedWeather = lastWeather.OverrideConfig(weatherConfig);
        nativeBridge.ApplyWeather(appliedWeather);
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
            RefreshLocalizedWeatherString();
            
            if (result.RawJson != null && result.Point != null)
            {
                SaveCache(result.RawJson, result.Point);
            }

            nativeBridge.ApplyWeather(lastWeather);
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

