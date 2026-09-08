using System.Text.Json;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Ruleset;
using ClassIsland.Shared;
using Microsoft.Extensions.Hosting;

namespace Copper_Bulb.Automation;

/// <summary>
/// 铜灯条件服务：周期性轮询所有“铜灯”内部的规则集，
/// 当内部条件的判定结果发生变化时，像 T 触发器一样翻转铜灯的点亮状态。
/// </summary>
public class CopperBulbService : IHostedService
{
    /// <summary>
    /// “铜灯”规则的 ID。
    /// </summary>
    public const string RuleId = "copper_bulb.bulb";

    /// <summary>
    /// 服务单例，供规则处理程序与设置界面访问。
    /// </summary>
    public static CopperBulbService? Instance { get; private set; }

    private readonly Dictionary<Guid, CopperBulbRuleSettings> _bulbs = new();
    private DispatcherTimer? _timer;
    private IRulesetService? _rulesetService;
    private IAutomationService? _automationService;

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
            Poll();
        }
        catch
        {
            // 轮询失败时静默，等待下一次时钟。
        }
    }

    /// <summary>
    /// 扫描自动化工作流（含铜灯内嵌规则集），收集所有铜灯设置实例
    /// （并把 JsonElement 反序列化回活对象以便持久化状态）。
    /// </summary>
    private void Scan()
    {
        var found = new HashSet<CopperBulbRuleSettings>(ReferenceEqualityComparer.Instance);

        foreach (var workflow in _automationService!.Workflows)
        {
            ScanRuleset(workflow.Ruleset, found);
        }

        foreach (var (id, settings) in _bulbs.ToList())
        {
            if (!found.Contains(settings))
                _bulbs.Remove(id);
        }
    }

    private void ScanRuleset(Ruleset ruleset, HashSet<CopperBulbRuleSettings> found)
    {
        foreach (var group in ruleset.Groups)
        {
            foreach (var rule in group.Rules)
            {
                if (rule.Id != RuleId) continue;
                var settings = EnsureLiveSettings(rule);
                if (settings == null) continue;
                RegisterBulb(settings, found);
                // 递归扫描铜灯内部的条件，支持铜灯嵌套。
                ScanRuleset(settings.InternalRuleset, found);
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

    private void RegisterBulb(CopperBulbRuleSettings s, HashSet<CopperBulbRuleSettings> found)
    {
        found.Add(s);
        if (_bulbs.TryGetValue(s.Id, out var existing))
        {
            if (ReferenceEquals(existing, s)) return;
            // 复制规则组会导致两份设置共享同一 Id，为后到者重新分配，避免双重翻转。
            s.Id = Guid.NewGuid();
        }

        _bulbs[s.Id] = s;
    }

    /// <summary>
    /// 轮询每个铜灯的内部条件：首次只同步基线；之后仅当内部条件
    /// 从不满足变为满足（上升沿）时翻转点亮状态，从满足变为不满足时不翻转。
    /// </summary>
    private void Poll()
    {
        var flipped = false;
        foreach (var s in _bulbs.Values.ToList())
        {
            var inner = _rulesetService!.IsRulesetSatisfied(s.InternalRuleset);
            if (s.LastInnerState == null)
            {
                s.LastInnerState = inner;
                continue;
            }

            var prev = s.LastInnerState.Value;
            s.LastInnerState = inner;
            if (prev || !inner) continue;
            s.IsOn = !s.IsOn;
            flipped = true;
        }

        if (flipped)
        {
            _automationService!.SaveConfig("铜灯状态改变。");
            _rulesetService!.NotifyStatusChanged();
        }
    }

    /// <summary>
    /// 读取铜灯当前点亮状态。优先取运行时注册表中的活对象，回退到设置中保存的值。
    /// </summary>
    public static bool GetIsOn(CopperBulbRuleSettings s)
    {
        var inst = Instance;
        if (inst != null)
        {
            lock (inst._bulbs)
            {
                if (inst._bulbs.TryGetValue(s.Id, out var live))
                    return live.IsOn;
            }
        }

        return s.IsOn;
    }
}
