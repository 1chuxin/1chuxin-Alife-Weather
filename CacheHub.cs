using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace chuxin.Weather;

/// <summary>
/// 统一缓存中枢（DESIGN.md §11.2）。上游更新频率（实况 10min 级、逐时/逐日小时级、AQI 小时级、指数每日）
/// 都远慢于询问频率，缓存是额度安全与查询响应的根基：
/// - 进程内共享（多角色同城市复用同一份数据），键 = 领域+城市+参数；
/// - 单飞并发去重：同键并发只放一个请求，其余等同一任务；
/// - TTL 分档：AI 查询路径统一 3 分钟，后台路径按数据域各自的节拍；
/// - 实测计数：按"月:领域"累计真实发出的请求数，供 UI 显示额度用量。
/// </summary>
public static class CacheHub
{
    // ── 各数据域 TTL（DESIGN.md §11.2/§11.4）──
    public static readonly TimeSpan QueryTtl = TimeSpan.FromMinutes(3);      // AI 查询路径统一 3 分钟
    public static readonly TimeSpan WarningTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AqiTtl = TimeSpan.FromHours(2);
    public static readonly TimeSpan IndicesTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan HistoryTtl = TimeSpan.FromHours(12);
    public static readonly TimeSpan AstroTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinutelyTtl = TimeSpan.FromMinutes(30);  // 触发式拉取冷却
    public static readonly TimeSpan GeoTtl = TimeSpan.FromDays(365);         // 城市→ID/坐标 基本不变

    sealed class Entry
    {
        public DateTime At;
        public Task<object?> Task = null!;
    }

    static readonly ConcurrentDictionary<string, Entry> Map = new();
    static readonly ConcurrentDictionary<string, int> Counts = new();   // "yyyyMM:领域" → 次数

    /// <summary>
    /// 取缓存或在过期时加载。同键并发只执行一次 loader；loader 失败的条目会被移除以便重试。
    /// 关键约束：过期条目必须先被原子移除、loader 必须在抢注成功后才发起——
    /// 否则 TryAdd 撞上旧条目会形成"发起请求→白扔→再发起"的自旋（真实事故，2026-09-27）。
    /// </summary>
    public static async Task<T> GetOrLoadAsync<T>(string key, TimeSpan ttl, Func<Task<T>> loader, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Map.TryGetValue(key, out var hit) && DateTime.Now - hit.At < ttl)
                return (T)(await hit.Task.ConfigureAwait(false))!;

            // 过期/缺失：仅当表里仍是刚看到的这个条目时才原子移除（不误删并发放上的新条目）
            if (hit != null)
                ((ICollection<KeyValuePair<string, Entry>>)Map).Remove(KeyValuePair.Create(key, hit));

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fresh = new Entry { At = DateTime.Now, Task = tcs.Task };
            if (!Map.TryAdd(key, fresh)) continue;   // 并发竞争：胜者已放入新条目，下轮循环命中它

            _ = RunCounted(key, loader, tcs, fresh);   // 抢注成功才发起 loader；结果/异常经 tcs 广播给所有等待者
            try
            {
                return (T)(await tcs.Task.ConfigureAwait(false))!;
            }
            catch
            {
                // 失败条目移除（仅当仍指向本次任务），下一次调用可重试
                if (Map.TryGetValue(key, out var cur) && ReferenceEquals(cur, fresh))
                    Map.TryRemove(key, out _);
                throw;
            }
        }
    }

    static async Task RunCounted<T>(string key, Func<Task<T>> loader, TaskCompletionSource<object?> tcs, Entry fresh)
    {
        try
        {
            Count(key);
            var value = (object?)await loader().ConfigureAwait(false);
            // TTL 按完成时刻起算：若按发起时刻，长加载（v1 三连请求+降级链）会在条目刚完成时即被判过期，
            // 单飞窗口被截断、缓存寿命被加载时长侵蚀
            fresh.At = DateTime.Now;
            tcs.SetResult(value);
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
        }
    }

    static void Count(string key)
    {
        int sep = key.IndexOf(':');
        string domain = sep > 0 ? key[..sep] : key;
        Counts.AddOrUpdate($"{DateTime.Now:yyyyMM}:{domain}", 1, (_, n) => n + 1);
        PruneIfNeeded();
    }

    static int _adds;

    static void PruneIfNeeded()
    {
        if (Interlocked.Increment(ref _adds) % 64 != 0 || Map.Count < 128) return;
        var stale = Map.Where(kv => !kv.Key.StartsWith("geo:", StringComparison.Ordinal)
                                    && DateTime.Now - kv.Value.At > TimeSpan.FromHours(25))
                       .Select(kv => kv.Key).ToList();
        foreach (var k in stale) Map.TryRemove(k, out _);
    }

    /// <summary>本月实测请求总数（自然月，全部领域之和）。</summary>
    public static int MonthRequests()
    {
        string month = DateTime.Now.ToString("yyyyMM");
        return Counts.Where(kv => kv.Key.StartsWith(month + ":", StringComparison.Ordinal)).Sum(kv => kv.Value);
    }

    /// <summary>本月各领域实测请求数（UI 显示用）。</summary>
    public static Dictionary<string, int> MonthByDomain()
    {
        string month = DateTime.Now.ToString("yyyyMM");
        return Counts.Where(kv => kv.Key.StartsWith(month + ":", StringComparison.Ordinal))
                     .ToDictionary(kv => kv.Key[(month.Length + 1)..], kv => kv.Value);
    }
}
