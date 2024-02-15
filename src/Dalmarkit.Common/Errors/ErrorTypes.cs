namespace Dalmarkit.Common.Errors;

public class ErrorTypes : ErrorTypesBase
{
    public static ErrorDetail BadRequest => GetErrorDetail(nameof(BadRequest), ErrorMessages.BadRequest);

    public static ErrorDetail BadRequestDetails => GetErrorDetail(nameof(BadRequestDetails), ErrorMessages.BadRequestDetails);

    public static ErrorDetail ContentTypeUnsupported => GetErrorDetail(nameof(ContentTypeUnsupported), ErrorMessages.ContentTypeUnsupported);

    public static ErrorDetail DomainInvalid => GetErrorDetail(nameof(DomainInvalid), ErrorMessages.DomainInvalid);

    public static ErrorDetail FileExtensionUnsupported => GetErrorDetail(nameof(FileExtensionUnsupported), ErrorMessages.FileExtensionUnsupported);

    public static ErrorDetail FileSizeExceeded => GetErrorDetail(nameof(FileSizeExceeded), ErrorMessages.FileSizeExceeded);

    public static ErrorDetail ObjectExists => GetErrorDetail(nameof(ObjectExists), ErrorMessages.ObjectExists);

    public static ErrorDetail ObjectInvalid => GetErrorDetail(nameof(ObjectInvalid), ErrorMessages.ObjectInvalid);

    public static ErrorDetail ResourceConflict => GetErrorDetail(nameof(ResourceConflict), ErrorMessages.ResourceConflict);

    public static ErrorDetail ResourceNotFound => GetErrorDetail(nameof(ResourceNotFound), ErrorMessages.ResourceNotFound);

    public static ErrorDetail ResourceNotFoundFor => GetErrorDetail(nameof(ResourceNotFoundFor), ErrorMessages.ResourceNotFoundFor);

    public static ErrorDetail ResourceNotFoundInParent => GetErrorDetail(nameof(ResourceNotFoundInParent), ErrorMessages.ResourceNotFoundInParent);

    public static ErrorDetail ServerError => GetErrorDetail(nameof(ServerError), ErrorMessages.ServerError);

    public static ErrorDetail ServiceUnavailable => GetErrorDetail(nameof(ServiceUnavailable), ErrorMessages.ServiceUnavailable);

    public static ErrorDetail TransactionHashInvalid => GetErrorDetail(nameof(TransactionHashInvalid), ErrorMessages.TransactionHashInvalid);
}
