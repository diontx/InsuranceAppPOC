using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PolicyFlow.Infrastructure.Persistence;

/// <summary>
/// Supplies a <see cref="PolicyFlowDbContext"/> to the <c>dotnet ef</c> tooling.
/// </summary>
/// <remarks>
/// <para>
/// Without this, <c>dotnet ef migrations add</c> has to boot the API host to
/// obtain a context — which means it needs MongoDB, Service Bus and every other
/// dependency the host wires up, just to read the relational model.
/// </para>
/// <para>
/// The connection string is read from the environment. Migration <i>generation</i>
/// never connects to a database (the model is read from the assembly), so a
/// harmless localhost default is enough; <c>database update</c> does connect, and
/// then <c>SQL_CONNECTION_STRING</c> must be set. No credential is hard-coded.
/// </para>
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PolicyFlowDbContext>
{
    public PolicyFlowDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__PolicyFlow")
            // Placeholder for `migrations add`, which reads the model rather than
            // the database. Contains no password: any command that truly needs to
            // connect will fail loudly instead of silently reaching a wrong server.
            ?? "Server=localhost,1433;Database=PolicyFlow;Integrated Security=false;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<PolicyFlowDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsAssembly(typeof(PolicyFlowDbContext).Assembly.FullName))
            .Options;

        return new PolicyFlowDbContext(options);
    }
}
