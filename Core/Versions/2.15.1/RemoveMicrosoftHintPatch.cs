using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     移除“需要先进行一次正版验证才能使用其他验证方式”黄色提示条（2.15.1）。
    ///     PageLoginProfile.RefreshProfileList 中原本在档案数为 0 时显示 HintMicrosoft，
    ///     此补丁把对 HintMicrosoft.Visibility 的赋值改为恒为 Collapsed（枚举值 2），使其永不显示。
    /// </summary>
    public class RemoveMicrosoftHintPatch : IPatch
    {
        public string Name => "移除正版验证前置提示 (HintMicrosoft)";

        public bool Apply(ModuleDefinition module)
        {
            var method = module.GetAllTypes()
                .FirstOrDefault(t => t.Name == "PageLoginProfile")?
                .Methods.FirstOrDefault(m => m.Name == "RefreshProfileList");

            if (method?.CilMethodBody == null) return false;

            var instructions = method.CilMethodBody.Instructions;

            // 定位 ldfld HintMicrosoft（提示条控件引用）
            int ldfldIndex = -1;
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode.Code == CilCode.Ldfld &&
                    instr.Operand is IFieldDescriptor field &&
                    field.Name == "HintMicrosoft")
                {
                    ldfldIndex = i;
                    break;
                }
            }

            if (ldfldIndex < 0) return false;

            // 定位其后第一条 set_Visibility 调用（即对该提示条的赋值）
            int callIndex = -1;
            for (int i = ldfldIndex + 1; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if ((instr.OpCode.Code == CilCode.Callvirt || instr.OpCode.Code == CilCode.Call) &&
                    instr.Operand is IMethodDefOrRef callee &&
                    callee.Name == "set_Visibility")
                {
                    callIndex = i;
                    break;
                }
            }

            if (callIndex < 0) return false;

            // 删除 ldfld 与 set_Visibility 之间的三元判断指令
            //（仅包含 Profiles.Count 读取与分支，无副作用；try/catch 边界指令不在删除范围内），
            // 改为恒压入 Visibility.Collapsed（= 2）后调用 set_Visibility。
            for (int i = callIndex - 1; i > ldfldIndex; i--)
                instructions.RemoveAt(i);

            instructions.Insert(ldfldIndex + 1, new CilInstruction(CilOpCodes.Ldc_I4_2));

            return true;
        }
    }
}
