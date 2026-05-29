using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RealTimeWeatherForChill;

internal static class PublicApiParser
{
    internal static GeoPoint? ParseIpWhoIs(string json)
    {
        try
        {
            var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (root == null || !root.GetBool("success", false))
            {
                return null;
            }

            var city = root.GetString("city", "当前位置");
            var country = root.GetString("country", string.Empty);
            var name = string.IsNullOrWhiteSpace(country) ? city : $"{city}, {country}";
            return new GeoPoint(root.GetDouble("latitude", 0d), root.GetDouble("longitude", 0d), name);
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"解析 IP 定位 JSON 失败：{ex.Message}");
            return null;
        }
    }

    internal static GeoPoint? ParseOpenMeteoGeocoding(string json)
    {
        try
        {
            var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
            var results = root?.GetList("results");
            var first = results?.Count > 0 ? results[0] as Dictionary<string, object> : null;
            if (first == null)
            {
                return null;
            }

            var name = first.GetString("name", "手动位置");
            var admin = first.GetString("admin1", string.Empty);
            var country = first.GetString("country", string.Empty);
            var displayName = string.Join(", ", new[] { name, admin, country }.Where(part => !string.IsNullOrWhiteSpace(part)));
            return new GeoPoint(first.GetDouble("latitude", 0d), first.GetDouble("longitude", 0d), displayName);
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"解析城市定位 JSON 失败：{ex.Message}");
            return null;
        }
    }

    internal static WeatherSnapshot? ParseOpenMeteoWeather(string json, GeoPoint point)
    {
        try
        {
            var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
            var current = root?.GetDict("current");
            if (current == null)
            {
                return null;
            }

            var code = current.GetInt("weather_code", -1);
            var temperature = Mathf.RoundToInt((float)current.GetDouble("temperature_2m", 0d));
            var localTime = ParseLocalTime(current.GetString("time", string.Empty), DateTime.Now);
            var daily = root?.GetDict("daily");
            var sunrise = ParseFirstDailyTime(daily, "sunrise");
            var sunset = ParseFirstDailyTime(daily, "sunset");
            return new WeatherSnapshot(point.Name, WeatherLocalizer.WeatherText(code, GameLanguage.English), code, temperature, point.Latitude, point.Longitude, localTime, sunrise, sunset);
        }
        catch (Exception ex)
        {
            RealTimeWeatherPlugin.Log.LogWarning($"解析天气 JSON 失败：{ex.Message}");
            return null;
        }
    }

    private static DateTime ParseLocalTime(string value, DateTime fallback)
    {
        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;
    }

    private static DateTime? ParseFirstDailyTime(Dictionary<string, object>? daily, string key)
    {
        var values = daily?.GetList(key);
        if (values == null || values.Count == 0 || values[0] == null)
        {
            return null;
        }

        var value = Convert.ToString(values[0], System.Globalization.CultureInfo.InvariantCulture);
        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}

internal static class DictionaryExtensions
{
    internal static Dictionary<string, object>? GetDict(this Dictionary<string, object> dict, string key)
    {
        return dict.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;
    }

    internal static List<object>? GetList(this Dictionary<string, object> dict, string key)
    {
        return dict.TryGetValue(key, out var value) ? value as List<object> : null;
    }

    internal static string GetString(this Dictionary<string, object> dict, string key, string fallback)
    {
        return dict.TryGetValue(key, out var value) ? Convert.ToString(value) ?? fallback : fallback;
    }

    internal static int GetInt(this Dictionary<string, object> dict, string key, int fallback)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value switch
        {
            long l => (int)l,
            int i => i,
            double d => (int)d,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => fallback
        };
    }

    internal static double GetDouble(this Dictionary<string, object> dict, string key, double fallback)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value switch
        {
            double d => d,
            float f => f,
            long l => l,
            int i => i,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback
        };
    }

    internal static bool GetBool(this Dictionary<string, object> dict, string key, bool fallback)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => fallback
        };
    }
}
