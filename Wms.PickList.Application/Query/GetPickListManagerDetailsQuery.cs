using System;
using System.Threading;
using System.Threading.Tasks;
using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Wms.Contracts.Interfaces;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;
using Wms.PickList.Application.Services;

namespace Wms.PickList.Application.Query;

public sealed class GetPickListManagerDetailsQuery(Guid pickListUuid) : IQuery<PickListDetailsDto>
{
    public Guid PickListUuid { get; } = pickListUuid;

    public sealed class Handler(WmsDbContextWrite dbContext, ICurrentUser currentUser) : IQueryHandler<GetPickListManagerDetailsQuery, PickListDetailsDto>
    {
        public Task<PickListDetailsDto> HandleAsync(GetPickListManagerDetailsQuery query, CancellationToken ct = default)
        {
            return PickListDetailsCommon.GetPickListDetailsAsync(dbContext, currentUser, query.PickListUuid, ct);
        }
    }
}