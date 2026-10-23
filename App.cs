using System;
using System.Windows;

namespace MediaPlayerResumeAssistant;

public sealed class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new App();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new MainWindow(args);
        app.Run(window);
    }
}
