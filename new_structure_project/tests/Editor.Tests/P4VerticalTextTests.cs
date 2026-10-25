using Avalonia.LogicalTree;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4VerticalTextTests
{
    [Test]
    public void TopCentreAndBottomMoveTheSameTextBlockInsideItsBounds()
    {
        var layer = new Layer { Kind = LayerKind.Text, Text = "Align", FontSize = 42,
            Bounds = new RectD(25, 20, 250, 180) };
        var document = new EditorDocument { CanvasWidth = 400, CanvasHeight = 240,
            BackgroundArgb = 0xFF000000, Layers = [layer] };
        var ink = new List<(int Left, int Top, int Right, int Bottom)>();
        foreach (var alignment in Enum.GetValues<VerticalAlignment>())
        {
            layer.VerticalAlignment = alignment;
            ink.Add(TestImages.InkBounds(TestImages.RenderPng(document)));
        }
        Assert.Multiple(() =>
        {
            Assert.That(ink[0].Top, Is.LessThan(ink[1].Top - 30));
            Assert.That(ink[1].Top, Is.LessThan(ink[2].Top - 30));
            Assert.That(ink.Select(box => box.Left).Distinct().Count(), Is.EqualTo(1));
            Assert.That(ink.Select(box => box.Right).Distinct().Count(), Is.EqualTo(1));
            Assert.That(ink[2].Bottom, Is.LessThan(200));
        });
    }

    [Test]
    public void NativeAndTemporaryRoundTripsPreserveAlignmentAndPixels()
    {
        var layer = new Layer { Kind = LayerKind.Text, Text = "中文 English\nSecond line",
            FontSize = 36, LineSpacing = 1.5, Alignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Bounds = new RectD(20, 20, 300, 180) };
        var document = new EditorDocument { CanvasWidth = 400, CanvasHeight = 240,
            Layers = [layer] };
        var png = TestImages.RenderPng(document);
        var directory = Directory.CreateTempSubdirectory("ppg-vertical-").FullName;
        try
        {
            var native = Path.Combine(directory, "vertical.ppgproj");
            var temporary = Path.Combine(directory, "vertical.json");
            NativePackageStore.Save(native, document, "p4-test");
            NativeDocumentStore.Save(temporary, document);
            foreach (var loaded in new[] { NativePackageStore.Read(native), NativeDocumentStore.Read(temporary) })
            {
                Assert.That(loaded.StateEquals(document), Is.True);
                Assert.That(TestImages.RenderPng(loaded), Is.EqualTo(png));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void OverflowingTextKeepsTheExistingTopClippedRendering()
    {
        var layer = new Layer { Kind = LayerKind.Text, Text = "Oversized text",
            FontSize = 60, Bounds = new RectD(20, 20, 250, 24) };
        var document = new EditorDocument { CanvasWidth = 400, CanvasHeight = 240,
            BackgroundArgb = 0xFF000000, Layers = [layer] };
        var top = TestImages.RenderPng(document);
        layer.VerticalAlignment = VerticalAlignment.Center;
        Assert.That(TestImages.RenderPng(document), Is.EqualTo(top));
        layer.VerticalAlignment = VerticalAlignment.Bottom;
        Assert.That(TestImages.RenderPng(document), Is.EqualTo(top));
    }

    [Test]
    public void TemporaryV2WithoutAlignmentLoadsAsTopWithoutSourceRewrite()
    {
        var document = new EditorDocument { Layers = [new Layer { Kind = LayerKind.Text,
            Text = "Old", VerticalAlignment = VerticalAlignment.Bottom }] };
        var directory = Directory.CreateTempSubdirectory("ppg-vertical-v2-").FullName;
        try
        {
            var path = Path.Combine(directory, "old.json");
            NativeDocumentStore.Save(path, document);
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["FormatVersion"] = 2;
            LegacyFormatTestHelpers.RemoveV8BackgroundFields(json, temporary: true);
            json["Document"]!["Layers"]![0]!.AsObject().Remove("VerticalAlignment");
            File.WriteAllText(path, json.ToJsonString());
            var bytes = File.ReadAllBytes(path);
            var stamp = File.GetLastWriteTimeUtc(path);
            var loaded = NativeDocumentStore.Read(path);
            Assert.Multiple(() =>
            {
                Assert.That(loaded.Layers.Single().VerticalAlignment,
                    Is.EqualTo(VerticalAlignment.Top));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
                Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(stamp));
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void InvalidAlignmentIsRejectedWithoutCommittingAnEdit()
    {
        var layer = new Layer { Kind = LayerKind.Text, Text = "Valid" };
        var session = new EditSession(new EditorDocument { Layers = [layer] });
        Assert.Throws<InvalidDataException>(() => session.UpdateLayer(layer.Id,
            item => item.VerticalAlignment = (VerticalAlignment)99));
        Assert.Multiple(() =>
        {
            Assert.That(session.Document.Find(layer.Id).VerticalAlignment,
                Is.EqualTo(VerticalAlignment.Top));
            Assert.That(session.UndoCount, Is.Zero);
        });
    }

    [Test]
    public void PropertyPanelChangesAlignmentAsOneUndoableEdit()
    {
        AvaloniaTestHost.EnsureStarted();
        var layer = new Layer { Kind = LayerKind.Text, Text = "Panel",
            Bounds = new RectD(20, 30, 240, 180) };
        var session = new EditSession(new EditorDocument { Layers = [layer] });
        session.SelectedLayerId = layer.Id;
        var window = new MainWindow(session);
        try
        {
            window.Show();
            var choice = window.GetLogicalDescendants().OfType<ComboBox>()
                .Single(combo => combo.Name == "LayerVerticalAlignment");
            var apply = UiTestActions.FindNamed(window, "ApplyLayerProperties");
            Assert.That(choice.SelectedItem, Is.EqualTo(VerticalAlignment.Top));
            choice.SelectedItem = VerticalAlignment.Bottom;
            UiTestActions.Click(apply);
            Assert.That(session.Document.Find(layer.Id).VerticalAlignment,
                Is.EqualTo(VerticalAlignment.Bottom));
            Assert.That(session.UndoCount, Is.EqualTo(1));
            Assert.That(session.Undo(), Is.True);
            Assert.That(session.Document.Find(layer.Id).VerticalAlignment,
                Is.EqualTo(VerticalAlignment.Top));
            Assert.That(session.Redo(), Is.True);
            Assert.That(session.Document.Find(layer.Id).VerticalAlignment,
                Is.EqualTo(VerticalAlignment.Bottom));
        }
        finally { window.Close(); }
    }
}
