using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace chuxin.Weather;

public sealed class DaySnap
{
    public string Date { get; set; } = "";
    public double TempMax { get; set; }
    public double TempMin { get; set; }
    public double TempNow { get; set; }
    public int Code { get; set; }
    public string Desc { get; set; } = "";
    public double Uv { get; set; } = -1;        // 日紫外线极值（day_over_day 对比用）
    public double PrecipMm { get; set; } = -1;  // 日降水量
}

public sealed class MonitorState
{
    public Dictionary<string, List<string>> Announced { get; set; } = new();   // 城市→已播报预警ID
    public Dictionary<string, DaySnap> Baselines { get; set; } = new();        // 城市→当日基线
    public Dictionary<string, List<DaySnap>> DayRecords { get; set; } = new(); // 城市→当日历次检测
    public Dictionary<string, DaySnap> Yesterday { get; set; } = new();        // 城市→昨日快照
    public Dictionary<string, DateTime> FiredAt { get; set; } = new();         // 防抖键→上次触发时间（实现 CooldownHours 语义）
    public List<string> PendingPushes { get; set; } = new();                   // 静默时段暂存的 P0
    public string? LastContextLine { get; set; }
    public string? BriefDate { get; set; }
    public string? LastWarningCycle { get; set; }   // 心跳观测
    public string? LastChangeCycle { get; set; }
}

/// <summary>状态轨只读快照（UI 用）：来自配置与本地缓存，零网络。</summary>
public sealed record RailStatus(
    string City,
    bool HasNow,
    string Desc,
    double Temp,
    int Code,
    string? LastWarningCheck,
    string? LastChangeCheck,
    int RuleTotal,
    int RuleEnabled);

/// <summary>
/// 推送状态机：预警轮询、变化检测（规则引擎）、晨报、常驻天气上下文（P1）、三级分发（DESIGN.md §5）。
/// 状态按角色持久化（Character.StorageKey 前缀），全部后台任务可被 DestroyCancellationToken 取消。
/// </summary>
public sealed class WeatherMonitor(
    WeatherModule module,
    Interactor<WeatherModule> interactor,
    ChatBot chatBot,
    ILogger<WeatherModule> logger)
{
    public const string ContextMarker = "[当前天气·chuxin.Weather]";
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    MonitorState _state = new();
    readonly object _gate = new();
    string? _lastWarnLog; // 同类错误只报一次

    string StatePath => Path.Combine(AlifePath.StorageFolderPath, module.StorageKey, "Weather", "state.json");
    WeatherConfig Cfg => module.Configuration;

    public string[] MonitoredCities =>
        (new[] { Cfg.DefaultCity })
            .Concat(Cfg.WatchCities.Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries))
            .Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>UI 状态轨快照：锁内组装（默认城市实况取当日基线，规则/心跳取配置与本地状态）。</summary>
    public RailStatus GetRailStatus()
    {
        lock (_gate)
        {
            bool has = _state.Baselines.TryGetValue(Cfg.DefaultCity, out var snap);
            return new RailStatus(
                Cfg.DefaultCity, has,
                has ? snap!.Desc : "", has ? snap!.TempNow : 0, has ? snap!.Code : -1,
                _state.LastWarningCycle, _state.LastChangeCycle,
                Cfg.Rules.Count, Cfg.Rules.Count(r => r.Enabled));
        }
    }

    #region 状态持久化

    public void LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                _state = JsonSerializer.Deserialize<MonitorState>(File.ReadAllText(StatePath)) ?? new();
        }
        catch (Exception ex) { logger.LogWarning(ex, "天气状态读取失败，使用新状态"); _state = new(); }
    }

    void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            lock (_gate) File.WriteAllText(StatePath, JsonSerializer.Serialize(_state, JsonOpts));
        }
        catch (Exception ex) { logger.LogWarning(ex, "天气状态保存失败"); }
    }

    /// <summary>换城/重置：记录当前生效预警为已播报，避免换城刷屏。</summary>
    public async Task ResetCityBaselineAsync(string city, CancellationToken ct)
    {
        var alarms = await GetAlarmsForCityAsync(city, ct);
        lock (_gate) _state.Announced[city] = alarms.Select(a => a.AlertId).ToList();
        SaveState();
    }

    #endregion

    #region 时间窗

    public bool InQuietHours()
    {
        string spec = Cfg.QuietHours?.Trim() ?? "";
        if (spec.Length == 0) return false;
        var parts = spec.Split('-');
        if (parts.Length != 2 || !TimeSpan.TryParse(parts[0].Trim(), out var start) || !TimeSpan.TryParse(parts[1].Trim(), out var end))
            return WarnOnce($"静默时段格式非法：{spec}（应为 HH:mm-HH:mm），已忽略");
        var now = DateTime.Now.TimeOfDay;
        return start <= end ? now >= start && now < end : now >= start || now < end; // 跨零点
    }

    bool WarnOnce(string msg)
    {
        if (_lastWarnLog == msg) return false;
        _lastWarnLog = msg;
        logger.LogWarning("{Msg}", msg);
        return false;
    }

    #endregion

    #region 数据获取（按配置选源）

    public async Task<WeatherData?> GetWeatherAsync(string city, CancellationToken ct, int forecastDays = 3) =>
        Cfg.QuerySource == "qweather"
            ? await module.QWeather.GetWeatherAsync(city, ct, forecastDays)
            : await module.Wttr.GetAsync(city, ct);

    public async Task<List<NmcAlarm>> GetAlarmsForCityAsync(string city, CancellationToken ct) =>
        Cfg.WarningSource == "qweather" ? await module.QWeather.GetWarningsAsync(city, ct)
        : Cfg.WarningSource == "nmc" ? await module.Nmc.GetForCityAsync(city, ct)
        : [];

    async Task<Dictionary<string, List<NmcAlarm>>> GetAlarmsForCitiesAsync(string[] cities, CancellationToken ct)
    {
        var result = new Dictionary<string, List<NmcAlarm>>();
        if (Cfg.WarningSource == "off") return result;
        if (Cfg.WarningSource == "nmc")
        {
            var all = await module.Nmc.GetAllAsync(ct);
            if (all == null) { WarnOnce("中央气象台预警接口结构异常（可能已变更），本轮跳过推送"); return result; }
            foreach (var city in cities)
                result[city] = all.Where(a => a.Title.Contains(city, StringComparison.Ordinal)).ToList();
            return result;
        }
        foreach (var city in cities)
        {
            try { result[city] = await module.QWeather.GetWarningsAsync(city, ct); }
            catch (OperationCanceledException) { throw; }
            // 失败城市不放入 result（与"确实无预警"区分）：WarningCycle 对缺失城市整城跳过，防止误推"预警解除"
            catch (Exception ex) { WarnOnce($"和风预警获取失败（{city}）：{ex.Message}"); }
        }
        return result;
    }

    #endregion

    #region 预警监控（P0）

    /// <param name="baselineMode">true=只记录基线不推送（启动/换城）</param>
    public async Task WarningCycleAsync(bool baselineMode, CancellationToken ct)
    {
        if (!Cfg.EnableWarningMonitor || Cfg.WarningSource == "off") return;
        lock (_gate) { _state.LastWarningCycle = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); }
        var cities = MonitoredCities;
        var byCity = await GetAlarmsForCitiesAsync(cities, ct);
        var truncation = Cfg.WarningSource == "nmc" ? module.Nmc.ConsumeTruncationNotice() : null;
        if (!string.IsNullOrEmpty(truncation)) WarnOnce(truncation);
        if (byCity.Count == 0 && Cfg.WarningSource != "off") { if (baselineMode) SaveState(); return; }

        var msgs = new List<string>();
        var newAlerts = new List<NmcAlarm>();          // 本轮新预警（待抓正文）
        var newAlertsCities = new Dictionary<NmcAlarm, string>();
        lock (_gate)
        {
            foreach (var city in cities)
            {
                // 取数失败的城市不在 byCity 里：整城跳过（不评新增、不判解除），等下一轮
                if (!byCity.TryGetValue(city, out var alarms)) continue;
                var known = _state.Announced.TryGetValue(city, out var s) ? s : new List<string>();
                var now0 = DateTime.Now;

                foreach (var a in alarms.Where(a => a.EndTime == null || a.EndTime > now0))
                {
                    if (known.Contains(a.AlertId)) continue;
                    // 变更归并（DESIGN.md §11.5）：新 id 若取代已播报的旧 id，视为"更新"而非"新增"，
                    // 旧 id 当场移出已播报集合 → 后面的解除判定不会为其补推"解除"
                    var olds = a.Supersedes?.Where(known.Contains).ToList() ?? [];
                    foreach (var oldId in olds) known.Remove(oldId);
                    a.IsUpdate = olds.Count > 0;
                    known.Add(a.AlertId);
                    if (!baselineMode)
                    {
                        newAlerts.Add(a);
                        newAlertsCities[a] = city;
                    }
                }
                // 解除"检测"与"通知"解耦：消失的 id 始终移出已播报集合（防 state 无界增长），是否播报看 NotifyOnClear
                foreach (var gone in known.Where(id => alarms.All(x => x.AlertId != id)).ToList())
                {
                    known.Remove(gone);
                    if (!baselineMode && Cfg.NotifyOnClear)
                        msgs.Add($"[P0·预警解除] {city}\n此前的一条预警已从生效列表中消失（可能已解除）。可再向用户提一句。");
                }
                _state.Announced[city] = known;
            }
        }

        // 抓取新预警正文（全量、4 并发、带缓存；失败降级为显示链接）
        if (newAlerts.Count > 0)
        {
            try { await module.Nmc.EnrichDetailsAsync(newAlerts, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "预警正文抓取失败，降级为仅标题"); }
            foreach (var a in newAlerts)
            {
                var city2 = newAlertsCities[a];
                var detailLine = a.Detail.Length > 0 ? $"\n正文：{WeatherTexts.Truncate(a.Detail, 200)}" : "";
                var urlLine = a.Detail.Length == 0 && a.Url.Length > 0 ? $"\n详情：https://www.nmc.cn{a.Url}" : "";
                var head = a.IsUpdate ? "[P0·预警更新]" : "[P0·预警]";
                var sender = a.SenderName.Length > 0 ? a.SenderName + " " : "";
                msgs.Add($"{head} {city2}\n{sender}{a.Title}（发布 {a.IssueTime}）{detailLine}{urlLine}\n请适时提醒用户注意安全，酌情安排出行建议。");
            }
        }
        SaveState();
        if (!baselineMode && msgs.Count > 0)
            await DispatchAsync(msgs, ct);
    }

    #endregion

    #region 变化检测（规则引擎 + P1 上下文）

    public async Task ChangeCycleAsync(CancellationToken ct)
    {
        if (!Cfg.EnableChangeMonitor) return;
        lock (_gate) { _state.LastChangeCycle = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); }
        var cities = MonitoredCities;
        var noDataMetrics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pushMsgs = new List<string>();
        var silentNotes = new List<string>();
        WeatherData? defaultCityData = null;

        // 预警冲突抑制依据：当前是否有生效预警（一次请求，10 分钟缓存）
        var alarmsByCity = await GetAlarmsForCitiesAsync(cities, ct);
        string today = DateTime.Now.ToString("yyyy-MM-dd");

        foreach (var city in cities)
        {
            ct.ThrowIfCancellationRequested();
            WeatherData? data;
            try { data = await GetWeatherAsync(city, ct); }
            // per-city 隔离：单个城市失败（城市名无法解析/配额/网络）只跳过该城，不中止整轮规则评价
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { WarnOnce($"天气数据获取失败（{city}）：{ex.Message}"); continue; }
            if (data == null) { WarnOnce($"天气数据获取失败（{city}），本轮跳过"); continue; }
            if (city == Cfg.DefaultCity) defaultCityData = data;

            UpdateSnapshots(city, data);

            // 历史兜底：本地昨日快照缺失（重装/首次跨天）时用时光机补齐，day_over_day 规则不哑一天
            if (State.Yesterday(city) == null && Cfg.QuerySource == "qweather")
                await TryFillYesterdayAsync(city, ct);
            // 空气质量：慢变数据（2h 缓存），附加后 aqi 规则指标与状态轨可用
            if (Cfg.EnableAirQuality && Cfg.QuerySource == "qweather")
            {
                try { data.Air = await module.QWeather.GetAirAsync(city, ct); }
                catch (OperationCanceledException) { throw; }
                catch { /* 空气质量失败不影响主链路 */ }
            }

            var hasWarning = alarmsByCity.TryGetValue(city, out var al) && al.Count > 0;
            foreach (var hit in module.Engine.Evaluate(Cfg.Rules, city, data,
                         State.Baseline(city), State.Yesterday(city), noDataMetrics))
            {
                string key = $"{city}:{hit.Rule.Name}";
                lock (_gate)
                {
                    if (_state.FiredAt.TryGetValue(key, out var last) &&
                        (DateTime.Now - last).TotalHours < Math.Max(0, hit.Rule.CooldownHours))
                        continue;
                    _state.FiredAt[key] = DateTime.Now;
                }

                string text = $"[P0·{hit.Rule.Name}] {city}\n{hit.Text}\n请酌情提醒用户。";
                if (hit.Rule.Metric == "precip_prob" && data.Days.FirstOrDefault()?.Hourly is { Count: > 0 } thunderHours
                    && thunderHours.Max(h => h.ThunderProb) >= 50)
                    text = text.Replace("请酌情提醒用户。", "同时雷暴概率较高，请注意。\n请酌情提醒用户。");
                bool downgraded = hasWarning && hit.Rule.IsBuiltin && hit.Rule.Metric != "weather_code";
                if (downgraded || hit.Rule.Level == "silent")
                    silentNotes.Add($"（{hit.Rule.Name}：{hit.Text}）");
                else
                    pushMsgs.Add(text);
            }

            // 临近降雨（触发式，DESIGN.md §11.4）：逐时概率先判，命中才拉分钟级数据
            if (Cfg.QuerySource == "qweather" && Cfg.EnableRainNowcast)
            {
                try { await CheckRainNowcastAsync(city, data, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "临近降雨检查失败（{City}）", city); }
            }
        }
        if (noDataMetrics.Count > 0)
            WarnOnce($"以下指标在当前数据源无数据，相关规则未评价：{string.Join("、", noDataMetrics)}");

        // 数据卫生：剔除已不在监控范围的城市快照（换城/清空监控列表后遗留）
        lock (_gate)
        {
            var live = cities.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var staleCity in _state.Baselines.Keys.Where(c => !live.Contains(c)).ToList())
            {
                _state.Baselines.Remove(staleCity);
                _state.DayRecords.Remove(staleCity);
                _state.Yesterday.Remove(staleCity);
                _state.Announced.Remove(staleCity);
            }
        }

        // P1：常驻天气行（静默注入，不唤醒）
        if (defaultCityData != null)
        {
            int warnCount = alarmsByCity.TryGetValue(Cfg.DefaultCity, out var al) ? al.Count : 0;
            UpdateContextLine(defaultCityData, warnCount, silentNotes, ct);
        }
        SaveState();

        // P0：静默时段暂存，结束后补报
        if (pushMsgs.Count > 0)
            await DispatchAsync(pushMsgs, ct);
    }

    void UpdateSnapshots(string city, WeatherData data)
    {
        var todayF = data.Days.FirstOrDefault();
        string date = todayF?.Date.ToString("yyyy-MM-dd") ?? DateTime.Now.ToString("yyyy-MM-dd");
        var snap = new DaySnap
        {
            Date = date, TempMax = todayF?.MaxC ?? data.Now.Temp, TempMin = todayF?.MinC ?? data.Now.Temp,
            TempNow = data.Now.Temp, Code = data.Now.Code,
            Desc = !string.IsNullOrEmpty(data.Now.Text) ? data.Now.Text : WeatherTexts.CodeCnOf(data.Now.Code),
            Uv = todayF?.Uv ?? -1, PrecipMm = todayF?.PrecipMm ?? -1,
        };
        lock (_gate)
        {
            if (_state.Baselines.TryGetValue(city, out var old) && old.Date != date)
            {
                // 跨天：汇总昨日历次记录 → 昨日快照
                var records = _state.DayRecords.TryGetValue(city, out var r) ? r : null;
                if (records is { Count: > 0 })
                    _state.Yesterday[city] = new DaySnap
                    {
                        Date = old.Date,
                        TempMax = records.Max(x => x.TempMax),
                        TempMin = records.Min(x => x.TempMin),
                        TempNow = records[^1].TempNow,
                        Code = records[records.Count / 2].Code,
                        Desc = records[records.Count / 2].Desc,
                        Uv = records.Where(x => x.Uv >= 0).Select(x => x.Uv).DefaultIfEmpty(-1).Max(),
                        PrecipMm = records[^1].PrecipMm,
                    };
                _state.Baselines[city] = snap;
                _state.DayRecords[city] = [snap];
                PruneFiredKeys(date);
            }
            else if (!_state.Baselines.ContainsKey(city))
            {
                _state.Baselines[city] = snap;
                _state.DayRecords[city] = [snap];
            }
            else
            {
                var records = _state.DayRecords.TryGetValue(city, out var r) ? r : (_state.DayRecords[city] = []);
                records.Add(snap);
                if (records.Count > 64) records.RemoveAt(0);
            }
        }
    }

    void PruneFiredKeys(string today)
    {
        lock (_gate)
        {
            var stale = _state.FiredAt.Where(kv => (DateTime.Now - kv.Value).TotalDays > 7).ToList();
            foreach (var k in stale) _state.FiredAt.Remove(k.Key);
        }
    }

    MonitorState State => _state; // 引擎读取快照用（在锁外读，字段级竞争可容忍：最坏跳过一轮）

    readonly HashSet<string> _histTried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>历史兜底（DESIGN.md §11.5）：本地昨日快照缺失时拉一次时光机历史补齐（每城市每次运行只试一次）。</summary>
    async Task TryFillYesterdayAsync(string city, CancellationToken ct)
    {
        if (!_histTried.Add(city)) return;
        try
        {
            var baseDate = DateTime.TryParse(State.Baseline(city)?.Date, out var bd) ? bd : DateTime.Today;
            var yd = baseDate.AddDays(-1);
            if (yd < DateTime.Today.AddDays(-10)) return;   // 时光机只覆盖过去 10 天
            var h = await module.QWeather.GetHistoryAsync(city, yd, ct);
            if (h == null || h.TempMax <= -900) return;
            lock (_gate)
                _state.Yesterday[city] = new DaySnap
                {
                    Date = yd.ToString("yyyy-MM-dd"),
                    TempMax = h.TempMax, TempMin = h.TempMin, TempNow = h.TempMin,
                    Code = h.Code, Desc = h.Desc, PrecipMm = h.Precip,
                };
            logger.LogInformation("城市 {City} 的昨日快照由时光机历史数据补齐（{Date}）", city, yd.ToString("yyyy-MM-dd"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "历史数据补齐昨日快照失败（{City}）", city); }
    }

    /// <summary>临近降雨推送：未来 3h 逐时概率 ≥50% 且当前无雨 → 拉分钟级降水精确定时，12h 冷却/城市。</summary>
    async Task CheckRainNowcastAsync(string city, WeatherData data, CancellationToken ct)
    {
        if (data.Now.PrecipMm > 0.05) return;   // 已经在下雨，无需"临近"提醒
        var window = data.HourlyFlat.Where(h => h.Ts >= DateTime.Now && h.Ts <= DateTime.Now.AddHours(3)).ToList();
        if (window.Count == 0 || window.Max(h => h.RainProb) < 50) return;

        string key = $"{city}:__rainnowcast__";
        lock (_gate)
        {
            if (_state.FiredAt.TryGetValue(key, out var last) && (DateTime.Now - last).TotalHours < 12) return;
        }
        var mi = await module.QWeather.GetMinutelyAsync(city, ct);
        if (mi is not { Supported: true } || mi.Items.Count == 0) return;
        var first = mi.Items.FirstOrDefault(p => p.Precip > 0);
        if (first == null) return;   // 数据说两小时内没雨（逐时概率虚高）
        lock (_gate) _state.FiredAt[key] = DateTime.Now;
        SaveState();

        int mins = Math.Max(1, (int)Math.Round((first.FxTime - DateTime.Now).TotalMinutes));
        string kind = first.Type == "snow" ? "下雪" : "下雨";
        await DispatchAsync([$"[P0·临近降雨] {city}\n约 {mins} 分钟后开始{kind}（未来两小时：{mi.Summary}）。\n请提醒用户带伞、调整出行安排。"], ct);
    }

    #endregion

    #region P1 常驻天气上下文

    void UpdateContextLine(WeatherData data, int warningCount, List<string> silentNotes, CancellationToken ct)
    {
        var yesterday = State.Yesterday.TryGetValue(data.City, out var y) ? y : null;
        string line = WeatherTexts.FormatContextLine(data, data, yesterday, warningCount, silentNotes);
        lock (_gate) { if (line == _state.LastContextLine) return; _state.LastContextLine = line; }
        try
        {
            chatBot.EditChatHistory(thread =>
            {
                var old = thread.ChatHistory.FirstOrDefault(m => m.Content?.StartsWith(ContextMarker, StringComparison.Ordinal) == true);
                if (old != null) thread.ChatHistory.Remove(old);
                thread.ChatHistory.AddSystemMessage(line);
            }, "更新天气上下文");
        }
        catch (Exception ex) { logger.LogWarning(ex, "天气上下文注入失败"); }
    }

    /// <summary>OnStart / 换城时的立即注入（幂等）：拉默认城市天气 → 种当日基线 → 原地替换常驻天气行。</summary>
    public async Task<bool> TryInjectContextLineAsync(CancellationToken ct)
    {
        try
        {
            var data = await GetWeatherAsync(Cfg.DefaultCity, ct);
            if (data == null) return false;
            UpdateSnapshots(Cfg.DefaultCity, data);   // 顺带种基线，规则引擎对新城立即可用
            var alarms = await GetAlarmsForCityAsync(Cfg.DefaultCity, ct);
            UpdateContextLine(data, alarms.Count, [], ct);
            return true;
        }
        catch (Exception ex) { logger.LogWarning(ex, "首次天气上下文注入失败"); return false; }
    }

    #endregion

    #region 晨报（P2）

    DateTime? _briefFailUntil;   // 晨报失败退避：10 分钟内不重试，避免心跳期打爆数据源

    public async Task BriefAsync(CancellationToken ct)
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        lock (_gate) { if (_state.BriefDate == today) return; }
        if (_briefFailUntil.HasValue && DateTime.Now < _briefFailUntil.Value) return;
        try
        {
            var data = await GetWeatherAsync(Cfg.DefaultCity, ct);
            if (data == null) { _briefFailUntil = DateTime.Now.AddMinutes(10); return; }
            var alarms = await GetAlarmsForCityAsync(Cfg.DefaultCity, ct);
            var sb = new System.Text.StringBuilder();
            sb.Append($"[P2·晨报] 早上好，今日天气：{Cfg.DefaultCity} ");
            sb.Append(WeatherTexts.CodeCnOf(data.Now.Code));
            var todayF = data.Days.FirstOrDefault();
            if (todayF != null && todayF.MaxC > -900) sb.Append($"，{todayF.MinC:0.#}~{todayF.MaxC:0.#}°C");
            if (State.Yesterday.TryGetValue(Cfg.DefaultCity, out var y) && todayF is { MaxC: > -900 })
            {
                double delta = todayF.MaxC - y.TempMax;
                if (Math.Abs(delta) >= 3) sb.Append($"（较昨日最高温{(delta > 0 ? "升" : "降")} {Math.Abs(delta):0.#}°C）");
            }
            if (todayF != null && todayF.Hourly.Count > 0)
            {
                int rain = todayF.Hourly.Max(h => h.RainProb);
                if (rain >= 30) sb.Append($"；今日降水概率最高 {rain}%");
                if (todayF.Sunrise != default && todayF.Sunset != default)
                    sb.Append($"；日出 {todayF.Sunrise:HH:mm}，日落 {todayF.Sunset:HH:mm}");
            }
            // 和风源扩展：空气质量 / 穿衣指数 / 月相（失败静默缺席，不影响晨报主文）
            if (Cfg.QuerySource == "qweather")
            {
                if (Cfg.EnableAirQuality)
                {
                    var air = await module.QWeather.GetAirAsync(Cfg.DefaultCity, ct);
                    if (air is { Aqi: >= 0 }) sb.Append($"；空气质量 {air.Category}（AQI {air.Aqi:0}）");
                }
                if (Cfg.EnableIndices)
                {
                    var idx = await module.QWeather.GetIndicesAsync(Cfg.DefaultCity, "3", 1, ct);
                    var cloth = idx?.FirstOrDefault();
                    if (cloth != null) sb.Append($"；穿衣{cloth.Category}");
                }
                string? moon = WeatherTexts.MoonCnOf(todayF?.MoonPhase ?? "");
                if (moon != null) sb.Append($"；月相 {moon}");
            }
            sb.Append(alarms.Count > 0
                ? $"。⚠ 当前有 {alarms.Count} 条生效预警：{string.Join("；", alarms.Take(3).Select(a => a.Title))}。请主动向用户播报。"
                : "。当前无生效预警。");
            interactor.Poke(sb.ToString());
            lock (_gate) _state.BriefDate = today;   // 成功才标记，失败下轮心跳退避重试
            SaveState();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "晨报生成失败"); _briefFailUntil = DateTime.Now.AddMinutes(10); }
    }

    #endregion

    #region 分发与模拟测试

    /// <summary>P0 分发：静默时段暂存，非静默期合并为一次 Poke（含补报）。</summary>
    async Task DispatchAsync(List<string> msgs, CancellationToken ct)
    {
        if (InQuietHours())
        {
            lock (_gate)
            {
                _state.PendingPushes.AddRange(msgs);
                while (_state.PendingPushes.Count > 20) _state.PendingPushes.RemoveAt(0); // 防膨胀
            }
            SaveState();   // 暂存必须落盘，否则静默期重启丢推送
            return;
        }
        List<string> pending;
        lock (_gate) { pending = _state.PendingPushes; _state.PendingPushes = []; }
        SaveState();
        var all = pending.Concat(msgs).ToList();
        if (all.Count == 0) return;
        interactor.Poke(string.Join("\n\n", all));
        await Task.CompletedTask;
    }

    /// <summary>心跳调用：非静默期且有暂存推送 → 立即补报（静默结束不依赖新推送触发）。</summary>
    public void FlushPendingIfDue()
    {
        if (InQuietHours()) return;
        List<string> pending;
        lock (_gate)
        {
            if (_state.PendingPushes.Count == 0) return;
            pending = _state.PendingPushes;
            _state.PendingPushes = [];
        }
        SaveState();
        interactor.Poke(string.Join("\n\n", pending));
    }

    /// <summary>UI 模拟测试：按当前数据试跑一条规则，返回命中结果说明。</summary>
    public async Task<string> TestRuleAsync(WeatherRule rule, CancellationToken ct)
    {
        var err = rule.Validate();
        if (err != null) return $"规则非法：{err}";
        if (!rule.Enabled) return "规则已禁用，请先启用再测试";
        var data = await GetWeatherAsync(string.IsNullOrEmpty(rule.City) ? Cfg.DefaultCity : rule.City, ct);
        if (data == null) return "天气数据获取失败，请稍后再试";
        var targetCity = string.IsNullOrEmpty(rule.City) ? Cfg.DefaultCity : rule.City;
        // 注意：纯模拟评估，不写快照（UI 测试不得污染监测状态）
        var noData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = module.Engine.Evaluate([rule], targetCity, data,
            State.Baseline(targetCity), State.Yesterday(targetCity), noData);
        if (noData.Count > 0) return $"该指标在当前数据源（{Cfg.QuerySource}）无数据，规则不会生效";
        if (hits.Count == 0) return "未命中：当前数据不满足规则条件（阈值合适时，真实天气变化到阈值即推送）";
        var hit = hits[0];
        return $"✔ 命中！渲染文案：\n{hit.Text}";
    }

    #endregion
}

public static class MonitorStateExt
{
    public static DaySnap? Baseline(this MonitorState s, string city) =>
        s.Baselines.TryGetValue(city, out var v) ? v : null;
    public static DaySnap? Yesterday(this MonitorState s, string city) =>
        s.Yesterday.TryGetValue(city, out var v) ? v : null;
}
