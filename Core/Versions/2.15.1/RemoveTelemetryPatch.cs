using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     移除遥测上报（2.15.1，作用于 PCL.Core.dll）：
    ///     - _InitSentry：不再初始化 Sentry SDK（未初始化时 SDK 的 Capture 调用均为空操作）
    ///     - ReportException：错误日志不再上报 Sentry
    ///     - _ReportDeviceEnvironment：设备环境信息不再上报
    ///     生命周期入口 _StartAsync 为 async 方法（内含状态机），不直接清空；
    ///     其依赖的三个方法均已清空，即使遥测被手动开启也只会做一次无害的本地 STUN 测试。
    /// </summary>
    public class RemoveTelemetryPatch : IPatch
    {
        public string Name => "移除遥测上报 (TelemetryService)";

        public bool Apply(ModuleDefinition module)
        {
            var type = module.GetAllTypes()
                .FirstOrDefault(t => t.FullName == "PCL.Core.App.Essentials.TelemetryService");

            if (type == null) return false;

            bool patched = false;

            foreach (var methodName in new[] { "_InitSentry", "ReportException", "_ReportDeviceEnvironment" })
            {
                var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
                if (method?.CilMethodBody == null) continue;

                method.CilMethodBody.ExceptionHandlers.Clear();
                var instructions = method.CilMethodBody.Instructions;
                instructions.Clear();
                instructions.Add(new CilInstruction(CilOpCodes.Ret));
                patched = true;
            }

            return patched;
        }
    }
}
