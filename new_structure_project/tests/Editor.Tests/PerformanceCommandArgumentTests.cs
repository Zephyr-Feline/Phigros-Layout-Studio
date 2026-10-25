using System.Reflection;
using Editor.Desktop;
using NUnit.Framework;

namespace Editor.Tests;

public sealed class PerformanceCommandArgumentTests
{
    private static IEnumerable<TestCaseData> InvalidArguments()
    {
        string[][] cases =
        [
            ["--performance"],
            ["--performance", ""],
            ["--performance", " "],
            ["--performance", "--large-resources"],
            ["--performance", "unused", "--unknown"],
            ["--performance", "unused", "--large-resources", "extra"]
        ];
        foreach (var arguments in cases) yield return new TestCaseData((object)arguments);
    }

    [TestCaseSource(nameof(InvalidArguments))]
    public void InvalidPerformanceArgumentsReturnUsageBeforeGraphicsInitialization(string[] arguments)
    {
        // The suite already owns the headless platform; a second setup would throw.
        // Invalid arguments must be rejected before attempting that setup or creating output.
        var entryPoint = typeof(MainWindow).Assembly.GetType("Editor.Desktop.Program")!
            .GetMethod("Main", BindingFlags.Static | BindingFlags.Public)!;
        var originalError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            Assert.That(entryPoint.Invoke(null, [arguments]), Is.EqualTo(2));
            Assert.That(error.ToString(), Does.Contain("Usage: --performance"));
        }
        finally { Console.SetError(originalError); }
    }
}
