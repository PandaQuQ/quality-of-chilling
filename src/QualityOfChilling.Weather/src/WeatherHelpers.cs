using System;

namespace RealTimeWeatherForChill;

internal static class WeatherLocalizer
{
    internal static string WeatherText(int code, GameLanguage language)
    {
        return language switch
        {
            GameLanguage.Japanese => WeatherTextJa(code),
            GameLanguage.ChineseSimplified => WeatherTextZhHans(code),
            GameLanguage.ChineseTraditional => WeatherTextZhHant(code),
            GameLanguage.Portuguese => WeatherTextPt(code),
            GameLanguage.Korean => WeatherTextKo(code),
            GameLanguage.Russian => WeatherTextRu(code),
            _ => WeatherTextEn(code)
        };
    }

    internal static string GetEnableWeatherText(GameLanguage language)
    {
        return language switch
        {
            GameLanguage.ChineseSimplified => "真实天气",
            GameLanguage.ChineseTraditional => "真實天氣",
            GameLanguage.Japanese => "リアルタイム天気",
            GameLanguage.Korean => "실시간 날씨",
            GameLanguage.Portuguese => "Tempo em Tempo Real",
            GameLanguage.Russian => "Реальная погода",
            _ => "Real-time Weather"
        };
    }

    internal static string GetSyncDayNightText(GameLanguage language)
    {
        return language switch
        {
            GameLanguage.ChineseSimplified => "真实日夜",
            GameLanguage.ChineseTraditional => "真實日夜",
            GameLanguage.Japanese => "リアルタイム日夜",
            GameLanguage.Korean => "실시간 낮과 밤",
            GameLanguage.Portuguese => "Ciclo Dia/Noite Real",
            GameLanguage.Russian => "Реальное время суток",
            _ => "Real-time Day/Night"
        };
    }

    private static string WeatherTextEn(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 => "Snow",
        77 => "Snow grains",
        80 or 81 or 82 => "Showers",
        85 or 86 => "Snow showers",
        95 or 96 or 99 => "Thunderstorm",
        _ => "Unknown"
    };

    private static string WeatherTextZhHans(int code) => code switch
    {
        0 => "晴",
        1 => "大部晴朗",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻毛毛雨",
        61 or 63 or 65 => "雨",
        66 or 67 => "冻雨",
        71 or 73 or 75 => "雪",
        77 => "雪粒",
        80 or 81 or 82 => "阵雨",
        85 or 86 => "阵雪",
        95 or 96 or 99 => "雷暴",
        _ => "未知"
    };

    private static string WeatherTextZhHant(int code) => code switch
    {
        0 => "晴",
        1 => "大致晴朗",
        2 => "多雲",
        3 => "陰",
        45 or 48 => "霧",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "凍毛毛雨",
        61 or 63 or 65 => "雨",
        66 or 67 => "凍雨",
        71 or 73 or 75 => "雪",
        77 => "雪粒",
        80 or 81 or 82 => "陣雨",
        85 or 86 => "陣雪",
        95 or 96 or 99 => "雷暴",
        _ => "未知"
    };

    private static string WeatherTextJa(int code) => code switch
    {
        0 => "晴れ",
        1 => "ほぼ晴れ",
        2 => "一部曇り",
        3 => "曇り",
        45 or 48 => "霧",
        51 or 53 or 55 => "霧雨",
        56 or 57 => "着氷性霧雨",
        61 or 63 or 65 => "雨",
        66 or 67 => "着氷性雨",
        71 or 73 or 75 => "雪",
        77 => "細雪",
        80 or 81 or 82 => "にわか雨",
        85 or 86 => "にわか雪",
        95 or 96 or 99 => "雷雨",
        _ => "不明"
    };

    private static string WeatherTextPt(int code) => code switch
    {
        0 => "Céu limpo",
        1 => "Predom. limpo",
        2 => "Parcialmente nublado",
        3 => "Nublado",
        45 or 48 => "Nevoeiro",
        51 or 53 or 55 => "Chuvisco",
        56 or 57 => "Chuvisco congelante",
        61 or 63 or 65 => "Chuva",
        66 or 67 => "Chuva congelante",
        71 or 73 or 75 => "Neve",
        77 => "Grãos de neve",
        80 or 81 or 82 => "Aguaceiros",
        85 or 86 => "Aguaceiros de neve",
        95 or 96 or 99 => "Trovoada",
        _ => "Desconhecido"
    };

    private static string WeatherTextKo(int code) => code switch
    {
        0 => "맑음",
        1 => "대체로 맑음",
        2 => "구름 조금",
        3 => "흐림",
        45 or 48 => "안개",
        51 or 53 or 55 => "이슬비",
        56 or 57 => "어는 이슬비",
        61 or 63 or 65 => "비",
        66 or 67 => "어는 비",
        71 or 73 or 75 => "눈",
        77 => "싸락눈",
        80 or 81 or 82 => "소나기",
        85 or 86 => "소낙눈",
        95 or 96 or 99 => "뇌우",
        _ => "알 수 없음"
    };

    private static string WeatherTextRu(int code) => code switch
    {
        0 => "Ясно",
        1 => "Преим. ясно",
        2 => "Переменная облачность",
        3 => "Облачно",
        45 or 48 => "Туман",
        51 or 53 or 55 => "Морось",
        56 or 57 => "Ледяная морось",
        61 or 63 or 65 => "Дождь",
        66 or 67 => "Ледяной дождь",
        71 or 73 or 75 => "Снег",
        77 => "Снежные зерна",
        80 or 81 or 82 => "Ливни",
        85 or 86 => "Снежные ливни",
        95 or 96 or 99 => "Гроза",
        _ => "Неизвестно"
    };
}

internal static class SolarPhaseClassifier
{
    internal static SolarPhase Classify(DateTime localTime, DateTime sunriseTime, DateTime sunsetTime)
    {
        if (localTime >= sunriseTime && localTime < sunsetTime.AddMinutes(-30d))
        {
            return SolarPhase.Day;
        }

        if (localTime >= sunsetTime.AddMinutes(-30d) && localTime < sunsetTime.AddMinutes(30d))
        {
            return SolarPhase.Sunset;
        }

        return SolarPhase.Night;
    }

    internal static SolarPhase ClassifyBySunElevation(double latitude, double longitude, DateTime utcTime)
    {
        var elevation = CalculateSolarElevation(latitude, longitude, utcTime);
        if (elevation > 6d)
        {
            return SolarPhase.Day;
        }

        return elevation >= -6d ? SolarPhase.Sunset : SolarPhase.Night;
    }

    private static double CalculateSolarElevation(double latitude, double longitude, DateTime utcTime)
    {
        var dayOfYear = utcTime.DayOfYear;
        var hour = utcTime.Hour + utcTime.Minute / 60d + utcTime.Second / 3600d;
        var gamma = 2d * Math.PI / 365d * (dayOfYear - 1 + (hour - 12d) / 24d);
        var declination = 0.006918d
            - 0.399912d * Math.Cos(gamma)
            + 0.070257d * Math.Sin(gamma)
            - 0.006758d * Math.Cos(2d * gamma)
            + 0.000907d * Math.Sin(2d * gamma)
            - 0.002697d * Math.Cos(3d * gamma)
            + 0.00148d * Math.Sin(3d * gamma);
        var equationOfTime = 229.18d * (0.000075d
            + 0.001868d * Math.Cos(gamma)
            - 0.032077d * Math.Sin(gamma)
            - 0.014615d * Math.Cos(2d * gamma)
            - 0.040849d * Math.Sin(2d * gamma));
        var trueSolarTime = (hour * 60d + equationOfTime + 4d * longitude) % 1440d;
        if (trueSolarTime < 0d)
        {
            trueSolarTime += 1440d;
        }

        var hourAngle = trueSolarTime / 4d - 180d;
        var latitudeRad = latitude * Math.PI / 180d;
        var hourAngleRad = hourAngle * Math.PI / 180d;
        var cosZenith = Math.Sin(latitudeRad) * Math.Sin(declination) + Math.Cos(latitudeRad) * Math.Cos(declination) * Math.Cos(hourAngleRad);
        cosZenith = Math.Max(-1d, Math.Min(1d, cosZenith));
        return 90d - Math.Acos(cosZenith) * 180d / Math.PI;
    }
}

internal static class WeatherClassifier
{
    internal static WeatherKind Classify(int code, string text)
    {
        if (code is 95 or 96 or 99)
        {
            return WeatherKind.Storm;
        }

        if (code is 51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82)
        {
            return WeatherKind.Rain;
        }

        if (code is 71 or 73 or 75 or 77 or 85 or 86)
        {
            return WeatherKind.Snow;
        }

        if (code is 45 or 48)
        {
            return WeatherKind.Fog;
        }

        if (code is 2 or 3)
        {
            return WeatherKind.Cloudy;
        }

        if (code is 0 or 1)
        {
            return WeatherKind.Clear;
        }

        if (text.Contains("雪")) return WeatherKind.Snow;
        if (text.Contains("雷")) return WeatherKind.Storm;
        if (text.Contains("雨")) return WeatherKind.Rain;
        if (text.Contains("雾") || text.Contains("霾")) return WeatherKind.Fog;
        if (text.Contains("云") || text.Contains("阴")) return WeatherKind.Cloudy;
        if (text.Contains("晴")) return WeatherKind.Clear;
        return WeatherKind.Unknown;
    }
}
