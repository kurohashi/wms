using Dit.Db.Extentions.Extentions.Paging;
using Dit.Db.Extentions.Extentions.Queryable;
using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Contracts.Interfaces;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;
using Wms.PickList.Application.Enums;

namespace Wms.PickList.Application.Query;

public class GetPickListsQueryResult
{
    public PagedResult<PickListSummaryDto> PickLists { get; set; } = null!;
    public PickListStatsCountsDto Stats { get; set; } = null!;
}

public sealed class GetPickListsQuery : IQuery<GetPickListsQueryResult>
{
    public PickListStatus? Status { get; set; }
    public string? PickListNumber { get; set; }
    public string? UniqueOrderId { get; set; }
    public string? ZoneCode { get; set; }
    public string? AssignedTo { get; set; }
    public string? Priority { get; set; }
    public string? OrderDeliveryType { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public int? Page { get; init; }
    public int? Limit { get; init; }
    private int PageNo => Page ?? 1;
    private int PageSizeNo => Limit ?? 20;

    public sealed class Handler(WmsDbContextWrite dbContext, ICurrentUser currentUser) : IQueryHandler<GetPickListsQuery, GetPickListsQueryResult>
    {
        public async Task<GetPickListsQueryResult> HandleAsync(GetPickListsQuery query, CancellationToken ct = default)
        {
            long warehouseId = currentUser.WarehouseId ?? 0;
            if (warehouseId == 0) throw new BusinessRuleException("User is not associated with any warehouse.");

            int? parsedPriority = null;
            if (!string.IsNullOrWhiteSpace(query.Priority))
            {
                var digits = new string(query.Priority.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var p))
                {
                    parsedPriority = p;
                }
            }

            var fromDate = query.FromDate?.ToUniversalTime() ?? DateTime.UtcNow.Date;

            // Common query with all shared filters
            var commonQuery = dbContext.PickLists.AsNoTracking()
                .Where(pl => pl.IsActive == true && pl.IsDeleted == false && pl.WarehouseId == warehouseId)
                .Where(pl => pl.CreatedDate >= fromDate)
                .WhereIf(query.ToDate.HasValue,
                    pl => pl.CreatedDate < query.ToDate!.Value.Date.AddDays(1))
                .WhereIf(!string.IsNullOrWhiteSpace(query.PickListNumber),
                    pl => EF.Functions.ILike(pl.PickListNumber!, $"%{query.PickListNumber!.Trim()}%"))
                .WhereIf(!string.IsNullOrWhiteSpace(query.ZoneCode),
                    pl => pl.PickListItems
                        .Where(pi => pi.IsActive == true && pi.IsDeleted == false)
                        .SelectMany(pi => pi.InventoryAllocations.Where(ia => ia.IsActive == true && ia.IsDeleted == false))
                        .Any(ia => ia.FromBinNavigation.Aisle != null && EF.Functions.ILike(ia.FromBinNavigation.Aisle.Zone.Code, $"%{query.ZoneCode!.Trim()}%")))
                .WhereIf(!string.IsNullOrWhiteSpace(query.AssignedTo),
                    pl => dbContext.Workers.Any(w => w.Id == pl.AssignedWorkerId && EF.Functions.ILike(w.Name, $"%{query.AssignedTo!.Trim()}%")))
                .WhereIf(parsedPriority.HasValue,
                    pl => pl.Priority == parsedPriority.Value)
                .WhereIf(!string.IsNullOrWhiteSpace(query.UniqueOrderId),
                    pl => pl.PickListItems
                        .Where(pi => pi.IsActive == true && pi.IsDeleted == false)
                        .Join(dbContext.WmsOrderItems, pi => pi.OrderItemId, oi => oi.Id, (pi, oi) => oi)
                        .Join(dbContext.WmsOrders, oi => oi.OrderId, o => o.Id, (oi, o) => o)
                        .Any(o => EF.Functions.ILike(o.UniqueOrderId!, $"%{query.UniqueOrderId!.Trim()}%")))
                .WhereIf(!string.IsNullOrWhiteSpace(query.OrderDeliveryType),
                    pl => pl.PickListItems
                        .Where(pi => pi.IsActive == true && pi.IsDeleted == false)
                        .Join(dbContext.WmsOrderItems, pi => pi.OrderItemId, oi => oi.Id, (pi, oi) => oi)
                        .Join(dbContext.WmsOrders, oi => oi.OrderId, o => o.Id, (oi, o) => o)
                        .Any(o => EF.Functions.ILike(o.OrderDeliveryType!, $"%{query.OrderDeliveryType!.Trim()}%")));

            // Calculate stats across all statuses directly from commonQuery
            var stats = await commonQuery
                .GroupBy(x => x.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var statsDict = stats.ToDictionary(x => (x.Status ?? "").ToUpperInvariant(), x => x.Count);
            statsDict.TryGetValue(nameof(PickListStatus.PENDING), out var pendingCount);
            statsDict.TryGetValue(nameof(PickListStatus.ASSIGNED), out var assignedCount);
            statsDict.TryGetValue(nameof(PickListStatus.IN_PROGRESS), out var inProgressCount);
            statsDict.TryGetValue(nameof(PickListStatus.COMPLETED), out var completedCount);
            statsDict.TryGetValue(nameof(PickListStatus.CANCELLED), out var cancelledCount);

            var statsDto = new PickListStatsCountsDto(
                stats.Sum(x => x.Count),
                pendingCount,
                assignedCount,
                inProgressCount,
                completedCount,
                cancelledCount
            );

            // Apply status filter and paginate
            string? statusFilter = query.Status?.ToString();

            var pagedPickLists = await commonQuery
                .WhereIf(!string.IsNullOrWhiteSpace(statusFilter), s => s.Status == statusFilter)
                .OrderByDescending(pl => pl.CreatedDate)
                .Include(pl => pl.DropLocation)
                    .ThenInclude(d => d!.Aisle)
                        .ThenInclude(a => a.Zone)
                .GetPaged(query.PageNo, query.PageSizeNo);

            var pickListIds = pagedPickLists.Results.Select(pl => pl.Id).ToList();
            var pickListUuids = pagedPickLists.Results.Select(pl => pl.PickListUuid).ToList();

            // Batch-load related data for only the current page items
            Dictionary<Guid, PickerDto> pickersLookup = [];
            Dictionary<long, (int TotalItems, int CompletedItems)> itemStatsLookup = [];
            Dictionary<long, List<string>> ordersLookup = [];
            Dictionary<long, List<string>> deliveryTypesLookup = [];
            Dictionary<long, List<string>> customerTypesLookup = [];
            Dictionary<long, List<string>> binsLookup = [];
            Dictionary<long, string?> zoneCodesLookup = [];

            if (pickListIds.Count > 0)
            {
                // 1. Batch load Pickers
                var pickers = await dbContext.Tasks
                    .AsNoTracking()
                    .Where(t => t.ReferenceId.HasValue && pickListUuids.Contains(t.ReferenceId.Value)
                             && t.Type == nameof(TaskTypeEnum.PICK)
                             && t.IsActive && !t.IsDeleted)
                    .Join(dbContext.Assignments.Where(a => a.IsActive && !a.IsDeleted),
                        t => t.Id,
                        a => a.TaskId,
                        (t, a) => new { t.ReferenceId, a.WorkerId })
                    .Join(dbContext.Workers.Where(w => w.IsActive && !w.IsDeleted),
                        ta => ta.WorkerId,
                        w => w.Id,
                        (ta, w) => new { ReferenceId = ta.ReferenceId!.Value, Picker = new PickerDto(w.WorkerUuid.ToString(), w.Name) })
                    .ToListAsync(ct);

                pickersLookup = pickers
                    .GroupBy(x => x.ReferenceId)
                    .ToDictionary(g => g.Key, g => g.First().Picker);

                // 2. Batch load Item Counts & Completion Stats
                var itemStats = await dbContext.PickListItems
                    .AsNoTracking()
                    .Where(pi => pickListIds.Contains(pi.PickListId) && pi.IsActive == true && pi.IsDeleted == false)
                    .GroupBy(pi => pi.PickListId)
                    .Select(g => new
                    {
                        PickListId = g.Key,
                        TotalItems = g.Count(),
                        CompletedItems = g.Count(i => i.Status == nameof(PickListItemStatus.SHORT_PICK) || i.Status == nameof(PickListStatus.COMPLETED))
                    })
                    .ToListAsync(ct);

                itemStatsLookup = itemStats.ToDictionary(x => x.PickListId, x => (x.TotalItems, x.CompletedItems));

                // 3. Batch load Unique Order IDs and Delivery Types
                var orderLookups = await dbContext.PickListItems
                    .AsNoTracking()
                    .Where(pi => pickListIds.Contains(pi.PickListId) && pi.IsActive == true && pi.IsDeleted == false)
                    .Join(dbContext.WmsOrderItems, pi => pi.OrderItemId, oi => oi.Id, (pi, oi) => new { pi.PickListId, oi.OrderId })
                    .Join(dbContext.WmsOrders, x => x.OrderId, o => o.Id, (x, o) => new { x.PickListId, o.UniqueOrderId, o.OrderDeliveryType, o.CustomerType })
                    .Distinct()
                    .ToListAsync(ct);

                ordersLookup = orderLookups
                    .GroupBy(x => x.PickListId)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.UniqueOrderId).Distinct().ToList());

                deliveryTypesLookup = orderLookups
                    .GroupBy(x => x.PickListId)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.OrderDeliveryType).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList()!);

                customerTypesLookup = orderLookups
                    .GroupBy(x => x.PickListId)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.CustomerType).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList()!);

                // 4. Batch load Bin Codes and From-Bin Zone Codes
                var binLookupsRaw = await dbContext.PickListItems
                    .AsNoTracking()
                    .Where(pi => pickListIds.Contains(pi.PickListId) && pi.IsActive == true && pi.IsDeleted == false)
                    .SelectMany(pi => pi.InventoryAllocations
                        .Where(ia => ia.IsActive == true && ia.IsDeleted == false)
                        .Select(ia => new { 
                            pi.PickListId, 
                            Code = ia.FromBinNavigation.Code,
                            ZoneCode = ia.FromBinNavigation.Aisle != null ? ia.FromBinNavigation.Aisle.Zone.Code : null
                        }))
                    .Distinct()
                    .ToListAsync(ct);

                binsLookup = binLookupsRaw
                    .Where(x => x.Code != null)
                    .GroupBy(x => x.PickListId)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.Code!).Distinct().ToList());

                zoneCodesLookup = binLookupsRaw
                    .Where(x => !string.IsNullOrEmpty(x.ZoneCode))
                    .GroupBy(x => x.PickListId)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.ZoneCode).FirstOrDefault());
            }

            // Assemble DTO list in-memory via O(1) dictionary lookups
            var dtoList = pagedPickLists.Results.Select(pl =>
            {
                var (totalItems, completedItems) = itemStatsLookup.TryGetValue(pl.Id, out var stat) ? stat : (0, 0);
                decimal? percentComplete = totalItems > 0 ? (decimal)completedItems / totalItems * 100 : 0;

                var dTypes = deliveryTypesLookup.TryGetValue(pl.Id, out var types) ? types : [];
                string? orderDeliveryType = dTypes.FirstOrDefault();
                
                var cTypes = customerTypesLookup.TryGetValue(pl.Id, out var ctypes) ? ctypes : [];
                string? customerType = cTypes.FirstOrDefault();
                
                return new PickListSummaryDto(
                    pl.PickListUuid,
                    pl.PickListNumber,
                    totalItems,
                    pl.Status,
                    pl.DropLocation?.Code,
                    zoneCodesLookup.TryGetValue(pl.Id, out var zCode) ? zCode : null,
                    pl.CreatedDate,
                    pickersLookup.TryGetValue(pl.PickListUuid, out var picker) ? picker : null,
                    pl.Priority,
                    percentComplete,
                    pl.ModifiedDate != null
                        ? (int)(DateTime.UtcNow - pl.ModifiedDate.Value).TotalMinutes
                        : (pl.CreatedDate != null ? (int)(DateTime.UtcNow - pl.CreatedDate.Value).TotalMinutes : null),
                    ordersLookup.TryGetValue(pl.Id, out var orders) ? orders : [],
                    binsLookup.TryGetValue(pl.Id, out var bins) ? bins : [],
                    orderDeliveryType,
                    customerType
                );
            }).ToList();

            var pagedResult = new PagedResult<PickListSummaryDto>
            {
                CurrentPage = pagedPickLists.CurrentPage,
                PageSize = pagedPickLists.PageSize,
                RowCount = pagedPickLists.RowCount,
                Results = dtoList
            };

            return new GetPickListsQueryResult
            {
                PickLists = pagedResult,
                Stats = statsDto
            };
        }
    }
}