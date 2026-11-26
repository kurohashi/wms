using System;

namespace Wms.PickList.Application.Dto
{
    public class CreateInventoryAllocationDto
    {
        public Guid PickListItemUuid { get; set; }
        public Guid BinUuid { get; set; }
        public Guid BatchUuid { get; set; }
    }
}
