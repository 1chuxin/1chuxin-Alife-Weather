# 观云台 · chuxin.Weather

[Alife](https://github.com/BDFFZI/Alife) 的天气服务插件：零配置可用，AI 角色能查天气、盯预警、按自定义规则提醒天气变化，并在对话上下文中常驻一行当前天气。

## 功能

- **天气查询**：`query_weather` / `query_forecast`，默认 wttr.in（免 Key），可切换和风天气；配 Key 后接入和风新一代接口（v1 为主、v7 自动回退）
- **全量数据域（和风免费组）**：逐小时预报 `query_hourly`、分钟级降雨 `query_rain`（约几分钟开始下雨，仅中国城市）、空气质量 `query_air`、生活指数 `query_indices`、日出日落与月相 `query_astro`、历史天气 `query_history`
- **统一缓存**：AI 查询 3 分钟缓存、各数据域按更新频率分档缓存，单飞并发去重；配置 UI 显示本月实测请求量（免费额度 50,000 次/月）
- **灾害预警**：`query_warning`，中央气象台（免 Key）/ 和风天气，新预警主动推送；支持预警变更归并（supersedes）与过期过滤
- **变化提醒规则**：内置 6 条默认规则（强降温 / 降雨概率 / 今昨温变 / 当日累计变化 / 现象突变×2），可增删改，支持数据窗口、指标（含 AQI/紫外线/降水量/降水概率）、操作符、阈值、冷却时间与自定义文案模板；UI 内一键模拟测试
- **临近降雨提醒**：未来数小时可能降雨时用分钟级降水精确提醒「约 X 分钟后开始下雨」（触发式，不下雨不耗请求）
- **三级分发**：强天气剧变推送（Poke 唤醒 AI 播报）；细微变化静默写入常驻天气上下文；静默时段内只记录、结束后补报
- **每日晨报**：天气汇总 + 空气质量 + 穿衣建议 + 月相
- **观云台 UI**：开场动画、实时状态轨、四分区控制台（总控 / 数据源 / 提醒规则 / 晨报免打扰）
- **角色级隔离**：不同角色可挂不同城市、规则集与配置

## 安装

在 Alife 客户端「插件市场」搜索 **天气服务（观云台）** 安装；或从 [Releases](https://github.com/1chuxin/1chuxin-Alife-Weather/releases) 下载 zip 手动放入 `Storage/Plugins/chuxin.Weather/`。

零配置即可使用（wttr.in + 中央气象台）；如需和风天气，在配置 UI「数据源」分区填入自己的 API Key。

## 说明

- 开场动画素材由 agnes-image-2.5-flash 生成
- 数据来源：[wttr.in](https://wttr.in) · [中央气象台 nmc.cn](https://www.nmc.cn) · [和风天气](https://dev.qweather.com)

## License

MIT
