using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Controls.Ruleset;

namespace Copper_Bulb.Automation;

/// <summary>
/// “铜灯”条件的设置界面：内嵌一个规则集编辑器，
/// 用于添加铜灯内部的其他自动化条件。
/// </summary>
public class CopperBulbRuleSettingsControl : RuleSettingsControlBase<CopperBulbRuleSettings>
{
    /// <summary>
    /// 深色主题下 FluentAvalonia 的卡片背景随嵌套逐层变白，超过约 32 层后
    /// 背景已接近纯白、浅色文字不可读。超过该层级的铜灯文本改为黑色，
    /// 较浅层不受影响。
    /// </summary>
    private const int DarkTextNestingThreshold = 32;

    private readonly TextBox _nameBox;
    private readonly TextBlock _nameErrorText;
    private readonly TextBlock _titleText;
    private readonly RulesetControl _rulesetControl = new() { ShowTitle = false };

    public CopperBulbRuleSettingsControl()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // 名称行：每个铜灯拥有全局唯一的名称，默认 "新铜灯 {N}"。
        _nameErrorText = new TextBlock
        {
            Text = "名称不能为空且不可与其他铜灯重名",
            IsVisible = false,
            Foreground = Brushes.Red
        };

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        nameRow.Children.Add(new TextBlock
        {
            Text = "名称",
            VerticalAlignment = VerticalAlignment.Center
        });
        _nameBox = new TextBox
        {
            Watermark = "新铜灯",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _nameBox.TextChanged += (_, _) => _nameErrorText.IsVisible = false;
        _nameBox.LostFocus += OnNameLostFocus;
        nameRow.Children.Add(_nameBox);
        panel.Children.Add(nameRow);
        panel.Children.Add(_nameErrorText);

        _titleText = new TextBlock
        {
            Text = "铜灯内部条件（内部条件从不满足变为满足时，铜灯状态翻转）："
        };
        panel.Children.Add(_titleText);
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

    /// <summary>
    /// 名称失焦时校验：空名或重名则还原为上一个有效名称并提示。
    /// </summary>
    private void OnNameLostFocus(object? sender, RoutedEventArgs e)
    {
        var s = Settings;
        var inst = CopperBulbService.Instance;
        if (s == null || inst == null) return;

        if (inst.TryRename(s, _nameBox.Text ?? ""))
        {
            _nameErrorText.IsVisible = false;
            return;
        }

        // 校验失败：还原显示并提示。
        _nameBox.Text = s.Name;
        _nameErrorText.IsVisible = true;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (Settings == null) return;
        _rulesetControl.Ruleset = Settings.InternalRuleset;

        // 注册活对象并补分配默认名称（旧配置或新建时 Name 为空）。
        var inst = CopperBulbService.Instance;
        if (inst != null)
        {
            inst.Register(Settings);
            var name = inst.EnsureName(Settings);
            if (_nameBox.Text != name)
                _nameBox.Text = name;
        }
        else if (!string.IsNullOrEmpty(Settings.Name))
        {
            _nameBox.Text = Settings.Name;
        }

        var depth = NestingDepth();

        // 在标题中显示本卡片所在的嵌套深度（最外层铜灯为 1，向内逐层加 1）。
        _titleText.Text = $"铜灯内部条件（嵌套深度 {depth + 1}）（内部条件从不满足变为满足时，铜灯状态翻转）：";

        // 深色主题且嵌套超过 32 层时，卡片背景已接近纯白，浅色文字不可读，
        // 将本层铜灯的文本设为黑色；浅层铜灯不做任何改动。
        // 本地值优先级高于主题样式，确保覆盖 FluentAvalonia 的文字颜色。
        var isDark = TopLevel.GetTopLevel(this)?.ActualThemeVariant == ThemeVariant.Dark;
        if (isDark && depth >= DarkTextNestingThreshold)
            _titleText.Foreground = Brushes.Black;
    }

    /// <summary>
    /// 本控件在铜灯嵌套中的层级：沿可视树向上统计祖先“铜灯”设置控件的数量。
    /// 最外层铜灯为 0，每向内嵌套一层加 1。
    /// </summary>
    private int NestingDepth()
    {
        var depth = 0;
        Control? cur = this;
        while (cur != null)
        {
            cur = cur.Parent as Control;
            if (cur is CopperBulbRuleSettingsControl)
                depth++;
        }
        return depth;
    }
}
