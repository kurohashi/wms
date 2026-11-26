using FluentValidation;
using Wms.PickList.Application.Query;

namespace Wms.PickList.Application.Validator;

public class GetReportedDamageQueryValidator : AbstractValidator<GetReportedDamageQuery>
{
    public GetReportedDamageQueryValidator()
    {
        RuleFor(x => x.Page)
            .NotNull()
            .WithMessage("Page parameter is required.")
            .GreaterThan(0)
            .WithMessage("Page parameter must be greater than 0.");

        RuleFor(x => x.Limit)
            .NotNull()
            .WithMessage("Limit parameter is required.")
            .GreaterThan(0)
            .WithMessage("Limit parameter must be greater than 0.");
    }
}
