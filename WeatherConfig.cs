using System.Collections.Generic;
using System.ComponentModel;

namespace chuxin.Weather;

/// <summary>配置 POCO（DESIGN.md §6.5）。规则集 Rules 存在配置里，角色级配置优先。</summary>
public class WeatherConfig
{
    [DisplayName("查询数据源")]
    [Description("wttr = 免费无 Key（默认）；qweather = 和风天气（需下方 Key）")]
    public string QuerySource { get; set; } = "wttr";

    [DisplayName("预警数据源")]
    [Description("nmc = 中央气象台（默认）；qweather = 和风天气；off = 关闭预警监控")]
    public string WarningSource { get; set; } = "nmc";

    [DisplayName("和风天气 API Key")]
    [Description("切换到 qweather 源时填写（和风控制台获取）")]
    public string QWeatherApiKey { get; set; } = "";

    [DisplayName("和风天气 API Host")]
    [Description("新版账号到控制台复制专属 API Host（形如 xxxxxxxx.re.qweatherapi.com）；旧版账号用 devapi.qweather.com（免费）/ api.qweather.com（付费）")]
    public string QWeatherApiHost { get; set; } = "https://devapi.qweather.com";

    [DisplayName("默认城市")]
    [Description("查询缺省城市；AI 也可通过 set_default_city 修改")]
    public string DefaultCity { get; set; } = "北京";

    [DisplayName("监控城市列表")]
    [Description("逗号分隔；留空仅监控默认城市。预警与变化检测的监控范围 = 默认城市 + 此列表")]
    public string WatchCities { get; set; } = "";

    [DisplayName("启用预警监控")]
    [Description("关闭后不推送官方灾害预警")]
    public bool EnableWarningMonitor { get; set; } = true;

    [DisplayName("预警轮询间隔(分钟)")]
    [Description("下限 5；nmc 源每轮仅 1 次请求")]
    public int PollIntervalMinutes { get; set; } = 10;

    [DisplayName("启用变化检测")]
    [Description("关闭后不评价规则、不注入常驻天气上下文")]
    public bool EnableChangeMonitor { get; set; } = true;

    [DisplayName("变化检测间隔(分钟)")]
    [Description("下限 30；规则评价频率与此相同（wttr 数据约小时级更新，更快无意义）")]
    public int ChangeCheckIntervalMinutes { get; set; } = 60;

    [DisplayName("启用每日晨报")]
    [Description("每天定时推送一次天气汇总，独立于变化检测开关")]
    public bool DailyBriefEnabled { get; set; } = false;

    [DisplayName("晨报时间")]
    [Description("格式 HH:mm")]
    public string DailyBriefTime { get; set; } = "07:30";

    [DisplayName("启动时播报已生效预警")]
    [Description("默认关闭：启动时已生效的旧预警只记录不播报")]
    public bool AnnounceOnStart { get; set; } = false;

    [DisplayName("预警解除提醒")]
    public bool NotifyOnClear { get; set; } = true;

    [DisplayName("静默时段")]
    [Description("如 23:00-07:00（可跨零点），时段内推送只记录、结束后补报；留空关闭")]
    public string QuietHours { get; set; } = "23:00-07:00";

    [DisplayName("推送规则")]
    [Description("内置默认规则 + 自定义规则（建议在规则 UI 中编辑）")]
    public List<WeatherRule> Rules { get; set; } = [];

    [DisplayName("规则已初始化")]
    [Description("内部字段：默认规则集是否已预置")]
    public bool RulesSeeded { get; set; }
}
