using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wms.PickList.Application.Common;
using Wms.PickList.Application.Services;
using Wms.Persistence.DbContexts;
using Dit.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using Wms.Contracts.Interfaces;

namespace Wms.PickList.Application.Command;

/// <summary>
/// Generates a pick list for a single order by its order id using the shared pick list pipeline.
/// </summary>
public sealed class GeneratePickListForOrderCommand(long orderId) : ICommand<GeneratePickListsResult>
{
    public long OrderId { get; } = orderId;

    public sealed class Handler(
        WmsDbContextWrite dbContext,
        ICurrentUser currentUser,
        OrderInventoryAllocationService allocationService,
        IOptions<PickListSettings> settings,
        IFcmNotificationService? fcmService = null,
        ILogger<Work.Application.Tasks.Comand.AddTaskCommand.Handler>? taskLogger = null)
        : ICommandHandler<GeneratePickListForOrderCommand, GeneratePickListsResult>
    {
        private readonly PickListGenerationService _generationService =
            new(dbContext, currentUser, allocationService, settings, fcmService, taskLogger);

        public async Task<GeneratePickListsResult> HandleAsync(GeneratePickListForOrderCommand command,CancellationToken ct = default)
        {
            if (command.OrderId <= 0)
                throw new BusinessRuleException("orderId must be greater than zero.");

            var order = await dbContext.WmsOrders
                .Include(o => o.WmsOrderItems)
                .FirstOrDefaultAsync(
                    o => o.Id == command.OrderId && o.IsPicklistCreated != true,
                    ct)
                ?? throw new BusinessRuleException(
                    $"Order '{command.OrderId}' was not found or already has a pick list.");

            var runResult = await _generationService.GenerateAsync([order], ct);
            return runResult.Result;
        }
    }
}
