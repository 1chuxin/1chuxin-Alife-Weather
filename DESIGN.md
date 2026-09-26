# chuxin.Weather 天气插件设计

> 版本 v0.8（动工版）· 2026-09-26 · 适用 Alife 客户端 4.2.x
> v0.4：三级分发（Poke 推送 / 静默注入 / 定点晨报）+ 数据源准确度与更新频率评估。
> v0.5：吸收自研 router.js 实战经验——当日累计变化规则（修渐变漏报）、P1 注入通道补全、城市随行入路线图。
> v0.6：分类定为**初心的小工具**；**自定义规则系统**（内置规则统一为默认规则集，UI 层可增删改）；补审查发现的漏洞（OnUpdate 防重入、多角色状态隔离、跨零点窗口、同轮 Poke 合并、昨日快照语义）。
> v0.7（终审）：补回常规配置项总表（§6.5，v0.6 重构时遗漏）、常驻天气行附生效预警条数、指标×窗口组合约束、规则城市须在监控范围、晨报独立于变化检测开关。设计定稿。
> v0.8（动工）：插件 ID 定为 **`chuxin.Weather`**（与 chuxin.TokenStats 统一）；新增 AI 函数 `<set_default_city/>`（修改默认城市并自动纳入监控）；明确"查询其他城市"由 `query_weather/query_forecast/query_warning` 的 `city` 参数承担（wttr 全球覆盖）；默认城市北京。开始实现。
> 目标：为 Alife 角色提供**天气查询**、**灾害预警推送**、**天气变化提醒**（强变化推送、弱变化静默更新上下文），**规则用户可自定义**，开箱即用（零注册）。

## 1. 总体架构

```
                          ┌────────────────────────────────────────┐
                          │        角色 AI（Character）             │
                          └───────▲────────────────────▲───────────┘
                    XmlFunction 调用│                    │ 三级分发
                    （AI 主动拉数据）│        ┌───────────┼────────────┐
                          ┌────────┴───┐    │P0 Poke推送 │P1 静默注入   │P2 定点晨报
                          │            │    │(唤醒AI)    │(改上下文不唤醒)│(定时汇总)
                          │ WeatherModule（ChatBehaviour，注入 ChatBot）
                          │  OnAwake   注册函数 / 校验配置
                          │  OnStart   基线拉取 + 首次注入常驻天气行
                          │  OnUpdate  墙钟心跳 → 防重入后台任务
                          │            ├─ 预警轮询（10 min）
                          │            ├─ 变化检测（60 min）→ 规则引擎
                          │            └─ 晨报定点（07:30，可选）
                          │  OnDestroy 注销 / 释放 HttpClient
                          └───┬──────────────┬──────────────┬───────┘
                              │              │              │
                    ┌─────────▼───┐  ┌───────▼──────┐  ┌────▼─────────┐
                    │ 查询客户端    │  │ 预警客户端     │  │ 规则引擎+分发   │
                    │ WttrIn(默认) │  │ Nmc(默认)    │  │ 规则=统一数据模型│
                    │ QWeather(可选)│  │ QWeather(可选)│  │ 内置4条=默认集  │
                    └─────────┬───┘  └───────┬──────┘  └────┬─────────┘
                              └──────────────┼──────────────┘
                        ┌────────────────────▼─────────────────────┐
                        │ WeatherTexts（WWO 代码→中文映射、格式化）   │
                        │ StorageSystem（状态持久化，按角色隔离）     │
                        │ 角色级 Configuration（规则集存这里）        │
                        └─────────────────────────────────────────┘
```

五条架构原则：

1. **单模块双通道**：查询走函数调用，推送走心跳检测后分级分发。
2. **心跳单源多任务，任务全部后台化**：`OnUpdate` 只做墙钟判断和"启动后台任务"，HTTP 请求在后台 Task 里跑（带防重入标志 + 逐步检查 `DestroyCancellationToken`），绝不阻塞心跳。
3. **数据源可插拔**：查询/预警各一个窄接口，默认 wttr+nmc 零配置，配和风 Key 整体升级。
4. **一切推送规则统一为规则引擎的数据模型**：内置 4 条规则只是"默认规则集"，与用户自定义规则同机制、同 UI 管理（§6）。
5. **推送必过状态机**：diff → 去重（防抖键）→ 静默时段 → 同轮合并，宁可漏报不轰炸。

## 2. 插件身份与文件结构

- **插件 ID / 文件夹名**：`chuxin.Weather`（与 `chuxin.TokenStats` 统一前缀）
- **命名空间**：`chuxin.Weather`；**模块 ID**：`chuxin.Weather.WeatherModule`
- **模块名**：`天气服务`；**defaultCategory**：`初心的小工具`
- **默认城市**：安阳；AI 可经 `<set_default_city/>` 迁移（监控范围自动跟随默认城市）
- **配置 UI**：razor EditorUI（预编译），规则管理界面见 §6.3

```
chuxin.Weather/
├── manifest.json              // Version 4.2.0，仅依赖 FunctionCaller
├── WeatherModule.cs           // 模块：生命周期 + 3 个 XmlFunction + 心跳调度（EditorUI 指向 UI 类）
├── WeatherConfig.cs           // 配置 POCO（含规则集 List<WeatherRule>）
├── WeatherRule.cs             // 规则模型 + 校验
├── WeatherRuleEngine.cs       // 规则评价引擎（内置 4 条=默认规则集）
├── WeatherClients.cs          // WttrInClient + NmcAlarmClient（+可选 QWeatherClient）
├── WeatherMonitor.cs          // 推送状态机：diff + 三级分发 + 快照滚动
├── WeatherTexts.cs            // WWO 代码→中文映射表、格式化输出
├── WeatherModuleUI_razor.g.cs // 规则管理 UI（razor 项目预编译产物，平铺进插件）
└── DESIGN.md
```

开发期另建 razor 类库项目（`Microsoft.NET.Sdk.Razor`，引用 Alife.Framework / FunctionCaller 源项目，照官方示例 csproj），编译后把生成的 `WeatherModuleUI.razor.g.cs` 平铺进插件文件夹——官方插件（TimedTask 等）均为该结构。manifest.json：

```json
{
  "Version": "4.2.0",
  "Dependencies": {
    "Alife.Function.FunctionCaller": ""
  },
  "Environments": null
}
```

模块构造函数注入：`XmlFunctionCaller` / `ILogger<>` / `Interactor<>` / `ChatBot`。不引第三方 NuGet。

## 3. 数据源设计（全部实测过）

### 3.1 查询：wttr.in（默认，无 Key）

`GET https://wttr.in/{城市}?format=j1`（城市名支持中文/拼音/英文）

- **返回结构**（已实测）：`current_condition[]`（temp_C/FeelsLikeC/humidity/pressure/visibility/precipMM/winddir16Point/windspeedKmph/uvIndex/weatherCode/observation_time）；`weather[]` 3 天预报（maxtempC/mintempC/astronomy + hourly[8] 每 3 小时：chanceofrain、chanceofthunder 等）。
- **对策**：weatherDesc 英文且 `lang=zh` 实测未翻译 → 内置 WWO weatherCode→中文映射表（约 40 条）；只有 3 天预报。
- 监控/渲染统一用 `format=j1`（需要 weatherCode 与逐时段降水概率）；紧凑格式 `format=%C+%t+%h+%w+%p+%S+%s` 留作省流量备选。

### 3.2 预警：中央气象台 nmc.cn（默认，无 Key，官方数据+非官方接口）

`GET https://www.nmc.cn/f/rest/findAlarm?pageNo=1&pageSize=1000`

- **返回结构**（已实测 code=0）：`data.page.list[]` 全国生效预警（实测 770 条），每条 `alertid`/`issuetime`/`title`（自带级别）/`url`/`pic`。
- **过滤**：`province` 参数实测不可用（417）→ 一次拉全国、本地按城市名 Contains 匹配。
- **风险对策**：反序列化全宽容；关键字段提取失败 → `LogWarning`+停推送；连续失败只报一次日志；轮询 try/catch 全包。
- **附带发现**：nmc 站点实况接口 `/f/rest/real/{stationid}` 含 `weather/wind/warn/publish_time` 字段（实测该次返回空模板，stationid 形态待实现时核验）——若可用，预警可按站点精确匹配并成为中文实况源，列入路线图。

### 3.3 可选升级：和风天气（配 Key）与 Open-Meteo（扩展位）

- **和风**：按 LocationID 精确匹配、预警含正文/级别/发布单位、7 天预报。端点 `/v7/weather/now|3d|7d`、`/v7/warning/now`、GeoAPI；免费版 host `devapi.qweather.com`（可配置）；鉴权 API Key（`X-QW-Api-Key` 头）。
- **Open-Meteo（v1 不实现）**：无 Key、7–16 天预报、自带 geocoding、无中国预警。

### 3.4 源选择配置

| 配置 | 取值 | 默认 |
|---|---|---|
| QuerySource | `wttr` / `qweather` | `wttr` |
| WarningSource | `nmc` / `qweather` / `off` | `nmc` |

### 3.5 各源准确度与更新频率评估（决定轮询节奏）

| 源 | 数据性质 | 更新频率 | 准确度评估 | 对本插件的约束 |
|---|---|---|---|---|
| nmc findAlarm | 官方灾害预警（CMA 发布） | 分钟级 | **权威**（与气象台发布同步） | 10 分钟轮询绰绰有余 |
| wttr.in | WWO 模型+实况混合 | 服务端分钟级缓存；实况约**小时级** | 中等：中国城市为模型插值，温度偏差 1–3°C 量级 | **变化检测 60 分钟一轮正合适**；查询函数按需调用不受影响 |
| QWeather（可选） | CMA 数据+自有加工 | 实况分钟级（以官方文档为准） | 高（中国境内权威，结构化最好） | 切换后变化检测可提速到 30 分钟 |
| Open-Meteo（扩展位） | 纯模型数据（ECMWF/GFS 每 6 小时一轮） | 模型 6 小时一次，输出逐小时 | 全球模型中 ECMWF 最优，无本地实况修正；无中国预警 | 适合预报兜底与历史对比，**不适合实况跟踪** |

**结论**：预警走 nmc 分钟级权威源；实况/变化走 wttr 小时级源；轮询节奏与数据更新频率对齐。规则引擎的评价频率 = 变化检测频率（60 分钟），自定义规则达不到比这更细的粒度——这是数据源决定的物理上限，UI 上要向用户说明。

### 3.6 候选数据源评估（2026-09-26 搜索+实测，路线图选型依据）

| 源 | 实测结果 | 对本插件的价值 |
|---|---|---|
| **Open-Meteo Air Quality**（air-quality-api.open-meteo.com） | ✅ 无 Key 直连，北京 current：US AQI 187 / PM2.5 240 / PM10 246（与 wttr"烟霾"互证）；hourly 逐小时，全球 CAMS 模型 | **AQI 路线图最优解**：一个 GET 替代 nmc stationid 链；新增 `aqi_us` 规则指标后"AQI>150 推送"立即可用 |
| **MET Norway locationforecast 2.0 compact** | ✅ 无 Key（UA 需可识别，通用 UA 会被 block），北京逐小时 `next_1_hours` 降水量+symbol_code，前 2 天逐小时、共 9.5 天 | **临近降水的最优免费源**：1 小时粒度降水量+现象，填补 wttr 3 小时概率的粒度缺口；要求 ≥10 分钟缓存 |
| **7Timer!**（www.7timer.info，中科院天文台主办） | ✅ 无 Key，GFS 模型，3 小时步长 8 天（64 点），temp2m/weather/prec_type/prec_amount/cloudcover | 备用预报源（中国托管但无 SLA）；weather 亦为英文代码需映射，风速为蒲福等级，精度信息量不及 wttr |
| 中国天气网 d1.weather.com.cn | 未实测（需 Referer 伪造+GBK 解码+JS 变量剥离，`weather_index/{城市ID}.html` 含 alert 字段，城市 ID 需查表） | nmc 预警的备用源，但工程量/稳定性比差；仅在 nmc 失效时考虑 |
| 华风爱科 platform.weathercn.com | 未实测（需免费注册，每日 500 次） | 中国气象局×AccuWeather 官方：分钟级降水+灾害预警——**愿意注册 Key 时比和风更"官方"的中国方案** |

## 4. AI 函数设计（3 个 OneShot）

注册照官方模板：`OnAwake` 里 `RegisterHandler(new XmlHandler(this){...}, DocumentMode.Implicit, cancellationToken: DestroyCancellationToken)`；结果统一 `interactor.Poke()` 回给 AI。

### 4.1 `<query_weather city="" />`
- 缺省用默认城市。返回：天气现象（中文）、气温、体感、湿度、风、更新时间；附未来 3 小时段降水概率摘要。
- 有生效预警时追加一行"⚠ 当前有 N 条生效预警，可用 <query_warning/> 查看详情"。

### 4.2 `<query_forecast city="" days="3" />`
- `days` 夹取 1–3（wttr 源；qweather 源支持 7）。逐日：白天/夜间现象、最高最低温、降水概率、日出日落。

### 4.3 `<query_warning city="" />`
- nmc 源：该城市生效预警 `title`+发布时间+详情链接；qweather 源：含级别/类型/正文摘要。无预警如实返回。
- **三个查询函数的 `city` 参数即"查询其他城市"的接口**：任意城市名（中文/拼音/英文），wttr 源全球覆盖；预警源仅中国大陆。

### 4.4 `<set_default_city city="" />`
- AI 在用户说"我搬到杭州了 / 以后看杭州的天气"时调用：更新默认城市为 `city`，（默认城市天然在监控范围内，不追加 WatchCities）；
- 迁移后重置该城市预警基线（新城市首次拉取只记状态不播报），避免换城刷屏；
- 城市解析失败（wttr 查无此地）返回错误提示，不改配置。

## 5. 推送子系统：三级分发（核心）

### 5.1 分级规则

| 级别 | 通道 | 触发 | AI 侧效果 |
|---|---|---|---|
| **P0 立即推送** | `interactor.Poke`（唤醒 AI） | 预警新增/解除；规则引擎命中且规则 Level=push | AI 被唤醒，主动播报 |
| **P1 静默注入** | `ChatBot.EditChatHistory` 或 `interactor.Prompt` 替换常驻天气行 | 常规天气状态变化；规则 Level=silent 的命中 | **不打扰**：AI 下次推理时自然看到最新天气 |
| **P2 定点晨报** | `interactor.Poke` | 每日 `DailyBriefTime` | AI 被唤醒，做一日汇总播报 |

P0 与 P1 的边界一句话：**用户需要立刻知道的事才 Poke，其余一律静默更新上下文**。静默时段只作用于 P0（P2 定点跳过静默时段内的触发）；P1 注入照常进行（它不唤醒人）。

### 5.2 常驻天气上下文（P1 的实现）

上下文中常驻一条"当前天气"消息，随数据更新**原地替换**：

```
消息格式（内容前缀作为识别标记）：
[当前天气·WeatherModule] 北京市：晴 26°C（体感 27°C），湿度 42%，南偏南风 7km/h。
明日：多云转小雨 17~22°C，降水概率 65%。今日较昨日：最高温 -9°C（明显降温）。
当前生效预警 2 条（<query_warning/> 可查详情）。
（此行由天气服务自动维护，AI 可自然引用，无需回应本条。）

更新流程（变化检测后台任务内）：
1. 拉取新数据 → WeatherTexts 渲染"新天气行"
2. 与上次注入文本比对（内存 + StorageSystem 持久化），相同则跳过
3. 替换（两种通道，实现第一步用小实验定夺）：
   优先 interactor.Prompt()——框架常驻提示词机制，热重载自动卸载重注入；
   若重复调用为"追加"语义，则回退 ChatBot.EditChatHistory(thread => {
       var old = thread.ChatHistory.FirstOrDefault(m => m.Content?.StartsWith("[当前天气·WeatherModule]") == true);
       if (old != null) thread.ChatHistory.Remove(old);
       thread.ChatHistory.AddSystemMessage(newText);
   }, "更新天气上下文");   // 官方 Memory 插件已验证的写法
```

- **幂等**：OnStart 时同样走"查找→有则替换无则新增"（Memory 在 OnStart 装载记忆有先例），角色重载后自动恢复。
- **不唤醒 AI**——文本静躺上下文，AI 被搭话或被其他 Poke 唤醒时自然拿到最新天气。
- 同一轮检测的多条命中**合并为一次 Poke**（拼接为一条多段文本），一个唤醒说清所有事。

### 5.3 规则引擎（P0/P1 的统一判定，含自定义规则）

**规则模型（WeatherRule）**：

| 字段 | 说明 |
|---|---|
| Name | 规则名（唯一，作防抖键一部分） |
| Enabled | 启停 |
| City | 作用城市，空=所有监控城市 |
| Window | 数据窗口：`now`（实况）/ `today`（今日预报）/ `tomorrow`（明日）/ `intraday`（vs 当日基线）/ `day_over_day`（vs 昨日快照）/ `hourly_today`（今日逐时段） |
| Metric | 指标，必须来自当前源的数据面：`temp` `feels_like` `temp_max` `temp_min` `humidity` `wind_speed` `uv` `precip_mm` `precip_prob` `thunder_prob` `weather_code`（wttr 源；qweather 源另有扩展） |
| Op | `>` `>=` `<` `<=` `==` `changed`（数值=绝对变化量超阈值；`weather_code`=类别突变，如 晴↔雨/雪） |
| Threshold | 阈值（`changed` 时为变化量；`weather_code` 用类别枚举） |
| CooldownHours | 冷却时间（默认 24 = 当日一次），防抖键 `{日期}:{城市}:{规则名}`，持久化 |
| Level | `push`（P0）/ `silent`（P1） |
| MessageTemplate | 文案模板，占位符 `{city}` `{metric}` `{value}` `{prev}` `{threshold}` `{time}`，空=用默认模板 |

**内置默认规则集**（预置在规则列表里，UI 可见、可改阈值、可禁用，机制与自定义规则完全一致）：

| 规则 | Window+Metric+Op+Threshold | Level |
|---|---|---|
| 强降温/升温 | tomorrow · temp_max · changed · 8 | push |
| 降雨/雷暴临近 | hourly_today · precip_prob · >= · 60（thunder_prob ≥50% 合并提示） | push |
| 今日较昨日突变 | day_over_day · temp_max · changed · 6 | push |
| 当日累计变化 | intraday · temp · changed · 6 | push |

**引擎行为**：

- 每轮变化检测（60 分钟）拉取数据后，对每个监控城市逐一评价所有启用规则；
- 指标不在当前源数据面（如 wttr 无 AQI）→ 该规则跳过并在 UI 标灰 + 日志一次性告警，不崩溃；
- 非法 Op/空模板等在 UI 保存时校验，加载时二次校验，坏规则自动禁用不拖垮引擎；
- **预警冲突抑制**：官方预警 P0 推送当日已覆盖该城市时，仅内置的降雨/温差类默认规则降级为 silent（避免同一件事说两遍）；**用户自定义规则不受抑制**——显式定义的规则尊重用户；
- 现象类（weather_code）突变规则在防抖窗口内只报一次。
- **指标×窗口组合约束**：`precip_prob`/`thunder_prob` 只有逐时段数据，day 级窗口（today/tomorrow）下取该日 hourly 的最大值；日级指标（temp_max/temp_min）、now 级指标（temp/wind/uv 等）按窗口语义取数；非法组合（如 `now` 窗口配 `changed`——没有对比基准）在 UI 保存时拦截。

### 5.4 快照体系（规则引擎的地基）

wttr 无昨日数据，插件**自滚动维护三份快照**（StorageSystem，按角色隔离）：

| 快照 | 内容 | 更新时机 | 用途 |
|---|---|---|---|
| 当日基线 | 当日首次检测的实况摘要 | 每日首次检测时固定 | intraday 规则（修 router.js 渐变漏报：它只和上一轮比，每小时降 2°C 的强降温全天不触发） |
| 当日历次记录 | 每轮检测摘要的小数组（≤48 条） | 每轮追加 | 次日汇总成昨日快照 |
| 昨日快照 | 昨日 max/min/现象/降水汇总 | 次日首次检测时由历次记录汇总生成 | day_over_day 规则、晨报"较昨日" |

### 5.5 P0 预警监控流程

```
OnStart:  基线拉取——当前已生效预警记为"已播报"（AnnounceOnStart=true 时播报），避免启动刷屏
每 10 min: 拉取预警列表（nmc 一次全国 / qweather 逐监控城市）
          foreach 监控城市:
              新增 = 城市预警 − announced   → 命中推送
              解除 = announced − 城市预警   → NotifyOnClear 时命中
              announced = 城市预警
          静默时段内只更新 announced，时段结束后补报
```

- 去重键 `alertid`；状态经 `StorageSystem` 持久化（按角色隔离），重载/重启不重复播报。

### 5.6 降噪总表

| 策略 | 默认 | 说明 |
|---|---|---|
| 预警轮询间隔 | 10 分钟 | 下限 5 分钟；nmc 源每次 1 请求 |
| 变化检测间隔 | 60 分钟 | 与 wttr 数据节奏匹配（§3.5），无谓加密只会撞限流 |
| 启动静默 | 开 | 旧预警只记状态不播报 |
| 静默时段 | 关（可设 `23:00-07:00`） | **跨零点窗口解析**（23→7 视为同一段）；时段内 P0 只记录不 Poke，结束后补报 |
| 防抖 | CooldownHours（默认 24） | 每规则每城市冷却期内至多一次（现象来回摆动不重复推送） |
| 当日基线 | 每日 1 份 | 防"只和上一轮比"的渐变漏报（router.js 实战教训） |
| 同轮合并 | 开 | 一轮检测的多条命中合并为一次 Poke |
| 预警优先 | 开 | 仅作用于内置默认规则（§5.3） |

### 5.7 推送文本格式

```
[P0·预警] 北京市 · 大风蓝色预警（来自中央气象台）
发布：2026-09-26 16:01
标题：北京市气象台发布大风蓝色预警信号
详情：https://www.nmc.cn/...
请适时提醒用户注意安全，酌情安排出行建议。

[P0·强降温] 北京市
今日最高 28°C → 明日最高 17°C（降幅 11°C），且明天有雨。
请主动提醒用户添衣带伞。

[P2·晨报] 早上好，以下是今日天气：北京市晴转多云 19~26°C，
较昨日降温 5°C；今日 20 时后降水概率 40%；日落 18:03。当前无生效预警。
```

结尾一句为给 AI 的行为提示，让推送有"人格"而非弹公告。自定义规则的 MessageTemplate 渲染后嵌入同格式。

## 6. 自定义规则系统与配置 UI（新需求）

### 6.1 设计立场

规则不只是"高级功能"，而是插件的**核心抽象**：§5.3 的内置 4 条规则就是预置的默认规则集，用户在 UI 里看到的不是黑盒开关，而是和自定义规则完全同构、可改可停的规则条目。改阈值、关掉某条、加一条"风速 > 30 km/h 提醒关窗"，都是同一套机制。

### 6.2 UI 实现形态：纯 C# 组件（不用 razor）

规则是**动态列表**（增删改任意条），框架默认表单 UI 只适合扁平键值，做不了列表编辑，因此走自定义 EditorUI。实测结论（两次 UI 崩溃换来的教训）：**必须纯 C# 组件 + 手写 `BuildRenderTree`**，不写 .razor（razor 生成器与插件热编译冲突）；控件用原生 `input/select/textarea/button` + `value/checked` 属性 + `EventCallback.Factory.Create`，**禁用 @bind / @onclick 语法糖**（本宿主中实测会卡死渲染）。

- **UI 基类契约（实测自官方插件）**：双泛型 `ModuleUIBase<WeatherModule, WeatherConfig>`，基类直接提供强类型 `Configuration`（活对象）——UI 直接改 `Configuration.*`，落盘交给框架外层「保存配置」按钮；渲染开头必须判空兜底（`if (Configuration == null)`）；`Module` 仅在事件处理器中判空使用，**绝不在 OnInitialized 中解引用**（框架赋值时机在 OnInitialized 之后）；
- `WeatherModule` 打 `[Module(..., EditorUI = typeof(WeatherModuleUI))]`；
- 装饰元素与样式全部自带：`<style>` 注入全量 CSS（类名 `wx-` 前缀隔离），素材 `Assets/*.webp` 经 `LoadAsset` 转 data-URI 内嵌（三候选路径：`AlifePath.StorageFolderPath\Plugins\chuxin.Weather\Assets` → `AppContext.BaseDirectory\Plugins\...` → `BaseDirectory\Assets`），缺失时按 CSS 渐变兜底降级。

### 6.3 观云台 UI 规格（2026-09-26 冻结，概念稿 uitest/weather-ui-mockup.html）

**主题**：「观云台」（天空蓝 #0ea5e9 + 晨昏橙 #f59e0b），三段式结构（参考千瞳 VisionRouter 的门面→门闩→深色控制台骨架，内容全为天气意象）：

1. **开场动画（约 3.35s，点按任意处跳过）**：夜航（银河+流星+光尘）→ 等离子日轮自云海升起（黑底素材 `boot-orb.webp` + `mix-blend-mode:screen`，`wx-orb-rise` 从 118% 升至 20%）→ 破晓 + 铭牌「观 云 台」+ 进度条。C# 侧三态状态机 `_bootPhase`（0 播放 → 2750ms → 1 淡出 → +600ms → 2 移除），`prefers-reduced-motion` 时直接跳过。
2. **门面**：主视觉 `hero2.webp`（穹顶观星台+金光穿云，呼吸动画）+ 标题字幕（流光 `background-clip:text`）+ **状态轨** `rail.webp`（实时快照：默认城市实况/预警源/规则盯梢/和风备用 4 节点 + 三个指标块）+ **门闩**「⊙ 打开配置」。
3. **控制台**（展开态）：极光背景 + 流光扫描线标题 + 渐变描边毛玻璃面板（data-idx 01–04 水印），四分区侧栏：**总控**（默认城市/查询源/两总开关）、**数据源**（预警源/和风 Key 密文框/Host/监控城市/两间隔）、**提醒规则**（规则卡片列表 + 编辑器 + 模拟测试 + 恢复默认）、**晨报与免打扰**。分区切换只渲染当前 section（切走即销毁重建，`wx-panel-in` 动画自动重播）。

**实现红线**：不给会重排的元素 SetKey（WebView2 残影层，千瞳血泪）；动画时间轴用 CSS 变量 `--boot:2.7s` 统一延时；入场类 `.intro` 在门闩点开后移除、跳过时与 boot 一并移除；状态轨数据来自 `WeatherMonitor.GetRailStatus()`（锁内组装的只读快照：默认城市当日基线 + 心跳观测 + 规则计数，零网络）。

**素材清单**（agnes-image-2.5-flash 生成，8 张 webp 约 160KB）：`hero2.webp` 门面主视觉、`rail.webp` 状态轨背景、`boot-dawn/night/clouds/sun/orb/rays.webp` 开场动画分层（黑底 screen 混合免抠，云层切片加 mask 羽化）。

### 6.4 规则的存储与作用域

- 规则集存在 `WeatherConfig`（`List<WeatherRule>`），走配置系统：全局 `{存储目录}/Configuration/{模块全名}.json`，**角色级优先**（`{角色目录}/Configuration/`）——不同角色可挂不同规则集；
- UI 修改即写配置（配置系统自动 UI 联动），下一轮检测即生效，无需重载插件；
- 多角色注意：模块每角色一个实例，规则、快照、防抖键全部按角色隔离（§7）。

### 6.5 常规配置项总表

| 配置项 | 默认 | 说明 |
|---|---|---|
| QuerySource | `wttr` | `wttr` / `qweather` |
| WarningSource | `nmc` | `nmc` / `qweather` / `off`（off = 关闭预警监控） |
| QWeatherApiKey / QWeatherApiHost | "" / `https://devapi.qweather.com` | 切 qweather 源后生效 |
| DefaultCity | "北京" | 查询缺省城市；**监控范围 = 默认城市 ∪ WatchCities** |
| WatchCities | "" | 逗号分隔；预警与变化检测的监控范围；自定义规则的 City 必须属于该范围（UI 下拉只列监控城市并提示） |
| EnableWarningMonitor | true | 预警监控总开关 |
| PollIntervalMinutes | 10 | 预警轮询间隔，下限 5 |
| EnableChangeMonitor | true | 变化检测总开关（**晨报不依赖它**，见下） |
| ChangeCheckIntervalMinutes | 60 | 变化检测 = 规则评价频率，下限 30 |
| DailyBriefEnabled / DailyBriefTime | false / "07:30" | 晨报到点**自取一次数据**，独立于变化检测开关 |
| AnnounceOnStart | false | 启动时播报已生效预警 |
| NotifyOnClear | true | 预警解除提醒 |
| QuietHours | "" | 静默时段，跨零点可解析（§5.6） |

说明：内置 4 条规则的阈值（原 TempChangeThreshold 等）**不再作为顶层配置项**——它们是默认规则集实例的 `Threshold`，在 UI 规则列表里直接改（§6.1 的设计立场的自然结果）。

## 7. 生命周期与热重载要点（含实测修补）

> **运行时实测教训（2026-09-26 上线验证）**：
> ① **不依赖框架共享的 OnUpdate 驱动**——ChatActivity 的 UpdateAsync 循环中任何一个模块的异常都会**终止整个角色的更新循环，且只写 Console 不写日志文件**（框架源码 Alife.PluginContext/ChatActivity 实测）。本插件改用**自有心跳循环**：OnAwake 启动 `PeriodicTimer(5s)` + `DestroyCancellationToken` 的后台 Task，墙钟检查派发三类任务，与驱动健康度彻底解耦。
> ② **配置遮蔽**：角色级 `{角色目录}/Configuration/` 优先于全局——模块 OnAwake 的种子逻辑会把默认规则集写进**角色级**，之后 MCP `SetModuleConfig` 不带 characterName 写的全局配置**不生效**。调试时必须带 `characterName` 读写角色级。
> ③ 预警正文：nmc 列表接口无正文，详情页 `/publish/alarm/{alertid}.html` 的唯一 `<p>` 即完整正文（实测可提取），每轮推送/查询最多抓 5 条，带缓存与逐条容错。

- **OnUpdate 防重入后台化（审查修补）**：心跳只置"任务到期"标志并 `Task.Run` 启动后台执行（若上一轮未结束则跳过本轮），HTTP/规则评价全在后台任务里，每步检查 `DestroyCancellationToken`——慢请求不会卡心跳，重入不会叠加（Memory 插件的 `compressing` 标志同思路）。
- **多角色状态隔离（审查修补）**：模块实例每角色一份；StorageSystem 的键一律加 `Character.StorageKey` 前缀（参照 Memory 插件用 `Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, ...)` 的做法），快照/防抖键/已播报集合互不串扰；HttpClient 可共享。
- `HttpClient` 模块级单例（设 User-Agent），`OnDestroy` 释放；函数注册与后台任务挂 `DestroyCancellationToken`。
- `OnUpdate` 墙钟控频而非 `FrameCount` 取模——参照官方 TimedTask 写法。
- 状态锁：后台任务、查询函数、上下文替换并发访问 announced 集合/缓存/快照，统一 `lock`。
- 常驻天气行注入幂等，热重载后 OnStart 重走即恢复；规则集来自配置，热重载不丢。
- `QuietHours`/`DailyBriefTime` 解析统一走一个 `TimeWindow` 工具（处理跨零点与非法格式回退默认值）。

## 8. 部署与测试计划（本机文件路径）

1. 建插件目录落 manifest + 源文件；建 razor UI 项目并预编译出 `WeatherModuleUI_razor.g.cs` 平铺进去。
2. UI「系统管理/插件环境/同步环境」（或 MCP `ReloadPluginEnvironment`）→ `ReloadPlugin("Xiaoqian.Weather")`，盯日志 `[Error]` 修编译错。
3. **第一步小实验**：验证 `interactor.Prompt()` 重复调用语义（替换 or 追加），定 P1 通道。
4. **零配置先测查询**：对话"今天天气怎么样 / 这周会下雨吗"；`ReadCharacterContext` 确认常驻天气行已注入。
5. P1 静默验证：临时把变化检测间隔调到 1 分钟，确认天气行被替换、AI 未被唤醒。
6. P0 验证：阈值临时调低（温差 1°C、降水 10%），确认当日防抖只 Poke 一次、同轮合并为一条；预警用常发预警城市观察。
7. 规则 UI 验证：改内置规则阈值→生效；新建规则（如 wind_speed > 5 push）→"模拟测试"命中→真实触发；坏规则（非法指标）→保存拦截；禁用全部规则→无推送。
8. 快照链验证：手动改 StorageSystem 快照日期模拟昨日，验证 day_over_day 与晨报"较昨日"。
9. 多角色验证：两个角色启用模块，确认状态互不干扰。
10. 热重载验证：改代码 → ReloadPlugin → 函数无重复注册、后台任务无重复、规则集保留。

## 9. 能力路线图

**低成本高感知，推荐紧随 v1：**
- **空气质量 AQI**：nmc 有 `/f/rest/aqi/{stationid}`；并入查询返回与晨报，同时成为自定义规则新指标。
- **站点级精确预警**：nmc 站点实况接口 `warn` 字段若验证可用，按站点精确匹配预警（根治"朝阳区/朝阳市"重名）。
- **穿衣/出行建议行**、**日出日落/月相**（wttr astronomy 免费带）。

**Alife 特色方向：**
- **天气驱动角色状态**：P1 常驻天气行已是雏形，进一步把预警/强变化写成角色情绪提示注入；配合官方 `Alife.Function.VirtualWorld`。
- **Skill 形态**：`{存储目录}/Skills/天气/SKILL.md` 教 AI 何时主动看天气、怎么自然播报。
- **城市随行**：`<set_watch_city city="" />` 函数——用户说"我最近在杭州"时 AI 直接迁移监控与默认城市（借鉴 router.js 由 LLM 更新 location.txt 的机制，走 `ConfigurationSystem` 落配置）。

**数据增强：**
- Open-Meteo 历史对比（archive API）；小时级 nowcast 与自维护城市库**不建议做**。

## 10. 风险与开放问题

- **nmc 接口非官方承诺**（数据为中央气象台官方）：结构变更检测与降级已设计（§3.2）；失效可切 `WarningSource=qweather`。
- **wttr.in 准确度中等且限流**：轮询节奏已按其数据频率校准；对准确度敏感的用户可切 QWeather。
- **城市名匹配**（nmc 源）：title Contains 匹配，区县级预警需把区名加入 WatchCities；站点级匹配是根治方案（路线图）。
- **变化提醒误报**：wttr 预报有偏差，阈值默认保守 + 防抖 + 静默时段兜底；上线后按体感调参——规则 UI 让调参变成用户自己就能做的事。
- **P1 注入与记忆压缩共存**：`Prompt()` 通道若确认可用则天然规避（常驻内容在提示词区不在历史）；`EditChatHistory` 回退方案可能被 Memory 压缩机制处理——实现阶段验证，必要时提高注入频率补偿。
- **razor UI 构建链**：需要本地 .NET SDK 预编译 g.cs，改 UI 后要重编译再同步插件——开发期稍繁琐，产物结构官方已验证可行。
- **待确认**：① 插件 ID 是否从 `Xiaoqian.Weather` 统一改为 `chuxin.*`（与 chuxin.TokenStats 一致）？② 默认城市设哪？（配置项，不阻塞开发）
