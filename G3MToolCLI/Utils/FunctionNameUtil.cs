using System.Text.RegularExpressions;

namespace G3MToolCLI.Utils;

internal static partial class FunctionNameUtil
{
    public static bool IsLocalChildFunctionName(string name) =>
        name.Contains("____struct___", StringComparison.Ordinal) ||
        name.StartsWith("gml_Script_anon_", StringComparison.Ordinal);

    public static string NormalizeLocalChildFunctionName(string name)
    {
        name = StructOrdinalRegex().Replace(name, "____struct___#");
        return AnonymousOrdinalRegex().Replace(name, "$1_#_$2");
    }

    public static bool TryGetLocalChildOrdinalFamily(
        string name, out string family, out int ordinal)
    {
        var structMatch = StructOrdinalFamilyRegex().Match(name);
        if (structMatch.Success && int.TryParse(structMatch.Groups["ordinal"].Value, out ordinal))
        {
            family = structMatch.Groups["prefix"].Value + "#" + structMatch.Groups["suffix"].Value;
            return true;
        }

        var anonymousMatch = AnonymousOrdinalFamilyRegex().Match(name);
        if (anonymousMatch.Success && int.TryParse(anonymousMatch.Groups["ordinal"].Value, out ordinal))
        {
            family = anonymousMatch.Groups["prefix"].Value + "_#_" + anonymousMatch.Groups["suffix"].Value;
            return true;
        }

        family = string.Empty;
        ordinal = 0;
        return false;
    }

    public static string CanonicalizeFunctionName(string name)
    {
        const string nestedGlobalScriptPrefix = "gml_Script_gml_GlobalScript_";
        return name.StartsWith(nestedGlobalScriptPrefix, StringComparison.Ordinal)
            ? "gml_Script_" + name[nestedGlobalScriptPrefix.Length..]
            : name;
    }

    [GeneratedRegex("____struct___\\d+")]
    private static partial Regex StructOrdinalRegex();

    [GeneratedRegex(@"^(gml_Script_anon_.+)_\d+_([^_].*)$")]
    private static partial Regex AnonymousOrdinalRegex();

    [GeneratedRegex(@"^(?<prefix>gml_Script____struct___)(?<ordinal>\d+)(?<suffix>_.+)$")]
    private static partial Regex StructOrdinalFamilyRegex();

    [GeneratedRegex(@"^(?<prefix>gml_Script_anon_.+)_(?<ordinal>\d+)_(?<suffix>[^_].*)$")]
    private static partial Regex AnonymousOrdinalFamilyRegex();
}
