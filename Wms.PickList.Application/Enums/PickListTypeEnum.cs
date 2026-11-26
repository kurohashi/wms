using System.Text.Json.Serialization;

namespace Wms.PickList.Application.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PickListTypeEnum
{
    
    /// 1. Picking Bins → To Cluster Bins/Zone
    PickingToCluster = 1,

    /// 2. Cluster Bins → Route Bins/Zone
    ClusterToRoute = 2,

    /// 3. Picking Zone → Final Route Zone for Short Pick (For sorting case)
    PickingToFinalRouteShortPick = 3,

    /// 4. Route Zone → To Final Route Zone (For specific storage types like Chiller, Freezer, etc.)
    RouteToFinalRoute = 4
}
