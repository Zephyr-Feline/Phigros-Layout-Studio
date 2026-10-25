namespace Editor.Application;

public enum PendingChangesChoice
{
    Cancel,
    Save,
    Discard
}

/// <summary>Coordinates the decision before replacing or closing an edited document.</summary>
public static class PendingChangesWorkflow
{
    public static async Task<bool> ConfirmAsync(NativeProjectWorkflow project,
        Func<Task<PendingChangesChoice>> choose, Func<Task<bool>> save)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(choose);
        ArgumentNullException.ThrowIfNull(save);

        var pending = new PendingDocumentOperation(project.Session, () => project.CurrentPath);
        if (!project.IsDirty) return true;
        var choice = await choose();
        if (choice is not (PendingChangesChoice.Discard or PendingChangesChoice.Save)) return false;
        pending.EnsureUnchanged("Project changed while confirming unsaved changes. Please try again.");
        if (choice == PendingChangesChoice.Discard) return true;
        if (!await save()) return false;
        // Save As may legitimately change the path, but cannot authorize another document.
        pending.EnsureUnchanged("Project changed while saving unsaved changes. Please try again.", checkPath: false);
        return !project.IsDirty;
    }
}
