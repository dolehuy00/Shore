using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using Button = Avalonia.Controls.Button;

namespace A0;

/// <summary>T5: an Avalonia view on Android, built in code to keep the spike small.</summary>
public sealed class App : Avalonia.Application
{
    public static event EventHandler<string>? ActionRequested;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = CreateView();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static DockPanel CreateView()
    {
        var buttons = new WrapPanel { Margin = new Thickness(8) };
        foreach ((string text, string action) in new[]
        {
            ("Server (không dịch vụ)", "server"),
            ("Bật dịch vụ nền", "fgs-start"),
            ("Tắt dịch vụ nền", "fgs-stop"),
        })
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
            button.Click += (_, _) => ActionRequested?.Invoke(null, action);
            buttons.Children.Add(button);
        }

        var list = new ListBox { ItemsSource = SpikeLog.Lines, FontSize = 12 };
        var root = new DockPanel { Margin = new Thickness(0, 32, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Top);
        root.Children.Add(new TextBlock { Text = "Shorekeeper A0b", FontSize = 20, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left });
        DockPanel.SetDock(root.Children[0], Dock.Top);
        root.Children.Add(buttons);
        root.Children.Add(list);
        return root;
    }
}
