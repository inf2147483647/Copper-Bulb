using System.Text.Json;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Ruleset;
using ClassIsland.Shared;
using Microsoft.Extensions.Hosting;

namespace Copper_Bulb.Automation;

/// <summary>
    /// 铜灯条件服务：驱动计时器做扫描与持久化；
    /// 铜灯状态的边沿翻转统一在规则处理程序 <see cref="Handle"/> 中完成，
    /// 以便任意承载规则集的地方（组件隐藏条件、窗口规则、自动化工作流等）
    /// 都能在宿主求值时检测内部条件边沿并翻转。
    /// </summary>
    public class CopperBulbService : IHostedService
{
    /// <summary>
    /// "铜灯"规则的 ID。
    /// </summary>
    public const string RuleId = "copper_bulb.bulb";

    /// <summary>
    /// 服务单例，供规则处理程序与设置界面访问。
    /// </summary>
    public static CopperBulbService? Instance { get; private set; }

    private readonly Dictionary<Guid, CopperBulbRuleSettings> _bulbs = new();
    private readonly object _bulbsLock = new();
    private DispatcherTimer? _timer;
    private IRulesetService? _rulesetService;
    private IAutomationService? _automationService;
    private bool _dirty;
    [ThreadStatic] private static int _evalDepth;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Instance = this;
        // 不在构造函数中注入服务，避免提前解析 IAutomationService。
        // 若在 ExtraIsland 等插件的 ServicesFetcher 注入服务之前就构造 AutomationService，
        // 会触发其触发器 Loaded() 并因宿主服务尚未就绪而崩溃。
        // 改为在定时器首次 tick 时懒解析（届时所有后台服务均已启动）。
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
            _timer.Start();
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
                _timer = null;
            }
        });

        return Task.CompletedTask;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            _rulesetService ??= IAppHost.TryGetService<IRulesetService>();
            _automationService ??= IAppHost.TryGetService<IAutomationService>();
            if (_rulesetService == null || _automationService == null)
                return;

            Scan();
            FlushDirty();
        }
        catch
        {
            // 轮询失败时静默，等待下一次时钟。
        }
    }

    /// <summary>
    /// 扫描自动化工作流（含铜灯内嵌规则集），把 JsonElement 设置反序列化为活对象，
    /// 便于在保存自动化配置时把点亮状态一并持久化。
    /// </summary>
    private void Scan()
    {
        foreach (var workflow in _automationService!.Workflows)
        {
            ScanRuleset(workflow.Ruleset);
        }
    }

    private void ScanRuleset(Ruleset ruleset)
    {
        foreach (var group in ruleset.Groups)
        {
            foreach (var rule in group.Rules)
            {
                if (rule.Id != RuleId) continue;
                var settings = EnsureLiveSettings(rule);
                if (settings == null) continue;
                RegisterBulb(settings);
                // 递归扫描铜灯内部的条件，支持铜灯嵌套。
                ScanRuleset(settings.InternalRuleset);
            }
        }
    }

    private static CopperBulbRuleSettings? EnsureLiveSettings(Rule rule)
    {
        switch (rule.Settings)
        {
            case CopperBulbRuleSettings s:
                return s;
            case JsonElement json:
            {
                CopperBulbRuleSettings? s;
                try
                {
                    s = json.Deserialize<CopperBulbRuleSettings>();
                }
                catch
                {
                    return null;
                }

                if (s == null) return null;
                if (s.Id == Guid.Empty) s.Id = Guid.NewGuid();
                rule.Settings = s;
                return s;
            }
            default:
            {
                var fresh = new CopperBulbRuleSettings();
                rule.Settings = fresh;
                return fresh;
            }
        }
    }

    private void RegisterBulb(CopperBulbRuleSettings s)
    {
        lock (_bulbsLock)
        {
            if (_bulbs.TryGetValue(s.Id, out var existing))
            {
                if (ReferenceEquals(existing, s)) return;
                // 复制规则组会导致两份设置共享同一 Id，为后到者重新分配，避免双重翻转。
                s.Id = Guid.NewGuid();
            }
            _bulbs[s.Id] = s;
        }
    }

    /// <summary>
    /// 取（或注册）某个 Id 对应的稳定活对象，供任意规则集场景复用锁存状态。
    /// </summary>
    private CopperBulbRuleSettings GetOrAddLive(CopperBulbRuleSettings s)
    {
        lock (_bulbsLock)
        {
            if (_bulbs.TryGetValue(s.Id, out var existing))
                return existing;
            _bulbs[s.Id] = s;
            return s;
        }
    }

    private void MarkDirty() => _dirty = true;

    /// <summary>
    /// 把“已翻转”状态写回自动化配置并通知宿主刷新（由 1 秒计时段触发，
    /// 避免在规则求值过程中调用 SaveConfig 造成重入问题）。
    /// </summary>
    private void FlushDirty()
    {
        if (!_dirty || _automationService == null || _rulesetService == null) return;
        _dirty = false;
        try
        {
            _automationService.SaveConfig("铜灯状态改变。");
            _rulesetService.NotifyStatusChanged();
        }
        catch
        {
            _dirty = true; // 下个 tick 重试
        }
    }

    /// <summary>
    /// "铜灯"规则处理程序：每次宿主求值承载它的规则集时会被调用——
    /// 先求值内部规则集（同时刷新内部条件的状态指示点），
    /// 再检测内部条件“不满足→满足”的上升沿并翻转点亮状态，
    /// 最后返回铜灯当前点亮状态。
    /// 这样在任何规则集场景下都能按 T 触发器语义工作。
    /// </summary>
    public static bool Handle(object? settings)
    {
        if (settings is not CopperBulbRuleSettings s) return false;
        if (++_evalDepth > 64) // 防御极端嵌套/自引用
        {
            _evalDepth--;
            return s.IsOn;
        }
        try
        {
            var inst = Instance;
            if (inst == null) return s.IsOn;

            var rs = inst._rulesetService ?? IAppHost.TryGetService<IRulesetService>();
            if (rs == null) return GetIsOnValue(s);

            var live = inst.GetOrAddLive(s);
            var inner = rs.IsRulesetSatisfied(live.InternalRuleset);

            if (live.LastInnerState == null)
            {
                live.LastInnerState = inner; // 首次只建立基线，不翻转
            }
            else
            {
                var prev = live.LastInnerState.Value;
                live.LastInnerState = inner;
                if (!prev && inner) // 上升沿：不满足→满足
                {
                    live.IsOn = !live.IsOn;
                    inst.MarkDirty();
                }
            }

            return live.IsOn;
        }
        finally
        {
            _evalDepth--;
        }
    }

    private static bool GetIsOnValue(CopperBulbRuleSettings s)
    {
        var inst = Instance;
        if (inst != null)
        {
            lock (inst._bulbsLock)
            {
                if (inst._bulbs.TryGetValue(s.Id, out var live))
                    return live.IsOn;
            }
        }

        return s.IsOn;
    }
}
