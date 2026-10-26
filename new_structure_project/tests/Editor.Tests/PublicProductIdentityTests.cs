using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Formats.Compatibility;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class PublicProductIdentityTests
{
    [Test]
    public void WindowAndAssemblyIdentifyTheFirstPublicLayoutStudioRelease()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(new EditorDocument());
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            Assert.That(window.Title, Does.StartWith("Phigros Layout Studio 1.0 — "));
            UiTestActions.ClickNamed(window, "AddText");
            Assert.That(window.Title, Does.StartWith("Phigros Layout Studio 1.0 — ").And.EndsWith(" *"));
            var assembly = typeof(App).Assembly;
            Assert.Multiple(() =>
            {
                Assert.That(assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product,
                    Is.EqualTo("Phigros Layout Studio"));
                Assert.That(assembly.GetCustomAttribute<AssemblyTitleAttribute>()!.Title,
                    Is.EqualTo("Phigros Layout Studio"));
                Assert.That(assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description,
                    Is.EqualTo("Phigros 曲目排版工作室（PLS）：曲目预览排版编辑器"));
                Assert.That(assembly.GetName().Version, Is.EqualTo(new Version(1, 0, 0, 0)));
                Assert.That(App.ProductVersion, Is.EqualTo("1.0.0"));
            });
        }
        finally { edit.MarkSaved(); window.Close(); }
    }

    [Test]
    public void SourceImportDiagnosticsDescribeRenderingWithoutInternalProductNumbers()
    {
        AvaloniaTestHost.EnsureStarted();
        var options = new CompatibilityOptions
        {
            ProbeImageSize = ImageAssetValidator.ReadEncodedDimensions,
            ProbeImageOpacity = asset => ImageAssetValidator.IsOpaque(asset, TestImages.Decoder)
        };
        var imported = 0;
        foreach (var fixture in CompatibilityEvidenceFixtures.Create().Where(item => item.ExpectedError is null))
        {
            var result = CompatibilityService.ImportBytes(Encoding.UTF8.GetBytes(fixture.Xml.ToString()), options);
            var descriptions = result.Report.Diagnostics.Select(item => item.Message)
                .Concat(result.Report.Dimensions.Select(item => item.Detail))
                .Append(result.Report.Summary);
            foreach (var description in descriptions)
                Assert.That(Regex.IsMatch(description, @"\bV[12]\b", RegexOptions.IgnoreCase), Is.False,
                    $"{fixture.Id}: user-visible import text must not expose internal product generations: {description}");
            imported++;
        }
        Assert.That(imported, Is.GreaterThan(0));
    }

    [Test]
    public void OrdinaryInterfaceAndImportStatusDoNotSuggestAnEarlierPublishedEdition()
    {
        AvaloniaTestHost.EnsureStarted();
        var edit = new EditSession(new EditorDocument());
        var window = new MainWindow(edit);
        try
        {
            window.Show();
            var texts = window.GetLogicalDescendants().OfType<TextBlock>()
                .Select(block => block.Text).Where(text => text is not null).ToArray();
            Assert.That(texts.Any(text => Regex.IsMatch(text!, @"\bV[12]\b|旧版|旧工程|增强版")), Is.False);
            var imported = UiText.Translate("Imported source document sample. Save Project writes a new .ppgproj; the source file stays protected.");
            Assert.That(imported, Does.StartWith("文件已导入。").And.Contain("原文件受保护"));
            Assert.That(UiText.Translate("The file is not a recognised project document."), Does.Contain(".ppgproj"));
            Assert.That(edit.IsDirty, Is.False);
            Assert.That(edit.UndoCount, Is.Zero);
        }
        finally { window.Close(); }
    }
}
