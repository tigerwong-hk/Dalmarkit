using Dalmarkit.Common.Api.Responses;
using Dalmarkit.Common.Dtos.InputDtos;
using Dalmarkit.Common.Errors;
using Dalmarkit.Common.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text;

namespace Dalmarkit.AspNetCore.Controllers;

public abstract class RestApiUploadControllerBase : RestApiControllerBase
{
    protected static readonly CompositeFormat ErrorFileSizeExceeded = CompositeFormat.Parse(ErrorMessages.FileSizeExceeded);
    protected static readonly CompositeFormat ErrorModelStateRequired = CompositeFormat.Parse(ErrorMessages.ModelStateErrors.FieldRequired);
    protected static readonly CompositeFormat ErrorUnsupportedContentType = CompositeFormat.Parse(ErrorMessages.ContentTypeUnsupported);
    protected static readonly CompositeFormat ErrorUnsupportedFileExtension = CompositeFormat.Parse(ErrorMessages.FileExtensionUnsupported);

    protected static bool IsContentTypeValid(string contentType, string[] supportedContentTypes)
    {
        return Array.Exists(supportedContentTypes, item => item.Equals(contentType, StringComparison.OrdinalIgnoreCase));
    }

    protected static bool IsFileExtensionValid(string fileExtension, string[] supportedFileExtensions)
    {
        return Array.Exists(supportedFileExtensions, item => item.Equals(fileExtension, StringComparison.OrdinalIgnoreCase));
    }

    protected static bool IsObjectSizeExceeded(long objectSizeBytes, long maxObjectSizeMb)
    {
        return objectSizeBytes > maxObjectSizeMb;
    }

    protected virtual (UploadObjectInputDto?, string?, ActionResult?) GetUploadObjectInputDto(string createRequestId,
        Guid parentId,
        string objectName,
        long maxObjectSizeBytes,
        string[] supportedContentTypes,
        string[] supportedFileExtensions,
        IFormFile fileContent)
    {
        if (fileContent.Length <= 0)
        {
            return (null, null, ApiResponse(Result.Error<bool, ErrorDetail>(ErrorTypes.BadRequestDetails
                .WithArgs(string.Format(CultureInfo.InvariantCulture, ErrorModelStateRequired, "File Content")))));
        }

        if (IsObjectSizeExceeded(fileContent.Length, maxObjectSizeBytes))
        {
            return (null, null, ApiResponse(Result.Error<bool, ErrorDetail>(ErrorTypes.BadRequestDetails
                .WithArgs(string.Format(CultureInfo.InvariantCulture, ErrorFileSizeExceeded, maxObjectSizeBytes, fileContent.Length)))));
        }

        if (string.IsNullOrWhiteSpace(fileContent.FileName))
        {
            return (null, null, ApiResponse(Result.Error<bool, ErrorDetail>(ErrorTypes.BadRequestDetails
                .WithArgs(string.Format(CultureInfo.InvariantCulture, ErrorModelStateRequired, "File Name")))));
        }

        string? fileExtension = Path.GetExtension(fileContent.FileName);

        (bool isValidContentType, string? fileExtensionWithoutPeriod, JsonResult? contentValidationErrors) = ValidateObjectType(fileContent.ContentType, fileExtension, supportedContentTypes, supportedFileExtensions);
        if (!isValidContentType)
        {
            return (null, null, contentValidationErrors!);
        }

        _ = Guard.NotNullOrWhiteSpace(fileExtensionWithoutPeriod, nameof(fileExtensionWithoutPeriod));

        UploadObjectInputDto uploadObjectInputDto = new()
        {
            CreateRequestId = createRequestId,
            ParentId = parentId,
            ObjectName = objectName,
            ObjectExtension = fileExtensionWithoutPeriod!,
        };

        return (uploadObjectInputDto, fileExtensionWithoutPeriod, null);
    }

    protected virtual (bool, string?, JsonResult?) ValidateObjectType(string contentType, string? fileExtension, string[] supportedContentTypes, string[] supportedFileExtensions)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return (false, null, ApiResponse(Result.Error<bool, ErrorDetail>(ErrorTypes.BadRequestDetails
                .WithArgs(string.Format(CultureInfo.InvariantCulture, ErrorUnsupportedContentType, contentType)))));
        }

        if (string.IsNullOrWhiteSpace(fileExtension))
        {
            return (false, null, ApiResponse(Result.Error<bool, ErrorDetail>(ErrorTypes.BadRequestDetails
                .WithArgs(string.Format(CultureInfo.InvariantCulture, ErrorUnsupportedFileExtension, fileExtension)))));
        }

        string fileExtensionWithoutPeriod = fileExtension[1..];
#pragma warning disable RCS1238 // Avoid nested ?: operators
        return string.IsNullOrWhiteSpace(fileExtensionWithoutPeriod)
            ? (false, null, ApiResponse(Result.Error<bool, ErrorDetail>(ErrorTypes.BadRequestDetails
                .WithArgs(string.Format(CultureInfo.InvariantCulture, ErrorUnsupportedFileExtension, fileExtension)))))
            : !IsContentTypeValid(contentType, supportedContentTypes)
            ? (false, null, ApiResponse(Result.Error<bool, ErrorDetail>(ErrorTypes.BadRequestDetails
                .WithArgs(string.Format(CultureInfo.InvariantCulture, ErrorUnsupportedContentType, contentType)))))
            : !IsFileExtensionValid(fileExtensionWithoutPeriod, supportedFileExtensions)
            ? (false, null, ApiResponse(Result.Error<bool, ErrorDetail>(ErrorTypes.BadRequestDetails
                .WithArgs(string.Format(CultureInfo.InvariantCulture, ErrorUnsupportedFileExtension, fileExtensionWithoutPeriod)))))
            : ((bool, string?, JsonResult?))(true, fileExtensionWithoutPeriod, null);
#pragma warning restore RCS1238 // Avoid nested ?: operators
    }
}
