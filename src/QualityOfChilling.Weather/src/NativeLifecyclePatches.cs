using System;
using System.Reflection;
using HarmonyLib;

namespace RealTimeWeatherForChill;

[HarmonyPatch]
internal static class FacilityEnvironmentSetupPatch
{
    private static MethodBase? TargetMethod()
    {
        return AccessTools.TypeByName("Bulbul.FacilityEnvironment")
            ?.GetMethod("Setup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static void Postfix(object __instance)
    {
        try
        {
            var field = __instance.GetType().GetField("_windowViewService", BindingFlags.Instance | BindingFlags.NonPublic);
            var service = field?.GetValue(__instance);
            if (service == null)
            {
                return;
            }

            var plugin = RealTimeWeatherPlugin.Instance;
            if (!ReferenceEquals(plugin, null))
            {
                plugin.CaptureWindowViewService(service);
            }
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"捕获 FacilityEnvironment WindowViewService 失败：{ex.Message}");
        }
    }
}

[HarmonyPatch]
internal static class CurrentDateAndTimeUiPatch
{
    private static bool triggeredFirstRefresh;
    private static bool loggedFirstInjection;

    private static MethodBase? TargetMethod()
    {
        return AccessTools.TypeByName("Bulbul.CurrentDateAndTimeUI")
            ?.GetMethod("UpdateDateAndTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static void Postfix(object __instance)
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
            var field = __instance.GetType().GetField("_dateText", BindingFlags.Instance | BindingFlags.NonPublic);
            var textObject = field?.GetValue(__instance);
            var textProperty = textObject?.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            if (textProperty?.GetValue(textObject) is string currentText && !currentText.Contains(weatherText))
            {
                textProperty.SetValue(textObject, StripExistingWeather(currentText) + " | " + weatherText);
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
