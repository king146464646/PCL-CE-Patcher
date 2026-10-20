using AsmResolver.DotNet.Bundles;

namespace PCL_CE_Patcher.Core.Utils
{
    public static class PatcherUtils
    {
        public static BundleFileType DetectFileType(string fileName)
        {
            string lower = fileName.ToLowerInvariant();
            if (lower.EndsWith(".deps.json")) return BundleFileType.DepsJson;
            if (lower.EndsWith(".runtimeconfig.json")) return BundleFileType.RuntimeConfigJson;
            if (lower.EndsWith(".dll")) return BundleFileType.Assembly;
            return BundleFileType.Unknown;
        }
    }
}