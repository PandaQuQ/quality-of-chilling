using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

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

        ApplyWeatherText(__instance);
    }

    internal static void RefreshAll()
    {
        var type = AccessTools.TypeByName("Bulbul.CurrentDateAndTimeUI");
        if (type == null)
        {
            return;
        }

        foreach (var component in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (component != null && type.IsInstanceOfType(component))
            {
                ApplyWeatherText(component);
            }
        }
    }

    private static void ApplyWeatherText(object instance)
    {
        var weatherText = RealTimeWeatherPlugin.CurrentUiWeatherString;
        if (string.IsNullOrEmpty(weatherText))
        {
            return;
        }

        try
        {
            var field = instance.GetType().GetField("_dateText", BindingFlags.Instance | BindingFlags.NonPublic);
            var textObject = field?.GetValue(instance);
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

[HarmonyPatch]
internal static class LanguageSupplierSetPatch
{
    private static MethodBase? TargetMethod()
    {
        var type = AccessTools.TypeByName("Bulbul.LanguageSupplier");
        return type?.GetMethod("Set", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? type?.GetMethod("set_Language", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static void Postfix(object __instance)
    {
        try
        {
            var property = __instance.GetType().GetProperty("Language", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            RealTimeWeatherPlugin.NotifyGameLanguageChanged(property?.GetValue(__instance));
            CurrentDateAndTimeUiPatch.RefreshAll();
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogDebug($"处理游戏语言变更失败：{ex.Message}");
        }
    }
}

[HarmonyPatch]
internal static class SettingUiSetupPatch
{
    private static MethodBase? TargetMethod()
    {
        return AccessTools.TypeByName("Bulbul.SettingUI")
            ?.GetMethod("Setup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static void Postfix(object __instance)
    {
        try
        {
            SettingUiInjector.Inject(__instance);
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogError($"注入设置菜单失败：{ex}");
        }
    }
}

internal static class SettingUiInjector
{
    internal static void Inject(object settingUi)
    {
        var type = settingUi.GetType();
        var vsyncActField = type.GetField("_verticalSyncActivateInteractableUI", BindingFlags.Instance | BindingFlags.NonPublic);
        var vsyncDeactField = type.GetField("_verticalSyncDeactivateInteractableUI", BindingFlags.Instance | BindingFlags.NonPublic);

        var vsyncAct = vsyncActField?.GetValue(settingUi) as MonoBehaviour;
        var vsyncDeact = vsyncDeactField?.GetValue(settingUi) as MonoBehaviour;

        if (vsyncAct == null || vsyncDeact == null)
        {
            RealTimeWeatherPlugin.Log.LogWarning("未找到垂直同步按钮，无法克隆设置行。");
            return;
        }

        var vsyncRow = vsyncAct.transform.parent.gameObject;
        var generalParent = GetParentTransform(settingUi, "_generalParent");

        if (generalParent == null)
        {
            RealTimeWeatherPlugin.Log.LogWarning("未找到常规设置容器，使用垂直同步默认容器。");
            generalParent = vsyncRow.transform.parent;
        }

        // Check if already injected in this transform
        if (generalParent.Find("RealTimeWeather_EnableRow") != null)
        {
            return;
        }

        RealTimeWeatherPlugin.Log.LogInfo("开始在游戏设置菜单内原生注入实时天气选项...");

        // 1. Clone Row for Enable Weather
        var weatherRow = UnityEngine.Object.Instantiate(vsyncRow, generalParent);
        weatherRow.name = "RealTimeWeather_EnableRow";
        weatherRow.SetActive(true);

        string labelText = RealTimeWeatherPlugin.CurrentLanguage switch
        {
            GameLanguage.ChineseSimplified => "启用实时天气",
            GameLanguage.ChineseTraditional => "啟用即時天氣",
            GameLanguage.Japanese => "实时天气同步",
            GameLanguage.Korean => "실시간 날씨 동기화",
            GameLanguage.Portuguese => "Tempo em Tempo Real",
            GameLanguage.Russian => "Реальная погода",
            _ => "Real-time Weather"
        };

        var interactableUiType = AccessTools.TypeByName("Bulbul.InteractableUI");
        var enableButtons = weatherRow.GetComponentsInChildren(interactableUiType, true);

        if (enableButtons != null && enableButtons.Length >= 2)
        {
            var btnOn = enableButtons[0];
            var btnOff = enableButtons[1];

            SetRowLabel(weatherRow, labelText, btnOn, btnOff);

            var setupMethod = interactableUiType.GetMethod("Setup", new Type[] { typeof(Action) });

            Action onAct = () =>
            {
                RealTimeWeatherPlugin.Log.LogInfo("用户在设置菜单启用了实时天气");
                var config = RealTimeWeatherPlugin.Instance?.Config;
                if (config != null)
                {
                    config.Bind("General", "Enabled", true).Value = true;
                    config.Save();
                }
                SetIsUsing(btnOn, true);
                SetIsUsing(btnOff, false);
                RealTimeWeatherPlugin.Instance?.TriggerRefreshFromGameReady();
            };

            Action onDeact = () =>
            {
                RealTimeWeatherPlugin.Log.LogInfo("用户在设置菜单关闭了实时天气");
                var config = RealTimeWeatherPlugin.Instance?.Config;
                if (config != null)
                {
                    config.Bind("General", "Enabled", true).Value = false;
                    config.Save();
                }
                SetIsUsing(btnOn, false);
                SetIsUsing(btnOff, true);
                CurrentDateAndTimeUiPatch.RefreshAll();
            };

            setupMethod?.Invoke(btnOn, new object[] { onAct });
            setupMethod?.Invoke(btnOff, new object[] { onDeact });

            var config = RealTimeWeatherPlugin.Instance?.Config;
            bool isEnabled = config != null && config.Bind("General", "Enabled", true).Value;
            SetIsUsing(btnOn, isEnabled);
            SetIsUsing(btnOff, !isEnabled);
        }
        else
        {
            SetText(weatherRow, labelText);
        }

        // 2. Clone Row for Auto IP Location
        var autoLocRow = UnityEngine.Object.Instantiate(vsyncRow, generalParent);
        autoLocRow.name = "RealTimeWeather_AutoLocRow";
        autoLocRow.SetActive(true);

        string autoLocLabel = RealTimeWeatherPlugin.CurrentLanguage switch
        {
            GameLanguage.ChineseSimplified => "自动 IP 定位",
            GameLanguage.ChineseTraditional => "自動 IP 定位",
            GameLanguage.Japanese => "自动 IP 定位",
            GameLanguage.Korean => "자동 IP 위치",
            GameLanguage.Portuguese => "Localização por IP",
            GameLanguage.Russian => "Автоопределение IP",
            _ => "Auto IP Location"
        };

        var autoLocButtons = autoLocRow.GetComponentsInChildren(interactableUiType, true);

        if (autoLocButtons != null && autoLocButtons.Length >= 2)
        {
            var btnOn = autoLocButtons[0];
            var btnOff = autoLocButtons[1];

            SetRowLabel(autoLocRow, autoLocLabel, btnOn, btnOff);

            var setupMethod = interactableUiType.GetMethod("Setup", new Type[] { typeof(Action) });

            Action onAct = () =>
            {
                RealTimeWeatherPlugin.Log.LogInfo("用户在设置菜单启用了自动 IP 定位");
                var config = RealTimeWeatherPlugin.Instance?.Config;
                if (config != null)
                {
                    config.Bind("Location", "AutoIpLocation", false).Value = true;
                    config.Save();
                }
                SetIsUsing(btnOn, true);
                SetIsUsing(btnOff, false);
                RealTimeWeatherPlugin.Instance?.TriggerRefreshFromGameReady();
            };

            Action onDeact = () =>
            {
                RealTimeWeatherPlugin.Log.LogInfo("用户在设置菜单关闭了自动 IP 定位");
                var config = RealTimeWeatherPlugin.Instance?.Config;
                if (config != null)
                {
                    config.Bind("Location", "AutoIpLocation", false).Value = false;
                    config.Save();
                }
                SetIsUsing(btnOn, false);
                SetIsUsing(btnOff, true);
                RealTimeWeatherPlugin.Instance?.TriggerRefreshFromGameReady();
            };

            setupMethod?.Invoke(btnOn, new object[] { onAct });
            setupMethod?.Invoke(btnOff, new object[] { onDeact });

            var config = RealTimeWeatherPlugin.Instance?.Config;
            bool isAutoIp = config != null && config.Bind("Location", "AutoIpLocation", false).Value;
            SetIsUsing(btnOn, isAutoIp);
            SetIsUsing(btnOff, !isAutoIp);
        }
        else
        {
            SetText(autoLocRow, autoLocLabel);
        }

        // Apply visual layout adjustments to position injected rows properly
        try
        {
            PositionInjectedRows(weatherRow, autoLocRow, generalParent);
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"调整注入行位置失败：{ex.Message}");
        }

        RealTimeWeatherPlugin.Log.LogInfo("已成功在常规设置菜单内注入“启用实时天气”和“自动 IP 定位”两个原生选项。");
    }

    private static Transform? GetParentTransform(object settingUi, string fieldName)
    {
        try
        {
            var field = settingUi.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            var val = field?.GetValue(settingUi);
            if (val is GameObject go) return go.transform;
            if (val is Transform t) return t;
        }
        catch
        {
        }
        return null;
    }

    private static void SetText(GameObject go, string text)
    {
        foreach (var comp in go.GetComponentsInChildren<Component>(true))
        {
            if (comp == null) continue;
            var typeName = comp.GetType().FullName;
            if (typeName == "UnityEngine.UI.Text" || typeName == "TMPro.TextMeshProUGUI")
            {
                var prop = comp.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                prop?.SetValue(comp, text);
            }
        }
    }

    private static void SetRowLabel(GameObject row, string text, Component btnOn, Component btnOff)
    {
        foreach (var comp in row.GetComponentsInChildren<Component>(true))
        {
            if (comp == null) continue;
            var typeName = comp.GetType().FullName;
            if (typeName == "UnityEngine.UI.Text" || typeName == "TMPro.TextMeshProUGUI")
            {
                if (comp.transform.IsChildOf(btnOn.transform) || comp.transform.IsChildOf(btnOff.transform))
                {
                    continue;
                }
                var prop = comp.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                prop?.SetValue(comp, text);
            }
        }
    }

    private static void PositionInjectedRows(GameObject weatherRow, GameObject autoLocRow, Transform generalParent)
    {
        var children = new List<RectTransform>();
        for (int i = 0; i < generalParent.childCount; i++)
        {
            var child = generalParent.GetChild(i) as RectTransform;
            if (child != null && child.gameObject.activeSelf && 
                child.gameObject != weatherRow && child.gameObject != autoLocRow)
            {
                children.Add(child);
            }
        }

        if (children.Count == 0)
        {
            RealTimeWeatherPlugin.Log.LogWarning("常规设置容器中没有找到任何原生子项，无法定位注入行。");
            return;
        }

        foreach (var child in children)
        {
            RealTimeWeatherPlugin.Log.LogInfo($"[UI Debug] 子项: {child.name}, anchoredPosition={child.anchoredPosition}, size={child.rect.size}");
        }

        children.Sort((a, b) => b.anchoredPosition.y.CompareTo(a.anchoredPosition.y));

        RectTransform? lowestRow = null;
        RectTransform? secondLowestRow = null;

        for (int i = children.Count - 1; i >= 0; i--)
        {
            var child = children[i];
            if (child.rect.height > 15 && child.rect.height < 100)
            {
                if (lowestRow == null)
                {
                    lowestRow = child;
                }
                else if (secondLowestRow == null)
                {
                    secondLowestRow = child;
                    break;
                }
            }
        }

        if (lowestRow == null)
        {
            lowestRow = children[children.Count - 1];
        }

        float spacing = -60f;
        if (lowestRow != null)
        {
            if (secondLowestRow != null)
            {
                float diff = lowestRow.anchoredPosition.y - secondLowestRow.anchoredPosition.y;
                if (diff < 0)
                {
                    spacing = diff;
                }
            }
            else
            {
                float height = lowestRow.rect.height;
                if (height > 0)
                {
                    spacing = -height - 10f;
                }
            }

            RealTimeWeatherPlugin.Log.LogInfo($"最下方行是: {lowestRow.name}, Y={lowestRow.anchoredPosition.y}, spacing={spacing}");

            var weatherRect = weatherRow.GetComponent<RectTransform>();
            var autoLocRect = autoLocRow.GetComponent<RectTransform>();

            if (weatherRect != null && autoLocRect != null)
            {
                Vector2 posWeather = weatherRect.anchoredPosition;
                posWeather.y = lowestRow.anchoredPosition.y + spacing;
                posWeather.x = lowestRow.anchoredPosition.x;
                weatherRect.anchoredPosition = posWeather;

                Vector2 posAutoLoc = autoLocRect.anchoredPosition;
                posAutoLoc.y = posWeather.y + spacing;
                posAutoLoc.x = lowestRow.anchoredPosition.x;
                autoLocRect.anchoredPosition = posAutoLoc;

                RealTimeWeatherPlugin.Log.LogInfo($"已成功设置注入选项的位置。启用实时天气 Y: {weatherRect.anchoredPosition.y}, 自动 IP 定位 Y: {autoLocRect.anchoredPosition.y}");
            }
        }
    }

    private static void SetIsUsing(object button, bool value)
    {
        try
        {
            var type = button.GetType();
            var prop = type.GetProperty("IsUsing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                prop.SetValue(button, value);
            }
            else
            {
                var method = type.GetMethod("set_IsUsing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                method?.Invoke(button, new object[] { value });
            }
        }
        catch
        {
        }
    }
}
