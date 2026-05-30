using UndertaleModLib;
using UndertaleModLib.Models;

namespace G3MToolCLI.Services;

internal sealed record CodeArchiveEntryDescriptor(
    string ArchiveKey,
    string LogicalName,
    string SafeName,
    string? ParentArchiveKey,
    int Occurrence);

internal static class CodeEntryArchiveIdentity
{
    public static string BuildOccurrenceKey(string safeName, int occurrence)
    {
        return occurrence <= 1 ? safeName : $"{safeName}__occ{occurrence:D4}";
    }

    public static List<CodeArchiveEntryDescriptor> DescribeEntries(
        UndertaleData data,
        HashSet<string>? topLevelFilterNames = null)
    {
        var result = new List<CodeArchiveEntryDescriptor>();
        var topLevelByNameCount = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in data.Code)
        {
            if (entry?.Name?.Content == null || entry.ParentEntry != null)
                continue;

            if (topLevelFilterNames != null && !topLevelFilterNames.Contains(entry.Name.Content))
                continue;

            int occurrence = topLevelByNameCount.GetValueOrDefault(entry.Name.Content) + 1;
            topLevelByNameCount[entry.Name.Content] = occurrence;

            string safeName = ResourceExportService.SafeName(entry.Name.Content);
            string archiveKey = BuildOccurrenceKey(safeName, occurrence);
            result.Add(new CodeArchiveEntryDescriptor(archiveKey, entry.Name.Content, safeName, null, occurrence));

            DescribeChildren(entry, archiveKey, result);
        }

        return result;
    }

    public static Dictionary<string, CodeArchiveEntryDescriptor> DescribeEntriesByKey(
        UndertaleData data,
        HashSet<string>? topLevelFilterNames = null)
    {
        return DescribeEntries(data, topLevelFilterNames).ToDictionary(x => x.ArchiveKey, StringComparer.Ordinal);
    }

    private static void DescribeChildren(
        UndertaleCode parent,
        string parentArchiveKey,
        List<CodeArchiveEntryDescriptor> output)
    {
        if (parent.ChildEntries.Count == 0)
            return;

        var childCountByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in parent.ChildEntries)
        {
            if (child?.Name?.Content == null)
                continue;

            int occurrence = childCountByName.GetValueOrDefault(child.Name.Content) + 1;
            childCountByName[child.Name.Content] = occurrence;

            string safeName = ResourceExportService.SafeName(child.Name.Content);
            string childLeafKey = BuildOccurrenceKey(safeName, occurrence);
            output.Add(new CodeArchiveEntryDescriptor(
                $"{parentArchiveKey}/{childLeafKey}",
                child.Name.Content,
                safeName,
                parentArchiveKey,
                occurrence));
        }
    }
}
