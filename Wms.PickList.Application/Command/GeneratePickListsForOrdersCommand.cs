using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Wms.Contracts.Interfaces;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Common;
using Wms.PickList.Application.Services;

namespace Wms.PickList.Application.Command;

/// <summary>
/// Generates pick lists for the given order ids using the shared pick list pipeline.
/// </summary>
public sealed class GeneratePickListsForOrdersCommand(
    IReadOnlyList<long> orderIds,
    bool useClusterPriority = false) : ICommand<GeneratePickListsResult>
{
    public IReadOnlyList<long> OrderIds { get; } = orderIds;
    public bool UseClusterPriority { get; } = useClusterPriority;

    public sealed class Handler(
        WmsDbContextWrite dbContext,
        ICurrentUser currentUser,
        OrderInventoryAllocationService allocationService,
        IOptions<PickListSettings> settings,
        IFcmNotificationService? fcmService = null,
        ILogger<Work.Application.Tasks.Comand.AddTaskCommand.Handler>? taskLogger = null)
        : ICommandHandler<GeneratePickListsForOrdersCommand, GeneratePickListsResult>
    {
        private readonly PickListGenerationService _generationService =
            new(dbContext, currentUser, allocationService, settings, fcmService, taskLogger);

        public async Task<GeneratePickListsResult> HandleAsync(
            GeneratePickListsForOrdersCommand command,
            CancellationToken ct = default)
        {
            if (command.OrderIds is null || command.OrderIds.Count == 0)
                throw new BusinessRuleException("OrderIds cannot be empty.");

            var distinctOrderIds = command.OrderIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (distinctOrderIds.Count == 0)
                throw new BusinessRuleException("OrderIds must contain at least one valid order id.");

            var orders = await dbContext.WmsOrders
                .Include(o => o.WmsOrderItems)
                .Where(o => distinctOrderIds.Contains(o.Id) && o.IsPicklistCreated != true)
                .OrderBy(o => o.DeliveryDateTime)
                .ToListAsync(ct);

            var options = command.UseClusterPriority
                ? new PickListGenerationOptions
                {
                    FixedPriority = PickListGenerationOptions.ClusterOrderPriority
                }
                : new PickListGenerationOptions();

            var runResult = await _generationService.GenerateAsync(orders, options, ct);
            return runResult.Result;
        }
    }
}
