using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     跳过 McLaunchPrecheck 末尾的"正版购买提示"弹窗（2.15.1）：
    ///     原代码为 if (!ProfileService.HasMicrosoftProfile) { 弹"购买正版 / 试用模式 / 返回"提示 }，
    ///     纯离线用户每次启动游戏都会被弹一次。
    ///     此补丁把 "call get_HasMicrosoftProfile; br..." 两条指令替换为 "nop; br.s 原目标"，
    ///     无条件跳过整个提示块；方法内其余检查（路径合法性、实例状态、验证要求等）保持原样。
    /// </summary>
    public class SkipPurchaseHintPatch : IPatch
    {
        public string Name => "跳过正版购买提示 (McLaunchPrecheck)";

        public bool Apply(ModuleDefinition module)
        {
            var method = module.GetAllTypes()
                .SelectMany(t => t.Methods)
                .FirstOrDefault(m => m.Name == "McLaunchPrecheck");

            if (method?.CilMethodBody == null) return false;

            var instructions = method.CilMethodBody.Instructions;

            for (int i = 0; i < instructions.Count - 1; i++)
            {
                var instr = instructions[i];
                var next = instructions[i + 1];

                if ((instr.OpCode.Code == CilCode.Callvirt || instr.OpCode.Code == CilCode.Call) &&
                    instr.Operand is IMethodDefOrRef callee &&
                    callee.Name == "get_HasMicrosoftProfile" &&
                    IsBranch(next.OpCode.Code) &&
                    next.Operand is ICilLabel target)
                {
                    // get_HasMicrosoftProfile 是静态属性：call 压入 bool 结果，随后的分支按条件跳过提示块。
                    // 替换为 nop; br.s 原目标：不压栈、无条件跳过，
                    // 且不依赖原分支方向（brtrue / brfalse 均可正确处理）。
                    instructions[i] = new CilInstruction(CilOpCodes.Nop);
                    instructions[i + 1] = new CilInstruction(CilOpCodes.Br_S, target);
                    return true;
                }
            }

            return false;
        }

        private static bool IsBranch(CilCode code)
            => code is CilCode.Br or CilCode.Br_S
                or CilCode.Brtrue or CilCode.Brtrue_S
                or CilCode.Brfalse or CilCode.Brfalse_S;
    }
}
