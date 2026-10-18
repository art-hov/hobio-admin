using HobioAdmin.DbTool;

// Usage:
//   dotnet run --project src/HobioAdmin.DbTool -- views
//   dotnet run --project src/HobioAdmin.DbTool -- reader
//   dotnet run --project src/HobioAdmin.DbTool -- all
//
// Settings come from environment variables so passwords never end up in shell history:
//   HOBIO_DB_CONNECTION     admin connection string to the Tushpa database (must include Database=...)
//   HOBIO_READER_PASSWORD   password for the read-only login (reader / all only)
//   HOBIO_READER_LOGIN      optional, default hobio_admin_reader

var command = args.FirstOrDefault()?.ToLowerInvariant();
if (command is not ("views" or "reader" or "all"))
{
    Console.Error.WriteLine("Usage: HobioAdmin.DbTool <views|reader|all>");
    Console.Error.WriteLine("Env: HOBIO_DB_CONNECTION (required), HOBIO_READER_PASSWORD (reader/all), HOBIO_READER_LOGIN (optional)");
    return 2;
}

var connectionString = Environment.GetEnvironmentVariable("HOBIO_DB_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("HOBIO_DB_CONNECTION is not set.");
    return 2;
}

try
{
    if (command is "views" or "all")
    {
        Console.WriteLine("Applying analytics views...");
        await SqlScripts.ApplyViewsAsync(connectionString, Console.WriteLine);
    }

    if (command is "reader" or "all")
    {
        var password = Environment.GetEnvironmentVariable("HOBIO_READER_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("HOBIO_READER_PASSWORD is not set.");
            return 2;
        }

        var login = Environment.GetEnvironmentVariable("HOBIO_READER_LOGIN");
        Console.WriteLine("Creating/updating read-only login...");
        await SqlScripts.CreateReaderAsync(connectionString, password,
            string.IsNullOrWhiteSpace(login) ? SqlScripts.DefaultReaderLogin : login, Console.WriteLine);
    }

    Console.WriteLine("Done.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
