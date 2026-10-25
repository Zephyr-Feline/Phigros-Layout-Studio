using System.Diagnostics;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R2RecoveryProcessTests
{
    [Test]
    public void SnapshotWrittenByOneProcessIsReadableByANewProcess()
    {
        var folder = Path.Combine(Path.GetTempPath(), "r2-process-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var exe = Path.Combine(TestContext.CurrentContext.TestDirectory, "Editor.Desktop.exe");
            Assert.That(File.Exists(exe), Is.True, "The desktop host must be next to the test assembly.");
            Assert.That(Run(exe, folder, "write"), Is.EqualTo(0));
            Assert.That(Run(exe, folder, "read"), Is.EqualTo(0));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static int Run(string exe, string folder, string mode)
    {
        var start = new ProcessStartInfo(exe, $"--recovery-probe \"{folder}\" {mode}")
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new IOException("Could not start the desktop probe.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.That(process.WaitForExit(15000), Is.True, $"{mode} process did not exit.");
        if (process.ExitCode != 0)
            TestContext.Out.WriteLine($"{mode} stdout: {stdout}\n{mode} stderr: {stderr}");
        return process.ExitCode;
    }
}
