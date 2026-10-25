using Avalonia.LogicalTree;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using Editor.Formats;
using Editor.Rendering;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class P4LocalFontTests
{
    private string _directory = "";
    [SetUp] public void SetUp()
    {
        AvaloniaTestHost.EnsureStarted();
        _directory = Directory.CreateTempSubdirectory("ppg-local-font-").FullName;
    }
    [TearDown] public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public void ImportedTtfChangesPixelsAndSurvivesSourceDeletionLibraryReloadAndNativeRoundTrip()
    {
        var source = Path.Combine(_directory, "my-font.ttf");
        File.Copy(BundledTtf(), source);
        var libraryPath = Path.Combine(_directory, "library");
        var library = new LocalFontLibrary(libraryPath);
        var info = library.Import(source);
        Assert.That(info.Family, Does.Contain("Inter"));
        Assert.That(FontManager.Current.TryGetGlyphTypeface(new Typeface(info.Reference), out var glyph), Is.True);
        Assert.That(glyph!.FamilyName, Is.EqualTo(info.Family));
        var edit = new EditSession(TextDocument());
        var before = TestImages.RenderPng(edit.Document);
        var id = edit.Document.Layers[0].Id;
        edit.UpdateLayer(id, layer => layer.FontFamily = info.Reference);
        var after = TestImages.RenderPng(edit.Document);
        Assert.That(after, Is.Not.EqualTo(before), "the imported face changes the actual glyph pixels");
        Assert.That(edit.Document.Assets, Is.Empty, "fonts are not embedded in project assets");
        edit.Undo();
        Assert.That(TestImages.RenderPng(edit.Document), Is.EqualTo(before));
        edit.Redo();
        File.Delete(source);
        var reloaded = new LocalFontLibrary(libraryPath);
        Assert.That(reloaded.Warnings, Is.Empty);
        Assert.That(reloaded.Fonts.Single(), Is.EqualTo(info));
        var path = Path.Combine(_directory, "font.ppgproj");
        NativePackageStore.Save(path, edit.Document, "p4-font-test");
        var reopened = NativePackageStore.Read(path);
        Assert.That(reopened.Layers[0].FontFamily, Is.EqualTo(info.Reference));
        Assert.That(TestImages.RenderPng(reopened), Is.EqualTo(after));
    }

    [Test]
    public void IdenticalFontIsDeduplicatedAndBadOrOversizedFilesDoNotEnterLibrary()
    {
        var libraryPath = Path.Combine(_directory, "library");
        var library = new LocalFontLibrary(libraryPath);
        var info = library.Import(BundledTtf());
        Assert.That(library.Import(BundledTtf()), Is.EqualTo(info));
        Assert.That(Directory.GetFiles(libraryPath), Has.Length.EqualTo(1));
        var bad = Path.Combine(_directory, "bad.ttf");
        File.WriteAllBytes(bad, new byte[20]);
        Assert.Throws<InvalidDataException>(() => library.Import(bad));
        using (var stream = File.Create(bad)) stream.SetLength(LocalFontRegistry.MaxFontBytes + 1L);
        Assert.Throws<InvalidDataException>(() => library.Import(bad));
        Assert.That(library.Fonts, Has.Count.EqualTo(1));
        Assert.That(Directory.GetFiles(libraryPath), Has.Length.EqualTo(1));
    }

    [Test]
    public void MissingLocalReferenceFallsBackWithNoticeAndFontButtonIsUndoable()
    {
        var library = new LocalFontLibrary(Path.Combine(_directory, "library"));
        var info = library.Import(BundledTtf());
        var document = TextDocument();
        var normal = TestImages.RenderPng(document);
        var missing = LocalFontRegistry.Prefix + new string('0', 64) + "#Missing";
        document.Layers[0].FontFamily = missing;
        Assert.That(TestImages.RenderPng(document), Is.EqualTo(normal));
        Assert.That(document.Layers[0].FontFamily, Is.EqualTo(missing), "rendering never rewrites a missing reference");
        var edit = new EditSession(document) { SelectedLayerId = document.Layers[0].Id };
        var window = new MainWindow(edit, library);
        try
        {
            window.Show();
            var notice = window.GetVisualDescendants().OfType<TextBlock>()
                .Single(control => control.Name == "FontNotice");
            Assert.That(notice.Text, Does.Contain("以下图层缺少本地字体"));
            window.GetLogicalDescendants().OfType<ComboBox>()
                .Single(control => control.Name == "LocalFontChoices").SelectedItem = info;
            UiTestActions.Click(UiTestActions.FindNamed(window, "ApplyLocalFont"));
            Assert.That(edit.Document.Layers[0].FontFamily, Is.EqualTo(info.Reference));
            Assert.That(edit.UndoCount, Is.EqualTo(1));
            Assert.That(notice.IsVisible, Is.False);
            var fontField = window.GetLogicalDescendants().OfType<TextBox>()
                .Single(control => control.Name == "LayerFontFamily");
            Assert.That(fontField.Text, Does.Contain("（本地）"));
            Assert.That(fontField.Text, Does.Not.Contain(info.Sha256));
            window.GetLogicalDescendants().OfType<TextBox>()
                .Single(control => control.Name == "LayerFontSize").Text = "80";
            UiTestActions.Click(UiTestActions.FindNamed(window, "ApplyLayerProperties"));
            Assert.That(edit.Document.Layers[0].FontFamily, Is.EqualTo(info.Reference),
                "editing typography must retain the local font reference behind its friendly name");
            Assert.That(edit.Document.Layers[0].FontSize, Is.EqualTo(80));
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Undo(), Is.True);
            Assert.That(edit.Document.Layers[0].FontFamily, Is.EqualTo(missing));
        }
        finally { window.Close(); }
    }

    [Test]
    public void CorruptLibraryFileIsReportedWithoutPreventingOtherFontsFromLoading()
    {
        var libraryPath = Path.Combine(_directory, "library");
        var library = new LocalFontLibrary(libraryPath);
        var good = library.Import(BundledTtf());
        File.WriteAllBytes(Path.Combine(libraryPath, new string('0', 64) + ".otf"), new byte[30]);
        var reloaded = new LocalFontLibrary(libraryPath);
        Assert.That(reloaded.Fonts.Single(), Is.EqualTo(good));
        Assert.That(reloaded.Warnings, Has.Count.EqualTo(1));
    }

    [Test, Category("LocalFonts")]
    public void ExistingLocalOtfCanBeImportedWithoutBundlingItWithTests()
    {
        var source = OptionalFont("PPG_TEST_LOCAL_OTF_PATH", ".otf");
        var library = new LocalFontLibrary(Path.Combine(_directory, "otf-library"));
        var info = library.Import(source);
        Assert.That(FontManager.Current.TryGetGlyphTypeface(new Typeface(info.Reference), out var glyph), Is.True);
        Assert.That(glyph!.FamilyName, Is.EqualTo(info.Family));
        var document = TextDocument();
        document.Layers[0].FontFamily = info.Reference;
        document.Layers[0].Text = "测试中文字体";
        Assert.That(TestImages.InkBounds(TestImages.RenderPng(document)).Right, Is.GreaterThan(0));
    }

    [Test, Category("LocalFonts")]
    public void ExistingLocalTtfCanBeImportedWithoutBundlingItWithTests()
    {
        var source = OptionalFont("PPG_TEST_LOCAL_TTF_PATH", ".ttf");
        var library = new LocalFontLibrary(Path.Combine(_directory, "ttf-library"));
        var info = library.Import(source);
        Assert.That(FontManager.Current.TryGetGlyphTypeface(new Typeface(info.Reference), out var glyph), Is.True);
        Assert.That(glyph!.FamilyName, Is.EqualTo(info.Family));
        var document = TextDocument();
        document.Layers[0].FontFamily = info.Reference;
        Assert.That(TestImages.InkBounds(TestImages.RenderPng(document)).Right, Is.GreaterThan(0));
    }

    private string BundledTtf()
    {
        // Already distributed by the suite's Avalonia.Headless dependency. Core font behavior must
        // remain mandatory without relying on an OS font or a private, untracked user fixture.
        var path = Path.Combine(_directory, "fixture-inter.ttf");
        if (File.Exists(path)) return path;
        using var asset = AssetLoader.Open(new Uri("avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf"));
        using var output = File.Create(path);
        asset.CopyTo(output);
        return path;
    }

    private static string OptionalFont(string variable, string extension)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(path))
            Assert.Ignore($"Missing local input: set {variable} to an existing user-owned {extension} font; no font is downloaded.");
        if (!File.Exists(path))
            Assert.Ignore($"Missing local input: {variable}={path} does not exist; no font is downloaded.");
        Assert.That(Path.GetExtension(path), Is.EqualTo(extension).IgnoreCase, variable);
        return path!;
    }

    private static EditorDocument TextDocument() => new()
    {
        CanvasWidth = 800, CanvasHeight = 600, BackgroundArgb = 0xFF000000,
        Layers = [new Layer { Kind = LayerKind.Text, Text = "WWW iii 012345",
            FontFamily = LocalFontRegistry.Fallback, FontSize = 70, Bounds = new RectD(20, 20, 700, 200) }]
    };
}
