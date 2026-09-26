using AstraSize.Models;

namespace FolderMorpher.UI;

public static class PresentationTestRunner
{
    public static void VerifyStorageNodePresentation()
    {
        var parent = new FileItemNode(@"C:\Root", "Root", 1000, true);
        parent.Children.Add(new FileItemNode(@"C:\Root\Sub", "Sub", 400, true) { Parent = parent });
        if (parent.ExpandIcon != "▶" || parent.IconGlyph != "📁" || parent.FontWeight != "Bold")
            throw new InvalidOperationException("Storage node presentation bindings changed.");
        parent.IsExpanded = true;
        if (parent.ExpandIcon != "▼" || parent.BadgeBackground != "#FEF3C7")
            throw new InvalidOperationException("Storage node expanded presentation changed.");
        parent.Children[0].Percentage = 40;
        parent.Children[0].DiffBytes = 50 * 1024 * 1024;
        if (parent.Children[0].ShareFormatted != "40.0%" ||
            parent.Children[0].DiffFormatted != "+50 MB ▲")
            throw new InvalidOperationException("Storage share/diff presentation changed.");
    }
}
