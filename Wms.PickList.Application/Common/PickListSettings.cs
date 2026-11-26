namespace Wms.PickList.Application.Common;

public sealed class PickListSettings
{
    public const string SectionName = "PickListSettings";

    public int MaxUniqueSkus { get; set; } = 30;
}
