using Dit.Db.Extentions.Extentions.Paging;
using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Contracts.Interfaces;
using Wms.Domain.Enums;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;
using Wms.Work.Application.Common.Helpers;

namespace Wms.PickList.Application.Query;

public sealed class GetWorkerPickListQuery : IQuery<PagedResult<WorkerPickListDto>>
{
    public Guid WorkerUuid { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
    private int PageNo => Page ?? 1;
    private int PageSizeNo => PageSize ?? 1;

    public sealed class Handler(WmsDbContextWrite dbContext, ICurrentUser currentUser) : IQueryHandler<GetWorkerPickListQuery, PagedResult<WorkerPickListDto>>
    {
        public async Task<PagedResult<WorkerPickListDto>> HandleAsync(GetWorkerPickListQuery query, CancellationToken ct = default)
        {
            long warehouseId = currentUser.WarehouseId ?? 0;
            if (warehouseId == 0) throw new BusinessRuleException("User is not associated with any warehouse.");

            await dbContext.ValidateWorkerShiftAndLoginAsync(query.WorkerUuid, ct: ct);

            var queryable =
                from a in dbContext.Assignments.AsNoTracking()
                join w in dbContext.Workers.AsNoTracking()
                    on a.WorkerId equals w.Id
                join t in dbContext.Tasks.AsNoTracking()
                    on a.TaskId equals t.Id
                join p in dbContext.PickLists.AsNoTracking()
                    on t.ReferenceId equals p.PickListUuid
                join b in dbContext.Bins.AsNoTracking()
                    on p.DropLocationId equals b.Id into binGroup
                from b in binGroup.DefaultIfEmpty()
                where w.WorkerUuid == query.WorkerUuid
                      && !t.IsDeleted
                      && t.IsActive
                      && t.Type == nameof(TaskTypeEnum.PICK)
                      && t.Status != nameof(TaskStatusEnum.COMPLETED)
                orderby p.Status == nameof(PickListStatus.COMPLETED) ? 0 : (p.Status == nameof(PickListStatus.IN_PROGRESS) ? 1 : 2), p.Priority
                select new WorkerPickListDto(
                    p.PickListNumber!,
                    p.PickListUuid,
                    a.AssignmentUuid,
                    t.TaskUuid,
                    p.Priority,
                    p.Status,
                    dbContext.PickListItems.Where(i => i.PickListId == p.Id && ((i.Status == nameof(PickListStatus.COMPLETED)) || (i.Status == nameof(PickListStatus.SHORT_PICK) && i.PickedQty != 0))).GroupBy(i => i.SkuId).Count(),
                    dbContext.PickListItems.Where(i => i.PickListId == p.Id).GroupBy(i => i.SkuId).Count(),
                    b.Code
                );

            var pagedResult = await queryable.GetPaged(query.PageNo, query.PageSizeNo);

            return pagedResult;
        }
    }
}