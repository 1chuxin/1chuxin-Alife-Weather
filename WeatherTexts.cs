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
        "weather_code" => "天气现象", "aqi" => "空气质量AQI",
        _ => metric
    };

    /// <summary>和风月相英文标识→中文（daily.astro 的 moonPhase；识别不了返回原文，空返回 null=不显示）。</summary>
    public static string? MoonCnOf(string phase) => phase switch
    {
        "" or null => null,
        "new-moon" => "新月", "waxing-crescent" => "娥眉月", "first-quarter" => "上弦月",
        "waxing-gibbous" => "盈凸月", "full-moon" => "满月", "waning-gibbous" => "亏凸月",
        "last-quarter" => "下弦月", "waning-crescent" => "残月",
        _ => phase,
    };

    /// <summary>实时天气查询返回（AI 阅读友好）。</summary>
    public static string FormatCurrent(WeatherData d, int warningCount)
    {
        var sb = new StringBuilder();
        var n = d.Now;
        string phen = !string.IsNullOrEmpty(n.Text) ? n.Text : CodeCnOf(n.Code);
        sb.Append($"{d.City} 实时天气（更新于 {n.ObsTime:MM-dd HH:mm}）：\n");
        string windTail = n.WindScale >= 0
            ? $"（{n.WindScale} 级{(n.WindGustKph > 0 ? $"，阵风 {n.WindGustKph:0}km/h" : "")}）" : "";
        sb.Append($"{phen} {n.Temp:0.#}°C（体感 {n.Feels:0.#}°C）");
        if (n.Humidity >= 0) sb.Append($"，湿度 {n.Humidity}%");
        sb.Append($"，{WindDirCnOf(n.WindDir)}风 {n.WindKph:0}km/h{windTail}");
        if (n.Uv >= 0) sb.Append($"，紫外线 {n.Uv:0.#}");
        sb.Append('\n');
        var detail = new List<string>();
        if (n.Pressure > 0) detail.Add($"气压 {n.Pressure:0}hPa");
        if (n.VisM > 0) detail.Add($"能见度 {n.VisM / 1000:0.#}km");
        if (n.Cloud >= 0) detail.Add($"云量 {n.Cloud:0}%");
        if (n.Dew > -500) detail.Add($"露点 {n.Dew:0.#}°C");
        if (detail.Count > 0) sb.Append(string.Join("，", detail) + '\n');
        if (d.Air is { Aqi: >= 0 } air)
            sb.Append($"空气质量 {air.Category}（AQI {air.Aqi:0}{(air.Primary.Length > 0 ? $"，首要 {air.Primary}" : "")}）\n");
        AppendHourlyRain(d, sb);
        if (warningCount > 0)
            sb.Append($"⚠ 当前有 {warningCount} 条生效预警，可用 <query_warning/> 查看详情\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>未来 3 小时段降水概率摘要（有雨才提）。</summary>
    static void AppendHourlyRain(WeatherData d, StringBuilder sb)
    {
        var upcoming = d.HourlyFlat.Count > 0
            ? d.HourlyFlat.Where(h => h.Ts >= d.FetchedAt).OrderBy(h => h.Ts).Take(3).ToList()
            : d.Days.FirstOrDefault()?.Hourly
                .Where(h => h.Time >= d.FetchedAt.Hour).OrderBy(h => h.Time).Take(3).ToList();
        if (upcoming == null || upcoming.Count == 0) return;
        int maxRain = upcoming.Max(h => h.RainProb);
        if (maxRain >= 30)
        {
            var seg = string.Join("、", upcoming.Select(h => $"{(d.HourlyFlat.Count > 0 ? h.Ts.Hour : h.Time):00}时 {h.RainProb}%"));
            sb.Append($"未来时段降水概率：{seg}\n");
        }
    }

    public static string FormatForecast(WeatherData d, int days)
    {
        var sb = new StringBuilder();
        sb.Append($"{d.City} 未来 {Math.Min(days, d.Days.Count)} 天预报：\n");
        foreach (var day in d.Days.Take(days))
        {
            string phen = day.TextDay.Length > 0
                ? (day.TextNight.Length > 0 && day.TextNight != day.TextDay ? $"{day.TextDay}转{day.TextNight}" : day.TextDay)
                : CodeCnOf(day.DayCode);
            sb.Append($"{day.Date:MM-dd} {phen}，{day.MinC:0.#}~{day.MaxC:0.#}°C");
            int rain = day.PrecipProb >= 0 ? (int)day.PrecipProb
                : day.Hourly.Count > 0 ? day.Hourly.Max(h => h.RainProb) : 0;
            if (rain >= 30) sb.Append($"，降水概率 {rain}%{(day.PrecipMm > 0 ? $"（约 {day.PrecipMm:0.#}mm）" : "")}");
            if (day.Uv >= 0) sb.Append($"，紫外线 {day.Uv:0.#}");
            if (day.Sunrise != default && day.Sunset != default)
                sb.Append($"，日出 {day.Sunrise:HH:mm} 日落 {day.Sunset:HH:mm}");
            string? moon = MoonCnOf(day.MoonPhase);
            if (moon != null) sb.Append($"，{moon}");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>逐小时预报（自动聚合保证 ≤40 行）。</summary>
    public static string FormatHourly(string city, List<HourPoint> pts)
    {
        if (pts.Count == 0) return $"{city} 暂无逐小时预报数据";
        int step = Math.Max(1, (int)Math.Ceiling(pts.Count / 40.0));
        var sb = new StringBuilder($"{city} 逐小时预报（{pts.Count} 小时，每 {step} 小时一行）：\n");
        for (int i = 0; i < pts.Count; i += step)
        {
            var seg = pts.Skip(i).Take(step).ToList();
            var h0 = seg[0];
            double tmin = seg.Min(x => x.TempC), tmax = seg.Max(x => x.TempC);
            int prob = seg.Max(x => x.RainProb);
            double mm = seg.Sum(x => x.PrecipMm > 0 ? x.PrecipMm : 0);
            string temp = Math.Abs(tmax - tmin) < 0.3 ? $"{tmax:0.#}°C" : $"{tmin:0.#}~{tmax:0.#}°C";
            sb.Append($"{h0.Ts:MM-dd HH:00} {CodeCnOf(h0.Code)} {temp}，降水概率 {prob}%{(mm > 0 ? $"，约 {mm:0.#}mm" : "")}\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>分钟级降水（未来两小时）。</summary>
    public static string FormatRain(MinutelyInfo mi, string city)
    {
        if (!mi.Supported) return $"{city} 不在分钟级降水覆盖范围内（该数据仅支持中国城市）";
        var first = mi.Items.FirstOrDefault(p => p.Precip > 0);
        if (first == null) return $"{city} {mi.Summary}";
        var last = mi.Items.Last(p => p.Precip > 0);
        int mins = Math.Max(1, (int)Math.Round((first.FxTime - DateTime.Now).TotalMinutes));
        string kind = first.Type == "snow" ? "下雪" : "下雨";
        int duration = Math.Max(5, (int)((last.FxTime - first.FxTime).TotalMinutes));
        return $"{city} 未来两小时分钟级降水：{mi.Summary}\n预计 {mins} 分钟后开始{kind}，持续约 {duration} 分钟。";
    }

    /// <summary>空气质量详情。</summary>
    public static string FormatAir(AirInfo a, string city)
    {
        var sb = new StringBuilder($"{city} 空气质量（更新于 {a.Time:HH:mm}）：\n");
        foreach (var idx in a.Indexes)
            sb.Append($"· [{idx.Code}] {idx.Name}：AQI {idx.Aqi:0} {idx.Category}{(idx.Primary.Length > 0 ? $"，首要污染物 {idx.Primary}" : "")}\n");
        var pols = a.Pollutants.Where(p => p.Value >= 0)
            .Select(p => $"{p.Name} {p.Value:0.#}{p.Unit}").ToList();
        if (pols.Count > 0) sb.Append($"污染物浓度：{string.Join("，", pols)}\n");
        if (a.Advice.Length > 0) sb.Append($"一般人群：{a.Advice}\n");
        if (a.AdviceSensitive.Length > 0) sb.Append($"敏感人群：{a.AdviceSensitive}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>生活指数。</summary>
    public static string FormatIndices(List<IndexItem> items, string city)
    {
        if (items.Count == 0) return $"{city} 暂无生活指数数据";
        var sb = new StringBuilder($"{city} 生活指数：\n");
        foreach (var i in items)
            sb.Append($"· {i.Name}：{i.Category}（L{i.Level}）—— {i.Text}\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>日出日落/晨昏蒙影/月相。</summary>
    public static string FormatAstro(List<AstroDay> days, string city)
    {
        if (days.Count == 0) return $"{city} 暂无天文数据";
        var sb = new StringBuilder($"{city} 天文信息：\n");
        foreach (var a in days)
        {
            sb.Append($"{a.Date:MM-dd}：日出 {a.Sunrise:HH:mm}，日落 {a.Sunset:HH:mm}");
            if (a.CivilDawn != default && a.CivilDusk != default)
                sb.Append($"，天亮 {a.CivilDawn:HH:mm} 天黑 {a.CivilDusk:HH:mm}");
            if (a.Moonrise != default) sb.Append($"，月出 {a.Moonrise:HH:mm} 月落 {a.Moonset:HH:mm}");
            if (a.MoonPhase.Length > 0) sb.Append($"，{MoonCnOf(a.MoonPhase) ?? a.MoonPhase}");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>历史天气回顾。</summary>
    public static string FormatHistory(List<HistoryDay> days, string city)
    {
        if (days.Count == 0) return $"{city} 暂无历史天气数据";
        var sb = new StringBuilder($"{city} 过去天气回顾：\n");
        foreach (var h in days)
        {
            if (h.TempMax <= -900) continue;
            sb.Append($"{h.Date:MM-dd}：{h.TempMin:0.#}~{h.TempMax:0.#}°C");
            if (h.Precip >= 0) sb.Append($"，降水 {h.Precip:0.#}mm");
            if (h.Humidity >= 0) sb.Append($"，湿度 {h.Humidity:0}%");
            if (h.Desc.Length > 0) sb.Append($"，{h.Desc}");
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
        string phen = !string.IsNullOrEmpty(d.Now.Text) ? d.Now.Text : CodeCnOf(d.Now.Code);
        sb.Append($"{d.City}：{phen} {d.Now.Temp:0.#}°C（体感 {d.Now.Feels:0.#}°C）");
        if (d.Now.Humidity >= 0) sb.Append($"，湿度 {d.Now.Humidity}%");
        sb.Append($"，{WindDirCnOf(d.Now.WindDir)}风 {d.Now.WindKph:0}km/h。");
        var tomorrow = tomorrowSrc?.Days.Skip(1).FirstOrDefault() ?? d.Days.Skip(1).FirstOrDefault();
        if (tomorrow != null)
            sb.Append($"明日：{CodeCnOf(tomorrow.DayCode)} {tomorrow.MinC:0.#}~{tomorrow.MaxC:0.#}°C。");
        var todayMax = d.Days.FirstOrDefault()?.MaxC ?? -999;
        if (yesterday != null && todayMax > -900)
        {
            double delta = todayMax - yesterday.TempMax;
            if (Math.Abs(delta) >= 3)
                sb.Append($"今日较昨日：最高温 {(delta > 0 ? "+" : "")}{delta:0.#}°C。");
        }
        if (warningCount > 0) sb.Append($"当前生效预警 {warningCount} 条（<query_warning/> 可查详情）。");
        foreach (var note in silentNotes) sb.Append(note);
        sb.Append("（此行由天气服务自动维护，AI 可自然引用，无需回应本条。）");
        return sb.ToString();
    }
}
