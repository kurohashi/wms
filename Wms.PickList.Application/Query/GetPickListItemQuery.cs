using System;
using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Query;

public sealed class GetPickListItemQuery(Guid pickListItemUuid, Guid binUuid)
    : IQuery<PickListItemAllocationDto>
{
    public Guid PickListItemUuid { get; } = pickListItemUuid;
    public Guid BinUuid { get; } = binUuid;

    public sealed class Handler(WmsDbContextWrite dbContext)
        : IQueryHandler<GetPickListItemQuery, PickListItemAllocationDto>
    {
        public async Task<PickListItemAllocationDto> HandleAsync(
            GetPickListItemQuery query,
            CancellationToken ct = default)
        {

            var id = await dbContext.PickListItems
               .AsNoTracking()
               .Where(x => x.PickListItemUuid == query.PickListItemUuid)
               .Select(x => new
               {
                   x.Id,
                   x.SkuId,
                   x.RequestedQty,
                   x.PickedQty
               })
               .FirstOrDefaultAsync(ct);

            if (id == null)
                throw new BusinessRuleException("Pick list item not found.");

            var binId = await dbContext.Bins
                .Where(x => x.BinUuid == query.BinUuid)
                .Select(x => x.Id)
                .FirstOrDefaultAsync(ct);

            if (binId == 0)
                throw new BusinessRuleException("binId not found.");

            var locInvBatchId = await dbContext.LocationInventoryBatches
                .Where(x => x.LocationId == binId && x.SkuId == id.SkuId)
                .Select(x => new
                {
                    x.Id,
                    x.AvailableQty
                })
                .FirstOrDefaultAsync(ct);

            if (locInvBatchId == null)
                throw new BusinessRuleException("Inventory Batch Id not found.");

            var picked = id.PickedQty ?? 0m;
            var allocatedNum = id.RequestedQty - picked;
            if (allocatedNum <= 0m)
                allocatedNum = 0m;

            var available = locInvBatchId.AvailableQty;

            var lower = Math.Min(allocatedNum, available);

            return new PickListItemAllocationDto(
                PickListItemId: id.Id,
                LocationInventoryBatchId: locInvBatchId.Id,
                Quantity: lower
            );

        }
    }
}