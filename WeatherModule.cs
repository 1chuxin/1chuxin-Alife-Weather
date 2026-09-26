using System;
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
/// 天气服务模块：3 个查询函数 + set_default_city；三级分发推送子系统（DESIGN.md）。
/// 零配置可用（wttr 查询 + 中央气象台预警），数据源可切换到和风天气。
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
    public QWeatherClient QWeather => _qweather ??= new(_http, Configuration.QWeatherApiHost, Configuration.QWeatherApiKey);
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
            Description = "查询天气、天气预警，并可在用户搬家或想切换关注城市时修改默认城市。",
            Explanation = """
                天气服务使用说明
                - <query_weather city="城市"/> 实时天气（city 可省略 = 默认城市；支持国内外任意城市）
                - <query_forecast city="城市" days="3"/> 多日预报（wttr 源最多 3 天）
                - <query_warning city="城市"/> 当前生效的官方灾害预警（覆盖中国大陆城市）
                - <set_default_city city="城市"/> 用户搬家或想长期关注另一城市时调用，切换后天气查询与推送都跟随新城市

                何时用：用户问天气/出行/穿衣、提到天气变化、或天气推送后想看详情时。
                插件会在强天气变化和官方预警发布时主动提醒你（Poke），平时会静默更新你上下文中的当前天气行。
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
            _nextWarning = DateTime.Now.AddMinutes(Math.Max(5, Configuration.PollIntervalMinutes));
            _nextChange = DateTime.Now.AddMinutes(2); // 首次变化检测：启动后 2 分钟，种子当日基线
            _started = true;
        }
        catch (Exception ex) { logger.LogWarning(ex, "天气模块启动初始化失败（不影响查询功能）"); }
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
                        _nextBrief = now.Date.AddDays(1);
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

    [XmlFunction(FunctionMode.OneShot, name: "query_weather")]
    [Description("查询实时天气。city 可省略（用默认城市），支持国内外任意城市")]
    public async Task QueryWeather([Description("城市名，如：北京 / 上海 / Tokyo，可省略")] string? city = null)
    {
        string c = city?.Trim().Length > 0 ? city.Trim() : Configuration.DefaultCity;
        try
        {
            var data = await SafeWeatherAsync(c);
            if (data == null) { interactor.Poke($"暂时无法获取「{c}」的天气（数据源不可用或城市名无法识别），请稍后再试或换个写法。"); return; }
            int warnCount = (await Monitor.GetAlarmsForCityAsync(c, DestroyCancellationToken)).Count;
            interactor.Poke(WeatherTexts.FormatCurrent(data, warnCount));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { interactor.Poke($"天气查询失败：{ex.Message}"); }
    }

    [XmlFunction(FunctionMode.OneShot, name: "query_forecast")]
    [Description("查询未来多日天气预报。city 可省略；wttr 源最多 3 天，和风源最多 7 天")]
    public async Task QueryForecast(
        [Description("城市名，可省略")] string? city = null,
        [Description("天数 1-7，默认 3")] int days = 3)
    {
        string c = city?.Trim().Length > 0 ? city.Trim() : Configuration.DefaultCity;
        int max = Configuration.QuerySource == "qweather" ? 7 : 3;
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

    [XmlFunction(FunctionMode.OneShot, name: "query_warning")]
    [Description("查询当前生效的官方灾害预警（含正文，覆盖中国大陆城市）")]
    public async Task QueryWarning([Description("城市名，可省略")] string? city = null)
    {
        string c = city?.Trim().Length > 0 ? city.Trim() : Configuration.DefaultCity;
        try
        {
            var alarms = await Monitor.GetAlarmsForCityAsync(c, DestroyCancellationToken);
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
