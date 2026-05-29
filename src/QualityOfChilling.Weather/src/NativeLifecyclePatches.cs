using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RealTimeWeatherForChill;



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
                textProperty.SetValue(textObject, StripExistingWeather(currentText) + " | " + weatherText, null);
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
        try
        {
            var dumpType = AccessTools.TypeByName("Bulbul.InteractableUI");
            if (dumpType != null)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[InteractableUI Dump] FullName: {dumpType.FullName}");
                sb.AppendLine("--- Fields ---");
                foreach (var field in dumpType.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    sb.AppendLine($" - {field.FieldType.Name} {field.Name}");
                }
                sb.AppendLine("--- Properties ---");
                foreach (var prop in dumpType.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    sb.AppendLine($" - {prop.PropertyType.Name} {prop.Name} (Get={prop.GetMethod != null}, Set={prop.SetMethod != null})");
                }
                sb.AppendLine("--- Methods ---");
                foreach (var method in dumpType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var pars = string.Join(", ", System.Linq.Enumerable.Select(method.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                    sb.AppendLine($" - {method.ReturnType.Name} {method.Name}({pars})");
                }
                RealTimeWeatherPlugin.Log.LogInfo(sb.ToString());
            }
            else
            {
                RealTimeWeatherPlugin.Log.LogWarning("[InteractableUI Dump] Bulbul.InteractableUI not found!");
            }
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"Dump Bulbul.InteractableUI failed: {ex.Message}");
        }

        try
        {
            var viewType = AccessTools.TypeByName("Bulbul.WindowViewType");
            if (viewType != null && viewType.IsEnum)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[WindowViewType Dump] Names:");
                foreach (var name in Enum.GetNames(viewType))
                {
                    sb.AppendLine($" - {name}");
                }
                RealTimeWeatherPlugin.Log.LogInfo(sb.ToString());
            }
            else
            {
                RealTimeWeatherPlugin.Log.LogWarning("[WindowViewType Dump] Bulbul.WindowViewType not found!");
            }
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"Dump WindowViewType failed: {ex.Message}");
        }

        try
        {
            var uiType = AccessTools.TypeByName("Bulbul.SettingUI");
            if (uiType != null)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[SettingUI Dump] FullName: {uiType.FullName}");
                sb.AppendLine("--- Fields ---");
                foreach (var field in uiType.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    sb.AppendLine($" - {field.FieldType.Name} {field.Name}");
                }
                sb.AppendLine("--- Properties ---");
                foreach (var prop in uiType.GetProperties(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    sb.AppendLine($" - {prop.PropertyType.Name} {prop.Name} (Get={prop.GetMethod != null}, Set={prop.SetMethod != null})");
                }
                sb.AppendLine("--- Methods ---");
                foreach (var method in uiType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var pars = string.Join(", ", System.Linq.Enumerable.Select(method.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                    sb.AppendLine($" - {method.ReturnType.Name} {method.Name}({pars})");
                }
                RealTimeWeatherPlugin.Log.LogInfo(sb.ToString());
            }
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"Dump SettingUI failed: {ex.Message}");
        }

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

        // Clean up legacy rows if they exist to prevent double injection
        var legacyEnableRow = contentTransform.Find("RealTimeWeather_EnableRow");
        if (legacyEnableRow != null) UnityEngine.Object.Destroy(legacyEnableRow.gameObject);
        var legacyAutoLocRow = contentTransform.Find("RealTimeWeather_AutoLocRow");
        if (legacyAutoLocRow != null) UnityEngine.Object.Destroy(legacyAutoLocRow.gameObject);

        // Check if already injected in this transform under new names
        var weatherRowTransform = contentTransform.Find("RealTimeWeather_SyncWeatherRow");
        var dayNightRowTransform = contentTransform.Find("RealTimeWeather_SyncDayNightRow");

        if (weatherRowTransform != null && dayNightRowTransform != null)
        {
            UpdateInjectedRowVisuals(weatherRowTransform.gameObject, dayNightRowTransform.gameObject);
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
        weatherRow.name = "RealTimeWeather_SyncWeatherRow";
        weatherRow.SetActive(true);

        string labelText = WeatherLocalizer.GetEnableWeatherText(RealTimeWeatherPlugin.CurrentLanguage);

        var interactableUiType = AccessTools.TypeByName("Bulbul.InteractableUI");
        
        Component? btnOn = FindChildRecursive(weatherRow.transform, btnOnName)?.GetComponent(interactableUiType);
        Component? btnOff = FindChildRecursive(weatherRow.transform, btnOffName)?.GetComponent(interactableUiType);

        if (btnOn == null || btnOff == null)
        {
            RealTimeWeatherPlugin.Log.LogWarning("通过递归名称未找到克隆的'真实天气'按钮，回退至 GetComponentsInChildren。");
            var enableButtons = weatherRow.GetComponentsInChildren(interactableUiType, true);
            if (enableButtons != null && enableButtons.Length >= 2)
            {
                btnOn = enableButtons[0];
                btnOff = enableButtons[1];
            }
        }

        if (btnOn != null && btnOff != null)
        {
            // Define actions first so SetupInteractable can wire EventTrigger
            Action weatherOnAct = () =>
            {
                RealTimeWeatherPlugin.Log.LogInfo("用户在设置菜单启用了真实天气");
                var p = RealTimeWeatherPlugin.Instance;
                if (!ReferenceEquals(p, null) && p.WeatherConfig != null)
                {
                    p.WeatherConfig.SyncWeather.Value = true;
                    p.Config.Save();
                }
                SetIsUsing(btnOn, true);
                SetIsUsing(btnOff, false);
                RealTimeWeatherPlugin.Instance?.ReapplyCurrentWeather();
            };

            Action weatherOffAct = () =>
            {
                RealTimeWeatherPlugin.Log.LogInfo("用户在设置菜单关闭了真实天气");
                var p = RealTimeWeatherPlugin.Instance;
                if (!ReferenceEquals(p, null) && p.WeatherConfig != null)
                {
                    p.WeatherConfig.SyncWeather.Value = false;
                    p.Config.Save();
                }
                SetIsUsing(btnOn, false);
                SetIsUsing(btnOff, true);
                RealTimeWeatherPlugin.Instance?.ReapplyCurrentWeather();
                CurrentDateAndTimeUiPatch.RefreshAll();
            };

            SetupInteractable(btnOn, weatherOnAct);
            SetupInteractable(btnOff, weatherOffAct);
            SetRowLabel(weatherRow, labelText, btnOn, btnOff);

            var plugin = RealTimeWeatherPlugin.Instance;
            bool isEnabled = !ReferenceEquals(plugin, null) && plugin.WeatherConfig != null && plugin.WeatherConfig.SyncWeather.Value;
            SetIsUsing(btnOn, isEnabled);
            SetIsUsing(btnOff, !isEnabled);
        }
        else
        {
            SetText(weatherRow, labelText);
        }

        // 2. Clone Row for Real-time Day/Night
        var dayNightRow = UnityEngine.Object.Instantiate(rowTemplate, contentTransform);
        dayNightRow.name = "RealTimeWeather_SyncDayNightRow";
        dayNightRow.SetActive(true);

        string dayNightLabel = WeatherLocalizer.GetSyncDayNightText(RealTimeWeatherPlugin.CurrentLanguage);

        Component? dayNightBtnOn = FindChildRecursive(dayNightRow.transform, btnOnName)?.GetComponent(interactableUiType);
        Component? dayNightBtnOff = FindChildRecursive(dayNightRow.transform, btnOffName)?.GetComponent(interactableUiType);

        if (dayNightBtnOn == null || dayNightBtnOff == null)
        {
            RealTimeWeatherPlugin.Log.LogWarning("通过递归名称未找到克隆的‘真实日夜’按钮，回退至 GetComponentsInChildren。");
            var dayNightButtons = dayNightRow.GetComponentsInChildren(interactableUiType, true);
            if (dayNightButtons != null && dayNightButtons.Length >= 2)
            {
                dayNightBtnOn = dayNightButtons[0];
                dayNightBtnOff = dayNightButtons[1];
            }
        }

        if (dayNightBtnOn != null && dayNightBtnOff != null)
        {
            // Define actions first so SetupInteractable can wire EventTrigger
            Action dayNightOnAct = () =>
            {
                RealTimeWeatherPlugin.Log.LogInfo("用户在设置菜单启用了真实日夜");
                var p = RealTimeWeatherPlugin.Instance;
                if (!ReferenceEquals(p, null) && p.WeatherConfig != null)
                {
                    p.WeatherConfig.SyncDayNight.Value = true;
                    p.Config.Save();
                }
                SetIsUsing(dayNightBtnOn, true);
                SetIsUsing(dayNightBtnOff, false);
                RealTimeWeatherPlugin.Instance?.ReapplyCurrentWeather();
            };

            Action dayNightOffAct = () =>
            {
                RealTimeWeatherPlugin.Log.LogInfo("用户在设置菜单关闭了真实日夜");
                var p = RealTimeWeatherPlugin.Instance;
                if (!ReferenceEquals(p, null) && p.WeatherConfig != null)
                {
                    p.WeatherConfig.SyncDayNight.Value = false;
                    p.Config.Save();
                }
                SetIsUsing(dayNightBtnOn, false);
                SetIsUsing(dayNightBtnOff, true);
                RealTimeWeatherPlugin.Instance?.ReapplyCurrentWeather();
            };

            SetupInteractable(dayNightBtnOn, dayNightOnAct);
            SetupInteractable(dayNightBtnOff, dayNightOffAct);
            SetRowLabel(dayNightRow, dayNightLabel, dayNightBtnOn, dayNightBtnOff);

            var plugin = RealTimeWeatherPlugin.Instance;
            bool isDayNight = !ReferenceEquals(plugin, null) && plugin.WeatherConfig != null && plugin.WeatherConfig.SyncDayNight.Value;
            SetIsUsing(dayNightBtnOn, isDayNight);
            SetIsUsing(dayNightBtnOff, !isDayNight);
        }
        else
        {
            SetText(dayNightRow, dayNightLabel);
        }

        // Apply visual layout adjustments to position injected rows properly
        try
        {
            PositionInjectedRows(weatherRow, dayNightRow, contentTransform);
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"调整注入行位置失败：{ex.Message}");
        }

        RealTimeWeatherPlugin.Log.LogInfo("已成功在常规设置菜单内注入“真实天气”和“真实日夜”两个原生选项。");
    }

    private static void UpdateInjectedRowVisuals(GameObject weatherRow, GameObject dayNightRow)
    {
        string labelText = WeatherLocalizer.GetEnableWeatherText(RealTimeWeatherPlugin.CurrentLanguage);
        string dayNightLabel = WeatherLocalizer.GetSyncDayNightText(RealTimeWeatherPlugin.CurrentLanguage);

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
            var plugin = RealTimeWeatherPlugin.Instance;
            bool isEnabled = !ReferenceEquals(plugin, null) && plugin.WeatherConfig != null && plugin.WeatherConfig.SyncWeather.Value;
            SetIsUsing(btnOn, isEnabled);
            SetIsUsing(btnOff, !isEnabled);
        }
        else
        {
            SetText(weatherRow, labelText);
        }

        var dayNightButtons = dayNightRow.GetComponentsInChildren(interactableUiType, true);
        Component? dayNightBtnOn = null;
        Component? dayNightBtnOff = null;
        if (dayNightButtons != null && dayNightButtons.Length >= 2)
        {
            dayNightBtnOn = dayNightButtons[0];
            dayNightBtnOff = dayNightButtons[1];
        }

        if (dayNightBtnOn != null && dayNightBtnOff != null)
        {
            SetRowLabel(dayNightRow, dayNightLabel, dayNightBtnOn, dayNightBtnOff);
            var plugin = RealTimeWeatherPlugin.Instance;
            bool isDayNight = !ReferenceEquals(plugin, null) && plugin.WeatherConfig != null && plugin.WeatherConfig.SyncDayNight.Value;
            SetIsUsing(dayNightBtnOn, isDayNight);
            SetIsUsing(dayNightBtnOff, !isDayNight);
        }
        else
        {
            SetText(dayNightRow, dayNightLabel);
        }

        // Apply Sibling Index Fix
        weatherRow.transform.SetAsFirstSibling();
        dayNightRow.transform.SetAsFirstSibling();

        // Apply Mask Refresh Toggle Hack
        var rectMask = weatherRow.transform.parent?.parent?.GetComponent("UnityEngine.UI.RectMask2D") as Behaviour;
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
                prop?.SetValue(comp, text, null);
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
                prop?.SetValue(comp, text, null);
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

    private static void PositionInjectedRows(GameObject weatherRow, GameObject dayNightRow, Transform contentTransform)
    {
        var children = new List<RectTransform>();
        for (int i = 0; i < contentTransform.childCount; i++)
        {
            var child = contentTransform.GetChild(i) as RectTransform;
            if (child != null && child.gameObject.activeSelf && 
                child.gameObject != weatherRow && child.gameObject != dayNightRow)
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
            var dayNightRect = dayNightRow.GetComponent<RectTransform>();

            if (weatherRect != null && dayNightRect != null)
            {
                Vector2 posWeather = weatherRect.anchoredPosition;
                posWeather.y = lowestRow.anchoredPosition.y + spacing;
                posWeather.x = lowestRow.anchoredPosition.x;
                weatherRect.anchoredPosition = posWeather;

                Vector2 posDayNight = dayNightRect.anchoredPosition;
                posDayNight.y = posWeather.y + spacing;
                posDayNight.x = lowestRow.anchoredPosition.x;
                dayNightRect.anchoredPosition = posDayNight;

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
                dayNightRow.transform.SetAsFirstSibling();

                // Apply Mask Refresh Toggle Hack
                var rectMask = contentTransform.parent?.GetComponent("UnityEngine.UI.RectMask2D") as Behaviour;
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
            var component = button as Component;

            // Set the backing field directly
            var backingField = type.GetField("<IsUsing>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (backingField != null)
            {
                backingField.SetValue(button, value);
            }
            else
            {
                var prop = type.GetProperty("IsUsing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                prop?.SetValue(button, value, null);
            }

            // Directly manipulate Image alpha for visual feedback
            // _usingImage: shown when "active/selected", _baseImage: shown when "inactive/default"
            var usingImageField = type.GetField("_usingImage", BindingFlags.Instance | BindingFlags.NonPublic);
            var baseImageField = type.GetField("_baseImage", BindingFlags.Instance | BindingFlags.NonPublic);
            var usingAlphaField = type.GetField("_usingImageAlpha", BindingFlags.Instance | BindingFlags.NonPublic);
            var baseAlphaField = type.GetField("_baseImageAlpha", BindingFlags.Instance | BindingFlags.NonPublic);

            float usingAlpha = 1f;
            float baseAlpha = 1f;
            if (usingAlphaField != null) usingAlpha = (float)usingAlphaField.GetValue(button);
            if (baseAlphaField != null) baseAlpha = (float)baseAlphaField.GetValue(button);

            if (usingImageField?.GetValue(button) is Array usingImages)
            {
                foreach (var img in usingImages)
                {
                    if (img != null)
                    {
                        var colorProp = img.GetType().GetProperty("color");
                        if (colorProp != null)
                        {
                            var c = (Color)colorProp.GetValue(img);
                            c.a = value ? usingAlpha : 0f;
                            colorProp.SetValue(img, c, null);
                        }
                    }
                }
            }

            if (baseImageField?.GetValue(button) is Array baseImages)
            {
                foreach (var img in baseImages)
                {
                    if (img != null)
                    {
                        var colorProp = img.GetType().GetProperty("color");
                        if (colorProp != null)
                        {
                            var c = (Color)colorProp.GetValue(img);
                            c.a = value ? 0f : baseAlpha;
                            colorProp.SetValue(img, c, null);
                        }
                    }
                }
            }

        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"SetIsUsing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Completely bypasses the broken InteractableUI R3 event system for cloned buttons.
    /// Instead, adds a Unity EventTrigger for direct click handling and sets _isFinishSetup=true.
    /// </summary>
    private static void SetupInteractable(Component btn, Action clickAction)
    {
        if (btn == null) return;
        try
        {
            var interactableUiType = btn.GetType();
            var go = btn.gameObject;

            // 1. Mark setup as finished so any internal checks pass
            var finishSetupField = interactableUiType.GetField("_isFinishSetup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (finishSetupField != null)
            {
                finishSetupField.SetValue(btn, true);
            }

            // 2. Set _onInteractAction as a fallback (in case the native event chain works)
            var interactField = interactableUiType.GetField("_onInteractAction", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (interactField != null)
            {
                interactField.SetValue(btn, clickAction);
            }

            // 3. Kill any stale tweens from the cloned template
            var killTweensMethod = interactableUiType.GetMethod("KillAllTweens", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            killTweensMethod?.Invoke(btn, null);

            // 4. Clear stale R3 disposable from clone
            var disposableField = interactableUiType.GetField("_disposable", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (disposableField != null)
            {
                disposableField.SetValue(btn, null);
            }

            // 5. Add a direct Unity Button for Click handling - this bypasses R3 entirely
            var btnType = AccessTools.TypeByName("UnityEngine.UI.Button");
            if (btnType != null)
            {
                var unityBtn = go.GetComponent(btnType);
                if (unityBtn == null)
                {
                    unityBtn = go.AddComponent(btnType);
                }

                var onClickProp = btnType.GetProperty("onClick");
                if (onClickProp != null)
                {
                    var onClick = onClickProp.GetValue(unityBtn);
                    var addListenerMethod = onClick?.GetType().GetMethod("AddListener");
                    if (addListenerMethod != null)
                    {
                        var action = new UnityEngine.Events.UnityAction(clickAction);
                        addListenerMethod.Invoke(onClick, new object[] { action });
                    }
                }
            }

            // 6. Ensure the button has a raycast target (Image with raycastTarget=true)
            var imgType = AccessTools.TypeByName("UnityEngine.UI.Image");
            if (imgType != null)
            {
                var images = go.GetComponentsInChildren(imgType, true);
                bool hasRaycastTarget = false;
                foreach (var img in images)
                {
                    if (img != null)
                    {
                        var raycastProp = imgType.GetProperty("raycastTarget");
                        if (raycastProp != null && (bool)raycastProp.GetValue(img))
                        {
                            hasRaycastTarget = true;
                            break;
                        }
                    }
                }

                if (!hasRaycastTarget && images.Length > 0)
                {
                    var raycastProp = imgType.GetProperty("raycastTarget");
                    raycastProp?.SetValue(images[0], true, null);
                }
            }

            // 7. Also add a Graphic (invisible) on the button GO itself if it doesn't have one
            var graphicType = AccessTools.TypeByName("UnityEngine.UI.Graphic");
            if (graphicType != null && imgType != null)
            {
                var graphic = go.GetComponent(graphicType);
                if (graphic == null)
                {
                    var img = go.AddComponent(imgType);
                    var colorProp = imgType.GetProperty("color");
                    colorProp?.SetValue(img, new Color(0, 0, 0, 0), null); // Fully transparent
                    var raycastProp = imgType.GetProperty("raycastTarget");
                    raycastProp?.SetValue(img, true, null);
                }
            }
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"SetupInteractable failed for {btn.name}: {ex}");
        }
    }
}
