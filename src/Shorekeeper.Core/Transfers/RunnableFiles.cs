namespace Shorekeeper.Core.Transfers;

/// <summary>
/// Received files that run something when opened, rather than show it: programs, installers, scripts, shortcuts, and
/// disk images that open as a drive full of them. Opening one asks first (docs/04-identity-security.md §5).
/// </summary>
public static class RunnableFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Programs and installers.
        ".exe", ".com", ".scr", ".pif", ".cpl", ".msi", ".msp", ".mst", ".msix", ".msixbundle", ".appx", ".appxbundle",
        ".appinstaller", ".application", ".appref-ms", ".jar", ".gadget",
        // Scripts.
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".ps1xml", ".psc1", ".vbs", ".vbe", ".vb", ".js", ".jse", ".wsf", ".wsh",
        ".wsc", ".sct", ".hta", ".py", ".pyw",
        // Shortcuts and files Windows acts on.
        ".lnk", ".url", ".website", ".scf", ".reg", ".inf", ".msc", ".chm", ".hlp", ".settingcontent-ms", ".library-ms",
        ".search-ms", ".searchconnector-ms", ".diagcab", ".xll", ".iqy", ".slk",
        // Disk images: their files do not carry the Mark of the Web once mounted.
        ".iso", ".img", ".vhd", ".vhdx",
    };

    public static bool Is(string path) => Extensions.Contains(Path.GetExtension(path));
}
