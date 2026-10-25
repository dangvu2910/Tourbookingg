using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;

namespace Tourbooking.Distributed;

// A backend knows its own actions. Foreign links still use the public gateway URL.
public sealed class ServiceUrlHelperFactory : IUrlHelperFactory
{
    private readonly UrlHelperFactory factory = new();

    public IUrlHelper GetUrlHelper(ActionContext context) => new ServiceUrlHelper(factory.GetUrlHelper(context));

    private sealed class ServiceUrlHelper(IUrlHelper inner) : IUrlHelper
    {
        public ActionContext ActionContext => inner.ActionContext;

        public string? Action(UrlActionContext context)
        {
            var controller = context.Controller ?? Convert.ToString(ActionContext.RouteData.Values["controller"], CultureInfo.InvariantCulture);
            var action = context.Action ?? Convert.ToString(ActionContext.RouteData.Values["action"], CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(controller) || string.IsNullOrEmpty(action)) return inner.Action(context);
            var service = ActionContext.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Service:Name"];
            if (ServiceBoundary.Owner(controller, action) == service) return inner.Action(context);

            var values = new RouteValueDictionary(context.Values);
            var path = $"{ActionContext.HttpContext.Request.PathBase}/{Uri.EscapeDataString(controller)}/{Uri.EscapeDataString(action)}";
            if (values.Remove("id", out var id) && id != null)
                path += "/" + Uri.EscapeDataString(Convert.ToString(id, CultureInfo.InvariantCulture)!);
            var query = values.Where(v => v.Value != null && v.Key is not ("controller" or "action" or "area"))
                .Select(v => new KeyValuePair<string, string?>(v.Key, Convert.ToString(v.Value, CultureInfo.InvariantCulture)));
            path = QueryHelpers.AddQueryString(path, query);
            if (!string.IsNullOrEmpty(context.Fragment)) path += "#" + context.Fragment;
            if (context.Protocol != null || context.Host != null)
                path = $"{context.Protocol ?? ActionContext.HttpContext.Request.Scheme}://{context.Host ?? ActionContext.HttpContext.Request.Host.Value}{path}";
            return path;
        }

        public string? Content(string? contentPath) => inner.Content(contentPath);
        public bool IsLocalUrl(string? url) => inner.IsLocalUrl(url);
        public string? Link(string? routeName, object? values) => inner.Link(routeName, values);
        public string? RouteUrl(UrlRouteContext context)
        {
            var values = new RouteValueDictionary(context.Values);
            if (context.RouteName == null && values.TryGetValue("controller", out var controller)
                && values.TryGetValue("action", out var action))
                return Action(new UrlActionContext
                {
                    Controller = Convert.ToString(controller, CultureInfo.InvariantCulture),
                    Action = Convert.ToString(action, CultureInfo.InvariantCulture),
                    Values = values, Protocol = context.Protocol, Host = context.Host, Fragment = context.Fragment
                });
            return inner.RouteUrl(context);
        }
    }
}
