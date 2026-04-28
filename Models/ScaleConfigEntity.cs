using System.ComponentModel.DataAnnotations;

namespace ScaleReaderService.Models;

/// <summary>
/// A configured scale stored in the local SQLite database.
/// </summary>
public class ScaleConfigEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>Unique string identifier for this scale (e.g. "truck-scale-1")</summary>
    [Required]
    [StringLength(50)]
    public string ScaleId { get; set; } = "";

    /// <summary>Human-friendly name (e.g. "Truck Scale 2")</summary>
    [StringLength(100)]
    public string DisplayName { get; set; } = "";

    /// <summary>Scale brand/protocol from scale-models.json (e.g. "Generic SMA", "Mettler Toledo")</summary>
    [StringLength(50)]
    public string ScaleBrand { get; set; } = "Generic SMA";

    /// <summary>Connection type: "TCP" or "Serial"</summary>
    [StringLength(10)]
    public string ConnectionType { get; set; } = "TCP";

    /// <summary>Wire-level protocol/format used to parse readings: "SMA" or "IQ355"</summary>
    [StringLength(20)]
    public string Protocol { get; set; } = "SMA";

    // ---- TCP fields ----

    /// <summary>IP address of the scale indicator (TCP)</summary>
    [StringLength(100)]
    public string IpAddress { get; set; } = "127.0.0.1";

    /// <summary>TCP port for the scale connection</summary>
    public int Port { get; set; } = 10001;

    // ---- Serial fields ----

    /// <summary>Serial port name (e.g. "COM1", "/dev/ttyUSB0"). Required when ConnectionType="Serial".</summary>
    [StringLength(50)]
    public string? SerialPortName { get; set; }

    /// <summary>Serial baud rate (default 9600)</summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>Serial data bits (default 8)</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>Serial parity: "None", "Even", "Odd", "Mark", "Space" (default "None")</summary>
    [StringLength(10)]
    public string Parity { get; set; } = "None";

    /// <summary>Serial stop bits: 1, 2, or 0 for OnePointFive (default 1)</summary>
    public int StopBits { get; set; } = 1;

    // ---- Common ----

    /// <summary>Custom request command override (e.g. "W\r\n"). If empty, uses brand default. Ignored for streaming protocols.</summary>
    [StringLength(100)]
    public string? RequestCommand { get; set; }

    /// <summary>Polling interval in milliseconds (TCP). For streaming serial scales this is the broadcast throttle.</summary>
    public int PollingIntervalMs { get; set; } = 750;

    /// <summary>Socket / serial read timeout in milliseconds</summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>Whether this scale is active</summary>
    public bool Active { get; set; } = true;
}
