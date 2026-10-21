using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AsmResolver;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Bundles;
using AsmResolver.IO;
using AsmResolver.PE.File;
using AsmResolver.PE.File.Headers;
using PCL_CE_Patcher.Core.Patches;
using PCL_CE_Patcher.Core.Utils;
using PCL_CE_Patcher.Core.Versions.v2_15_1;

namespace PCL_CE_Patcher.Core
{
    public class PatcherService
    {
        public void Execute(string inputExe, string outputExe, string version)
        {
            ConfigService.Log($"Starting patch process for: {Path.GetFileName(inputExe)} (Version: {version})");
            
            List<IPatch> patches = GetPatchesForVersion(version);
            if (patches == null || patches.Count == 0)
            {
                throw new Exception($"未找到版本 {version} 的补丁方案");
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "PCL_Patch_" + Guid.NewGuid().ToString().Substring(0, 8));
            Directory.CreateDirectory(tempDir);

            string preparedHostPath = Path.Combine(tempDir, "PreparedHost.exe");
            string mainDllName = "Plain Craft Launcher 2.dll";
            string dllPath = string.Empty;

            try
            {
                // 1. Load Host
                ConfigService.Log("Loading CleanHost resource...");
                var cleanPe = LoadCleanHost();
                if (cleanPe == null) throw new Exception("内部错误：无法加载 Assets/CleanHost.exe");

                // 2. Extract
                ConfigService.Log("Extracting bundle...");
                var bundle = BundleManifest.FromFile(inputExe);
                int fileCount = 0;
                foreach (var file in bundle.Files)
                {
                    string safePath = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                    string fullPath = Path.Combine(tempDir, safePath);
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    if (file.Contents != null)
                    {
                        using var fs = File.Create(fullPath);
                        var writer = new BinaryStreamWriter(fs);
                        file.Contents.Write(writer);
                        fileCount++;
                    }

                    // Optimization: Check for DLL during extraction
                    if (string.IsNullOrEmpty(dllPath) && 
                       (safePath.EndsWith("Plain Craft Launcher 2.dll", StringComparison.OrdinalIgnoreCase) ||
                        (safePath.Contains("Plain") && safePath.Contains("Craft") && safePath.EndsWith(".dll"))))
                    {
                        dllPath = fullPath;
                    }
                }
                ConfigService.Log($"Extracted {fileCount} files.");

                // 3. Patch DLL
                if (string.IsNullOrEmpty(dllPath))
                {
                     // Fallback search if not found during extraction
                     var foundDlls = Directory.GetFiles(tempDir, "Plain Craft Launcher 2.dll", SearchOption.AllDirectories);
                     if (foundDlls.Length == 0) foundDlls = Directory.GetFiles(tempDir, "*Plain*Craft*Launcher*2*.dll", SearchOption.AllDirectories);
                     if (foundDlls.Length > 0) dllPath = foundDlls[0];
                }

                if (string.IsNullOrEmpty(dllPath)) throw new FileNotFoundException($"解包数据异常，未找到核心 DLL: {mainDllName}");
                
                ConfigService.Log($"Targeted DLL: {dllPath}");
                ConfigService.Log("Applying patches...");
                
                var module = ModuleDefinition.FromFile(dllPath);
                bool anyPatched = false;

                foreach (var patch in patches)
                {
                    if (patch.Apply(module))
                    {
                        ConfigService.Log($"Applied: {patch.Name}");
                        anyPatched = true;
                    }
                    else
                    {
                        ConfigService.Log($"Skipped/Failed: {patch.Name}", "WARN");
                    }
                }

                if (anyPatched)
                {
                    module.Write(dllPath);
                    ConfigService.Log("DLL patched and saved.");
                }
                else 
                {
                     ConfigService.Log("No patches were applied! The output may be identical to input.", "WARN");
                }

                // 3.5 Patch PCL.Core.dll（配置默认值、遥测等）
                var corePatches = GetCorePatchesForVersion(version);
                if (corePatches.Count > 0)
                {
                    var coreDlls = Directory.GetFiles(tempDir, "PCL.Core.dll", SearchOption.AllDirectories);
                    if (coreDlls.Length > 0)
                    {
                        ConfigService.Log($"Targeted Core DLL: {coreDlls[0]}");
                        var coreModule = ModuleDefinition.FromFile(coreDlls[0]);
                        bool anyCorePatched = false;

                        foreach (var patch in corePatches)
                        {
                            if (patch.Apply(coreModule))
                            {
                                ConfigService.Log($"Applied: {patch.Name}");
                                anyCorePatched = true;
                            }
                            else
                            {
                                ConfigService.Log($"Skipped/Failed: {patch.Name}", "WARN");
                            }
                        }

                        if (anyCorePatched)
                        {
                            coreModule.Write(coreDlls[0]);
                            ConfigService.Log("Core DLL patched and saved.");
                        }
                    }
                    else
                    {
                        ConfigService.Log("PCL.Core.dll not found in bundle, core patches skipped.", "WARN");
                    }
                }

                // 4. Prepare Host
                ConfigService.Log("Transplanting resources & settings GUI...");
                var originalPe = PEFile.FromFile(inputExe);
                var rsrcSection = originalPe.Sections.FirstOrDefault(s => s.Name == ".rsrc");

                if (rsrcSection != null)
                {
                    byte[] rsrcBytes = ExtractSectionBytes(rsrcSection);

                    var newRsrc = new PESection(".rsrc_n", rsrcSection.Characteristics, new DataSegment(rsrcBytes));
                    cleanPe.Sections.Add(newRsrc);

                    // 必须先对齐分配好各节 RVA，再写 Resource 目录。
                    // PEFile.Write → UpdateHeaders → AlignDataDirectoryEntries 会用新增节的旧头
                    //（Add 后未对齐时 RVA=0）去判断“目录落在哪个节内”：0 ≤ 目录RVA < 0+节大小 恒成立，
                    // 于是给目录 RVA 再加上节的 RVA（如 0x29000 → 0x52000），
                    // 加载器据此解析资源会越界，Windows 报错误 193“%1 不是有效的 Win32 应用程序”，
                    // 表现为“识别不了是 exe”。先对齐后旧头与新 RVA 一致，delta 为 0，目录不再被改动。
                    cleanPe.AlignSections();

                    // 资源目录中 IMAGE_RESOURCE_DATA_ENTRY.OffsetToData 是绝对 RVA，
                    // 资源平移到新节后必须按 delta（新Rva - 原Rva）修正，否则加载器同样会读到越界地址。
                    int delta = unchecked((int)(newRsrc.Rva - rsrcSection.Rva));
                    FixResourceDirectoryRvas(rsrcBytes, delta);

                    cleanPe.OptionalHeader.DataDirectories[(int)DataDirectoryIndex.ResourceDirectory]
                        = new DataDirectory(newRsrc.Rva, newRsrc.GetVirtualSize());
                }

                cleanPe.OptionalHeader.SubSystem = SubSystem.WindowsGui;
                cleanPe.Write(preparedHostPath);

                // 5. Repack
                ConfigService.Log("Repacking bundle...");
                var newManifest = new BundleManifest(6);
                var tempFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories);

                foreach (var f in tempFiles)
                {
                    if (Path.GetFileName(f).Equals("PreparedHost.exe", StringComparison.OrdinalIgnoreCase)) continue;

                    string relPath = Path.GetRelativePath(tempDir, f);
                    byte[] data = File.ReadAllBytes(f);
                    var type = PatcherUtils.DetectFileType(relPath, f);
                    newManifest.Files.Add(new BundleFile(relPath, type, new DataSegment(data)));
                }

                var parameters = BundlerParameters.FromTemplate(preparedHostPath, mainDllName);
                newManifest.WriteUsingTemplate(outputExe, parameters);

                // 6. Post Process
                var finalPe = PEFile.FromFile(outputExe);
                bool needRewrite = false;

                if (finalPe.OptionalHeader.SubSystem != SubSystem.WindowsGui)
                {
                    finalPe.OptionalHeader.SubSystem = SubSystem.WindowsGui;
                    needRewrite = true;
                }

                // 兜底：确保 Resource 目录指向 .rsrc_n 的实际位置。
                // 若目录 RVA 与节 RVA 不一致（资源解析必然越界，Windows 报错误 193），强制修正。
                var finalRsrc = finalPe.Sections.FirstOrDefault(s => s.Name == ".rsrc_n");
                if (finalRsrc != null)
                {
                    var dataDirs = finalPe.OptionalHeader.DataDirectories;
                    var resDir = dataDirs[(int)DataDirectoryIndex.ResourceDirectory];
                    if (resDir.VirtualAddress != finalRsrc.Rva)
                    {
                        ConfigService.Log(
                            $"Resource directory mismatch: 0x{resDir.VirtualAddress:X} -> 0x{finalRsrc.Rva:X}, fixing...",
                            "WARN");
                        dataDirs[(int)DataDirectoryIndex.ResourceDirectory] =
                            new DataDirectory(finalRsrc.Rva, finalRsrc.GetVirtualSize());
                        needRewrite = true;
                    }
                }

                if (needRewrite)
                    finalPe.Write(outputExe);

                ConfigService.Log($"Success! Output: {outputExe}");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        
        private List<IPatch> GetPatchesForVersion(string version)
        {
            if (version == "2.15.1")
            {
                return new List<IPatch>
                {
                    new SkipPurchaseHintPatch(),
                    new UnlockProfilePatch(),
                    new RemoveMicrosoftHintPatch(),
                    new RemoveRegionRestrictionPatch(),
                    new RemoveTelemetryDialogPatch(),
                    new RemoveTelemetrySettingPatch(),
                    new FixInstanceResetOptiFinePatch(),
                    new FixOptiFineInstallerOutputNamePatch()
                };
            }
            // Future versions can be added here
            return new List<IPatch>();
        }

        private List<IPatch> GetCorePatchesForVersion(string version)
        {
            if (version == "2.15.1")
            {
                return new List<IPatch>
                {
                    new ChangeDefaultConfigPatch(),
                    new RemoveTelemetryPatch()
                };
            }
            return new List<IPatch>();
        }

        /// <summary>把某个 PE 节的原始数据抽取为字节数组。</summary>
        private static byte[] ExtractSectionBytes(PESection section)
        {
            using var ms = new MemoryStream();
            var writer = new BinaryStreamWriter(ms);
            section.Contents?.Write(writer);
            return ms.ToArray();
        }

        /// <summary>
        /// 修正 PE 资源目录中所有 IMAGE_RESOURCE_DATA_ENTRY.OffsetToData（绝对 RVA）。
        /// 目录条目（IMAGE_RESOURCE_DIRECTORY_ENTRY）里的偏移是相对资源根的，平移时无需改动；
        /// 只有数据项中的绝对 RVA 需要加上 delta 才能继续指向正确的资源内容。
        /// </summary>
        private static void FixResourceDirectoryRvas(byte[] data, int delta)
        {
            if (data.Length < 16 || delta == 0)
                return;

            var visited = new HashSet<int>();

            void WalkDirectory(int dirOffset)
            {
                if (dirOffset < 0 || dirOffset + 16 > data.Length || !visited.Add(dirOffset))
                    return;

                int named = BitConverter.ToUInt16(data, dirOffset + 12);
                int idCount = BitConverter.ToUInt16(data, dirOffset + 14);
                int entryCount = named + idCount;
                int entriesOffset = dirOffset + 16;

                for (int i = 0; i < entryCount; i++)
                {
                    int entryOffset = entriesOffset + i * 8;
                    if (entryOffset + 8 > data.Length)
                        return;

                    uint offsetToData = BitConverter.ToUInt32(data, entryOffset + 4);
                    bool isSubdirectory = (offsetToData & 0x80000000) != 0;
                    int target = (int)(offsetToData & 0x7FFFFFFF);

                    if (isSubdirectory)
                    {
                        WalkDirectory(target);
                    }
                    else if (target >= 0 && target + 4 <= data.Length)
                    {
                        // 数据项：OffsetToData 是绝对 RVA，需按 delta 平移
                        uint oldRva = BitConverter.ToUInt32(data, target);
                        uint newRva = unchecked((uint)((int)oldRva + delta));
                        byte[] bytes = BitConverter.GetBytes(newRva);
                        Array.Copy(bytes, 0, data, target, 4);
                    }
                }
            }

            WalkDirectory(0);
        }

        private PEFile? LoadCleanHost()
        {
            string hostPath = Path.Combine(AppContext.BaseDirectory, "Assets", "CleanHost.exe");

            if (!File.Exists(hostPath))
            {
                ConfigService.Log($"Host file not found at: {hostPath}", "ERROR");
                return null;
            }

            try 
            {
                return PEFile.FromFile(hostPath);
            }
            catch (Exception ex)
            {
                ConfigService.Log($"Failed to load CleanHost: {ex.Message}", "ERROR");
                return null;
            }
        }
    }
}
