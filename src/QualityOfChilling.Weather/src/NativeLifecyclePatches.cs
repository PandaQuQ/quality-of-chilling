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
            SettingUiInjector.RefreshSettingLabels();
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
    internal static void RefreshSettingLabels()
    {
        try
        {
            var settingUiType = AccessTools.TypeByName("Bulbul.SettingUI");
            if (settingUiType == null) return;

            foreach (var component in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (component != null && settingUiType.IsInstanceOfType(component))
                {
                    Inject(component);
                }
            }
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogDebug($"刷新设置菜单语言标签失败：{ex.Message}");
        }
    }

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

        // Dump hierarchy for debugging
        try
        {
            var sb = new System.Text.StringBuilder();
            DumpHierarchy(generalParent, 0, sb);
            RealTimeWeatherPlugin.Log.LogInfo($"[UI Debug] generalParent Hierarchy:\n{sb.ToString()}");
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"Dump hierarchy failed: {ex.Message}");
        }

        // Standard ScrollView hierarchy: ScrollView/Viewport/Content
        var contentTransform = generalParent.Find("ScrollView/Viewport/Content");
        if (contentTransform == null)
        {
            contentTransform = generalParent.Find("ScrollView/Viewport/content");
        }
        if (contentTransform == null)
        {
            contentTransform = generalParent;
            RealTimeWeatherPlugin.Log.LogWarning("未找到常规设置的 ScrollView Content，使用常规设置容器根节点。");
        }
        else
        {
            RealTimeWeatherPlugin.Log.LogInfo("已成功定位常规设置的 ScrollView Content 容器。");
        }

        // Check if already injected in this transform
        var weatherRowTransform = contentTransform.Find("RealTimeWeather_EnableRow");
        var autoLocRowTransform = contentTransform.Find("RealTimeWeather_AutoLocRow");

        if (weatherRowTransform != null && autoLocRowTransform != null)
        {
            UpdateInjectedRowVisuals(weatherRowTransform.gameObject, autoLocRowTransform.gameObject);
            return;
        }

        // Select the best template row from General settings (ChangeAlwaysOnTop) for 100% native layout & scaling
        GameObject? rowTemplate = null;
        var alwaysOnTopTransform = contentTransform.Find("ChangeAlwaysOnTop");
        if (alwaysOnTopTransform != null)
        {
            rowTemplate = alwaysOnTopTransform.gameObject;
            RealTimeWeatherPlugin.Log.LogInfo("成功找到常规设置中的原生‘ChangeAlwaysOnTop’行，将作为克隆模板。");
        }
        else
        {
            rowTemplate = vsyncRow;
            RealTimeWeatherPlugin.Log.LogWarning("未找到 ChangeAlwaysOnTop，回退使用 VSync 作为克隆模板。");
        }

        string btnOnName = (rowTemplate == vsyncRow) ? vsyncAct.name : "AwaysTopOnButton";
        string btnOffName = (rowTemplate == vsyncRow) ? vsyncDeact.name : "AwaysTopOffButton";

        RealTimeWeatherPlugin.Log.LogInfo("开始在游戏设置菜单内原生注入实时天气选项...");

        // 1. Clone Row for Enable Weather
        var weatherRow = UnityEngine.Object.Instantiate(rowTemplate, contentTransform);
        weatherRow.name = "RealTimeWeather_EnableRow";
        weatherRow.SetActive(true);

        string labelText = WeatherLocalizer.GetEnableWeatherText(RealTimeWeatherPlugin.CurrentLanguage);

        var interactableUiType = AccessTools.TypeByName("Bulbul.InteractableUI");
        
        Component? btnOn = FindChildRecursive(weatherRow.transform, btnOnName)?.GetComponent(interactableUiType);
        Component? btnOff = FindChildRecursive(weatherRow.transform, btnOffName)?.GetComponent(interactableUiType);

        if (btnOn == null || btnOff == null)
        {
            RealTimeWeatherPlugin.Log.LogWarning("通过递归名称未找到克隆的‘启用实时天气’按钮，回退至 GetComponentsInChildren。");
            var enableButtons = weatherRow.GetComponentsInChildren(interactableUiType, true);
            if (enableButtons != null && enableButtons.Length >= 2)
            {
                btnOn = enableButtons[0];
                btnOff = enableButtons[1];
            }
        }

        if (btnOn != null && btnOff != null)
        {
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
        var autoLocRow = UnityEngine.Object.Instantiate(rowTemplate, contentTransform);
        autoLocRow.name = "RealTimeWeather_AutoLocRow";
        autoLocRow.SetActive(true);

        string autoLocLabel = WeatherLocalizer.GetAutoLocText(RealTimeWeatherPlugin.CurrentLanguage);

        Component? autoLocBtnOn = FindChildRecursive(autoLocRow.transform, btnOnName)?.GetComponent(interactableUiType);
        Component? autoLocBtnOff = FindChildRecursive(autoLocRow.transform, btnOffName)?.GetComponent(interactableUiType);

        if (autoLocBtnOn == null || autoLocBtnOff == null)
        {
            RealTimeWeatherPlugin.Log.LogWarning("通过递归名称未找到克隆的‘自动 IP 定位’按钮，回退至 GetComponentsInChildren。");
            var autoLocButtons = autoLocRow.GetComponentsInChildren(interactableUiType, true);
            if (autoLocButtons != null && autoLocButtons.Length >= 2)
            {
                autoLocBtnOn = autoLocButtons[0];
                autoLocBtnOff = autoLocButtons[1];
            }
        }

        if (autoLocBtnOn != null && autoLocBtnOff != null)
        {
            SetRowLabel(autoLocRow, autoLocLabel, autoLocBtnOn, autoLocBtnOff);

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
                SetIsUsing(autoLocBtnOn, true);
                SetIsUsing(autoLocBtnOff, false);
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
                SetIsUsing(autoLocBtnOn, false);
                SetIsUsing(autoLocBtnOff, true);
                RealTimeWeatherPlugin.Instance?.TriggerRefreshFromGameReady();
            };

            setupMethod?.Invoke(autoLocBtnOn, new object[] { onAct });
            setupMethod?.Invoke(autoLocBtnOff, new object[] { onDeact });

            var config = RealTimeWeatherPlugin.Instance?.Config;
            bool isAutoIp = config != null && config.Bind("Location", "AutoIpLocation", false).Value;
            SetIsUsing(autoLocBtnOn, isAutoIp);
            SetIsUsing(autoLocBtnOff, !isAutoIp);
        }
        else
        {
            SetText(autoLocRow, autoLocLabel);
        }

        // Apply visual layout adjustments to position injected rows properly
        try
        {
            PositionInjectedRows(weatherRow, autoLocRow, contentTransform);
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"调整注入行位置失败：{ex.Message}");
        }

        RealTimeWeatherPlugin.Log.LogInfo("已成功在常规设置菜单内注入“启用实时天气”和“自动 IP 定位”两个原生选项。");
    }

    private static void UpdateInjectedRowVisuals(GameObject weatherRow, GameObject autoLocRow)
    {
        string labelText = WeatherLocalizer.GetEnableWeatherText(RealTimeWeatherPlugin.CurrentLanguage);
        string autoLocLabel = WeatherLocalizer.GetAutoLocText(RealTimeWeatherPlugin.CurrentLanguage);

        var interactableUiType = AccessTools.TypeByName("Bulbul.InteractableUI");
        
        var weatherButtons = weatherRow.GetComponentsInChildren(interactableUiType, true);
        Component? btnOn = null;
        Component? btnOff = null;
        if (weatherButtons != null && weatherButtons.Length >= 2)
        {
            btnOn = weatherButtons[0];
            btnOff = weatherButtons[1];
        }

        if (btnOn != null && btnOff != null)
        {
            SetRowLabel(weatherRow, labelText, btnOn, btnOff);
            var config = RealTimeWeatherPlugin.Instance?.Config;
            bool isEnabled = config != null && config.Bind("General", "Enabled", true).Value;
            SetIsUsing(btnOn, isEnabled);
            SetIsUsing(btnOff, !isEnabled);
        }
        else
        {
            SetText(weatherRow, labelText);
        }

        var autoLocButtons = autoLocRow.GetComponentsInChildren(interactableUiType, true);
        Component? autoLocBtnOn = null;
        Component? autoLocBtnOff = null;
        if (autoLocButtons != null && autoLocButtons.Length >= 2)
        {
            autoLocBtnOn = autoLocButtons[0];
            autoLocBtnOff = autoLocButtons[1];
        }

        if (autoLocBtnOn != null && autoLocBtnOff != null)
        {
            SetRowLabel(autoLocRow, autoLocLabel, autoLocBtnOn, autoLocBtnOff);
            var config = RealTimeWeatherPlugin.Instance?.Config;
            bool isAutoIp = config != null && config.Bind("Location", "AutoIpLocation", false).Value;
            SetIsUsing(autoLocBtnOn, isAutoIp);
            SetIsUsing(autoLocBtnOff, !isAutoIp);
        }
        else
        {
            SetText(autoLocRow, autoLocLabel);
        }

        // Apply Sibling Index Fix
        weatherRow.transform.SetAsFirstSibling();
        autoLocRow.transform.SetAsFirstSibling();

        // Apply Mask Refresh Toggle Hack
        var rectMask = weatherRow.transform.parent?.parent?.GetComponent<UnityEngine.UI.RectMask2D>();
        if (rectMask != null)
        {
            rectMask.enabled = false;
            rectMask.enabled = true;
        }
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

    private static Transform? FindChildRecursive(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.name == name)
            {
                return child;
            }
            var result = FindChildRecursive(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private static void DumpHierarchy(Transform t, int depth, System.Text.StringBuilder sb)
    {
        string indent = new string('-', depth * 2);
        sb.AppendLine($"{indent}{t.name} (active={t.gameObject.activeSelf}, pos={t.localPosition}, size={(t as RectTransform)?.rect.size})");
        for (int i = 0; i < t.childCount; i++)
        {
            DumpHierarchy(t.GetChild(i), depth + 1, sb);
        }
    }

    private static void PositionInjectedRows(GameObject weatherRow, GameObject autoLocRow, Transform contentTransform)
    {
        var children = new List<RectTransform>();
        for (int i = 0; i < contentTransform.childCount; i++)
        {
            var child = contentTransform.GetChild(i) as RectTransform;
            if (child != null && child.gameObject.activeSelf && 
                child.gameObject != weatherRow && child.gameObject != autoLocRow)
            {
                children.Add(child);
            }
        }

        if (children.Count == 0)
        {
            RealTimeWeatherPlugin.Log.LogWarning("常规设置 Content 容器中没有找到任何原生子项，无法定位注入行。");
            return;
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

                var contentRect = contentTransform.GetComponent<RectTransform>();
                if (contentRect != null)
                {
                    Vector2 size = contentRect.sizeDelta;
                    size.y += Mathf.Abs(spacing) * 2f;
                    contentRect.sizeDelta = size;
                    RealTimeWeatherPlugin.Log.LogInfo($"已成功调整常规设置 Content 容器高度，新增了 {Mathf.Abs(spacing) * 2f}px，当前总高度 Y: {contentRect.sizeDelta.y}px");
                }

                // Apply Sibling Index Fix
                weatherRow.transform.SetAsFirstSibling();
                autoLocRow.transform.SetAsFirstSibling();

                // Apply Mask Refresh Toggle Hack
                var rectMask = contentTransform.parent?.GetComponent<UnityEngine.UI.RectMask2D>();
                if (rectMask != null)
                {
                    rectMask.enabled = false;
                    rectMask.enabled = true;
                }
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
