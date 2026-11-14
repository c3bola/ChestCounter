using Unreal.Core.Models.Enums;

namespace Unreal.Core.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class NetFieldExportGroupPrefixAttribute : Attribute
{
    public string Prefix { get; private set; }

    public ParseMode MinimalParseMode { get; private set; }

    public NetFieldExportGroupPrefixAttribute(string prefix, ParseMode minimalParseMode = ParseMode.Normal)
    {
        Prefix = prefix;
        MinimalParseMode = minimalParseMode;
    }
}
