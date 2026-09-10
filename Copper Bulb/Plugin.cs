using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Copper_Bulb.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Copper_Bulb;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 给铜灯服务注入插件私有配置目录，用于跨重启持久化锁存状态（与宿主容器无关）。
        CopperBulbService.PluginConfigFolder = PluginConfigFolder;

        // “铜灯”条件：内部条件从不满足变为满足时，像 T 触发器一样翻转点亮/熄灭状态；变回不满足时不翻转。
        services.AddRule<CopperBulbRuleSettings, CopperBulbRuleSettingsControl>(
            CopperBulbService.RuleId,
            "铜灯",
            "\uEA63",
            CopperBulbService.Handle);

        // “翻转铜灯”行动：按唯一名称指定铜灯，触发时翻转其点亮状态。
        services.AddAction<CopperBulbFlipAction, CopperBulbFlipActionSettingsControl>();

        services.AddHostedService<CopperBulbService>();
    }
}
