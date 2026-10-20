using AsmResolver.DotNet.Bundles;
using AsmResolver.PE.File;

namespace PCL_CE_Patcher.Core.Utils
{
    public static class PatcherUtils
    {
        /// <summary>
        ///     根据文件内容判断 bundle 文件类型。
        ///     .dll 需要区分托管程序集（Assembly）与原生库（NativeBinary）：
        ///     原生库必须标记为 NativeBinary，host 才会将其提取到磁盘供 P/Invoke 加载；
        ///     错标为 Assembly 会导致 DllNotFoundException（如 e_sqlite3）。
        /// </summary>
        /// <param name="fileName">相对路径，仅用于按扩展名判断 json 等类型。</param>
        /// <param name="fullPath">完整路径，用于读取 PE 头判断托管/原生。</param>
        public static BundleFileType DetectFileType(string fileName, string fullPath)
        {
            string lower = fileName.ToLowerInvariant();
            if (lower.EndsWith(".deps.json")) return BundleFileType.DepsJson;
            if (lower.EndsWith(".runtimeconfig.json")) return BundleFileType.RuntimeConfigJson;
            if (lower.EndsWith(".dll"))
                return IsManagedAssembly(fullPath) ? BundleFileType.Assembly : BundleFileType.NativeBinary;
            return BundleFileType.Unknown;
        }

        /// <summary>
        ///     判断 PE 文件是否包含 CLR header（COM Descriptor，数据目录索引 14）。
        ///     有则为托管程序集，无则为原生库。
        /// </summary>
        private static bool IsManagedAssembly(string path)
        {
            try
            {
                var pe = PEFile.FromFile(path);
                var dataDirectories = pe.OptionalHeader.DataDirectories;
                const int ComDescriptorIndex = 14;
                return dataDirectories.Count > ComDescriptorIndex
                       && dataDirectories[ComDescriptorIndex].IsPresentInPE;
            }
            catch
            {
                // 非 PE 文件按原生处理
                return false;
            }
        }
    }
}
