using System.Collections;
using UnityEngine;

namespace RealTimeWeatherForChill;

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

    internal Coroutine StartPluginCoroutine(IEnumerator routine)
    {
        return StartCoroutine(routine);
    }

    internal void StopPluginCoroutine(Coroutine routine)
    {
        StopCoroutine(routine);
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
