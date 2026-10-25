using Avalonia;
using Avalonia.Headless;
using NUnit.Framework;

namespace Editor.Tests;

/// <summary>
/// Image decode and offscreen render assertions need the Skia headless platform. This mirrors the
/// configuration used by the shipping <c>--probe</c> path, but allows rendering on test threads.
/// </summary>
[SetUpFixture]
public sealed class AvaloniaTestHost
{
    private static bool _started;

    [OneTimeSetUp]
    public void Start() => EnsureStarted();

    public static void EnsureStarted()
    {
        if (_started) return;
        var stateRoot = Path.Combine(Path.GetTempPath(), "ppg-headless-state-" + Environment.ProcessId);
        Directory.CreateDirectory(stateRoot);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPG_INTERNAL_VERIFY_RECENT_PROJECTS")))
            Environment.SetEnvironmentVariable("PPG_INTERNAL_VERIFY_RECENT_PROJECTS",
                Path.Combine(stateRoot, "RecentProjects.json"));
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPG_INTERNAL_VERIFY_RECOVERY_DIRECTORY")))
            Environment.SetEnvironmentVariable("PPG_INTERNAL_VERIFY_RECOVERY_DIRECTORY",
                Path.Combine(stateRoot, "Recovery"));
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPG_INTERNAL_VERIFY_PREFERENCES_PATH")))
            Environment.SetEnvironmentVariable("PPG_INTERNAL_VERIFY_PREFERENCES_PATH",
                Path.Combine(stateRoot, "Preferences.json"));
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPG_INTERNAL_VERIFY_EXPORT_PRESETS_PATH")))
            Environment.SetEnvironmentVariable("PPG_INTERNAL_VERIFY_EXPORT_PRESETS_PATH",
                Path.Combine(stateRoot, "ExportPresets.json"));
        Environment.SetEnvironmentVariable("PPG_INTERNAL_VERIFY_DISABLE_AUTO_RECOVERY", "1");
        AppBuilder.Configure<TestApplication>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
                ShouldRenderOnUIThread = false
            })
            .SetupWithoutStarting();
        _started = true;
    }

    private sealed class TestApplication : Avalonia.Application
    {
    }
}
