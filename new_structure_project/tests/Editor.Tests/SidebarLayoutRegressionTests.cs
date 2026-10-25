using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

[NonParallelizable]
public sealed class SidebarLayoutRegressionTests
{
    [TestCase(1280, 720, false)]
    [TestCase(1280, 720, true)]
    [TestCase(800, 500, false)]
    [TestCase(800, 500, true)]
    public void FloatingScrollbarsAndSearchContentHaveSeparateSpace(int width, int height, bool dark)
    {
        using var host = new Host(width, height, dark);
        var search = host.Find<TextBox>("LayerSearch");
        var list = host.Find<ListBox>("LayerList");
        var outer = list.GetVisualAncestors().OfType<ScrollViewer>().First();
        var inner = list.GetVisualDescendants().OfType<ScrollViewer>().Single();
        var outerBar = VerticalBar(outer);
        var innerBar = VerticalBar(inner);
        host.Capture("top");
        Assert.Multiple(() =>
        {
            Assert.That(outer.Extent.Height, Is.GreaterThan(outer.Viewport.Height));
            Assert.That(inner.Extent.Height, Is.GreaterThan(inner.Viewport.Height));
            Assert.That(outer.AllowAutoHide && inner.AllowAutoHide, Is.True,
                "retain floating, auto-hiding scrollbars");
            Assert.That(host.Rect(innerBar).Right, Is.LessThanOrEqualTo(host.Rect(outerBar).Left),
                "the list and sidebar scrollbar hit areas must not overlap");
            foreach (var buttonName in new[] { "PreviousLayerMatch", "NextLayerMatch" })
            {
                var button = host.Find<Button>(buttonName);
                var text = button.GetVisualDescendants().OfType<TextBlock>()
                    .Single(block => block.Text == button.Content?.ToString());
                Assert.That(host.Rect(text).Top, Is.GreaterThanOrEqualTo(host.Rect(button).Top));
                Assert.That(host.Rect(text).Bottom, Is.LessThanOrEqualTo(host.Rect(button).Bottom));
                Assert.That(host.Rect(text).Center.Y, Is.EqualTo(host.Rect(button).Center.Y).Within(1),
                    "navigation captions must be vertically centred");
                Assert.That(host.Rect(button).Height, Is.EqualTo(host.Rect(search).Height).Within(1));
            }
            var placeholder = search.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Text == search.PlaceholderText && block.IsEffectivelyVisible);
            Assert.That(placeholder.TextLayout.Height, Is.LessThanOrEqualTo(placeholder.Bounds.Height + 0.5),
                "the actual placeholder line must not be cropped vertically");
            Assert.That(host.Rect(placeholder).Center.Y, Is.EqualTo(host.Rect(search).Center.Y).Within(1));
            Assert.That(host.Edit.IsDirty, Is.False);
            Assert.That(host.Edit.UndoCount, Is.Zero);
        });
    }

    [TestCase(1280, 720, false)]
    [TestCase(1280, 720, true)]
    [TestCase(800, 500, false)]
    [TestCase(800, 500, true)]
    public void SummaryAndWrappedPropertyLabelsStayInsideTheirContainers(int width, int height, bool dark)
    {
        using var host = new Host(width, height, dark);
        host.Window.FontSize = 18;
        host.Settle();
        var summary = host.Find<TextBlock>("LayerSelectionSummary");
        var summaryRow = (Control)summary.Parent!;
        var opacity = host.Find<TextBox>("LayerOpacity");
        var rotation = host.Find<TextBox>("LayerRotation");
        var labels = host.Window.GetLogicalDescendants().OfType<TextBlock>()
            .Where(block => block.Text is "不透明度（%）" or "旋转角度（°）").ToArray();
        UiTestActions.Reveal(opacity);
        host.Settle();
        host.Capture("properties");
        var help = host.Window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(block => block.Text == "单行属性按 Enter 应用；多行文字按 Enter 换行。");
        Assert.Multiple(() =>
        {
            Assert.That(summary.Text, Does.Contain("隐藏 1").And.Contain("锁定 1"));
            Assert.That(host.Rect(summary).Right, Is.LessThanOrEqualTo(host.Rect(summaryRow).Right + 0.5));
            Assert.That(summary.TextLayout.Height, Is.LessThanOrEqualTo(summary.Bounds.Height + 0.5));
            Assert.That(summary.TextLayout.Width, Is.LessThanOrEqualTo(summary.Bounds.Width + 0.5));
            Assert.That(summary.TextLayout.TextLines.All(line => !line.HasCollapsed), Is.True,
                "selection counts must be available without ellipsis or clipping");
            Assert.That(labels, Has.Length.EqualTo(2));
            foreach (var label in labels)
            {
                Assert.That(label.TextLayout.Width, Is.LessThanOrEqualTo(label.Bounds.Width + 0.5),
                    "long property labels must wrap within their column");
                Assert.That(label.TextLayout.Height, Is.LessThanOrEqualTo(label.Bounds.Height + 0.5));
            }
            Assert.That(host.Rect(opacity).Top, Is.EqualTo(host.Rect(rotation).Top).Within(0.5),
                "paired inputs must align even when their labels wrap differently");
            var foreground = ((ISolidColorBrush)help.Foreground!).Color;
            var background = dark ? Colors.Black : Colors.White;
            Assert.That(Contrast(foreground, background), Is.GreaterThanOrEqualTo(4.5),
                "enabled instructions must remain readable in the selected theme");
            Assert.That(host.Edit.IsDirty, Is.False);
            Assert.That(host.Edit.UndoCount, Is.Zero);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ImageOptionsWrapBesideTheCropButton(bool dark)
    {
        using var host = new Host(800, 500, dark, image: true);
        var slanted = host.Find<CheckBox>("LayerSlanted");
        var crop = host.Find<Button>("OpenImageCrop");
        UiTestActions.Reveal(crop);
        host.Settle();
        host.Capture("image-options");
        var caption = slanted.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "斜切边缘（图片裁切填充）");
        Assert.Multiple(() =>
        {
            Assert.That(caption.TextLayout.Height, Is.LessThanOrEqualTo(caption.Bounds.Height + 0.5));
            Assert.That(caption.TextLayout.Width, Is.LessThanOrEqualTo(caption.Bounds.Width + 0.5));
            Assert.That(host.Rect(slanted).Right, Is.LessThanOrEqualTo(host.Rect(crop).Left));
            Assert.That(host.Rect(crop).Right, Is.LessThanOrEqualTo(host.Rect((Control)crop.Parent!).Right));
            Assert.That(host.Edit.IsDirty, Is.False);
        });
    }

    [Test]
    public void LeavingCanvasClosesItsHintBeforeEnteringSidebar()
    {
        using var host = new Host(1280, 720, false);
        var canvas = host.Window.GetVisualDescendants().OfType<SceneCanvas>().Single();
        var centre = canvas.TranslatePoint(new(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2), host.Window)!.Value;
        host.Window.MouseMove(centre);
        ToolTip.SetIsOpen(canvas, true);
        host.Settle();
        Assert.That(ToolTip.GetIsOpen(canvas), Is.True);
        var search = host.Find<TextBox>("LayerSearch");
        host.Window.MouseMove(search.TranslatePoint(new(5, 5), host.Window)!.Value);
        Assert.That(ToolTip.GetIsOpen(canvas), Is.False,
            "a canvas hint must not linger over sidebar fields after the pointer leaves");
    }

    private static ScrollBar VerticalBar(ScrollViewer owner) => owner.GetVisualDescendants()
        .OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical &&
            bar.GetVisualAncestors().OfType<ScrollViewer>().First() == owner);

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) =>
            0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        private readonly string _label;
        public EditSession Edit { get; }
        public MainWindow Window { get; }

        public Host(int width, int height, bool dark, bool image = false)
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            var layers = Enumerable.Range(1, 25).Select(index => new Layer
            {
                Kind = LayerKind.Shape, Name = $"layer-{index:00}",
                Bounds = new(100 + index * 4, 100, 240, 160),
                Visible = index != 2, Locked = index == 3
            }).ToList();
            var document = new EditorDocument { Layers = layers };
            if (image)
            {
                var assetId = Guid.NewGuid();
                layers[0].Kind = LayerKind.Image;
                layers[0].AssetId = assetId;
                document.AddAsset(assetId, P4AutomaticBackgroundTests.Solid(0xFF00A7C2));
            }
            Edit = new(document);
            Edit.SetSelection(image ? [layers[0].Id] : layers.Select(layer => layer.Id));
            Window = new MainWindow(Edit)
            {
                Width = width, Height = height,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
            };
            _label = $"{width}x{height}-{(dark ? "dark" : "light")}";
            Window.Show();
            Settle();
        }

        public T Find<T>(string name) where T : Control => Window.GetLogicalDescendants()
            .OfType<T>().Single(control => control.Name == name);
        public Rect Rect(Control control) => new(control.TranslatePoint(default, Window)!.Value, control.Bounds.Size);
        public void Settle() { Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); }
        public void Capture(string section)
        {
            using var image = new RenderTargetBitmap(new((int)Window.Width, (int)Window.Height), new(96, 96));
            image.Render(Window);
            var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"sidebar-{_label}-{section}.png");
            image.Save(path, new PngBitmapEncoderOptions());
            TestContext.AddTestAttachment(path, "Headless theme/layout evidence; not physical DPI acceptance.");
        }
        public void Dispose()
        {
            Edit.MarkSaved();
            Window.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
