using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3YFocusSelectionTests
{
    [Test]
    public void InvertSelectionUsesOnlyVisibleUnlockedLayersAndNeverEditsHistory()
    {
        var front = new Layer
        {
            Kind = LayerKind.Shape, Name = "front",
            Bounds = new RectD(20, 20, 100, 80)
        };
        var middle = new Layer
        {
            Kind = LayerKind.Shape, Name = "middle",
            Bounds = new RectD(150, 20, 100, 80)
        };
        var hidden = new Layer
        {
            Kind = LayerKind.Shape, Name = "hidden", Visible = false,
            Bounds = new RectD(280, 20, 100, 80)
        };
        var locked = new Layer
        {
            Kind = LayerKind.Shape, Name = "locked", Locked = true,
            Bounds = new RectD(410, 20, 100, 80)
        };
        var back = new Layer
        {
            Kind = LayerKind.Shape, Name = "back",
            Bounds = new RectD(540, 20, 100, 80)
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 700, CanvasHeight = 180,
            Layers = [front, middle, hidden, locked, back]
        });
        edit.SetSelection([front.Id]);

        edit.InvertEditableSelection();
        Assert.Multiple(() =>
        {
            Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { back.Id, middle.Id }));
            Assert.That(edit.SelectedLayerIds, Does.Not.Contain(hidden.Id));
            Assert.That(edit.SelectedLayerIds, Does.Not.Contain(locked.Id));
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
        });

        edit.SetSelection([middle.Id]);
        edit.InvertEditableSelection([middle.Id, front.Id]);
        Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { front.Id }),
            "a filtered eligible set narrows the complement without changing the model");
        Assert.That(edit.UndoCount, Is.Zero);
    }

    [Test]
    public void F6CyclesAllFourZonesRestoresTheLastPropertyFieldAndSkipsHiddenRulers()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var edit = Session();
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            window.UpdateLayout();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var layers = window.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "LayerList");
            var name = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerName");
            var xField = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerX");
            var topRuler = window.GetLogicalDescendants().OfType<CanvasRuler>()
                .Single(ruler => ruler.Name == "HorizontalRuler");
            var rulers = window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "ShowRulers");
            rulers.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            canvas.Focus();
            PressF6(window);
            Assert.That(window.FocusManager!.GetFocusedElement(), Is.SameAs(layers));
            PressF6(window);
            Assert.That(window.FocusManager.GetFocusedElement(), Is.SameAs(name));

            Assert.That(xField.Focus(), Is.True);
            PressF6(window);
            Assert.That(window.FocusManager.GetFocusedElement(), Is.SameAs(topRuler));
            PressF6(window, shift: true);
            Assert.That(window.FocusManager.GetFocusedElement(), Is.SameAs(xField),
                "the property zone restores its last focused field");
            PressF6(window, shift: true);
            Assert.That(window.FocusManager.GetFocusedElement(), Is.SameAs(layers));
            PressF6(window, shift: true);
            Assert.That(window.FocusManager.GetFocusedElement(), Is.SameAs(canvas));

            rulers.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            canvas.Focus();
            PressF6(window, shift: true);
            Assert.That(window.FocusManager.GetFocusedElement(), Is.SameAs(xField),
                "hidden rulers are omitted from the cycle");
            Assert.That(UiTestActions.Status(window), Does.Contain("Shift+F6"));
            Assert.That(edit.UndoCount, Is.Zero);
            Assert.That(edit.IsDirty, Is.False);
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void F6DoesNotStealAnImePreeditButStillMovesAfterTheCompositionEnds()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var edit = Session();
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            window.UpdateLayout();
            var name = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerName");
            var presenter = name.GetVisualDescendants()
                .OfType<Avalonia.Controls.Presenters.TextPresenter>().Single();
            Assert.That(name.Focus(), Is.True);
            presenter.PreeditText = "ni";
            Dispatcher.UIThread.RunJobs();

            PressF6(window);
            Assert.That(window.FocusManager!.GetFocusedElement(), Is.SameAs(name));
            Assert.That(presenter.PreeditText, Is.EqualTo("ni"));
            Assert.That(UiTestActions.Status(window), Is.Not.Contain("焦点："));

            presenter.PreeditText = null;
            Dispatcher.UIThread.RunJobs();
            PressF6(window);
            Assert.That(window.FocusManager.GetFocusedElement(), Is.Not.SameAs(name));
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void FocusedCanvasListAndTextBoxKeepTheirOwnArrowKeyOwners()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var first = new Layer
        {
            Kind = LayerKind.Shape, Name = "first",
            Bounds = new RectD(40, 40, 140, 90)
        };
        var second = new Layer
        {
            Kind = LayerKind.Shape, Name = "second",
            Bounds = new RectD(240, 40, 140, 90)
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [first, second]
        });
        edit.SetSelection([first.Id]);
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            window.UpdateLayout();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var list = window.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "LayerList");

            Assert.That(canvas.Focus(), Is.True);
            var originalX = edit.Document.Find(first.Id).Bounds.X;
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.That(edit.Document.Find(first.Id).Bounds.X, Is.EqualTo(originalX + 1));
            Assert.That(edit.UndoCount, Is.EqualTo(1));

            PressF6(window);
            Assert.That(window.FocusManager!.GetFocusedElement(), Is.SameAs(list));
            var documentBeforeListKey = edit.Document.Copy();
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.That(edit.Document.StateEquals(documentBeforeListKey), Is.True);
            Assert.That(edit.UndoCount, Is.EqualTo(1));

            PressF6(window);
            Assert.That(window.FocusManager!.GetFocusedElement(), Is.InstanceOf<TextBox>());
            var propertyField = (TextBox)window.FocusManager.GetFocusedElement()!;
            var textBefore = propertyField.Text;
            var xBefore = edit.Document.Find(first.Id).Bounds.X;
            window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.None, null);
            Assert.That(edit.Document.Find(first.Id).Bounds.X, Is.EqualTo(xBefore));
            Assert.That(propertyField.Text, Is.EqualTo(textBefore));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void SelectionCommandsRespectTheFilterAndExcludeHiddenOrLockedRows()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var first = new Layer
        {
            Kind = LayerKind.Shape, Name = "Match A",
            Bounds = new RectD(20, 20, 100, 80)
        };
        var second = new Layer
        {
            Kind = LayerKind.Shape, Name = "Match B",
            Bounds = new RectD(150, 20, 100, 80)
        };
        var hidden = new Layer
        {
            Kind = LayerKind.Shape, Name = "Match Hidden", Visible = false,
            Bounds = new RectD(280, 20, 100, 80)
        };
        var locked = new Layer
        {
            Kind = LayerKind.Shape, Name = "Match Locked", Locked = true,
            Bounds = new RectD(410, 20, 100, 80)
        };
        var other = new Layer
        {
            Kind = LayerKind.Shape, Name = "Other",
            Bounds = new RectD(540, 20, 100, 80)
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 700, CanvasHeight = 180,
            Layers = [first, second, hidden, locked, other]
        });
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var search = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "LayerSearch");
            search.Text = "Match";
            window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "FilterLayerMatches").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            edit.SetSelection([first.Id]);
            UiTestActions.ClickNamed(window, "InvertSelection");
            Assert.Multiple(() =>
            {
                Assert.That(edit.SelectedLayerIds, Is.EqualTo(new[] { second.Id }));
                Assert.That(UiTestActions.Status(window), Does.Contain("反向选择 1"));
                Assert.That(edit.UndoCount, Is.Zero);
                Assert.That(edit.IsDirty, Is.False);
            });

            UiTestActions.ClickNamed(window, "ClearSelection");
            Assert.That(edit.SelectedLayerIds, Is.Empty);
            var layers = window.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "LayerList");
            Assert.That(layers.Focus(), Is.True);
            window.KeyPress(Key.A, RawInputModifiers.Control | RawInputModifiers.Shift,
                PhysicalKey.None, null);
            Assert.That(edit.SelectedLayerIds,
                Is.EquivalentTo(new[] { first.Id, second.Id }));
            Assert.That(edit.UndoCount, Is.Zero);
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void ZoomRowMenuAndCompactButtonsExposeAccessibleNamesAndTooltips()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var edit = Session();
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var layers = window.GetLogicalDescendants().OfType<ListBox>()
                .Single(box => box.Name == "LayerList");
            var zoom = window.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "ZoomStatus");
            var previous = window.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "PreviousLayerMatch");
            var selectGroup = window.GetLogicalDescendants().OfType<Button>()
                .Single(button => button.Name == "SelectGroupOverview");
            var visibility = window.GetVisualDescendants().OfType<Button>()
                .First(button => button.Name == "LayerVisibilityButton");
            var row = window.GetVisualDescendants().OfType<Grid>()
                .First(grid => grid.ContextMenu is not null);
            var context = row.ContextMenu!;
            var rename = context.Items.OfType<MenuItem>()
                .Single(item => item.Name == "LayerContextRename");

            Assert.Multiple(() =>
            {
                Assert.That(AutomationProperties.GetName(canvas), Is.EqualTo("画布"));
                Assert.That(AutomationProperties.GetName(layers), Is.EqualTo("图层列表"));
                Assert.That(AutomationProperties.GetName(zoom), Does.StartWith("视图缩放"));
                Assert.That(ToolTip.GetTip(zoom)?.ToString(), Does.Contain("Ctrl+0"));
                Assert.That(AutomationProperties.GetName(previous), Is.EqualTo("上一个匹配图层"));
                Assert.That(ToolTip.GetTip(previous)?.ToString(), Does.Contain("匹配"));
                Assert.That(AutomationProperties.GetName(selectGroup), Is.EqualTo("选择整组"));
                Assert.That(ToolTip.GetTip(selectGroup)?.ToString(), Does.Contain("隐藏和锁定"));
                Assert.That(AutomationProperties.GetName(visibility), Is.EqualTo("隐藏图层"));
                Assert.That(ToolTip.GetTip(visibility)?.ToString(), Is.EqualTo("隐藏此图层"));
                Assert.That(AutomationProperties.GetName(context), Is.EqualTo("图层行操作"));
                Assert.That(AutomationProperties.GetName(rename), Is.EqualTo("重命名图层"));
                Assert.That(ToolTip.GetTip(rename)?.ToString(),
                    Is.EqualTo("重命名当前右键点击的图层。"));
            });
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    [Test]
    public void CustomCanvasAndRulersExposeNamedPanePeersAndStatusIsLive()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var edit = Session();
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var rulers = window.GetLogicalDescendants().OfType<CanvasRuler>().ToArray();
            var status = window.GetLogicalDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "EditorStatus");
            var canvasPeer = ControlAutomationPeer.CreatePeerForElement(canvas);
            Assert.That(canvas.Focus(), Is.True);
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(canvasPeer.GetName(), Is.EqualTo("画布"));
                Assert.That(canvasPeer.GetAutomationControlType(),
                    Is.EqualTo(AutomationControlType.Pane));
                Assert.That(canvasPeer.GetHelpText(), Does.Contain("方向键"));
                Assert.That(AutomationProperties.GetAutomationId(canvas),
                    Is.EqualTo("SceneCanvas"));
                Assert.That(canvasPeer.IsKeyboardFocusable(), Is.True);
                Assert.That(canvasPeer.HasKeyboardFocus(), Is.True);
                Assert.That(rulers, Has.Length.EqualTo(2));
                Assert.That(rulers.Select(ruler =>
                        ControlAutomationPeer.CreatePeerForElement(ruler).GetName()),
                    Is.EquivalentTo(new[] { "水平标尺", "垂直标尺" }));
                Assert.That(rulers.Select(ruler =>
                        ControlAutomationPeer.CreatePeerForElement(ruler).GetAutomationControlType()),
                    Is.All.EqualTo(AutomationControlType.Pane));
                Assert.That(rulers.Select(AutomationProperties.GetAutomationId),
                    Is.EquivalentTo(new[] { "HorizontalRuler", "VerticalRuler" }));
                Assert.That(AutomationProperties.GetLiveSetting(status),
                    Is.EqualTo(AutomationLiveSetting.Polite));
            });
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static void PressF6(Window window, bool shift = false) =>
        window.KeyPress(Key.F6,
            shift ? RawInputModifiers.Shift : RawInputModifiers.None,
            PhysicalKey.None, null);

    private static EditSession Session()
    {
        var layer = new Layer
        {
            Kind = LayerKind.Text, Name = "索引用图层", Text = "测试",
            Bounds = new RectD(60, 50, 320, 100), FontSize = 42
        };
        var edit = new EditSession(new EditorDocument
        {
            CanvasWidth = 640, CanvasHeight = 360, Layers = [layer]
        });
        edit.SelectedLayerId = layer.Id;
        return edit;
    }
}
