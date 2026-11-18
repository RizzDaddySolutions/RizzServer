using Microsoft.Extensions.Logging;
using RizzServer.Core;

namespace RizzServer.Drivers;

public sealed class DriverFactory : IDriverFactory
{
    private readonly ILoggerFactory _loggers;

    public DriverFactory(ILoggerFactory loggers) => _loggers = loggers;

    public IDriver Create(ConnectionConfig config)
    {
        var log = _loggers.CreateLogger($"Driver.{config.Name}");
        return config.DriverType switch
        {
            DriverType.ModbusTcp => new ModbusDriver(config, log, rtu: false),
            DriverType.ModbusRtu => new ModbusDriver(config, log, rtu: true),
            DriverType.Serial => new SerialDriver(config, log),
            DriverType.Rs232Poll => new Rs232PollDriver(config, log),
            DriverType.Mqtt => new MqttDriver(config, log),
            DriverType.OpcDa => new OpcDaDriver(config, log),
            _ => throw new NotSupportedException(config.DriverType.ToString()),
        };
    }
}
