using System.Text.Json.Nodes;

namespace Editor.Tests;

internal static class LegacyFormatTestHelpers
{
    public static void RemoveV8BackgroundFields(JsonNode root, bool temporary)
    {
        var blend = temporary
            ? root["Document"]?["BackgroundBlend"] as JsonObject
            : root["backgroundBlend"] as JsonObject;
        if (blend is null) return;
        foreach (var field in temporary
                     ? new[] { "ContributionMode", "SelectedArtworkLayerIds", "Range", "Transition" }
                     : new[] { "contributionMode", "selectedArtworkLayerIds", "range", "transition" })
            blend.Remove(field);
    }
}
