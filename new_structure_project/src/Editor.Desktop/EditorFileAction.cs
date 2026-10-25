namespace Editor.Desktop;

/// <summary>File requests exposed by the desktop; hosts can supply local path pickers.</summary>
public enum EditorFileAction
{
    ImportImage, ReplaceImage, BackgroundImage, OpenProject, SaveTemporary,
    ImportFont, OpenLayout, SaveLayout
}
