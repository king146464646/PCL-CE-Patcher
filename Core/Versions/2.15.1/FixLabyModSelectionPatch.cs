using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     修复“修改实例”页选择 LabyMod 版本时闪退的官方 Bug（2.15.1）：
    ///     PageInstanceInstall.LabyMod_Selected 把 ((dynamic)sender.Tag)("channel") 写成了
    ///     **圆括号调用**（下载页 PageDownloadInstall 同段代码是正确的方括号索引 ["channel"]），
    ///     JsonObject 不是委托，动态绑定器抛 RuntimeBinderException（Cannot invoke a non-delegate type），
    ///     未被捕获导致启动器直接退出。
    ///     IL 层面 Binder.Invoke 与 Binder.GetIndex 签名完全一致（flags, context, typeArguments），
    ///     且三个取值（channel / commitReference / version）共享同一个 CallSite 缓存（仅生成一次 Invoke 调用），
    ///     因此把 call Binder::Invoke 原地改写为 call Binder::GetIndex 即等价于改为方括号索引。
    /// </summary>
    public class FixLabyModSelectionPatch : IPatch
    {
        public string Name => "修复修改实例页选择 LabyMod 闪退";

        public bool Apply(ModuleDefinition module)
        {
            var method = module.GetAllTypes()
                .FirstOrDefault(t => t.Name == "PageInstanceInstall")
                ?.Methods.FirstOrDefault(m => m.Name == "LabyMod_Selected");

            if (method?.CilMethodBody == null) return false;

            var instructions = method.CilMethodBody.Instructions;
            bool patched = false;
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode.Code == CilCode.Call &&
                    instr.Operand is MemberReference memberRef &&
                    memberRef.Name == "Invoke" &&
                    memberRef.Parent is TypeReference binderType &&
                    binderType.Name == "Binder" &&
                    binderType.Namespace == "Microsoft.CSharp.RuntimeBinder")
                {
                    // 原地替换操作数：Invoke → GetIndex（签名不变，站点 delegate 类型完全兼容）
                    instr.Operand = new MemberReference(memberRef.Parent, "GetIndex",
                        (MemberSignature?)memberRef.Signature);
                    patched = true;
                }
            }
            return patched;
        }
    }
}
