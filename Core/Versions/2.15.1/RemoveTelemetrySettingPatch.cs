using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.DotNet.Signatures.Types;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     彻底移除设置页-杂项里的"启用遥测数据收集"选项（2.15.1）：
    ///     1. 删除 PageSetupLauncherMisc.Reload 中对 CheckSystemTelemetry.Checked 的整段赋值指令（不再读写遥测配置）；
    ///     2. 在 Reload 开头插入 (CheckSystemTelemetry.Parent as Panel)?.Children.Remove(CheckSystemTelemetry)，
    ///        将复选框从可视化树中真正移除，而非仅仅隐藏。
    ///     WPF 成员引用通过 ReferenceImporter 手工构造，不依赖模块中恰好已导入的引用。
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
        ///       dup; brtrue.s HAS_PANEL; pop; br.s SKIP;
        ///     HAS_PANEL:
        ///       callvirt get_Children; ldarg.0; ldfld CheckSystemTelemetry; callvirt UIElementCollection::Remove;
        ///     SKIP: nop
        ///     两条路径均不残留栈值；重复执行 Reload 时 Parent 已为 null，序列安全跳过。
        ///     WPF 引用经 ReferenceImporter 构造导入，不依赖模块已有的成员引用。
        /// </summary>
        private static bool DetachCheckBox(ModuleDefinition module, IList<CilInstruction> instructions, IFieldDescriptor checkBoxField)
        {
            var presentationFramework = module.AssemblyReferences
                .FirstOrDefault(reference => reference.Name == "PresentationFramework");
            if (presentationFramework == null) return false;

            var dependencyObjectRef = new TypeReference(module, presentationFramework, "System.Windows", "DependencyObject");
            var uiElementRef = new TypeReference(module, presentationFramework, "System.Windows", "UIElement");
            var panelRef = new TypeReference(module, presentationFramework, "System.Windows.Controls", "Panel");
            var collectionRef = new TypeReference(module, presentationFramework, "System.Windows.Controls", "UIElementCollection");

            var importer = new ReferenceImporter(module);
            var getParent = importer.ImportMethod(new MemberReference(dependencyObjectRef, "get_Parent",
                MethodSignature.CreateInstance(new TypeDefOrRefSignature(dependencyObjectRef))));
            var getChildren = importer.ImportMethod(new MemberReference(panelRef, "get_Children",
                MethodSignature.CreateInstance(new TypeDefOrRefSignature(collectionRef))));
            var removeFromCollection = importer.ImportMethod(new MemberReference(collectionRef, "Remove",
                MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, new TypeDefOrRefSignature(uiElementRef))));
            var panelType = importer.ImportType(panelRef);

            // AsmResolver 要求分支指令的操作数为 ICilLabel，需用 CilInstructionLabel 包装目标指令
            var hasPanelTarget = new CilInstruction(CilOpCodes.Callvirt, getChildren);
            var skipTarget = new CilInstruction(CilOpCodes.Nop);

            instructions.Insert(0, new CilInstruction(CilOpCodes.Ldarg_0));
            instructions.Insert(1, new CilInstruction(CilOpCodes.Ldfld, checkBoxField));
            instructions.Insert(2, new CilInstruction(CilOpCodes.Callvirt, getParent));
            instructions.Insert(3, new CilInstruction(CilOpCodes.Isinst, panelType));
            instructions.Insert(4, new CilInstruction(CilOpCodes.Dup));
            instructions.Insert(5, new CilInstruction(CilOpCodes.Brtrue_S, new CilInstructionLabel(hasPanelTarget)));
            instructions.Insert(6, new CilInstruction(CilOpCodes.Pop));
            instructions.Insert(7, new CilInstruction(CilOpCodes.Br_S, new CilInstructionLabel(skipTarget)));
            instructions.Insert(8, hasPanelTarget);
            instructions.Insert(9, new CilInstruction(CilOpCodes.Ldarg_0));
            instructions.Insert(10, new CilInstruction(CilOpCodes.Ldfld, checkBoxField));
            instructions.Insert(11, new CilInstruction(CilOpCodes.Callvirt, removeFromCollection));
            instructions.Insert(12, skipTarget);

            return true;
        }
    }
}
