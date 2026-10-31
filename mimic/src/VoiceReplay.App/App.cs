using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using VoiceReplay.Core;

namespace VoiceReplay.App;

public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--transcription-test")
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "Smoke");
            Directory.CreateDirectory(directory);
            try
            {
                string text = Task.Run(() => new LocalTranscriber(new Store()).Transcribe(args[1])).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(directory, "transcription.txt"), text);
                return string.IsNullOrWhiteSpace(text) ? 1 : 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "transcription.txt"), ex.ToString()); return 1; }
        }
        using var instance = new Mutex(true, args.Contains("--smoke-test") ? "Local\\VoiceReplayStudioSmoke-" + Guid.NewGuid().ToString("N") : "Local\\VoiceReplayStudioV1", out bool created);
        if (!created)
        {
            MessageBox.Show("Voice Replay Studio is already running. Open it from the system tray.");
            return 0;
        }
        var app = new App();
        app.Resources = (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <SolidColorBrush x:Key="Text" Color="#EDF1FA"/><SolidColorBrush x:Key="Panel" Color="#202A3B"/>
          <Style TargetType="Window"><Setter Property="Background" Value="#101723"/><Setter Property="Foreground" Value="{StaticResource Text}"/><Setter Property="FontFamily" Value="Segoe UI"/><Setter Property="FontSize" Value="14"/></Style>
          <Style TargetType="Button"><Setter Property="Margin" Value="0,4,8,4"/><Setter Property="Padding" Value="14,8"/><Setter Property="Background" Value="#2D4160"/><Setter Property="Foreground" Value="{StaticResource Text}"/><Setter Property="BorderThickness" Value="0"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="b" Background="{TemplateBinding Background}" CornerRadius="6" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="#42618B"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="TextBox"><Setter Property="Background" Value="#1C283B"/><Setter Property="Foreground" Value="{StaticResource Text}"/><Setter Property="CaretBrush" Value="White"/><Setter Property="Padding" Value="10,7"/><Setter Property="Margin" Value="0,4,0,8"/><Setter Property="BorderBrush" Value="#42516B"/></Style>
          <Style TargetType="ComboBox"><Setter Property="Margin" Value="0,4,8,8"/><Setter Property="Padding" Value="8"/><Setter Property="Foreground" Value="#101723"/><Setter Property="MinWidth" Value="90"/></Style>
          <Style TargetType="CheckBox"><Setter Property="Foreground" Value="{StaticResource Text}"/><Setter Property="Margin" Value="0,6,0,8"/></Style>
          <Style TargetType="ListBox"><Setter Property="Background" Value="#172131"/><Setter Property="Foreground" Value="{StaticResource Text}"/><Setter Property="BorderThickness" Value="0"/><Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/></Style>
          <Style TargetType="ListBoxItem"><Setter Property="Padding" Value="12"/><Setter Property="Margin" Value="0,0,0,3"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem"><Border x:Name="row" Background="#1B283B" CornerRadius="6" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsSelected" Value="True"><Setter TargetName="row" Property="Background" Value="#294867"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="row" Property="BorderBrush" Value="#5ABDD0"/><Setter TargetName="row" Property="BorderThickness" Value="1"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
        </ResourceDictionary>
        """);
        try
        {
            var store = new Store(args.Contains("--smoke-test") ? Path.Combine(Path.GetTempPath(), "VoiceReplaySmoke-" + Guid.NewGuid().ToString("N")) : null);
            app.DispatcherUnhandledException += (_, e) => { store.Log("UI error", e.Exception); MessageBox.Show(e.Exception.Message, "Voice Replay Studio"); e.Handled = true; };
            int outputArg = Array.IndexOf(args, "--test-audio");
            var main = new MainWindow(store, args.Contains("--smoke-test"), outputArg >= 0 && outputArg + 1 < args.Length ? args[outputArg + 1] : null);
            return app.Run(main);
        }
        catch (Exception ex) { MessageBox.Show(ex.ToString(), "Voice Replay Studio — startup error"); return 1; }
    }
}
internal static class Ui
{
    public static void Window(Window window)
    {
        window.Background = new SolidColorBrush(Color.FromRgb(16, 23, 35));
        window.Foreground = new SolidColorBrush(Color.FromRgb(237, 241, 250));
        window.FontFamily = new FontFamily("Segoe UI");
        window.FontSize = 14;
    }
    public static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    public static Button Button(string title, Action action)
    {
        var b = new Button { Content = title };
        b.Click += (_, _) => action();
        return b;
    }
    public static Button AsyncButton(string title, Func<Task> action)
    {
        var b = new Button { Content = title };
        b.Click += async (_, _) => { b.IsEnabled = false; try { await action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "Voice Replay Studio"); } finally { b.IsEnabled = true; } };
        return b;
    }
    public static StackPanel Row(params UIElement[] items)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items)
            s.Children.Add(i);
        return s;
    }
}
