using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FitzLauncher.Core.Diagnostics;

namespace FitzLauncher.App.Views;

/// <summary>Plain-English error with fix links, the technical report, and ways to send it.</summary>
public partial class CrashWindow : Window
{
    private readonly string _report;
    private readonly string _title;
    private readonly string _logsDir;

    public CrashWindow(PlainError error, string report, string logsDir, bool fatal)
    {
        InitializeComponent();
        _report = report;
        _title = error.Title;
        _logsDir = logsDir;
        TitleText.Text = error.Title;
        WhatText.Text = error.WhatHappened;
        DoText.Text = error.WhatToDo;
        DetailsBox.Text = report;
        foreach (var fix in error.Fixes)
        {
            var b = new Button { Content = fix.Label, Margin = new Thickness(0, 0, 8, 8), Style = (Style)FindResource("PrimaryButton") };
            b.Click += (_, _) => Open(fix.Url);
            FixPanel.Children.Add(b);
        }
        if (fatal) CloseButton.Content = "Close";
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_report); } catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        Copy_Click(sender, e);
        Open(ErrorExplainer.IssueUrl(_title, _report));
    }

    private void Logs_Click(object sender, RoutedEventArgs e) => Open(_logsDir);

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.ProcessPath is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
