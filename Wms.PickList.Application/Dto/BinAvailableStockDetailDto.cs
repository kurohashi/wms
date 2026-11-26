namespace Wms.PickList.Application.Dto;

public sealed class BinAvailableStockDetailDto
{
    public long SkuId { get; set; }
    public long BinId { get; set; }
    public long? BatchId { get; set; }
    public Guid? BatchUuid { get; set; }
    public string BinCode { get; set; } = null!;
    public Guid BinUuid { get; set; }
    public int LifeTimeStockInWarehouse { get; set; }
    public int LifeTimeSoldQty { get; set; }
    public int StockOnHand { get; set; }
    public int AvailableQtyForNewOrders { get; set; }
    public int ReservedQty { get; set; }
    public int AvailableForSale { get; set; }
}
