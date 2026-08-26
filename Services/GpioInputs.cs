using System.Collections.Concurrent;
using System.Device.Gpio;

namespace ScaleReaderService.Services;

/// <summary>
/// Reads the on-scale end detectors wired to the Pi's GPIO header.
///
/// Everything here is best-effort and fails open. The same build runs on a Pi,
/// on a Windows scale house PC, and on a developer laptop, and only the first
/// of those has a GPIO chip — so a missing controller, a pin another process
/// already holds, or a read that throws all resolve to "not blocked". A site
/// with no detectors wired sets no pins and never reaches the hardware at all.
/// Failing the other way would be far worse: an install with no GPIO would
/// refuse every weighment on the whole site.
/// </summary>
public sealed class GpioInputs : IDisposable
{
    private readonly ILogger<GpioInputs> _log;
    private readonly object _gate = new();

    /// <summary>Pins already opened for input, so each is opened once.</summary>
    private readonly ConcurrentDictionary<int, bool> _opened = new();

    private GpioController? _controller;

    /// <summary>
    /// Set once the platform has told us there is no GPIO here. Latched so a
    /// Windows install logs the reason a single time instead of once per read
    /// at the scale's polling rate.
    /// </summary>
    private bool _unavailable;

    public GpioInputs(ILogger<GpioInputs> log) => _log = log;

    /// <summary>
    /// Is either end detector reporting something across the beam? True means
    /// the truck is not fully on the deck. Pins left null are skipped, so this
    /// is false for any scale without detectors configured.
    /// </summary>
    public bool AnyDetectorActive(int? pin1, int? pin2, bool invert, bool pullUp)
    {
        if (pin1 == null && pin2 == null) return false;
        return IsActive(pin1, invert, pullUp) || IsActive(pin2, invert, pullUp);
    }

    private bool IsActive(int? pin, bool invert, bool pullUp)
    {
        if (pin == null || _unavailable) return false;

        try
        {
            lock (_gate)
            {
                var controller = _controller ??= new GpioController();

                if (!_opened.ContainsKey(pin.Value))
                {
                    // PullUp is what a dry contact or open-collector sensor
                    // needs to read HIGH when idle. Not every board supports
                    // every mode, so fall back to a plain input rather than
                    // losing the detector entirely.
                    var mode = pullUp ? PinMode.InputPullUp : PinMode.Input;
                    if (!controller.IsPinModeSupported(pin.Value, mode)) mode = PinMode.Input;

                    controller.OpenPin(pin.Value, mode);
                    _opened[pin.Value] = true;
                    _log.LogInformation("On-scale detector: opened GPIO {Pin} as {Mode}", pin.Value, mode);
                }

                var high = controller.Read(pin.Value) == PinValue.High;
                return invert ? !high : high;
            }
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or TypeInitializationException or DllNotFoundException)
        {
            // No GPIO on this machine at all — a Windows scale house or a dev
            // box. Latch so this is said once, and let every weighment through.
            _unavailable = true;
            _log.LogInformation("On-scale detectors are configured but this machine has no GPIO — treating every scale as occupied. ({Msg})", ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // A single bad pin (already in use, out of range) must not latch the
            // whole feature off, but it must not block weighing either.
            _log.LogWarning("On-scale detector: could not read GPIO {Pin}: {Msg}", pin, ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_controller == null) return;
            foreach (var pin in _opened.Keys)
            {
                try { _controller.ClosePin(pin); } catch { /* shutting down */ }
            }
            try { _controller.Dispose(); } catch { /* shutting down */ }
            _controller = null;
        }
    }
}
