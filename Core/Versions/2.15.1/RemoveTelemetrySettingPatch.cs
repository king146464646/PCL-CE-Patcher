using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     彻底移除设置页-杂项里的"启用遥测数据收集"选项（2.15.1）：
    ///     1. 删除 PageSetupLauncherMisc.Reload 中对 CheckSystemTelemetry.Checked 的整段赋值指令（不再读写遥测配置）；
    ///     2. 在 Reload 开头插入 (CheckSystemTelemetry.Parent as Panel)?.Children.Remove(CheckSystemTelemetry)，
    ///        将复选框从可视化树中真正移除，而非仅仅隐藏。
    /// </summary>
    public class RemoveTelemetrySettingPatch : IPatch
    {
        public string Name => "移除设置页遥测开关 (PageSetupLauncherMisc)";

        public bool Apply(ModuleDefinition module)
        {
            var method = module.GetAllTypes()
                .FirstOrDefault(t => t.Name == "PageSetupLauncherMisc")?
                .Methods.FirstOrDefault(m => m.Name == "Reload");

            if (method?.CilMethodBody == null) return false;

            var instructions = method.CilMethodBody.Instructions;

            // 先捕获 CheckSystemTelemetry 的字段引用（删除赋值段后仍需用于移除控件的序列）
            IFieldDescriptor? checkBoxField = null;
            foreach (var instr in instructions)
            {
                if (instr.OpCode.Code == CilCode.Ldfld &&
                    instr.Operand is IFieldDescriptor field &&
                    field.Name == "CheckSystemTelemetry")
                {
                    checkBoxField = field;
                    break;
                }
            }

            bool removedAssignment = RemoveCheckedAssignment(instructions);
            bool detached = checkBoxField != null && DetachCheckBox(module, instructions, checkBoxField);

            return removedAssignment || detached;
        }

        /// <summary>
        ///     删除 Reload 中的 CheckSystemTelemetry.Checked = Config.System.Telemetry; 指令段：
        ///     ldarg.0; ldfld CheckSystemTelemetry; ...; callvirt set_Checked —— 整段移除以保持栈平衡。
        /// </summary>
        private static bool RemoveCheckedAssignment(IList<CilInstruction> instructions)
        {
            int ldfldIndex = -1;
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode.Code == CilCode.Ldfld &&
                    instr.Operand is IFieldDescriptor field &&
                    field.Name == "CheckSystemTelemetry")
                {
                    ldfldIndex = i;
                    break;
                }
            }

            if (ldfldIndex <= 0) return false;

            // 起点必须是加载 this 的指令，否则删除会破坏栈平衡
            if (instructions[ldfldIndex - 1].OpCode.Code != CilCode.Ldarg_0) return false;
            int startIndex = ldfldIndex - 1;

            // 终点：其后第一条 set_Checked 调用（中间的 get_Telemetry 等调用为赋值服务，一并删除）
            int endIndex = -1;
            for (int i = ldfldIndex + 1; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if ((instr.OpCode.Code == CilCode.Callvirt || instr.OpCode.Code == CilCode.Call) &&
                    instr.Operand is IMethodDefOrRef callee &&
                    callee.Name == "set_Checked")
                {
                    endIndex = i;
                    break;
                }
            }

            if (endIndex < 0) return false;

            for (int i = endIndex; i >= startIndex; i--)
                instructions.RemoveAt(i);

            return true;
        }

        /// <summary>
        ///     在 Reload 开头插入 (CheckSystemTelemetry.Parent as Panel)?.Children.Remove(CheckSystemTelemetry)：
        ///       ldarg.0; ldfld CheckSystemTelemetry;
        ///       callvirt get_Parent; isinst Panel;
        ///       dup; brfalse.s SKIP;
        ///       callvirt get_Children;
        ///       ldarg.0; ldfld CheckSystemTelemetry;
        ///       callvirt UIElementCollection::Remove;
        ///     SKIP:
        ///     WPF 方法引用复用模块内已导入的引用。
        /// </summary>
        private static bool DetachCheckBox(ModuleDefinition module, IList<CilInstruction> instructions, IFieldDescriptor checkBoxField)
        {
            IMethodDefOrRef? getParent = null;
            IMethodDefOrRef? getChildren = null;
            IMethodDefOrRef? removeFromCollection = null;
            TypeReference? panelType = null;

            foreach (var memberRef in module.GetImportedMemberReferences())
            {
                if (memberRef is not IMethodDefOrRef methodRef) continue;

                if (getParent == null &&
                    memberRef.Name == "get_Parent" &&
                    memberRef.Parent is TypeReference dependencyObject && dependencyObject.Name == "DependencyObject")
                {
                    getParent = methodRef;
                }
                else if (getChildren == null &&
                         memberRef.Name == "get_Children" &&
                         memberRef.Parent is TypeReference panel && panel.Name == "Panel")
                {
                    getChildren = methodRef;
                    panelType = panel;
                }
                else if (removeFromCollection == null &&
                         memberRef.Name == "Remove" &&
                         memberRef.Parent is TypeReference collection && collection.Name == "UIElementCollection")
                {
                    removeFromCollection = methodRef;
                }

                if (getParent != null && getChildren != null && removeFromCollection != null) break;
            }

            if (getParent == null || getChildren == null || removeFromCollection == null || panelType == null)
                return false;

            var skipTarget = instructions[0];

            instructions.Insert(0, new CilInstruction(CilOpCodes.Ldarg_0));
            instructions.Insert(1, new CilInstruction(CilOpCodes.Ldfld, checkBoxField));
            instructions.Insert(2, new CilInstruction(CilOpCodes.Callvirt, getParent));
            instructions.Insert(3, new CilInstruction(CilOpCodes.Isinst, panelType));
            instructions.Insert(4, new CilInstruction(CilOpCodes.Dup));
            instructions.Insert(5, new CilInstruction(CilOpCodes.Brfalse_S, skipTarget));
            instructions.Insert(6, new CilInstruction(CilOpCodes.Callvirt, getChildren));
            instructions.Insert(7, new CilInstruction(CilOpCodes.Ldarg_0));
            instructions.Insert(8, new CilInstruction(CilOpCodes.Ldfld, checkBoxField));
            instructions.Insert(9, new CilInstruction(CilOpCodes.Callvirt, removeFromCollection));

            return true;
        }
    }
}
