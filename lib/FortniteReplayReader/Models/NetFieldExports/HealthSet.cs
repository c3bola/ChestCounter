using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Models.NetFieldExports;

/// <summary>
/// Health and shield attributes replicated by the FortPlayerStateAthena HealthSet.
/// Values are nullable because updates only include changed handles.
/// </summary>
[NetFieldExportGroup("/Script/FortniteGame.FortRegenHealthSet", minimalParseMode: ParseMode.Full)]
public class HealthSet : INetFieldExportGroup
{
    [NetFieldExportHandle(0, RepLayoutCmdType.PropertyFloat)]
    public float? HealthBaseValue { get; set; }

    [NetFieldExportHandle(1, RepLayoutCmdType.PropertyFloat)]
    public float? HealthCurrentValue { get; set; }

    [NetFieldExportHandle(3, RepLayoutCmdType.PropertyFloat)]
    public float? HealthMaxValue { get; set; }

    [NetFieldExportHandle(7, RepLayoutCmdType.PropertyFloat)]
    public float? HealthUnclampedBaseValue { get; set; }

    [NetFieldExportHandle(8, RepLayoutCmdType.PropertyFloat)]
    public float? HealthUnclampedCurrentValue { get; set; }

    [NetFieldExportHandle(30, RepLayoutCmdType.PropertyFloat)]
    public float? ShieldBaseValue { get; set; }

    [NetFieldExportHandle(31, RepLayoutCmdType.PropertyFloat)]
    public float? ShieldCurrentValue { get; set; }

    [NetFieldExportHandle(33, RepLayoutCmdType.PropertyFloat)]
    public float? ShieldMaxValue { get; set; }

    [NetFieldExportHandle(37, RepLayoutCmdType.PropertyFloat)]
    public float? ShieldUnclampedBaseValue { get; set; }

    [NetFieldExportHandle(38, RepLayoutCmdType.PropertyFloat)]
    public float? ShieldUnclampedCurrentValue { get; set; }
}
