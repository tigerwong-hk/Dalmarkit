using Dalmarkit.Common.Api.Responses;
using Dalmarkit.Common.AuditTrail;
using Dalmarkit.Common.Dtos.InputDtos;
using Dalmarkit.Common.Entities.BaseEntities;
using Dalmarkit.Common.Errors;
using Dalmarkit.Common.Services;
using Dalmarkit.Common.Validation;
using Dalmarkit.EntityFrameworkCore.Services.DataServices;

namespace Dalmarkit.EntityFrameworkCore.Services.ApplicationServices;

public abstract class ApplicationCommandUploadServiceBase() : ApplicationServiceBase()
{
    protected virtual async Task<Result<TUploadObjectDto, ErrorDetail>> UploadObjectAsync<TParentEntity, TUploadObjectEntity, TParentDataService, TUploadObjectDataService, TObjectStorageService, TUploadObjectDto>(
        TParentDataService parentDataService,
        TUploadObjectDataService uploadObjectDataService,
        TObjectStorageService objectStorageService,
        Func<UploadObjectInputDto, TUploadObjectEntity> inputMapFunction,
        Func<TUploadObjectEntity, string, TUploadObjectDto> outputMapFunction,
        UploadObjectInputDto inputDto,
        Stream stream,
        string bucketName,
        string rootFolderName,
        AuditDetail auditDetail,
        CancellationToken cancellationToken = default)
        where TParentEntity : ReadOnlyEntityBase
        where TUploadObjectEntity : StorageObjectEntityBase, new()
        where TParentDataService : IDataServiceBase<TParentEntity>
        where TUploadObjectDataService : IReadWriteDataServiceBase<TUploadObjectEntity>, IStorageObjectDataService<TUploadObjectEntity>
        where TObjectStorageService : IObjectStorageService
        where TUploadObjectDto : class
    {
        _ = Guard.NotNull(parentDataService, nameof(parentDataService));
        _ = Guard.NotNull(uploadObjectDataService, nameof(uploadObjectDataService));
        _ = Guard.NotNull(objectStorageService, nameof(objectStorageService));
        _ = Guard.NotNull(inputMapFunction, nameof(inputMapFunction));
        _ = Guard.NotNull(outputMapFunction, nameof(outputMapFunction));
        _ = Guard.NotNull(inputDto, nameof(inputDto));
        _ = Guard.NotNullOrWhiteSpace(inputDto.CreateRequestId, nameof(inputDto.CreateRequestId));
        _ = Guard.NotEmpty(inputDto.ParentId, nameof(inputDto.ParentId));
        _ = Guard.NotNullOrWhiteSpace(inputDto.ObjectExtension, nameof(inputDto.ObjectExtension));
        _ = Guard.NotNullOrWhiteSpace(inputDto.ObjectName, nameof(inputDto.ObjectName));
        _ = Guard.NotNull(stream, nameof(stream));
        _ = Guard.NotNullOrWhiteSpace(bucketName, nameof(bucketName));
        _ = Guard.NotNullOrWhiteSpace(rootFolderName, nameof(rootFolderName));
        _ = Guard.NotNull(auditDetail, nameof(auditDetail));

        if (stream == Stream.Null)
        {
            throw new ArgumentException(ErrorMessages.StreamNull, nameof(stream));
        }

        TParentEntity? entity = await parentDataService.FindEntityIdAsync(inputDto.ParentId, false, cancellationToken);
        if (entity == null)
        {
            return Error<TUploadObjectDto>(ErrorTypes.ResourceNotFound, typeof(TParentEntity), inputDto.ParentId);
        }

        inputDto.ObjectName = inputDto.ObjectName!.Trim();
        TUploadObjectEntity? existingObject = await uploadObjectDataService.GetEntityByExpressionAsync(
            x => x.ObjectName == inputDto.ObjectName && !x.IsDeleted, cancellationToken);
        if (existingObject != null && existingObject.PrincipalId == inputDto.ParentId)
        {
            return Error<TUploadObjectDto>(ErrorTypes.BadRequestDetails, ErrorMessages.ObjectExists);
        }

        TUploadObjectEntity newEntry = inputMapFunction(inputDto);
        newEntry.PrincipalId = inputDto.ParentId;
        newEntry.SelfId = Guid.NewGuid();

        await objectStorageService.UploadObjectAsync(
            inputDto.ParentId,
            newEntry.SelfId,
            newEntry.ObjectExtension,
            stream,
            bucketName,
            rootFolderName,
            null,
            false,
            null,
            cancellationToken);

        _ = await uploadObjectDataService.CreateAsync(newEntry, auditDetail, cancellationToken);
        _ = await uploadObjectDataService.SaveChangesAsync(cancellationToken);

        TUploadObjectDto output = outputMapFunction(
            newEntry,
            ObjectStorageAccess.GetPublicStorageObjectUrl(
                bucketName, rootFolderName, inputDto.ParentId, newEntry.SelfId, newEntry.ObjectExtension));

        return Ok(output);
    }
}
