using BepInEx.Configuration;

namespace RealTimeWeatherForChill;

internal sealed class WeatherConfig
{
    internal ConfigEntry<bool> SyncWeather { get; }
    internal ConfigEntry<bool> SyncDayNight { get; }
    internal ConfigEntry<bool> AutoIpLocation { get; }
    internal ConfigEntry<string> ManualLocation { get; }
    internal ConfigEntry<int> RefreshMinutes { get; }
    internal ConfigEntry<int> TimeoutSeconds { get; }
    internal ConfigEntry<float> IntensityScale { get; }
    internal ConfigEntry<bool> InjectNativeDateTimeUI { get; }
    internal ConfigEntry<bool> UseNativeWindowWeather { get; }

    internal WeatherConfig(ConfigFile config)
    {
        SyncWeather = config.Bind("General", "SyncWeather", true, "启用真实天气。如果关闭，天气将保持晴天。");
        SyncDayNight = config.Bind("General", "SyncDayNight", true, "启用真实日夜。如果关闭，时间将保持白天。");

        // Migrate old Enabled setting if it exists and was explicitly set to false
        var oldEnabledEntry = config.Bind("General", "Enabled", true, "启用实时天气同步（已废弃，由 SyncWeather 代替）。");
        if (!oldEnabledEntry.Value)
        {
            SyncWeather.Value = false;
            oldEnabledEntry.Value = true; // reset so we don't migrate again
        }

        AutoIpLocation = config.Bind("Location", "AutoIpLocation", true, "使用 IP 自动定位。启用后会访问外部定位/天气 API。");
        ManualLocation = config.Bind("Location", "ManualLocation", "beijing", "关闭自动定位时用于公共地理编码 API 的城市名、拼音或经纬度，格式可为 39.9,116.4。");
        RefreshMinutes = config.Bind("Weather", "RefreshMinutes", 30, new ConfigDescription("天气刷新间隔（分钟）。", new AcceptableValueRange<int>(1, 180)));
        TimeoutSeconds = config.Bind("Weather", "TimeoutSeconds", 5, new ConfigDescription("网络请求超时（秒）。", new AcceptableValueRange<int>(2, 15)));
        IntensityScale = config.Bind("Effects", "IntensityScale", 1f, new ConfigDescription("fallback 天气效果强度倍率。", new AcceptableValueRange<float>(0f, 2f)));
        InjectNativeDateTimeUI = config.Bind("Native", "InjectNativeDateTimeUI", true, "把天气信息追加到游戏现有日期/时间 UI。");
        UseNativeWindowWeather = config.Bind("Native", "UseNativeWindowWeather", true, "尝试调用游戏原生 WindowViewService.ChangeWeatherAndTime 切换窗口天气/时间。");
    }
}
