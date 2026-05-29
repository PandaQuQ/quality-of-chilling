using System;

namespace RealTimeWeatherForChill;

internal sealed class WeatherSnapshot
{
    internal string Location { get; }
    internal string Text { get; }
    internal int Code { get; }
    internal int TemperatureCelsius { get; }
    internal WeatherKind Kind { get; }
    internal double Latitude { get; }
    internal double Longitude { get; }
    internal DateTime LocalTime { get; }
    internal DateTime? SunriseTime { get; }
    internal DateTime? SunsetTime { get; }
    internal SolarPhase SolarPhase { get; }

    internal WeatherSnapshot(string location, string text, int code, int temperatureCelsius, double latitude, double longitude, DateTime localTime, DateTime? sunriseTime, DateTime? sunsetTime)
    {
        Location = location;
        Text = text;
        Code = code;
        TemperatureCelsius = temperatureCelsius;
        Kind = WeatherClassifier.Classify(code, text);
        Latitude = latitude;
        Longitude = longitude;
        LocalTime = localTime;
        SunriseTime = sunriseTime;
        SunsetTime = sunsetTime;
        SolarPhase = sunriseTime.HasValue && sunsetTime.HasValue
            ? SolarPhaseClassifier.Classify(localTime, sunriseTime.Value, sunsetTime.Value)
            : SolarPhaseClassifier.ClassifyBySunElevation(latitude, longitude, DateTime.UtcNow);
    }

    internal WeatherSnapshot(string location, string text, int code, int temperatureCelsius, double latitude, double longitude, DateTime localTime, DateTime? sunriseTime, DateTime? sunsetTime, WeatherKind kind, SolarPhase solarPhase)
    {
        Location = location;
        Text = text;
        Code = code;
        TemperatureCelsius = temperatureCelsius;
        Latitude = latitude;
        Longitude = longitude;
        LocalTime = localTime;
        SunriseTime = sunriseTime;
        SunsetTime = sunsetTime;
        Kind = kind;
        SolarPhase = solarPhase;
    }

    internal WeatherSnapshot OverrideConfig(WeatherConfig config)
    {
        var kind = Kind;
        var code = Code;
        var text = Text;
        if (!config.SyncWeather.Value)
        {
            kind = WeatherKind.Clear;
            code = 0;
            text = WeatherLocalizer.WeatherText(0, GameLanguage.English);
        }

        var solarPhase = SolarPhase;
        if (!config.SyncDayNight.Value)
        {
            solarPhase = SolarPhase.Day;
        }

        return new WeatherSnapshot(
            Location,
            text,
            code,
            TemperatureCelsius,
            Latitude,
            Longitude,
            LocalTime,
            SunriseTime,
            SunsetTime,
            kind,
            solarPhase
        );
    }
}

internal sealed class GeoPoint
{
    internal double Latitude { get; }
    internal double Longitude { get; }
    internal string Name { get; }

    internal GeoPoint(double latitude, double longitude, string name)
    {
        Latitude = latitude;
        Longitude = longitude;
        Name = name;
    }
}

internal enum SolarPhase
{
    Day,
    Sunset,
    Night
}

internal enum WeatherKind
{
    Clear,
    Cloudy,
    Rain,
    Snow,
    Fog,
    Storm,
    Unknown
}

internal enum GameLanguage
{
    Japanese,
    English,
    ChineseSimplified,
    ChineseTraditional,
    Portuguese,
    Korean,
    Russian
}
