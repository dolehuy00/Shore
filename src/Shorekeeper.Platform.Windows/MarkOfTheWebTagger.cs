using Shorekeeper.Core.Platform;

namespace Shorekeeper.Platform.Windows;

/// <summary>Writes the NTFS <c>Zone.Identifier</c> stream ("Mark of the Web") on received files.</summary>
public sealed class MarkOfTheWebTagger : IFileTagger
{
    // ZoneId 3 = Internet: the most cautious zone, used by browsers for downloads.
    private const string Content = "[ZoneTransfer]\r\nZoneId=3\r\n";

    public void MarkAsDownloaded(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", Content);
        }
        catch (IOException)
        {
            // Not NTFS (e.g. FAT32 USB drive): nothing to mark.
        }
    }
}
