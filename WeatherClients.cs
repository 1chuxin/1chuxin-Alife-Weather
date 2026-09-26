using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace chuxin.Weather;

#region 数据模型

public sealed class CurrentObs
{
    public double Temp { get; set; } = -999;
    public double Feels { get; set; } = -999;
    public int Humidity { get; set; } = -1;
    public double WindKph { get; set; } = -1;
    public string WindDir { get; set; } = "";
    public double Uv { get; set; } = -1;
    public double PrecipMm { get; set; }
    public int Code { get; set; }
    public DateTime ObsTime { get; set; }
}

public sealed class HourPoint
{
    public int Time { get; set; }          // 0-21 点
    public double TempC { get; set; }
    public int RainProb { get; set; }
    public int ThunderProb { get; set; }
    public int Code { get; set; }
}

public sealed class DayForecast
{
    public DateTime Date { get; set; }
    public double MaxC { get; set; }
    public double MinC { get; set; }
    public int DayCode { get; set; }
    public DateTime Sunrise { get; set; }
    public DateTime Sunset { get; set; }
    public List<HourPoint> Hourly { get; set; } = new();
}

public sealed class WeatherData
{
    public string City { get; set; } = "";
    public CurrentObs Now { get; set; } = new();
    public List<DayForecast> Days { get; set; } = new();
    public DateTime FetchedAt { get; set; }
}

public sealed class NmcAlarm
{
    public string AlertId { get; set; } = "";
    public string IssueTime { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Detail { get; set; } = "";   // 正文（详情页抓取，可能为空）
}

#endregion

/// <summary>wttr.in 查询客户端（format=j1，端点与响应结构已实测，见 DESIGN.md §3.1）。</summary>
public sealed class WttrClient(HttpClient http)
{
    public async Task<WeatherData?> GetAsync(string city, CancellationToken ct)
    {
        try
        {
            string url = $"https://wttr.in/{Uri.EscapeDataString(city)}?format=j1";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var data = new WeatherData { City = city, FetchedAt = DateTime.Now };

            if (root.TryGetProperty("current_condition", out var cc) && cc.GetArrayLength() > 0)
                data.Now = ParseCurrent(cc[0]);

            if (root.TryGetProperty("weather", out var wx))
                foreach (var day in wx.EnumerateArray())
                    data.Days.Add(ParseDay(day));

            return data.Now.Temp > -900 ? data : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    static CurrentObs ParseCurrent(JsonElement e)
    {
        var o = new CurrentObs
        {
            Temp = D(e, "temp_C"), Feels = D(e, "FeelsLikeC"),
            Humidity = (int)D(e, "humidity"), WindKph = D(e, "windspeedKmph"),
            WindDir = S(e, "winddir16Point"), Uv = D(e, "uvIndex"), PrecipMm = D(e, "precipMM"),
            Code = (int)D(e, "weatherCode"),
        };
        if (S(e, "observation_time") is { Length: >= 5 } t &&
            TimeSpan.TryParseExact(t[..5], "hh\\:mm", CultureInfo.InvariantCulture, out var ts))
            o.ObsTime = DateTime.Today.Add(ts);
        return o;
    }

    static DayForecast ParseDay(JsonElement e)
    {
        var d = new DayForecast
        {
            Date = DateTime.Parse(S(e, "date") ?? DateTime.Today.ToString("yyyy-MM-dd")),
            MaxC = D(e, "maxtempC"), MinC = D(e, "mintempC"),
        };
        if (e.TryGetProperty("hourly", out var hourly))
            foreach (var h in hourly.EnumerateArray())
                d.Hourly.Add(new HourPoint
                {
                    Time = (int)D(h, "time") / 100,
                    TempC = D(h, "tempC"),
                    RainProb = (int)D(h, "chanceofrain"),
                    ThunderProb = (int)D(h, "chanceofthunder"),
                    Code = (int)D(h, "weatherCode"),
                });
        if (d.Hourly.Count > 0) d.DayCode = d.Hourly.OrderBy(h => Math.Abs(h.Time - 12)).First().Code;
        if (e.TryGetProperty("astronomy", out var astro) && astro.GetArrayLength() > 0)
        {
            var a = astro[0];
            if (TryTime(S(a, "sunrise"), out var sr)) d.Sunrise = d.Date.Add(sr);
            if (TryTime(S(a, "sunset"), out var ss)) d.Sunset = d.Date.Add(ss);
        }
        return d;
    }

    static bool TryTime(string? s, out TimeSpan ts)
    {
        ts = default;
        return !string.IsNullOrEmpty(s) && TimeSpan.TryParseExact(s.Trim(), "hh\\:mm\\ tt", CultureInfo.InvariantCulture, out ts)
            || !string.IsNullOrEmpty(s) && TimeSpan.TryParseExact(s.Trim(), "hh\\:mm", CultureInfo.InvariantCulture, out ts);
    }

    static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static double D(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : -999;
}

/// <summary>中央气象台预警客户端（/f/rest/findAlarm，端点已实测；数据官方、接口非官方承诺）。</summary>
public sealed class NmcAlarmClient(HttpClient http)
{
    TimeSpan _cacheTtl = TimeSpan.FromMinutes(10);
    List<NmcAlarm>? _cache;
    DateTime _cacheAt = DateTime.MinValue;
    public string? TruncationNotice { get; private set; }   // 非空 = 单页截断，有预警不可见

    /// <summary>取走截断通知（读后清空），无通知返回 null。</summary>
    public string? ConsumeTruncationNotice() { var n = TruncationNotice; TruncationNotice = null; return n; }

    /// <summary>拉取全国生效预警（带 10 分钟缓存）。返回 null = 结构异常（视为接口变更）。</summary>
    public async Task<List<NmcAlarm>?> GetAllAsync(CancellationToken ct)
    {
        if (_cache != null && DateTime.Now - _cacheAt < _cacheTtl) return _cache;
        try
        {
            string url = "https://www.nmc.cn/f/rest/findAlarm?pageNo=1&pageSize=1000";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return _cache;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("page", out var page) ||
                !page.TryGetProperty("list", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return null; // 结构变更信号

            var result = new List<NmcAlarm>();
            foreach (var item in list.EnumerateArray())
            {
                string id = GetStr(item, "alertid");
                string title = GetStr(item, "title");
                if (id.Length == 0 || title.Length == 0) return null; // 关键字段缺失 = 结构变更
                result.Add(new NmcAlarm
                {
                    AlertId = id,
                    Title = title,
                    IssueTime = GetStr(item, "issuetime"),
                    Url = GetStr(item, "url"),
                });
            }
            if (page.TryGetProperty("count", out var cntEl) && cntEl.ValueKind == JsonValueKind.Number
                && cntEl.GetInt32() > result.Count)
                TruncationNotice = $"全国生效预警 {cntEl.GetInt32()} 条超过单页上限 {result.Count}，部分预警不可见";
            else
                TruncationNotice = null;
            _cache = result;
            _cacheAt = DateTime.Now;
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch { return _cache; } // 网络抖动：回退旧缓存
    }

    public async Task<List<NmcAlarm>> GetForCityAsync(string city, CancellationToken ct)
    {
        var all = await GetAllAsync(ct);
        if (all == null) return [];
        return all.Where(a => a.Title.Contains(city, StringComparison.Ordinal)).ToList();
    }

    static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ── 正文详情抓取 ──
    // 详情页 /publish/alarm/{alertid}.html 为 HTML，正文在唯一一个 <p> 里（实测）。
    readonly Dictionary<string, string> _detailCache = new();
    readonly object _detailGate = new();

    /// <summary>为预警列表抓取正文（默认全部、4 并发、带缓存与逐条容错；失败留空时调用方降级为显示链接）。</summary>
    public async Task EnrichDetailsAsync(IList<NmcAlarm> alarms, CancellationToken ct, int max = 30)
    {
        var todo = new List<NmcAlarm>();
        lock (_detailGate)
        {
            foreach (var a in alarms)
            {
                if (a.Detail.Length > 0) continue;
                if (_detailCache.TryGetValue(a.AlertId, out var c)) { a.Detail = c; continue; }
                if (a.Url.Length > 0) todo.Add(a);
            }
        }
        todo = todo.Take(max).ToList();
        using var gate = new System.Threading.SemaphoreSlim(4);
        var tasks = todo.Select(async a =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    using var resp = await http.GetAsync("https://www.nmc.cn" + a.Url, ct);
                    if (!resp.IsSuccessStatusCode) return;
                    var html = await resp.Content.ReadAsStringAsync(ct);
                    var match = System.Text.RegularExpressions.Regex.Match(html, @"<p[^>]*>([^<]{30,})</p>");
                    var detail = match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : "";
                    lock (_detailGate)
                    {
                        if (_detailCache.Count > 800) _detailCache.Clear();
                        _detailCache[a.AlertId] = detail;
                    }
                    a.Detail = detail;
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 单条详情失败不影响整体 */ }
        });
        await Task.WhenAll(tasks);
    }
}

/// <summary>
/// 和风天气客户端（新一代专属 Host API，实测结构 2026-09）：
/// /geo/v2/city/lookup（带 /geo 前缀）、/v7/weather/now|24h|3d|7d、
/// /weatheralert/v1/current/{lat}/{lon}（新一代预警：正文+防御指南内置，按坐标精准匹配）、
/// 鉴权：X-QW-Api-Key 请求头。响应为 gzip（HttpClient 需开自动解压）。
/// </summary>
public sealed class QWeatherClient(HttpClient http, string host, string apiKey)
{
    readonly Dictionary<string, (string Id, double Lat, double Lon)> _locCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WeatherData> GetWeatherAsync(string city, CancellationToken ct, int forecastDays = 3)
    {
        var (id, _, _) = await ResolveLocationAsync(city, ct);
        var data = new WeatherData { City = city, FetchedAt = DateTime.Now };

        var nowRoot = await GetJsonAsync($"/v7/weather/now?location={id}", ct);
        if (nowRoot.TryGetProperty("now", out var now))
        {
            data.Now = new CurrentObs
            {
                Temp = Num(now, "temp"),
                Feels = Num(now, "feelsLike"),
                Humidity = (int)Num(now, "humidity"),
                WindDir = Str(now, "windDir"),
                WindKph = Num(now, "windSpeed"),
                PrecipMm = Num(now, "precip"),
                Code = TextToCode(Str(now, "text")),
                ObsTime = DateTimeOffset.TryParse(Str(now, "obsTime"), out var t) ? t.LocalDateTime : DateTime.Now,
            };
        }

        int span = forecastDays <= 3 ? 3 : 7;
        var dailyRoot = await GetJsonAsync($"/v7/weather/{span}d?location={id}", ct);
        if (dailyRoot.TryGetProperty("daily", out var daily))
            foreach (var day in daily.EnumerateArray())
            {
                data.Days.Add(new DayForecast
                {
                    Date = DateTime.TryParse(Str(day, "fxDate"), out var d0) ? d0 : DateTime.Today,
                    MaxC = Num(day, "tempMax"),
                    MinC = Num(day, "tempMin"),
                    DayCode = TextToCode(Str(day, "textDay")),
                    Sunrise = DateTime.TryParse($"{Str(day, "fxDate")} {Str(day, "sunrise")}", out var sr) ? sr : default,
                    Sunset = DateTime.TryParse($"{Str(day, "fxDate")} {Str(day, "sunset")}", out var ss) ? ss : default,
                });
            }

        // 24 小时逐小时（含逐小时降水概率/降水量）——按日期归入对应的 DayForecast
        var hourlyRoot = await GetJsonAsync($"/v7/weather/24h?location={id}", ct);
        if (hourlyRoot.TryGetProperty("hourly", out var hourly))
            foreach (var h in hourly.EnumerateArray())
            {
                var fx = DateTimeOffset.TryParse(Str(h, "fxTime"), out var ft) ? ft.LocalDateTime : DateTime.Now;
                var day = data.Days.FirstOrDefault(d => d.Date == fx.Date);
                if (day == null) continue;
                day.Hourly.Add(new HourPoint
                {
                    Time = fx.Hour,
                    TempC = Num(h, "temp"),
                    RainProb = (int)Num(h, "pop"),
                    ThunderProb = 0,
                    Code = TextToCode(Str(h, "text")),
                });
            }
        return data;
    }

    public async Task<List<NmcAlarm>> GetWarningsAsync(string city, CancellationToken ct)
    {
        var (_, lat, lon) = await ResolveLocationAsync(city, ct);
        var root = await GetJsonAsync($"/weatheralert/v1/current/{lat:0.##}/{lon:0.##}", ct);
        var list = new List<NmcAlarm>();
        if (root.TryGetProperty("alerts", out var alerts) && alerts.ValueKind == JsonValueKind.Array)
            foreach (var a in alerts.EnumerateArray())
            {
                var detail = Str(a, "description");
                var ins = Str(a, "instruction");
                if (ins.Length > 0) detail += (detail.Length > 0 ? "\n" : "") + "防御指南：" + ins;
                list.Add(new NmcAlarm
                {
                    AlertId = Str(a, "id"),
                    Title = Str(a, "headline"),
                    IssueTime = DateTimeOffset.TryParse(Str(a, "issuedTime"), out var t)
                        ? t.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "",
                    Url = "",
                    Detail = detail,
                });
            }
        return list;
    }

    async Task<(string Id, double Lat, double Lon)> ResolveLocationAsync(string city, CancellationToken ct)
    {
        if (_locCache.TryGetValue(city, out var cached)) return cached;
        var root = await GetJsonAsync($"/geo/v2/city/lookup?location={Uri.EscapeDataString(city)}&number=1", ct);
        if (root.TryGetProperty("location", out var loc) && loc.GetArrayLength() > 0)
        {
            var l = loc[0];
            var result = (Str(l, "id"),
                double.TryParse(Str(l, "lat"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var la) ? la : 0,
                double.TryParse(Str(l, "lon"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lo) ? lo : 0);
            _locCache[city] = result;
            return result;
        }
        throw new Exception($"和风天气无法解析城市：{city}");
    }

    async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, host + path);
        req.Headers.TryAddWithoutValidation("X-QW-Api-Key", apiKey);
        using var resp = await http.SendAsync(req, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
        {
            string title = err.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string detail = err.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
            throw new Exception($"和风天气错误（{title}）：{detail}");
        }
        if (root.TryGetProperty("code", out var c) && c.GetString() is { } code && code != "200")
            throw new Exception($"和风天气接口返回 code={code}（402=配额不足，401=Key 无效）");
        return root.Clone();
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
              : double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d
              : -999
            : -999;

    /// <summary>中文天气现象→WWO 近似代码（复用现有中文映射）。</summary>
    static int TextToCode(string text) => text switch
    {
        "晴" => 113, "多云" => 116, "阴" => 119, "小雨" => 296, "中雨" => 302, "大雨" => 308,
        "暴雨" => 308, "阵雨" => 353, "雷阵雨" => 389, "雨夹雪" => 362, "小雪" => 368, "中雪" => 371,
        "大雪" => 371, "雾" => 248, "霾" => 143, "扬沙" => 143, "浮尘" => 143,
        _ => text.Contains("雪") ? 371 : text.Contains("雨") ? 296 : text.Contains("雷") ? 389 : 116
    };
}
