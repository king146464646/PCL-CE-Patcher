using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     修改 PCL.Core 中 ConfigService 自动生成的配置默认值（2.15.1）：
    ///     - ToolDownloadTranslateV2（社区资源文件名格式）：1（[名称] xxx）→ 4（create-1.21.1-6.0.4）
    ///     - SystemSystemUpdate（自动更新行为）：1（DownloadAndAnnounce 自动下载并提示）→ 3（Disable 关闭）
    ///     默认值嵌入 _InitializeConfigItems 中 ConfigItem 构造调用的常量参数，
    ///     定位方式：key 的两条连续 ldstr 之后的第一条 ldc.i4 常量即为默认值。
    /// </summary>
    public class ChangeDefaultConfigPatch : IPatch
    {
        public string Name => "修改配置默认值 (文件名格式 / 自动更新)";

        public bool Apply(ModuleDefinition module)
        {
            var method = module.GetAllTypes()
                .FirstOrDefault(t => t.FullName == "PCL.Core.App.Configuration.ConfigService")?
                .Methods.FirstOrDefault(m => m.Name == "_InitializeConfigItems");

            if (method?.CilMethodBody == null) return false;

            var instructions = method.CilMethodBody.Instructions;

            bool okNameFormat = PatchDefaultConstant(instructions, "ToolDownloadTranslateV2", CilOpCodes.Ldc_I4_4);
            bool okUpdateMode = PatchDefaultConstant(instructions, "SystemSystemUpdate", CilOpCodes.Ldc_I4_3);

            return okNameFormat || okUpdateMode;
        }

        /// <summary>
        ///     在 items 数组初始化器中定位：
        ///     ldstr key; ldstr key; ldc.i4.X; ldc.i4.Y; newobj ConfigItem::.ctor ...
        ///     并把第三条指令（默认值常量）替换为 <paramref name="newDefault"/>。
        /// </summary>
        private static bool PatchDefaultConstant(
            IList<CilInstruction> instructions, string key, CilOpCode newDefault)
        {
            for (int i = 0; i < instructions.Count - 2; i++)
            {
                if (instructions[i].OpCode.Code != CilCode.Ldstr) continue;
                if (!string.Equals(instructions[i].Operand as string, key, StringComparison.Ordinal)) continue;

                // 第二条也必须是相同的 ldstr key（元组 Item1 + ConfigItem ctor 参数）
                if (instructions[i + 1].OpCode.Code != CilCode.Ldstr) continue;
                if (!string.Equals(instructions[i + 1].Operand as string, key, StringComparison.Ordinal)) continue;

                // 第三条为默认值常量
                var valueInstr = instructions[i + 2];
                if (IsLdcI4(valueInstr))
                {
                    valueInstr.OpCode = newDefault;
                    valueInstr.Operand = null;
                    return true;
                }
            }

            return false;
        }

        private static bool IsLdcI4(CilInstruction instr) => instr.OpCode.Code switch
        {
            CilCode.Ldc_I4 or CilCode.Ldc_I4_S or
            CilCode.Ldc_I4_0 or CilCode.Ldc_I4_1 or CilCode.Ldc_I4_2 or
            CilCode.Ldc_I4_3 or CilCode.Ldc_I4_4 or CilCode.Ldc_I4_5 or
            CilCode.Ldc_I4_6 or CilCode.Ldc_I4_7 or CilCode.Ldc_I4_8 => true,
            _ => false
        };
    }
}
