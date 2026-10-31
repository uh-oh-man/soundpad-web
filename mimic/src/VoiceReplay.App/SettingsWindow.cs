using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VoiceReplay.Core;

namespace VoiceReplay.App;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(Store store, LocalTranscriber speech, SoundpadService soundpad, Hotkeys? hotkeys)
    {
        Ui.Window(this);
        Title = "Settings · Voice Replay Studio";
        Width = 680;
        Height = 830;
        MinWidth = 580;
        MinHeight = 500;
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(Ui.Text("SETTINGS", 24));
        panel.Children.Add(Ui.Text("Capture source is selected in the main window. The buffer continues only after you start monitoring. WAV export: PCM 16-bit mono.", 13));
        panel.Children.Add(Ui.Text("Rolling buffer capacity (seconds)"));
        var capacity = new ComboBox { ItemsSource = new[] { 30, 60, 120 }, SelectedItem = store.Settings.BufferSeconds };
        panel.Children.Add(capacity);
        panel.Children.Add(Ui.Text("WAV sample rate"));
        var format = new ComboBox { ItemsSource = new[] { 16000, 44100, 48000 }, SelectedItem = store.Settings.SampleRate };
        panel.Children.Add(format);
        panel.Children.Add(Ui.Text("Global hotkeys — click a field and press a combination", 18));
        TextBox KeyBox(string label, string value)
        {
            panel.Children.Add(Ui.Text(label));
            var box = new TextBox { Text = value };
            panel.Children.Add(box);
            box.GotKeyboardFocus += (_, _) => hotkeys?.Clear();
            box.PreviewKeyDown += (_, e) =>
            {
                e.Handled = true;
                var k = e.Key == Key.System ? e.SystemKey : e.Key;
                if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                    return;
                string mods = "";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    mods += "Ctrl+";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                    mods += "Alt+";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    mods += "Shift+";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
                    mods += "Win+";
                var name = k.ToString();
                if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
                    name = name[1..];
                box.Text = mods + name;
            };
            return box;
        }
        var capture = KeyBox("Capture", store.Settings.CaptureHotkey);
        var stop = KeyBox("Stop playback (local + Soundpad)", store.Settings.StopHotkey);
        var edit = KeyBox("Open selected clip editor", store.Settings.EditorHotkey);
        panel.Children.Add(Ui.Text("RegisterHotKey works across normal applications. Exclusive fullscreen and elevated games depend on Windows/game behavior; universal compatibility is unverified.", 12));
        panel.Children.Add(Ui.Text("Preview monitoring output", 18));
        var devices = MonitorOutputs.List().ToArray();
        var monitor = new ComboBox { ItemsSource = devices, DisplayMemberPath = "Label", SelectedItem = devices.FirstOrDefault(x => x.Id == store.Settings.MonitorDeviceId) };
        panel.Children.Add(monitor);
        panel.Children.Add(Ui.Text("Choose physical headphones or an Elgato Monitor output. Outgoing microphone/voice-chat/mixing buses are blocked. Existing Windows and Wave Link routing is never changed.", 12));
        var auto = new CheckBox { Content = "Automatically import completed clips into Soundpad", IsChecked = store.Settings.AutoImport };
        panel.Children.Add(auto);
        var instant = new CheckBox { Content = "Instant import before transcription (may create a named replacement)", IsChecked = store.Settings.InstantImport };
        panel.Children.Add(instant);
        panel.Children.Add(Ui.Text("Soundpad uses its local pipe and a dedicated Voice Replay Studio category. Editing imported clips preserves the old entry and imports a versioned replacement. Trial Soundpad cannot add sounds.", 12));
        var spStatus = Ui.Text(soundpad.Status);
        panel.Children.Add(spStatus);
        panel.Children.Add(Ui.AsyncButton("Soundpad read-only diagnostic", async () => { spStatus.Text = await soundpad.Diagnostic(); store.Log(spStatus.Text); }));
        panel.Children.Add(Ui.Text("Offline transcription · English · base.en · 4 CPU threads", 18));
        var modelStatus = Ui.Text(speech.Status);
        panel.Children.Add(modelStatus);
        panel.Children.Add(Ui.AsyncButton("Download / repair model (~148 MB)", async () => { modelStatus.Text = "Downloading model…"; await Task.Run(() => speech.Download()); modelStatus.Text = speech.Status; }));
        panel.Children.Add(Ui.Text("Clip storage directory"));
        var storage = new TextBox { Text = store.Settings.StorageDirectory };
        panel.Children.Add(storage);
        var minimized = new CheckBox { Content = "Start minimized to tray", IsChecked = store.Settings.StartMinimized };
        panel.Children.Add(minimized);
        var startup = new CheckBox { Content = "Start with Windows (current user only)", IsChecked = store.Settings.Startup };
        panel.Children.Add(startup);
        panel.Children.Add(Ui.Button("Save settings", () =>
        {
            try
            {
                var keys = new[] { capture.Text, stop.Text, edit.Text }.Select(HotkeySpec.Parse).ToArray();
                if (keys.Distinct().Count() != 3)
                    throw new ArgumentException("Each action needs a different hotkey.");
                string directory = Path.GetFullPath(storage.Text);
                Directory.CreateDirectory(directory);
                // Register first; a conflict leaves previous settings intact and restored below.
                var trial = new Settings { CaptureHotkey = capture.Text, StopHotkey = stop.Text, EditorHotkey = edit.Text };
                hotkeys?.Set(trial);
                using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    if (startup.IsChecked == true)
                        run.SetValue("VoiceReplayStudio", '"' + Environment.ProcessPath + '"');
                    else
                        run.DeleteValue("VoiceReplayStudio", false);
                var s = store.Settings;
                s.CaptureHotkey = capture.Text;
                s.StopHotkey = stop.Text;
                s.EditorHotkey = edit.Text;
                s.BufferSeconds = (int)capacity.SelectedItem;
                s.SampleRate = (int)format.SelectedItem;
                s.MonitorDeviceId = (monitor.SelectedItem as MonitorOutput)?.Id ?? "";
                s.AutoImport = auto.IsChecked == true;
                s.InstantImport = instant.IsChecked == true;
                s.StorageDirectory = directory;
                s.StartMinimized = minimized.IsChecked == true;
                s.Startup = startup.IsChecked == true;
                store.Save();
                Close();
            }
            catch (Exception ex) { try { hotkeys?.Set(store.Settings); } catch { } MessageBox.Show(ex.Message, "Settings"); }
        }));
        Closed += (_, _) => { try { hotkeys?.Set(store.Settings); } catch (Exception ex) { MessageBox.Show(ex.Message, "Global hotkeys"); } };
    }
}
