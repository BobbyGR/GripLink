using Windows.Devices.Sensors;

namespace GripLink.Input;

/// <summary>Reads the Claw's 6-axis IMU through the Windows sensor API.</summary>
internal sealed class MotionSource
{
    private readonly Gyrometer? _gyro;
    private readonly Accelerometer? _accel;
    private readonly Func<double, double, double, double>[] _gyroMap, _accelMap;

    public bool HasGyro => _gyro is not null;
    public bool HasAccel => _accel is not null;

    public MotionSource(MotionConfig cfg, Action<string> log)
    {
        _gyroMap = [Axis(cfg.GyroX), Axis(cfg.GyroY), Axis(cfg.GyroZ)];
        _accelMap = [Axis(cfg.AccelX), Axis(cfg.AccelY), Axis(cfg.AccelZ)];
        if (!cfg.Enabled) return;

        try
        {
            _gyro = Gyrometer.GetDefault();
            if (_gyro is not null) _gyro.ReportInterval = Math.Max(_gyro.MinimumReportInterval, cfg.SensorIntervalMs);
        }
        catch (Exception ex) { log($"Gyrometer unavailable: {ex.Message}"); }

        try
        {
            _accel = Accelerometer.GetDefault();
            if (_accel is not null) _accel.ReportInterval = Math.Max(_accel.MinimumReportInterval, cfg.SensorIntervalMs);
        }
        catch (Exception ex) { log($"Accelerometer unavailable: {ex.Message}"); }

        log($"Motion: gyro {(HasGyro ? "found" : "NOT found")}, accelerometer {(HasAccel ? "found" : "NOT found")}");
    }

    private static Func<double, double, double, double> Axis(string spec)
    {
        spec = spec.Trim().ToUpperInvariant();
        double sign = spec.StartsWith('-') ? -1 : 1;
        char axis = spec.TrimStart('+', '-') is { Length: 1 } a ? a[0] : throw new FormatException($"Bad axis '{spec}'");
        return axis switch
        {
            'X' => (x, _, _) => sign * x,
            'Y' => (_, y, _) => sign * y,
            'Z' => (_, _, z) => sign * z,
            _ => throw new FormatException($"Bad axis '{spec}'"),
        };
    }

    /// <summary>Angular velocity in deg/s, already in DualSense axes.</summary>
    public (double X, double Y, double Z) ReadGyro()
    {
        var r = _gyro?.GetCurrentReading();
        if (r is null) return (0, 0, 0);
        double x = r.AngularVelocityX, y = r.AngularVelocityY, z = r.AngularVelocityZ;
        return (_gyroMap[0](x, y, z), _gyroMap[1](x, y, z), _gyroMap[2](x, y, z));
    }

    /// <summary>Acceleration in g, already in DualSense axes. Defaults to "lying flat" when no sensor.</summary>
    public (double X, double Y, double Z) ReadAccel()
    {
        var r = _accel?.GetCurrentReading();
        if (r is null) return (0, 1, 0);
        double x = r.AccelerationX, y = r.AccelerationY, z = r.AccelerationZ;
        return (_accelMap[0](x, y, z), _accelMap[1](x, y, z), _accelMap[2](x, y, z));
    }
}
