using Editor.Application;
using Editor.Core;
using NUnit.Framework;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Editor.Desktop;
using System.Reflection;

namespace Editor.Tests;

public sealed class P4ExportPreflightTests
{
    [TestCase("Cancel", false)]
    [TestCase("Export anyway", true)]
    [TestCase(null, false)]
    public void WarningDialogRequiresExplicitContinue(string? button, bool expected)
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new Avalonia.Themes.Fluent.FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var window = new MainWindow(new EditSession(new EditorDocument()));
        try
        {
            window.Show();
            var method = typeof(MainWindow).GetMethod("ReviewExportWarnings", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var warnings = new[] { new ExportWarning(ExportWarningKind.ClippedByCanvas,
                Guid.NewGuid(), "曲绘 A", "Partly outside the canvas; PNG clips it at the edge.") };
            var pending = (Task<bool>)method.Invoke(window, [warnings])!;
            var dialog = window.OwnedWindows.Single();
            Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t =>
                t.Text?.Contains("曲绘 A：部分超出画布，PNG 会裁切边缘。", StringComparison.Ordinal) == true), Is.True);
            if (button is null) dialog.Close();
            else UiTestActions.Click(UiTestActions.Find(dialog, button));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.That(pending.IsCompleted, Is.True);
            Assert.That(pending.GetAwaiter().GetResult(), Is.EqualTo(expected));
        }
        finally { window.Close(); Avalonia.Application.Current!.Styles.Remove(theme); }
    }

    [TestCase(1920, 1080)]
    [TestCase(1440, 1080)]
    [TestCase(2740, 1080)]
    public void UsesCanvasUnitsAndDistinguishesTouchingClippedAndOutside(int width, int height)
    {
        var inside = new Layer { Bounds = new(0, 0, width, height) };
        var clipped = new Layer { Bounds = new(-1, 10, 100, 100), Locked = true };
        var outside = new Layer { Bounds = new(width, 10, 100, 100) };
        var left = new Layer { Bounds = new(-100, 10, 100, 100) };
        var bottom = new Layer { Bounds = new(10, height, 100, 100) };
        var top = new Layer { Bounds = new(10, -100, 100, 100) };
        var doc = new EditorDocument { CanvasWidth = width, CanvasHeight = height,
            Layers = [inside, clipped, outside, left, bottom, top] };
        foreach (var layer in doc.Layers) layer.Kind = LayerKind.Shape;
        var before = doc.Copy();
        var warnings = ExportPreflight.Inspect(doc, _ => false);
        Assert.That(warnings.Select(w => w.LayerId), Is.EqualTo(new[] { clipped.Id, outside.Id, left.Id, bottom.Id, top.Id }));
        Assert.That(warnings.Select(w => w.Kind), Is.EqualTo(new[] { ExportWarningKind.ClippedByCanvas,
            ExportWarningKind.OutsideCanvas, ExportWarningKind.OutsideCanvas,
            ExportWarningKind.OutsideCanvas, ExportWarningKind.OutsideCanvas }));
        Assert.That(doc.StateEquals(before), Is.True);
    }

    [Test]
    public void HiddenGroupMembersAreReportedOnceAndLocalFontsOnlyForNonemptyVisibleText()
    {
        var group = Guid.NewGuid();
        var hidden = new Layer { Kind = LayerKind.Text, Visible = false, GroupId = group,
            Text = "hidden", Bounds = new(-100, -100, 10, 10), FontFamily = "missing" };
        var visible = new Layer { Kind = LayerKind.Text, GroupId = group, Text = "visible", FontFamily = "missing" };
        var empty = new Layer { Kind = LayerKind.Text, FontFamily = "missing" };
        var shape = new Layer { Kind = LayerKind.Shape, Text = "ignored", FontFamily = "missing" };
        var doc = new EditorDocument { Layers = [hidden, visible, empty, shape] };
        var warnings = ExportPreflight.Inspect(doc, family => family == "missing");
        Assert.That(warnings.Select(w => (w.LayerId, w.Kind)), Is.EqualTo(new[] {
            (hidden.Id, ExportWarningKind.HiddenLayer), (visible.Id, ExportWarningKind.MissingLocalFont) }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AcceptedWarningsExportExactDocumentWithoutChangingHistory(bool transparent)
    {
        var edit = Session();
        edit.UpdateLayer(edit.Document.Layers[0].Id, l => l.Name = "changed");
        var before = edit.Document.Copy();
        var stages = new List<string>();
        var workflow = new PngExportWorkflow(edit, _ => stages.Add("validate"), (doc, mode, width) =>
        {
            Assert.That(doc.StateEquals(before), Is.True);
            Assert.That(mode, Is.EqualTo(transparent));
            stages.Add("render"); return [1, 2, 3];
        }, (_, bytes) => { Assert.That(bytes, Is.EqualTo(new byte[] { 1, 2, 3 })); stages.Add("write"); },
            new ProtectedSourcePaths(), () => null);
        var result = await workflow.ExportWithReviewAsync(transparent, _ => false, warnings =>
        {
            Assert.That(warnings.Single().Kind, Is.EqualTo(ExportWarningKind.ClippedByCanvas));
            stages.Add("review"); return Task.FromResult(true);
        }, () => { stages.Add("path"); return Task.FromResult<string?>("export.png"); });
        Assert.That(result, Is.True);
        Assert.That(stages, Is.EqualTo(new[] { "validate", "review", "path", "validate", "render", "write" }));
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.IsDirty, Is.True);
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationAtEitherPromptNeverRendersOrWrites(bool acceptReview)
    {
        var edit = Session();
        var before = edit.Document.Copy();
        var picked = false;
        var workflow = NoOutputWorkflow(edit);
        Assert.That(await workflow.ExportWithReviewAsync(false, _ => false,
            _ => Task.FromResult(acceptReview), () => { picked = true; return Task.FromResult<string?>(null); }), Is.False);
        Assert.That(picked, Is.EqualTo(acceptReview));
        Assert.That(edit.Document.StateEquals(before), Is.True);
        Assert.That(edit.UndoCount, Is.Zero);
        Assert.That(edit.IsDirty, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ChangedDocumentDuringReviewOrPickerCannotExportUnreviewedState(bool duringPicker)
    {
        var edit = Session();
        var workflow = NoOutputWorkflow(edit);
        void Change() => edit.UpdateLayer(edit.Document.Layers[0].Id, l => l.Name = "changed");
        Assert.ThrowsAsync<InvalidOperationException>(async () => await workflow.ExportWithReviewAsync(false,
            _ => false, _ => { if (!duringPicker) Change(); return Task.FromResult(true); },
            () => { if (duringPicker) Change(); return Task.FromResult<string?>("export.png"); }));
        Assert.That(edit.IsDirty, Is.True);
        Assert.That(edit.UndoCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CleanDocumentSkipsReview()
    {
        var edit = new EditSession(new EditorDocument());
        Assert.That(await NoOutputWorkflow(edit).ExportWithReviewAsync(false, _ => false,
            _ => throw new AssertionException("No warning dialog expected"),
            () => Task.FromResult<string?>(null)), Is.False);
    }

    [Test]
    public void ActiveGestureStopsBeforeAnyPrompt()
    {
        var edit = Session();
        edit.BeginGesture(edit.Document.Layers[0].Id);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await NoOutputWorkflow(edit).ExportWithReviewAsync(false,
            _ => false, _ => throw new AssertionException("Unexpected review"),
            () => throw new AssertionException("Unexpected picker")));
        edit.CancelGesture();
    }

    [Test]
    public void CorruptImageStopsBeforeAnyPrompt()
    {
        var edit = Session();
        var workflow = new PngExportWorkflow(edit, _ => throw new InvalidDataException("corrupt image"),
            (_, _, _) => throw new AssertionException("Unexpected render"),
            (_, _) => throw new AssertionException("Unexpected write"), new ProtectedSourcePaths(), () => null);
        Assert.ThrowsAsync<InvalidDataException>(async () => await workflow.ExportWithReviewAsync(false,
            _ => false, _ => throw new AssertionException("Unexpected review"),
            () => throw new AssertionException("Unexpected picker")));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void InvalidDifficultyOrMissingArtworkRemainErrors(bool badge)
    {
        var layer = badge ? EditorDocument.NewBadge(new(0, 0)) : new Layer { Kind = LayerKind.Image };
        if (badge) layer.Level = 0;
        Assert.Throws<InvalidDataException>(() => ExportPreflight.Inspect(new EditorDocument { Layers = [layer] }, _ => false));
    }

    private static EditSession Session() => new(new EditorDocument
    {
        Layers = [new Layer { Kind = LayerKind.Shape, Bounds = new(-10, 20, 100, 100) }]
    });

    private static PngExportWorkflow NoOutputWorkflow(EditSession edit) => new(edit, _ => { },
        (_, _, _) => throw new AssertionException("Unexpected render"),
        (_, _) => throw new AssertionException("Unexpected write"), new ProtectedSourcePaths(), () => null);
}
