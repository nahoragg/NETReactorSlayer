﻿/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.IO;
using dnlib.DotNet;

namespace NETReactorSlayer.Core.Helper.De4dot;

public class AssemblyModule
{
    private readonly string filename;
    private readonly ModuleContext moduleContext;
    private ModuleDefMD module;

    public AssemblyModule(string filename, ModuleContext moduleContext)
    {
        this.filename = Path.GetFullPath(filename);
        this.moduleContext = moduleContext;
    }

    public ModuleDefMD Load()
    {
        var options = new ModuleCreationOptions(moduleContext) {TryToLoadPdbFromDisk = false};
        return SetModule(ModuleDefMD.Load(filename, options));
    }

    public ModuleDefMD Load(byte[] fileData)
    {
        var options = new ModuleCreationOptions(moduleContext) {TryToLoadPdbFromDisk = false};
        return SetModule(ModuleDefMD.Load(fileData, options));
    }

    private ModuleDefMD SetModule(ModuleDefMD newModule)
    {
        module = newModule;
        TheAssemblyResolver.Instance.AddModule(module);
        module.EnableTypeDefFindCache = true;
        module.Location = filename;
        return module;
    }

    public ModuleDefMD Reload(
        byte[] newModuleData, DumpedMethodsRestorer dumpedMethodsRestorer, IStringDecrypter stringDecrypter)
    {
        TheAssemblyResolver.Instance.Remove(module);
        var options = new ModuleCreationOptions(moduleContext) {TryToLoadPdbFromDisk = false};
        var mod = ModuleDefMD.Load(newModuleData, options);
        if (dumpedMethodsRestorer != null)
            dumpedMethodsRestorer.Module = mod;
        mod.StringDecrypter = stringDecrypter;
        mod.MethodDecrypter = dumpedMethodsRestorer;
        mod.TablesStream.ColumnReader = dumpedMethodsRestorer;
        mod.TablesStream.MethodRowReader = dumpedMethodsRestorer;
        return SetModule(mod);
    }

    public override string ToString() => filename;
}