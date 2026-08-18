using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DigiChat.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` tooling (migrations). Runtime connection strings
/// come from the API's configuration; override here with DIGICHAT_CONNSTR.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DigiChatDbContext>
{
    public const string DefaultConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;AttachDbFilename=%DBDIR%\DigiChat.mdf;Initial Catalog=DigiChat;Trusted_Connection=True;MultipleActiveResultSets=true";
    public const string DefaultMockConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;AttachDbFilename=%DBDIR%\DigiChat.Mock.mdf;Initial Catalog=DigiChat_Mock;Trusted_Connection=True;MultipleActiveResultSets=true";

    public DigiChatDbContext CreateDbContext(string[] args)
    {
        var conn = DatabaseLocation.Resolve(
            Environment.GetEnvironmentVariable("DIGICHAT_CONNSTR") ?? DefaultConnectionString);
        var options = new DbContextOptionsBuilder<DigiChatDbContext>()
            .UseSqlServer(conn)
            .Options;
        return new DigiChatDbContext(options);
    }
}
