using Dit.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Wms.Persistence.DbContexts;

namespace Wms.PickList.Application.Helper;

public static class ZoneHelper
{
    public static async Task<string> GetFinalZone(this WmsDbContextWrite dbContext, CancellationToken ct)
    {
        var zoneCode = await dbContext.Zones.AsNoTracking().Where(z => z.IsFinalZone).Select(z => z.Code).FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException("Final zone code not found.");

        return zoneCode;
    }
}
