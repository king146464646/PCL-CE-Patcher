using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     移除大陆 / 未知地区的功能限制（RestrictedFeaturePolicy），2.15.1。
    ///     该类基于 RegionPolicy 判定系统地区，在受限地区阻止 Minecraft 下载、安装、
    ///     整合包安装、启动缺文件补全等功能。此补丁让：
    ///     - Check(...) 恒返回 true（放行所有被拦截的功能）
    ///     - get_IsRestricted 恒返回 false（启动时正常补全缺失的游戏文件）
    ///     - ThrowIfRestricted 不再抛出 CancelledException
    /// </summary>
    public class RemoveRegionRestrictionPatch : IPatch
    {
        public string Name => "移除地区功能限制 (RestrictedFeaturePolicy)";

        public bool Apply(ModuleDefinition module)
        {
            var type = module.GetAllTypes()
                .FirstOrDefault(t => t.Name == "RestrictedFeaturePolicy");

            if (type == null) return false;

            bool patched = false;

            // Check(feature, showHint) 恒返回 true（允许操作）
            var check = type.Methods.FirstOrDefault(m => m.Name == "Check");
            if (check?.CilMethodBody != null)
            {
                RewriteToConstantReturn(check, CilOpCodes.Ldc_I4_1);
                patched = true;
            }

            // IsRestricted 属性恒返回 false（功能不受限）
            var isRestricted = type.Methods.FirstOrDefault(m => m.Name == "get_IsRestricted");
            if (isRestricted?.CilMethodBody != null)
            {
                RewriteToConstantReturn(isRestricted, CilOpCodes.Ldc_I4_0);
                patched = true;
            }

            // ThrowIfRestricted 仅保留 ret（不再抛出异常）
            var throwIfRestricted = type.Methods.FirstOrDefault(m => m.Name == "ThrowIfRestricted");
            if (throwIfRestricted?.CilMethodBody != null)
            {
                RewriteToConstantReturn(throwIfRestricted, null);
                patched = true;
            }

            return patched;
        }

        /// <summary>
        ///     清空方法体，重写为 [ldc.i4.X;] ret。
        /// </summary>
        private static void RewriteToConstantReturn(MethodDefinition method, CilOpCode? pushValue)
        {
            method.CilMethodBody.ExceptionHandlers.Clear();
            var instructions = method.CilMethodBody.Instructions;
            instructions.Clear();

            if (pushValue != null)
                instructions.Add(new CilInstruction(pushValue.Value));

            instructions.Add(new CilInstruction(CilOpCodes.Ret));
        }
    }
}
