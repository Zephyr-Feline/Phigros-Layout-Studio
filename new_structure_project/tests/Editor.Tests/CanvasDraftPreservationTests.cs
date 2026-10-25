using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class CanvasDraftPreservationTests
{
    private const string TextDraft = "这是尚未应用的草稿第一行\n第二行需要完整保留";
    private const string NameDraft = "尚未应用的图层名称";

    [TestCase("click")]
    [TestCase("shift-click")]
    [TestCase("shift-click-multiple")]
    [TestCase("resize")]
    [TestCase("rotate")]
    [TestCase("move-return")]
    [TestCase("resize-return")]
    [TestCase("rotate-return")]
    public void AnUnchangedCanvasActionKeepsDraftsSelectionAndTheRedoBranch(string action)
    {
        using var host = new Host(multiple: action == "shift-click-multiple");
        host.SetDrafts();
        var state = host.CaptureState();
        var mode = action.StartsWith("resize", StringComparison.Ordinal) ? "resize"
            : action.StartsWith("rotate", StringComparison.Ordinal) ? "rotate" : "move";
        var start = host.Start(mode);
        var modifiers = action.StartsWith("shift-click", StringComparison.Ordinal)
            ? RawInputModifiers.Shift : RawInputModifiers.None;

        host.Window.MouseMove(start, modifiers);
        host.Window.MouseDown(start, MouseButton.Left, modifiers);
        if (action.EndsWith("-return", StringComparison.Ordinal))
        {
            host.Window.MouseMove(host.End(mode), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            host.Window.MouseMove(start, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        }
        host.Window.MouseUp(start, MouseButton.Left, modifiers);

        host.AssertUnchanged(state);
    }

    [TestCase("move", "escape")]
    [TestCase("move", "capture")]
    [TestCase("pan", "escape")]
    [TestCase("pan", "capture")]
    public void CancellingWithoutChangingSelectionKeepsDraftsAndHistory(string mode, string cancellation)
    {
        using var host = new Host();
        host.SetDrafts();
        var state = host.CaptureState();
        var start = host.Start(mode);
        var end = host.End(mode);
        var button = mode == "pan" ? MouseButton.Middle : MouseButton.Left;
        var modifiers = mode == "pan" ? RawInputModifiers.MiddleMouseButton
            : RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt;
        host.Window.MouseMove(start);
        host.Window.MouseDown(start, button);
        host.Window.MouseMove(end, modifiers);
        Assert.That(host.Canvas.IsInteracting, Is.True);
        var pointer = host.LastPointer;
        Assert.That(pointer, Is.Not.Null);

        if (cancellation == "escape")
            host.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        else
        {
            pointer!.Capture(host.Window);
            Assert.That(pointer.Captured, Is.SameAs(host.Window), "Cancellation must retain the new capture owner.");
        }

        host.AssertUnchanged(state);
        pointer!.Capture(null);
        host.Window.MouseUp(end, button);
        host.AssertUnchanged(state);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void CancellingPanAfterAnInPlaceCanvasResizeRefreshesTheChangedContext(bool width)
    {
        using var host = new Host();
        host.SetDrafts();
        var document = host.Edit.Document;
        var point = host.Start("pan");
        host.Window.MouseMove(point);
        host.Window.MouseDown(point, MouseButton.Middle);
        Assert.That(host.Canvas.IsInteracting, Is.True);

        host.Edit.UpdateDocument(current =>
        {
            if (width) current.CanvasWidth = 1200;
            else current.CanvasHeight = 700;
        });
        Assert.That(host.Edit.Document, Is.SameAs(document));
        host.Canvas.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(host.Canvas.IsInteracting, Is.False);
            Assert.That(host.Field("CanvasWidth").Text, Is.EqualTo(width ? "1200" : "1000"));
            Assert.That(host.Field("CanvasHeight").Text, Is.EqualTo(width ? "600" : "700"));
            Assert.That(host.Field("LayerName").Text, Is.EqualTo(host.Layer.Name));
            Assert.That(host.Field("TextContent").Text, Is.EqualTo(host.Layer.Text));
            Assert.That(host.Field("LayerFontSize").Text, Is.EqualTo("54"));
            Assert.That(host.Window.Title, Does.Contain("*"));
            Assert.That(host.Edit.IsDirty, Is.True);
            Assert.That(host.Edit.UndoCount, Is.EqualTo(2), "Only the canvas resize adds history; cancelling pan does not.");
            Assert.That(host.Edit.RedoCount, Is.Zero);
            Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(new[] { host.Id }));
            Assert.That(UiTestActions.Error(host.Window), Is.Null);
        });
        host.AssertListSelection();
        host.Window.MouseUp(point, MouseButton.Middle);
    }

    [Test]
    public void SelectingAnotherLayerStillRefreshesTheListAndProperties()
    {
        using var host = new Host();
        host.SetDrafts();
        var before = host.Edit.Document.Copy();
        var point = host.View(LayerGeometry.Centre(host.Edit.Document.Find(host.SecondId).Bounds));
        host.Window.MouseMove(point);
        host.Window.MouseDown(point, MouseButton.Left);
        host.Window.MouseUp(point, MouseButton.Left);

        Assert.Multiple(() =>
        {
            Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(new[] { host.SecondId }));
            Assert.That(host.Field("LayerName").Text, Is.EqualTo("第二图层"));
            Assert.That(host.Field("TextContent").Text, Is.EqualTo("另一图层的文字"));
            Assert.That(host.Field("LayerFontSize").Text, Is.EqualTo("54"));
            Assert.That(host.Edit.Document.StateEquals(before), Is.True);
            Assert.That(host.Edit.UndoCount, Is.EqualTo(1));
            Assert.That(host.Edit.RedoCount, Is.EqualTo(1));
            Assert.That(host.Edit.IsDirty, Is.False);
        });
        host.AssertListSelection();
    }

    [Test]
    public void ACommittedMoveStillRefreshesPropertiesAndRecordsExactlyOneEdit()
    {
        using var host = new Host();
        host.SetDrafts();
        var before = host.Edit.Document.Copy();
        var start = host.Start("move");
        var end = host.End("move");
        host.Window.MouseMove(start);
        host.Window.MouseDown(start, MouseButton.Left);
        host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
        host.Window.MouseUp(end, MouseButton.Left);

        Assert.Multiple(() =>
        {
            Assert.That(host.Layer.Bounds.X, Is.EqualTo(before.Find(host.Id).Bounds.X + 50).Within(1e-8));
            Assert.That(host.Layer.Bounds.Y, Is.EqualTo(before.Find(host.Id).Bounds.Y + 30).Within(1e-8));
            Assert.That(host.Field("LayerX").Text, Is.EqualTo("200"));
            Assert.That(host.Field("LayerY").Text, Is.EqualTo("230"));
            Assert.That(host.Field("LayerName").Text, Is.EqualTo(host.Layer.Name));
            Assert.That(host.Field("TextContent").Text, Is.EqualTo(host.Layer.Text));
            Assert.That(host.Field("LayerFontSize").Text, Is.EqualTo("54"));
            Assert.That(host.Edit.SelectedLayerIds, Is.EqualTo(new[] { host.Id }));
            Assert.That(host.Edit.UndoCount, Is.EqualTo(2));
            Assert.That(host.Edit.RedoCount, Is.Zero);
            Assert.That(host.Edit.IsDirty, Is.True);
            Assert.That(host.Canvas.IsInteracting, Is.False);
        });
        host.AssertListSelection();
        UiTestActions.Click(host.Window, "Undo");
        Assert.That(host.Edit.Document.StateEquals(before), Is.True);
        Assert.That(host.Edit.IsDirty, Is.False);
    }

    private sealed record State(EditorDocument Document, Guid[] Selection,
        string? Text, string? Name, string? FontSize, int Caret, int Start, int End);

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        public EditSession Edit { get; }
        public MainWindow Window { get; }
        public SceneCanvas Canvas { get; }
        public Guid Id { get; }
        public Guid SecondId { get; }
        public IPointer? LastPointer { get; private set; }
        public Layer Layer => Edit.Document.Find(Id);

        public Host(bool multiple = false)
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            var first = new Layer { Kind = LayerKind.Text, Name = "初始图层", Text = "已经应用的文字",
                FontSize = 54, Bounds = new(150, 200, 320, 160) };
            var second = new Layer { Kind = LayerKind.Text, Name = "第二图层", Text = "另一图层的文字",
                FontSize = 54, Bounds = new(650, 360, 240, 120) };
            Id = first.Id;
            SecondId = second.Id;
            Edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
                Layers = [first, second] });
            Edit.UpdateLayer(Id, layer => layer.Name = "已经应用的名称");
            Edit.UpdateLayer(Id, layer => layer.ColorArgb = 0xFFCF1313);
            Edit.Undo();
            Edit.MarkSaved();
            Edit.SetSelection(multiple ? [Id, SecondId] : [Id]);
            Window = new MainWindow(Edit);
            Window.Show();
            Canvas = Window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            Canvas.AddHandler(InputElement.PointerPressedEvent, (_, args) => LastPointer = args.Pointer,
                RoutingStrategies.Bubble, handledEventsToo: true);
        }

        public TextBox Field(string name) => Window.GetLogicalDescendants().OfType<TextBox>()
            .Single(field => field.Name == name);

        public void SetDrafts()
        {
            Assert.That(UiTestActions.Focus(Field("LayerName")), Is.True);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Field("LayerName").Text = NameDraft;
            Field("LayerFontSize").Text = "NaN";
            var text = Field("TextContent");
            text.Text = TextDraft;
            text.CaretIndex = TextDraft.Length;
            text.SelectionStart = TextDraft.Length - 5;
            text.SelectionEnd = TextDraft.Length - 1;
        }

        public State CaptureState()
        {
            var text = Field("TextContent");
            return new(Edit.Document.Copy(), Edit.SelectedLayerIds.ToArray(),
                text.Text, Field("LayerName").Text, Field("LayerFontSize").Text,
                text.CaretIndex, text.SelectionStart, text.SelectionEnd);
        }

        public void AssertUnchanged(State state)
        {
            Assert.Multiple(() =>
            {
                var text = Field("TextContent");
                Assert.That(text.Text, Is.EqualTo(state.Text));
                Assert.That(Field("LayerName").Text, Is.EqualTo(state.Name));
                Assert.That(Field("LayerFontSize").Text, Is.EqualTo(state.FontSize));
                Assert.That(text.CaretIndex, Is.EqualTo(state.Caret));
                Assert.That(text.SelectionStart, Is.EqualTo(state.Start));
                Assert.That(text.SelectionEnd, Is.EqualTo(state.End));
                Assert.That(Edit.Document.StateEquals(state.Document), Is.True);
                Assert.That(Edit.SelectedLayerIds, Is.EqualTo(state.Selection));
                Assert.That(Edit.UndoCount, Is.EqualTo(1));
                Assert.That(Edit.RedoCount, Is.EqualTo(1));
                Assert.That(Edit.IsDirty, Is.False);
                Assert.That(Canvas.IsInteracting, Is.False);
                Assert.That(UiTestActions.Error(Window), Is.Null);
            });
            AssertListSelection();
        }

        public void AssertListSelection()
        {
            var list = Window.GetLogicalDescendants().OfType<ListBox>().Single();
            Assert.That(list.SelectedItems!.Cast<object>().Select(item => item.ToString()!.Split('：')[^1]),
                Is.EquivalentTo(Edit.SelectedLayerIds.Select(id => Edit.Document.Find(id).Name)));
        }

        private ViewportTransform Transform() => new(Edit.Document.CanvasWidth, Edit.Document.CanvasHeight,
            Canvas.Bounds.Width, Canvas.Bounds.Height);

        public Point View(PointD logical)
        {
            var point = Transform().ToView(logical);
            return Canvas.TranslatePoint(new(point.X, point.Y), Window)!.Value;
        }

        public Point Start(string mode) => mode switch
        {
            "resize" => View(LayerGeometry.HandlePoint(Layer.Bounds, ResizeHandle.BottomRight)),
            "rotate" => View(new(Layer.Bounds.X + Layer.Bounds.Width / 2,
                Layer.Bounds.Y - 24 / Transform().Scale)),
            "pan" => View(new(50, 50)),
            _ => View(LayerGeometry.Centre(Layer.Bounds))
        };

        public Point End(string mode)
        {
            var start = Start(mode);
            if (mode == "rotate")
                return View(LayerGeometry.Rotate(new(Layer.Bounds.X + Layer.Bounds.Width / 2,
                    Layer.Bounds.Y - 24 / Transform().Scale), LayerGeometry.Centre(Layer.Bounds), 45));
            if (mode == "pan") return new(start.X + 60, start.Y + 30);
            var logical = mode == "resize"
                ? LayerGeometry.HandlePoint(Layer.Bounds, ResizeHandle.BottomRight) : LayerGeometry.Centre(Layer.Bounds);
            return View(new(logical.X + 50, logical.Y + 30));
        }

        public void Dispose()
        {
            LastPointer?.Capture(null);
            Edit.CancelGesture();
            Canvas.Refresh();
            Edit.MarkSaved();
            Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
