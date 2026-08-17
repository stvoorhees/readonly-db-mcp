using ReadOnlyDbMcp.Config;
using ReadOnlyDbMcp.Connections;
using ReadOnlyDbMcp.Tabular;

namespace ReadOnlyDbMcp.Cli;

/// <summary>
/// Human-facing setup verbs ('init', 'doctor') dispatched from Program.cs before the MCP
/// server is constructed. Nothing here is an MCP tool: when the process runs as a server,
/// these code paths are unreachable, so agents can never invoke them.
/// </summary>
public static class SetupCli
{
    /// <summary>
    /// How to invoke this process again, for help text. Running the built exe directly must
    /// echo that path, not the 'readonly-db-mcp' tool command that only exists when the
    /// package is installed as a .NET tool.
    /// </summary>
    public static string Invocation
    {
        get
        {
            // ProcessPath is the host executable: the built exe, or the .NET tool shim (which
            // is already named 'readonly-db-mcp'). GetCommandLineArgs()[0] is unsuitable — for
            // managed apps it reports the .dll path, which is not runnable as typed.
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return "readonly-db-mcp";

            var name = Path.GetFileNameWithoutExtension(exe);
            if (name.Equals("readonly-db-mcp", StringComparison.OrdinalIgnoreCase))
                return "readonly-db-mcp";
            if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var dll = Environment.GetCommandLineArgs() is { Length: > 0 } a ? a[0] : "ReadOnlyDbMcp.dll";
                return $"dotnet {(dll.Contains(' ') ? $"\"{dll}\"" : dll)}";
            }

            return exe.Contains(' ') ? $"\"{exe}\"" : exe;
        }
    }

    private const string ConfigTemplate = """
        {
          // ReadOnlyDbMcp connection config. Keep this file out of source control.
          // Caps (defaults shown):
          "maxRows": 1000,
          "defaultLimit": 100,
          "commandTimeoutSeconds": 30,
          "connections": {
            // Name entries whatever you like; expose per server instance with --connections <name>.
            "demo": {
              // provider: sqlserver | postgres | mysql | ssas
              "provider": "sqlserver",
              // Use a READ-ONLY credential. Either put the connection string here...
              "connectionString": "Server=localhost;Database=MyDb;Integrated Security=true;TrustServerCertificate=true"
              // ...or reference an environment variable instead of connectionString:
              // "connectionStringEnv": "MYDB_CONNECTION"
              // Optional (default false): offer the get_view_definition tool for this connection.
              // Needs VIEW DEFINITION (SQL Server) / SHOW VIEW (MySQL) on the credential; Postgres needs nothing extra.
              // "exposeViewDefinitions": true
            }
          }
        }
        """;

    public static int Init(string[] args)
    {
        var (explicitPath, _) = AppConfig.ParseArgs(args);
        var configPath = AppConfig.ResolveConfigPath(explicitPath);

        if (File.Exists(configPath))
        {
            Console.WriteLine($"Config already exists at '{configPath}' — leaving it untouched.");
            Console.WriteLine($"Validate it with: {Invocation} doctor");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, ConfigTemplate + Environment.NewLine);
        Console.WriteLine($"Created '{configPath}'.");
        Console.WriteLine("Next steps:");
        Console.WriteLine("  1. Edit the file: point the 'demo' connection at your database (read-only credential).");
        Console.WriteLine($"  2. Validate: {Invocation} doctor");
        Console.WriteLine("  3. Register the server in your MCP client with --connections demo (see README).");
        return 0;
    }

    public static async Task<int> DoctorAsync(string[] args, CancellationToken ct)
    {
        var (explicitPath, requested) = AppConfig.ParseArgs(args);
        var configPath = AppConfig.ResolveConfigPath(explicitPath);

        ConfigFile file;
        try
        {
            file = AppConfig.ReadConfigFile(configPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL config: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Config: '{configPath}' — {file.Connections.Count} connection(s) defined.");

        var names = requested.Count > 0 ? requested : [.. file.Connections.Keys];
        if (names.Count == 0)
        {
            Console.WriteLine("FAIL config: no connections defined. Add one and re-run.");
            return 1;
        }

        var failed = false;
        foreach (var name in names)
        {
            if (!file.Connections.TryGetValue(name, out var cc))
            {
                Console.WriteLine($"FAIL {name}: not defined in the config file.");
                failed = true;
                continue;
            }

            try
            {
                if (TabularConnectionRegistry.IsTabularProvider(cc.Provider))
                {
                    var tabular = TabularConnectionRegistry.Resolve(name, cc);
                    var model = await TabularSchemaLoader.LoadAsync(tabular.ConnectionString, file.CommandTimeoutSeconds, ct);
                    Console.WriteLine($"OK   {name} ({tabular.Provider}): connected, {model.Tables.Count} table(s), {model.Measures.Count} measure(s).");
                    continue;
                }

                var (provider, connectionString) = ConnectionRegistry.Resolve(name, cc);
                await using var connection = provider.CreateConnection(connectionString);
                await connection.OpenAsync(ct);
                await provider.ApplySessionReadOnlyAsync(connection, ct);

                var warning = await provider.CheckWritePrivilegesAsync(connection, ct);
                var schema = await provider.LoadSchemaAsync(connection, ct);
                var tables = schema.Tables.Count(t => t.Kind == "table");
                var views = schema.Tables.Count(t => t.Kind == "view");

                Console.WriteLine($"OK   {name} ({provider.Kind}): connected, {tables} table(s), {views} view(s).");
                if (warning is not null)
                    Console.WriteLine($"WARN {name}: {warning}. The server only ever constructs SELECT statements, " +
                                      "but a read-only credential is strongly recommended.");

                if (cc.ExposeViewDefinitions)
                {
                    var firstView = schema.Tables.FirstOrDefault(t => t.Kind == "view");
                    if (firstView is null)
                        Console.WriteLine($"NOTE {name}: exposeViewDefinitions is on, but there are no views to verify against.");
                    else if (await provider.GetViewDefinitionAsync(connection, firstView.Schema, firstView.Name, ct) is null)
                    {
                        var grant = provider.ViewDefinitionRequiredPrivilege is { } p
                            ? $" Grant {p} to the credential (metadata-only; does not weaken read-only)."
                            : "";
                        Console.WriteLine($"FAIL {name}: exposeViewDefinitions is on, but the credential cannot read view definitions.{grant}");
                        failed = true;
                    }
                    else
                        Console.WriteLine($"OK   {name}: view definitions readable (exposeViewDefinitions).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL {name}: {ex.Message}");
                failed = true;
            }
        }

        Console.WriteLine(failed
            ? "Doctor found problems — fix the FAIL lines above and re-run."
            : "All checks passed. Register the server in your MCP client (see README).");
        return failed ? 1 : 0;
    }
}
