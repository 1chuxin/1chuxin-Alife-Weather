using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace chuxin.Weather;

/// <summary>WWO 天气代码→中文映射与各类格式化输出（wttr.in 的 weatherDesc 为英文，lang=zh 实测未翻译）。</summary>
public static class WeatherTexts
{
    static readonly Dictionary<int, string> CodeCn = new()
    {
        [113] = "晴", [116] = "多云", [119] = "阴", [122] = "阴",
        [143] = "霾", [149] = "烟霾", [248] = "雾", [260] = "冻雾",
        [176] = "零星阵雨", [263] = "毛毛雨", [266] = "轻毛毛雨",
        [293] = "零星小雨", [296] = "小雨", [299] = "零星中到大雨", [302] = "中雨",
        [305] = "零星暴雨", [308] = "大雨", [353] = "阵雨", [356] = "中到大阵雨", [359] = "暴雨",
        [179] = "零星小雪", [227] = "吹雪", [230] = "暴风雪",
        [362] = "零星雨夹雪", [365] = "中到大雨夹雪", [368] = "零星小雪", [371] = "中到大雪", [392] = "零星雷伴雪", [395] = "中到大雷伴雪",
        [182] = "冻雨", [185] = "冻毛毛雨", [281] = "冻毛毛雨", [284] = "强冻毛毛雨",
        [311] = "冻雨", [314] = "强冻雨", [350] = "冰雹", [374] = "阵性冰雹", [377] = "零星冻毛毛雨",
        [200] = "雷阵雨伴冰雹", [386] = "零星雷阵雨", [389] = "雷阵雨",
    };

    /// <summary>现象类别（用于 weather_code 的"类别突变"判定）。</summary>
    static readonly Dictionary<int, string> CodeCategory = new()
    {
        [113] = "晴", [116] = "多云", [119] = "阴", [122] = "阴",
        [143] = "雾霾", [149] = "雾霾", [248] = "雾", [260] = "雾",
        [176] = "雨", [263] = "雨", [266] = "雨", [281] = "冻雨", [284] = "冻雨",
        [293] = "雨", [296] = "雨", [299] = "雨", [302] = "雨", [305] = "雨", [308] = "雨",
        [353] = "雨", [356] = "雨", [359] = "雨",
        [179] = "雪", [227] = "雪", [230] = "雪", [362] = "雨夹雪", [365] = "雨夹雪",
        [368] = "雪", [371] = "雪", [392] = "雷雪", [395] = "雷雪",
        [182] = "冻雨", [185] = "冻雨", [311] = "冻雨", [314] = "冻雨",
        [350] = "冰雹", [374] = "冰雹", [377] = "冻雨",
        [200] = "雷暴", [386] = "雷暴", [389] = "雷暴",
    };

    public static string CodeCnOf(int code) => CodeCn.TryGetValue(code, out var s) ? s : "未知现象";
    public static string CategoryOf(int code) => CodeCategory.TryGetValue(code, out var s) ? s : "其他";

    static readonly Dictionary<string, string> WindDirCn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["N"] = "北", ["NNE"] = "东北偏北", ["NE"] = "东北", ["ENE"] = "东北偏东",
        ["E"] = "东", ["ESE"] = "东南偏东", ["SE"] = "东南", ["SSE"] = "东南偏南",
        ["S"] = "南", ["SSW"] = "西南偏南", ["SW"] = "西南", ["WSW"] = "西南偏西",
        ["W"] = "西", ["WNW"] = "西北偏西", ["NW"] = "西北", ["NNW"] = "西北偏北",
    };

    public static string WindDirCnOf(string dir) =>
        string.IsNullOrEmpty(dir) ? "" : WindDirCn.TryGetValue(dir.Trim(), out var cn) ? cn : dir;

    public static string MetricCn(string metric) => metric switch
    {
        "temp" => "气温", "feels_like" => "体感温度", "temp_max" => "最高温", "temp_min" => "最低温",
        "humidity" => "湿度", "wind_speed" => "风速", "uv" => "紫外线指数",
        "precip_mm" => "降水量", "precip_prob" => "降水概率", "thunder_prob" => "雷暴概率",
        "weather_code" => "天气现象",
        _ => metric
    };

    /// <summary>实时天气查询返回（AI 阅读友好）。</summary>
    public static string FormatCurrent(WeatherData d, int warningCount)
    {
        var sb = new StringBuilder();
        var n = d.Now;
        sb.Append($"{d.City} 实时天气（更新于 {n.ObsTime:MM-dd HH:mm}）：\n");
        sb.Append($"{CodeCnOf(n.Code)} {n.Temp:0.#}°C（体感 {n.Feels:0.#}°C），湿度 {n.Humidity}%，{WeatherTexts.WindDirCnOf(n.WindDir)}风 {n.WindKph:0}km/h");
        if (n.Uv >= 0) sb.Append($"，紫外线 {n.Uv:0.#}");
        sb.Append('\n');
        AppendHourlyRain(d, sb);
        if (warningCount > 0)
            sb.Append($"⚠ 当前有 {warningCount} 条生效预警，可用 <query_warning/> 查看详情\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>未来 3 小时段降水概率摘要（有雨才提）。</summary>
    static void AppendHourlyRain(WeatherData d, StringBuilder sb)
    {
        var upcoming = d.Days.FirstOrDefault()?.Hourly
            .Where(h => h.Time >= d.FetchedAt.Hour).OrderBy(h => h.Time).Take(3).ToList();
        if (upcoming == null || upcoming.Count == 0) return;
        int maxRain = upcoming.Max(h => h.RainProb);
        if (maxRain >= 30)
        {
            var seg = string.Join("、", upcoming.Select(h => $"{h.Time:00}时 {h.RainProb}%"));
            sb.Append($"未来时段降水概率：{seg}\n");
        }
    }

    public static string FormatForecast(WeatherData d, int days)
    {
        var sb = new StringBuilder();
        sb.Append($"{d.City} 未来 {Math.Min(days, d.Days.Count)} 天预报：\n");
        foreach (var day in d.Days.Take(days))
        {
            sb.Append($"{day.Date:MM-dd} {CodeCnOf(day.DayCode)}，{day.MinC:0.#}~{day.MaxC:0.#}°C");
            int rain = day.Hourly.Count > 0 ? day.Hourly.Max(h => h.RainProb) : 0;
            if (rain >= 30) sb.Append($"，降水概率 {rain}%");
            if (day.Sunrise != default && day.Sunset != default)
                sb.Append($"，日出 {day.Sunrise:HH:mm} 日落 {day.Sunset:HH:mm}");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public static string FormatWarnings(string city, List<NmcAlarm> alarms)
    {
        if (alarms.Count == 0) return $"{city} 当前无生效预警";
        var sb = new StringBuilder();
        sb.Append($"{city} 当前生效预警 {alarms.Count} 条：\n");
        foreach (var a in alarms)
        {
            sb.Append($"· {a.Title}（发布 {a.IssueTime}）\n");
            if (a.Detail.Length > 0) sb.Append($"  正文：{Truncate(a.Detail, 200)}\n");
            else if (a.Url.Length > 0) sb.Append($"  详情：https://www.nmc.cn{a.Url}\n");
        }
        return sb.ToString().TrimEnd();
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>P1 常驻天气上下文行（前缀即识别标记，内容随数据原地替换）。</summary>
    public static string FormatContextLine(WeatherData d, WeatherData? tomorrowSrc, DaySnap? yesterday, int warningCount, IEnumerable<string> silentNotes)
    {
        var sb = new StringBuilder();
        sb.Append("[当前天气·chuxin.Weather] ");
        sb.Append($"{d.City}：{CodeCnOf(d.Now.Code)} {d.Now.Temp:0.#}°C（体感 {d.Now.Feels:0.#}°C），湿度 {d.Now.Humidity}%，{WeatherTexts.WindDirCnOf(d.Now.WindDir)}风 {d.Now.WindKph:0}km/h。");
        var tomorrow = tomorrowSrc?.Days.Skip(1).FirstOrDefault() ?? d.Days.Skip(1).FirstOrDefault();
        if (tomorrow != null)
            sb.Append($"明日：{CodeCnOf(tomorrow.DayCode)} {tomorrow.MinC:0.#}~{tomorrow.MaxC:0.#}°C。");
        if (yesterday != null)
        {
            double delta = d.Days.FirstOrDefault()?.MaxC - yesterday.TempMax ?? 0;
            if (Math.Abs(delta) >= 3)
                sb.Append($"今日较昨日：最高温 {(delta > 0 ? "+" : "")}{delta:0.#}°C。");
        }
        if (warningCount > 0) sb.Append($"当前生效预警 {warningCount} 条（<query_warning/> 可查详情）。");
        foreach (var note in silentNotes) sb.Append(note);
        sb.Append("（此行由天气服务自动维护，AI 可自然引用，无需回应本条。）");
        return sb.ToString();
    }
}
