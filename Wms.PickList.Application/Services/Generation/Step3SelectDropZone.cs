using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Domain.Entities;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Helper;
using Task = System.Threading.Tasks.Task;

namespace Wms.PickList.Application.Services.Generation;

/// <summary>
/// Step 3: Assigns drop location bin using FINAL_ZONE rules, route/cluster order type,
/// storage-type zone EnableSeparateZonePicking, and RouteBinAssignment.
/// </summary>
internal sealed class Step3SelectDropZone(WmsDbContextWrite dbContext) : PickListGenerationStepBase(dbContext)
{
    protected override async Task ExecuteCoreAsync(PickListGenerationContext context, CancellationToken ct)
    {
        if (context.OrderItemDrafts.Count == 0)
            return;

        var warehouseId = context.WarehouseId;
        var orderIds = context.Orders.Select(o => o.Id).ToList();

        // 1. Determine which orders are linked to a route. OrderId → RouteId
        var orderRouteMap = await DbContext.RouteOrders
            .AsNoTracking()
            .Where(ro => ro.OrderId != null
                         && orderIds.Contains(ro.OrderId.Value)
                         && ro.IsActive == true
                         && ro.IsDeleted == false
                         && ro.Status != "COMPLETED")
            .GroupBy(ro => ro.OrderId!.Value)
            .Select(g => new { OrderId = g.Key, RouteId = g.Select(ro => ro.RouteId).First() })
            .ToDictionaryAsync(x => x.OrderId, x => x.RouteId, ct);

        var routeIds = orderRouteMap.Values.Distinct().ToList();

        // 1.2 Route drop bins when separate zone picking is off.
        var routeBinAssignments = routeIds.Count == 0
            ? []
            : await DbContext.RouteBinAssignments
                .AsNoTracking()
                .Include(r => r.Bin)
                .Where(r => r.RouteId != null && routeIds.Contains(r.RouteId.Value) && r.IsActive)
                .ToListAsync(ct);

        var assignmentsByRouteId = routeBinAssignments
            .GroupBy(r => r.RouteId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AssignedAt).First().Bin);

        // 1.3 Cluster drop bins.
        var clusterIds = context.Orders
            .Select(o => o.ClusterId)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct()
            .ToList();

        var clusterBinAssignments = clusterIds.Count == 0
            ? []
            : await DbContext.RouteBinAssignments
                .AsNoTracking()
                .Include(r => r.Bin)
                .Where(r => r.ClusterId != null && clusterIds.Contains(r.ClusterId) && r.IsActive)
                .ToListAsync(ct);

        var assignmentsByClusterId = clusterBinAssignments
            .GroupBy(r => r.ClusterId!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AssignedAt).First().Bin);

        // Storage-type zones use Zone.Code = StorageType.Code; EnableSeparateZonePicking toggles per-type bins.
        var zonesByCode = await DbContext.Zones
            .AsNoTracking()
            .Where(z => z.WarehouseId == warehouseId
                        && z.IsActive != false
                        && z.IsDeleted != true)
            .ToListAsync(ct);

        var zoneSettingsByCode = zonesByCode
            .GroupBy(z => z.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var storageTypeCodesByUuid = await DbContext.StorageTypes
            .AsNoTracking()
            .Where(st => st.IsActive && st.IsDeleted != true)
            .ToDictionaryAsync(st => st.StorageTypeUuid, st => st.Code, ct);

        var warehouseBins = await DbContext.Bins
            .AsNoTracking()
            .Include(b => b.Aisle)
            .ThenInclude(a => a.Zone)
            .Where(b => b.Aisle.Zone.WarehouseId == warehouseId
                        && b.IsActive == true
                        && b.IsDeleted == false)
            .ToListAsync(ct);

        var finalZoneCode = await DbContext.GetFinalZone(ct);

        var finalZoneBins = warehouseBins
            .Where(b => HasZoneCode(b.Aisle.Zone, finalZoneCode))
            .ToList();

        foreach (var draft in context.OrderItemDrafts)
        {
            var order = draft.Order;
            var isRouteOrder = context.UseRouteOnlyDropZone || orderRouteMap.ContainsKey(order.Id);
            var hasCluster = !string.IsNullOrWhiteSpace(order.ClusterId);

            var orderFinalZoneBins = finalZoneBins;
            if (!isRouteOrder && assignmentsByClusterId.TryGetValue(order.ClusterId!, out var clusterMappedBin))
            {
                orderFinalZoneBins = [.. finalZoneBins.Where(z => z.Id == clusterMappedBin.Id)];
            }
            else if (isRouteOrder
                     && orderRouteMap.TryGetValue(order.Id, out var rId)
                     && assignmentsByRouteId.TryGetValue(rId, out var routeMappedBin))
            {
                orderFinalZoneBins = [.. finalZoneBins.Where(z => z.Id == routeMappedBin.Id)];
            }

            Bin? dropBin = context.ShortPickOrderItems is not null
                ? SelectFinalZoneRoutableBin(orderFinalZoneBins)
                : isRouteOrder
                    ? SelectRouteOrderDropBin(
                        draft,
                        warehouseBins,
                        orderFinalZoneBins,
                        zoneSettingsByCode,
                        storageTypeCodesByUuid,
                        orderRouteMap.TryGetValue(order.Id, out var routeId) ? routeId : null,
                        assignmentsByRouteId)
                    : hasCluster
                        ? SelectClusterOrderDropBin(
                            draft,
                            warehouseBins,
                            orderFinalZoneBins,
                            zoneSettingsByCode,
                            storageTypeCodesByUuid)
                        : null;

            if (dropBin is null)
                throw new BusinessRuleException($"No suitable drop bin found for order {order.UniqueOrderId} (route: {isRouteOrder}, cluster: {hasCluster}).");

            // For cluster drop zones, only products with IsBulkPick = true are picked. Non-bulk items are skipped.
            if (dropBin.IsClusterBased && !draft.IsBulkPick)
            {
                draft.BinAllocations.Clear();
                draft.DropLocationId = null;
                continue;
            }

            draft.DropLocationId = dropBin.Id;
            context.Counters.DropZoneStepsExecuted++;
        }
    }

    /// <summary>
    /// Route order (linked to route):
    /// - From bin already IsRoutable -> FINAL_ZONE IsRoutable bin.
    /// - EnableSeparateZonePicking ON  -> IsRoutable bin in storage-type zone (Zone.Code = storage type code).
    /// - EnableSeparateZonePicking OFF -> bin from RouteBinAssignment for this route.
    /// </summary>
    private static Bin? SelectRouteOrderDropBin(
        OrderItemAllocationDraft draft,
        IReadOnlyList<Bin> warehouseBins,
        IReadOnlyList<Bin> finalZoneBins,
        IReadOnlyDictionary<string, Zone> zoneSettingsByCode,
        IReadOnlyDictionary<Guid, string> storageTypeCodesByUuid,
        int? routeId,
        IReadOnlyDictionary<int, Bin> assignmentsByRouteId)
    {
        // 1. From bin is already at a routable bin -> go directly to FINAL_ZONE IsRoutable bin.
        if (draft.BinAllocations.Any(b => b.IsRoutable))
            return SelectFinalZoneRoutableBin(finalZoneBins);

        // 1.1 Resolve storage type code from the from bin.
        var (storageTypeId, storageTypeCode) = ResolveStorageType(draft, storageTypeCodesByUuid);
        if (string.IsNullOrWhiteSpace(storageTypeCode))
            return SelectFinalZoneRoutableBin(finalZoneBins);

        if (!zoneSettingsByCode.TryGetValue(storageTypeCode, out var storageTypeZone))
            return SelectFinalZoneRoutableBin(finalZoneBins);

        if (storageTypeZone.EnableSeparateZonePicking)
        {
            // 1.1 Drop bin in the storage-type zone with IsRoutable = true.
            return MatchBinByStorageType(
                       warehouseBins.Where(b =>
                           HasZoneCode(b.Aisle.Zone, storageTypeCode) && b.IsRoutable),
                       storageTypeId,
                       storageTypeCode)
                   ?? SelectFinalZoneRoutableBin(finalZoneBins);
        }

        // 1.2 Separate zone picking off -> use route-assigned bin.
        if (routeId is > 0 && assignmentsByRouteId.TryGetValue(routeId.Value, out var assignedBin) && assignedBin.IsRoutable)
            return assignedBin;

        return SelectFinalZoneRoutableBin(finalZoneBins);
    }

    /// <summary>
    /// Cluster order (not route-linked, cluster present):
    /// - EnableSeparateZonePicking ON  -> IsClusterBased bin in storage-type zone.
    /// - EnableSeparateZonePicking OFF -> FINAL_ZONE IsClusterBased bin.
    /// </summary>
    private static Bin? SelectClusterOrderDropBin(
        OrderItemAllocationDraft draft,
        IReadOnlyList<Bin> warehouseBins,
        IReadOnlyList<Bin> finalZoneBins,
        IReadOnlyDictionary<string, Zone> zoneSettingsByCode,
        IReadOnlyDictionary<Guid, string> storageTypeCodesByUuid)
    {



        //// 2.1 Resolve storage type code from the FromBin in AllocationDraft.
        var (storageTypeId, storageTypeCode) = ResolveStorageType(draft, storageTypeCodesByUuid);
        if (string.IsNullOrWhiteSpace(storageTypeCode))
            return SelectFinalZoneClusterBasedBin(finalZoneBins);

        if (!zoneSettingsByCode.TryGetValue(storageTypeCode, out var storageTypeZone))
            return SelectFinalZoneClusterBasedBin(finalZoneBins);

        if (storageTypeZone.EnableSeparateZonePicking)
        {
            // 2.1 Drop bin in the storage-type zone with IsClusterBased = true.
            return MatchBinByStorageType(
                       warehouseBins.Where(b =>
                           HasZoneCode(b.Aisle.Zone, storageTypeCode) && b.IsClusterBased),
                       storageTypeId,
                       storageTypeCode)
                   ?? SelectFinalZoneClusterBasedBin(finalZoneBins);
        }

        // 2.2 Separate zone picking off -> shared FINAL_ZONE IsClusterBased bin.
        return SelectFinalZoneClusterBasedBin(finalZoneBins);
    }

    private static (Guid? StorageTypeId, string? StorageTypeCode) ResolveStorageType(
        OrderItemAllocationDraft draft,
        IReadOnlyDictionary<Guid, string> storageTypeCodesByUuid)
    {
        var storageTypeId = draft.BinAllocations
            .Select(b => b.StorageTypeId)
            .FirstOrDefault(id => id.HasValue);

        var storageTypeCode = draft.BinAllocations
            .Select(b => b.BinStorageType)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

        if (string.IsNullOrWhiteSpace(storageTypeCode)
            && storageTypeId.HasValue
            && storageTypeCodesByUuid.TryGetValue(storageTypeId.Value, out var code))
        {
            storageTypeCode = code;
        }

        return (storageTypeId, storageTypeCode);
    }

    private static bool HasZoneCode(Zone zone, string zoneCode) =>
        string.Equals(zone.Code, zoneCode, StringComparison.OrdinalIgnoreCase);

    private static Bin? SelectFinalZoneRoutableBin(IReadOnlyList<Bin> finalZoneBins) =>
        finalZoneBins.FirstOrDefault(b => b.IsRoutable);

    private static Bin? SelectFinalZoneClusterBasedBin(IReadOnlyList<Bin> finalZoneBins) =>
        finalZoneBins.FirstOrDefault(b => b.IsClusterBased);

    private static Bin? MatchBinByStorageType(
        IEnumerable<Bin> candidates,
        Guid? storageTypeId,
        string? storageTypeCode)
    {
        var binList = candidates.ToList();
        if (binList.Count == 0)
            return null;

        if (storageTypeId.HasValue)
        {
            return binList.FirstOrDefault(b => b.StorageTypeUuid == storageTypeId)
                ?? (!string.IsNullOrWhiteSpace(storageTypeCode)
                    ? binList.FirstOrDefault(b =>
                        string.Equals(b.StorageType, storageTypeCode, StringComparison.OrdinalIgnoreCase))
                    : null);
        }

        if (!string.IsNullOrWhiteSpace(storageTypeCode))
        {
            return binList.FirstOrDefault(b =>
                string.Equals(b.StorageType, storageTypeCode, StringComparison.OrdinalIgnoreCase));
        }

        return binList.FirstOrDefault();
    }
}
