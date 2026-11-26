using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Microsoft.EntityFrameworkCore;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;

namespace Wms.PickList.Application.Command;

public sealed class UpdatePickListStatusCommand : ICommand<bool>
{
    public Guid PickListUuid { get; }
    public string ModifiedBy { get; }

    public UpdatePickListStatusCommand(Guid pickListUuid, string modifiedBy)
    {
        PickListUuid = pickListUuid;
        ModifiedBy = modifiedBy;
    }

    public sealed class Handler : ICommandHandler<UpdatePickListStatusCommand, bool>
    {
        private readonly WmsDbContextWrite _dbContext;

        public Handler(WmsDbContextWrite dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<bool> HandleAsync(UpdatePickListStatusCommand command, CancellationToken ct = default)
        {
            var pickList = await _dbContext.PickLists
                .FirstOrDefaultAsync(p => p.PickListUuid == command.PickListUuid, ct);

            if (pickList == null)
                return false;

            pickList.Status = nameof(PickListStatus.IN_PROGRESS);
            pickList.ModifiedBy = command.ModifiedBy;
            pickList.ModifiedDate = DateTime.UtcNow;
            _dbContext.Update(pickList);
            var result = await _dbContext.SaveChangesAsync(ct);
            return result > 0 ? true : false;
        }
    }
}