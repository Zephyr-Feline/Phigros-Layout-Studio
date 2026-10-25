using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

[NonParallelizable]
public sealed class P6WindowLayoutTests
{
    [TestCase(1280, 720, LayerKind.Text)]
    [TestCase(1280, 720, LayerKind.Image)]
    [TestCase(800, 500, LayerKind.Text)]
    [TestCase(800, 500, LayerKind.Image)]
    public void CompactChineseWindowKeepsCanvasAndSelectedPropertiesUsable(int width, int height, LayerKind kind)
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var imageId = Guid.NewGuid();
        var selected = new Layer { Kind = kind, Name = kind == LayerKind.Text ? "中文标题" : "测试曲绘",
            Text = "中文标题 / Preview", Bounds = new(100, 100, 420, 180),
            AssetId = kind == LayerKind.Image ? imageId : null };
        var document = new EditorDocument { CanvasWidth = 1440, CanvasHeight = 1080, Layers = [selected] };
        if (kind == LayerKind.Image) document.AddAsset(imageId, P4AutomaticBackgroundTests.Solid(0xFF00A7C2));
        var edit = new EditSession(document) { SelectedLayerId = selected.Id };
        var window = new MainWindow(edit) { Width = width, Height = height };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var logical = window.GetLogicalDescendants().OfType<Control>().ToArray();
            var menu = logical.OfType<Menu>().Single(control => control.Name == "MainMenu");
            var toolbar = logical.Single(control => control.Name == "MainToolbar");
            var canvas = window.GetVisualDescendants().OfType<SceneCanvas>().Single();
            var properties = logical.OfType<Expander>().Single(control => control.Name == "LayerProperties");
            var name = logical.OfType<TextBox>().Single(control => control.Name == "LayerName");
            var buttons = toolbar.GetLogicalDescendants().OfType<Button>().ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()),
                    Is.EqualTo(new[] { "文件", "编辑", "添加", "图层", "排列", "视图", "模板" }));
                Assert.That(buttons, Has.Length.EqualTo(6), "only frequent actions consume toolbar space");
                Assert.That(buttons.Select(button => button.Content?.ToString()), Is.EqualTo(new[]
                    { "打开工程", "保存工程", "导入图片", "添加文字", "撤销", "适合窗口" }));
                Assert.That(toolbar.Bounds.Height, Is.LessThanOrEqualTo(72), "toolbar must leave room for the canvas");
                Assert.That(canvas.Bounds.Width, Is.GreaterThanOrEqualTo(width * 0.45));
                Assert.That(canvas.Bounds.Height, Is.GreaterThanOrEqualTo(height * 0.60));
                Assert.That(properties.IsExpanded, Is.True, "the primary property panel is immediately available");
                Assert.That(name.Text, Is.EqualTo(selected.Name));
                Assert.That(name.IsVisible, Is.True);
                Assert.That(name.IsEnabled, Is.True);
                Assert.That(edit.IsDirty, Is.False, "layout and selection do not edit the document");
            });
            var toolbarAt = toolbar.TranslatePoint(default, window)!.Value;
            foreach (var button in buttons)
            {
                var at = button.TranslatePoint(default, window)!.Value;
                Assert.That(at.Y, Is.GreaterThanOrEqualTo(toolbarAt.Y));
                Assert.That(at.X + button.Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width));
            }
            if (width == 1280)
            {
                foreach (var fieldName in new[] { "LayerWidth", "LayerHeight", "LayerRotation", "LayerKeepAspect" })
                {
                    var field = logical.Single(control => control.Name == fieldName);
                    var at = field.TranslatePoint(default, window)!.Value;
                    Assert.That(at.Y + field.Bounds.Height, Is.LessThanOrEqualTo(
                        canvas.TranslatePoint(default, window)!.Value.Y + canvas.Bounds.Height),
                        $"{fieldName} should be discoverable without scrolling at this size");
                }
            }
            UiTestActions.Reveal(name);
            var propertyAt = name.TranslatePoint(default, window)!.Value;
            Assert.That(propertyAt.X + name.Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width));
            Assert.That(propertyAt.Y, Is.GreaterThanOrEqualTo(0));
            Assert.That(propertyAt.Y + name.Bounds.Height, Is.LessThanOrEqualTo(window.ClientSize.Height),
                "selected properties must remain reachable in the scrollable side panel");
            using var screenshot = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            screenshot.Render(window);
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"ux-{width}x{height}-{kind}.png");
            screenshot.Save(path, new PngBitmapEncoderOptions());
            Assert.That(new FileInfo(path).Length, Is.GreaterThan(1024));
            TestContext.AddTestAttachment(path, "Headless layout screenshot; physical Windows DPI is verified separately.");
        }
        finally
        {
            edit.MarkSaved();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }
}
