using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Application;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class R3BackgroundContributionUiTests
{
    [Test]
    public void DesktopDialogAppliesSelectedContributorsAndTransition()
    {
        AvaloniaTestHost.EnsureStarted();
        var theme = new FluentTheme();
        Avalonia.Application.Current!.Styles.Add(theme);
        var document = P4AutomaticBackgroundTests.MakeDocument(2, 800, 600);
        var ids = document.Layers.Select(layer => layer.Id).ToArray();
        var edit = new EditSession(document);
        var window = new MainWindow(edit, recovery: EmptyRecovery(edit));
        try
        {
            window.Show();
            var overlay = window.GetLogicalDescendants().OfType<CheckBox>()
                .Single(box => box.Name == "ShowBackgroundContributors");
            overlay.IsChecked = true;
            Assert.That(window.GetLogicalDescendants().OfType<SceneCanvas>().Single()
                .ShowBackgroundContributors, Is.True);
            overlay.IsChecked = false;
            UiTestActions.ClickNamed(window, "OpenBackgroundContribution");
            PumpUntil(() => window.OwnedWindows.OfType<BackgroundContributionDialog>().Any());
            var dialog = window.OwnedWindows.OfType<BackgroundContributionDialog>().Single();
            var mode = dialog.GetLogicalDescendants().OfType<ComboBox>()
                .Single(box => box.Name == "BackgroundContributionMode");
            mode.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var rows = dialog.GetLogicalDescendants().OfType<CheckBox>()
                .Where(box => box.Name?.StartsWith("BackgroundContributor_", StringComparison.Ordinal) == true)
                .ToArray();
            Assert.That(rows, Has.Length.EqualTo(2));
            Assert.That(rows[0].IsEnabled, Is.True);
            rows[0].IsChecked = true;
            rows[1].IsChecked = false;
            dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "BackgroundRange").Text = "0.4";
            dialog.GetLogicalDescendants().OfType<TextBox>()
                .Single(box => box.Name == "BackgroundTransition").Text = "0.9";
            UiTestActions.ClickNamed(dialog, "ApplyBackgroundContribution");
            PumpUntil(() => edit.UndoCount == 1 &&
                edit.Document.BackgroundBlend.ContributionMode == BackgroundContributionMode.Selected);
            Assert.That(edit.Document.BackgroundBlend.SelectedArtworkLayerIds, Is.EqualTo(new[] { ids[0] }));
            Assert.That(edit.Document.BackgroundBlend.Range, Is.EqualTo(0.4));
            Assert.That(edit.Document.BackgroundBlend.Transition, Is.EqualTo(0.9));
            edit.Undo();
            Assert.That(edit.Document.BackgroundBlend.ContributionMode,
                Is.EqualTo(BackgroundContributionMode.Automatic));
        }
        finally
        {
            edit.MarkSaved();
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }

    private static AutoRecoveryService EmptyRecovery(EditSession edit) =>
        new(edit, Path.Combine(Path.GetTempPath(),
            "r3c-background-recovery-" + Guid.NewGuid().ToString("N")[..8]),
            "r3c-test", () => DateTimeOffset.Now, (_, _) => false);

    private static void PumpUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.That(condition(), Is.True, "The UI operation did not complete.");
    }
}
