using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     移除首启"启用遥测数据收集"询问弹窗（2.15.1）。
    ///     FormMain_Loaded 中原本执行：
    ///     <code>
    ///     if (Config.System.TelemetryConfig.IsDefault())
    ///     {
    ///         var selection = ModMain.MyMsgBox(...);
    ///         Config.System.TelemetryConfig.SetValue(selection == 1, forceNewValue: true);
    ///     }
    ///     </code>
    ///     此补丁把 IsDefault 调用替换为 pop; pop; ldc.i4.0（恒返回 false），
    ///     弹窗及后续的 SetValue 均不再执行，遥测保持默认关闭。
    /// </summary>
    public class RemoveTelemetryDialogPatch : IPatch
    {
        public string Name => "移除遥测询问弹窗 (FormMain_Loaded)";

        public bool Apply(ModuleDefinition module)
        {
            var method = module.GetAllTypes()
                .FirstOrDefault(t => t.Name == "FormMain")?
                .Methods.FirstOrDefault(m => m.Name == "FormMain_Loaded");

            if (method?.CilMethodBody == null) return false;

            var instructions = method.CilMethodBody.Instructions;

            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if ((instr.OpCode.Code == CilCode.Callvirt || instr.OpCode.Code == CilCode.Call) &&
                    instr.Operand is IMethodDefOrRef callee &&
                    callee.Name == "IsDefault")
                {
                    // IsDefault(this, null) 共消耗 2 个栈值并返回 bool。
                    // 替换为 pop; pop; ldc.i4.0：栈效果一致，后续 brfalse 跳过弹窗。
                    instructions.RemoveAt(i);
                    instructions.Insert(i, new CilInstruction(CilOpCodes.Pop));
                    instructions.Insert(i + 1, new CilInstruction(CilOpCodes.Pop));
                    instructions.Insert(i + 2, new CilInstruction(CilOpCodes.Ldc_I4_0));
                    return true;
                }
            }

            return false;
        }
    }
}
