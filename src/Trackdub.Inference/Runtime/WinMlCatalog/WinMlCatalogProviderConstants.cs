namespace Trackdub.Inference.Runtime.WinMlCatalog;

public static class OpenVinoCatalogProviderConstants
{
    public const string OrtExecutionProviderName = "OpenVINOExecutionProvider";

    public const string HardwareNotSupportedDetail =
        "Requires Intel Tiger Lake (11th Gen) CPU+, Alder Lake (12th Gen) GPU+, or Arrow Lake (15th Gen) NPU per Windows ML catalog.";
}

public static class QnnProviderConstants
{
    public const string OrtExecutionProviderName = "QNNExecutionProvider";

    public const string HardwareNotSupportedDetail =
        "Requires Snapdragon X Elite/Plus with Hexagon NPU driver ≥ 30.0.140.0.";
}

public static class VitisAiProviderConstants
{
    public const string OrtExecutionProviderName = "VitisAIExecutionProvider";

    public const string HardwareNotSupportedDetail =
        "Requires AMD Ryzen AI (XDNA) NPU with Adrenalin driver 25.6.3–25.9.1.";
}
