using Editor.Application;
using Editor.Core;
using Editor.Formats;

namespace Editor.Desktop;

/// <summary>Small two-process probe used by the internal regression gate.</summary>
internal static class RecoveryProbe
{
    public static int Run(string directory, string mode, TextWriter output, TextWriter error)
    {
        try
        {
            var path = Path.GetFullPath(directory);
            if (mode == "write")
            {
                var layer = new Layer
                {
                    Kind = LayerKind.Text, Name = "before", Text = "恢复探针",
                    Bounds = new RectD(40, 40, 320, 90)
                };
                var session = new EditSession(new EditorDocument
                {
                    CanvasWidth = 640, CanvasHeight = 360, Layers = [layer]
                });
                session.UpdateLayer(layer.Id, value => value.Name = "dirty");
                var service = new AutoRecoveryService(session, path, "recovery-probe",
                    () => new DateTimeOffset(2026, 10, 17, 13, 0, 0, TimeSpan.FromHours(8)),
                    (_, _) => false);
                if (!service.CaptureIfDirty(Path.Combine(path, "probe.ppgproj")))
                {
                    error.WriteLine("RECOVERY WRITE FAILED");
                    return 1;
                }
                output.WriteLine("RECOVERY WRITE PASS");
                return 0;
            }
            if (mode == "read")
            {
                var edit = new EditSession(new EditorDocument());
                var service = new AutoRecoveryService(edit, path, "recovery-probe",
                    () => new DateTimeOffset(2026, 10, 17, 13, 0, 0, TimeSpan.FromHours(8)),
                    (_, _) => false);
                var load = service.LoadCandidates();
                if (load.Candidates.Count != 1)
                {
                    error.WriteLine($"RECOVERY READ FAILED: candidates={load.Candidates.Count}");
                    return 1;
                }
                var document = NativePackageStore.Read(load.Candidates[0].SnapshotPath);
                if (document.Layers.Count != 1 || document.Layers[0].Name != "dirty" ||
                    document.Layers[0].Text != "恢复探针")
                {
                    error.WriteLine("RECOVERY READ FAILED: snapshot content mismatch");
                    return 1;
                }
                output.WriteLine("RECOVERY READ PASS");
                return 0;
            }
            error.WriteLine("Usage: --recovery-probe <directory> write|read");
            return 2;
        }
        catch (Exception failure)
        {
            error.WriteLine(failure.Message);
            return 1;
        }
    }
}
