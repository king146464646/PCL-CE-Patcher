using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     让 ProfileUi.CanCreateOtherProfile 恒返回 true，
    ///     解锁离线 / 第三方登录档案的创建（2.15.1）。
    /// </summary>
    public class UnlockProfilePatch : IPatch
    {
        public string Name => "解锁离线/第三方登录 (CanCreateOtherProfile)";

        public bool Apply(ModuleDefinition module)
        {
            var method = module.GetAllTypes()
                .SelectMany(t => t.Methods)
                .FirstOrDefault(m => m.Name == "CanCreateOtherProfile");

            if (method?.CilMethodBody == null) return false;

            // 1. 清空所有的异常处理块 (Try-Catch/Finally)
            method.CilMethodBody.ExceptionHandlers.Clear();

            // 2. 清空指令列表，改为恒返回 true
            var instructions = method.CilMethodBody.Instructions;
            instructions.Clear();
            instructions.Add(new CilInstruction(CilOpCodes.Ldc_I4_1));
            instructions.Add(new CilInstruction(CilOpCodes.Ret));

            // 3. 重置栈和局部变量
            method.CilMethodBody.MaxStack = 1;
            method.CilMethodBody.LocalVariables.Clear();

            return true;
        }
    }
}
