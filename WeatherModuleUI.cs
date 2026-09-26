using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Foundation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace chuxin.Weather;

/// <summary>
/// 观云台 UI（纯 C# 组件，无 razor）。
/// 三段式：开场动画（夜航→破晓→铭牌，点按可跳过）→ 门面（主视觉 + 状态轨 + 门闩）→ 深色控制台（四分区）。
/// 手写 RenderTreeBuilder + value/checked 原生控件 + EventCallback.Factory.Create
/// （不用 @bind / @onclick 语法糖，本宿主中实测会卡死渲染）。
/// 素材经 LoadAsset 转 data-URI 内嵌（Plugins/chuxin.Weather/Assets），缺失时按 CSS 兜底渐变降级。
/// 改动直接写基类提供的 Configuration 活对象，落盘由框架「保存配置」按钮负责。
/// </summary>
public class WeatherModuleUI : ModuleUIBase<WeatherModule, WeatherConfig>
{
    [Inject]
    IJSRuntime JS { get; set; } = default!;

    // ── 开场状态机：0=播放中 1=淡出中 2=已移除 ──
    int _bootPhase;
    bool _bootTimerStarted;
    bool _intro = true;          // wx-root.intro：入场时间轴（门闩点开后移除）
    bool _configOpen;
    int _activeSection;

    // ── 规则编辑 / 测试 ──
    WeatherRule? _editing;
    WeatherRule? _backup;        // 打开编辑器时的快照，取消时还原
    WeatherRule? _testing;
    string _testResult = "";
    bool _testOk;

    // ── 素材缓存（data-URI） ──
    string? _heroUri, _railUri, _dawnUri, _nightUri, _cloudsUri, _sunUri, _orbUri, _raysUri;

    // ── 开场装饰元素（固定种子，重渲染位置稳定） ──
    readonly (double l, double t, double s, double d)[] _stars;
    readonly (double l, double s, double dur, double d)[] _motes;
    static readonly (string l, string t, string d)[] Meteors = { ("70%", "14%", ".45s"), ("52%", "24%", "1.35s") };

    const int BootMs = 2750;   // 与 CSS --boot 对齐：夜航 → 日轮升起 → 铭牌
    const int FadeMs = 600;

    static readonly (string ico, string label)[] NavItems =
    {
        ("◎", "总控"), ("☁", "数据源"), ("⚡", "提醒规则"), ("◑", "晨报免打扰"),
    };

    public WeatherModuleUI()
    {
        var rng = new Random(20260926);
        _stars = Enumerable.Range(0, 8).Select(_ =>
            (rng.NextDouble() * 94 + 2, rng.NextDouble() * 55 + 3,
             Math.Round(rng.NextDouble() * 1.8 + 1, 1), Math.Round(rng.NextDouble() * 1.4, 2))).ToArray();
        _motes = Enumerable.Range(0, 9).Select(_ =>
            (Math.Round(rng.NextDouble() * 94 + 2, 1), Math.Round(rng.NextDouble() * 3 + 3, 1),
             Math.Round(rng.NextDouble() * 4 + 5, 1), Math.Round(rng.NextDouble() * 3, 1))).ToArray();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_bootPhase != 0 || _bootTimerStarted)
            return;
        _bootTimerStarted = true;
        try
        {
            bool reduced = false;
            try { reduced = await JS.InvokeAsync<bool>("eval", "matchMedia('(prefers-reduced-motion: reduce)').matches"); }
            catch { reduced = false; }
            if (reduced)
            {
                _bootPhase = 2;
                _intro = false;
                StateHasChanged();
                return;
            }

            await Task.Delay(BootMs);
            if (_bootPhase != 0) return;         // 用户已点按跳过
            _bootPhase = 1;                       // .fade：透明过渡
            StateHasChanged();

            await Task.Delay(FadeMs);
            if (_bootPhase != 1) return;
            _bootPhase = 2;                       // 从渲染树移除
            StateHasChanged();
        }
        catch (Exception)
        {
            // 组件可能已被销毁；保持静默
        }
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.OpenElement(0, "p");
            b.AddContent(1, "配置未就绪，请关闭后重新打开。");
            b.CloseElement();
            return;
        }

        int i = 0;
        b.OpenElement(i++, "style");
        b.AddContent(i++, Css);
        b.CloseElement();

        string cls = "wx-root";
        if (_intro && _bootPhase < 2) cls += " intro";
        if (_configOpen) cls += " config-open";

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", cls);

        if (_bootPhase < 2) RenderBoot(b, ref i);
        RenderFacade(b, ref i);
        RenderConsole(b, ref i);

        b.CloseElement(); // wx-root
    }

    // ═══════════════════════ 开场动画 ═══════════════════════

    void RenderBoot(RenderTreeBuilder b, ref int i)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", _bootPhase == 1 ? "wx-boot fade" : "wx-boot");
        b.AddAttribute(i++, "aria-hidden", "true");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => SkipBoot()));

        AddImg(b, ref i, "wx-boot-dawnimg", _dawnUri ??= LoadAsset("boot-dawn.webp"));
        AddImg(b, ref i, "wx-boot-nightimg", _nightUri ??= LoadAsset("boot-night.webp"));

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-boot-stars");
        foreach (var (l, t, s, d) in _stars)
        {
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "style", $"left:{l:0.#}%;top:{t:0.#}%;width:{s}px;height:{s}px;animation-delay:{d:0.##}s");
            b.CloseElement();
        }
        b.CloseElement();

        AddImg(b, ref i, "boot-rays", _raysUri ??= LoadAsset("boot-rays.webp"));
        AddImg(b, ref i, "wx-boot-sun", _sunUri ??= LoadAsset("boot-sun.webp"));
        AddImg(b, ref i, "boot-orb", _orbUri ??= LoadAsset("boot-orb.webp"));

        AddImg(b, ref i, "wx-boot-cloud b-cl3", _cloudsUri ??= LoadAsset("boot-clouds.webp"));
        AddImg(b, ref i, "wx-boot-cloud b-cl1", _cloudsUri);
        AddImg(b, ref i, "wx-boot-cloud b-cl2", _cloudsUri);

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "boot-brand");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "boot-brand-title");
        b.AddContent(i++, "观 云 台");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "boot-brand-line");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "boot-brand-sub");
        b.AddContent(i++, "CHUXIN WEATHER · 先知风雨");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "boot-progress");
        b.OpenElement(i++, "span");
        b.CloseElement();
        b.CloseElement();
        b.CloseElement(); // boot-brand

        foreach (var (l, t, d) in Meteors)
        {
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "boot-shoot");
            b.AddAttribute(i++, "style", $"left:{l};top:{t};animation-delay:{d}");
            b.CloseElement();
        }
        foreach (var (l, s, dur, d) in _motes)
        {
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "boot-mote");
            b.AddAttribute(i++, "style", $"width:{s}px;height:{s}px;left:{l}%;bottom:-4%;animation-duration:{dur:0.#}s;animation-delay:{d:0.#}s");
            b.CloseElement();
        }

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-boot-tip");
        b.AddContent(i++, "正 在 同 步 天 气 数 据");
        b.CloseElement();

        b.CloseElement(); // wx-boot
    }

    Task SkipBoot()
    {
        if (_bootPhase >= 2) return Task.CompletedTask;
        _bootPhase = 2;
        _intro = false;      // 跳过时门面直接呈现（与概念稿 skipAll 行为一致）
        StateHasChanged();
        return Task.CompletedTask;
    }

    // ═══════════════════════ 门面：主视觉 + 状态轨 + 门闩 ═══════════════════════

    void RenderFacade(RenderTreeBuilder b, ref int i)
    {
        // 主视觉
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-photo-frame");
        AddImg(b, ref i, "wx-photo", _heroUri ??= LoadAsset("hero2.webp"), "破晓云海上的观星台，金光穿云");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-vignette");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-mist wx-mist-a");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-mist wx-mist-b");
        b.CloseElement();
        AddImg(b, ref i, "wx-front-cloud", _cloudsUri ??= LoadAsset("boot-clouds.webp"));
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-caption");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-kicker");
        b.AddContent(i++, "CHUXIN WEATHER");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-title");
        b.AddContent(i++, "观云台");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-sub");
        b.AddContent(i++, "云上观澜 · 先知风雨");
        b.CloseElement();
        b.CloseElement(); // caption
        b.CloseElement(); // photo-frame

        RenderRail(b, ref i);

        // 门闩
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-gate");
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "wx-gate-btn");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, OpenConfigAsync));
        b.AddContent(i++, "⊙ 打开配置");
        b.CloseElement();
        b.CloseElement(); // gate
    }

    async Task OpenConfigAsync()
    {
        if (_bootPhase < 2) _bootPhase = 2;   // 保险：正常情况下门闩被开场层遮住
        _intro = false;
        _configOpen = true;
        StateHasChanged();
        try { await JS.InvokeVoidAsync("scrollTo", 0, 0); }
        catch { }
    }

    void RenderRail(RenderTreeBuilder b, ref int i)
    {
        var st = GetRail();
        bool warningOn = Configuration.EnableWarningMonitor && Configuration.WarningSource != "off";
        bool monitorOn = warningOn || Configuration.EnableChangeMonitor;
        bool qwReady = Configuration.QWeatherApiKey.Trim().Length > 0;

        b.OpenElement(i++, "aside");
        b.AddAttribute(i++, "class", "wx-rail");
        b.AddAttribute(i++, "aria-label", "天气服务状态");

        AddImg(b, ref i, "wx-rail-bg", _railUri ??= LoadAsset("rail.webp"));
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rail-shade");
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rail-content");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rail-kicker");
        b.AddContent(i++, "STATION STATUS");
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rail-title");
        b.AddContent(i++, st.City);
        b.OpenElement(i++, "small");
        b.AddContent(i++, st.HasNow ? $"{st.Temp:0.#}°C {st.Desc}" : "等待首轮检测");
        b.CloseElement();
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rail-online");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "wx-signal");
        b.CloseElement();
        b.AddContent(i++, monitorOn ? "监测运行中" : "监测已暂停");
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rail-nodes");

        AddRailNode(b, ref i, EmojiOf(st.Code),
            $"实况 · {(Configuration.QuerySource == "qweather" ? "和风天气" : "wttr.in")}",
            st.HasNow ? $"{st.Desc} {st.Temp:0.#}°C" : "启动后首轮检测生成",
            true, Configuration.EnableChangeMonitor ? "LIVE" : "PAUSE");

        string warnSrc = Configuration.WarningSource switch
        {
            "qweather" => "和风天气",
            "off" => "已关闭",
            _ => "中央气象台"
        };
        bool warnOff = !warningOn;
        AddRailNode(b, ref i, "⚠️", $"预警 · {warnSrc}",
            Configuration.WarningSource == "off" ? "预警监控已关闭"
                : st.LastWarningCheck is { Length: >= 16 } w ? $"上次检查 {w.Substring(11, 5)}" : "等待首轮检查",
            !warnOff, warnOff ? "OFF" : "LIVE");

        AddRailNode(b, ref i, "⚡", $"变化盯梢 · {st.RuleTotal} 条规则",
            st.RuleEnabled > 0 ? $"{st.RuleEnabled} 条启用中" : "暂无启用规则",
            Configuration.EnableChangeMonitor && st.RuleEnabled > 0,
            Configuration.EnableChangeMonitor ? "LIVE" : "PAUSE");

        AddRailNode(b, ref i, "🔭", "和风天气 · 备用",
            qwReady ? "已配置，可切换为查询 / 预警源" : "未配置（填 Key 后可用）",
            qwReady, qwReady ? "READY" : "IDLE");

        b.CloseElement(); // wx-rail-nodes

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rail-metrics");
        AddRailMetric(b, ref i, "预警检查", $"{Configuration.PollIntervalMinutes} 分钟");
        AddRailMetric(b, ref i, "变化检查", $"{Configuration.ChangeCheckIntervalMinutes} 分钟");
        AddRailMetric(b, ref i, "免打扰",
            string.IsNullOrWhiteSpace(Configuration.QuietHours) ? "关闭" : CompactHours(Configuration.QuietHours));
        b.CloseElement();

        b.CloseElement(); // wx-rail-content
        b.CloseElement(); // wx-rail
    }

    RailStatus GetRail()
    {
        try
        {
            var monitor = Module?.Monitor;
            if (monitor != null) return monitor.GetRailStatus();
        }
        catch { }
        return new RailStatus(Configuration.DefaultCity, false, "", 0, -1, null, null,
            Configuration.Rules.Count, Configuration.Rules.Count(r => r.Enabled));
    }

    void AddRailNode(RenderTreeBuilder b, ref int i, string ico, string name, string meta, bool on, string state)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", on ? "wx-rail-node on" : "wx-rail-node off");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "wx-rail-node-ico");
        b.AddContent(i++, ico);
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "wx-rail-node-copy");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "wx-rail-node-name");
        b.AddContent(i++, name);
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "wx-rail-node-meta");
        b.AddContent(i++, meta);
        b.CloseElement();
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "wx-rail-node-state");
        b.AddContent(i++, state);
        b.CloseElement();
        b.CloseElement();
    }

    void AddRailMetric(RenderTreeBuilder b, ref int i, string label, string value)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rail-metric");
        b.OpenElement(i++, "span");
        b.AddContent(i++, label);
        b.CloseElement();
        b.OpenElement(i++, "strong");
        b.AddContent(i++, value);
        b.CloseElement();
        b.CloseElement();
    }

    // ═══════════════════════ 控制台 ═══════════════════════

    void RenderConsole(RenderTreeBuilder b, ref int i)
    {
        bool warningOn = Configuration.EnableWarningMonitor && Configuration.WarningSource != "off";
        bool monitorOn = warningOn || Configuration.EnableChangeMonitor;

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-console");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-console-head");
        b.OpenElement(i++, "div");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-console-kicker");
        b.AddContent(i++, "WEATHER CONSOLE");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-console-title");
        b.AddContent(i++, "观云控制台");
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-console-sub");
        b.AddContent(i++, "天气剧变会主动提醒你；细微变化悄悄记在当前天气里");
        b.CloseElement();
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-console-meter");
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "wx-signal");
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddContent(i++, monitorOn ? "监测中" : "已暂停");
        b.CloseElement();
        b.CloseElement();
        b.CloseElement(); // wx-console-head

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-shell");

        // 侧栏
        b.OpenElement(i++, "nav");
        b.AddAttribute(i++, "class", "wx-nav");
        for (int n = 0; n < NavItems.Length; n++)
        {
            int idx = n;
            b.OpenElement(i++, "button");
            b.AddAttribute(i++, "type", "button");
            b.AddAttribute(i++, "class", _activeSection == n ? "wx-nav-btn active" : "wx-nav-btn");
            b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this,
                () => { _activeSection = idx; StateHasChanged(); }));
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "wx-nav-ico");
            b.AddContent(i++, NavItems[n].ico);
            b.CloseElement();
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "wx-nav-copy");
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "wx-nav-label");
            b.AddContent(i++, NavItems[n].label);
            b.CloseElement();
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "wx-nav-meta");
            b.AddContent(i++, NavMeta(n));
            b.CloseElement();
            b.CloseElement();
            b.CloseElement();
        }
        b.CloseElement(); // wx-nav

        // 工作区：只渲染当前分区，切换即重建 → wx-panel-in 动画自动重播
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-workspace");
        b.OpenElement(i++, "section");
        b.AddAttribute(i++, "class", "wx-sec");
        switch (_activeSection)
        {
            case 0: RenderSectionMain(b, ref i); break;
            case 1: RenderSectionSources(b, ref i); break;
            case 2: RenderSectionRules(b, ref i); break;
            default: RenderSectionBrief(b, ref i); break;
        }
        b.CloseElement(); // section
        b.CloseElement(); // wx-workspace

        b.CloseElement(); // wx-shell
        b.CloseElement(); // wx-console
    }

    string NavMeta(int n) => n switch
    {
        0 => "城市 · 数据源 · 开关",
        1 => "和风凭据 · 检查频率",
        2 => $"{Configuration.Rules.Count} 条规则 · 可自定义",
        _ => "定时 · 静默时段"
    };

    /// <summary>打开一个 wx-panel（头部 + 描述），面板保持开放，由调用方收尾 CloseElement。</summary>
    void AddPanelOpen(RenderTreeBuilder b, ref int i, string accent, string idx, string ico, string title, string desc)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-panel");
        b.AddAttribute(i++, "style", $"--a:{accent}");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-panel-head");
        b.AddAttribute(i++, "data-idx", idx);
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "wx-panel-ico");
        b.AddContent(i++, ico);
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "wx-panel-title");
        b.AddContent(i++, title);
        b.CloseElement();
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-panel-desc");
        b.AddContent(i++, desc);
        b.CloseElement();
    }

    void RenderSectionMain(RenderTreeBuilder b, ref int i)
    {
        AddPanelOpen(b, ref i, "#38bdf8", "01", "◎", "总控",
            "改动立即生效；点上方「保存配置」长期保存。");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-grid2");
        AddTextInput(b, ref i, "默认城市", Configuration.DefaultCity,
            "搬家 / 换城市时，也可以直接让 AI 改", null, v => Configuration.DefaultCity = v);
        AddSelect(b, ref i, "查询数据源", Configuration.QuerySource,
            "默认源完全免费；和风更稳定，但需要自己申请 Key",
            new[] { ("wttr", "wttr.in（免费，无需申请）"), ("qweather", "和风天气（需要 Key）") },
            v => Configuration.QuerySource = v);
        b.CloseElement(); // wx-grid2
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:grid;grid-template-columns:1fr 1fr;gap:10px 12px;margin-top:10px");
        AddSwitchRow(b, ref i, "预警提醒", "官方发布灾害预警时第一时间说",
            Configuration.EnableWarningMonitor, v => Configuration.EnableWarningMonitor = v);
        AddSwitchRow(b, ref i, "天气变化提醒", "按提醒规则盯紧天气变化",
            Configuration.EnableChangeMonitor, v => Configuration.EnableChangeMonitor = v);
        b.CloseElement();
        b.CloseElement(); // wx-panel
    }

    void RenderSectionSources(RenderTreeBuilder b, ref int i)
    {
        AddPanelOpen(b, ref i, "#818cf8", "02", "☁", "数据源与检查频率",
            "不填 Key 也完全可用；想用和风时填上 Key，再回总控切换即可。");
        AddSelect(b, ref i, "预警数据源", Configuration.WarningSource,
            "中央气象台免费无需申请；选「关闭」则不查预警",
            new[] { ("nmc", "中央气象台（免费）"), ("qweather", "和风天气（需要 Key）"), ("off", "关闭预警监控") },
            v => Configuration.WarningSource = v);
        AddTextInput(b, ref i, "和风 API Key", Configuration.QWeatherApiKey,
            "只存在你自己电脑的配置里，不会随插件发布", "粘贴你的 Key",
            v => Configuration.QWeatherApiKey = v.Trim(), password: true);
        AddTextInput(b, ref i, "和风 API 地址", Configuration.QWeatherApiHost,
            "新版账号在控制台复制专属地址；旧版用 devapi.qweather.com", "从和风控制台复制",
            v => Configuration.QWeatherApiHost = v.Trim());
        AddTextInput(b, ref i, "额外关注的城市", Configuration.WatchCities,
            "多个城市用逗号隔开，例如：北京, 上海；留空 = 只看默认城市", null,
            v => Configuration.WatchCities = v);
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-grid2");
        AddNumInput(b, ref i, "预警多久查一次（分钟）", Configuration.PollIntervalMinutes,
            "下限 5，太勤会被数据源限流",
            v => Configuration.PollIntervalMinutes = Math.Max(5, (int)v));
        AddNumInput(b, ref i, "天气多久看一次（分钟）", Configuration.ChangeCheckIntervalMinutes,
            "下限 30；wttr 数据约一小时才更新一次",
            v => Configuration.ChangeCheckIntervalMinutes = Math.Max(30, (int)v));
        b.CloseElement(); // wx-grid2
        b.CloseElement(); // wx-panel
    }

    void RenderSectionRules(RenderTreeBuilder b, ref int i)
    {
        AddPanelOpen(b, ref i, "#f59e0b", "03", "⚡", "提醒规则",
            "「主动推送」会叫醒 AI 播报；「静默记录」只更新上下文里的当前天气。夜里不推送，攒到早上一起说。");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rules");
        foreach (var r in Configuration.Rules.ToArray())
            RenderRuleCard(b, ref i, r);
        b.CloseElement(); // wx-rules

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rule-foot");
        AddBtn(b, ref i, "＋ 添加规则", "wx-btn mini", AddRule);
        AddBtn(b, ref i, "恢复默认", "wx-btn mini ghost", ResetRules);
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "spacer");
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "wx-foot-note");
        b.AddContent(i++, "改动立即生效");
        b.CloseElement();
        b.CloseElement(); // wx-rule-foot

        if (_editing != null)
            RenderEditor(b, ref i, _editing);

        if (_testResult.Length > 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", _testOk ? "wx-testbox ok" : "wx-testbox");
            b.AddContent(i++, _testResult);
            b.CloseElement();
        }

        b.CloseElement(); // wx-panel
    }

    void RenderRuleCard(RenderTreeBuilder b, ref int i, WeatherRule r)
    {
        bool on = r.Enabled;
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", on ? "wx-rule" : "wx-rule disabled");
        b.AddAttribute(i++, "style", r.Level == "push" ? "--a:#f59e0b" : "--a:#60a5fa");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rule-name");
        b.OpenElement(i++, "em");
        b.AddContent(i++, r.Name);
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", r.IsBuiltin ? "wx-badge builtin" : "wx-badge custom");
        b.AddContent(i++, r.IsBuiltin ? "内置" : "自定义");
        b.CloseElement();
        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", r.Level == "push" ? "wx-badge push" : "wx-badge silent");
        b.AddContent(i++, r.Level == "push" ? "主动推送" : "静默记录");
        b.CloseElement();
        b.CloseElement(); // wx-rule-name

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rule-ops");
        AddLed(b, ref i, on, r);
        AddBtn(b, ref i, "编辑", "wx-btn mini", () => BeginEdit(r));
        AddBtnAsync(b, ref i, "wx-btn mini", () => TestRuleCore(r), _testing == r, "试试");
        AddBtn(b, ref i, "删", "wx-btn mini danger", () => DeleteRule(r));
        b.CloseElement(); // wx-rule-ops

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-rule-summary");
        b.AddContent(i++, $"{r.Summary()}{(string.IsNullOrEmpty(r.City) ? "" : $" · 只看{r.City}")} · {r.CooldownHours} 小时不重复");
        b.CloseElement();

        b.CloseElement(); // wx-rule
    }

    void AddLed(RenderTreeBuilder b, ref int i, bool on, WeatherRule r)
    {
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", on ? "wx-led on" : "wx-led");
        b.AddAttribute(i++, "title", "启用 / 停用");
        b.AddAttribute(i++, "aria-label", "启用");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this,
            () => { r.Enabled = !r.Enabled; StateHasChanged(); }));
        b.CloseElement();
    }

    void RenderEditor(RenderTreeBuilder b, ref int i, WeatherRule e)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-editor");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-editor-head");
        b.AddContent(i++, $"✎ 编辑规则 · {e.Name}");
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-grid2");
        AddTextInput(b, ref i, "规则名", e.Name, null, null, v => e.Name = v);
        AddTextInput(b, ref i, "城市", e.City, null, "留空 = 默认城市", v => e.City = v);
        AddSelect(b, ref i, "看什么时候", e.Window, null, WinOpts(), v => e.Window = v);
        AddSelect(b, ref i, "看什么", e.Metric, null, MetricOpts(), v => e.Metric = v);
        AddSelect(b, ref i, "怎么算触发", e.Op, null, OpOpts(), v => e.Op = v);
        AddNumInput(b, ref i, "门槛", e.Threshold, null, v => e.Threshold = v);
        AddNumInput(b, ref i, "提醒间隔（小时）", e.CooldownHours, "这段时间内不重复提醒",
            v => e.CooldownHours = Math.Max(0, (int)v));
        AddSelect(b, ref i, "触发后", e.Level, null,
            new[] { ("push", "主动推送（叫醒 AI）"), ("silent", "静默记录（不打扰）") },
            v => e.Level = v);
        b.CloseElement(); // wx-grid2

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-field");
        b.AddAttribute(i++, "style", "margin-top:10px");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-label");
        b.AddContent(i++, "提醒文案");
        b.CloseElement();
        b.OpenElement(i++, "textarea");
        b.AddAttribute(i++, "rows", 3);
        b.AddAttribute(i++, "class", "wx-tarea");
        b.AddAttribute(i++, "value", e.MessageTemplate);
        b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this,
            ev => e.MessageTemplate = ev.Value?.ToString() ?? ""));
        b.CloseElement();
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-hint");
        b.AddContent(i++, "留空 = 默认文案；可插入：{city} 城市 · {metric} 指标 · {value} 新值 · {prev} 旧值 · {threshold} 门槛 · {time} 时间");
        b.CloseElement();
        b.CloseElement(); // wx-field

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:flex;gap:8px;margin-top:12px");
        AddBtn(b, ref i, "完成", "wx-btn", () =>
        {
            _editing = null;
            _backup = null;
            StateHasChanged();
        });
        AddBtn(b, ref i, "取消", "wx-btn ghost", () =>
        {
            if (_editing != null && _backup != null) CopyRule(_backup, _editing);
            _editing = null;
            _backup = null;
            StateHasChanged();
        });
        b.CloseElement();

        b.CloseElement(); // wx-editor
    }

    void RenderSectionBrief(RenderTreeBuilder b, ref int i)
    {
        AddPanelOpen(b, ref i, "#2dd4bf", "04", "◑", "晨报与免打扰",
            "每天早上播报一次当天天气；夜里不推送，攒到早上一起说。");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-grid2");
        AddTextInput(b, ref i, "晨报时间", Configuration.DailyBriefTime,
            "每天这时播报当天天气（HH:mm）", null, v => Configuration.DailyBriefTime = v);
        AddTextInput(b, ref i, "免打扰时段", Configuration.QuietHours,
            "夜里不打扰，攒到早上一起说；留空关闭", null, v => Configuration.QuietHours = v);
        b.CloseElement(); // wx-grid2
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "style", "display:grid;grid-template-columns:1fr 1fr;gap:10px 12px;margin-top:10px");
        AddSwitchRow(b, ref i, "每日晨报", "每天早上播报一次",
            Configuration.DailyBriefEnabled, v => Configuration.DailyBriefEnabled = v);
        AddSwitchRow(b, ref i, "启动时播报预警", "打开 Alife 时立即提醒已生效的预警",
            Configuration.AnnounceOnStart, v => Configuration.AnnounceOnStart = v);
        AddSwitchRow(b, ref i, "预警解除提醒", "预警解除时说一声",
            Configuration.NotifyOnClear, v => Configuration.NotifyOnClear = v);
        b.CloseElement();
        b.CloseElement(); // wx-panel
    }

    // ═══════════════════════ 规则操作 ═══════════════════════

    void BeginEdit(WeatherRule r)
    {
        _editing = r;
        _backup = CloneRule(r);
        _testResult = "";
        StateHasChanged();
    }

    void DeleteRule(WeatherRule r)
    {
        Configuration.Rules.Remove(r);
        if (_editing == r) { _editing = null; _backup = null; }
        StateHasChanged();
    }

    void AddRule()
    {
        var rule = new WeatherRule
        {
            Name = $"自定义规则{Configuration.Rules.Count(r => !r.IsBuiltin) + 1}",
            Window = "now", Metric = "temp", Op = ">", Threshold = 35,
            CooldownHours = 24, Level = "push"
        };
        Configuration.Rules.Add(rule);
        _editing = rule;
        _backup = CloneRule(rule);
        _testResult = "";
        StateHasChanged();
    }

    void ResetRules()
    {
        Configuration.Rules = DefaultRules.Create();
        _editing = null;
        _backup = null;
        _testResult = "";
        StateHasChanged();
    }

    async Task TestRuleCore(WeatherRule rule)
    {
        if (Module == null)
        {
            _testResult = "模块尚未就绪";
            _testOk = false;
            StateHasChanged();
            return;
        }
        _testing = rule;
        _testResult = "";
        StateHasChanged();
        try
        {
            _testResult = await Module.TestRuleAsync(rule);
            _testOk = _testResult.StartsWith("✔");   // WeatherMonitor.TestRuleAsync 命中时以 ✔ 开头
        }
        catch (Exception ex)
        {
            _testResult = "测试失败：" + ex.Message;
            _testOk = false;
        }
        finally
        {
            _testing = null;
            StateHasChanged();
        }
    }

    static WeatherRule CloneRule(WeatherRule r) => new()
    {
        Name = r.Name, Enabled = r.Enabled, City = r.City, Window = r.Window,
        Metric = r.Metric, Op = r.Op, Threshold = r.Threshold, CooldownHours = r.CooldownHours,
        Level = r.Level, MessageTemplate = r.MessageTemplate, IsBuiltin = r.IsBuiltin
    };

    static void CopyRule(WeatherRule src, WeatherRule dst)
    {
        dst.Name = src.Name; dst.Enabled = src.Enabled; dst.City = src.City;
        dst.Window = src.Window; dst.Metric = src.Metric; dst.Op = src.Op;
        dst.Threshold = src.Threshold; dst.CooldownHours = src.CooldownHours;
        dst.Level = src.Level; dst.MessageTemplate = src.MessageTemplate;
    }

    static (string v, string t)[] WinOpts() => new[]
    {
        ("now", "实况"), ("today", "今天"), ("tomorrow", "明天"),
        ("intraday", "较今早"), ("day_over_day", "较昨天"), ("hourly_today", "今天逐小时")
    };

    static (string v, string t)[] MetricOpts() => new[]
    {
        ("temp", "气温"), ("feels_like", "体感"), ("temp_max", "最高温"), ("temp_min", "最低温"),
        ("humidity", "湿度"), ("wind_speed", "风速"), ("uv", "紫外线"),
        ("precip_mm", "降水量"), ("precip_prob", "降水概率"), ("thunder_prob", "雷暴概率"),
        ("weather_code", "天气现象")
    };

    static (string v, string t)[] OpOpts() => new[]
    {
        (">", "大于"), (">=", "≥"), ("<", "小于"), ("<=", "≤"),
        ("==", "等于"), ("changed", "变化超过（现象选「天气现象」= 突变）")
    };

    // ═══════════════════════ 渲染辅助 ═══════════════════════

    void AddImg(RenderTreeBuilder b, ref int i, string cls, string? uri, string? alt = null)
    {
        if (uri == null) return;
        b.OpenElement(i++, "img");
        b.AddAttribute(i++, "class", cls);
        b.AddAttribute(i++, "src", uri);
        if (alt != null) b.AddAttribute(i++, "alt", alt);
        b.AddAttribute(i++, "draggable", "false");
        b.CloseElement();
    }

    void AddTextInput(RenderTreeBuilder b, ref int i, string label, string value,
        string? hint, string? placeholder, Action<string> setter, bool password = false)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-field");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-label");
        b.AddContent(i++, label);
        b.CloseElement();
        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "type", password ? "password" : "text");
        b.AddAttribute(i++, "class", "wx-in");
        if (placeholder != null) b.AddAttribute(i++, "placeholder", placeholder);
        b.AddAttribute(i++, "value", value);
        b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this,
            ev => setter(ev.Value?.ToString() ?? "")));
        b.CloseElement();
        if (hint != null)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "wx-hint");
            b.AddContent(i++, hint);
            b.CloseElement();
        }
        b.CloseElement(); // wx-field
    }

    void AddNumInput(RenderTreeBuilder b, ref int i, string label, double value, string? hint, Action<double> setter)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-field");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-label");
        b.AddContent(i++, label);
        b.CloseElement();
        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "type", "number");
        b.AddAttribute(i++, "step", "any");
        b.AddAttribute(i++, "class", "wx-in");
        b.AddAttribute(i++, "value", value.ToString("0.#"));
        b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this,
            ev => { if (double.TryParse(ev.Value?.ToString(), out var d)) setter(d); }));
        b.CloseElement();
        if (hint != null)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "wx-hint");
            b.AddContent(i++, hint);
            b.CloseElement();
        }
        b.CloseElement(); // wx-field
    }

    void AddSelect(RenderTreeBuilder b, ref int i, string label, string value,
        string? hint, (string v, string t)[] options, Action<string> setter)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-field");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-label");
        b.AddContent(i++, label);
        b.CloseElement();
        b.OpenElement(i++, "select");
        b.AddAttribute(i++, "class", "wx-select");
        b.AddAttribute(i++, "value", value);
        b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this,
            ev => setter(ev.Value?.ToString() ?? "")));
        foreach (var (v, t) in options)
        {
            b.OpenElement(i++, "option");
            b.AddAttribute(i++, "value", v);
            if (v == value) b.AddAttribute(i++, "selected", true);
            b.AddContent(i++, t);
            b.CloseElement();
        }
        b.CloseElement();
        if (hint != null)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "wx-hint");
            b.AddContent(i++, hint);
            b.CloseElement();
        }
        b.CloseElement(); // wx-field
    }

    void AddSwitchRow(RenderTreeBuilder b, ref int i, string label, string? desc, bool value, Action<bool> setter)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-switchrow");
        b.OpenElement(i++, "div");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "wx-switch-label");
        b.AddContent(i++, label);
        b.CloseElement();
        if (desc != null)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "wx-switch-desc");
            b.AddContent(i++, desc);
            b.CloseElement();
        }
        b.CloseElement();
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", value ? "wx-switch on" : "wx-switch");
        b.AddAttribute(i++, "aria-label", label);
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this,
            () => { setter(!value); StateHasChanged(); }));
        b.CloseElement();
        b.CloseElement(); // wx-switchrow
    }

    void AddBtn(RenderTreeBuilder b, ref int i, string text, string cls, Action action)
    {
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", cls);
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, action));
        b.AddContent(i++, text);
        b.CloseElement();
    }

    void AddBtnAsync(RenderTreeBuilder b, ref int i, string cls, Func<Task> action, bool busy, string text)
    {
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", cls);
        if (busy) b.AddAttribute(i++, "disabled", true);
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, action));
        if (busy)
        {
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "wx-spinner");
            b.CloseElement();
        }
        else b.AddContent(i++, text);
        b.CloseElement();
    }

    string? LoadAsset(string fileName)
    {
        string relative = Path.Combine("Plugins", "chuxin.Weather", "Assets", fileName);
        string[] candidates =
        {
            Path.Combine(AlifePath.StorageFolderPath, relative),
            Path.Combine(AppContext.BaseDirectory, relative),
            Path.Combine(AppContext.BaseDirectory, "Assets", fileName)
        };
        foreach (string path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                return $"data:image/webp;base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
            }
            catch
            {
                // 资源不可读时继续尝试其他位置
            }
        }
        return null;
    }

    static string EmojiOf(int code) => code switch
    {
        < 0 => "⏳",
        113 => "☀️",
        116 => "⛅",
        119 or 122 => "☁️",
        143 or 248 or 260 => "🌫️",
        200 or 386 or 389 => "⛈️",
        179 or 182 or 185 or 227 or 230 or 317 or 320 or 323 or 326 or 329 or 332
            or 335 or 338 or 350 or 362 or 365 or 368 or 371 or 374 or 377 => "🌨️",
        _ => "🌧️"
    };

    static string CompactHours(string spec)
    {
        var p = spec.Split('-');
        if (p.Length == 2 && p[0].Trim().Length >= 2 && p[1].Trim().Length >= 2)
            return $"{p[0].Trim()[..2]}–{p[1].Trim()[..2]}";
        return spec;
    }

    // ═══════════════════════ 样式（移植自概念稿 uitest/weather-ui-mockup.html） ═══════════════════════

    const string Css = """
.wx-root{
  --boot:2.7s;
  --sky:#0ea5e9; --sky-soft:#7dd3fc; --sky-deep:#0c4a6e;
  --dawn:#fb923c; --amber:#f59e0b; --warn:#f87171;
  --ink:#eaf4fb; --ink-dim:#8fb0c4; --ink-faint:#5c7d92;
  --line:rgba(125,211,252,.16); --line-strong:rgba(125,211,252,.32);
  position:relative;overflow:hidden;width:100%;max-width:960px;margin:0 auto;
  border-radius:20px;border:1px solid #aebfc9;color:#1c1917;
  background:linear-gradient(180deg,#cfe3ef,#e9f1f6 320px,#f2f6f9 100%);
  box-shadow:0 28px 68px rgba(28,40,52,.24),inset 0 1px 0 rgba(255,255,255,.6);
  transition:background .5s ease,box-shadow .5s ease,border-color .5s ease;
  font-family:system-ui,-apple-system,"Segoe UI","Microsoft YaHei",sans-serif;
  font-size:14px;line-height:1.5;text-align:left;
}
.wx-root,.wx-root *,.wx-root *::before,.wx-root *::after{box-sizing:border-box}
.wx-root img{user-select:none}
.wx-root button{font-family:inherit}
.wx-root.config-open{
  background:linear-gradient(180deg,#081420 0,#0b1a2a 240px,#081420 100%);
  border-color:rgba(125,211,252,.26);
  box-shadow:0 28px 72px rgba(2,10,20,.55),inset 0 0 0 1px rgba(125,211,252,.05);
}

/* ───── 主视觉 ───── */
.wx-photo-frame{
  position:relative;width:100%;aspect-ratio:16/9;min-height:520px;
  overflow:hidden;isolation:isolate;background:#7ba7c9;
  transition:height .6s cubic-bezier(.22,1,.36,1),aspect-ratio .5s ease,border-radius .45s ease;
}
.wx-photo{
  position:absolute;inset:0;width:100%;height:100%;display:block;
  object-fit:cover;object-position:center 42%;
  filter:saturate(.94) contrast(1.02) brightness(1.0);
  transform-origin:50% 40%;
  animation:wx-breathe 14s ease-in-out infinite alternate;
}
@keyframes wx-breathe{from{transform:scale(1.01)}to{transform:scale(1.035) translateY(-5px)}}
.wx-vignette{
  position:absolute;inset:0;pointer-events:none;
  background:linear-gradient(180deg,rgba(10,20,30,.02) 40%,rgba(8,16,26,.20) 70%,rgba(6,12,22,.66) 100%),
             radial-gradient(ellipse at center,transparent 55%,rgba(10,22,34,.26) 100%);
}
.wx-mist{position:absolute;pointer-events:none;border-radius:50%;filter:blur(22px);
  background:radial-gradient(circle,rgba(255,255,255,.42) 0,rgba(255,255,255,.12) 46%,transparent 72%);}
.wx-mist-a{width:64%;height:22%;left:-20%;bottom:6%;animation:wx-drift-a 19s ease-in-out infinite alternate}
.wx-mist-b{width:58%;height:20%;right:-18%;bottom:16%;animation:wx-drift-b 23s ease-in-out infinite alternate}
@keyframes wx-drift-a{to{transform:translate(52px,-8px) scale(1.07)}}
@keyframes wx-drift-b{to{transform:translate(-46px,8px) scale(1.08)}}
.wx-caption{position:absolute;z-index:7;left:40px;bottom:44px;color:#fff;pointer-events:none}
.wx-kicker{font-size:9px;font-weight:700;letter-spacing:5px;color:rgba(255,255,255,.75);text-shadow:0 2px 14px rgba(0,0,0,.5)}
.wx-title{
  margin-top:8px;font-size:34px;font-weight:700;letter-spacing:14px;
  background:linear-gradient(112deg,#ffffff 42%,#ffe9c8 50%,#ffffff 58%);
  background-size:240% 100%;-webkit-background-clip:text;background-clip:text;color:transparent;
  filter:drop-shadow(0 2px 18px rgba(0,0,0,.55)) drop-shadow(0 0 30px rgba(255,240,220,.25));
  animation:wx-shine 7s ease-in-out infinite}
@keyframes wx-shine{0%{background-position:130% 0}45%{background-position:-130% 0}100%{background-position:-130% 0}}
.wx-front-cloud{position:absolute;left:-4%;bottom:-8%;width:108%;height:36%;object-fit:cover;object-position:center 62%;
  mix-blend-mode:screen;opacity:.5;pointer-events:none;z-index:6;
  -webkit-mask-image:radial-gradient(120% 100% at 50% 100%,#000 55%,transparent 100%);
  mask-image:radial-gradient(120% 100% at 50% 100%,#000 55%,transparent 100%);
  animation:wx-front-drift 30s ease-in-out infinite alternate}
@keyframes wx-front-drift{from{transform:translateX(0)}to{transform:translateX(-40px)}}
.wx-root.config-open .wx-front-cloud{display:none}
.wx-sub{margin-top:10px;font-size:12px;letter-spacing:3px;color:rgba(255,255,255,.88);text-shadow:0 2px 12px rgba(0,0,0,.7)}

.wx-root.config-open .wx-photo-frame{height:240px;min-height:0;aspect-ratio:auto;border-radius:0 0 20px 20px}
.wx-root.config-open .wx-photo{object-position:center 55%;animation:none;transform:scale(1.02);filter:saturate(.9) brightness(.86)}
.wx-root.config-open .wx-mist{display:none}
.wx-root.config-open .wx-vignette{background:linear-gradient(180deg,rgba(3,10,18,.06),rgba(3,10,18,.5) 100%)}
.wx-root.config-open .wx-caption{left:32px;bottom:26px;transform:scale(.9);transform-origin:left bottom}
.wx-root.config-open .wx-title{font-size:20px;letter-spacing:8px}
.wx-root.config-open .wx-sub{display:none}

/* ───── 状态轨 ───── */
.wx-rail{
  position:absolute;z-index:8;top:24px;right:24px;bottom:24px;width:302px;
  overflow:hidden;border-radius:18px;border:1px solid rgba(186,230,253,.22);
  background:#0a1826;box-shadow:0 22px 54px rgba(2,10,20,.4),inset 0 1px 0 rgba(255,255,255,.08);
  transition:opacity .4s ease,transform .45s cubic-bezier(.22,1,.36,1);
}
.wx-rail::before{content:"";position:absolute;top:0;left:12%;right:12%;height:1px;z-index:2;
  background:linear-gradient(90deg,transparent,rgba(186,230,253,.55),transparent)}
.wx-rail-bg{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;object-position:center 30%;opacity:.8}
.wx-rail-shade{position:absolute;inset:0;background:linear-gradient(180deg,rgba(4,12,22,.34),rgba(4,12,22,.68) 48%,rgba(3,10,18,.94) 100%)}
.wx-rail-content{position:relative;z-index:1;height:100%;padding:18px 16px 78px;color:#dbeffe;display:flex;flex-direction:column;min-height:0}
.wx-rail-kicker{color:#7dd3fc;font:700 9px/1.3 ui-monospace,Consolas,monospace;letter-spacing:3px}
.wx-rail-title{margin-top:6px;font-size:19px;font-weight:700;letter-spacing:1px;color:#f2fbff}
.wx-rail-title small{font-size:12px;font-weight:600;color:#9dc8de;margin-left:8px;letter-spacing:0}
.wx-rail-online{display:flex;align-items:center;gap:8px;margin-top:8px;color:#7fa6bd;font:700 10px/1.2 ui-monospace,Consolas,monospace;letter-spacing:1px}
.wx-signal{width:6px;height:6px;border-radius:50%;background:#34d399;box-shadow:0 0 8px #34d399;animation:wx-pulse 1.9s ease-in-out infinite;flex:none}
@keyframes wx-pulse{50%{transform:scale(1.4);filter:brightness(1.3)}}
.wx-rail-nodes{display:flex;min-height:0;flex:1 1 auto;flex-direction:column;gap:6px;margin-top:12px;overflow-y:auto;padding-right:3px;scrollbar-width:thin;scrollbar-color:#1e3a52 transparent}
.wx-rail-node{display:grid;grid-template-columns:24px minmax(0,1fr) auto;align-items:center;gap:8px;padding:8px 9px;border-radius:11px;
  border:1px solid rgba(125,211,252,.1);background:rgba(3,12,22,.44);color:#6d8ea3;transition:transform .22s ease,border-color .22s ease}
.wx-rail-node.on{border-color:rgba(125,211,252,.26);background:rgba(10,60,96,.24);color:#c9e9f9}
.wx-rail-node.on:hover{transform:translateX(3px)}
.wx-rail-node-ico{display:grid;place-items:center;width:24px;height:24px;border-radius:8px;border:1px solid #1d3c55;background:#081624;font-size:12px}
.wx-rail-node-copy{display:flex;min-width:0;flex-direction:column;gap:2px}
.wx-rail-node-name{font-size:10px;font-weight:700;letter-spacing:.6px}
.wx-rail-node-meta{overflow:hidden;font-size:9px;color:#57809c;text-overflow:ellipsis;white-space:nowrap}
.wx-rail-node.on .wx-rail-node-meta{color:#7fb2cc}
.wx-rail-node-state{font:700 8px/1 ui-monospace,Consolas,monospace;letter-spacing:.7px;color:#48657a}
.wx-rail-node.on .wx-rail-node-state{color:#5eead4;text-shadow:0 0 9px rgba(52,211,153,.4)}
.wx-rail-node.off .wx-rail-node-ico{opacity:.55}
.wx-rail-metrics{display:grid;grid-template-columns:repeat(3,1fr);gap:6px;margin-top:10px}
.wx-rail-metric{padding:7px 8px;border-radius:9px;border:1px solid rgba(125,211,252,.12);background:rgba(2,10,20,.5)}
.wx-rail-metric span{display:block;font:700 8px/1.2 ui-monospace,Consolas,monospace;letter-spacing:.8px;color:#4f7a96}
.wx-rail-metric strong{display:block;margin-top:5px;color:#c6e7f5;font:700 11px/1 ui-monospace,Consolas,monospace}

/* ───── 门闩 ───── */
.wx-gate{
  position:absolute;z-index:10;right:38px;bottom:40px;width:276px;
  display:flex;flex-direction:column;gap:8px;
  transition:opacity .4s ease,transform .45s cubic-bezier(.22,1,.36,1);
}
.wx-gate-btn{
  width:100%;padding:12px 26px;border-radius:999px;cursor:pointer;
  border:1px solid rgba(255,255,255,.55);letter-spacing:1px;
  background:rgba(236,248,255,.88);backdrop-filter:blur(14px);
  color:#123;font-size:13px;font-weight:650;
  box-shadow:0 8px 26px rgba(4,14,26,.35),inset 0 1px 0 #fff;
  transition:transform .2s ease,box-shadow .2s ease,background .2s ease;
}
.wx-gate-btn:hover{transform:translateY(-2px);background:#fff;box-shadow:0 12px 32px rgba(4,14,26,.42),inset 0 1px 0 #fff}

.wx-root.config-open .wx-rail{opacity:0;transform:translateX(30px) scale(.97);pointer-events:none}
.wx-root.config-open .wx-gate{opacity:0;transform:translateY(14px);pointer-events:none}
.wx-console{display:none}
.wx-root.config-open .wx-console{display:block}

/* ───── 控制台 ───── */
.wx-console{position:relative;z-index:4;padding:20px 22px 28px;color:var(--ink);
  background-image:linear-gradient(rgba(125,211,252,.03) 1px,transparent 1px),linear-gradient(90deg,rgba(125,211,252,.03) 1px,transparent 1px);
  background-size:26px 26px;
  animation:wx-console-in .6s cubic-bezier(.22,1,.36,1) both}
.wx-console::before{content:"";position:absolute;inset:0;pointer-events:none;
  background:radial-gradient(640px 240px at 14% -40px,rgba(56,189,248,.13),transparent 70%),
             radial-gradient(560px 220px at 78% -30px,rgba(167,139,250,.11),transparent 70%),
             radial-gradient(760px 260px at 45% -50px,rgba(45,212,191,.08),transparent 70%),
             radial-gradient(900px 520px at 50% 118%,rgba(14,90,140,.14),transparent 70%)}
@keyframes wx-console-in{from{opacity:0;transform:translateY(16px);filter:blur(6px)}to{opacity:1;transform:translateY(0);filter:blur(0)}}
.wx-console-head{position:relative;display:flex;align-items:center;justify-content:space-between;gap:18px;padding:4px 6px 18px;border-bottom:1px solid var(--line)}
.wx-console-head::after{content:"";position:absolute;left:0;bottom:-1px;height:1px;width:220px;pointer-events:none;
  background:linear-gradient(90deg,transparent,#7dd3fc 40%,#e9d8ff 60%,transparent);animation:wx-headscan 5s ease-in-out infinite}
@keyframes wx-headscan{0%{left:-220px;opacity:0}14%{opacity:1}86%{opacity:1}100%{left:100%;opacity:0}}
.wx-console-kicker{font-size:9px;letter-spacing:3px;color:#7dd3fc;font-weight:700;opacity:.85}
.wx-console-title{margin-top:5px;font-size:21px;font-weight:700;letter-spacing:4px;
  background:linear-gradient(100deg,#eaf7ff 32%,#9adcf7 50%,#eaf7ff 68%);background-size:220% 100%;
  -webkit-background-clip:text;background-clip:text;color:transparent;
  filter:drop-shadow(0 0 22px rgba(125,211,252,.18));animation:wx-shine 8s ease-in-out infinite}
.wx-console-sub{margin-top:5px;font-size:11px;letter-spacing:.6px;color:#6f93a9}
.wx-console-meter{flex:0 0 auto;display:flex;align-items:center;gap:8px;padding:7px 12px;border-radius:999px;
  border:1px solid rgba(125,211,252,.3);background:linear-gradient(120deg,rgba(14,80,128,.22),rgba(20,40,80,.28));
  box-shadow:0 0 18px rgba(56,189,248,.1),inset 0 1px 0 rgba(255,255,255,.06);color:#9adcf7;
  font:700 10px/1.2 ui-monospace,Consolas,monospace;letter-spacing:1px}

.wx-shell{display:grid;grid-template-columns:158px minmax(0,1fr);gap:16px;align-items:start;margin-top:16px}

/* 侧栏 */
.wx-nav{position:sticky;top:10px;display:flex;flex-direction:column;gap:7px;padding:9px;border-radius:18px;
  border:1px solid rgba(125,211,252,.16);
  background:linear-gradient(165deg,rgba(16,34,54,.72),rgba(6,15,26,.8));
  backdrop-filter:blur(18px);-webkit-backdrop-filter:blur(18px);
  box-shadow:0 18px 44px rgba(0,0,0,.3),inset 0 1px 0 rgba(255,255,255,.05)}
.wx-nav-btn{position:relative;display:grid;grid-template-columns:30px minmax(0,1fr);align-items:center;gap:9px;width:100%;
  padding:10px 9px;border-radius:13px;border:1px solid transparent;background:transparent;color:#6d8ea3;
  text-align:left;cursor:pointer;transition:transform .22s ease,color .22s ease,background .22s ease,border-color .22s ease}
.wx-nav-btn::before{content:"";position:absolute;left:0;top:20%;bottom:20%;width:2px;border-radius:99px;background:#38bdf8;opacity:0;
  transform:scaleY(.25);box-shadow:0 0 12px #38bdf8;transition:opacity .2s ease,transform .3s cubic-bezier(.22,1,.36,1)}
.wx-nav-btn:hover{color:#c9e9f9;background:rgba(56,189,248,.06)}
.wx-nav-btn.active{color:#e8f7ff;border-color:rgba(125,211,252,.3);
  background:linear-gradient(110deg,rgba(14,80,128,.38),rgba(14,80,128,.1));transform:translateX(3px);
  box-shadow:0 8px 24px rgba(0,0,0,.22),inset 0 1px 0 rgba(153,220,255,.08)}
.wx-nav-btn.active::before{opacity:1;transform:scaleY(1)}
.wx-nav-ico{display:grid;place-items:center;width:30px;height:30px;border-radius:10px;font-size:14px;
  border:1px solid rgba(125,211,252,.22);background:linear-gradient(140deg,rgba(14,60,96,.55),rgba(8,22,36,.92));color:#6f96ad;
  transition:all .22s ease}
.wx-nav-btn.active .wx-nav-ico{background:linear-gradient(140deg,#0ea5e9,#6366f1);border-color:transparent;color:#fff;
  box-shadow:0 0 18px rgba(56,189,248,.4);transform:rotate(-4deg) scale(1.05)}
.wx-nav-copy{display:flex;min-width:0;flex-direction:column;gap:3px}
.wx-nav-label{font-size:11px;font-weight:700;letter-spacing:.7px;white-space:nowrap}
.wx-nav-meta{overflow:hidden;font-size:8px;color:#4f7a96;text-overflow:ellipsis;white-space:nowrap}

/* 工作区面板 */
.wx-workspace{position:relative;min-width:0}
.wx-panel{position:relative;overflow:hidden;margin-bottom:14px;padding:17px 19px 19px 22px;border-radius:18px;
  border:1px solid transparent;
  background:linear-gradient(150deg,rgba(16,32,52,.78),rgba(7,16,28,.86)) padding-box,
             linear-gradient(160deg,rgba(125,211,252,.34),rgba(125,211,252,.06) 34%,rgba(99,102,241,.14) 68%,rgba(125,211,252,.22)) border-box;
  backdrop-filter:blur(16px);-webkit-backdrop-filter:blur(16px);
  box-shadow:0 18px 44px rgba(0,0,0,.3),inset 0 1px 0 rgba(255,255,255,.05);
  transition:transform .25s ease,box-shadow .25s ease;
  animation:wx-panel-in .45s cubic-bezier(.22,1,.36,1) both}
.wx-panel>*{position:relative;z-index:1}
.wx-panel::after{content:"";position:absolute;z-index:0;left:-60%;top:-120%;width:30%;height:340%;pointer-events:none;
  transform:rotate(16deg);opacity:0;
  background:linear-gradient(90deg,transparent,rgba(153,220,255,.09),transparent)}
.wx-panel:hover{transform:translateY(-2px);box-shadow:0 22px 54px rgba(0,0,0,.36),0 0 28px rgba(13,148,136,.05)}
.wx-panel:hover::after{animation:wx-panelscan .9s ease}
@keyframes wx-panelscan{0%{left:-60%;opacity:0}35%{opacity:1}100%{left:130%;opacity:0}}
.wx-panel::before{content:"";position:absolute;z-index:1;left:0;top:14px;bottom:14px;width:2px;border-radius:99px;
  background:linear-gradient(180deg,transparent,var(--a,#38bdf8) 18%,var(--a,#38bdf8) 82%,transparent);box-shadow:0 0 16px var(--a,#38bdf8)}
.wx-panel-head{display:flex;align-items:center;gap:10px;margin-bottom:6px;color:#d8ecf8}
.wx-panel-head::after{content:attr(data-idx);margin-left:auto;font:700 11px/1 ui-monospace,Consolas,monospace;
  letter-spacing:2px;color:rgba(125,211,252,.35)}
.wx-panel-ico{display:inline-grid;place-items:center;width:26px;height:22px;border-radius:7px;font-size:12px;color:#bde9ff;
  border:1px solid rgba(125,211,252,.4);background:linear-gradient(140deg,rgba(56,189,248,.18),rgba(56,189,248,.05));
  box-shadow:0 0 14px rgba(56,189,248,.12)}
.wx-panel-title{font-size:14px;font-weight:650;letter-spacing:.4px}
.wx-panel-desc{font-size:11px;color:#5f8299;margin-bottom:12px;line-height:1.6}
@keyframes wx-panel-in{from{opacity:0;transform:translateX(16px) scale(.99);filter:blur(5px)}to{opacity:1;transform:translateX(0) scale(1);filter:blur(0)}}

/* 控件 */
.wx-grid2{display:grid;grid-template-columns:1fr 1fr;gap:10px 12px}
.wx-field{padding:11px 13px 10px;border-radius:13px;
  border:1px solid rgba(120,155,180,.16);
  background:linear-gradient(180deg,rgba(4,12,22,.5),rgba(3,10,18,.62));
  box-shadow:inset 0 1px 0 rgba(255,255,255,.03);
  transition:border-color .2s ease,background .2s ease,box-shadow .2s ease}
.wx-field:hover{border-color:rgba(125,211,252,.3)}
.wx-field:focus-within{border-color:rgba(125,211,252,.45);box-shadow:0 0 0 3px rgba(56,189,248,.09),0 0 20px rgba(14,120,180,.1)}
.wx-label{font-size:12px;font-weight:600;color:#c3dcea;letter-spacing:.25px}
.wx-hint{font-size:10px;color:#53768c;margin-top:4px;line-height:1.5}
.wx-in,.wx-select,.wx-tarea{
  width:100%;margin-top:6px;padding:8px 11px;border-radius:9px;font-size:13px;
  color:#daf2fb;background:#050f18;border:1px solid #24404f;outline:none;
  transition:border-color .18s ease,box-shadow .18s ease}
.wx-in:focus,.wx-select:focus,.wx-tarea:focus{border-color:#2f95c9;box-shadow:0 0 0 3px rgba(56,189,248,.12),0 0 14px rgba(14,120,180,.12)}
.wx-in[type=number]{color-scheme:dark}
.wx-select{appearance:none;cursor:pointer;
  background-image:linear-gradient(45deg,transparent 50%,#5f8299 50%),linear-gradient(135deg,#5f8299 50%,transparent 50%);
  background-position:calc(100% - 16px) 55%,calc(100% - 11px) 55%;background-size:5px 5px;background-repeat:no-repeat}
.wx-select option{background:#0b1c2c;color:#daf2fb}
.wx-tarea{resize:vertical;min-height:52px;font-size:12px}

/* 开关 */
.wx-switchrow{display:flex;align-items:center;justify-content:space-between;gap:10px;padding:11px 13px;border-radius:13px;
  border:1px solid rgba(120,155,180,.16);
  background:linear-gradient(180deg,rgba(4,12,22,.5),rgba(3,10,18,.62));
  box-shadow:inset 0 1px 0 rgba(255,255,255,.03);transition:border-color .2s ease}
.wx-switchrow:hover{border-color:rgba(125,211,252,.3)}
.wx-switch{position:relative;width:44px;height:22px;border-radius:999px;border:none;cursor:pointer;flex:none;
  background:#1e3a4d;transition:background .25s ease;box-shadow:inset 0 1px 4px rgba(0,0,0,.4)}
.wx-switch::after{content:"";position:absolute;top:2px;left:2px;width:18px;height:18px;border-radius:50%;background:#e8f4fa;
  box-shadow:0 1px 4px rgba(0,0,0,.4);transition:left .25s cubic-bezier(.22,1,.36,1)}
.wx-switch.on{background:linear-gradient(135deg,#0ea5e9,#0284c7);box-shadow:0 0 14px rgba(14,165,233,.35)}
.wx-switch.on::after{left:24px}
.wx-switch-label{font-size:12px;font-weight:600;color:#c3dcea}
.wx-switch-desc{font-size:10px;color:#53768c;margin-top:2px}

/* 按钮 */
.wx-btn{position:relative;overflow:hidden;display:inline-flex;align-items:center;gap:6px;padding:7px 15px;border-radius:999px;border:none;cursor:pointer;
  font-size:12px;font-weight:600;color:#eaf6ff;letter-spacing:.4px;
  background:linear-gradient(135deg,#0369a1,#075985);box-shadow:0 4px 14px rgba(3,105,161,.3),inset 0 1px 0 rgba(186,230,253,.2);
  transition:transform .18s ease,box-shadow .18s ease}
.wx-btn::after{content:"";position:absolute;inset:0;width:40%;pointer-events:none;
  background:linear-gradient(90deg,transparent,rgba(255,255,255,.22),transparent);transform:translateX(-120%)}
.wx-btn:not(:disabled):hover{transform:translateY(-1px);box-shadow:0 7px 20px rgba(14,165,233,.32)}
.wx-btn:not(:disabled):hover::after{animation:wx-btnshine .6s ease}
@keyframes wx-btnshine{to{transform:translateX(320%)}}
.wx-btn:disabled{opacity:.6;cursor:wait}
.wx-btn.ghost{background:transparent;border:1px solid rgba(125,211,252,.28);color:#9ad3ee;box-shadow:none}
.wx-btn.ghost:not(:disabled):hover{border-color:rgba(125,211,252,.55);color:#dcf1fc}
.wx-btn.mini{padding:4px 11px;font-size:11px;border-radius:8px;box-shadow:none;background:rgba(14,80,128,.28);border:1px solid rgba(125,211,252,.22);color:#a8d8ef}
.wx-btn.mini:hover{background:rgba(14,100,160,.4);color:#e2f4fd}
.wx-btn.danger{background:rgba(127,29,29,.3);border:1px solid rgba(248,113,113,.3);color:#fca5a5;box-shadow:none}
.wx-btn.danger:hover{background:rgba(153,27,27,.42)}
.wx-spinner{width:11px;height:11px;border:2px solid rgba(255,255,255,.3);border-top-color:#fff;border-radius:50%;animation:wx-spin .7s linear infinite}
@keyframes wx-spin{to{transform:rotate(360deg)}}

/* 规则卡片 */
.wx-rules{display:flex;flex-direction:column;gap:8px}
.wx-rule{position:relative;display:grid;grid-template-columns:minmax(0,1fr) auto;gap:4px 12px;align-items:center;
  padding:12px 13px 12px 18px;border-radius:14px;border:1px solid transparent;overflow:hidden;
  background:linear-gradient(150deg,rgba(13,28,46,.82),rgba(6,14,24,.9)) padding-box,
             linear-gradient(160deg,rgba(125,211,252,.26),rgba(125,211,252,.05) 40%,rgba(125,211,252,.16)) border-box;
  backdrop-filter:blur(12px);-webkit-backdrop-filter:blur(12px);
  box-shadow:0 10px 26px rgba(0,0,0,.22),inset 0 1px 0 rgba(255,255,255,.04);
  transition:transform .2s ease,box-shadow .2s ease,opacity .3s ease}
.wx-rule:hover{transform:translateY(-2px);box-shadow:0 14px 34px rgba(0,0,0,.3),0 0 22px rgba(56,189,248,.06)}
.wx-rule::before{content:"";position:absolute;left:0;top:16%;bottom:16%;width:2px;border-radius:99px;background:var(--a,#38bdf8);opacity:.75}
.wx-rule.disabled{opacity:.5}
.wx-rule.disabled::before{background:#426075;box-shadow:none}
.wx-rule-name{display:flex;align-items:center;gap:8px;font-size:13px;font-weight:650;color:#d9edf9;min-width:0}
.wx-rule-name em{font-style:normal;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.wx-badge{flex:none;font-size:9px;font-weight:700;letter-spacing:.6px;padding:2.5px 7px;border-radius:999px}
.wx-badge.builtin{color:#7fb8d8;border:1px solid rgba(125,211,252,.24);background:rgba(14,80,128,.2)}
.wx-badge.custom{color:#c4b5fd;border:1px solid rgba(196,181,253,.3);background:rgba(91,33,182,.18)}
.wx-badge.push{color:#fcd34d;border:1px solid rgba(251,191,36,.35);background:rgba(120,53,15,.24)}
.wx-badge.silent{color:#93c5fd;border:1px solid rgba(147,197,253,.3);background:rgba(23,37,84,.35)}
.wx-rule-summary{grid-column:1;font-size:11px;color:#53768c;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.wx-rule-ops{grid-row:1/3;grid-column:2;display:flex;align-items:center;gap:6px}
.wx-led{position:relative;width:34px;height:19px;border-radius:999px;border:none;cursor:pointer;background:#1e3a4d;flex:none;transition:background .25s ease}
.wx-led::after{content:"";position:absolute;top:2.5px;left:2.5px;width:14px;height:14px;border-radius:50%;background:#dbeafe;transition:left .25s cubic-bezier(.22,1,.36,1)}
.wx-led.on{background:linear-gradient(135deg,#10b981,#059669);box-shadow:0 0 10px rgba(16,185,129,.4)}
.wx-led.on::after{left:17.5px}
.wx-rule-foot{display:flex;align-items:center;gap:8px;margin-top:12px;flex-wrap:wrap}
.wx-rule-foot .spacer{flex:1}
.wx-foot-note{font-size:10px;color:#405d70;letter-spacing:.3px}

/* 编辑面板 / 测试结果 */
.wx-editor{margin-top:12px;padding:14px 15px 15px;border-radius:13px;border:1px dashed rgba(125,211,252,.3);background:rgba(8,24,38,.6);animation:wx-pop .3s ease}
@keyframes wx-pop{from{opacity:0;transform:scale(.985)}to{opacity:1;transform:scale(1)}}
.wx-editor-head{display:flex;align-items:center;gap:8px;font-size:12px;font-weight:700;color:#bde9ff;letter-spacing:1px;margin-bottom:10px}
.wx-testbox{margin-top:12px;padding:11px 13px;border-radius:11px;white-space:pre-wrap;font-size:12px;line-height:1.7;
  border:1px solid rgba(251,191,36,.32);background:rgba(120,53,15,.16);color:#fde9c0;animation:wx-pop .3s ease}
.wx-testbox.ok{border-color:rgba(52,211,153,.3);background:rgba(6,78,59,.16);color:#c9f5e5}

/* ───── 开场动画：夜航 → 破晓 → 铭牌 ───── */
.wx-boot{position:absolute;inset:0;z-index:60;overflow:hidden;border-radius:20px;cursor:pointer}
.wx-boot.fade{opacity:0;transition:opacity .55s ease;pointer-events:none}
.wx-boot::after{content:"";position:absolute;inset:0;pointer-events:none;z-index:9;
  background:radial-gradient(ellipse at center,transparent 52%,rgba(3,9,18,.38) 100%)}
.wx-boot-dawnimg,.wx-boot-nightimg{position:absolute;inset:0;width:100%;height:100%;object-fit:cover}
.wx-boot-nightimg{animation:wx-fadeout 1s ease 1.02s both}
.wx-boot-stars span{position:absolute;border-radius:50%;background:#e6f2ff;box-shadow:0 0 6px rgba(200,225,255,.95);animation:wx-twinkle 1.7s ease-in-out infinite}
@keyframes wx-twinkle{0%,100%{opacity:.1}50%{opacity:.95}}
.wx-boot-sun{position:absolute;left:50%;top:21%;width:400px;height:400px;margin:-200px 0 0 -200px;border-radius:50%;
  mix-blend-mode:screen;animation:wx-sun 1.05s cubic-bezier(.22,1,.36,1) .95s both}
@keyframes wx-sun{from{transform:scale(.18);opacity:0}to{transform:scale(1);opacity:.95}}
.wx-boot.fade .wx-boot-sun{animation:wx-swell .55s ease both}
@keyframes wx-swell{from{transform:scale(1);opacity:.95}to{transform:scale(1.9);opacity:1}}
.boot-rays{position:absolute;left:50%;top:21%;width:680px;height:680px;margin:-340px 0 0 -340px;
  mix-blend-mode:screen;opacity:0;
  animation:wx-rays-in 1.5s ease 1.05s both,wx-rot 32s linear infinite}
@keyframes wx-rays-in{to{opacity:.7}}
@keyframes wx-rot{to{transform:rotate(360deg)}}
.boot-shoot{position:absolute;width:110px;height:1.5px;border-radius:99px;
  background:linear-gradient(90deg,transparent,rgba(220,235,255,.95));
  opacity:0;animation:wx-shoot 1.1s ease-in-out both}
@keyframes wx-shoot{0%{opacity:0;transform:translate(0,0) rotate(-32deg)}12%{opacity:1}55%{opacity:.9}100%{opacity:0;transform:translate(-260px,150px) rotate(-32deg)}}
.boot-mote{position:absolute;border-radius:50%;filter:blur(1.5px);
  background:radial-gradient(circle,rgba(255,236,200,.9),rgba(255,236,200,0) 70%);
  animation:wx-mote linear infinite}
@keyframes wx-mote{0%{transform:translateY(0);opacity:0}12%{opacity:.5}85%{opacity:.35}100%{transform:translateY(-56vh) translateX(6vw);opacity:0}}
.boot-brand{position:absolute;left:0;right:0;top:47%;z-index:10;text-align:center;pointer-events:none;
  opacity:0;animation:wx-brand-in .9s cubic-bezier(.22,1,.36,1) 1.9s both}
@keyframes wx-brand-in{from{opacity:0;transform:translateY(16px)}to{opacity:1;transform:none}}
.boot-brand-title{font-family:'Noto Serif SC','Source Han Serif SC','STZhongsong','STSong','SimSun',serif;
  font-size:46px;font-weight:700;letter-spacing:18px;padding-left:18px;color:#fff;
  text-shadow:0 2px 34px rgba(255,180,90,.6),0 2px 10px rgba(110,55,10,.4)}
.boot-brand-line{width:150px;height:1px;margin:14px auto 10px;
  background:linear-gradient(90deg,transparent,rgba(255,220,170,.9),transparent)}
.boot-brand-sub{font-size:10px;letter-spacing:5px;color:rgba(255,244,228,.85);text-shadow:0 1px 8px rgba(80,40,10,.4)}
.boot-progress{width:170px;height:2px;margin:18px auto 0;border-radius:99px;background:rgba(255,255,255,.16);overflow:hidden}
.boot-progress span{display:block;height:100%;border-radius:99px;
  background:linear-gradient(90deg,#ffd9a0,#f59e0b);box-shadow:0 0 10px rgba(245,158,11,.85);
  transform-origin:left;transform:scaleX(0);animation:wx-progress .85s cubic-bezier(.3,.6,.4,1) 1.95s both}
@keyframes wx-progress{to{transform:scaleX(1)}}
.wx-boot-cloud{position:absolute;left:-6%;width:112%;object-fit:cover;mix-blend-mode:screen;
  -webkit-mask-image:linear-gradient(180deg,transparent 0,#000 22%,#000 78%,transparent 100%);
  mask-image:linear-gradient(180deg,transparent 0,#000 22%,#000 78%,transparent 100%)}
.b-cl1{height:40%;top:60%;object-position:center 85%;filter:brightness(.72);animation:wx-cd1 2.05s linear .05s both}
.b-cl2{height:46%;top:72%;object-position:center 60%;filter:brightness(.55);animation:wx-cd2 2.05s linear .05s both}
.b-cl3{height:30%;top:46%;object-position:center 40%;opacity:.4;filter:brightness(.95);animation:wx-cd3 2.05s linear .05s both}
@keyframes wx-cd1{to{transform:translateY(420px)}}
@keyframes wx-cd2{to{transform:translateY(500px)}}
@keyframes wx-cd3{to{transform:translateY(260px)}}
.boot-orb{position:absolute;left:50%;top:118%;width:330px;height:330px;margin:-165px 0 0 -165px;
  mix-blend-mode:screen;
  animation:wx-orb-rise 2.1s cubic-bezier(.4,.08,.35,1) .05s both,wx-orb-bloom 2.1s ease .05s both}
@keyframes wx-orb-rise{
  0%{top:118%;transform:scale(.72)}
  70%{top:22%;transform:scale(1)}
  100%{top:20%;transform:scale(1.05)}}
@keyframes wx-orb-bloom{
  0%{filter:brightness(.42) saturate(.9)}
  55%{filter:brightness(.78) saturate(.98)}
  100%{filter:brightness(1.14) saturate(1.06)}}
.wx-boot-tip{position:absolute;left:0;right:0;bottom:26px;text-align:center;
  color:rgba(255,255,255,.85);font-size:11px;letter-spacing:6px;font-weight:600;
  text-shadow:0 2px 10px rgba(0,0,0,.4);animation:wx-tip-in .8s ease .35s both}
@keyframes wx-tip-in{from{opacity:0;transform:translateY(10px)}to{opacity:1;transform:none}}

/* ───── 入场（衔接开场动画之后） ───── */
.wx-root.intro .wx-photo-frame{animation:wx-awaken .5s ease-out both;animation-delay:calc(var(--boot) - .1s)}
.wx-root.intro .wx-photo{animation:wx-reveal 1.6s cubic-bezier(.16,.78,.18,1) var(--boot) both}
.wx-root.intro .wx-vignette{animation:wx-fadein .5s ease var(--boot) both}
.wx-root.intro .wx-caption{animation:wx-caption-in .8s cubic-bezier(.22,1,.36,1) calc(var(--boot) + .15s) both}
.wx-root.intro .wx-rail{animation:wx-rail-in .7s cubic-bezier(.22,1,.36,1) calc(var(--boot) + .3s) both}
.wx-root.intro .wx-gate{animation:wx-up-in .6s cubic-bezier(.22,1,.36,1) calc(var(--boot) + .45s) both}
@keyframes wx-awaken{from{opacity:0;filter:brightness(.5)}to{opacity:1;filter:brightness(1)}}
@keyframes wx-reveal{0%{transform:scale(1.14);filter:brightness(.7) saturate(1.1)}60%{filter:brightness(.95)}100%{transform:scale(1.01);filter:brightness(1) saturate(.94)}}
@keyframes wx-fadein{from{opacity:0}to{opacity:1}}
@keyframes wx-caption-in{from{opacity:0;transform:translateY(16px);letter-spacing:8px}to{opacity:1;transform:translateY(0)}}
@keyframes wx-rail-in{from{opacity:0;transform:translateX(30px) scale(.97);filter:blur(8px)}to{opacity:1;transform:translateX(0) scale(1);filter:blur(0)}}
@keyframes wx-up-in{from{opacity:0;transform:translateY(14px)}to{opacity:1;transform:translateY(0)}}
@keyframes wx-fadeout{to{opacity:0}}

@media (max-width:900px){
  .wx-photo-frame{aspect-ratio:4/5;min-height:0}
  .wx-rail{display:none}
  .wx-gate{position:relative;right:auto;bottom:auto;width:auto;margin-top:-84px;padding:0 22px 22px}
  .wx-gate-btn{width:auto}
  .wx-shell{grid-template-columns:1fr}
  .wx-nav{position:relative;flex-direction:row;overflow-x:auto}
  .wx-nav-btn{flex:0 0 auto}
  .wx-nav-meta{display:none}
  .wx-grid2{grid-template-columns:1fr}
}
@media (prefers-reduced-motion:reduce){
  .wx-root *,.wx-root *::before,.wx-root *::after{animation-duration:.01ms !important;animation-iteration-count:1 !important;transition-duration:.01ms !important}
}
""";
}
