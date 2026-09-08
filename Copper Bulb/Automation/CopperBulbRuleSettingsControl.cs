using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Controls.Ruleset;

namespace Copper_Bulb.Automation;

/// <summary>
/// “铜灯”条件的设置界面：内嵌一个规则集编辑器，
/// 用于添加铜灯内部的其他自动化条件。
/// </summary>
public class CopperBulbRuleSettingsControl : RuleSettingsControlBase<CopperBulbRuleSettings>
{
    private readonly RulesetControl _rulesetControl = new() { ShowTitle = false };

    public CopperBulbRuleSettingsControl()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        panel.Children.Add(new TextBlock
        {
            Text = "铜灯内部条件（内部条件从不满足变为满足时，铜灯状态翻转）："
        });
        panel.Children.Add(_rulesetControl);

        Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (Settings == null) return;
        _rulesetControl.Ruleset = Settings.InternalRuleset;
    }
}
