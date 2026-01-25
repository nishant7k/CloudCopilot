using System.Reflection;

namespace CloudCopilot.Services;

public sealed class CopilotClientManager : IAsyncDisposable
{
    private readonly ConnectionStatus _status;
    private readonly ILogger<CopilotClientManager> _logger;
    private object? _client;
    private Type? _clientType;

    public CopilotClientManager(ConnectionStatus status, ILogger<CopilotClientManager> logger)
    {
        _status = status;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Environment.SetEnvironmentVariable("COPILOT_SDK_SERVER_MODE", "cli");
            Environment.SetEnvironmentVariable("COPILOT_SERVER_MODE", "cli");

            var assembly = LoadCopilotAssembly();
            _clientType = assembly.GetType("GitHub.Copilot.SDK.CopilotClient");

            if (_clientType is null)
            {
                throw new InvalidOperationException("GitHub.Copilot.SDK CopilotClient type not found.");
            }

            var options = CreateClientOptions(assembly);
            _client = CreateClientInstance(_clientType, options);

            await InvokeStartAsync(_client, cancellationToken);
            _status.CopilotConnected = true;
            _status.CopilotError = null;
        }
        catch (Exception ex)
        {
            _status.CopilotConnected = false;
            _status.CopilotError = ex.Message;
            _logger.LogError(ex, "Copilot SDK failed to start");
        }
    }

    public async Task<object> CreateSessionAsync(CancellationToken cancellationToken)
    {
        if (_client is null || _clientType is null)
        {
            throw new InvalidOperationException("Copilot SDK is not started.");
        }

        var assembly = _clientType.Assembly;
        var config = CreateSessionConfig(assembly);

        var method = _clientType.GetMethods()
            .FirstOrDefault(m => m.Name == "CreateSessionAsync" && m.GetParameters().Length == (config is null ? 0 : 1))
            ?? _clientType.GetMethods().FirstOrDefault(m => m.Name == "CreateSessionAsync");

        if (method is null)
        {
            throw new InvalidOperationException("CreateSessionAsync method not found on CopilotClient.");
        }

        var parameters = method.GetParameters().Length switch
        {
            0 => Array.Empty<object?>(),
            1 => new[] { config },
            _ => new[] { config, cancellationToken }
        };

        var result = method.Invoke(_client, parameters);
        if (result is Task sessionTask)
        {
            await sessionTask;
            var resultProperty = sessionTask.GetType().GetProperty("Result");
            if (resultProperty is null)
            {
                throw new InvalidOperationException("CreateSessionAsync did not return a session.");
            }
            return resultProperty.GetValue(sessionTask) ?? throw new InvalidOperationException("CreateSessionAsync returned null.");
        }

        if (result is not null)
        {
            return result;
        }

        throw new InvalidOperationException("CreateSessionAsync did not return a session.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is null || _clientType is null)
        {
            return;
        }

        var stopMethod = _clientType.GetMethod("StopAsync") ?? _clientType.GetMethod("Stop");
        if (stopMethod is not null)
        {
            var result = stopMethod.Invoke(_client, Array.Empty<object?>());
            if (result is Task stopTask)
            {
                await stopTask;
            }
        }
    }

    private static Assembly LoadCopilotAssembly()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "GitHub.Copilot.SDK", StringComparison.OrdinalIgnoreCase));

        return assembly ?? Assembly.Load("GitHub.Copilot.SDK");
    }

    private object? CreateClientOptions(Assembly assembly)
    {
        var optionsType = assembly.GetType("GitHub.Copilot.SDK.CopilotClientOptions");
        if (optionsType is null)
        {
            return null;
        }

        var options = Activator.CreateInstance(optionsType);
        if (options is null)
        {
            return null;
        }

        SetEnumProperty(options, "ServerMode", "Cli");
        SetProperty(options, "UseCliServer", true);
        SetProperty(options, "CliServer", true);
        SetProperty(options, "AllowTools", FriendlyToolNames.All);
        SetProperty(options, "ToolAllowList", FriendlyToolNames.All);
        SetProperty(options, "EnableDefaultTools", false);
        SetProperty(options, "DisableDefaultTools", true);
        return options;
    }

    private static object CreateClientInstance(Type clientType, object? options)
    {
        if (options is null)
        {
            return Activator.CreateInstance(clientType) ?? throw new InvalidOperationException("Failed to create CopilotClient.");
        }

        var ctor = clientType.GetConstructors().FirstOrDefault(c =>
        {
            var parameters = c.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(options);
        });

        return ctor is not null
            ? ctor.Invoke(new[] { options })
            : Activator.CreateInstance(clientType) ?? throw new InvalidOperationException("Failed to create CopilotClient.");
    }

    private static async Task InvokeStartAsync(object client, CancellationToken cancellationToken)
    {
        var type = client.GetType();
        var method = type.GetMethods().FirstOrDefault(m => m.Name == "StartAsync" && m.GetParameters().Length == 1)
            ?? type.GetMethod("StartAsync")
            ?? type.GetMethod("Start");

        if (method is null)
        {
            throw new InvalidOperationException("CopilotClient StartAsync method not found.");
        }

        var parameters = method.GetParameters().Length switch
        {
            0 => Array.Empty<object?>(),
            1 => new object?[] { cancellationToken },
            _ => new object?[] { cancellationToken }
        };

        var result = method.Invoke(client, parameters);
        if (result is Task task)
        {
            await task;
        }
    }

    private static object? CreateSessionConfig(Assembly assembly)
    {
        var configType = assembly.GetType("GitHub.Copilot.SDK.SessionConfig");
        if (configType is null)
        {
            return null;
        }

        var config = Activator.CreateInstance(configType);
        if (config is null)
        {
            return null;
        }

        SetProperty(config, "EnableDefaultTools", false);
        SetProperty(config, "DisableDefaultTools", true);
        SetProperty(config, "Tools", Array.Empty<object>());
        SetProperty(config, "ToolAllowList", FriendlyToolNames.All);

        return config;
    }

    private static void SetProperty(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName);
        if (property is null || !property.CanWrite)
        {
            return;
        }

        if (value is null)
        {
            property.SetValue(target, null);
            return;
        }

        if (property.PropertyType.IsInstanceOfType(value))
        {
            property.SetValue(target, value);
            return;
        }

        try
        {
            var converted = Convert.ChangeType(value, property.PropertyType);
            property.SetValue(target, converted);
        }
        catch
        {
        }
    }

    private static void SetEnumProperty(object target, string propertyName, string enumValue)
    {
        var property = target.GetType().GetProperty(propertyName);
        if (property is null || !property.CanWrite || !property.PropertyType.IsEnum)
        {
            return;
        }

        try
        {
            var parsed = Enum.Parse(property.PropertyType, enumValue, ignoreCase: true);
            property.SetValue(target, parsed);
        }
        catch
        {
        }
    }

    private static class FriendlyToolNames
    {
        public static readonly string[] All =
        {
            "list_providers",
            "list_families",
            "search_instances",
            "get_pricing",
            "compare_instances"
        };
    }
}
