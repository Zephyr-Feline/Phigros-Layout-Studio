using Editor.Application;
using Editor.Core;
using Editor.Rendering;

namespace Editor.Desktop;

/// <summary>Read-only verification of a saved project against pre-shutdown exports.</summary>
public static class ProjectReopenProbe
{
    public static void Verify(string projectPath, string opaquePath, string transparentPath)
    {
        var original = File.ReadAllBytes(projectPath);
        var writeTime = File.GetLastWriteTimeUtc(projectPath);
        var session = new EditSession(new EditorDocument());
        var workflow = new NativeProjectWorkflow(session, NativeImaging.ValidateImages, "p5-reopen");
        workflow.Open(projectPath);
        if (workflow.IsDirty || session.UndoCount != 0 || session.RedoCount != 0)
            throw new InvalidDataException("Reopened project did not start a clean session.");

        Compare(opaquePath, false);
        Compare(transparentPath, true);
        if (!original.SequenceEqual(File.ReadAllBytes(projectPath)) ||
            writeTime != File.GetLastWriteTimeUtc(projectPath))
            throw new InvalidDataException("Reopening modified the source project.");

        void Compare(string expectedPath, bool transparent)
        {
            var expected = File.ReadAllBytes(expectedPath);
            var actual = PngExporter.Render(session.Document, transparent, NativeImaging.Decoder);
            if (!expected.SequenceEqual(actual))
                throw new InvalidDataException($"Reopened project changed the {(transparent ? "transparent" : "opaque")} PNG: {projectPath}");
        }
    }
}
