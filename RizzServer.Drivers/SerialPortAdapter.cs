using System.IO.Ports;
using NModbus.IO;

namespace RizzServer.Drivers;

/// <summary>Bridges System.IO.Ports.SerialPort to NModbus' stream abstraction for RTU.</summary>
internal sealed class SerialPortAdapter : IStreamResource
{
    private readonly SerialPort _port;

    public SerialPortAdapter(SerialPort port) => _port = port;

    public int InfiniteTimeout => SerialPort.InfiniteTimeout;

    public int ReadTimeout
    {
        get => _port.ReadTimeout;
        set => _port.ReadTimeout = value;
    }

    public int WriteTimeout
    {
        get => _port.WriteTimeout;
        set => _port.WriteTimeout = value;
    }

    public void DiscardInBuffer() => _port.DiscardInBuffer();

    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);

    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);

    /// <summary>No-op: the <see cref="SerialPortHub"/> owns the port so other devices on the bus keep working.</summary>
    public void Dispose() { }
}
