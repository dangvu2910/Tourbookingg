using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Tourbooking.Distributed;

public sealed class ConcurrencyFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not DbUpdateConcurrencyException) return;
        context.Result = new ConflictObjectResult(new ProblemDetails
        {
            Status = 409, Title = "Dữ liệu vừa được cập nhật bởi yêu cầu khác.",
            Detail = "Vui lòng tải lại trang trước khi thực hiện lại thao tác."
        });
        context.ExceptionHandled = true;
    }
}
