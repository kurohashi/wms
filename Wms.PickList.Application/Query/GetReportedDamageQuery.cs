using Dit.Db.Extentions.Extentions.Paging;
using Dit.Shared.Cqrs.Abstractions;
using Dit.Shared.Cqrs.Handlers;
using Microsoft.EntityFrameworkCore;
using Wms.Persistence.DbContexts;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Query;

public sealed class GetReportedDamageQuery : IQuery<PagedResult<ReportedDamageDetailDto>>
{
    public long? LocationId { get; init; }
    public long? SkuId { get; init; }
    public long? BatchId { get; init; }
    public string? ReferenceType { get; init; }
    public int? Page { get; init; }
    public int? Limit { get; init; }

    public sealed class Handler(WmsDbContextWrite dbContext) : IQueryHandler<GetReportedDamageQuery, PagedResult<ReportedDamageDetailDto>>
    {
        public async Task<PagedResult<ReportedDamageDetailDto>> HandleAsync(GetReportedDamageQuery query, CancellationToken ct = default)
        {
            var rejections = dbContext.PutawayRejections
                .AsNoTracking()
                //.Where(r => r.Type == "PICKLIST-REPORT-DAMAGE")
                .Include(r => r.Location)
                .Include(r => r.Batch)
                .Include(r => r.RejectionReason)
                .AsQueryable();

            if (query.LocationId.HasValue && query.LocationId.Value > 0)
            {
                rejections = rejections.Where(r => r.LocationId == query.LocationId.Value);
            }

            if (query.SkuId.HasValue && query.SkuId.Value > 0)
            {
                rejections = rejections.Where(r => r.SkuId == query.SkuId.Value);
            }

            if (query.BatchId.HasValue && query.BatchId.Value > 0)
            {
                rejections = rejections.Where(r => r.BatchId == query.BatchId.Value);
            }

            if (!string.IsNullOrWhiteSpace(query.ReferenceType))
            {
                rejections = rejections.Where(r => r.Type == query.ReferenceType);
            }

            var projected = rejections
                .OrderByDescending(r => r.CreatedDate)
                .Select(r => new ReportedDamageDetailDto(
                    r.Id,
                    r.LocationId,
                    r.Location != null ? r.Location.Code : null,
                    r.BatchId,
                    r.Batch != null ? r.Batch.BatchNumber : null,
                    r.Quantity,
                    r.SkuId ?? 0,
                    dbContext.Products.Where(p => p.Sku == r.SkuId || p.Id == r.SkuId).Select(p => p.Name).FirstOrDefault(),
                    r.RejectionReasonId,
                    r.RejectionReason != null ? r.RejectionReason.Name : null,
                    r.Type,
                    r.Remarks,
                    r.CreatedDate
                ));

            int pageNo = query.Page ?? 1;
            int limitNo = query.Limit ?? 20;

            return await projected.GetPaged(pageNo, limitNo);
        }
    }
}
