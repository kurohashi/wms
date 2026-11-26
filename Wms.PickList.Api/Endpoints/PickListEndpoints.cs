using Dit.Db.Extentions.Extentions.Paging;
using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Exceptions;
using Dit.Shared.Exceptions.Models;
using Microsoft.AspNetCore.Mvc;
using Wms.Infrastructure.Background;
using Wms.Order.Application.Command;
using Wms.PickList.Application.Command;
using Wms.PickList.Application.Dto;
using Wms.PickList.Application.Query;

namespace Wms.PickList.Api.Endpoints;

public static class PickListEndpoints
{
    // Placeholder user ID â€” replace with real auth when implemented.
    private const string SystemUserId = "1";

    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/picklist").WithTags("PickLists");

        // POST /picklist/generate â€” select routes/orders, then generate pick lists in background
        group.MapPost("/generate", async ([FromBody] List<int>? routeIds, [FromQuery] int? batchSize, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var effectiveBatchSize = batchSize ?? 10;
            var response = await dispatcher.SendAsync<GeneratePickListsCommand, GeneratePickListsQueuedResponse>(
                new GeneratePickListsCommand(routeIds, effectiveBatchSize),
                ct);

            return Results.Accepted(
                "/picklist/generate",
                ApiResponse<GeneratePickListsQueuedResponse>.Ok(
                    response,
                    response.Message));
        }).AllowAnonymous()
        .WithName("GeneratePickLists");

        // POST /picklists â€” Create Pick List
        group.MapPost("/", async (CreatePickListRequestDto request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                if (request.Items == null || request.Items.Count == 0)
                    return Results.BadRequest(ApiResponse.Fail("At least one item is required."));

                var uuid = await dispatcher.SendAsync<CreatePickListCommand, Guid>(
                    new CreatePickListCommand(request, SystemUserId), ct);

                return Results.Ok(ApiResponse<object>.Ok(new { pickListUuid = uuid }, "Pick list created successfully."));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ApiResponse.Fail(ex.Message));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
        });

        // GET /picklists/list â€” Get Pick Lists (Paginated)
        group.MapGet("/list", async ([AsParameters] GetPickListsQuery query, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.QueryAsync<GetPickListsQuery, GetPickListsQueryResult>(query, ct);

            return Results.Ok(result);
        });

        group.MapGet("/stats", async (IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.QueryAsync<GetPickListWidgetStatsQuery, PickListWidgetStatsDto>(new GetPickListWidgetStatsQuery(), ct);
            return Results.Ok(ApiResponse<PickListWidgetStatsDto>.Ok(result));
        });

        group.MapGet("/manager/details/{pickListUuid:guid}", async (Guid pickListUuid, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var details = await dispatcher.QueryAsync<GetPickListManagerDetailsQuery, PickListDetailsDto>(
                    new GetPickListManagerDetailsQuery(pickListUuid), ct);

                return Results.Ok(ApiResponse<PickListDetailsDto>.Ok(
                    details));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ApiResponse.Fail(ex.Message));
            }
        });

        group.MapGet("/executive/details/{pickListUuid:guid}", async (Guid pickListUuid, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var details = await dispatcher.QueryAsync<GetPickListExecutiveDetailsQuery, PickListDetailsDto>(
                    new GetPickListExecutiveDetailsQuery(pickListUuid), ct);

                return Results.Ok(ApiResponse<PickListDetailsDto>.Ok(
                    details));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ApiResponse.Fail(ex.Message));
            }
        });


        // PUT /picklists/{pickListUuid}/reassign â€” Reassign Worker/Dropzone/BinLocation
        group.MapPut("/{pickListUuid:Guid}/reassign", async (Guid pickListUuid, ReassignPickListRequestDto request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var userId = "1";
                bool result = false;

                switch (request.Type)
                {
                    case ReassignType.Worker:
                        result = await dispatcher.SendAsync<ReassignPickListWorkerCommand, bool>(
                            new ReassignPickListWorkerCommand(pickListUuid, request), ct);
                        break;

                    case ReassignType.Dropzone:
                        result = await dispatcher.SendAsync<ReassignPickListDropzoneCommand, bool>(
                            new ReassignPickListDropzoneCommand(pickListUuid, request), ct);
                        break;

                    default:
                        throw new BusinessRuleException("Invalid ReassignType");
                }

                return Results.Ok(ApiResponse<bool>.Ok(result, "Pick list reassigned successfully."));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ApiResponse.Fail(ex.Message));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
        }).WithName("ReassignPickList");


        group.MapPatch("/{pickListUuid:guid}/status", async (Guid pickListUuid, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var result = await dispatcher.SendAsync<UpdatePickListStatusCommand, bool>(
                    new UpdatePickListStatusCommand(pickListUuid, SystemUserId), ct);

                if (!result)
                {
                    return Results.NotFound(ApiResponse.Fail($"PickList with UUID {pickListUuid} not found."));
                }

                return Results.Ok(ApiResponse<object>.Ok(
                    new { pickListUuid, status = "IN_PROGRESS" },
                    "Pick list status updated to IN_PROGRESS."
                ));
            }
            catch (Exception ex)
            {
                var dbError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return Results.BadRequest(ApiResponse.Fail(dbError));
            }
        });

        // Worker Picklist Endpoint
        group.MapGet("/tasks/{workerUuid:guid}", async (Guid workerUuid, int? page, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var result = await dispatcher.QueryAsync<GetWorkerPickListQuery, PagedResult<WorkerPickListDto>>(new GetWorkerPickListQuery
                { WorkerUuid = workerUuid, Page = page }, ct);

                return Results.Ok(
                    ApiResponse<PagedResult<WorkerPickListDto>>.Ok(result));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ApiResponse.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Internal Server Error",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // POST /picklist/invoice-order/add â€” Create Invoice Orders for all orders in a route
        group.MapPost("/invoice-order/add", async ([FromQuery] Guid routeUuid, IDispatcher dispatcher, IBackgroundTaskQueue backgroundTaskQueue, CancellationToken ct) =>
        {
            try
            {
                var result = await dispatcher.SendAsync<CreateInvoiceOrderCommand, CreateInvoiceOrderResultDto>(
                    new CreateInvoiceOrderCommand(routeUuid, isManual: true), ct);

                foreach (var invoiceOrderId in result.InvoiceOrderIds)
                {
                    await backgroundTaskQueue.QueueCommandAsync(new ProcessInvoiceOrderCommand(invoiceOrderId));
                }

                await backgroundTaskQueue.QueueCommandAsync(new CreatePostSortingCreditNote(result.CNInvoiceId));

                return Results.Created($"/picklist/invoice-order?routeUuid={routeUuid}", ApiResponse<object>.Ok(
                    new { invoiceOrderIds = result.InvoiceOrderIds, cnInvoiceId = result.CNInvoiceId },
                    "Invoice orders created successfully."));
            }
            catch (BusinessRuleException ex)
            {
                return Results.BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Internal Server Error",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // POST /picklist/invoice-order/{invoiceOrderId:long}/sync â€” Trigger Zoho sync (or full processing) manually for a single Invoice Order in case of failure
        group.MapPost("/invoice-order/{invoiceOrderId:long}/sync", async (long invoiceOrderId, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                await dispatcher.SendAsync(new ProcessInvoiceOrderCommand(invoiceOrderId), ct);

                return Results.Ok(ApiResponse<object>.Ok(
                    new { invoiceOrderId },
                    "Invoice order Zoho sync triggered successfully."));
            }
            catch (BusinessRuleException ex)
            {
                return Results.BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ApiResponse<object>.Fail(ex.Message));
            }
        });

        // suggested bins for a given sku
        group.MapGet("/suggested-bins", async (Guid picklistItemUuid, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var result = await dispatcher.QueryAsync<GetSuggestedBinsQuery, SuggestedBinsResponseDto>(
                    new GetSuggestedBinsQuery(picklistItemUuid), ct);

                return Results.Ok(ApiResponse<SuggestedBinsResponseDto>.Ok(result));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
        });


        group.MapPost("/executive/pick", async (NextPickingRequestDto request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var result = await dispatcher.SendAsync<PickCommand, NextPickingResponseDto>(new PickCommand(request), ct);
                return Results.Ok(ApiResponse<NextPickingResponseDto>.Ok(result));
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Internal Server Error",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);

            }
        });

        group.MapPost("/fallback-pick", async (FallbackPickListRequestDto request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var result = await dispatcher.SendAsync<FallbackPickPickListCommand, FallbackPickListResponseDto>(
                    new FallbackPickPickListCommand(request), ct);
                return Results.Ok(ApiResponse<FallbackPickListResponseDto>.Ok(result, result.Message));
            }
            catch (BusinessRuleException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Internal Server Error",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("FallbackPickPickList");


        //// endpoint except: save bin -> create inventory allocation
        //group.MapPost("/save-bin-to-picklist/{binUuid:guid}/{picklistItemUuid:guid}", async (Guid binUuid, Guid picklistItemUuid, IDispatcher dispatcher, CancellationToken ct) =>
        //{
        //    try
        //    {
        //        // Query to compute allocation (PickListItemId, LocationInventoryBatchId, Quantity)
        //        var allocationDto = await dispatcher.QueryAsync<GetPickListItemQuery, PickListItemAllocationDto>(new GetPickListItemQuery(picklistItemUuid, binUuid), ct);

        //        if (allocationDto == null)
        //            return Results.NotFound(ApiResponse.Fail("Allocation data not found."));

        //        if (allocationDto.Quantity <= 0)
        //            return Results.BadRequest(ApiResponse.Fail("No allocatable quantity available."));

        //        // Persist allocation into InventoryAllocation (creates row and adjusts location available qty)
        //        // Assumes AddInventoryAllocationCommand exists and returns the created allocation Id (long)
        //        var allocationId = await dispatcher.SendAsync<AddInventoryAllocationCommand, long>(new AddInventoryAllocationCommand(allocationDto), ct);

        //        return Results.Ok(ApiResponse<long>.Ok(allocationId, "Bin allocated and saved to picklist item."));
        //    }
        //    catch (Exception ex)
        //    {
        //        // log if you have logger in scope
        //        return Results.BadRequest(ApiResponse.Fail(ex.Message));
        //    }
        //});


        group.MapPost("/picklistquickorder/{orderId:long}", async (long orderId, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var result = await dispatcher.SendAsync<GeneratePickListForOrderCommand, GeneratePickListsResult>(new GeneratePickListForOrderCommand(orderId), ct);

                return Results.Ok(ApiResponse<GeneratePickListsResult>.Ok(result, "Pick list generated successfully."));
            }
            catch (Exception ex)
            {
                var dbError = ex.InnerException?.Message ?? ex.Message;
                return Results.BadRequest(ApiResponse.Fail(dbError));
            }
        });

        group.MapPost("/inventory-allocation", async (CreateInventoryAllocationDto request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var id = await dispatcher.SendAsync<CreateInventoryAllocationCommand, long>(
                    new CreateInventoryAllocationCommand(request), ct);
                return Results.Ok(ApiResponse<long>.Ok(id, "Inventory allocation created successfully."));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(ApiResponse.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Internal Server Error", detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        group.MapPost("/report-damage", async (ReportPickListDamageDto request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var result = await dispatcher.SendAsync<ReportPickListDamageCommand, ReportPickListDamageResponseDto>(
                    new ReportPickListDamageCommand(request), ct);
                return Results.Ok(ApiResponse<ReportPickListDamageResponseDto>.Ok(result, "Damage reported successfully."));
            }
            catch (NotFoundException ex)
            {
                return Results.NotFound(ApiResponse.Fail(ex.Message));
            }
            catch (BusinessRuleException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Internal Server Error", detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("ReportPickListDamage");

        group.MapPatch("/update-priority", async (UpdatePickListPriorityDto request, IDispatcher dispatcher, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                await dispatcher.SendAsync<UpdatePickListPriorityCommand, UpdatePickListPriorityResponseDto>(new UpdatePickListPriorityCommand(request), ct);
                return Results.Ok(ApiResponse.Ok("Pick list priority updated successfully."));
            }
            catch (NotFoundException ex)
            {
                return Results.NotFound(ApiResponse.Fail(ex.Message));
            }
            catch (BusinessRuleException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Internal Server Error", detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        group.MapPost("/cancel/{pickListNumber}", async (string pickListNumber, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var result = await dispatcher.SendAsync<CancelPickListCommand, bool>(new CancelPickListCommand(pickListNumber), ct);
                return Results.Ok(ApiResponse<bool>.Ok(result, "Pick list cancelled successfully."));
            }
            catch (NotFoundException ex)
            {
                return Results.NotFound(ApiResponse.Fail(ex.Message));
            }
            catch (BusinessRuleException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Internal Server Error", detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("CancelPickList");

        group.MapPost("/bulk-reassign", async (BulkReassignPickListRequestDto request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                var result = await dispatcher.SendAsync<BulkReassignPickListCommand, BulkReassignPickListResponseDto>(new BulkReassignPickListCommand(request), ct);
                return Results.Ok(ApiResponse<BulkReassignPickListResponseDto>.Ok(result, result.Message));
            }
            catch (NotFoundException ex)
            {
                return Results.NotFound(ApiResponse.Fail(ex.Message));
            }
            catch (BusinessRuleException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Internal Server Error", detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .WithName("BulkReassignPickList");
    }
}
