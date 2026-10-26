using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NxInfinity.Runtime;

public enum GraphicsDeviceAvailability { Available, Partial, Unsupported, Invalid }

/// <summary>Observed renderer identity. Marketing names never manufacture numeric device IDs.</summary>
public sealed record GraphicsDeviceSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public string Source { get; init; } = "";
    public string? Api { get; init; }
    public uint? VendorId { get; init; }
    public uint? DeviceId { get; init; }
    public string? VendorName { get; init; }
    public string? DeviceName { get; init; }
    public uint? DriverId { get; init; }
    public string? DriverName { get; init; }
    public string? DriverInfo { get; init; }
    public uint? DriverVersionRaw { get; init; }
    public uint? ApiVersionRaw { get; init; }
    public string? DriverVersion { get; init; }
    public string? ApiVersion { get; init; }
    public string? RendererVersionLabel { get; init; }
    /// <summary>Native device-local heap capacity, not a residency measurement, available budget or inferred retail VRAM size.</summary>
    public ulong? DeviceLocalHeapBytes { get; init; }
    /// <summary>Whether the native renderer identifies shared device memory; null retains an unknown classification.</summary>
    public bool? DeviceMemoryShared { get; init; }
    public int OmittedDisplayLabels { get; init; }
    public string? VendorIdHex => VendorId?.ToString("x8", CultureInfo.InvariantCulture);
    public string? DeviceIdHex => DeviceId?.ToString("x8", CultureInfo.InvariantCulture);
    public bool SelectedDeviceVerified => Source == "native_vulkan_selected_physical_device" &&
        Api == "vulkan" && VendorId is > 0 && DeviceId is not null && DriverVersionRaw is not null && ApiVersionRaw is not null;
    /// <summary>Opaque exact native version binding. It is not a chronological driver version.</summary>
    public string? DriverIdentityKey => !SelectedDeviceVerified || OmittedDisplayLabels != 0 ? null :
        "vulkan:" + VendorIdHex + ":" + DeviceIdHex + ":" +
        (DriverId?.ToString("x8", CultureInfo.InvariantCulture) ?? "unknown") + ":" +
        DriverVersionRaw!.Value.ToString("x8", CultureInfo.InvariantCulture) + ":" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((DriverName ?? "") + "\n" + (DriverInfo ?? ""))));
    public bool RenderingCorrectnessVerified => false;
}

public sealed record GraphicsDeviceResult(GraphicsDeviceAvailability Availability, string Code, string Message,
    GraphicsDeviceSnapshot? Snapshot = null);

/// <summary>Bounded allowlist for diagnostic exports. No filesystem or network access.</summary>
public static class GraphicsDeviceTelemetry
{
    public static GraphicsDeviceResult Read(TelemetrySnapshot telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        try
        {
            var metrics = telemetry.BackendMetrics;
            if (metrics is null) return Unavailable();
            int omitted = 0;
            if (metrics.TryGetValue("graphicsDevice", out var data) && data.ValueKind != JsonValueKind.Null)
            {
                if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != data.EnumerateObject().Count() ||
                    UInt(data, "schemaVersion") != 1 || Text(data, "source", ref omitted) != "native_vulkan_selected_physical_device" ||
                    Text(data, "api", ref omitted) != "vulkan") return Invalid();
                var snapshot = new GraphicsDeviceSnapshot
                {
                    Source = "native_vulkan_selected_physical_device", Api = "vulkan",
                    VendorId = UInt(data, "vendorId"), DeviceId = UInt(data, "deviceId"), DriverId = UInt(data, "driverId"),
                    DeviceName = Text(data, "deviceName", ref omitted), VendorName = Text(data, "vendorName", ref omitted),
                    DriverName = Text(data, "driverName", ref omitted), DriverInfo = Text(data, "driverInfo", ref omitted),
                    DriverVersionRaw = UInt(data, "driverVersionRaw"), ApiVersionRaw = UInt(data, "apiVersionRaw"),
                    DriverVersion = Text(data, "driverVersion", ref omitted), ApiVersion = Text(data, "apiVersion", ref omitted),
                    DeviceLocalHeapBytes = HeapBytes(data), DeviceMemoryShared = Boolean(data, "deviceMemoryShared"),
                    OmittedDisplayLabels = omitted
                };
                return snapshot.SelectedDeviceVerified
                    ? new(GraphicsDeviceAvailability.Available, "selected_native_device", "Identity read from the renderer's selected physical device; game correctness remains unverified.", snapshot)
                    : new(GraphicsDeviceAvailability.Partial, "selected_device_identity_incomplete", "The renderer supplied an incomplete device record; exact hardware binding is unavailable.", snapshot);
            }
            // Older/current managed adapters supply native labels but no public numeric device IDs.
            // Preserve that evidence without assigning PCI IDs from a device or product name.
            string? Label(string name) => metrics.TryGetValue(name, out var value) ? LabelValue(value, ref omitted) : null;
            string? vendor = Label("gpuVendor"), model = Label("gpuModel"), driver = Label("gpuDriver"), version = Label("gpuVersion");
            if (vendor is null && model is null && driver is null && version is null) return Unavailable();
            string? api = null;
            if (metrics.TryGetValue("performance", out var performance) && performance.ValueKind == JsonValueKind.Object)
            {
                string? nativeApi = Text(performance, "api", ref omitted);
                if (nativeApi is "vulkan" or "opengl") api = nativeApi;
            }
            return new(GraphicsDeviceAvailability.Partial, "native_renderer_labels_only",
                "Native renderer labels are available. Numeric active-device IDs remain unknown and cannot qualify an exact hardware profile.",
                new() { Source = "native_renderer_hardware_info", Api = api, VendorName = vendor, DeviceName = model,
                    DriverName = driver, RendererVersionLabel = version, OmittedDisplayLabels = omitted });
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        { return Invalid(); }
    }

    private static GraphicsDeviceResult Unavailable() => new(GraphicsDeviceAvailability.Unsupported,
        "graphics_device_unavailable", "This core has not supplied an active graphics-device observation.");
    private static GraphicsDeviceResult Invalid() => new(GraphicsDeviceAvailability.Invalid,
        "graphics_device_invalid", "The core supplied an invalid or unsupported graphics-device record.");
    private static uint? UInt(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out uint number)) throw new FormatException();
        return number;
    }
    private static string? Text(JsonElement data, string name, ref int omitted) =>
        data.TryGetProperty(name, out var value) ? LabelValue(value, ref omitted) : null;
    private static ulong? HeapBytes(JsonElement data)
    {
        if (!data.TryGetProperty("deviceLocalHeapBytes", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out ulong bytes) || bytes > (1UL << 50)) throw new FormatException();
        return bytes;
    }
    private static bool? Boolean(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException();
        return value.GetBoolean();
    }
    private static string? LabelValue(JsonElement value, ref int omitted)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FormatException();
        string text = value.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(text)) return null;
        // These are display labels, never arbitrary native logs, paths or opaque blobs.
        if (text.Length > 256 || text.Any(c => !(char.IsLetterOrDigit(c) || " .,_()+-@#[]{}=".Contains(c))))
        { omitted++; return null; }
        return text;
    }
}
