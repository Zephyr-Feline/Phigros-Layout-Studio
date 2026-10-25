using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Editor.Desktop;

namespace Editor.Tests;

/// <summary>Find an editor action regardless of whether it lives in a menu or a toolbar.</summary>
internal static class UiTestActions
{
    public static Control Find(Control root, string label)
    {
        var translated = UiText.Translate(label);
        return Actions(root).First(control =>
            control.Name == label || Label(control) == translated || Label(control) == label);
    }

    public static Control FindNamed(Control root, string name) =>
        Actions(root).First(control => control.Name == name);

    public static void Click(Control root, string label) => Click(Find(root, label));
    public static void ClickNamed(Control root, string name) => Click(FindNamed(root, name));

    public static void Click(Control action)
    {
        switch (action)
        {
            case MenuItem menu: menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); break;
            case Button button: button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
            default: throw new InvalidOperationException($"'{action.Name}' is not an editor action.");
        }
    }

    public static void Reveal(Control control)
    {
        foreach (var section in control.GetLogicalAncestors().OfType<Expander>()) section.IsExpanded = true;
        var window = control.GetLogicalAncestors().OfType<Window>().FirstOrDefault();
        window?.UpdateLayout();
        control.BringIntoView();
        window?.UpdateLayout();
    }

    public static bool Focus(Control control) { Reveal(control); return control.Focus(); }

    public static string? Status(Control root) => root.GetLogicalDescendants().OfType<TextBlock>()
        .FirstOrDefault(block => block.Name == "EditorStatus")?.Text;

    public static string? Error(Control root) => Status(root) is { } status &&
        status.StartsWith("错误：", StringComparison.Ordinal) ? status : null;

    private static IEnumerable<Control> Actions(Control root)
    {
        var pending = new Queue<Control>(new[] { root }.Concat(root.GetLogicalDescendants().OfType<Control>())
            .Concat(root.GetVisualDescendants().OfType<Control>()));
        var visited = new HashSet<Control>();
        while (pending.TryDequeue(out var control))
        {
            if (!visited.Add(control)) continue;
            // Closed popup items have not entered either tree yet, but remain real menu actions.
            if (control is Menu menu)
                foreach (var child in menu.Items.OfType<Control>()) pending.Enqueue(child);
            if (control is MenuItem item)
                foreach (var child in item.Items.OfType<Control>()) pending.Enqueue(child);
            if (control is MenuItem or Button) yield return control;
        }
    }

    private static string? Label(Control control) => control switch
    {
        MenuItem menu => menu.Header?.ToString(),
        Button button => button.Content?.ToString(),
        _ => null
    };
}
