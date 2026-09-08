using ClassIsland.Core.Models.Ruleset;

namespace Copper_Bulb.Automation;

/// <summary>
/// “铜灯”条件的设置。
/// 铜灯像一个由红石控制的 T 触发器：当内部条件从不满足变为满足时，
/// 铜灯在点亮和熄灭两种状态之间切换一次，并保持新状态直到下次被触发；
/// 内部条件从满足变为不满足时不翻转。
/// </summary>
public class CopperBulbRuleSettings
{
    /// <summary>
    /// 铜灯的稳定标识，用于在运行时定位锁存状态。
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 铜灯内部的判定条件（规则集）。
    /// </summary>
    public Ruleset InternalRuleset { get; set; } = new();

    /// <summary>
    /// 铜灯当前是否点亮（T 触发器的锁存状态）。
    /// </summary>
    public bool IsOn { get; set; }

    /// <summary>
    /// 上一次内部条件的判定结果，用于检测变化（边沿触发）。
    /// null 表示尚未初始化，首次判定时只同步、不切换。
    /// </summary>
    public bool? LastInnerState { get; set; }
}
