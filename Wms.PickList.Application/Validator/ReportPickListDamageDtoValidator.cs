using FluentValidation;
using Wms.PickList.Application.Dto;

namespace Wms.PickList.Application.Validator;

public class ReportPickListDamageDtoValidator : AbstractValidator<ReportPickListDamageDto>
{
    public ReportPickListDamageDtoValidator()
    {
        RuleFor(x => x.InventoryAllocationId)
            .GreaterThan(0)
            .WithMessage("InventoryAllocationId is required.");

        RuleFor(x => x.Quantity)
            .GreaterThan(0)
            .WithMessage("Quantity must be greater than 0.");

        RuleFor(x => x.RejectionReasonId)
            .GreaterThan(0)
            .WithMessage("RejectionReasonId is required.");
    }
}
