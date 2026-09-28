using Microsoft.EntityFrameworkCore.Design;
namespace Feed.Core.Infrastructure;
public sealed class DesignFactory : IDesignTimeDbContextFactory<FeedDb> { public FeedDb CreateDbContext(string[] args) => new(new InstancePaths()); }
