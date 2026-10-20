using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     移除首启"启用遥测数据收集"询问弹窗（2.15.1）。
    ///     弹窗代码位于 FormMain.FormMain_Loaded 中 ModBase.RunInNewThread(() => { ... }) 的 lambda 内，
    ///     lambda 捕获了 this（内部调用实例方法 EndProgram），Roslyn 会把方法体编译到
    ///     FormMain 的嵌套 display class（如 &lt;FormMain_Loaded&gt;c__DisplayClass0_0）中，
    ///     因此这里遍历 FormMain 及其所有嵌套类型的方法，而非只搜 FormMain_Loaded 本身。
    ///     匹配 "call get_TelemetryConfig → (ldnull) → call IsDefault" 序列，
    ///     把 IsDefault 调用替换为 pop; pop; ldc.i4.0（恒返回 false），
    ///     弹窗及后续 SetValue 均不再执行，遥测保持默认关闭。
    ///     构造函数中"旧版本升级重置遥测"的 IsDefault 调用（.ctor）不在匹配范围。
    /// </summary>
    public class RemoveTelemetryDialogPatch : IPatch
    {
        public string Name => "移除遥测询问弹窗 (FormMain)";

        public bool Apply(ModuleDefinition module)
        {
            foreach (var type in module.GetAllTypes())
            {
                // FormMain 本体的 FullName 为 "PCL.FormMain"，
                // 嵌套 display class 为 "PCL.FormMain/<FormMain_Loaded>c__DisplayClass0_0" 等形式
                var fullName = type.FullName?.ToString();
                if (fullName == null || !fullName.StartsWith("PCL.FormMain")) continue;

                foreach (var method in type.Methods)
                {
                    // 构造函数里的 IsDefault 属于旧版本升级重置逻辑，保持原样
                    if (method.Name == ".ctor") continue;
                    if (method.CilMethodBody == null) continue;

                    var instructions = method.CilMethodBody.Instructions;

                    for (int i = 0; i < instructions.Count; i++)
                    {
                        if (!IsCallNamed(instructions[i], "get_TelemetryConfig")) continue;

                        // IsDefault 的可选参数为 null，调用点 IL 为 ldnull; call IsDefault
                        for (int j = i + 1; j < instructions.Count && j <= i + 3; j++)
                        {
                            var next = instructions[j];
                            if (next.OpCode.Code == CilCode.Ldnull)
                                continue;

                            if (!IsCallNamed(next, "IsDefault"))
                                break;

                            // IsDefault(this, null) 消耗 2 个栈值并压入 bool 结果，
                            // 替换为 pop; pop; ldc.i4.0 后栈效果一致，后续 brfalse 永远跳过弹窗。
                            instructions.RemoveAt(j);
                            instructions.Insert(j, new CilInstruction(CilOpCodes.Pop));
                            instructions.Insert(j + 1, new CilInstruction(CilOpCodes.Pop));
                            instructions.Insert(j + 2, new CilInstruction(CilOpCodes.Ldc_I4_0));
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsCallNamed(CilInstruction instruction, string name)
            => (instruction.OpCode.Code == CilCode.Callvirt || instruction.OpCode.Code == CilCode.Call) &&
               instruction.Operand is IMethodDefOrRef callee && callee.Name == name;
    }
}
