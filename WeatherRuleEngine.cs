using System;
using System.Collections.Generic;
using System.Linq;

namespace chuxin.Weather;

public sealed class RuleHit
{
    public WeatherRule Rule { get; init; } = null!;
    public string City { get; init; } = "";
    public double Value { get; init; }
    public double? Prev { get; init; }
    public string Text { get; init; } = "";
    public bool IsPush => Rule.Level == "push";
}

/// <summary>
/// 规则评价引擎：内置默认规则与自定义规则同机制（DESIGN.md §5.3）。
/// 评价频率 = 变化检测频率；指标取数受"指标×窗口组合约束"。无状态（快照由调用方传入）。
/// </summary>
public sealed class WeatherRuleEngine
{
    /// <summary>对单个城市评价全部启用规则。badRules 用于收集加载期发现的无数据指标（供 UI 标灰/日志）。</summary>
    public List<RuleHit> Evaluate(IEnumerable<WeatherRule> rules, string city, WeatherData data,
        DaySnap? baseline, DaySnap? yesterday, ISet<string> noDataMetrics)
    {
        var hits = new List<RuleHit>();
        foreach (var rule in rules.Where(r => r.Enabled && (string.IsNullOrEmpty(r.City) || r.City == city)))
        {
            if (!TryGetValue(rule, city, data, baseline, yesterday, out var value, out var prev))
            {
                noDataMetrics.Add($"{rule.Metric}@{rule.Window}");
                continue;
            }
            if (!IsHit(rule, value, prev, baseline, yesterday)) continue;
            hits.Add(new RuleHit
            {
                Rule = rule, City = city, Value = value, Prev = prev,
                Text = rule.Render(city, value, prev, DateTime.Now.ToString("MM-dd HH:mm")),
            });
        }
        return hits;
    }

    /// <summary>当前数据是否命中规则条件（不含冷却判定——冷却由 Monitor 按防抖键统一管理）。</summary>
    public bool IsHit(WeatherRule rule, double value, double? prev, DaySnap? baseline, DaySnap? yesterday)
    {
        if (rule.Op == "changed")
        {
            if (rule.Metric == "weather_code")
            {
                // 类别突变：与窗口指定的对比基准比较类别；只推"涉及降水"的显著突变
                // （转下雨/雪/雷，或降水结束转晴/多云），晴↔多云↔阴这类小幅变化不推
                int? baseCode = rule.Window == "day_over_day" ? yesterday?.Code
                    : rule.Window == "intraday" ? baseline?.Code
                    : rule.Window == "tomorrow" ? prev?.Code() : null;
                if (baseCode == null) return false;
                string from = WeatherTexts.CategoryOf(baseCode.Value);
                string to = WeatherTexts.CategoryOf(value.Code());
                if (from == to) return false;
                bool toPrecip = IsPrecip(to), fromPrecip = IsPrecip(from);
                return toPrecip || (fromPrecip && (to == "晴" || to == "多云"));
            }
            double? baseVal = rule.Window switch
            {
                "intraday" => baseline?.TempNow,
                "day_over_day" => prev,
                "tomorrow" => prev,
                _ => null,
            };
            if (baseVal == null) return false;
            return Math.Abs(value - baseVal.Value) >= rule.Threshold;
        }
        return rule.Op switch
        {
            ">" => value > rule.Threshold,
            ">=" => value >= rule.Threshold,
            "<" => value < rule.Threshold,
            "<=" => value <= rule.Threshold,
            "==" => Math.Abs(value - rule.Threshold) < 0.05,
            _ => false,
        };
    }

    bool TryGetValue(WeatherRule rule, string city, WeatherData data, DaySnap? baseline, DaySnap? yesterday,
        out double value, out double? prev)
    {
        value = double.NaN; prev = null;
        var today = data.Days.FirstOrDefault();
        var tomorrow = data.Days.Skip(1).FirstOrDefault();

        switch (rule.Window)
        {
            case "now":
                value = rule.Metric switch
                {
                    "temp" => data.Now.Temp, "feels_like" => data.Now.Feels,
                    "humidity" => data.Now.Humidity, "wind_speed" => data.Now.WindKph,
                    "uv" => data.Now.Uv, "precip_mm" => data.Now.PrecipMm,
                    "weather_code" => data.Now.Code,
                    "temp_max" => today?.MaxC ?? double.NaN,
                    "temp_min" => today?.MinC ?? double.NaN,
                    _ => double.NaN,
                };
                prev = rule.Metric == "weather_code" ? baseline?.Code : baseline?.TempNow;
                break;
            case "today":
                value = DailyMetric(rule.Metric, today);
                prev = yesterday != null ? DailyMetricFromSnap(rule.Metric, yesterday) : null;
                break;
            case "tomorrow":
                value = DailyMetric(rule.Metric, tomorrow);
                prev = rule.Metric == "weather_code" ? (int?)(today?.DayCode ?? -1) : today != null ? DailyMetric(rule.Metric, today) : null;
                break;
            case "intraday":
                value = rule.Metric == "weather_code" ? data.Now.Code : data.Now.Temp;
                break;
            case "day_over_day":
                value = DailyMetric(rule.Metric, today);
                prev = yesterday != null ? DailyMetricFromSnap(rule.Metric, yesterday) : null;
                if (yesterday == null) return false; // 无昨日快照无法对比
                break;
            case "hourly_today":
                if (today == null || today.Hourly.Count == 0) return false;
                value = rule.Metric switch
                {
                    "precip_prob" => today.Hourly.Max(h => h.RainProb),
                    "thunder_prob" => today.Hourly.Max(h => h.ThunderProb),
                    "temp" => today.Hourly.Max(h => h.TempC),
                    "weather_code" => today.Hourly.OrderBy(h => h.Time).Last().Code,
                    _ => double.NaN,
                };
                break;
            default:
                return false;
        }
        if (rule.Metric != "weather_code" && (double.IsNaN(value) || (value <= -900 && value != 0))) return false;
        if (rule.Metric == "weather_code" && (double.IsNaN(value) || value < 0)) return false;
        return true;
    }

    static bool IsPrecip(string category) =>
        category is "雨" or "雪" or "雷暴" or "冻雨" or "冰雹" or "雨夹雪" or "雷雪";

    static double DailyMetric(string metric, DayForecast? day) => day == null ? double.NaN : metric switch
    {
        "temp_max" => day.MaxC, "temp_min" => day.MinC,
        "precip_prob" => day.Hourly.Count > 0 ? day.Hourly.Max(h => h.RainProb) : double.NaN,
        "thunder_prob" => day.Hourly.Count > 0 ? day.Hourly.Max(h => h.ThunderProb) : double.NaN,
        "weather_code" => day.DayCode,
        _ => double.NaN,
    };

    static double DailyMetricFromSnap(string metric, DaySnap snap) => metric switch
    {
        "temp_max" => snap.TempMax, "temp_min" => snap.TempMin,
        "weather_code" => snap.Code,
        _ => double.NaN,
    };
}

/// <summary>扩展：把 double 当作代码用的轻量封装（weather_code 在 prev 管道中的传递）。</summary>
public static class RuleHitExt
{
    public static int Code(this double? v) => (int)(v ?? -1);
    public static int Code(this double v) => (int)v;
}

/// <summary>内置默认规则集（预置于规则列表，UI 可改阈值/禁用，机制与自定义规则一致）。</summary>
public static class DefaultRules
{
    public static List<WeatherRule> Create() =>
    [
        new WeatherRule
        {
            Name = "强降温/升温", IsBuiltin = true, Window = "tomorrow", Metric = "temp_max",
            Op = "changed", Threshold = 8, CooldownHours = 24, Level = "push",
            MessageTemplate = "明日气温明显变化：今日 {prev}°C → 明日 {value}°C（变化 {threshold}°C 以上）。\n请主动提醒用户增减衣物。",
        },
        new WeatherRule
        {
            Name = "降雨/雷暴临近", IsBuiltin = true, Window = "hourly_today", Metric = "precip_prob",
            Op = ">=", Threshold = 60, CooldownHours = 24, Level = "push",
            MessageTemplate = "今天稍后降水概率 {value}%，出门建议带伞。",
        },
        new WeatherRule
        {
            Name = "今日较昨日突变", IsBuiltin = true, Window = "day_over_day", Metric = "temp_max",
            Op = "changed", Threshold = 6, CooldownHours = 24, Level = "push",
            MessageTemplate = "今天和昨天差别较大：昨日最高 {prev}°C，今日 {value}°C。请酌情提醒用户。",
        },
        new WeatherRule
        {
            Name = "当日累计变化", IsBuiltin = true, Window = "intraday", Metric = "temp",
            Op = "changed", Threshold = 6, CooldownHours = 24, Level = "push",
            MessageTemplate = "今日气温较早上明显变化：{prev}°C → {value}°C。请酌情提醒用户注意天气。",
        },
        new WeatherRule
        {
            Name = "现象突变（当日）", IsBuiltin = true, Window = "intraday", Metric = "weather_code",
            Op = "changed", Threshold = 0, CooldownHours = 24, Level = "push",
            MessageTemplate = "天气正在变化：{prev} → {value}。请酌情提醒用户（带伞、关窗、调整出行）。",
        },
        new WeatherRule
        {
            Name = "现象突变（较昨日）", IsBuiltin = true, Window = "day_over_day", Metric = "weather_code",
            Op = "changed", Threshold = 0, CooldownHours = 24, Level = "push",
            MessageTemplate = "今天天气和昨天明显不同：昨天{prev}，今天{value}。请酌情提醒用户。",
        },
    ];
}
