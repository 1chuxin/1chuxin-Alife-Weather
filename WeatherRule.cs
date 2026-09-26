using System;
using System.Collections.Generic;
using System.Text;

namespace chuxin.Weather;

/// <summary>
/// 一条天气推送/提醒规则。内置 4 条默认规则与用户自定义规则共用此模型（见 DESIGN.md §5.3/§6）。
/// </summary>
public sealed class WeatherRule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>作用城市，空 = 所有监控城市。必须在监控范围内（默认城市 ∪ WatchCities）。</summary>
    public string City { get; set; } = "";

    /// <summary>数据窗口：now / today / tomorrow / intraday / day_over_day / hourly_today</summary>
    public string Window { get; set; } = "now";

    /// <summary>指标：temp / feels_like / temp_max / temp_min / humidity / wind_speed / uv / precip_mm / precip_prob / thunder_prob / weather_code</summary>
    public string Metric { get; set; } = "temp";

    /// <summary>操作符：&gt; / &gt;= / &lt; / &lt;= / == / changed（数值=变化量；weather_code=类别突变）</summary>
    public string Op { get; set; } = ">";

    public double Threshold { get; set; }

    /// <summary>冷却时间（小时），默认 24 = 当日一次。</summary>
    public int CooldownHours { get; set; } = 24;

    /// <summary>分发级别：push（P0，Poke 唤醒 AI）/ silent（P1，仅更新常驻天气上下文）。</summary>
    public string Level { get; set; } = "push";

    /// <summary>文案模板，占位符 {city} {metric} {value} {prev} {threshold} {time}，空 = 默认文案。</summary>
    public string MessageTemplate { get; set; } = "";

    /// <summary>是否内置默认规则（UI 据此显示"默认"标记，可改可禁用）。</summary>
    public bool IsBuiltin { get; set; }

    public static readonly string[] ValidWindows = { "now", "today", "tomorrow", "intraday", "day_over_day", "hourly_today" };
    public static readonly string[] ValidMetrics = { "temp", "feels_like", "temp_max", "temp_min", "humidity", "wind_speed", "uv", "precip_mm", "precip_prob", "thunder_prob", "weather_code" };
    public static readonly string[] ValidOps = { ">", ">=", "<", "<=", "==", "changed" };

    /// <summary>校验规则，返回错误描述；null = 合法。</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "规则名不能为空";
        if (Array.IndexOf(ValidWindows, Window) < 0) return $"不支持的数据窗口：{Window}";
        if (Array.IndexOf(ValidMetrics, Metric) < 0) return $"不支持的指标：{Metric}";
        if (Array.IndexOf(ValidOps, Op) < 0) return $"不支持的操作符：{Op}";
        if (Op == "changed" && Window == "now") return "now 窗口没有对比基准，不能用 changed（渐变对比请用 intraday）";
        if (CooldownHours < 0) return "冷却时间不能为负";
        if (Level != "push" && Level != "silent") return $"分发级别只能是 push/silent，当前：{Level}";
        return null;
    }

    /// <summary>渲染文案：优先模板占位符，否则默认格式。weather_code 用中文现象渲染。</summary>
    public string Render(string city, double value, double? prev, string time)
    {
        string metricCn = WeatherTexts.MetricCn(Metric);
        bool isCode = Metric == "weather_code";
        string valStr = isCode ? WeatherTexts.CodeCnOf((int)value) : FormatNum(value);
        string prevStr = prev.HasValue
            ? (isCode ? WeatherTexts.CodeCnOf((int)prev.Value) : FormatNum(prev.Value))
            : "未知";
        string body;
        if (!string.IsNullOrWhiteSpace(MessageTemplate))
        {
            body = MessageTemplate
                .Replace("{city}", city).Replace("{metric}", metricCn)
                .Replace("{value}", valStr).Replace("{prev}", prevStr)
                .Replace("{threshold}", FormatNum(Threshold)).Replace("{time}", time);
        }
        else if (Op == "changed")
        {
            body = prev.HasValue
                ? $"{metricCn}发生明显变化：{prevStr} → {valStr}（变化 {FormatNum(Math.Abs(value - prev.Value))}，阈值 {FormatNum(Threshold)}）"
                : $"{metricCn}变化达到阈值（{FormatNum(Threshold)}），当前 {valStr}";
        }
        else
        {
            body = $"{metricCn}当前 {valStr}，触发条件：{Op} {FormatNum(Threshold)}";
        }
        return body;
    }

    static string FormatNum(double v) => Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0") : v.ToString("0.#");

    /// <summary>UI 摘要（一行描述规则含义）。</summary>
    public string Summary()
    {
        string win = Window switch
        {
            "now" => "实况",
            "today" => "今日",
            "tomorrow" => "明日",
            "intraday" => "实况较当日基线",
            "day_over_day" => "今日较昨日",
            "hourly_today" => "今日逐时段",
            _ => Window
        };
        string op = Op switch
        {
            ">" => ">", ">=" => "≥", "<" => "<", "<=" => "≤", "==" => "=",
            "changed" => "变化≥",
            _ => Op
        };
        return $"{win} · {WeatherTexts.MetricCn(Metric)} {op} {Threshold:0.#}{(Metric.Contains("temp") ? "°C" : "")}";
    }
}
