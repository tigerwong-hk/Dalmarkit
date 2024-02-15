using AutoMapper;
using Dalmarkit.Common.Api.Responses;
using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Dtos.InputDtos;
using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Errors;
using Dalmarkit.Common.Services;
using Dalmarkit.Common.Validation;
using Dalmarkit.EntityFrameworkCore.Mappers;
using Dalmarkit.EntityFrameworkCore.Services.DataServices;

namespace Dalmarkit.EntityFrameworkCore.Services.ApplicationServices;

public abstract class ApplicationUploadCommandServiceBase(IMapper mapper) : ApplicationCommandServiceBase(mapper)
{
    protected virtual async Task<Result<TOutputDto, ErrorDetail>> UploadObjectAsync<TParentDataService, TDependentDataService, TObjectStorageService, TOutputDto, TParentEntity, TDependentEntity>(
        TParentDataService parentDataService,
        TDependentDataService dependentDataService,
        TObjectStorageService objectStorageService,
        UploadObjectInputDto inputDto,
        Stream stream,
        string bucketName,
        string rootFolderName,
        string cdnCacheId,
        AuditDetail auditDetail,
        CancellationToken cancellationToken = default)
        where TParentDataService : IDataServiceBase<TParentEntity>
        where TDependentDataService : IReadWriteDataServiceBase<TDependentEntity>, IStorageObjectDataService<TDependentEntity>
        where TObjectStorageService : IObjectStorageService
        where TParentEntity : ReadWriteEntityBase
        where TDependentEntity : StorageObjectEntityBase
    {
        _ = Guard.NotNullOrWhiteSpace(inputDto.ObjectExtension, nameof(inputDto.ObjectExtension));
        _ = Guard.NotNullOrWhiteSpace(inputDto.ObjectName, nameof(inputDto.ObjectName));

        if (stream == Stream.Null)
        {
            throw new ArgumentException(ErrorMessages.StreamNull, nameof(stream));
        }

        TParentEntity? entity = await parentDataService.FindEntityIdAsync(inputDto.ParentId, false, cancellationToken);
        if (entity == null)
        {
            return Error<TOutputDto>(ErrorTypes.ResourceNotFound, typeof(TParentEntity), inputDto.ParentId);
        }

        inputDto.ObjectName = inputDto.ObjectName!.Trim();
        TDependentEntity? existingObject = await dependentDataService.GetEntityByExpressionAsync(x => x.ObjectName == inputDto.ObjectName && !x.IsDeleted, cancellationToken);
        if (existingObject != null && existingObject.PrincipalId == inputDto.ParentId)
        {
            return Error<TOutputDto>(ErrorTypes.BadRequestDetails, ErrorMessages.ObjectExists);
        }

        TDependentEntity newEntry = Mapper.Map<TDependentEntity>(inputDto);
        newEntry.PrincipalId = inputDto.ParentId;
        newEntry.SelfId = Guid.NewGuid();

        await objectStorageService.UploadObjectAsync(inputDto.ParentId, newEntry.SelfId, newEntry.ObjectExtension, stream, bucketName, rootFolderName, cdnCacheId, false, string.Empty, cancellationToken);

        _ = await dependentDataService.CreateAsync(newEntry, auditDetail, cancellationToken);
        _ = await dependentDataService.SaveChangesAsync(cancellationToken);

        TOutputDto? output = Mapper.Map<TOutputDto>(newEntry, opt =>
        {
            opt.Items[MapperItemKeys.BucketName] = bucketName;
            opt.Items[MapperItemKeys.RootFolderName] = rootFolderName;
        });

        return Ok(output);
    }
}
