using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     修复实例重置对 OptiFine 实例报错的官方 Bug（2.15.1）：
    ///     PageInstanceOverall.BtnManageRestore_Click 构造 McInstallRequest 时，
    ///     optiFineEntry 只设置了 Inherit 与 DisplayName，漏掉 NameVersion / NameFile / IsPreview，
    ///     导致 McInstallLoader 中 Path.Combine(..., entry.NameVersion) 抛
    ///     ArgumentNullException（Parameter 'path3'），重置必定失败。
    ///     官方原逻辑靠 RestrictedFeaturePolicy 地区限制在大陆拦截此入口，该 Bug 一直被掩盖。
    ///     此补丁把该三元表达式改为 optiFineVersion = currentVersion.OptiFine，
    ///     让 McInstallLoader 走官方已有的完整构造逻辑（与整合包安装同一条路径，
    ///     NameVersion / NameFile / IsPreview 均正确生成）。
    ///     修改均为原地改写指令（多余指令转 nop），不增删指令对象，分支引用不受影响。
    /// </summary>
    public class FixInstanceResetOptiFinePatch : IPatch
    {
        public string Name => "修复实例重置报错 (OptiFine)";

        public bool Apply(ModuleDefinition module)
        {
            var method = module.GetAllTypes()
                .FirstOrDefault(t => t.Name == "PageInstanceOverall")
                ?.Methods.FirstOrDefault(m => m.Name == "BtnManageRestore_Click");

            if (method?.CilMethodBody == null) return false;

            var instructions = method.CilMethodBody.Instructions;

            // 1. 定位 newobj DlOptiFineListEntry..ctor（三元表达式真分支开头）
            int newobjIndex = -1;
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode.Code == CilCode.Newobj &&
                    instr.Operand is IMethodDefOrRef ctor &&
                    ctor.Name == ".ctor" &&
                    ctor.DeclaringType?.Name == "DlOptiFineListEntry")
                {
                    newobjIndex = i;
                    break;
                }
            }
            if (newobjIndex < 0) return false;

            // 2. 定位初始化器末尾的 stfld DisplayName（真分支最后一条）
            int displayIndex = -1;
            for (int i = newobjIndex + 1; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode.Code == CilCode.Stfld &&
                    instr.Operand is IField field &&
                    field.Name == "DisplayName" &&
                    field.DeclaringType?.Name == "DlOptiFineListEntry")
                {
                    displayIndex = i;
                    break;
                }
            }
            if (displayIndex < newobjIndex + 2) return false;

            // 3. 在真分支内找 ldfld McInstanceInfo.OptiFine（DisplayName 拼接用），其前一条即 currentVersion 的 ldloc
            int ldfldIndex = -1;
            for (int i = newobjIndex + 2; i < displayIndex; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode.Code == CilCode.Ldfld &&
                    instr.Operand is IField f &&
                    f.Name == "OptiFine" &&
                    f.DeclaringType?.Name == "McInstanceInfo")
                {
                    ldfldIndex = i;
                    break;
                }
            }
            if (ldfldIndex < 0) return false;

            var ldlocInstr = instructions[ldfldIndex - 1];
            if (ldlocInstr.OpCode.Code is not (CilCode.Ldloc or CilCode.Ldloc_S
                or CilCode.Ldloc_0 or CilCode.Ldloc_1 or CilCode.Ldloc_2 or CilCode.Ldloc_3))
                return false;

            // 4. 定位三元表达式汇合点的 stfld optiFineEntry（br/ldnull 之后）
            int joinIndex = -1;
            for (int i = displayIndex + 1; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode.Code == CilCode.Stfld &&
                    instr.Operand is IField jf &&
                    jf.Name == "optiFineEntry" &&
                    jf.DeclaringType?.Name == "McInstallRequest")
                {
                    joinIndex = i;
                    break;
                }
            }
            if (joinIndex < 0) return false;

            // 5. 解析 McInstallRequest.optiFineVersion 字段（改存目标）
            var requestType = module.GetAllTypes().FirstOrDefault(t => t.Name == "McInstallRequest");
            var optiFineVersionField = requestType?.Fields.FirstOrDefault(f => f.Name == "optiFineVersion");
            if (optiFineVersionField == null) return false;

            // 6. 原地改写（不增删指令对象，所有分支 label 引用保持有效）：
            //    newobj → ldloc currentVersion；下一条（原 dup）→ ldfld OptiFine；
            //    其余真分支指令（set_Inherit、Concat、stfld DisplayName 等）全部转 nop；
            //    汇合点 stfld 改存 optiFineVersion。
            //    栈效果：原真分支净压入 entry（+1），改后净压入 OptiFine 字符串（+1），
            //    假分支 ldnull（+1）不变，汇合点 stfld 弹出 1，保持平衡。
            var dupSlot = instructions[newobjIndex + 1];
            if (dupSlot.OpCode.Code != CilCode.Dup) return false;

            var newobj = instructions[newobjIndex];
            newobj.OpCode = ldlocInstr.OpCode;
            newobj.Operand = ldlocInstr.Operand;

            dupSlot.OpCode = CilOpCodes.Ldfld;
            dupSlot.Operand = instructions[ldfldIndex].Operand;

            for (int i = newobjIndex + 2; i <= displayIndex; i++)
            {
                instructions[i].OpCode = CilOpCodes.Nop;
                instructions[i].Operand = null;
            }

            instructions[joinIndex].Operand = optiFineVersionField;
            return true;
        }
    }
}
