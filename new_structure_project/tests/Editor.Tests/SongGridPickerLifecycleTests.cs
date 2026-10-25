using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Editor.Core;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

[NonParallelizable]
public sealed class SongGridPickerLifecycleTests
{
    [Test]
    public void PendingChoiceRejectsReentryAndGenerationAndCancellationKeepsPreviousOrder()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var previous = Paths("previous");
        var calls = 0;
        using var host = new Host(_ => ++calls == 1
            ? Task.FromResult<IReadOnlyList<string>>(previous) : pending.Task);
        host.Choose();
        var before = host.Files.Items.Cast<string>().ToArray();
        host.Choose();
        host.Choose(); // Direct events also exercise the guard when the button is disabled.
        host.Generate();
        host.Field<TextBox>("SongGridGapX").Text = "25";
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(host.ChooseButton.IsEnabled, Is.False);
            Assert.That(host.GenerateButton.IsEnabled, Is.False);
            Assert.That(host.Result.IsCompleted, Is.False);
        });

        pending.SetResult([]);
        PumpUntil(() => host.ChooseButton.IsEnabled);
        Assert.That(host.Files.Items.Cast<string>(), Is.EqualTo(before));
        Assert.That(host.GenerateButton.IsEnabled, Is.True);
        host.Generate();
        PumpUntil(() => host.Result.IsCompleted);
        Assert.That(host.Result.Result!.Paths, Is.EqualTo(previous.Select(Path.GetFullPath)));
        host.AssertDocumentUnchanged();
    }

    [TestCase("success")]
    [TestCase("invalid-path")]
    [TestCase("failure")]
    [TestCase("cancel")]
    public void ClosedDialogIgnoresLatePickerResultsAndFurtherEvents(string outcome)
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var calls = 0;
        using var host = new Host(_ => ++calls == 1
            ? Task.FromResult<IReadOnlyList<string>>(Paths("original")) : pending.Task);
        host.Choose();
        var files = host.Files.Items.Cast<string>().ToArray();
        var error = host.Error.Text;
        var count = host.Field<TextBlock>("SongGridCount").Text;
        host.Choose();
        host.Dialog.Close();
        if (outcome == "failure") pending.SetException(new IOException("late picker failure"));
        else pending.SetResult(outcome switch
        {
            "success" => Paths("late"), "invalid-path" => ["\0"], _ => []
        });
        Dispatcher.UIThread.RunJobs();
        host.Choose();
        host.Generate();
        Dispatcher.UIThread.RunJobs();
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(host.Files.Items.Cast<string>(), Is.EqualTo(files));
            Assert.That(host.Error.Text, Is.EqualTo(error));
            Assert.That(host.Field<TextBlock>("SongGridCount").Text, Is.EqualTo(count));
            Assert.That(host.GenerateButton.IsEnabled, Is.False);
            Assert.That(host.Result.IsCompleted, Is.True);
            Assert.That(host.Result.Result, Is.Null);
        });
        host.AssertDocumentUnchanged();
    }

    [TestCase("sync-failure")]
    [TestCase("task-failure")]
    [TestCase("deferred-failure")]
    [TestCase("invalid-path")]
    public void PickerFailureKeepsPreviousOrderAndCanRetry(string outcome)
    {
        var calls = 0;
        var previous = Paths("original");
        var next = Paths("retry");
        var failure = new TaskCompletionSource<IReadOnlyList<string>>();
        using var host = new Host(owner =>
        {
            Assert.That(owner, Is.TypeOf<SongGridDialog>());
            calls++;
            if (calls == 1) return Task.FromResult<IReadOnlyList<string>>(previous);
            if (calls == 3) return Task.FromResult<IReadOnlyList<string>>(next);
            return outcome switch
            {
                "sync-failure" => throw new IOException("picker failure for retry"),
                "task-failure" => Task.FromException<IReadOnlyList<string>>(new IOException("picker failure for retry")),
                "deferred-failure" => failure.Task,
                // A valid prefix must not partially replace the previous selection.
                _ => Task.FromResult<IReadOnlyList<string>>([next[0], "\0", next[1], next[2]])
            };
        });
        host.Choose();
        var files = host.Files.Items.Cast<string>().ToArray();
        host.Choose();
        if (outcome == "deferred-failure") failure.SetException(new IOException("picker failure for retry"));
        PumpUntil(() => host.ChooseButton.IsEnabled && !string.IsNullOrWhiteSpace(host.Error.Text));
        Assert.Multiple(() =>
        {
            Assert.That(host.Files.Items.Cast<string>(), Is.EqualTo(files));
            Assert.That(host.GenerateButton.IsEnabled, Is.True);
            Assert.That(host.Result.IsCompleted, Is.False);
        });
        host.Choose();
        PumpUntil(() => host.Error.Text == "" && host.ChooseButton.IsEnabled);
        Assert.That(calls, Is.EqualTo(3));
        Assert.That(host.Files.Items.Cast<string>(), Is.EqualTo(next.Select((path, index) =>
            $"{index + 1}. {Path.GetFileName(path)}")));
        host.Generate();
        PumpUntil(() => host.Result.IsCompleted);
        Assert.That(host.Result.Result!.Paths, Is.EqualTo(next.Select(Path.GetFullPath)));
        host.AssertDocumentUnchanged();
    }

    [Test]
    public void InitialCancellationAllowsRetryAndChangedSettingsAreRevalidatedAfterChoice()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var calls = 0;
        using var host = new Host(_ => ++calls == 1
            ? Task.FromResult<IReadOnlyList<string>>([]) : pending.Task);
        host.Choose();
        Assert.Multiple(() =>
        {
            Assert.That(host.Files.Items.Count, Is.Zero);
            Assert.That(host.GenerateButton.IsEnabled, Is.False);
            Assert.That(host.ChooseButton.IsEnabled, Is.True);
        });
        host.Choose();
        host.Field<TextBox>("SongGridColumns").Text = "3";
        Dispatcher.UIThread.RunJobs();
        pending.SetResult(Paths("four"));
        PumpUntil(() => host.ChooseButton.IsEnabled);
        Assert.That(host.GenerateButton.IsEnabled, Is.False);
        Assert.That(host.Error.Text, Does.Contain("4 张").And.Contain("6 张"));
        host.Generate();
        Assert.That(host.Result.IsCompleted, Is.False);
        host.Field<TextBox>("SongGridColumns").Text = "2";
        PumpUntil(() => host.GenerateButton.IsEnabled);
        host.Generate();
        PumpUntil(() => host.Result.IsCompleted);
        Assert.That(host.Result.Result!.Settings.Count, Is.EqualTo(4));
        host.AssertDocumentUnchanged();
    }

    private static string[] Paths(string prefix) => Enumerable.Range(0, 4)
        .Select(index => Path.Combine(Path.GetTempPath(), $"{prefix}-{index}.png")).ToArray();

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.That(condition(), Is.True, "The expected song grid picker operation did not complete.");
    }

    private sealed class Host : IDisposable
    {
        private readonly FluentTheme _theme = new();
        private readonly Window _owner = new();
        private readonly EditorDocument _document = new() { CanvasWidth = 1000, CanvasHeight = 600 };
        private readonly EditorDocument _before;
        public SongGridDialog Dialog { get; }
        public Task<SongGridRequest?> Result { get; }
        public ListBox Files => Field<ListBox>("SongGridFiles");
        public TextBlock Error => Field<TextBlock>("SongGridError");
        public Button ChooseButton => Field<Button>("ChooseSongGridImages");
        public Button GenerateButton => Field<Button>("GenerateSongGrid");

        public Host(Func<Window, Task<IReadOnlyList<string>>> picker)
        {
            AvaloniaTestHost.EnsureStarted();
            Avalonia.Application.Current!.Styles.Add(_theme);
            _before = _document.Copy();
            _owner.Show();
            Dialog = new SongGridDialog(_document, picker);
            Result = Dialog.ShowDialog<SongGridRequest?>(_owner);
            Dialog.UpdateLayout();
        }

        public T Field<T>(string name) where T : Control => Dialog.GetLogicalDescendants()
            .OfType<T>().Single(control => control.Name == name);
        public void Choose() => UiTestActions.Click(ChooseButton);
        public void Generate() => UiTestActions.Click(GenerateButton);
        public void AssertDocumentUnchanged() => Assert.That(_document.StateEquals(_before), Is.True);

        public void Dispose()
        {
            foreach (var owned in _owner.OwnedWindows.ToArray()) owned.Close();
            _owner.Close();
            Avalonia.Application.Current!.Styles.Remove(_theme);
        }
    }
}
