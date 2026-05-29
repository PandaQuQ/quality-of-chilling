using System;
using System.Reflection;
using UnityEngine;

namespace RealTimeWeatherForChill;

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
                
                if (!ReferenceEquals(RealTimeWeatherPlugin.Instance, null))
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
