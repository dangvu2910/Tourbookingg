using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;

namespace Tourbooking.Distributed;

public sealed class ConcurrencyFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not DbUpdateConcurrencyException) return;
        if (context.HttpContext.Request.GetTypedHeaders().Accept?.Any(
            header => header.MediaType.Value == "text/html") == true)
        {
            context.Result = new ViewResult
            {
                StatusCode = 409,
                ViewName = "/Views/Shared/ConcurrencyConflict.cshtml",
                ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), context.ModelState)
                {
                    ["ReloadUrl"] = context.HttpContext.Request.Path.StartsWithSegments("/Admin")
                        ? "/Admin/Bookings" : "/Account/Bookings"
                }
            };
            context.ExceptionHandled = true;
            return;
        }
        context.Result = new ConflictObjectResult(new ProblemDetails
        {
            Status = 409, Title = "Dữ liệu vừa được cập nhật bởi yêu cầu khác.",
            Detail = "Vui lòng tải lại trang trước khi thực hiện lại thao tác."
        });
        context.ExceptionHandled = true;
    }
}
