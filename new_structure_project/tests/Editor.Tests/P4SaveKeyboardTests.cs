using Avalonia.LogicalTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4SaveKeyboardTests
{
    [Test]
    public void ControlSAndControlShiftSUseNativeSaveAndSaveAs()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var directory = Path.Combine(Path.GetTempPath(), "p4-save-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "first.ppgproj");
        var second = Path.Combine(directory, "second.ppgproj");
        var blocked = Path.Combine(directory, "blocked.ppgproj");
        Directory.CreateDirectory(blocked);
        var layer = new Layer { Kind = LayerKind.Text, Name = "Title", Text = "first",
            Bounds = new RectD(20, 20, 200, 80) };
        var edit = new EditSession(new EditorDocument { Layers = [layer] });
        var native = new NativeProjectWorkflow(edit, document => document.Validate(), "p4-test");
        native.SaveAs(first);
        string? nextPath = second;
        var window = new MainWindow(edit, nativeWorkflow: native,
            nativeSavePathPicker: () => Task.FromResult<string?>(nextPath));
        try
        {
            window.Show();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            canvas.Focus();
            edit.UpdateLayer(layer.Id, item => item.Text = "saved in place");
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.Multiple(() =>
            {
                Assert.That(native.IsDirty, Is.False);
                Assert.That(native.CurrentPath, Is.EqualTo(Path.GetFullPath(first)));
                Assert.That(NativePackageStore.Read(first).Find(layer.Id).Text, Is.EqualTo("saved in place"));
            });

            edit.UpdateLayer(layer.Id, item => item.Text = "saved as second");
            var row = window.GetVisualDescendants().OfType<ListBoxItem>().Single();
            row.Focus();
            Assert.That(row.IsFocused, Is.True);
            window.KeyPress(Key.S, RawInputModifiers.Control | RawInputModifiers.Shift,
                PhysicalKey.None, null);
            Assert.Multiple(() =>
            {
                Assert.That(native.IsDirty, Is.False);
                Assert.That(native.CurrentPath, Is.EqualTo(Path.GetFullPath(second)));
                Assert.That(NativePackageStore.Read(first).Find(layer.Id).Text, Is.EqualTo("saved in place"));
                Assert.That(NativePackageStore.Read(second).Find(layer.Id).Text, Is.EqualTo("saved as second"));
            });

            edit.UpdateLayer(layer.Id, item => item.Text = "still unsaved");
            nextPath = null;
            window.KeyPress(Key.S, RawInputModifiers.Control | RawInputModifiers.Shift,
                PhysicalKey.None, null);
            Assert.That(native.IsDirty, Is.True);
            Assert.That(native.CurrentPath, Is.EqualTo(Path.GetFullPath(second)));

            nextPath = blocked;
            window.KeyPress(Key.S, RawInputModifiers.Control | RawInputModifiers.Shift,
                PhysicalKey.None, null);
            Assert.Multiple(() =>
            {
                Assert.That(native.IsDirty, Is.True);
                Assert.That(native.CurrentPath, Is.EqualTo(Path.GetFullPath(second)));
                Assert.That(NativePackageStore.Read(second).Find(layer.Id).Text, Is.EqualTo("saved as second"));
                Assert.That(window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(block => block.Text?.StartsWith("错误：", StringComparison.Ordinal) == true), Is.True);
            });
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void ControlSFromTextFieldSavesAnUntitledProject()
    {
        AvaloniaTestHost.EnsureStarted();
        var directory = Path.Combine(Path.GetTempPath(), "p4-save-field-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "from-field.ppgproj");
        var edit = new EditSession(new EditorDocument());
        var layer = new Layer { Kind = LayerKind.Text, Text = "document text" };
        edit.AddLayer(layer);
        var native = new NativeProjectWorkflow(edit, document => document.Validate(), "p4-test");
        var window = new MainWindow(edit, nativeWorkflow: native,
            nativeSavePathPicker: () => Task.FromResult<string?>(path));
        try
        {
            window.Show();
            UiTestActions.Focus(window.GetLogicalDescendants().OfType<TextBox>()
                .Single(field => field.Name == "CanvasWidth"));
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.Multiple(() =>
            {
                Assert.That(native.IsDirty, Is.False);
                Assert.That(native.CurrentPath, Is.EqualTo(Path.GetFullPath(path)));
                Assert.That(NativePackageStore.Read(path).Find(layer.Id).Text, Is.EqualTo("document text"));
            });
        }
        finally
        {
            window.Close();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SaveShortcutRejectsActiveDragBeforeOpeningSaveAsPicker()
    {
        AvaloniaTestHost.EnsureStarted();
        var directory = Path.Combine(Path.GetTempPath(), "p4-save-gesture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "after-drag.ppgproj");
        var layer = new Layer { Kind = LayerKind.Shape, Bounds = new RectD(100, 100, 100, 100) };
        var edit = new EditSession(new EditorDocument { CanvasWidth = 1000, CanvasHeight = 600,
            Layers = [layer] });
        var native = new NativeProjectWorkflow(edit, document => document.Validate(), "p4-test");
        var pickerCalls = 0;
        var window = new MainWindow(edit, nativeWorkflow: native,
            nativeSavePathPicker: () =>
            {
                pickerCalls++;
                return Task.FromResult<string?>(path);
            });
        try
        {
            window.Show();
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var transform = new ViewportTransform(1000, 600, canvas.Bounds.Width, canvas.Bounds.Height);
            Point View(double x, double y)
            {
                var point = transform.ToView(new PointD(x, y));
                return canvas.TranslatePoint(new Point(point.X, point.Y), window)
                    ?? throw new AssertionException("The canvas is not laid out.");
            }
            window.MouseDown(View(150, 150), MouseButton.Left);
            window.MouseMove(View(180, 150), RawInputModifiers.LeftMouseButton | RawInputModifiers.Alt);
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(pickerCalls, Is.Zero);
            Assert.That(native.CurrentPath, Is.Null);
            window.MouseUp(View(180, 150), MouseButton.Left);

            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.Multiple(() =>
            {
                Assert.That(pickerCalls, Is.EqualTo(1));
                Assert.That(native.IsDirty, Is.False);
                Assert.That(NativePackageStore.Read(path).Find(layer.Id).Bounds.X,
                    Is.EqualTo(130).Within(1e-6));
            });
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void RepeatedSaveShortcutWaitsForTheFirstPicker()
    {
        AvaloniaTestHost.EnsureStarted();
        var directory = Path.Combine(Path.GetTempPath(), "p4-save-repeat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "once.ppgproj");
        var edit = new EditSession(new EditorDocument());
        edit.AddLayer(new Layer { Kind = LayerKind.Text, Text = "save once" });
        var native = new NativeProjectWorkflow(edit, document => document.Validate(), "p4-test");
        var picker = new TaskCompletionSource<string?>();
        var pickerCalls = 0;
        var window = new MainWindow(edit, nativeWorkflow: native,
            nativeSavePathPicker: () =>
            {
                pickerCalls++;
                return picker.Task;
            });
        try
        {
            window.Show();
            window.GetVisualDescendants().OfType<SceneCanvas>().Single().Focus();
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.That(pickerCalls, Is.EqualTo(1));
            Assert.That(native.IsDirty, Is.True);
            Assert.That(File.Exists(path), Is.False);

            picker.SetResult(path);
            Dispatcher.UIThread.RunJobs();
            Assert.Multiple(() =>
            {
                Assert.That(native.IsDirty, Is.False);
                Assert.That(native.CurrentPath, Is.EqualTo(Path.GetFullPath(path)));
                Assert.That(File.Exists(path), Is.True);
            });
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Directory.Delete(directory, recursive: true);
        }
    }
}
