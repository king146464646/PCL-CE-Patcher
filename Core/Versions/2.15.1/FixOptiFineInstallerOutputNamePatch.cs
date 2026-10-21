using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;
using PCL_CE_Patcher.Core.Patches;

namespace PCL_CE_Patcher.Core.Versions.v2_15_1
{
    /// <summary>
    ///     修复 OptiFine 安装器输出的版本文件夹名与 PCL 预期不一致的官方 Bug（2.15.1，影响 MC 26.x 的全部 OptiFine 安装）：
    ///     OptiFine 26.x 起安装包文件名与版本列表不再包含 "HD_U"（如 preview_OptiFine_26.2_K2_pre5.jar），
    ///     PCL 的 OptiFine 列表解析据此生成 NameVersion = "26.2-OptiFine_K2_pre5"；
    ///     但 OptiFine 安装器内部仍使用带 HD_U 的版本名，实际输出
    ///     versions\26.2-OptiFine_HD_U_K2_pre5\，与 PCL 合并阶段预期的 versions\{NameVersion}\ 不符，
    ///     合并时报“欲读取的文件不存在”并以“开始合并安装失败”告终（实例重置与新装均会触发）。
    ///     此补丁在 McDownloadOptiFineInstall 调用成功后插入一个注入的辅助方法：
    ///     若 PCL 预期的版本文件夹不存在，则把安装器实际输出的 *-OptiFine_* 文件夹重命名为 PCL 预期的名字。
    ///     旧版 OptiFine（文件夹名本来就一致）时辅助方法直接返回，行为不变。
    /// </summary>
    public class FixOptiFineInstallerOutputNamePatch : IPatch
    {
        public string Name => "修复 OptiFine 安装器输出文件夹名不匹配 (26.x)";

        public bool Apply(ModuleDefinition module)
        {
            // 1. 定位包含 call McDownloadOptiFineInstall 的方法体（新版 OptiFine 安装“方式 A”的 lambda，全模块唯一调用点）
            MethodDefinition? lambda = null;
            int callIndex = -1;
            foreach (var type in module.GetAllTypes())
            {
                foreach (var m in type.Methods)
                {
                    var mBody = m.CilMethodBody;
                    if (mBody == null) continue;
                    for (int i = 0; i < mBody.Instructions.Count; i++)
                    {
                        var instr = mBody.Instructions[i];
                        if (instr.OpCode.Code == CilCode.Call &&
                            instr.Operand is IMethodDescriptor md && md.Name == "McDownloadOptiFineInstall")
                        {
                            lambda = m;
                            callIndex = i;
                            break;
                        }
                    }
                    if (lambda != null) break;
                }
                if (lambda != null) break;
            }
            if (lambda?.CilMethodBody == null || callIndex < 7) return false;

            var instructions = lambda.CilMethodBody.Instructions;

            // 2. 校验 call 前的实参加载形状（源码顺序为
            //    McDownloadOptiFineInstall(baseMcFolderHome, target, task, useJavaWrapper, javaVersion)）：
            //    [call-7] ldloc baseMcFolderHome ← 复用
            //    [call-6] ldarg.0（闭包/this）
            //    [call-5] ldfld javaVersion（捕获变量）
            //    [call-4] ldarg task
            //    [call-3] useJavaWrapper（ldloc / ldc，不强校验）
            //    [call-2] ldarg.0（闭包/this）
            //    [call-1] ldfld target（捕获变量）
            var homeLoad = instructions[callIndex - 7];
            if (homeLoad.OpCode.Code is not (CilCode.Ldloc or CilCode.Ldloc_S
                or CilCode.Ldloc_0 or CilCode.Ldloc_1 or CilCode.Ldloc_2 or CilCode.Ldloc_3))
                return false;
            if (instructions[callIndex - 6].OpCode.Code != CilCode.Ldarg_0) return false;
            if (instructions[callIndex - 5].OpCode.Code != CilCode.Ldfld) return false;
            if (instructions[callIndex - 4].OpCode.Code is not (CilCode.Ldarg_0 or CilCode.Ldarg_1)) return false;
            if (instructions[callIndex - 2].OpCode.Code != CilCode.Ldarg_0) return false;
            if (instructions[callIndex - 1].OpCode.Code != CilCode.Ldfld) return false;

            // 3. 定位 DlOptiFineListEntry.NameVersion 字段与闭包中捕获的 downloadInfo 字段
            var entryType = module.GetAllTypes().FirstOrDefault(t => t.Name == "DlOptiFineListEntry");
            var nameVersionField = entryType?.Fields.FirstOrDefault(fd => fd.Name == "NameVersion");
            if (entryType == null || nameVersionField == null) return false;

            TypeDefinition? closureType = lambda.IsStatic
                ? lambda.Parameters[0]?.ParameterType?.ToTypeDefOrRef()?.Resolve()
                : lambda.DeclaringType;
            var downloadInfoField =
                closureType?.Fields.FirstOrDefault(fd => fd.Signature?.FieldType?.Name == "DlOptiFineListEntry");
            if (downloadInfoField == null)
            {
                // 兜底：从方法体内现有的 ldfld 中复用 DlOptiFineListEntry 类型的字段引用
                foreach (var instr in instructions)
                    if (instr.OpCode.Code == CilCode.Ldfld &&
                        instr.Operand is FieldDefinition ff && ff.DeclaringType?.Name == "DlOptiFineListEntry")
                    {
                        downloadInfoField = ff;
                        break;
                    }
            }
            if (downloadInfoField == null) return false;

            // 4. 查找 ModBase.Log 的两个重载（string 4 参 / Exception 5 参，含默认参数）
            var modBaseType = module.GetAllTypes().FirstOrDefault(t => t.Name == "ModBase");
            var logString = modBaseType?.Methods.FirstOrDefault(m => m.Name == "Log" &&
                m.Parameters.Count == 4 && m.Parameters[0].ParameterType?.Name == "String");
            var logEx = modBaseType?.Methods.FirstOrDefault(m => m.Name == "Log" &&
                m.Parameters.Count == 5 && m.Parameters[0].ParameterType?.Name == "Exception");
            if (logString == null || logEx == null) return false;

            // 5. 构造辅助方法所需的成员引用，并注入辅助类型与方法
            var f = module.CorLibTypeFactory;
            var strRef = f.String.ToTypeDefOrRef();
            var scope = (IResolutionScope?)module.AssemblyReferences.FirstOrDefault(a => a.Name == "System.Runtime")
                        ?? strRef.Scope
                        ?? module;
            var directoryRef = new TypeReference(module, scope, "System.IO", "Directory");
            var pathRef = new TypeReference(module, scope, "System.IO", "Path");
            var exceptionRef = new TypeReference(module, scope, "System", "Exception");

            var concatRef = new MemberReference(strRef, "Concat",
                MethodSignature.CreateStatic(f.String, f.String, f.String));
            var concat4Ref = new MemberReference(strRef, "Concat",
                MethodSignature.CreateStatic(f.String, f.String, f.String, f.String, f.String));
            var containsRef = new MemberReference(strRef, "Contains",
                MethodSignature.CreateInstance(f.Boolean, f.String));
            var dirExistsRef = new MemberReference(directoryRef, "Exists",
                MethodSignature.CreateStatic(f.Boolean, f.String));
            var dirGetDirsRef = new MemberReference(directoryRef, "GetDirectories",
                MethodSignature.CreateStatic(f.String.MakeSzArrayType(), f.String));
            var dirMoveRef = new MemberReference(directoryRef, "Move",
                MethodSignature.CreateStatic(f.Void, f.String, f.String));
            var pathGetFileNameRef = new MemberReference(pathRef, "GetFileName",
                MethodSignature.CreateStatic(f.String, f.String));
            var fileRef = new TypeReference(module, scope, "System.IO", "File");
            var fileExistsRef = new MemberReference(fileRef, "Exists",
                MethodSignature.CreateStatic(f.Boolean, f.String));
            var fileMoveRef = new MemberReference(fileRef, "Move",
                MethodSignature.CreateStatic(f.Void, f.String, f.String));

            var fixType = new TypeDefinition("PclCePatcherFix", "OptiFineInstallerOutputFix",
                TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.NotPublic,
                f.Object.ToTypeDefOrRef());
            var fixMethod = new MethodDefinition("FixInstallerOutputFolder",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodSignature.CreateStatic(f.Void, f.String, f.String));
            fixType.Methods.Add(fixMethod);
            module.TopLevelTypes.Add(fixType);

            // 6. 构造辅助方法的 IL：
            //    void FixInstallerOutputFolder(string home, string expectedName)
            //    {
            //        try {
            //            var dir = home + "\\.minecraft\\versions";
            //            if (!Directory.Exists(dir)) return;
            //            var expectedPath = dir + "\\" + expectedName;
            //            if (Directory.Exists(expectedPath)) return;   // 名字本来就一致（旧版 OptiFine）
            //            foreach (var d in Directory.GetDirectories(dir))
            //                if (Path.GetFileName(d).Contains("-OptiFine_")) { Directory.Move(d, expectedPath); log; return; }
            //        } catch (Exception ex) { Log(ex, "..."); }       // 静默失败，绝不抛出
            //    }
            var body = new CilMethodBody(fixMethod);
            fixMethod.CilMethodBody = body;
            body.InitializeLocals = true;
            body.MaxStack = 16;

            var localDir = new CilLocalVariable(f.String);
            var localDirs = new CilLocalVariable(f.String.MakeSzArrayType());
            var localI = new CilLocalVariable(f.Int32);
            var localExpected = new CilLocalVariable(f.String);
            var localD = new CilLocalVariable(f.String);
            var localEx = new CilLocalVariable(exceptionRef.ToTypeSignature());
            var localOldName = new CilLocalVariable(f.String);
            var localOldJson = new CilLocalVariable(f.String);
            var localNewJson = new CilLocalVariable(f.String);
            var localOldJar = new CilLocalVariable(f.String);
            var localNewJar = new CilLocalVariable(f.String);
            body.LocalVariables.Add(localDir);
            body.LocalVariables.Add(localDirs);
            body.LocalVariables.Add(localI);
            body.LocalVariables.Add(localExpected);
            body.LocalVariables.Add(localD);
            body.LocalVariables.Add(localEx);
            body.LocalVariables.Add(localOldName);
            body.LocalVariables.Add(localOldJson);
            body.LocalVariables.Add(localNewJson);
            body.LocalVariables.Add(localOldJar);
            body.LocalVariables.Add(localNewJar);

            var lDone = new CilInstructionLabel();
            var lLoop = new CilInstructionLabel();
            var lNext = new CilInstructionLabel();
            var lOkEnd = new CilInstructionLabel();
            var lSkipJson = new CilInstructionLabel();
            var lSkipJar = new CilInstructionLabel();

            var instrs = body.Instructions;
            var iStart = new CilInstruction(CilOpCodes.Ldarg_0);
            instrs.Add(iStart);
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, ".minecraft\\versions"));
            instrs.Add(new CilInstruction(CilOpCodes.Call, concatRef));
            instrs.Add(new CilInstruction(CilOpCodes.Stloc, localDir));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localDir));
            instrs.Add(new CilInstruction(CilOpCodes.Call, dirExistsRef));
            instrs.Add(new CilInstruction(CilOpCodes.Brfalse, lDone));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localDir));
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, "\\"));
            instrs.Add(new CilInstruction(CilOpCodes.Call, concatRef));
            instrs.Add(new CilInstruction(CilOpCodes.Ldarg_1));
            instrs.Add(new CilInstruction(CilOpCodes.Call, concatRef));
            instrs.Add(new CilInstruction(CilOpCodes.Stloc, localExpected));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localExpected));
            instrs.Add(new CilInstruction(CilOpCodes.Call, dirExistsRef));
            instrs.Add(new CilInstruction(CilOpCodes.Brtrue, lDone));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localDir));
            instrs.Add(new CilInstruction(CilOpCodes.Call, dirGetDirsRef));
            instrs.Add(new CilInstruction(CilOpCodes.Stloc, localDirs));
            instrs.Add(new CilInstruction(CilOpCodes.Ldc_I4_0));
            instrs.Add(new CilInstruction(CilOpCodes.Stloc, localI));
            var iLoop = new CilInstruction(CilOpCodes.Ldloc, localI);
            lLoop.Instruction = iLoop;
            instrs.Add(iLoop);
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localDirs));
            instrs.Add(new CilInstruction(CilOpCodes.Ldlen));
            instrs.Add(new CilInstruction(CilOpCodes.Conv_I4));
            instrs.Add(new CilInstruction(CilOpCodes.Bge, lDone));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localDirs));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localI));
            instrs.Add(new CilInstruction(CilOpCodes.Ldelem_Ref));
            instrs.Add(new CilInstruction(CilOpCodes.Stloc, localD));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localD));
            instrs.Add(new CilInstruction(CilOpCodes.Call, pathGetFileNameRef));
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, "-OptiFine_"));
            instrs.Add(new CilInstruction(CilOpCodes.Callvirt, containsRef));
            instrs.Add(new CilInstruction(CilOpCodes.Brfalse, lNext));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localD));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localExpected));
            instrs.Add(new CilInstruction(CilOpCodes.Call, dirMoveRef));
            // 文件夹内文件仍是旧名（如 26.2-OptiFine_HD_U_K2_pre5.json/jar），
            // 合并阶段按 {NameVersion}.json 读取，需同步改名：
            // oldName = Path.GetFileName(旧文件夹路径)，Move 后文件已位于新路径下、仅文件名为旧名
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localD));
            instrs.Add(new CilInstruction(CilOpCodes.Call, pathGetFileNameRef));
            instrs.Add(new CilInstruction(CilOpCodes.Stloc, localOldName));
            // oldJson = expectedPath + "\\" + oldName + ".json"
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localExpected));
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, "\\"));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localOldName));
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, ".json"));
            instrs.Add(new CilInstruction(CilOpCodes.Call, concat4Ref));
            instrs.Add(new CilInstruction(CilOpCodes.Stloc, localOldJson));
            // newJson = expectedPath + "\\" + expectedName + ".json"
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localExpected));
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, "\\"));
            instrs.Add(new CilInstruction(CilOpCodes.Ldarg_1));
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, ".json"));
            instrs.Add(new CilInstruction(CilOpCodes.Call, concat4Ref));
            instrs.Add(new CilInstruction(CilOpCodes.Stloc, localNewJson));
            // oldJar = expectedPath + "\\" + oldName + ".jar"
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localExpected));
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, "\\"));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localOldName));
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, ".jar"));
            instrs.Add(new CilInstruction(CilOpCodes.Call, concat4Ref));
            instrs.Add(new CilInstruction(CilOpCodes.Stloc, localOldJar));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localExpected));
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, "\\"));
            instrs.Add(new CilInstruction(CilOpCodes.Ldarg_1));
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, ".jar"));
            instrs.Add(new CilInstruction(CilOpCodes.Call, concat4Ref));
            instrs.Add(new CilInstruction(CilOpCodes.Stloc, localNewJar));
            // if (File.Exists(oldJson)) File.Move(oldJson, newJson);
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localOldJson));
            instrs.Add(new CilInstruction(CilOpCodes.Call, fileExistsRef));
            instrs.Add(new CilInstruction(CilOpCodes.Brfalse, lSkipJson));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localOldJson));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localNewJson));
            instrs.Add(new CilInstruction(CilOpCodes.Call, fileMoveRef));
            var iSkipJson = new CilInstruction(CilOpCodes.Ldloc, localOldJar);
            lSkipJson.Instruction = iSkipJson;
            instrs.Add(iSkipJson);
            // if (File.Exists(oldJar)) File.Move(oldJar, newJar);
            instrs.Add(new CilInstruction(CilOpCodes.Call, fileExistsRef));
            instrs.Add(new CilInstruction(CilOpCodes.Brfalse, lSkipJar));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localOldJar));
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localNewJar));
            instrs.Add(new CilInstruction(CilOpCodes.Call, fileMoveRef));
            var iSkipJar = new CilInstruction(CilOpCodes.Ldstr,
                "[Download] OptiFine 安装器输出的版本文件夹名与 PCL 预期不符，已自动重命名为：");
            lSkipJar.Instruction = iSkipJar;
            instrs.Add(iSkipJar);
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localExpected));
            instrs.Add(new CilInstruction(CilOpCodes.Call, concatRef));
            instrs.Add(new CilInstruction(CilOpCodes.Ldc_I4_0)); // LogLevel.Normal
            instrs.Add(new CilInstruction(CilOpCodes.Ldnull));
            instrs.Add(new CilInstruction(CilOpCodes.Ldnull));
            instrs.Add(new CilInstruction(CilOpCodes.Call, logString));
            instrs.Add(new CilInstruction(CilOpCodes.Br, lDone));
            var iNext = new CilInstruction(CilOpCodes.Ldloc, localI);
            lNext.Instruction = iNext;
            instrs.Add(iNext);
            instrs.Add(new CilInstruction(CilOpCodes.Ldc_I4_1));
            instrs.Add(new CilInstruction(CilOpCodes.Add));
            instrs.Add(new CilInstruction(CilOpCodes.Stloc, localI));
            instrs.Add(new CilInstruction(CilOpCodes.Br, lLoop));
            var iDone = new CilInstruction(CilOpCodes.Leave, lOkEnd);
            lDone.Instruction = iDone;
            instrs.Add(iDone);
            var iCatch = new CilInstruction(CilOpCodes.Stloc, localEx);
            instrs.Add(iCatch);
            instrs.Add(new CilInstruction(CilOpCodes.Ldloc, localEx));
            instrs.Add(new CilInstruction(CilOpCodes.Ldstr, "自动重命名 OptiFine 安装器输出文件夹失败"));
            instrs.Add(new CilInstruction(CilOpCodes.Ldc_I4_2)); // LogLevel.Debug
            instrs.Add(new CilInstruction(CilOpCodes.Ldnull));
            instrs.Add(new CilInstruction(CilOpCodes.Ldnull));
            instrs.Add(new CilInstruction(CilOpCodes.Call, logEx));
            instrs.Add(new CilInstruction(CilOpCodes.Leave, lOkEnd));
            var iEnd = new CilInstruction(CilOpCodes.Ret);
            lOkEnd.Instruction = iEnd;
            instrs.Add(iEnd);

            body.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Exception,
                TryStart = iStart.CreateLabel(),
                TryEnd = iCatch.CreateLabel(),
                HandlerStart = iCatch.CreateLabel(),
                HandlerEnd = iEnd.CreateLabel(),
                ExceptionType = exceptionRef
            });

            // 7. 在 call McDownloadOptiFineInstall 之后插入：
            //    ldloc baseMcFolderHome; ldarg.0; ldfld downloadInfo; ldfld NameVersion; call FixInstallerOutputFolder
            instructions.InsertRange(callIndex + 1, new[]
            {
                new CilInstruction(homeLoad.OpCode, homeLoad.Operand),
                new CilInstruction(CilOpCodes.Ldarg_0),
                new CilInstruction(CilOpCodes.Ldfld, downloadInfoField),
                new CilInstruction(CilOpCodes.Ldfld, nameVersionField),
                new CilInstruction(CilOpCodes.Call, fixMethod)
            });

            // 8. 展开所有宏指令（短跳转 → 长跳转、ldloc.N → ldloc 等），避免插入导致既有短跳转偏移溢出
            instructions.ExpandMacros();
            return true;
        }
    }
}
