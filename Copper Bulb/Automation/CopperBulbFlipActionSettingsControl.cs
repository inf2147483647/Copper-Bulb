using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;

namespace Copper_Bulb.Automation;

/// <summary>
/// “翻转铜灯”行动的设置界面：输入 + 下拉搜索框指定要翻转的铜灯（按唯一名称）。
/// </summary>
public class CopperBulbFlipActionSettingsControl : ActionSettingsControlBase<CopperBulbFlipActionSettings>
{
    private readonly AutoCompleteBox _nameBox;
    private readonly TextBlock _errorText;

    public CopperBulbFlipActionSettingsControl()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        panel.Children.Add(new TextBlock
        {
            Text = "要翻转的铜灯名称："
        });

        _nameBox = new AutoCompleteBox
        {
            Watermark = "输入或从下拉列表中选择铜灯名称",
            MinimumPrefixLength = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _nameBox.TextChanged += OnNameTextChanged;
        _nameBox.LostFocus += OnNameLostFocus;
        panel.Children.Add(_nameBox);

        _errorText = new TextBlock
        {
            Text = "未找到该名称的铜灯（请确认铜灯已在自动化中创建并命名）",
            IsVisible = false,
            Foreground = Brushes.Red
        };
        panel.Children.Add(_errorText);

        Content = panel;

        Loaded += OnLoaded;
    }

    /// <summary>
    /// 文本变化时把名称写回行动设置（宿主据此持久化行动项），并清除错误提示。
    /// </summary>
    private void OnNameTextChanged(object? sender, TextChangedEventArgs e)
    {
        _errorText.IsVisible = false;
        try
        {
            Settings.BulbName = _nameBox.Text?.Trim() ?? "";
        }
        catch
        {
            // 设置尚未就绪（控件刚构造、行动项未绑定），忽略本次同步。
        }
    }

    /// <summary>
    /// 每次打开/新建时刷新下拉候选：列出当前所有已注册铜灯的名称。
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _nameBox.ItemsSource = CopperBulbService.Instance?.BulbNames();

        var name = Settings.BulbName;
        if (!string.IsNullOrEmpty(name) && _nameBox.Text != name)
            _nameBox.Text = name;
    }

    /// <summary>
    /// 失焦时校验名称是否真实存在；不存在则提示（保留用户输入便于修改）。
    /// </summary>
    private void OnNameLostFocus(object? sender, RoutedEventArgs e)
    {
        var inst = CopperBulbService.Instance;
        var text = _nameBox.Text?.Trim() ?? "";

        if (inst == null || text.Length == 0)
        {
            _errorText.IsVisible = false;
            return;
        }

        _errorText.IsVisible = !inst.BulbNames().Contains(text, StringComparer.Ordinal);
    }
}
