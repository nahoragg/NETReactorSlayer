﻿/*
    Copyright (C) 2021 CodeStrikers.org
    This file is part of NETReactorSlayer.
    NETReactorSlayer is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
    NETReactorSlayer is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.
    You should have received a copy of the GNU General Public License
    along with NETReactorSlayer.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Collections.Generic;
using System.Linq;
using de4dot.blocks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace NETReactorSlayer.Core.Deobfuscators;

internal class Cleaner : IStage
{
    public static HashSet<MethodDef> MethodsToRemove = new();
    public static HashSet<ITypeDefOrRef> TypesToRemove = new();
    public static HashSet<Resource> ResourceToRemove = new();
    private CallCounter _methodCallCounter = new();

    public void Execute()
    {
        FixEntrypoint();
        if (Context.RemoveCalls)
        {
            CallRemover.RemoveCalls(MethodsToRemove.ToList());
            Logger.Done($"{MethodsToRemove.Count} Calls to obfuscator types removed.");
        }

        if (!Context.KeepTypes)
        {
            foreach (var method in MethodsToRemove)
                try
                {
                    method.DeclaringType.Remove(method);
                } catch { }

            foreach (var typeDef in TypesToRemove.Select(type => type.ResolveTypeDef()))
                if (typeDef.DeclaringType != null)
                    typeDef.DeclaringType.NestedTypes.Remove(typeDef);
                else
                    Context.Module.Types.Remove(typeDef);

            foreach (var rsrc in ResourceToRemove)
                Context.Module.Resources.Remove(Context.Module.Resources.Find(rsrc.Name));
        }

        if (Context.RemoveJunks)
        {
            foreach (var method in Context.Module.GetTypes().ToList().SelectMany(type => type.Methods.ToList()))
                try
                {
                    RemoveAttributes(method);
                    RemoveJunks(method);
                }
                catch { }

            foreach (var field in Context.Module.GetTypes().ToList().SelectMany(type => type.Fields))
                RemoveAttributes(field);
        }
    }

    private static void FixEntrypoint()
    {
        if (Context.Module.IsEntryPointValid &&
            Context.Module.EntryPoint.DeclaringType.Name.Contains("<PrivateImplementationDetails>"))
            if ((Context.Module.EntryPoint.Body.Instructions
                    .Last(x => x.OpCode == OpCodes.Call && x.Operand is IMethod iMethod &&
                               iMethod.ResolveMethodDef().IsStatic).Operand as IMethod).ResolveMethodDef() is
                { } entryPoint)
            {
                foreach (var attribute in Context.Module.EntryPoint.CustomAttributes)
                    entryPoint.CustomAttributes.Add(attribute);
                if (Context.Module.EntryPoint.DeclaringType.DeclaringType != null)
                    Context.Module.EntryPoint.DeclaringType.DeclaringType.NestedTypes.Remove(
                        Context.Module.EntryPoint.DeclaringType);
                else
                    Context.Module.Types.Remove(Context.Module.EntryPoint.DeclaringType);
                Logger.Done(
                    $"Entrypoint fixed: {Context.Module.EntryPoint.MDToken.ToInt32()}->{entryPoint.MDToken.ToInt32()}");
                Context.Module.EntryPoint = entryPoint;
            }
    }

    private static void RemoveAttributes(IHasCustomAttribute member)
    {
        if (member is MethodDef method)
        {
            method.IsNoInlining = false;
            method.IsSynchronized = false;
            method.IsNoOptimization = false;
        }

        for (var i = 0; i < member.CustomAttributes.Count; i++)
            try
            {
                var cattr = member.CustomAttributes[i];
                if (cattr.Constructor is
                    {FullName: "System.Void System.Diagnostics.DebuggerHiddenAttribute::.ctor()"})
                {
                    member.CustomAttributes.RemoveAt(i);
                    i--;
                    continue;
                }

                switch (cattr.TypeFullName)
                {
                    case "System.Diagnostics.DebuggerStepThroughAttribute":
                        member.CustomAttributes.RemoveAt(i);
                        i--;
                        continue;
                    case "System.Diagnostics.DebuggerNonUserCodeAttribute":
                        member.CustomAttributes.RemoveAt(i);
                        i--;
                        continue;
                    case "System.Diagnostics.DebuggerBrowsableAttribute":
                        member.CustomAttributes.RemoveAt(i);
                        i--;
                        continue;
                }

                if (cattr.TypeFullName != "System.Runtime.CompilerServices.MethodImplAttribute")
                    continue;
                var options = 0;
                if (!GetMethodImplOptions(cattr, ref options))
                    continue;
                if (options != 0 && options != (int) MethodImplAttributes.NoInlining)
                    continue;
                member.CustomAttributes.RemoveAt(i);
                i--;
            } catch { }
    }

    private void RemoveJunks(MethodDef method)
    {
        if (!method.HasBody)
            return;
        try
        {
            if (_methodCallCounter != null && method.Name == ".ctor" || method.Name == ".cctor" ||
                Context.Module.EntryPoint == method)
            {
                #region IsEmpty

                static bool IsEmpty(MethodDef methodDef)
                {
                    if (!DotNetUtils.IsEmptyObfuscated(methodDef))
                        return false;

                    var type = methodDef.DeclaringType;
                    if (type.HasEvents || type.HasProperties)
                        return false;
                    if (type.Fields.Count != 1 && type.Fields.Count != 2)
                        return false;
                    switch (type.Fields.Count)
                    {
                        case 2 when !(type.Fields.Any(x => x.FieldType.FullName == "System.Boolean") &&
                                      type.Fields.Any(x => x.FieldType.FullName == "System.Object")):
                        case 1 when type.Fields[0].FieldType.FullName != "System.Boolean":
                            return false;
                    }

                    if (type.IsPublic)
                        return false;

                    var otherMethods = 0;
                    foreach (var method in type.Methods)
                    {
                        if (method.Name == ".ctor" || method.Name == ".cctor")
                            continue;
                        if (method == methodDef)
                            continue;
                        otherMethods++;
                        if (method.Body == null)
                            return false;
                        if (method.Body.Instructions.Count > 20)
                            return false;
                    }

                    return otherMethods <= 8;
                }

                #endregion

                foreach (var calledMethod in DotNetUtils.GetCalledMethods(Context.Module, method))
                {
                    if (!calledMethod.IsStatic || calledMethod.Body == null)
                        continue;
                    if (!DotNetUtils.IsMethod(calledMethod, "System.Void", "()"))
                        continue;
                    if (IsEmpty(calledMethod))
                        _methodCallCounter?.Add(calledMethod);
                }

                var numCalls = 0;
                var methodDef = (MethodDef) _methodCallCounter?.Most(out numCalls);
                if (numCalls >= 10)
                {
                    CallRemover.RemoveCalls(methodDef);
                    try
                    {
                        if (methodDef?.DeclaringType.DeclaringType != null)
                            methodDef.DeclaringType.DeclaringType.NestedTypes.Remove(methodDef.DeclaringType);
                        else
                            Context.Module.Types.Remove(methodDef?.DeclaringType);
                    } catch { }

                    _methodCallCounter = null;
                }
            }
        } catch { }

        try
        {
            if (method.Body.Instructions.Count == 4 &&
                method.Body.Instructions[0].OpCode.Equals(OpCodes.Ldsfld) &&
                method.Body.Instructions[1].OpCode.Equals(OpCodes.Ldnull) &&
                method.Body.Instructions[2].OpCode.Equals(OpCodes.Ceq) &&
                method.Body.Instructions[3].OpCode.Equals(OpCodes.Ret))
                if (method.Body.Instructions[0].Operand is FieldDef {IsPublic: false} field &&
                    (field.FieldType.FullName == "System.Object" || field.DeclaringType != null &&
                        field.FieldType.FullName ==
                        field.DeclaringType.FullName))
                    foreach (var method2 in method.DeclaringType.Methods
                                 .Where(x => x.HasBody && x.Body.HasInstructions && x.Body.Instructions.Count == 2)
                                 .ToList().Where(method2 => !method2.IsPublic &&
                                                            (method2.ReturnType.FullName == "System.Object" ||
                                                             method2.DeclaringType != null &&
                                                             method2.ReturnType.FullName ==
                                                             field.DeclaringType.FullName))
                                 .Where(method2 => method2.Body.Instructions[0].OpCode.Equals(OpCodes.Ldsfld) &&
                                                   method2.Body.Instructions[1].OpCode.Equals(OpCodes.Ret)))
                    {
                        if (method2.Body.Instructions[0].Operand is not FieldDef field2 ||
                            field2.MDToken.ToInt32() != field.MDToken.ToInt32()) continue;
                        try
                        {
                            method.DeclaringType.Remove(method);
                        } catch { }

                        try
                        {
                            method2.DeclaringType.Remove(method2);
                        } catch { }

                        try
                        {
                            field.DeclaringType.Fields.Remove(field);
                        } catch { }

                        return;
                    }
        } catch { }

        try
        {
            if (IsInlineMethod(method))
            {
                method.DeclaringType.Remove(method);
                return;
            }
        } catch { }

        try
        {
            if (DotNetUtils.IsMethod(method, "System.Void", "()") &&
                method.IsStatic &&
                method.IsAssembly &&
                DotNetUtils.IsEmpty(method) &&
                !method.DeclaringType.Methods.Any(x => DotNetUtils.GetMethodCalls(x).Contains(method)))
                method.DeclaringType.Remove(method);
        } catch { }
    }

    private static bool IsInlineMethod(MethodDef method)
    {
        if (!method.IsStatic ||
            !method.IsAssembly &&
            !method.IsPrivateScope &&
            !method.IsPrivate ||
            method.GenericParameters.Count > 0 ||
            method.Name == ".cctor" ||
            !method.HasBody ||
            !method.Body.HasInstructions ||
            method.Body.Instructions.Count < 2)
            return false;

        switch (method.Body.Instructions[0].OpCode.Code)
        {
            case Code.Ldc_I4 or Code.Ldc_I4_0 or Code.Ldc_I4_1 or Code.Ldc_I4_2
                or Code.Ldc_I4_3 or Code.Ldc_I4_4 or Code.Ldc_I4_5 or Code.Ldc_I4_6 or Code.Ldc_I4_7 or Code.Ldc_I4_8
                or Code.Ldc_I4_M1 or Code.Ldc_I4_S or Code.Ldc_I8 or Code.Ldc_R4 or Code.Ldc_R8 or Code.Ldftn
                or Code.Ldnull
                or Code.Ldstr or Code.Ldtoken or Code.Ldsfld or Code.Ldsflda:
            {
                if (method.Body.Instructions[1].OpCode.Code != Code.Ret)
                    return false;
                break;
            }
            case Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0 or Code.Ldarg_1
                or Code.Ldarg_2 or Code.Ldarg_3 or Code.Ldarga or Code.Ldarga_S or Code.Call or Code.Newobj:
            {
                if (!IsCallMethod(method))
                    return false;
                break;
            }
            default:
                return false;
        }

        return true;
    }

    private static bool IsCallMethod(MethodDef method)
    {
        var loadIndex = 0;
        var methodArgsCount = DotNetUtils.GetArgsCount(method);
        var instrs = method.Body.Instructions;
        var i = 0;
        for (; i < instrs.Count && i < methodArgsCount; i++)
        {
            var instr = instrs[i];
            switch (instr.OpCode.Code)
            {
                case Code.Ldarg:
                case Code.Ldarg_S:
                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                case Code.Ldarga:
                case Code.Ldarga_S:
                    if (instr.GetParameterIndex() != loadIndex)
                        return false;
                    loadIndex++;
                    continue;
            }

            break;
        }

        if (loadIndex != methodArgsCount)
            return false;
        if (i + 1 >= instrs.Count)
            return false;

        switch (instrs[i].OpCode.Code)
        {
            case Code.Call:
            case Code.Callvirt:
            case Code.Newobj:
            case Code.Ldfld:
            case Code.Ldflda:
            case Code.Ldftn:
            case Code.Ldvirtftn:
                break;
            default:
                return false;
        }

        return instrs[i + 1].OpCode.Code == Code.Ret;
    }

    private static bool GetMethodImplOptions(CustomAttribute cA, ref int value)
    {
        if (cA.IsRawBlob)
            return false;
        if (cA.ConstructorArguments.Count != 1)
            return false;
        if (cA.ConstructorArguments[0].Type.ElementType != ElementType.I2 &&
            cA.ConstructorArguments[0].Type.FullName != "System.Runtime.CompilerServices.MethodImplOptions")
            return false;
        var arg = cA.ConstructorArguments[0].Value;
        switch (arg)
        {
            case short @int:
                value = @int;
                return true;
            case int int1:
                value = int1;
                return true;
            default:
                return false;
        }
    }
}