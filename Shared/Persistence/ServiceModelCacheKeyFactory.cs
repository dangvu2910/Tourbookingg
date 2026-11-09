using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Tourbooking.Distributed;

public sealed class ServiceModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), (context as ApplicationDbContext)?.EventSource, (context as ApplicationDbContext)?.ServiceSchema,
            (context as ApplicationDbContext)?.UsesMongoChat, context.Database.ProviderName, designTime);
}
