namespace Shitie.Core;

internal static class SoundPolicy
{
    private static bool IsWindowsScreenshotSource(string source)
    {
        // Match the clipboard producer, never a time window that could mute an
        // unrelated image copied after a cancelled screenshot.
        var name = Path.GetFileNameWithoutExtension(source);
        return name.Equals("SnippingTool", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ScreenClippingHost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ScreenSketch", StringComparison.OrdinalIgnoreCase);
    }
    internal static bool ShouldPlayForCapture(AppSettings settings, ClipPayload payload) =>
        !(settings.MuteAutomaticScreenshots
          && payload.Kind == ClipKind.Image
          && !payload.WasExplicitCopy
          && !payload.WasCut
          && IsWindowsScreenshotSource(payload.Source));
}
