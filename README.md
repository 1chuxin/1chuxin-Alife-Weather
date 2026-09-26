# 观云台 · chuxin.Weather

[Alife](https://github.com/BDFFZI/Alife) 的天气服务插件：零配置可用，AI 角色能查天气、盯预警、按自定义规则提醒天气变化，并在对话上下文中常驻一行当前天气。

## 功能

- **天气查询**：`query_weather` / `query_forecast`，默认 wttr.in（免 Key），可切换和风天气
- **灾害预警**：`query_warning`，中央气象台（免 Key）/ 和风天气，新预警主动推送
- **变化提醒规则**：内置 4 条默认规则（强降温 / 降雨概率 / 现象突变 / 今昨对比），可增删改，支持数据窗口、指标、操作符、阈值、冷却时间与自定义文案模板；UI 内一键模拟测试
- **三级分发**：强天气剧变推送（Poke 唤醒 AI 播报）；细微变化静默写入常驻天气上下文；静默时段内只记录、结束后补报
- **每日晨报**：每天定时推送一次天气汇总
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
