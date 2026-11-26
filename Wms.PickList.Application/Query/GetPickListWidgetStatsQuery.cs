using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Contracts.Interfaces;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;

namespace Wms.PickList.Application.Query;

public sealed record PickListWidgetStatsDto(
    int TotalPickListCount,
    int Pending,
    int Assigned,
    int InProgress,
    int Completed,
    int Cancelled
);

public sealed class GetPickListWidgetStatsQuery : IQuery<PickListWidgetStatsDto>
{
    public sealed class Handler(WmsDbContextWrite dbContext, ICurrentUser currentUser) : IQueryHandler<GetPickListWidgetStatsQuery, PickListWidgetStatsDto>
    {
        public async Task<PickListWidgetStatsDto> HandleAsync(GetPickListWidgetStatsQuery query, CancellationToken ct = default)
        {
            long warehouseId = currentUser.WarehouseId ?? 0; if (warehouseId == 0) throw new BusinessRuleException("User is not associated with any warehouse.");

            var today = DateTime.UtcNow.Date;
            var dateStart = today.AddDays(-1);
            var dateEnd = today.AddDays(1);

            var baseQuery = dbContext.PickLists.AsNoTracking()
                .Where(p => p.IsActive == true && p.IsDeleted == false
                && p.WarehouseId == warehouseId
                && p.CreatedDate >= dateStart
                && p.CreatedDate < dateEnd);

            var stats = await baseQuery.GroupBy(p => p.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);

            int GetCount(string status) => stats.FirstOrDefault(s => s.Status == status)?.Count ?? 0;


            var total = stats.Sum(s => s.Count);

            return new PickListWidgetStatsDto(
                total,
                GetCount(nameof(PickListStatus.PENDING)),
                GetCount(nameof(PickListStatus.ASSIGNED)),
                GetCount(nameof(PickListStatus.IN_PROGRESS)),
                GetCount(nameof(PickListStatus.COMPLETED)),
                GetCount(nameof(PickListStatus.CANCELLED))
            );
        }
    }
}