using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3WHistoryFeedbackTests
{
    [Test]
    public void LabelsFollowUndoRedoOrderAndClearTheRedoBranch()
    {
        var layer = new Layer
        {
            Kind = LayerKind.Text, Name = "title", Text = "A",
            Bounds = new RectD(40, 40, 300, 80)
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [layer]
        });
        edit.UpdateLayer(layer.Id, value => value.Name = "renamed", "重命名图层");
        edit.UpdateLayer(layer.Id, value => value.Text = "B", "编辑文字");

        Assert.Multiple(() =>
        {
            Assert.That(edit.UndoCount, Is.EqualTo(2));
            Assert.That(edit.UndoLabel, Is.EqualTo("编辑文字"));
            Assert.That(edit.RecentUndoLabels, Is.EqualTo(new[] { "编辑文字", "重命名图层" }));
            Assert.That(edit.RedoLabel, Is.Null);
            Assert.That(edit.RecentRedoLabels, Is.Empty);
        });

        Assert.That(edit.Undo(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(edit.UndoLabel, Is.EqualTo("重命名图层"));
            Assert.That(edit.RedoLabel, Is.EqualTo("编辑文字"));
            Assert.That(edit.RecentRedoLabels, Is.EqualTo(new[] { "编辑文字" }));
        });
        Assert.That(edit.Redo(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(edit.UndoLabel, Is.EqualTo("编辑文字"));
            Assert.That(edit.RedoLabel, Is.Null);
        });

        Assert.That(edit.Undo(), Is.True);
        edit.UpdateLayer(layer.Id, value => value.Visible = false, "显示或隐藏图层");
        Assert.Multiple(() =>
        {
            Assert.That(edit.RedoCount, Is.Zero);
            Assert.That(edit.RedoLabel, Is.Null);
            Assert.That(edit.UndoLabel, Is.EqualTo("显示或隐藏图层"));
        });
    }

    [Test]
    public void GestureLabelsAreRecordedOnceAndNoOpsNeverReplaceLabels()
    {
        var layer = new Layer
        {
            Kind = LayerKind.Shape, Name = "shape",
            Bounds = new RectD(100, 100, 320, 180)
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 1000, CanvasHeight = 600, Layers = [layer]
        });

        edit.BeginGesture(layer.Id);
        edit.MoveBy(0, 0);
        Assert.That(edit.CommitGesture(), Is.False);
        Assert.That(edit.UndoCount, Is.Zero);

        edit.BeginGesture(layer.Id);
        edit.MoveBy(24, 12);
        edit.MoveBy(40, 20);
        Assert.That(edit.CommitGesture(), Is.True);
        Assert.That(edit.UndoLabel, Is.EqualTo("移动图层"));
        Assert.That(edit.UndoCount, Is.EqualTo(1));

        edit.BeginGesture(layer.Id);
        edit.ResizeFromHandle(20, 10, ResizeHandle.BottomRight);
        Assert.That(edit.CommitGesture(), Is.True);
        Assert.That(edit.UndoLabel, Is.EqualTo("缩放图层"));

        edit.BeginGesture(layer.Id);
        edit.RotateBy(15);
        Assert.That(edit.CommitGesture(), Is.True);
        Assert.That(edit.UndoLabel, Is.EqualTo("旋转图层"));

        edit.Undo();
        var redo = edit.RedoLabel;
        edit.UpdateLayer(layer.Id, _ => { }, "不应记录");
        Assert.Multiple(() =>
        {
            Assert.That(edit.RedoLabel, Is.EqualTo(redo));
            Assert.That(edit.UndoLabel, Is.EqualTo("缩放图层"));
            Assert.That(edit.UndoCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void TemplateAssetAndStyleEditsCarrySpecificLabels()
    {
        var ids = Enumerable.Range(0, 3).Select(index => Guid.NewGuid()).ToArray();
        var layers = ids.Select((id, index) => new Layer
        {
            Id = id, Kind = LayerKind.Shape, Name = $"shape-{index}",
            Bounds = new RectD(40 + index * 180, 80, 160, 120)
        }).ToList();
        var unreferenced = Guid.NewGuid();
        var document = new EditorDocument
        {
            CanvasWidth = 900, CanvasHeight = 540, Layers = layers
        };
        document.AddAsset(unreferenced, new byte[] { 1, 2, 3 });
        var edit = new EditSession(document);

        edit.ApplyLayoutTemplate("three-columns", ids);
        Assert.That(edit.UndoLabel, Is.EqualTo("应用布局模板"));

        edit.Document.AddAsset(unreferenced, new byte[] { 1, 2, 3 });
        edit.CleanUnreferencedAssets();
        Assert.That(edit.UndoLabel, Is.EqualTo("清理未引用资源"));

        edit.UpdateLayer(ids[0], layer => layer.ColorArgb = 0xFF112233, "编辑图层属性");
        Assert.That(edit.UndoLabel, Is.EqualTo("编辑图层属性"));

        var texts = new[]
        {
            new Layer { Kind = LayerKind.Text, Text = "one", Bounds = new RectD(20, 20, 160, 50) },
            new Layer { Kind = LayerKind.Text, Text = "two", Bounds = new RectD(20, 90, 160, 50) }
        };
        var styleEdit = new EditSession(new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = texts.ToList()
        });
        styleEdit.ApplyLayerStyleBatch(texts.Select(layer => layer.Id),
            new LayerStylePatch(FontSize: 42));
        Assert.That(styleEdit.UndoLabel, Is.EqualTo("批量应用图层样式"));
    }

    [Test]
    public void BackgroundEditsAreLabelledAndNoOpCleanupPreservesRedo()
    {
        var layer = new Layer
        {
            Kind = LayerKind.Shape, Bounds = new RectD(40, 40, 220, 120)
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [layer]
        });
        edit.UpdateLayer(layer.Id, value => value.Name = "dirty", "重命名图层");
        Assert.That(edit.Undo(), Is.True);
        Assert.That(edit.RedoLabel, Is.EqualTo("重命名图层"));

        var cleanup = edit.CleanUnreferencedAssets();
        Assert.Multiple(() =>
        {
            Assert.That(cleanup.Changed, Is.False);
            Assert.That(edit.RedoLabel, Is.EqualTo("重命名图层"));
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.RedoCount, Is.EqualTo(1));
        });

        edit.SetFixedBackground(new byte[] { 4, 5, 6 });
        Assert.That(edit.UndoLabel, Is.EqualTo("设置固定背景"));
        Assert.That(edit.RedoCount, Is.Zero);
        edit.ClearFixedBackground();
        Assert.That(edit.UndoLabel, Is.EqualTo("清除固定背景"));
    }

    [Test]
    public void HistoryLabelsAreSessionOnlyAndReopenStartsEmpty()
    {
        var layer = new Layer
        {
            Kind = LayerKind.Shape, Bounds = new RectD(40, 40, 220, 120)
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [layer]
        });
        edit.UpdateLayer(layer.Id, value => value.Name = "saved", "重命名图层");
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "r3w-history-" + Guid.NewGuid().ToString("N") + ".ppgproj");
        try
        {
            NativePackageStore.Save(path, edit.Document, "r3w-test");
            var reopened = new EditSession(NativePackageStore.Read(path));
            Assert.Multiple(() =>
            {
                Assert.That(reopened.UndoCount, Is.Zero);
                Assert.That(reopened.RedoCount, Is.Zero);
                Assert.That(reopened.UndoLabel, Is.Null);
                Assert.That(reopened.RedoLabel, Is.Null);
                Assert.That(reopened.Document.Layers.Single().Name, Is.EqualTo("saved"));
            });
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void DesktopMenuToolbarStatusAndSidebarUseTheSameHistoryLabels()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var layer = new Layer
        {
            Kind = LayerKind.Text, Name = "before", Text = "title",
            Bounds = new RectD(50, 50, 300, 80)
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [layer]
        });
        edit.UpdateLayer(layer.Id, value => value.Name = "after", "重命名图层");
        var window = new MainWindow(edit, recovery: EmptyRecovery(edit));
        try
        {
            window.Show();
            window.UpdateLayout();
            var undoMenu = window.GetLogicalDescendants().OfType<MenuItem>()
                .Single(item => item.Name == "Undo");
            var undoButton = window.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "Undo");
            var summary = window.GetLogicalDescendants().OfType<TextBlock>()
                .Single(text => text.Name == "HistorySummary");

            Assert.Multiple(() =>
            {
                Assert.That(undoMenu?.Header?.ToString(), Is.EqualTo("撤销：重命名图层"));
                Assert.That(ToolTip.GetTip(undoMenu!)?.ToString(),
                    Does.StartWith("撤销：重命名图层"));
                Assert.That(ToolTip.GetTip(undoButton)?.ToString(),
                    Does.StartWith("撤销：重命名图层"));
                Assert.That(summary.Text, Does.Contain("可撤销："));
                Assert.That(summary.Text, Does.Contain("1. 重命名图层"));
                Assert.That(summary.Text, Does.Contain("可重做：无"));
            });

            UiTestActions.ClickNamed(window, "Undo");
            var redoMenu = window.GetLogicalDescendants().OfType<MenuItem>()
                .Single(item => item.Name == "Redo");
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(layer.Id).Name, Is.EqualTo("before"));
                Assert.That(UiTestActions.Status(window),
                    Is.EqualTo("已撤销：重命名图层。可重做：重命名图层。"));
                Assert.That(redoMenu?.Header?.ToString(), Is.EqualTo("重做：重命名图层"));
                Assert.That(summary.Text, Does.Contain("可撤销：无"));
                Assert.That(summary.Text, Does.Contain("1. 重命名图层"));
                Assert.That(edit.RedoCount, Is.EqualTo(1));
                Assert.That(edit.UndoCount, Is.Zero);
            });

            UiTestActions.ClickNamed(window, "Redo");
            Assert.Multiple(() =>
            {
                Assert.That(edit.Document.Find(layer.Id).Name, Is.EqualTo("after"));
                Assert.That(UiTestActions.Status(window),
                    Is.EqualTo("已重做：重命名图层。可撤销：重命名图层。"));
                Assert.That(edit.UndoCount, Is.EqualTo(1));
                Assert.That(edit.RedoCount, Is.Zero);
            });
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static AutoRecoveryService EmptyRecovery(EditSession edit) =>
        new(edit, Path.Combine(Path.GetTempPath(),
            "r3w-recovery-" + Guid.NewGuid().ToString("N")[..8]),
            "r3w-test", () => DateTimeOffset.Now, (_, _) => false);
}
