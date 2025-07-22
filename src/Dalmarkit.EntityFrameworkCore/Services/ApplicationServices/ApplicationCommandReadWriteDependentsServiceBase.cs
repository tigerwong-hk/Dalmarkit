using Dalmarkit.Common.Api.Responses;
using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Dtos.InputDtos;
using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Entities.DataModels;
using Dalmarkit.Common.Errors;
using Dalmarkit.Common.Validation;
using Dalmarkit.EntityFrameworkCore.Services.DataServices;

namespace Dalmarkit.EntityFrameworkCore.Services.ApplicationServices;

public abstract class ApplicationCommandReadWriteDependentsServiceBase()
    : ApplicationCommandReadOnlyDependentsServiceBase()
{
    protected virtual async Task<Result<IEnumerable<Guid>, ErrorDetail>> DeleteEntityDependentsAsync<TParentEntity, TDependentEntity, TParentDataService, TDependentDataService, TDeleteDependentInputDto, TDeleteDependentsInputDto>(
        TParentDataService parentDataService,
        TDependentDataService dependentDataService,
        TDeleteDependentsInputDto inputDto,
        AuditDetail auditDetail,
        CancellationToken cancellationToken = default)
        where TParentEntity : ReadOnlyEntityBase
        where TDependentEntity : ReadWriteEntityBase, IDataModelMultiple, IDataModelPrincipalId, IDataModelSelfId
        where TParentDataService : IDataServiceBase<TParentEntity>
        where TDependentDataService : IReadWriteDataServiceBase<TDependentEntity>
        where TDeleteDependentInputDto : IDependentInputDto, new()
        where TDeleteDependentsInputDto : DeleteDependentsInputDto<TDeleteDependentInputDto>
    {
        _ = Guard.NotNull(parentDataService, nameof(parentDataService));
        _ = Guard.NotNull(dependentDataService, nameof(dependentDataService));
        _ = Guard.NotNull(inputDto, nameof(inputDto));
        _ = Guard.NotEmpty(inputDto.ParentId, nameof(inputDto.ParentId));
        _ = Guard.NotNull(inputDto.Dependents, nameof(inputDto.Dependents));
        _ = Guard.NotNull(auditDetail, nameof(auditDetail));

        TParentEntity? parent = await parentDataService.FindEntityIdAsync(inputDto.ParentId, false, cancellationToken);
        if (parent == null)
        {
            return Error<IEnumerable<Guid>>(ErrorTypes.ResourceNotFound, typeof(TParentEntity), inputDto.ParentId);
        }

        List<Guid> modifiedDependentIds = [];
        foreach (TDeleteDependentInputDto dependentDto in inputDto.Dependents!)
        {
            TDependentEntity? retrievedEntity = await dependentDataService.FindEntityIdAsync(dependentDto.DependentId, false, cancellationToken);
            if (retrievedEntity == null || retrievedEntity.PrincipalId != inputDto.ParentId)
            {
                return Error<IEnumerable<Guid>>(ErrorTypes.ResourceNotFound, typeof(TDependentEntity), dependentDto.DependentId);
            }

            retrievedEntity.IsDeleted = true;
            _ = dependentDataService.Update(retrievedEntity, auditDetail);
            modifiedDependentIds.Add(retrievedEntity.SelfId);
        }

        _ = await dependentDataService.SaveChangesAsync(cancellationToken);

        return Ok(modifiedDependentIds.AsEnumerable());
    }

    protected virtual async Task<Result<IEnumerable<Guid>, ErrorDetail>> UpdateEntityDependentsAsync<TParentEntity, TDependentEntity, TParentDataService, TDependentDataService, TUpdateDependentInputDto, TUpdateDependentsInputDto>(
        TParentDataService parentDataService,
        TDependentDataService dependentDataService,
        Action<TUpdateDependentInputDto, TDependentEntity> inputMapFunction,
        TUpdateDependentsInputDto inputDto,
        AuditDetail auditDetail,
        CancellationToken cancellationToken = default)
        where TParentEntity : ReadOnlyEntityBase
        where TDependentEntity : ReadWriteEntityBase, IDataModelMultiple, IDataModelPrincipalId, IDataModelSelfId
        where TParentDataService : IDataServiceBase<TParentEntity>
        where TDependentDataService : IReadWriteDataServiceBase<TDependentEntity>
        where TUpdateDependentInputDto : IDependentInputDto, new()
        where TUpdateDependentsInputDto : UpdateDependentsInputDto<TUpdateDependentInputDto>
    {
        _ = Guard.NotNull(parentDataService, nameof(parentDataService));
        _ = Guard.NotNull(dependentDataService, nameof(dependentDataService));
        _ = Guard.NotNull(inputMapFunction, nameof(inputMapFunction));
        _ = Guard.NotNull(inputDto, nameof(inputDto));
        _ = Guard.NotEmpty(inputDto.ParentId, nameof(inputDto.ParentId));
        _ = Guard.NotNull(inputDto.Dependents, nameof(inputDto.Dependents));
        _ = Guard.NotNull(auditDetail, nameof(auditDetail));

        TParentEntity? parent = await parentDataService.FindEntityIdAsync(inputDto.ParentId, false, cancellationToken);
        if (parent == null)
        {
            return Error<IEnumerable<Guid>>(ErrorTypes.ResourceNotFound, typeof(TParentEntity), inputDto.ParentId);
        }

        List<Guid> modifiedDependentIds = [];
        foreach (TUpdateDependentInputDto dependentDto in inputDto.Dependents!)
        {
            TDependentEntity? retrievedEntity = await dependentDataService.FindEntityIdAsync(dependentDto.DependentId, false, cancellationToken);
            if (retrievedEntity == null || retrievedEntity.PrincipalId != inputDto.ParentId)
            {
                return Error<IEnumerable<Guid>>(ErrorTypes.ResourceNotFound, typeof(TDependentEntity), dependentDto.DependentId);
            }

            inputMapFunction(dependentDto, retrievedEntity);
            _ = dependentDataService.Update(retrievedEntity, auditDetail);
            modifiedDependentIds.Add(retrievedEntity.SelfId);
        }

        _ = await dependentDataService.SaveChangesAsync(cancellationToken);

        return Ok(modifiedDependentIds.AsEnumerable());
    }
}
