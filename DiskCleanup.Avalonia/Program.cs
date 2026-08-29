using Avalonia;
using Avalonia.Logging;

namespace DiskCleanup.Avalonia;

class Program
{
    // Avalonia entry point convention: BuildAvaloniaApp is looked up by name
    // by tooling (e.g. the previewer) even though it isn't called directly
    // outside Main, so it must keep this exact signature and stay public.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTextWriter(Console.Out, LogEventLevel.Warning);
}
