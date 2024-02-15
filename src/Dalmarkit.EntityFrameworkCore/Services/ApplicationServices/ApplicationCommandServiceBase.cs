using AutoMapper;
using Dalmarkit.Common.Api.Responses;
using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Dtos.InputDtos;
using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Errors;
using Dalmarkit.Common.Validation;
using Dalmarkit.EntityFrameworkCore.Services.DataServices;

namespace Dalmarkit.EntityFrameworkCore.Services.ApplicationServices;

public abstract class ApplicationCommandServiceBase(IMapper mapper) : ApplicationServiceBase(mapper)
{
    protected virtual async Task<Result<IEnumerable<Guid>, ErrorDetail>> CreateEntityDependentsAsync<TParentDataService, TDependentDataService, TCreateDependentsInputDto, TInputDto, TParentEntity, TDependentEntity>(
        TParentDataService parentDataService,
        TDependentDataService dependentDataService,
        TCreateDependentsInputDto inputDto,
        AuditDetail auditDetail,
        CancellationToken cancellationToken = default)
        where TParentDataService : IDataServiceBase<TParentEntity>
        where TDependentDataService : IReadWriteDataServiceBase<TDependentEntity>
        where TCreateDependentsInputDto : CreateDependentsInputDto<TInputDto>
        where TInputDto : new()
        where TParentEntity : ReadWriteEntityBase
        where TDependentEntity : DependentEntityBase
    {
        _ = Guard.NotNullOrWhiteSpace(inputDto.CreateRequestId, nameof(inputDto.CreateRequestId));

        TParentEntity? entity = await parentDataService.FindEntityIdAsync(inputDto.ParentId, false, cancellationToken);
        if (entity == null)
        {
            return Error<IEnumerable<Guid>>(ErrorTypes.ResourceNotFound, typeof(TParentEntity), inputDto.ParentId);
        }

        List<TDependentEntity> addedDependents = [];
        foreach (TInputDto? dependentDto in inputDto.Dependents!)
        {
            TDependentEntity newEntry = Mapper.Map<TDependentEntity>(dependentDto);
            newEntry.PrincipalId = inputDto.ParentId;
            newEntry.CreateRequestId = inputDto.CreateRequestId!;

            _ = await dependentDataService.CreateAsync(newEntry, auditDetail, cancellationToken);
            addedDependents.Add(newEntry);
        }

        _ = await dependentDataService.SaveChangesAsync(cancellationToken);

        return Ok(addedDependents.Select(x => x.SelfId));
    }

    protected virtual async Task<Result<IEnumerable<Guid>, ErrorDetail>> DeleteEntityDependentsAsync<TParentDataService, TDependentDataService, TDeleteDependentsInputDto, TInputDto, TParentEntity, TDependentEntity>(
        TParentDataService parentDataService,
        TDependentDataService dependentDataService,
        TDeleteDependentsInputDto inputDto,
        AuditDetail auditDetail,
        CancellationToken cancellationToken = default)
        where TParentDataService : IDataServiceBase<TParentEntity>
        where TDependentDataService : IReadWriteDataServiceBase<TDependentEntity>
        where TDeleteDependentsInputDto : DeleteDependentsInputDto<TInputDto>
        where TInputDto : IDependentInputDto, new()
        where TParentEntity : ReadWriteEntityBase
        where TDependentEntity : DependentEntityBase
    {
        TParentEntity? parent = await parentDataService.FindEntityIdAsync(inputDto.ParentId, false, cancellationToken);
        if (parent == null)
        {
            return Error<IEnumerable<Guid>>(ErrorTypes.ResourceNotFound, typeof(TParentEntity), inputDto.ParentId);
        }

        List<Guid> modifiedDependentIds = [];
        foreach (TInputDto dependentDto in inputDto.Dependents!)
        {
            TDependentEntity? retrievedEntry = await dependentDataService.FindEntityIdAsync(dependentDto.DependentId, false, cancellationToken);
            if (retrievedEntry == null || retrievedEntry.PrincipalId != inputDto.ParentId)
            {
                return Error<IEnumerable<Guid>>(ErrorTypes.ResourceNotFound, typeof(TDependentEntity), dependentDto.DependentId);
            }

            retrievedEntry.IsDeleted = true;
            _ = dependentDataService.Update(retrievedEntry, auditDetail);
            modifiedDependentIds.Add(retrievedEntry.SelfId);
        }

        _ = await dependentDataService.SaveChangesAsync(cancellationToken);

        return Ok(modifiedDependentIds.AsEnumerable());
    }

    protected virtual async Task<Result<IEnumerable<Guid>, ErrorDetail>> UpdateEntityDependentsAsync<TParentDataService, TDependentDataService, TUpdateDependentsInputDto, TInputDto, TParentEntity, TDependentEntity>(
        TParentDataService parentDataService,
        TDependentDataService dependentDataService,
        TUpdateDependentsInputDto inputDto,
        AuditDetail auditDetail,
        CancellationToken cancellationToken = default)
        where TParentDataService : IDataServiceBase<TParentEntity>
        where TDependentDataService : IReadWriteDataServiceBase<TDependentEntity>
        where TUpdateDependentsInputDto : UpdateDependentsInputDto<TInputDto>
        where TInputDto : IDependentInputDto, new()
        where TParentEntity : ReadWriteEntityBase
        where TDependentEntity : DependentEntityBase
    {
        TParentEntity? parent = await parentDataService.FindEntityIdAsync(inputDto.ParentId, false, cancellationToken);
        if (parent == null)
        {
            return Error<IEnumerable<Guid>>(ErrorTypes.ResourceNotFound, typeof(TParentEntity), inputDto.ParentId);
        }

        List<Guid> modifiedDependentIds = [];
        foreach (TInputDto dependentDto in inputDto.Dependents!)
        {
            TDependentEntity? retrievedEntry = await dependentDataService.FindEntityIdAsync(dependentDto.DependentId, false, cancellationToken);
            if (retrievedEntry == null || retrievedEntry.PrincipalId != inputDto.ParentId)
            {
                return Error<IEnumerable<Guid>>(ErrorTypes.ResourceNotFound, typeof(TDependentEntity), dependentDto.DependentId);
            }

            retrievedEntry = Mapper.Map(dependentDto, retrievedEntry);
            _ = dependentDataService.Update(retrievedEntry, auditDetail);
            modifiedDependentIds.Add(retrievedEntry.SelfId);
        }

        _ = await dependentDataService.SaveChangesAsync(cancellationToken);

        return Ok(modifiedDependentIds.AsEnumerable());
    }
}
