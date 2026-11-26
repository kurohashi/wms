using Dit.Shared.Cqrs.Abstractions;
using Microsoft.EntityFrameworkCore;
using Wms.Inventory.Application.ProductInventory.Command;
using Wms.Inventory.Application.ProductInventory.Dto;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Services.Cancellation;

public sealed class SyncInventory(WmsDbContextWrite dbContextWrite, IDispatcher dispatcher)
{
    public async Task ExecuteAsync(List<long> orderItemIds, CancellationToken ct)
    {
        var skuQuantities = await GetSkuIdFromOrderItemIds(orderItemIds, ct);
        foreach (var skuQuantity in skuQuantities)
        {
            var transactInventoryData = new TransactInventoryRequestDto(
                WarehouseId: skuQuantity.WarehouseId,
                SkuId: skuQuantity.Sku,
                Quantity: -(skuQuantity.Quantity),
                StockType: "SELLABLE",
                IsSyncToOms: true,
                IsSyncToZoho: false);

            await dispatcher.SendAsync<TransactInventoryCommand, TransactInventoryResponseDto>(new TransactInventoryCommand(transactInventoryData), ct);

        }
        await dbContextWrite.SaveChangesAsync(ct);
    }
    private async Task<List<SkuQuantityDto>> GetSkuIdFromOrderItemIds(List<long> orderItemIds, CancellationToken ct)
    {
        var skuData = await dbContextWrite.WmsOrderItems
            .Include(oi => oi.Order)
            .Where(oi => orderItemIds.Contains(oi.Id))
            .GroupBy(oi => new
            {
                oi.Sku,
                oi.Order.WarehouseId
            })
            .Select(g => new SkuQuantityDto
            (
                Sku : g.Key.Sku,
                Quantity : g.Sum(x => x.TotalItemQuantity),
                WarehouseId : g.Key.WarehouseId
            ))
            .Distinct()
            .ToListAsync(ct);
        
        return skuData;
    }
}
