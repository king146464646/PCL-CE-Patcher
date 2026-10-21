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
    ///     此补丁把 call get_HasMicrosoftProfile 原地改为 nop、其后的条件分支原地改为长跳转 br，
    ///     无条件跳过整个提示块；方法内其余检查（路径合法性、实例状态、验证要求等）保持原样。
    ///     注意：必须原地修改指令而非替换指令对象 —— 前面 throw 检查的分支目标正是该 call 指令，
    ///     替换对象会使该引用失效（Invalid labels）；原地改 opcode 不改变对象身份，引用依然有效。
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
                    IsBranch(next.OpCode.Code))
                {
                    // get_HasMicrosoftProfile 是静态属性：call 压入 bool，随后的分支按条件跳过提示块。
                    // call → nop（不再压栈）；条件分支 → br 长跳转（提示块含弹窗与 lambda，
                    // 长度可能超出短跳转 ±127 字节的范围），Operand（指向块尾的 label）保持不变。
                    // 栈效果：原 call(+1) + brtrue(-1, 条件跳) 净 0；新 nop(0) + br(0, 无条件跳) 净 0。
                    instr.OpCode = CilOpCodes.Nop;
                    instr.Operand = null;
                    next.OpCode = CilOpCodes.Br;
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
