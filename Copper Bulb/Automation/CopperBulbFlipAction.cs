using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;

namespace Copper_Bulb.Automation;

/// <summary>
/// “翻转铜灯”行动的设置：以唯一名称指定要翻转的铜灯。
/// </summary>
public class CopperBulbFlipActionSettings
{
    /// <summary>
    /// 目标铜灯的名称（全局唯一）。
    /// </summary>
    public string BulbName { get; set; } = "";
}

/// <summary>
/// 自动化行动：使指定名称的铜灯状态翻转（点亮变熄灭、熄灭变点亮）。
/// </summary>
[ActionInfo(FlipActionId, "翻转铜灯", "\uEA63")]
public class CopperBulbFlipAction : ActionBase<CopperBulbFlipActionSettings>
{
    /// <summary>
    /// “翻转铜灯”行动的 ID。
    /// </summary>
    public const string FlipActionId = "copper_bulb.flip";

    protected override Task OnInvoke()
    {
        // 按名称定位铜灯并翻转；名称为空或找不到时静默跳过。
        var inst = CopperBulbService.Instance;
        if (inst != null && Settings != null)
            inst.FlipByName(Settings.BulbName);
        return Task.CompletedTask;
    }
}
