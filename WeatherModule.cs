using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace chuxin.Weather;

/// <summary>
/// 天气服务模块：9 个查询函数 + set_default_city（DESIGN.md §11.3）；三级分发推送子系统（DESIGN.md §5/§11.4）。
/// 零配置可用（wttr 查询 + 中央气象台预警），配 Key 后接入和风免费组全量九类数据域。
/// </summary>
[Module("天气服务",
    "提供天气查询、灾害预警推送与天气变化提醒，规则可在 UI 中自定义。",
    defaultCategory: "初心的小工具",
    EditorUI = typeof(WeatherModuleUI))]
public class WeatherModule(
    XmlFunctionCaller functionCaller,
    ILogger<WeatherModule> logger,
    Interactor<WeatherModule> interactor,
    ChatBot chatBot,
    ConfigurationSystem configurationSystem) :
    ChatBehaviour,
    IConfigurable<WeatherConfig>
{
    public WeatherConfig Configuration { get; set; } = null!;

    readonly HttpClient _http = CreateHttp();
    WeatherMonitor _monitor = null!;

    WttrClient? _wttr;
    NmcAlarmClient? _nmc;
    QWeatherClient? _qweather;
    public WttrClient Wttr => _wttr ??= new(_http);
    public NmcAlarmClient Nmc => _nmc ??= new(_http);
    public QWeatherClient QWeather => _qweather ??= new(_http, Configuration.QWeatherApiHost, Configuration.QWeatherApiKey, Configuration.QWeatherApiVersion);
    public WeatherRuleEngine Engine { get; } = new();

    public string StorageKey => Character?.StorageKey ?? "Character\\__unknown__";

    static HttpClient CreateHttp()
    {
        var handler = new HttpClientHandler { UseProxy = false, AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "chuxin-weather-plugin/1.0 (Alife)");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    protected override Task OnAwake()
    {
        // 默认规则集：按名合并（全新安装全量预置；老配置升级自动补新增内置规则，不动用户改动）
        var defaults = DefaultRules.Create();
        bool added = false;
        foreach (var d in defaults)
            if (!Configuration.Rules.Any(r => r.Name == d.Name))
            {
                Configuration.Rules.Add(d);
                added = true;
            }
        if (added || !Configuration.RulesSeeded)
        {
            Configuration.RulesSeeded = true;
            SaveConfig();
        }
        foreach (var rule in Configuration.Rules.Where(r => r.Validate() != null).ToList())
        {
            rule.Enabled = false;   // 非法规则真实禁用（引擎以 Enabled 为准）
            logger.LogWarning("规则「{Name}」非法：{Err}，已自动禁用", rule.Name, rule.Validate());
        }

        _monitor = new WeatherMonitor(this, interactor, chatBot, logger);
        _monitor.LoadState();

        functionCaller.RegisterHandler(new XmlHandler(this)
        {
            Description = "查询天气/逐日预报/逐小时预报/分钟级降雨/空气质量/生活指数/天文/历史天气/灾害预警，并可修改默认城市。",
            Explanation = """
                天气服务使用说明（city 均可省略 = 默认城市）
                - <query_weather city=""/> 实时天气全套：现象/温度/体感/湿度/风/紫外线/气压/能见度/空气质量简报/未来时段降水
                - <query_forecast city="" days="3"/> 逐日预报（和风源最多 10 天：昼夜现象/降水概率与雨量/紫外线/月相）
                - <query_hourly city="" hours="72"/> 逐小时预报（1-240 小时，适合回答"明天下午几点下雨"）
                - <query_rain city=""/> 未来两小时分钟级降水（约几分钟开始下雨/雪；仅中国城市）
                - <query_air city=""/> 空气质量详情（AQI/污染物浓度/健康建议）
                - <query_indices city="" type="1,2,3" days="1"/> 生活指数（1=运动 2=洗车 3=穿衣 5=紫外线 8=舒适度）
                - <query_astro city="" days="3"/> 日出日落/天亮天黑/月出月落/月相
                - <query_history city="" days="3"/> 过去几天历史天气回顾（和风源，最多 10 天）
                - <query_warning city=""/> 当前生效的官方灾害预警
                - <set_default_city city=""/> 用户搬家或想长期关注另一城市时调用，切换后查询与推送都跟随新城市

                何时用：用户问天气/出行/穿衣/洗车/运动/空气好坏、提到天气变化、问"什么时候下雨/天黑/月亮几号圆"，或天气推送后想看详情时。
                插件会在强天气变化、临近降雨和官方预警发布时主动提醒你（Poke），平时会静默更新你上下文中的当前天气行。
                """,
        }, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken);

        // 自有心跳调度循环（替代 OnUpdate，见 HeartbeatLoopAsync 注释）
        _ = Task.Run(() => HeartbeatLoopAsync(DestroyCancellationToken));
        return Task.CompletedTask;
    }

    protected override async Task OnStart()
    {
        try
        {
            // 基线：启动时已生效预警只记录不播报（AnnounceOnStart 可改）；随后静默注入常驻天气行
            await _monitor.WarningCycleAsync(baselineMode: !Configuration.AnnounceOnStart, DestroyCancellationToken);
            await _monitor.TryInjectContextLineAsync(DestroyCancellationToken);
        }
        catch (Exception ex) { logger.LogWarning(ex, "天气模块启动初始化失败（不影响查询功能）"); }
        finally
        {
            // 无论初始化成败都放行心跳：各周期任务自身有 per-tick 容错，卡死比失败更糟
            _nextWarning = DateTime.Now.AddMinutes(Math.Max(5, Configuration.PollIntervalMinutes));
            _nextChange = DateTime.Now.AddMinutes(2); // 首次变化检测：启动后 2 分钟，种子当日基线
            _started = true;
        }
    }

    bool _started;
    DateTime _nextWarning = DateTime.MinValue, _nextChange = DateTime.MinValue, _nextBrief = DateTime.MinValue;
    int _warningBusy, _changeBusy, _briefBusy;

    /// <summary>
    /// 自有心跳循环：不依赖框架共享的 UpdateAsync 驱动（该驱动中任何模块的异常都会终止整个循环
    /// 且只写 Console，会导致所有模块的 OnUpdate 静默停摆）。每 5 秒墙钟检查，到点派发后台任务。
    /// </summary>
    async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new System.Threading.PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    if (!_started) continue;
                    _monitor.FlushPendingIfDue();   // 静默结束立即补报，不等下一次推送
                    var now = DateTime.Now;
                    if (Configuration.EnableWarningMonitor && Configuration.WarningSource != "off"
                        && now >= _nextWarning && Interlocked.CompareExchange(ref _warningBusy, 1, 0) == 0)
                    {
                        _nextWarning = now.AddMinutes(Math.Max(5, Configuration.PollIntervalMinutes));
                        _ = RunGuardedAsync(ct2 => _monitor.WarningCycleAsync(false, ct2), () => Interlocked.Exchange(ref _warningBusy, 0), "预警轮询");
                    }
                    if (Configuration.EnableChangeMonitor
                        && now >= _nextChange && Interlocked.CompareExchange(ref _changeBusy, 1, 0) == 0)
                    {
                        _nextChange = now.AddMinutes(Math.Max(30, Configuration.ChangeCheckIntervalMinutes));
                        _ = RunGuardedAsync(_monitor.ChangeCycleAsync, () => Interlocked.Exchange(ref _changeBusy, 0), "变化检测");
                    }
                    if (Configuration.DailyBriefEnabled && now >= _nextBrief
                        && TimeSpan.TryParse(Configuration.DailyBriefTime, out var briefAt) && now.TimeOfDay >= briefAt
                        && Interlocked.CompareExchange(ref _briefBusy, 1, 0) == 0)
                    {
                        // 只推进 10 分钟而不是预支"明天"：BriefDate 保证当天只成功一次，
                        // 失败时靠 _briefFailUntil 退避后重试，瞬时失败不会丢掉当天晨报
                        _nextBrief = now.AddMinutes(10);
                        _ = RunGuardedAsync(_monitor.BriefAsync, () => Interlocked.Exchange(ref _briefBusy, 0), "晨报");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "天气心跳单次检查异常（循环继续）"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task RunGuardedAsync(Func<CancellationToken, Task> work, Action release, string name)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(DestroyCancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            await work(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "{Name} 执行失败", name); }
        finally { release(); }
    }

    protected override Task OnDestroy()
    {
        _http.Dispose();
        return base.OnDestroy();
    }

    #region AI 函数

    string CityOrDefault(string? city) => city?.Trim().Length > 0 ? city.Trim() : Configuration.DefaultCity;

    [XmlFunction(FunctionMode.OneShot, name: "query_weather")]
    [Description("查询实时天气全套（现象/温度/体感/湿度/风/紫外线/气压/能见度/空气质量简报/未来降水）。city 可省略（用默认城市），支持国内外任意城市")]
    public async Task QueryWeather([Description("城市名，如：北京 / 上海 / Tokyo，可省略")] string? city = null)
    {
        string c = CityOrDefault(city);
        try
        {
            var data = await SafeWeatherAsync(c);
            if (data == null) { interactor.Poke($"暂时无法获取「{c}」的天气（数据源不可用或城市名无法识别），请稍后再试或换个写法。"); return; }
            int warnCount;
            try { warnCount = (await Monitor.GetAlarmsForCityAsync(c, DestroyCancellationToken)).Count; }
            catch { warnCount = 0; }   // 预警数只是附带信息，取数失败不连累主回答
            string text = WeatherTexts.FormatCurrent(data, warnCount);
            // 分钟级补位：即将降雨时给 AI 一个精确到分钟的开场白素材
            if (Configuration.QuerySource == "qweather" && Configuration.EnableRainNowcast && data.Now.PrecipMm <= 0.05)
            {
                try
                {
                    var mi = await QWeather.GetMinutelyAsync(c, DestroyCancellationToken, CacheHub.QueryTtl);
                    var first = mi is { Supported: true } ? mi.Items.FirstOrDefault(p => p.Precip > 0) : null;
                    if (first != null)
                    {
                        int mins = Math.Max(1, (int)Math.Round((first.FxTime - DateTime.Now).TotalMinutes));
                        text += $"\n预计 {mins} 分钟后开始{(first.Type == "snow" ? "下雪" : "下雨")}。";
                    }
                }
                catch { /* 分钟级缺失不影响主回答 */ }
            }
            interactor.Poke(text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { interactor.Poke($"天气查询失败：{ex.Message}"); }
    }

    [XmlFunction(FunctionMode.OneShot, name: "query_forecast")]
    [Description("查询未来多日天气预报（含昼夜现象/降水概率与雨量/紫外线/日出日落/月相）。和风源最多 10 天，wttr 源 3 天")]
    public async Task QueryForecast(
        [Description("城市名，可省略")] string? city = null,
        [Description("天数 1-10，默认 3")] int days = 3)
    {
        string c = CityOrDefault(city);
        int max = Configuration.QuerySource == "qweather" ? 10 : 3;
        days = Math.Clamp(days, 1, max);
        try
        {
            var data = await SafeWeatherAsync(c, days);
            if (data == null) { interactor.Poke($"暂时无法获取「{c}」的预报，请稍后再试。"); return; }
            interactor.Poke(WeatherTexts.FormatForecast(data, days));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { interactor.Poke($"预报查询失败：{ex.Message}"); }
    }

    [XmlFunction(FunctionMode.OneShot, name: "query_hourly")]
    [Description("查询逐小时预报（最多 240 小时，适合回答「明天下午几点下雨」「今晚几点降温」）。和风源专用，wttr 源降级为今日时段")]
    public async Task QueryHourly(
        [Description("城市名，可省略")] string? city = null,
        [Description("小时数 1-240，默认 72")] int hours = 72)
    {
        string c = CityOrDefault(city);
        try
        {
            if (Configuration.QuerySource != "qweather")
            {
                var data = await SafeWeatherAsync(c, 1);
                var hs = data?.Days.FirstOrDefault()?.Hourly;
                if (hs is not { Count: > 0 })
                { interactor.Poke($"暂时无法获取「{c}」的逐小时预报。"); return; }
                interactor.Poke(WeatherTexts.FormatHourly(c,
                    hs.Where(h => h.Time >= DateTime.Now.Hour).Take(Math.Clamp(hours, 1, 24)).ToList()));
                return;
            }
            var pts = await QWeather.GetHourlyFlatAsync(c, hours, DestroyCancellationToken);
            if (pts == null || pts.Count == 0) { interactor.Poke($"暂时无法获取「{c}」的逐小时预报，请稍后再试。"); return; }
            interactor.Poke(WeatherTexts.FormatHourly(c, pts));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { interactor.Poke($"逐小时预报查询失败：{ex.Message}"); }
    }

    [XmlFunction(FunctionMode.OneShot, name: "query_rain")]
    [Description("查询未来两小时分钟级降水（约几分钟开始下雨/雪、下多久）。仅中国城市支持")]
    public async Task QueryRain([Description("城市名，可省略")] string? city = null)
    {
        string c = CityOrDefault(city);
        try
        {
            if (Configuration.QuerySource != "qweather")
            { interactor.Poke("当前查询源为 wttr，不支持分钟级降水；切换到和风天气源后可用。"); return; }
            var mi = await QWeather.GetMinutelyAsync(c, DestroyCancellationToken, CacheHub.QueryTtl);
            if (mi == null) { interactor.Poke($"暂时无法获取「{c}」的分钟级降水数据，请稍后再试。"); return; }
            interactor.Poke(WeatherTexts.FormatRain(mi, c));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { interactor.Poke($"分钟级降水查询失败：{ex.Message}"); }
    }

    [XmlFunction(FunctionMode.OneShot, name: "query_air")]
    [Description("查询空气质量详情（AQI/类别/首要污染物/各项浓度/健康建议）。和风源专用")]
    public async Task QueryAir([Description("城市名，可省略")] string? city = null)
    {
        string c = CityOrDefault(city);
        try
        {
            if (Configuration.QuerySource != "qweather")
            { interactor.Poke("当前查询源为 wttr，不支持空气质量查询；切换到和风天气源后可用。"); return; }
            var air = await QWeather.GetAirAsync(c, DestroyCancellationToken, CacheHub.QueryTtl);
            if (air == null || air.Aqi < 0) { interactor.Poke($"暂时无法获取「{c}」的空气质量数据，请稍后再试。"); return; }
            interactor.Poke(WeatherTexts.FormatAir(air, c));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { interactor.Poke($"空气质量查询失败：{ex.Message}"); }
    }

    [XmlFunction(FunctionMode.OneShot, name: "query_indices")]
    [Description("查询生活指数（穿衣/洗车/运动/紫外线/舒适度等，含级别与建议原文）。和风源专用")]
    public async Task QueryIndices(
        [Description("城市名，可省略")] string? city = null,
        [Description("指数类型编号，逗号分隔：1=运动 2=洗车 3=穿衣 5=紫外线 8=舒适度；可省略")] string? type = null,
        [Description("天数 1-3，默认 1")] int days = 1)
    {
        string c = CityOrDefault(city);
        try
        {
            if (Configuration.QuerySource != "qweather")
            { interactor.Poke("当前查询源为 wttr，不支持生活指数；切换到和风天气源后可用。"); return; }
            var items = await QWeather.GetIndicesAsync(c, string.IsNullOrWhiteSpace(type) ? "1,2,3,8" : type.Trim(), days, DestroyCancellationToken);
            if (items == null || items.Count == 0) { interactor.Poke($"暂时无法获取「{c}」的生活指数，请稍后再试。"); return; }
            interactor.Poke(WeatherTexts.FormatIndices(items, c));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { interactor.Poke($"生活指数查询失败：{ex.Message}"); }
    }

    [XmlFunction(FunctionMode.OneShot, name: "query_astro")]
    [Description("查询日出日落/天亮天黑/月出月落/月相。默认 3 天，超过 3 天的部分每多一天多 2 次数据请求")]
    public async Task QueryAstro(
        [Description("城市名，可省略")] string? city = null,
        [Description("天数 1-10，默认 3")] int days = 3)
    {
        string c = CityOrDefault(city);
        days = Math.Clamp(days, 1, 10);
        try
        {
            var list = new List<AstroDay>();
            var data = await SafeWeatherAsync(c, Math.Min(days, 3));
            if (data != null)
                foreach (var d in data.Days.Take(3))
                    list.Add(new AstroDay
                    {
                        Date = d.Date, Sunrise = d.Sunrise, Sunset = d.Sunset,
                        CivilDawn = d.CivilDawn, CivilDusk = d.CivilDusk,
                        Moonrise = d.Moonrise, Moonset = d.Moonset, MoonPhase = d.MoonPhase,
                    });
            if (Configuration.QuerySource == "qweather")
                for (int i = 3; i < days; i++)
                {
                    var ad = await QWeather.GetAstroAsync(c, DateTime.Today.AddDays(i), DestroyCancellationToken);
                    if (ad != null) list.Add(ad);
                }
            if (list.Count == 0) { interactor.Poke($"暂时无法获取「{c}」的天文数据，请稍后再试。"); return; }
            interactor.Poke(WeatherTexts.FormatAstro(list.OrderBy(a => a.Date).ToList(), c));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { interactor.Poke($"天文数据查询失败：{ex.Message}"); }
    }

    [XmlFunction(FunctionMode.OneShot, name: "query_history")]
    [Description("查询过去 N 天的历史天气回顾（逐日温度/降水/湿度/现象）。和风源专用，最多 10 天；每多一天多 1 次数据请求")]
    public async Task QueryHistory(
        [Description("城市名，可省略")] string? city = null,
        [Description("回看天数 1-10，默认 3")] int days = 3)
    {
        string c = CityOrDefault(city);
        try
        {
            if (Configuration.QuerySource != "qweather")
            { interactor.Poke("当前查询源为 wttr，不支持历史天气查询；切换到和风天气源后可用。"); return; }
            days = Math.Clamp(days, 1, 10);
            var list = new List<HistoryDay>();
            for (int i = 1; i <= days; i++)
            {
                var h = await QWeather.GetHistoryAsync(c, DateTime.Today.AddDays(-i), DestroyCancellationToken);
                if (h != null && h.TempMax > -900) list.Add(h);
            }
            if (list.Count == 0) { interactor.Poke($"暂时无法获取「{c}」的历史天气，请稍后再试。"); return; }
            interactor.Poke(WeatherTexts.FormatHistory(list, c));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { interactor.Poke($"历史天气查询失败：{ex.Message}"); }
    }

    [XmlFunction(FunctionMode.OneShot, name: "query_warning")]
    [Description("查询当前生效的官方灾害预警（含正文与防御指南）")]
    public async Task QueryWarning([Description("城市名，可省略")] string? city = null)
    {
        string c = CityOrDefault(city);
        try
        {
            var alarms = (await Monitor.GetAlarmsForCityAsync(c, DestroyCancellationToken))
                .Where(a => a.EndTime == null || a.EndTime > DateTime.Now).ToList();
            if (Configuration.WarningSource == "nmc")
                await Nmc.EnrichDetailsAsync(alarms, DestroyCancellationToken);
            interactor.Poke(WeatherTexts.FormatWarnings(c, alarms));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { interactor.Poke($"预警查询失败：{ex.Message}"); }
    }

    [XmlFunction(FunctionMode.OneShot, name: "set_default_city")]
    [Description("修改默认天气城市（用户搬家或想长期关注另一城市时调用），自动纳入监控")]
    public async Task SetDefaultCity([Description("新的默认城市名")] string city)
    {
        city = city?.Trim() ?? "";
        if (city.Length == 0) { interactor.Poke("城市名不能为空。"); return; }
        var probe = await SafeWeatherAsync(city);
        if (probe == null) { interactor.Poke($"无法识别城市「{city}」（数据源查无此地或暂时不可用），默认城市未修改。"); return; }

        Configuration.DefaultCity = city;
        SaveConfig();   // 默认城市天然在监控范围内（MonitoredCities 取 DefaultCity ∪ WatchCities），不再追加 WatchCities
        try { await _monitor.ResetCityBaselineAsync(city, DestroyCancellationToken); }
        catch (Exception ex) { logger.LogWarning(ex, "换城基线记录失败"); }
        try { await _monitor.TryInjectContextLineAsync(DestroyCancellationToken); }   // 换城后立即刷新常驻天气行为新城
        catch (Exception ex) { logger.LogWarning(ex, "换城上下文刷新失败"); }
        interactor.Poke($"默认城市已切换为「{city}」并纳入天气监控，之后的天气查询与预警推送都会以它为准。");
    }

    #endregion

    #region 供 UI 与 Monitor 使用

    public WeatherMonitor Monitor => _monitor;

    async Task<WeatherData?> SafeWeatherAsync(string city, int forecastDays = 3)
    {
        try { return await _monitor.GetWeatherAsync(city, DestroyCancellationToken, forecastDays); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "天气数据获取失败：{City}", city); return null; }
    }

    void SaveConfig()
    {
        try { configurationSystem.SetConfiguration(typeof(WeatherModule), Configuration, Character?.StorageKey ?? ""); }
        catch (Exception ex) { logger.LogWarning(ex, "天气配置保存失败"); }
    }

    /// <summary>规则 UI 保存：替换配置并落盘（LanguageModelRouter 同款模式）。</summary>
    internal void ApplyConfigAndSave(WeatherConfig config)
    {
        Configuration = config;
        _qweather = null;   // Key/Host 可能已变，强制下次访问重建
        SaveConfig();
    }

    internal void ResetRulesToDefault()
    {
        Configuration.Rules = DefaultRules.Create();
        Configuration.RulesSeeded = true;
        SaveConfig();
    }

    /// <summary>规则 UI 模拟测试。</summary>
    internal Task<string> TestRuleAsync(WeatherRule rule) => _monitor.TestRuleAsync(rule, DestroyCancellationToken);

    #endregion
}
