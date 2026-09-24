using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MouseBatteryTray;

/// <summary>
/// 亚克力卡片的共用构建块。
///
/// 从 wpf/MouseBatteryDetailsWindow.cs 抽出，供完整版与多品牌版共用 ——
/// 这些控件（动画进度条、电池图标、分组标题、指标行）经过多轮截图核对，
/// 各自的尺寸、动画时长与配色都有讲究，重写一遍只会丢掉这些细节。
///
/// 全部是静态工厂：只产生视觉元素，不持有状态。
/// </summary>
public static class DeviceCardWidgets
{
    /// <summary>细腻的横向进度条，挂载后从 0 平滑展开。</summary>
    public static Border CreateAnimatedProgressBar(int percent, System.Windows.Media.Brush accent)
    {
        var track = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = ThemeService.TrackBackgroundBrush,
            Margin = new Thickness(0, 4, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var fill = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
        track.Child = fill;

        track.Loaded += (_, _) =>
        {
            double available = track.ActualWidth;
            if (available <= 0) return;
            double target = available * Math.Clamp(percent / 100.0, 0, 1);
            fill.BeginAnimation(System.Windows.FrameworkElement.WidthProperty, new DoubleAnimation
            {
                From = 0,
                To = target,
                Duration = TimeSpan.FromMilliseconds(420),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        };

        return track;
    }

    /// <summary>横向电池图标（外壳 + 按 10% 量化的填充 + 充电闪电）。</summary>
    public static StackPanel CreateHorizontalBatteryIcon(int percent, bool isCharging,
                                                         System.Windows.Media.Brush accent)
    {
        var container = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        var shell = new Border
        {
            Width = 27,
            Height = 13.5,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1.2),
            BorderBrush = accent,
            Background = Brushes.Transparent,
            Padding = new Thickness(1.2),
            VerticalAlignment = VerticalAlignment.Center
        };

        var inner = new Grid();

        int step = Math.Clamp((int)Math.Round(percent / 10.0) * 10, 0, 100);
        if (percent > 0 && step == 0) step = 10;

        var fill = new Border
        {
            Height = 9,
            CornerRadius = new CornerRadius(1.5),
            Background = accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
        inner.Children.Add(fill);

        shell.Loaded += (_, _) =>
        {
            const double maxInner = 27 - 2.4 - 2.4;
            double target = Math.Max(2, maxInner * (step / 100.0));
            fill.BeginAnimation(System.Windows.FrameworkElement.WidthProperty, new DoubleAnimation
            {
                From = 0,
                To = target,
                Duration = TimeSpan.FromMilliseconds(350),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        };

        if (isCharging)
        {
            inner.Children.Add(new TextBlock
            {
                Text = LucideIcons.Zap,
                FontFamily = ThemeService.LucideFont,
                FontSize = 8.5,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        shell.Child = inner;
        container.Children.Add(shell);

        // 电池正极小凸起
        container.Children.Add(new Border
        {
            Width = 2.5,
            Height = 7.5,
            CornerRadius = new CornerRadius(0, 1.5, 1.5, 0),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 0, 0)
        });

        return container;
    }

    /// <summary>分组标题行：左侧科技蓝标题 + 可选右侧副标题 + 可选设置按钮。</summary>
    public static Grid CreateGroupHeader(string title, string subtitle,
                                         bool hasPreviousGroup,
                                         bool showSettingsButton = false,
                                         Action? onSettings = null)
    {
        var header = new Grid
        {
            Margin = hasPreviousGroup
                ? new Thickness(0, 8, 0, 2)
                : new Thickness(0, 0, 0, 2)
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        if (showSettingsButton)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = ThemeService.TechBlueBrush,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (!string.IsNullOrEmpty(subtitle))
        {
            var sub = new TextBlock
            {
                Text = subtitle,
                Foreground = ThemeService.TechBlueBrush,
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = showSettingsButton
                    ? new Thickness(0, 0, 6, 0)
                    : new Thickness(0)
            };
            Grid.SetColumn(sub, 1);
            header.Children.Add(sub);
        }

        if (showSettingsButton && onSettings != null)
        {
            var btn = new Button
            {
                Content = LucideIcons.Settings,
                FontFamily = ThemeService.LucideFont,
                FontSize = 13,
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "打开设置",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Style = CreateIconButtonStyle()
            };
            btn.Click += (_, _) => onSettings();
            Grid.SetColumn(btn, 2);
            header.Children.Add(btn);
        }

        return header;
    }

    /// <summary>图标按钮样式：透明底，悬停时科技蓝微光。</summary>
    public static Style CreateIconButtonStyle()
    {
        var style = new System.Windows.Style(typeof(Button));
        style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.OverridesDefaultStyleProperty, true));
        style.Setters.Add(new System.Windows.Setter(System.Windows.FrameworkElement.CursorProperty, Cursors.Hand));

        var template = new System.Windows.Controls.ControlTemplate(typeof(Button));
        var border = new System.Windows.FrameworkElementFactory(typeof(Border));
        border.Name = "border";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var content = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(System.Windows.FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Center);
        content.SetValue(System.Windows.FrameworkElement.VerticalAlignmentProperty,
            VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;

        var hover = new System.Windows.Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new System.Windows.Setter
        {
            TargetName = "border",
            Property = Border.BackgroundProperty,
            Value = new SolidColorBrush(Color.FromArgb(40, 2, 132, 199))
        });
        template.Triggers.Add(hover);

        style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.TemplateProperty, template));
        style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, ThemeService.TechBlueBrush));
        return style;
    }

    /// <summary>左标签 + 右数值的一行。</summary>
    public static Grid CreateMetricRow(string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = ThemeService.SecondaryTextBrush,
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var val = new TextBlock
        {
            Text = value,
            Foreground = ThemeService.PrimaryTextBrush,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(val, 1);
        row.Children.Add(val);
        return row;
    }

    /// <summary>细分隔线。</summary>
    public static Border CreateSeparator() => new()
    {
        Height = 1,
        Background = ThemeService.SeparatorBrush,
        Margin = new Thickness(0, 7, 0, 6)
    };

    /// <summary>底部小号说明文字。</summary>
    public static TextBlock CreateHint(string text, double topMargin = 2,
                                       double bottomMargin = 4)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = ThemeService.FaintTextBrush,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, topMargin, 0, bottomMargin)
        };
        return tb;
    }

    /// <summary>主要 / 次要操作按钮。</summary>
    public static Button CreateActionButton(string text, bool primary)
    {
        var btn = new Button
        {
            Content = text,
            Height = 32,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Focusable = false
        };

        var style = new System.Windows.Style(typeof(Button));
        style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.OverridesDefaultStyleProperty, true));
        style.Setters.Add(new System.Windows.Setter(System.Windows.FrameworkElement.CursorProperty, Cursors.Hand));

        var template = new System.Windows.Controls.ControlTemplate(typeof(Button));
        var border = new System.Windows.FrameworkElementFactory(typeof(Border));
        border.Name = "border";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BackgroundProperty,
            new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty,
            new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));

        var content = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(System.Windows.FrameworkElement.HorizontalAlignmentProperty,
            HorizontalAlignment.Center);
        content.SetValue(System.Windows.FrameworkElement.VerticalAlignmentProperty,
            VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;

        var hover = new System.Windows.Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new System.Windows.Setter
        {
            TargetName = "border",
            Property = Border.OpacityProperty,
            Value = 0.86
        });
        template.Triggers.Add(hover);
        style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.TemplateProperty, template));
        btn.Style = style;

        if (primary)
        {
            btn.Background = ThemeService.TechBlueBrush;
            btn.Foreground = Brushes.White;
            btn.BorderBrush = Brushes.Transparent;
        }
        else
        {
            btn.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
                "ThemeButtonSecondaryBackground");
            btn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty,
                "ThemeButtonSecondaryForeground");
            btn.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "ThemeCardBorder");
        }

        return btn;
    }
}
