using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Wms.Domain.Entities;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;

namespace Wms.PickList.Application.Command;

public sealed class ProcessInvoiceOrderCommand(long invoiceOrderId) : ICommand
{
    public long InvoiceOrderId { get; } = invoiceOrderId;

    public sealed class Handler(WmsDbContextWrite dbContext, MysqlDbContext mysqlDbContext, IHttpClientFactory httpClientFactory,
        IConfiguration configuration, ILogger<Handler> logger) : ICommandHandler<ProcessInvoiceOrderCommand>
    {
        public async System.Threading.Tasks.Task HandleAsync(ProcessInvoiceOrderCommand command, CancellationToken ct = default)
        {

            // 1. Load InvoiceOrder
            var invoiceOrder = await dbContext.InvoiceOrders
                .Include(io => io.InvoiceOrderItems)
                .FirstOrDefaultAsync(io => io.Id == command.InvoiceOrderId, ct)
                ?? throw new BusinessRuleException(
                    $"InvoiceOrder with Id {command.InvoiceOrderId} not found. " +
                    "It may have been deleted between Phase 1 and Phase 2.");

            // 2. Load WmsOrder with its items
            var wmsOrder = await dbContext.WmsOrders
                .AsNoTracking()
                .Include(o => o.WmsOrderItems)
                .FirstOrDefaultAsync(o => o.Id == invoiceOrder.OrderId &&
                (o.OrderStatus == nameof(WmsOrderStatusEnum.INVOICED) || o.OrderStatus == nameof(WmsOrderStatusEnum.DISPATCHED) || o.OrderStatus == nameof(WmsOrderStatusEnum.SORTING_COMPLETED)), ct)
                ?? throw new BusinessRuleException(
                    $"WmsOrder with Id {invoiceOrder.OrderId} not found.");

            // 3. Build SKU → List of (FromBatchId, AllocatedQty)
            //    via: PickListItem → InventoryAllocation → LocationInventoryBatch
            // var allocationsBySku = await dbContext.PickListItems
            //     .AsNoTracking()
            //     .Where(pli => dbContext.WmsOrderItems
            //         .Any(woi => woi.OrderId == wmsOrder.Id && woi.Id == pli.OrderItemId))
            //     .SelectMany(pli => pli.InventoryAllocations)
            //     .Where(ia => ia.LocationBatchId != null && ia.IsPickable == true)
            //     .Select(ia => new
            //     {
            //         ia.PickListItem.SkuId,
            //         FromBatchId = 23, //Place Holder Number. The entire function is to be potentially deleted.
            //         PickedQty = ia.PickedQty ?? ia.AllocatedQty
            //     })
            //     .ToListAsync(ct);

            // var skuAllocationsMap = allocationsBySku
            //     .GroupBy(x => x.SkuId)
            //     .ToDictionary(
            //         g => g.Key,
            //         g => g.Select(a => new { a.FromBatchId, a.PickedQty }).ToList());

            // // Check if ledger entries are already created
            // var alreadyCreated = await dbContext.InventoryLedgers
            //     .AnyAsync(l => l.ReferenceType == "INVOICE" && l.ReferenceId == invoiceOrder.Id, ct);

            // if (!alreadyCreated)
            // {
            //     // 4. Create Batches + InventoryLedger entries
            //     foreach (var item in wmsOrder.WmsOrderItems)
            //     {
            //         var remainingConfirmed = item.ConfirmedQuantity ?? 0;
            //         if (remainingConfirmed <= 0) continue;

            //         if (skuAllocationsMap.TryGetValue(item.Sku, out var allocations))
            //         {
            //             foreach (var allocation in allocations)
            //             {
            //                 if (remainingConfirmed <= 0) break;

            //                 var qtyToDeduct = Math.Min(remainingConfirmed, (int)allocation.PickedQty);
            //                 if (qtyToDeduct <= 0) continue;

            //                 remainingConfirmed -= qtyToDeduct;

            //                 var batchNumber = await dbContext.Database
            //                     .SqlQuery<string>($"""
            //                         SELECT GenerateOutwardBatchNumberWH({wmsOrder.WarehouseId}) AS "Value"
            //                     """)
            //                     .FirstAsync(ct);

            //                 var batch = new Batch
            //                 {
            //                     BatchUuid = Guid.NewGuid(),
            //                     SkuId = item.Sku,
            //                     BatchNumber = batchNumber,
            //                     Quantity = qtyToDeduct,
            //                     Type = "OUTWARD",
            //                     Status = "ACTIVE",
            //                     IsActive = true,
            //                     IsDeleted = false,
            //                 };
            //                 await dbContext.Batches.AddAsync(batch, ct);

            //                 var ledgerEntry = new InventoryLedger
            //                 {
            //                     WarehouseId = wmsOrder.WarehouseId,
            //                     SkuId = item.Sku,
            //                     Batch = batch,
            //                     FromBatchId = allocation.FromBatchId,
            //                     ReferenceType = "INVOICE",
            //                     ReferenceId = invoiceOrder.Id,
            //                     ReferenceNumber = invoiceOrder.InvoiceNumber,
            //                     Quantity = qtyToDeduct,
            //                     Direction = "OUT",
            //                     TransactionTime = DateTime.UtcNow,
            //                     IsActive = true,
            //                     IsDeleted = false,
            //                 };
            //                 await dbContext.InventoryLedgers.AddAsync(ledgerEntry, ct);
            //             }
            //         }
            //     }

            //     // 5. Persist all Batch + InventoryLedger rows atomically
            //     await dbContext.SaveChangesAsync(ct);
            // }
            // else
            // {
            //     logger.LogInformation("InvoiceOrder {InvoiceOrderId} already has inventory ledger entries created. Skipping batch generation.",
            //         invoiceOrder.Id);
            // }

            // Pre-populate adjust_quantity on MySQL order_items so OMS reads correct delivered quantities when preparing Zoho invoice
            await PrePopulateOmsOrderItemsAsync(invoiceOrder.UniqueOrderId, [.. invoiceOrder.InvoiceOrderItems], invoiceOrder.IsPrepaid, ct);

            // Sync to Zoho
            await SyncInvoiceOrderToZohoAsync(invoiceOrder.UniqueOrderId, invoiceOrder.InvoiceNumber, [.. invoiceOrder.InvoiceOrderItems], invoiceOrder.SmallCartCost ?? 0, invoiceOrder.DeliveryCost ?? 0, invoiceOrder.IsPrepaid, ct);

            // Reconcile OMS order and items in MySQL to ensure adjust_quantity and adjust_net_amount match invoice
            await ReconcileOmsOrderAfterInvoiceAsync(invoiceOrder.UniqueOrderId, [.. invoiceOrder.InvoiceOrderItems], invoiceOrder.IsPrepaid, ct);

        }

        private async System.Threading.Tasks.Task SyncInvoiceOrderToZohoAsync(
            string uniqueOrderId,
            string invoiceNumber,
            List<InvoiceOrderItem> invoiceOrderItems,
            decimal? smallCartCost,
            decimal? deliveryCost,
            bool isPrepaid,
            CancellationToken ct)
        {
            var zohoSyncUrl = configuration["ZohoSettings:OmsInvoiceSyncUrl"];

            var itemsPayload = invoiceOrderItems
                .Select(item => new
                {
                    sku = item.Sku.ToString(),
                    quantity = isPrepaid ? item.TotalItemQuantity : item.PickedQuantity,  // this logic is very cruical before changing ask Ankit sir only
                    rate = item.BasePrice,
                    discount = item.Discount
                })
                .Where(x => x.quantity > 0)
                .ToList();

            if (!itemsPayload.Any())
            {
                logger.LogWarning($"[Zoho Sync] No picked items found for Invoice {invoiceNumber}. Skipping Zoho sync.");
                return;
            }

            var invoicePayload = new
            {
                type = "wms",
                data = new
                {
                    unique_order_id = uniqueOrderId,
                    invoiceNumber = invoiceNumber,
                    items = itemsPayload,
                    smallCartCost = smallCartCost,
                    deliveryCost = deliveryCost,
                }
            };

            var payload = new
            {
                type = "INVOICE",
                payload = invoicePayload
            };

            using var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, zohoSyncUrl);

            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload, jsonOptions);
            request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            try
            {
                var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(ct);
                    throw new BusinessRuleException($"Status: {response.StatusCode}, Details: {errorContent}");
                }
                logger.LogInformation($"[Zoho Sync] Successfully synced Invoice {invoiceNumber} to Zoho.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"[Zoho Sync] Failed to sync Invoice {invoiceNumber} to Zoho. Url: {zohoSyncUrl}");
                throw;
            }
        }

        private async System.Threading.Tasks.Task PrePopulateOmsOrderItemsAsync(
            string uniqueOrderId,
            List<InvoiceOrderItem> invoiceOrderItems,
            bool isPrepaid,
            CancellationToken ct)
        {
            try
            {
                var customerOrder = await mysqlDbContext.CustomerOrders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(co => co.UniqueOrderId == uniqueOrderId, ct);

                if (customerOrder == null)
                {
                    return;
                }

                foreach (var item in invoiceOrderItems)
                {
                    var skuStr = item.Sku.ToString();
                    var targetQty = isPrepaid ? (double)item.TotalItemQuantity : (double)item.PickedQuantity;
                    await mysqlDbContext.Database.ExecuteSqlRawAsync(
                        "UPDATE order_items SET adjust_quantity = {0}, adjust_total_item_quantity = {0}, adjust_total_item_price = item_price * {0} WHERE order_id = {1} AND sku = {2}",
                        [targetQty, customerOrder.Id, skuStr],
                        ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[OMS Pre-Populate] Error pre-populating order_items for {UniqueOrderId}.", uniqueOrderId);
            }
        }

        private async System.Threading.Tasks.Task ReconcileOmsOrderAfterInvoiceAsync(
            string uniqueOrderId,
            List<InvoiceOrderItem> invoiceOrderItems,
            bool isPrepaid,
            CancellationToken ct)
        {
            try
            {
                // Only reconcile if there are shortages; for prepaid orders or 100% picked, OMS leaves order unedited as expected
                bool hasAnyShortage = !isPrepaid && invoiceOrderItems.Any(i => i.PickedQuantity < i.TotalItemQuantity);
                if (!hasAnyShortage)
                {
                    return;
                }

                var customerOrder = await mysqlDbContext.CustomerOrders
                    .AsNoTracking()
                    .FirstOrDefaultAsync(co => co.UniqueOrderId == uniqueOrderId, ct);

                if (customerOrder == null)
                {
                    return;
                }

                // Update order_items in MySQL where adjust_quantity was left at 0 or NULL for fulfilled items
                foreach (var item in invoiceOrderItems.Where(i => i.PickedQuantity > 0))
                {
                    var skuStr = item.Sku.ToString();
                    var targetQty = isPrepaid ? (double)item.TotalItemQuantity : (double)item.PickedQuantity;
                    await mysqlDbContext.Database.ExecuteSqlRawAsync(
                        "UPDATE order_items SET adjust_quantity = {0}, adjust_total_item_quantity = {0}, adjust_total_item_price = item_price * {0}, is_edited = 1 WHERE order_id = {1} AND sku = {2} AND (adjust_quantity = 0 OR adjust_quantity IS NULL)",
                        [targetQty, customerOrder.Id, skuStr],
                        ct);
                }

                // Check created OrderInvoice in MySQL
                var orderInvoice = await mysqlDbContext.OrderInvoices
                    .AsNoTracking()
                    .Where(oi => oi.OrderId == customerOrder.Id)
                    .OrderByDescending(oi => oi.Id)
                    .FirstOrDefaultAsync(ct);

                if (orderInvoice != null && orderInvoice.Total > 0)
                {
                    // Reconcile customer_orders adjust_net_amount with the actual invoice total
                    await mysqlDbContext.Database.ExecuteSqlRawAsync(
                        "UPDATE customer_orders SET adjustSubTotal = {0}, adjustTax = {1}, adjust_net_amount = {2}, is_edited = 1 WHERE id = {3}",
                        [(double)orderInvoice.PreTax, (double)orderInvoice.TaxAmount, (double)orderInvoice.Total, customerOrder.Id],
                        ct);

                    logger.LogInformation(
                        "[OMS Reconcile] Successfully reconciled OMS order {UniqueOrderId} (OrderId: {OrderId}) to Invoice Total {Total}.",
                        uniqueOrderId, customerOrder.Id, orderInvoice.Total);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[OMS Reconcile] Error reconciling OMS order {UniqueOrderId} after invoice sync.", uniqueOrderId);
            }
        }
    }
}
