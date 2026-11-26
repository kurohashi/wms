using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wms.Contracts.Interfaces;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;
using Wms.PickList.Application.Services;

namespace Wms.PickList.Application.Command;

public sealed class BulkReassignPickListCommand(BulkReassignPickListRequestDto dto) : ICommand<BulkReassignPickListResponseDto>
{
    public BulkReassignPickListRequestDto Dto { get; } = dto;

    public sealed class Handler(
        IDispatcher dispatcher,
        WmsDbContextWrite dbContext,
        ICurrentUser currentUser,
        ILogger<Handler> logger) : ICommandHandler<BulkReassignPickListCommand, BulkReassignPickListResponseDto>
    {
        public async Task<BulkReassignPickListResponseDto> HandleAsync(BulkReassignPickListCommand command, CancellationToken ct = default)
        {
            long warehouseId = currentUser.WarehouseId ?? 0;
            if (warehouseId == 0)
                throw new BusinessRuleException("User is not associated with any warehouse.");

            var request = command.Dto;
            if (request == null)
                throw new BusinessRuleException("Request body cannot be null.");

            if (request.WorkerUuid == Guid.Empty)
                throw new BusinessRuleException("WorkerUuid is required for bulk worker reassignment.");

            if (request.PickListUuids == null || request.PickListUuids.Count == 0)
                throw new BusinessRuleException("At least one PickList UUID must be provided.");

            var distinctPickListUuids = request.PickListUuids.Distinct().ToList();

            var worker = await dbContext.Workers.FirstOrDefaultAsync(x => !x.IsDeleted && x.IsActive &&
                x.WorkerUuid == request.WorkerUuid && x.WarehouseId == warehouseId, ct)
                ?? throw new BusinessRuleException($"Worker with UUID {request.WorkerUuid} not found in this warehouse.");

            var response = new BulkReassignPickListResponseDto
            {
                TotalCount = distinctPickListUuids.Count
            };

            var userId = currentUser.UserId;

            logger.LogInformation("Starting bulk reassignment of {Count} pick list(s) to worker {WorkerUuid} (WorkerId: {WorkerId}) in warehouse {WarehouseId} requested by user {UserId}.",
                distinctPickListUuids.Count, request.WorkerUuid, worker.Id, warehouseId, userId);

            foreach (var pickListUuid in distinctPickListUuids)
            {
                if (pickListUuid == Guid.Empty)
                {
                    response.FailureCount++;
                    response.Results.Add(new BulkReassignItemResultDto
                    {
                        PickListUuid = pickListUuid,
                        Success = false,
                        ErrorMessage = "PickList UUID cannot be empty."
                    });
                    logger.LogWarning("Skipped invalid PickList UUID: Empty GUID provided in bulk reassignment to worker {WorkerUuid}.", request.WorkerUuid);
                    continue;
                }

                try
                {
                    var success = await PickListWorkerReassignmentHelper.ReassignPickListWorkerAsync(
                        dispatcher,
                        dbContext,
                        pickListUuid,
                        worker,
                        warehouseId,
                        userId,
                        ct);

                    if (success)
                    {
                        response.SuccessCount++;
                        response.Results.Add(new BulkReassignItemResultDto
                        {
                            PickListUuid = pickListUuid,
                            Success = true,
                            ErrorMessage = null
                        });
                        logger.LogInformation("Successfully reassigned PickList {PickListUuid} to worker {WorkerUuid} (WorkerId: {WorkerId}) in warehouse {WarehouseId}.",
                            pickListUuid, request.WorkerUuid, worker.Id, warehouseId);
                    }
                    else
                    {
                        response.FailureCount++;
                        response.Results.Add(new BulkReassignItemResultDto
                        {
                            PickListUuid = pickListUuid,
                            Success = false,
                            ErrorMessage = "Reassignment returned false."
                        });
                        logger.LogWarning("Failed to reassign PickList {PickListUuid} to worker {WorkerUuid} in warehouse {WarehouseId}: Reassignment returned false.",
                            pickListUuid, request.WorkerUuid, warehouseId);
                    }
                }
                catch (Exception ex)
                {
                    // Clear tracked changes from the failed operation to avoid poisoning subsequent iterations
                    dbContext.ChangeTracker.Clear();

                    var errorMessage = ex is BusinessRuleException || ex is NotFoundException
                        ? ex.Message
                        : (ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message);

                    response.FailureCount++;
                    response.Results.Add(new BulkReassignItemResultDto
                    {
                        PickListUuid = pickListUuid,
                        Success = false,
                        ErrorMessage = errorMessage
                    });

                    logger.LogError(ex, "Failed to reassign PickList {PickListUuid} to worker {WorkerUuid} in warehouse {WarehouseId}. Error: {ErrorMessage}. Continuing with remaining pick lists.",
                        pickListUuid, request.WorkerUuid, warehouseId, errorMessage);
                }
            }

            response.Success = response.FailureCount == 0;
            response.Message = response.FailureCount == 0
                ? $"Successfully reassigned all {response.SuccessCount} pick list(s)."
                : $"Reassigned {response.SuccessCount} of {response.TotalCount} pick list(s). {response.FailureCount} failed.";

            if (response.FailureCount == 0)
            {
                logger.LogInformation("Bulk reassignment completed successfully. Reassigned all {Count} pick list(s) to worker {WorkerUuid} in warehouse {WarehouseId}.",
                    response.TotalCount, request.WorkerUuid, warehouseId);
            }
            else
            {
                logger.LogWarning("Bulk reassignment completed with partial failures for worker {WorkerUuid} in warehouse {WarehouseId}. Succeeded: {SuccessCount}, Failed: {FailureCount}, Total: {TotalCount}.",
                    request.WorkerUuid, warehouseId, response.SuccessCount, response.FailureCount, response.TotalCount);
            }

            return response;
        }
    }
}
