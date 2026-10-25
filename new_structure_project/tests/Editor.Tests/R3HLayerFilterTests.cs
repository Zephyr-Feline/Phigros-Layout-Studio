using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3HLayerFilterTests
{
    [Test]
    public void FilteredListTrimsHiddenSelectionAndRestoresAllRows()
    {
        AvaloniaTestHost.EnsureStarted();
        var alpha = TextLayer("Alpha Layer", 100, "alpha");
        var beta = TextLayer("Beta Layer", 200, "beta");
        var gamma = TextLayer("Gamma Layer", 300, "gamma");
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 900, CanvasHeight = 600, Layers = [alpha, beta, gamma]
        });
        edit.SetSelection([alpha.Id, gamma.Id]);
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var search = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerSearch");
            var filter = window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "FilterLayerMatches");
            var list = window.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "LayerList");
            search.Text = "Alpha";
            Dispatcher.UIThread.RunJobs();
            Assert.That(filter.IsEnabled, Is.True);
            Assert.That(list.Items.Count, Is.EqualTo(3),
                "the full list remains until the filter is enabled");
            filter.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(list.Items.Count, Is.EqualTo(1));
                Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { alpha.Id }));
                Assert.That(edit.UndoCount, Is.Zero);
                Assert.That(edit.IsDirty, Is.False);
                Assert.That(window.GetLogicalDescendants().OfType<TextBlock>()
                    .Single(block => block.Name == "LayerFilterNotice").Text,
                    Does.Contain("1 / 3"));
            });
            search.Text = "Beta";
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(list.Items.Count, Is.EqualTo(1));
                Assert.That(edit.SelectedLayerIds, Is.Empty,
                    "rows outside the active result set are deselected");
                Assert.That(edit.UndoCount, Is.Zero);
            });
            filter.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.That(list.Items.Count, Is.EqualTo(3));
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
        }
    }

    [Test]
    public void FilteredControlASelectsOnlyVisibleEditableMatches()
    {
        AvaloniaTestHost.EnsureStarted();
        var alpha = TextLayer("Alpha", 100, "alpha");
        var beta = TextLayer("Beta", 200, "beta");
        beta.Locked = true;
        var gamma = TextLayer("Alpha locked", 300, "gamma");
        gamma.Locked = true;
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 900, CanvasHeight = 600, Layers = [alpha, beta, gamma]
        });
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var search = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerSearch");
            var filter = window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "FilterLayerMatches");
            var list = window.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "LayerList");
            search.Text = "Alpha";
            filter.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.That(list.Items.Count, Is.EqualTo(2));
            list.Focus();
            window.KeyPress(Avalonia.Input.Key.A, Avalonia.Input.RawInputModifiers.Control,
                Avalonia.Input.PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { alpha.Id }),
                "locked filtered matches remain visible but are not selected by Ctrl+A");
            Assert.That(edit.UndoCount, Is.Zero);
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
        }
    }

    private static Layer TextLayer(string name, double x, string text) => new()
    {
        Kind = LayerKind.Text, Name = name, Text = text,
        Bounds = new RectD(x, 100, 200, 70), FontSize = 30
    };
}
