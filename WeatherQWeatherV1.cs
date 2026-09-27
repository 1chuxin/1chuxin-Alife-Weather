using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace chuxin.Weather;

#region 新数据域模型

/// <summary>城市定位结果（GeoAPI，进程内缓存）。</summary>
public sealed record QLoc(string Id, double Lat, double Lon, string Country);

public sealed class AirInfo
{
    public double Aqi { get; set; } = -1;
    public string Category { get; set; } = "";       // 类别（良/轻度污染…）
    public string Primary { get; set; } = "";        // 首要污染物
    public string Advice { get; set; } = "";         // 一般人群健康建议
    public string AdviceSensitive { get; set; } = "";// 敏感人群建议
    public List<AirIndex> Indexes { get; set; } = new();      // 各标准（cn-mee / us-epa…）
    public List<AirPollutant> Pollutants { get; set; } = new();
    public DateTime Time { get; set; }
}

public sealed class AirIndex
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public double Aqi { get; set; } = -1;
    public string Category { get; set; } = "";
    public string Primary { get; set; } = "";
    public string Advice { get; set; } = "";
    public string AdviceSensitive { get; set; } = "";
}

public sealed class AirPollutant
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public double Value { get; set; } = -1;
    public string Unit { get; set; } = "";
}

public sealed class IndexItem
{
    public DateTime Date { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public int Level { get; set; } = -1;
    public string Category { get; set; } = "";
    public string Text { get; set; } = "";           // 建议原文
}

public sealed class MinutelyInfo
{
    public bool Supported { get; set; } = true;      // false = 城市不在覆盖范围（仅中国）
    public string Summary { get; set; } = "";        // 和风现成一句话（"未来两小时无降水"）
    public DateTime UpdateTime { get; set; }
    public List<MinutelyPoint> Items { get; set; } = new();
}

public sealed record MinutelyPoint(DateTime FxTime, double Precip, string Type);

public sealed class HistoryDay
{
    public DateTime Date { get; set; }
    public double TempMax { get; set; } = -999;
    public double TempMin { get; set; } = -999;
    public double Precip { get; set; } = -1;
    public double Humidity { get; set; } = -1;
    public double Pressure { get; set; } = -1;
    public int Code { get; set; } = -1;
    public string Desc { get; set; } = "";
}

public sealed class AstroDay
{
    public DateTime Date { get; set; }
    public DateTime Sunrise { get; set; }
    public DateTime Sunset { get; set; }
    public DateTime CivilDawn { get; set; }          // 民用晨光始
    public DateTime CivilDusk { get; set; }          // 民用暮光终（"天黑"）
    public DateTime Moonrise { get; set; }
    public DateTime Moonset { get; set; }
    public string MoonPhase { get; set; } = "";
}

#endregion

/// <summary>
/// QWeatherClient 的 v1 归一化与新数据域部分（DESIGN.md §11.1/§11.2）。
/// v1 为主传输层：坐标系寻址、结构化嵌套、原生天气码；单位陷阱在此收口
/// （概率/湿度 0–1→0–100、风速 m/s→km/h、UTC→本地、condition.code→WWO 码表）。
/// </summary>
public sealed partial class QWeatherClient
{
    // ── 天气包（v1）──

    async Task<WeatherData> GetWeatherV1Async(string city, int days)
    {
        var ct = CancellationToken.None;
        var loc = await ResolveLocationAsync(city, ct);
        var data = new WeatherData { City = city, FetchedAt = DateTime.Now };

        var dailyRoot = await GetJsonAsync($"/weather/v1/daily/{loc.Lat:0.##}/{loc.Lon:0.##}?days={days}", ct);
        if (dailyRoot.TryGetProperty("days", out var daysArr) && daysArr.ValueKind == JsonValueKind.Array)
            foreach (var d in daysArr.EnumerateArray())
                data.Days.Add(ParseDayV1(d));

        var hourlyRoot = await GetJsonAsync($"/weather/v1/hourly/{loc.Lat:0.##}/{loc.Lon:0.##}?hours=72", ct);
        if (hourlyRoot.TryGetProperty("hours", out var hoursArr) && hoursArr.ValueKind == JsonValueKind.Array)
            foreach (var h in hoursArr.EnumerateArray())
            {
                var hp = ParseHourV1(h);
                data.HourlyFlat.Add(hp);
                var day = data.Days.FirstOrDefault(d => d.Date == hp.Ts.Date);
                if (day != null) day.Hourly.Add(hp);
            }

        var curRoot = await GetJsonAsync($"/weather/v1/current/{loc.Lat:0.##}/{loc.Lon:0.##}", ct);
        data.Now = ParseCurrentV1(curRoot);
        return data;
    }

    static CurrentObs ParseCurrentV1(JsonElement root)
    {
        double hum = Dbl(root, "humidity");
        return new CurrentObs
        {
            Temp = NumIn(root, "temperature", "value"),
            Feels = NumIn(root, "feelsLike", "value"),
            Humidity = hum is >= 0 and <= 1 ? (int)Math.Round(hum * 100) : -1,
            WindDir = StrIn(root, "wind", "direction", "compass").ToUpperInvariant(),
            WindKph = Math.Round(NumIn(root, "wind", "speed", "value") * 3.6, 1),
            WindScale = (int)NumIn(root, "wind", "scale"),
            WindGustKph = Math.Round(NumIn(root, "windGust", "value") * 3.6, 1),
            PrecipMm = NzIn(root, "precipitation", "amount", "value"),
            Pressure = NumIn(root, "pressure", "value"),
            VisM = NumIn(root, "visibility", "value"),
            Cloud = Pct(Dbl(root, "cloudCover")),
            Dew = NumIn(root, "dewPoint", "value"),
            Uv = Dbl(root, "uvIndex"),
            Code = QCodeToWwo(StrIn(root, "condition", "code")),
            Text = StrIn(root, "condition", "text"),
            ObsTime = DateTime.Now,
        };
    }

    static HourPoint ParseHourV1(JsonElement h)
    {
        var fx = DateTimeOffset.TryParse(Str(h, "forecastTime"), out var t) ? t.LocalDateTime : DateTime.Now;
        string code = StrIn(h, "condition", "code");
        double prob = DblIn(h, "precipitation", "probability");
        return new HourPoint
        {
            Time = fx.Hour,
            Ts = fx,
            TempC = NumIn(h, "temperature", "value"),
            RainProb = prob >= 0 ? (int)Math.Round(prob * 100) : 0,
            ThunderProb = IsQThunder(code) ? 100 : 0,
            PrecipMm = NzIn(h, "precipitation", "amount", "value"),
            Uv = Dbl(h, "uvIndex"),
            Code = QCodeToWwo(code),
        };
    }

    static DayForecast ParseDayV1(JsonElement d)
    {
        var day = new DayForecast();
        if (DateTimeOffset.TryParse(Str(d, "forecastStartTime"), out var st)) day.Date = st.LocalDateTime.Date;
        day.MaxC = NumIn(d, "temperatureMax", "value");
        day.MinC = NumIn(d, "temperatureMin", "value");
        day.Uv = Dbl(d, "uvIndexMax");
        var astro = Obj(d, "astro");
        day.Sunrise = ToLocal(astro, "sunrise");
        day.Sunset = ToLocal(astro, "sunset");
        day.CivilDawn = ToLocal(astro, "civilDawn");
        day.CivilDusk = ToLocal(astro, "civilDusk");
        day.Moonrise = ToLocal(astro, "moonrise");
        day.Moonset = ToLocal(astro, "moonset");
        day.MoonPhase = StrIn(astro, "moonPhase");
        var dt = Obj(d, "daytime");
        var nt = Obj(d, "nighttime");
        day.DayCode = QCodeToWwo(StrIn(dt, "condition", "code"));
        day.NightCode = QCodeToWwo(StrIn(nt, "condition", "code"));
        day.TextDay = StrIn(dt, "condition", "text");
        day.TextNight = StrIn(nt, "condition", "text");
        day.Humidity = Pct(NumIn(dt, "humidity"));
        day.WindKph = Math.Round(NumIn(dt, "wind", "speed", "value") * 3.6, 1);
        double p1 = DblIn(dt, "precipitation", "probability"), p2 = DblIn(nt, "precipitation", "probability");
        day.PrecipProb = p1 < 0 && p2 < 0 ? -1 : Math.Round(Math.Max(p1, p2) * 100);   // 双缺省=-1（无数据），单缺省取有效值
        day.PrecipMm = NzIn(dt, "precipitation", "amount", "value") + NzIn(nt, "precipitation", "amount", "value");
        return day;
    }

    // ── 逐小时平铺（query_hourly 用，1–240h；v1 失败降级 v7 24h）──

    public Task<List<HourPoint>?> GetHourlyFlatAsync(string city, int hours, CancellationToken ct, TimeSpan? ttl = null)
    {
        int h = Math.Clamp(hours, 1, 240);
        return SafeAsync($"hourly:{city}:{h}", ttl ?? CacheHub.QueryTtl, async () =>
        {
            try
            {
                var loc = await ResolveLocationAsync(city, CancellationToken.None);
                var root = await GetJsonAsync($"/weather/v1/hourly/{loc.Lat:0.##}/{loc.Lon:0.##}?hours={h}", CancellationToken.None);
                var list = new List<HourPoint>();
                if (root.TryGetProperty("hours", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var x in arr.EnumerateArray())
                        list.Add(ParseHourV1(x));
                return list;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                var data = await GetWeatherV7Async(city, 1);
                return data.HourlyFlat.Take(h).ToList();
            }
        }, ct);
    }

    // ── 空气质量（/airquality/v1/current，实测 200）──

    public Task<AirInfo?> GetAirAsync(string city, CancellationToken ct, TimeSpan? ttl = null)
        => SafeAsync($"air:{city}", ttl ?? CacheHub.AqiTtl, async () =>
        {
            var loc = await ResolveLocationAsync(city, CancellationToken.None);
            var root = await GetJsonAsync($"/airquality/v1/current/{loc.Lat:0.##}/{loc.Lon:0.##}", CancellationToken.None);
            var info = new AirInfo { Time = DateTime.Now };
            if (root.TryGetProperty("indexes", out var idxs) && idxs.ValueKind == JsonValueKind.Array)
                foreach (var i in idxs.EnumerateArray())
                    info.Indexes.Add(new AirIndex
                    {
                        Code = Str(i, "code"),
                        Name = Str(i, "name"),
                        Aqi = Dbl(i, "aqi"),
                        Category = Str(i, "category"),
                        Primary = StrIn(i, "primaryPollutant", "name"),
                        Advice = StrIn(i, "health", "advice", "generalPopulation"),
                        AdviceSensitive = StrIn(i, "health", "advice", "sensitivePopulation"),
                    });
            // 主指数：优先国标（cn-*），无则取第一个；顶层便捷字段从主指数拷贝
            var main = info.Indexes.FirstOrDefault(i => i.Code.Contains("cn", StringComparison.OrdinalIgnoreCase))
                       ?? info.Indexes.FirstOrDefault();
            if (main != null)
            {
                info.Aqi = main.Aqi; info.Category = main.Category; info.Primary = main.Primary;
                info.Advice = main.Advice; info.AdviceSensitive = main.AdviceSensitive;
            }
            if (root.TryGetProperty("pollutants", out var pols) && pols.ValueKind == JsonValueKind.Array)
                foreach (var p in pols.EnumerateArray())
                    info.Pollutants.Add(new AirPollutant
                    {
                        Code = Str(p, "code"),
                        Name = StrIn(p, "name"),
                        Value = NumIn(p, "concentration", "value"),
                        Unit = StrIn(p, "concentration", "unit"),
                    });
            return info;
        }, ct);

    // ── 分钟级降水（/v7/minutely/5m，实测 200；仅中国城市）──

    public Task<MinutelyInfo?> GetMinutelyAsync(string city, CancellationToken ct, TimeSpan? ttl = null)
        => SafeAsync($"min:{city}", ttl ?? CacheHub.MinutelyTtl, async () =>
        {
            var loc = await ResolveLocationAsync(city, CancellationToken.None);
            if (!IsChina(loc)) return new MinutelyInfo { Supported = false };
            var root = await GetJsonAsync($"/v7/minutely/5m?location={loc.Lon:0.##},{loc.Lat:0.##}", CancellationToken.None);
            var mi = new MinutelyInfo { Summary = Str(root, "summary") };
            if (DateTimeOffset.TryParse(Str(root, "updateTime"), out var u)) mi.UpdateTime = u.LocalDateTime;
            if (root.TryGetProperty("minutely", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                {
                    if (!DateTimeOffset.TryParse(Str(m, "fxTime"), out var fx)) continue;
                    mi.Items.Add(new MinutelyPoint(fx.LocalDateTime,
                        double.TryParse(Str(m, "precip"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0,
                        Str(m, "type")));
                }
            return mi;
        }, ct);

    // ── 生活指数（/v7/indices/{1d|3d}，实测 200）──

    public Task<List<IndexItem>?> GetIndicesAsync(string city, string types, int days, CancellationToken ct, TimeSpan? ttl = null)
    {
        int dd = Math.Clamp(days, 1, 3);
        return SafeAsync($"idx:{city}:{types}:{dd}", ttl ?? CacheHub.IndicesTtl, async () =>
        {
            var loc = await ResolveLocationAsync(city, CancellationToken.None);
            var root = await GetJsonAsync($"/v7/indices/{dd}d?type={Uri.EscapeDataString(types)}&location={loc.Lon:0.##},{loc.Lat:0.##}", CancellationToken.None);
            var list = new List<IndexItem>();
            if (root.TryGetProperty("daily", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var i in arr.EnumerateArray())
                    list.Add(new IndexItem
                    {
                        Date = DateTime.TryParse(Str(i, "date"), out var d0) ? d0 : DateTime.Today,
                        Type = Str(i, "type"),
                        Name = Str(i, "name"),
                        Level = (int)Num(i, "level"),
                        Category = Str(i, "category"),
                        Text = Str(i, "text"),
                    });
            return list;
        }, ct);
    }

    // ── 时光机历史（/v7/historical/weather，实测 200；过去 10 天、不含当日、仅 LocationID）──

    public Task<HistoryDay?> GetHistoryAsync(string city, DateTime date, CancellationToken ct)
        => SafeAsync($"hist:{city}:{date:yyyyMMdd}", CacheHub.HistoryTtl, async () =>
        {
            var loc = await ResolveLocationAsync(city, CancellationToken.None);
            var root = await GetJsonAsync($"/v7/historical/weather?location={loc.Id}&date={date:yyyyMMdd}", CancellationToken.None);
            var hd = new HistoryDay { Date = date };
            if (root.TryGetProperty("weatherDaily", out var wd))
            {
                hd.TempMax = Num(wd, "tempMax");
                hd.TempMin = Num(wd, "tempMin");
                hd.Precip = Num(wd, "precip");
                hd.Humidity = Num(wd, "humidity");
                hd.Pressure = Num(wd, "pressure");
            }
            if (root.TryGetProperty("weatherHourly", out var wh) && wh.ValueKind == JsonValueKind.Array && wh.GetArrayLength() > 0)
            {
                var mid = wh.EnumerateArray().ElementAtOrDefault(wh.GetArrayLength() / 2);
                int code = QCodeToWwo(Str(mid, "icon"));
                hd.Code = code > 0 ? code : TextToCode(Str(mid, "text"));
                hd.Desc = Str(mid, "text");
            }
            return hd;
        }, ct);

    // ── 天文（/v7/astronomy/sun|moon，实测 200；单日期计价，sun+moon 各 1 次）──

    public Task<AstroDay?> GetAstroAsync(string city, DateTime date, CancellationToken ct)
        => SafeAsync($"astro:{city}:{date:yyyyMMdd}", CacheHub.AstroTtl, async () =>
        {
            var loc = await ResolveLocationAsync(city, CancellationToken.None);
            var ad = new AstroDay { Date = date };
            var sun = await GetJsonAsync($"/v7/astronomy/sun?location={loc.Lon:0.##},{loc.Lat:0.##}&date={date:yyyyMMdd}", CancellationToken.None);
            ad.Sunrise = ToLocalDT(sun, "sunrise");
            ad.Sunset = ToLocalDT(sun, "sunset");
            var moon = await GetJsonAsync($"/v7/astronomy/moon?location={loc.Lon:0.##},{loc.Lat:0.##}&date={date:yyyyMMdd}", CancellationToken.None);
            ad.Moonrise = ToLocalDT(moon, "moonrise");
            ad.Moonset = ToLocalDT(moon, "moonset");
            if (moon.TryGetProperty("moonPhase", out var mp) && mp.ValueKind == JsonValueKind.Array && mp.GetArrayLength() > 0)
                ad.MoonPhase = StrIn(mp.EnumerateArray().Last(), "name");
            return ad;
        }, ct);

    /// <summary>城市是否在中国（分钟级降水覆盖范围）。</summary>
    public bool IsChina(QLoc loc) => loc.Country.Contains("中国") || loc.Country.Contains("China", StringComparison.OrdinalIgnoreCase);

    // ── 共用工具 ──

    async Task<T?> SafeAsync<T>(string key, TimeSpan ttl, Func<Task<T>> loader, CancellationToken ct) where T : class
    {
        try { return await CacheHub.GetOrLoadAsync(key, ttl, loader, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    static DateTime ToLocal(JsonElement? e, string name)
    {
        if (e.HasValue && DateTimeOffset.TryParse(Str(e.Value, name), out var t)) return t.LocalDateTime;
        return default;
    }

    static DateTime ToLocalDT(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), out var t) ? t.LocalDateTime : default;

    static JsonElement? Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    static string StrIn(JsonElement? e, params string[] path)
    {
        if (!e.HasValue) return "";
        var cur = e.Value;
        for (int i = 0; i < path.Length - 1; i++)
        {
            var next = Obj(cur, path[i]);
            if (!next.HasValue) return "";
            cur = next.Value;
        }
        return Str(cur, path[^1]);
    }

    static double NumIn(JsonElement? e, params string[] path) => DblIn(e, path);

    static double DblIn(JsonElement? e, params string[] path)
    {
        if (!e.HasValue) return -999;
        var cur = e.Value;
        for (int i = 0; i < path.Length - 1; i++)
        {
            var next = Obj(cur, path[i]);
            if (!next.HasValue) return -999;
            cur = next.Value;
        }
        return Num(cur, path[^1]);
    }

    static double NzIn(JsonElement? e, params string[] path) { var v = DblIn(e, path); return v < 0 ? 0 : v; }

    static double Dbl(JsonElement e, string name) => Num(e, name);

    static double Pct(double v01) => v01 is >= 0 and <= 1 ? Math.Round(v01 * 100) : -1;

    static bool IsQThunder(string qcode) => qcode is "302" or "303" or "304";

    /// <summary>和风天气现象码→WWO 码（官方码表子集映射，类别语义与 WeatherTexts.CodeCategory 对齐；未知码 → -1）。</summary>
    internal static int QCodeToWwo(string qcode)
    {
        if (!int.TryParse(qcode, out var c)) return -1;
        return c switch
        {
            100 or 150 => 113,
            101 or 151 or 102 or 152 or 103 or 153 => 116,
            104 or 154 => 119,
            300 or 350 => 353,
            301 or 351 => 356,
            302 or 303 => 389,
            304 => 200,
            305 => 296,
            306 or 315 => 302,
            307 => 308,
            308 or 310 or 311 or 312 or 317 or 318 => 359,
            309 => 263,
            313 => 182,
            314 => 299,
            316 => 356,
            399 => 302,
            400 or 450 or 408 => 368,
            401 or 402 or 403 or 407 or 409 or 410 or 457 or 499 => 371,
            404 or 405 or 406 or 456 => 362,
            500 or 502 or 503 or 504 or 505 or 506 or 510 or 511 or 514 or 515 or 516 => 143,
            501 or 507 or 512 or 513 or 517 or 518 => 248,
            >= 900 and <= 904 => 113,
            _ => -1,
        };
    }
}
