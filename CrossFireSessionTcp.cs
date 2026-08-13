namespace CrossFireRouteLab;

/// <summary>
/// Compatibility shim for older source references.
/// The old room observer is retired; all CrossFire TCP measurement now lives
/// in CrossFireTcpRoomMonitor so there is only one active implementation.
/// </summary>
internal static class CrossFireSessionTcp
{
    public static void Apply(GameRouteLabV10Form form) => CrossFireTcpRoomMonitor.Apply(form);
}
