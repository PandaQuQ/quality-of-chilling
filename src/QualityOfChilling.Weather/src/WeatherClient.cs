using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace RealTimeWeatherForChill;

internal sealed class WeatherClient
{
    private readonly WeatherConfig config;

    internal WeatherClient(WeatherConfig config)
    {
        this.config = config;
    }

    internal IEnumerator Fetch(Action<Result> done)
    {
        GeoPoint? point = null;
        string? error = null;

        if (config.AutoIpLocation.Value)
        {
            yield return FetchIpLocation((value, message) =>
            {
                point = value;
                error = message;
            });
        }
        else
        {
            var location = config.ManualLocation.Value.Trim();
            if (string.IsNullOrWhiteSpace(location))
            {
                done(Result.Fail("未配置手动位置。"));
                yield break;
            }

            if (TryParseLatLon(location, out var manualPoint))
            {
                point = manualPoint;
            }
            else
            {
                yield return FetchGeocoding(location, (value, message) =>
                {
                    point = value;
                    error = message;
                });
            }
        }

        if (point == null)
        {
            done(Result.Fail(error ?? "定位失败。"));
            yield break;
        }

        yield return FetchOpenMeteo(point, done);
    }

    private IEnumerator FetchIpLocation(Action<GeoPoint?, string?> done)
    {
        using var request = UnityWebRequest.Get("https://ipwho.is/");
        request.timeout = Mathf.Clamp(config.TimeoutSeconds.Value, 2, 15);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            done(null, $"IP 定位失败：{request.error}");
            yield break;
        }

        done(PublicApiParser.ParseIpWhoIs(request.downloadHandler.text), "IP 定位响应解析失败。");
    }

    private IEnumerator FetchGeocoding(string location, Action<GeoPoint?, string?> done)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search" +
                  $"?name={UnityWebRequest.EscapeURL(location)}" +
                  "&count=1&language=zh&format=json";
        using var request = UnityWebRequest.Get(url);
        request.timeout = Mathf.Clamp(config.TimeoutSeconds.Value, 2, 15);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            done(null, $"城市定位失败：{request.error}");
            yield break;
        }

        done(PublicApiParser.ParseOpenMeteoGeocoding(request.downloadHandler.text), "城市定位响应解析失败。");
    }

    private IEnumerator FetchOpenMeteo(GeoPoint point, Action<Result> done)
    {
        var url = "https://api.open-meteo.com/v1/forecast" +
                  $"?latitude={point.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  $"&longitude={point.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  "&current=temperature_2m,weather_code&hourly=temperature_2m,weather_code&daily=sunrise,sunset&timezone=auto&forecast_days=1";
        using var request = UnityWebRequest.Get(url);
        request.timeout = Mathf.Clamp(config.TimeoutSeconds.Value, 2, 15);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            done(Result.Fail($"天气请求失败：{request.error}"));
            yield break;
        }

        var snapshot = PublicApiParser.ParseOpenMeteoWeather(request.downloadHandler.text, point);
        done(snapshot == null ? Result.Fail("天气响应解析失败。") : Result.Ok(snapshot, request.downloadHandler.text, point));
    }

    private static bool TryParseLatLon(string location, out GeoPoint point)
    {
        point = null!;
        var parts = location.Split(',');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon))
        {
            return false;
        }

        point = new GeoPoint(lat, lon, location);
        return true;
    }

    internal sealed class Result
    {
        internal WeatherSnapshot? Weather { get; }
        internal string? Error { get; }
        internal string? RawJson { get; }
        internal GeoPoint? Point { get; }

        private Result(WeatherSnapshot? weather, string? error, string? rawJson = null, GeoPoint? point = null)
        {
            Weather = weather;
            Error = error;
            RawJson = rawJson;
            Point = point;
        }

        internal static Result Ok(WeatherSnapshot weather, string rawJson, GeoPoint point) => new(weather, null, rawJson, point);
        internal static Result Fail(string error) => new(null, error);
    }
}
