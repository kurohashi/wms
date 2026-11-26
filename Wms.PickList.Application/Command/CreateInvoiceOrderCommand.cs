using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wms.Contracts.Interfaces;
using Wms.Domain.CustomEntities;
using Wms.Domain.Entities;
using Wms.Domain.Enums;
using Wms.Domain.MysqlEntities;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Command;

/// <summary>
/// Phase 1 — Fast HTTP handler.
/// Creates InvoiceOrder records, saves them, fires Phase 2 in the background, and returns IDs immediately.
/// Phase 2 (Batch + InventoryLedger + OMS event) runs via <see cref="ProcessInvoiceOrderCommand"/>.
/// </summary>
public sealed class CreateInvoiceOrderCommand(Guid routeUuid, long? warehouseId = null, bool isManual = false) : ICommand<CreateInvoiceOrderResultDto>
{
    public Guid RouteUuid { get; } = routeUuid;
    public long? WarehouseId { get; } = warehouseId;
    public bool IsManual { get; } = isManual;

    public sealed class Handler(WmsDbContextWrite dbContext, MysqlDbContext mysqlDbContext, ICurrentUser currentUser,
        ILogger<Handler> logger) : ICommandHandler<CreateInvoiceOrderCommand, CreateInvoiceOrderResultDto>
    {
        public async Task<CreateInvoiceOrderResultDto> HandleAsync(CreateInvoiceOrderCommand command, CancellationToken ct = default)
        {
            long warehouseId = command.WarehouseId ?? currentUser.WarehouseId ?? throw new BusinessRuleException("User is not associated with any warehouse.");

            var targetOrderStatus = command.IsManual
                ? nameof(WmsOrderStatusEnum.INVOICED_BY_WMS)
                : nameof(WmsOrderStatusEnum.INVOICED);

            // 1. Get RouteMaster for that RouteUuid
            var routeMaster = await dbContext.RouteMasters
                .FirstOrDefaultAsync(r => r.RouteUuid == command.RouteUuid && r.WarehouseId == warehouseId, ct);

            if (routeMaster == null)
                throw new BusinessRuleException($"RouteMaster not found for RouteUuid {command.RouteUuid} and WarehouseId {warehouseId}.");

            // 2. Get all OrderIds for this Route in a single query
            var orderIds = await dbContext.RouteOrders
                .AsNoTracking()
                .Where(ro => ro.RouteId == routeMaster.Id && ro.OrderId != null)
                .Select(ro => ro.OrderId!.Value)
                .Distinct()
                .ToListAsync(ct);

            if (orderIds.Count == 0)
                throw new BusinessRuleException($"No orders found for RouteUuid {command.RouteUuid}.");

            // 3. Batch load all WmsOrders with their items and TransferOrder warehouse details
            var wmsOrders = await dbContext.WmsOrders
                .Include(o => o.WmsOrderItems)
                .Include(o => o.TransferOrder)
                    .ThenInclude(to => to!.FromWarehouse)
                .Include(o => o.TransferOrder)
                    .ThenInclude(to => to!.ToWarehouse)
                .Where(o => orderIds.Contains(o.Id) && o.WarehouseId == warehouseId)
                .ToListAsync(ct);

            if (wmsOrders.Count == 0)
                throw new BusinessRuleException($"No WmsOrders found for RouteUuid {command.RouteUuid}.");

            // 4. Get RouteOrders for that Route
            var routeOrders = await dbContext.RouteOrders
                .Where(ro => ro.RouteId == routeMaster.Id && ro.OrderId != null)
                .ToListAsync(ct);

            if (routeOrders.Count == 0) throw new BusinessRuleException($"No RouteOrders found for RouteUuid {command.RouteUuid}.");

            // Fetch paymentStatus from MysqlDbContext.CustomerOrders for all unique order IDs
            var uniqueOrderIds = wmsOrders.Select(o => o.UniqueOrderId)
                .Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();

            logger.LogInformation("Searching unique order IDs in MySQL LDB: {UniqueOrderIds}", string.Join(", ", uniqueOrderIds));

            var paymentStatusMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (uniqueOrderIds.Count > 0)
            {
                var placeholders = string.Join(",", uniqueOrderIds.Select((_, i) => $"{{{i}}}"));
                var sql = $"SELECT * FROM customer_orders WHERE unique_order_id IN ({placeholders})";

                var customerOrders = await mysqlDbContext.CustomerOrders
                    .FromSqlRaw<CustomerOrder>(sql, uniqueOrderIds.Cast<object?>().ToArray())
                    .AsNoTracking()
                    .Select(co => new { co.UniqueOrderId, co.PaymentStatus })
                    .ToListAsync(ct);

                paymentStatusMap = customerOrders
                    .GroupBy(co => co.UniqueOrderId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().PaymentStatus, StringComparer.OrdinalIgnoreCase);
            }

            // 3. Batch check existing InvoiceOrders
            var existingInvoiceOrders = await dbContext.InvoiceOrders
                .AsNoTracking()
                .Where(io => orderIds.Contains(io.OrderId))
                .Select(io => new { io.OrderId, io.Id })
                .ToListAsync(ct);

            var existingInvoiceOrderMap = existingInvoiceOrders
                .ToDictionary(io => io.OrderId, io => io.Id);

            var createdInvoiceOrderIds = new List<long>();
            var newInvoiceOrders = new List<InvoiceOrder>();
            var ordersToProcessInBackground = new List<long>();
            var wmsOrderToInvoiceIdMap = new Dictionary<long, long>();

            // Update route status on routeMaster and routeOrders
            routeMaster.Status = nameof(RouteStatusEnum.INVOICED);
            foreach (var routeOrder in routeOrders)
            {
                routeOrder.Status = nameof(RouteStatusEnum.INVOICED);
            }

            foreach (var wmsOrder in wmsOrders)
            {
                // Intra-state (same-state) Transfer Orders do not require an invoice under GST rules
                bool isSameStateTransferOrder = wmsOrder.TransferOrder != null &&
                    (
                        (!string.IsNullOrEmpty(wmsOrder.TransferOrder.FromWarehouse?.State) &&
                         string.Equals(wmsOrder.TransferOrder.FromWarehouse.State, wmsOrder.TransferOrder.ToWarehouse?.State, StringComparison.OrdinalIgnoreCase))
                        ||
                        (!string.IsNullOrEmpty(wmsOrder.TransferOrder.FromWarehouse?.StateCode) &&
                         string.Equals(wmsOrder.TransferOrder.FromWarehouse.StateCode, wmsOrder.TransferOrder.ToWarehouse?.StateCode, StringComparison.OrdinalIgnoreCase))
                    );

                if (isSameStateTransferOrder)
                {
                    logger.LogInformation("Order {OrderId} (#{UniqueOrderId}) is an intra-state Transfer Order ({FromState} -> {ToState}). Skipping invoice creation.",
                        wmsOrder.Id, wmsOrder.UniqueOrderId, wmsOrder.TransferOrder?.FromWarehouse?.State, wmsOrder.TransferOrder?.ToWarehouse?.State);
                    if (wmsOrder.OrderStatus != nameof(WmsOrderStatusEnum.DISPATCHED))
                        wmsOrder.OrderStatus = targetOrderStatus;
                    continue;
                }

                if (wmsOrder.OrderStatus != nameof(WmsOrderStatusEnum.DISPATCHED))
                    wmsOrder.OrderStatus = targetOrderStatus;

                // Handle existing InvoiceOrder (idempotency & retry Phase 2 if needed)
                if (existingInvoiceOrderMap.TryGetValue(wmsOrder.Id, out var existingId))
                {
                    createdInvoiceOrderIds.Add(existingId);
                    ordersToProcessInBackground.Add(existingId);
                    wmsOrderToInvoiceIdMap[wmsOrder.Id] = existingId;
                    continue;
                }

                // Generate InvoiceNumber via DB sequence
                var invoiceNumber = await GetDocumentNumberAsync("invoice", wmsOrder.WarehouseId, ct);

                // Determine if this is a prepaid order (paymentStatus != "pending")
                paymentStatusMap.TryGetValue(wmsOrder.UniqueOrderId ?? string.Empty, out var paymentStatus);
                bool isPrepaid = !string.IsNullOrEmpty(paymentStatus)
                    && !string.Equals(paymentStatus, "pending", StringComparison.OrdinalIgnoreCase);

                // Build InvoiceOrder + InvoiceOrderItems in memory
                var invoiceOrder = new InvoiceOrder
                {
                    WarehouseId = wmsOrder.WarehouseId,
                    OrderId = wmsOrder.Id,
                    InvoiceNumber = invoiceNumber,
                    UniqueOrderId = wmsOrder.UniqueOrderId,
                    NetAmount = wmsOrder.NetAmount,
                    OrderStatus = wmsOrder.OrderStatus,
                    DeliveryDateTime = wmsOrder.DeliveryDateTime,
                    TransferOrderId = wmsOrder.TransferOrderId,
                    Outlet = wmsOrder.Outlet,
                    CustomerType = wmsOrder.CustomerType,
                    SmallCartCost = wmsOrder.SmallCartCost,
                    DeliveryCost = wmsOrder.DeliveryCost,
                    IsPrepaid = isPrepaid,
                    InvoiceOrderItems = wmsOrder.WmsOrderItems.Select(item => new InvoiceOrderItem
                    {
                        Sku = item.Sku,
                        Name = item.Name,
                        Unit = item.Unit,
                        Mrp = item.Mrp,
                        Weight = item.Weight,
                        BasePrice = item.BasePrice,
                        PickedQuantity = item.ConfirmedQuantity ?? 0,
                        TotalItemQuantity = item.TotalItemQuantity
                    }).ToList()
                };

                newInvoiceOrders.Add(invoiceOrder);
            }

            // 5. Bulk save all entities (InvoiceOrders, WmsOrders, RouteMasters, RouteOrders) in 1 single database transaction
            if (newInvoiceOrders.Count > 0)
            {
                await dbContext.InvoiceOrders.AddRangeAsync(newInvoiceOrders, ct);
            }

            await dbContext.SaveChangesAsync(ct);

            foreach (var newIo in newInvoiceOrders)
            {
                createdInvoiceOrderIds.Add(newIo.Id);
                ordersToProcessInBackground.Add(newIo.Id);
                wmsOrderToInvoiceIdMap[newIo.OrderId] = newIo.Id;
            }

            // Determine CNInvoiceId list: paymentStatus is not pending AND confirmed quantity != total item quantity for any item
            var cnInvoiceOrderIds = new List<long>();
            foreach (var wmsOrder in wmsOrders)
            {
                if (wmsOrderToInvoiceIdMap.TryGetValue(wmsOrder.Id, out var invoiceOrderId))
                {
                    paymentStatusMap.TryGetValue(wmsOrder.UniqueOrderId, out var paymentStatus);
                    bool isNotPending = !string.IsNullOrEmpty(paymentStatus) && !string.Equals(paymentStatus, "pending", StringComparison.OrdinalIgnoreCase);
                    bool hasQuantityMismatch = wmsOrder.WmsOrderItems.Any(item => (item.ConfirmedQuantity ?? 0) != item.TotalItemQuantity);

                    if (isNotPending && hasQuantityMismatch)
                    {
                        cnInvoiceOrderIds.Add(invoiceOrderId);
                    }
                }
            }

            // 6. Return immediately — Phase 2 background tasks are queued at the API level via IBackgroundTaskQueue
            return new CreateInvoiceOrderResultDto(createdInvoiceOrderIds, cnInvoiceOrderIds);
        }

        private async Task<string> GetDocumentNumberAsync(string documentType, long warehouseId, CancellationToken ct)
        {
            var result = mysqlDbContext.Database
                .SqlQueryRaw<DocumentNumberResult>(
                    "CALL GenDocSeqNum({0}, {1})",
                    documentType,
                    warehouseId)
                .AsEnumerable()
                .First();

            return result.DocumentNumber;
        }
    }
}
