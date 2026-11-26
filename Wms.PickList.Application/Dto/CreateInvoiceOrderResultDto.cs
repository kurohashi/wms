using System.Collections.Generic;

namespace Wms.PickList.Application.Dto;

public record CreateInvoiceOrderResultDto(
    List<long> InvoiceOrderIds,
    List<long> CNInvoiceId
);
