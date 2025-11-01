/*
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

using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NETReactorSlayer.Core.Abstractions;
using NETReactorSlayer.Core.Helper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace NETReactorSlayer.Core.Stages
{
    internal class MethodInliner : IStage
    {

        // https://www.cnblogs.com/Fred1987/p/18603592
        //copy from,https://gist.github.com/6rube/34b561827f0805f73742541b8b8bb770

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);

        public void Run(IContext context)
        {

            long count = 0;
            var proxies = new HashSet<MethodDef>();
            foreach (var method in context.Module.GetTypes().SelectMany(type =>
                         from x in type.Methods.ToList() where x.HasBody && x.Body.HasInstructions select x))
                try
                {



                    //MessageBox(new IntPtr(0), method.Body.Instructions[0].ToString(), "MessageBox", 0);

                    //break;



                    //if (method.Name.String != "XM3c8yL8YOc")

                    //    continue;

                    /*if (method.DeclaringType.Name.String == "FrmToolBox" && method.Name.String == "Chihgu7Fc")

                    {

                        MessageBox(new IntPtr(0), method.Body.Instructions[0].ToString(), "MessageBox", 0);



                    }

                    else

                        continue;
                    */

                    int length = method.Body.Instructions.Count;

                    for (int i = 0; i < length; i++)
                    {

                        // 		VIN_STUDEO.FrmToolBox.Chihgu7Fc(object, RoutedEventArgs) : void @06000017



                        /*if (method.Body.Instructions[i].Operand != null)
                        {

                            MethodDef methodDefcrap = method.Body.Instructions[i].Operand as MethodDef;

                            if (methodDefcrap!=null)

                            {

                                if (methodDefcrap.DeclaringType.Name.String == "FrmToolBox" && methodDefcrap.Name.String == "0")

                                {

                                    MessageBox(new IntPtr(0), methodDefcrap.ToString(), "MessageBox", 0);

                                }

                            }

                        }
                        */

                        MethodDef methodDef = null;
                        if (!method.Body.Instructions[i].OpCode.Equals(OpCodes.Call) && !method.Body.Instructions[i].OpCode.Equals(OpCodes.Callvirt))
                            continue;

                        methodDef = method.Body.Instructions[i].Operand as MethodDef;

                        if (methodDef == null) continue;



                        bool IsProperty = false;



                        foreach (PropertyDef prop in method.DeclaringType.Properties)

                        {

                            if (prop.GetMethod == methodDef)

                            {

                                IsProperty = true;

                                break;

                            }



                            if (prop.SetMethod == methodDef)

                            {

                                IsProperty = true;

                                break;

                            }

                        }



                        if (IsProperty) continue;



                        /*if (methodDef.DeclaringType.Name.String == "FrmToolBox" && methodDef.Name.String == "0")

                        {

                            MessageBox(new IntPtr(0), method.ToString(), "MessageBox", 0);

                        }*/


                        if (method.Body.Instructions[i].OpCode.Equals(OpCodes.Callvirt) && !methodDef.IsVirtual)
                            continue;


                        if (!methodDef.IsStatic)

                        {

                            if ((i - 1) < 0) continue;

                            if (method.Body.Instructions[i - 1].OpCode != OpCodes.Ldarg_0)

                                continue;

                        }



                        if (!IsInlineMethod(methodDef, out var instructions) ||
                            !IsCompatibleType(method.DeclaringType, methodDef.DeclaringType))
                            continue;
                        count++;



                        /*if (count == 10)

                        {

                            MessageBox(new IntPtr(0), methodDef.ToString(), "MessageBox", 0);

                            continue;

                        }*/



                        if (instructions.Count == 1)
                        {

                            method.Body.Instructions[i].OpCode = instructions[0].OpCode;

                            method.Body.Instructions[i].Operand = instructions[0].Operand;

                        }
                        else
                        {

                            method.Body.Instructions[i].OpCode = OpCodes.Nop;

                            method.Body.Instructions[i].Operand = null;

                            length += instructions.Count;

                            foreach (var instr in instructions)

                                method.Body.Instructions.Insert(i++, instr);
                        }

                        method.Body.UpdateInstructionOffsets();
                        proxies.Add(methodDef);
                    }

                    SimpleDeobfuscator.DeobfuscateBlocks(method);
                }
                catch { }

            foreach (var instruction in from type in context.Module.GetTypes()
                                        from method in from x in type.Methods.ToArray() where x.HasBody && x.Body.HasInstructions select x
                                        from instruction in method.Body.Instructions
                                        select instruction)
                try
                {
                    MethodDef item;
                    if (instruction.OpCode.OperandType == OperandType.InlineMethod &&
                        (item = instruction.Operand as MethodDef) != null && proxies.Contains(item))
                        proxies.Remove(item);


                }
                catch { }

            foreach (var method in proxies)
                method.DeclaringType.Remove(method);
            InlinedMethods += count;
        }

        private static bool IsInlineMethod(MethodDef method, out List<Instruction> instructions)
        {

            instructions = new List<Instruction>();
            if (!method.HasBody || !method.Body.HasInstructions)
                return false;
            if (!method.IsStatic && method.Body.Instructions[0].OpCode != OpCodes.Ldarg_0)
                return false;

            var list = method.Body.Instructions;
            var index = list.Count - 1;  // last instruction
            if (index < 1 || list[index].OpCode != OpCodes.Ret)
                return false;
            var code = list[index - 1].OpCode.Code;
            int length;
            if (code != Code.Call && code != Code.Callvirt && code != Code.Newobj)
            {  // threat Ldfld case:
                if (code != Code.Ldfld)
                    return false;
                instructions.Add(new Instruction(list[index - 1].OpCode, list[index - 1].Operand));
                length = (from i in list
                          where i.OpCode != OpCodes.Nop
                          select i).Count() - 2;  // instructions wiouth Ldfld instruction and ret 
                return (length == 1 && length == method.Parameters.Count - 1) || (length == 1 && length == method.Parameters.Count);
            }

            if (!method.IsStatic) // rest of instruction except Code.Ldfld not fixed for instance
                return false;


            //Edit:

            // return false;



            instructions.Add(new Instruction(list[index - 1].OpCode, list[index - 1].Operand));
            length = list.Count(i => i.OpCode != OpCodes.Nop) - 2;
            var count = list.Count - 2;
            if (length != method.Parameters.Count)
            {
                if (list[index - 2].IsLdcI4() && --length == method.Parameters.Count)
                {

                    count = list.Count - 3;
                    instructions.Insert(0, new Instruction(list[index - 2].OpCode, list[index - 2].Operand));
                }
                else
                    return false;
            }

            var num = 0;
            for (var j = 0; j < count; j++)
                if (list[j].OpCode != OpCodes.Nop)
                {
                    if (!list[j].IsLdarg())
                        return false;
                    if (list[j].GetParameterIndex() != num)
                        return false;
                    num++;
                }

            return length == num;

        }

        private static bool IsCompatibleType(IType origType, IType newType) =>
            new SigComparer(SigComparerOptions.IgnoreModifiers).Equals(origType, newType);


        public static long InlinedMethods;
    }
}