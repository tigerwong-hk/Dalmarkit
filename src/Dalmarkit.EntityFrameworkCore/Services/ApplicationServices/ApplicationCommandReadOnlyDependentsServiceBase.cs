using Dalmarkit.Common.Api.Responses;
using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Dtos.InputDtos;
using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Entities.DataModels;
using Dalmarkit.Common.Errors;
using Dalmarkit.Common.Validation;
using Dalmarkit.EntityFrameworkCore.Services.DataServices;

namespace Dalmarkit.EntityFrameworkCore.Services.ApplicationServices;

public abstract class ApplicationCommandReadOnlyDependentsServiceBase()
    : ApplicationServiceBase()
{
    protected virtual async Task<Result<IEnumerable<Guid>, ErrorDetail>> CreateEntityDependentsAsync<TParentEntity, TDependentEntity, TParentDataService, TDependentDataService, TDependentInputDto, TCreateDependentsInputDto>(
        TParentDataService parentDataService,
        TDependentDataService dependentDataService,
        Func<TDependentInputDto, TDependentEntity> inputMapFunction,
        TCreateDependentsInputDto inputDto,
        AuditDetail auditDetail,
        CancellationToken cancellationToken = default)
        where TParentEntity : ReadOnlyEntityBase
        where TDependentEntity : ReadOnlyEntityBase, IDataModelMultiple, IDataModelPrincipalId, IDataModelSelfId
        where TParentDataService : IDataServiceBase<TParentEntity>
        where TDependentDataService : IDataServiceBase<TDependentEntity>
        where TDependentInputDto : new()
        where TCreateDependentsInputDto : CreateDependentsInputDto<TDependentInputDto>
    {
        _ = Guard.NotNull(parentDataService, nameof(parentDataService));
        _ = Guard.NotNull(dependentDataService, nameof(dependentDataService));
        _ = Guard.NotNull(inputMapFunction, nameof(inputMapFunction));
        _ = Guard.NotNull(inputDto, nameof(inputDto));
        _ = Guard.NotNullOrWhiteSpace(inputDto.CreateRequestId, nameof(inputDto.CreateRequestId));
        _ = Guard.NotEmpty(inputDto.ParentId, nameof(inputDto.ParentId));
        _ = Guard.NotNull(inputDto.Dependents, nameof(inputDto.Dependents));
        _ = Guard.NotNull(auditDetail, nameof(auditDetail));

        TParentEntity? entity = await parentDataService.FindEntityIdAsync(inputDto.ParentId, false, cancellationToken);
        if (entity == null)
        {
            return Error<IEnumerable<Guid>>(ErrorTypes.ResourceNotFound, typeof(TParentEntity), inputDto.ParentId);
        }

        List<TDependentEntity> addedDependents = [];
        foreach (TDependentInputDto? dependentDto in inputDto.Dependents!)
        {
            TDependentEntity newEntry = inputMapFunction(dependentDto);
            newEntry.PrincipalId = inputDto.ParentId;
            newEntry.CreateRequestId = inputDto.CreateRequestId!;

            _ = await dependentDataService.CreateAsync(newEntry, auditDetail, cancellationToken);
            addedDependents.Add(newEntry);
        }

        _ = await dependentDataService.SaveChangesAsync(cancellationToken);

        return Ok(addedDependents.Select(x => x.SelfId));
    }
}
