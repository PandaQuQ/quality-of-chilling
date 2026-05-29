using System;
using System.Collections.Generic;
using System.Reflection;
using Bulbul;
using HarmonyLib;
using UnityEngine;

namespace RealTimeWeatherForChill;

internal sealed class NativeGameBridge
{
    private readonly WeatherConfig config;
    private float nextScanTime;
    private static readonly Dictionary<string, MonoBehaviour> controllers = new();
    private string? lastAppliedEnvironmentKey;

    internal NativeGameBridge(WeatherConfig config)
    {
        this.config = config;
    }

    internal void ForceScan()
    {
        nextScanTime = 0f;
        ScanControllers();
    }

    internal void ResetAppliedState()
    {
        lastAppliedEnvironmentKey = null;
    }

    internal void Tick(WeatherSnapshot? weather)
    {
        if (Time.unscaledTime >= nextScanTime)
        {
            nextScanTime = Time.unscaledTime + 10f;
            ScanControllers();
        }

        if (weather != null)
        {
            ApplyWeather(weather);
        }
        else
        {
            ApplyFallbackDayNight();
        }
    }

    private void ApplyFallbackDayNight()
    {
        if (!config.SyncDayNight.Value)
        {
            ApplyNativeState(SolarPhase.Day, WeatherKind.Clear);
            return;
        }

        var hour = DateTime.Now.Hour;
        SolarPhase phase = SolarPhase.Day;
        if (hour is >= 18 and < 19)
        {
            phase = SolarPhase.Sunset;
        }
        else if (hour is >= 19 or < 6)
        {
            phase = SolarPhase.Night;
        }

        ApplyNativeState(phase, WeatherKind.Clear);
    }

    internal void ApplyWeather(WeatherSnapshot weather)
    {
        SolarPhase targetPhase = config.SyncDayNight.Value ? weather.SolarPhase : SolarPhase.Day;
        WeatherKind targetKind = config.SyncWeather.Value ? weather.Kind : WeatherKind.Clear;

        ApplyNativeState(targetPhase, targetKind);
    }

    private void ApplyNativeState(SolarPhase phase, WeatherKind weather)
    {
        var environmentKey = $"{phase}:{weather}";
        if (lastAppliedEnvironmentKey == environmentKey)
        {
            return;
        }

        ScanControllers();

        string baseTarget = phase.ToString();
        bool isBadWeather = weather is WeatherKind.Rain or WeatherKind.Storm or WeatherKind.Snow or WeatherKind.Fog or WeatherKind.Cloudy;

        if (isBadWeather && phase != SolarPhase.Night)
        {
            baseTarget = "Cloudy";
        }

        ChangeBaseEnvironment(baseTarget);

        bool targetLightRain = weather == WeatherKind.Rain;
        bool targetHeavyRain = false;
        bool targetThunderRain = weather == WeatherKind.Storm;
        bool targetSnow = weather == WeatherKind.Snow;

        SetEnvironmentState("LightRain", targetLightRain);
        SetEnvironmentState("HeavyRain", targetHeavyRain);
        SetEnvironmentState("ThunderRain", targetThunderRain);
        SetEnvironmentState("Snow", targetSnow);

        lastAppliedEnvironmentKey = environmentKey;
        RealTimeWeatherPlugin.Log.LogInfo($"[NativeGameBridge] Applied native state - Phase: {phase}, Weather: {weather} (Base: {baseTarget}, LightRain: {targetLightRain}, ThunderRain: {targetThunderRain}, Snow: {targetSnow})");
    }

    private static MonoBehaviour? FindEnvironmentUi()
    {
        var uiType = AccessTools.TypeByName("Bulbul.EnvironmentUI");
        if (uiType == null) return null;

        foreach (var obj in Resources.FindObjectsOfTypeAll(uiType))
        {
            if (obj is MonoBehaviour mono && mono.gameObject.scene.rootCount != 0)
            {
                return mono;
            }
        }
        return null;
    }

    private static void ChangeBaseEnvironment(string targetEnvName)
    {
        var ui = FindEnvironmentUi();
        if (ui == null)
        {
            return;
        }

        try
        {
            var changeTimeMethod = ui.GetType().GetMethod("ChangeTime", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (changeTimeMethod != null)
            {
                var paramType = changeTimeMethod.GetParameters()[0].ParameterType;
                object enumVal = Enum.Parse(paramType, targetEnvName);
                changeTimeMethod.Invoke(ui, new object[] { enumVal });
            }
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"[NativeGameBridge] ChangeBaseEnvironment to {targetEnvName} failed: {ex.Message}");
        }
    }

    private static void ScanControllers()
    {
        var controllerType = AccessTools.TypeByName("Bulbul.EnvironmentController");
        if (controllerType == null) return;

        var keys = new List<string>(controllers.Keys);
        foreach (var key in keys)
        {
            if (controllers[key] == null)
            {
                controllers.Remove(key);
            }
        }

        foreach (var component in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (component == null || component.gameObject.scene.rootCount == 0 || !controllerType.IsInstanceOfType(component))
            {
                continue;
            }

            try
            {
                var envTypeProp = component.GetType().GetProperty("EnvironmentType", BindingFlags.Instance | BindingFlags.Public);
                var envTypeVal = envTypeProp?.GetValue(component);
                if (envTypeVal != null)
                {
                    controllers[envTypeVal.ToString()] = component;
                }
            }
            catch {}
        }
    }

    private static bool IsEnvironmentActive(string name)
    {
        var saveData = SaveDataManager.Instance;
        if (saveData == null || saveData.EnviromentData == null) return false;

        if (Enum.TryParse<WindowViewType>(name, true, out var windowType))
        {
            if (saveData.EnviromentData.WindowViewDic != null &&
                saveData.EnviromentData.WindowViewDic.TryGetValue(windowType, out var viewData))
            {
                return viewData.IsActive;
            }
        }

        if (Enum.TryParse<AmbientSoundType>(name, true, out var soundType))
        {
            if (saveData.EnviromentData.AmbientSoundDic != null &&
                saveData.EnviromentData.AmbientSoundDic.TryGetValue(soundType, out var soundData))
            {
                return soundData.SoundVolume > 0f && !soundData.IsMuteAmbient;
            }
        }

        return false;
    }

    private static void SetEnvironmentState(string name, bool targetState)
    {
        if (!controllers.TryGetValue(name, out var ctrl) || ctrl == null)
        {
            return;
        }

        bool currentState = IsEnvironmentActive(name);
        if (currentState == targetState)
        {
            return;
        }

        try
        {
            var method = ctrl.GetType().GetMethod("OnClickButtonMainIcon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(ctrl, null);
            }
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"[NativeGameBridge] Failed to set environment state for {name}: {ex.Message}");
        }
    }
}
