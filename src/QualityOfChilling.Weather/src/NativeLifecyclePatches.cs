using System;
using System.Reflection;
using HarmonyLib;
using Bulbul;
using TMPro;

namespace RealTimeWeatherForChill;

[HarmonyPatch(typeof(FacilityEnvironment), "Setup")]
internal static class FacilityEnvironmentSetupPatch
{
    private static void Postfix(FacilityEnvironment __instance)
    {
        try
        {
            var field = typeof(FacilityEnvironment).GetField("_windowViewService", BindingFlags.Instance | BindingFlags.NonPublic);
            var service = field?.GetValue(__instance);
            if (service != null)
            {
                var plugin = RealTimeWeatherPlugin.Instance;
                if (!ReferenceEquals(plugin, null))
                {
                    plugin.CaptureWindowViewService(service);
                }
            }
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"捕获 FacilityEnvironment WindowViewService 失败：{ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(CurrentDateAndTimeUI), "UpdateDateAndTime")]
internal static class CurrentDateAndTimeUiPatch
{
    private static bool triggeredFirstRefresh;
    private static bool loggedFirstInjection;

    private static void Postfix(CurrentDateAndTimeUI __instance)
    {
        var plugin = RealTimeWeatherPlugin.Instance;
        if (!triggeredFirstRefresh && !ReferenceEquals(plugin, null))
        {
            triggeredFirstRefresh = true;
            RealTimeWeatherPlugin.Log.LogInfo("CurrentDateAndTimeUI.UpdateDateAndTime 已触发，游戏主 UI 已就绪。启动实时天气刷新。");
            plugin.TriggerRefreshFromGameReady();
        }

        var weatherText = RealTimeWeatherPlugin.CurrentUiWeatherString;
        if (string.IsNullOrEmpty(weatherText))
        {
            return;
        }

        try
        {
            var field = typeof(CurrentDateAndTimeUI).GetField("_dateText", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(__instance) is TextMeshProUGUI text && !text.text.Contains(weatherText))
            {
                text.text = StripExistingWeather(text.text) + " | " + weatherText;
                if (!loggedFirstInjection)
                {
                    loggedFirstInjection = true;
                    RealTimeWeatherPlugin.Log.LogInfo($"CurrentDateAndTimeUI._dateText 已写入天气：{weatherText}");
                }
            }
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogDebug($"注入 CurrentDateAndTimeUI 天气文本失败：{ex.Message}");
        }
    }

    private static string StripExistingWeather(string value)
    {
        var index = value.IndexOf(" | ", StringComparison.Ordinal);
        return index >= 0 ? value.Substring(0, index) : value;
    }
}
