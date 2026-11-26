using System;

namespace Wms.PickList.Application.Dto
{
    public class ReassignPickListRequestDto
    {
        public ReassignType Type { get; set; }
        
      
        public long? DropLocationId { get; set; }
        public Guid? WorkerUuid { get; set; }
    }

    public enum ReassignType
    {
        Worker = 1,
        Dropzone = 2,
    }
}
