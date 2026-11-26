namespace Wms.PickList.Application.Dto;

public sealed record SuggestedBinsResponseDto(
    long SkuId,
    IReadOnlyList<SuggestedBinDto> Bins);

public sealed record SuggestedBinDto(
    long SkuId,
    Guid BinUuid,
    long BinId,
    string BinCode,
    long BatchId,
    Guid BatchUuid,
    string BatchCode,
    bool IsLocked,
    string? ExpiryDate);
