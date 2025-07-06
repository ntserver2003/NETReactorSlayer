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





using de4dot.blocks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HarmonyLib;
using NETReactorSlayer.Core.Abstractions;
using NETReactorSlayer.Core.Helper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Code = dnlib.DotNet.Emit.Code;
using OpCodes = dnlib.DotNet.Emit.OpCodes;

namespace NETReactorSlayer.Core.Stages
{
    internal class StringDecrypter : IStage
    {

        public void Run(IContext context)
        {
            Context = context;
            try
            {
                long count;

                try
                {
                    if (Find())
                    {
                        _decryptedResource = _encryptedResource.Decrypt();
                        count = InlineStringsStatically();
                    }
                    else
                        throw new Exception();

                    if (count == 0)
                        throw new Exception();

                    Cleaner.AddMethodToBeRemoved(_encryptedResource.DecrypterMethod);
                    Cleaner.AddResourceToBeRemoved(_encryptedResource.EmbeddedResource);
                }
                catch { count = InlineStringsDynamically(); }


                if (count > 0)
                    Context.Logger.Info(count + " Strings decrypted.");
                else
                    Context.Logger.Warn("Couldn't find any encrypted string.");
            }
            catch (Exception ex)
            {
                Context.Logger.Error($"An unexpected error occurred during decrypting strings. {ex.Message}.");
            }

            _encryptedResource?.Dispose();
        }

        private bool Find()
        {
            foreach (var type in Context.Module.GetTypes())
                try
                {
                    if (type.BaseType != null && type.BaseType.FullName != "System.Object")
                        continue;
                    foreach (var method in from method in type.Methods
                                           where method.IsStatic && method.HasBody
                                           where DotNetUtils.IsMethod(method, "System.String", "(System.Int32)")
                                           where EncryptedResource.IsKnownDecrypter(method, new[] { "System.String" }, true)
                                           select method)
                    {
                        FindKeyIv(method);

                        EncryptedResource resource = null;

                        try
                        {
                            resource = new EncryptedResource(Context, method, new[] { "System.String" });
                            if (resource.EmbeddedResource != null)
                            {
                                if (_decrypterMethods.Any(x => x.Value == resource.EmbeddedResource.Name) ||
                                    _decrypterMethods.Count == 0)
                                    _decrypterMethods.Add(resource.DecrypterMethod, resource.EmbeddedResource.Name);

                                if (_encryptedResource == null)
                                    _encryptedResource = resource;
                                else
                                    resource.Dispose();

                                continue;
                            }
                        }
                        catch { }

                        resource?.Dispose();
                    }
                }
                catch { }

            return _decrypterMethods.Count > 0;
        }

        private void FindKeyIv(MethodDef method)
        {
            var requiredTypes = new[]
            {
                "System.Byte[]",
                "System.IO.MemoryStream",
                "System.Security.Cryptography.CryptoStream"
            };
            foreach (var instructions in from calledMethod in DotNetUtils.GetCalledMethods(Context.Module, method)
                                         where calledMethod.DeclaringType == method.DeclaringType
                                         where calledMethod.MethodSig.GetRetType().GetFullName() == "System.Byte[]"
                                         let localTypes = new LocalTypes(calledMethod)
                                         where localTypes.All(requiredTypes)
                                         select calledMethod.Body.Instructions)
            {
                byte[] newKey = null, newIv = null;
                for (var i = 0; i < instructions.Count && (newKey == null || newIv == null); i++)
                {
                    var instr = instructions[i];
                    if (instr.OpCode.Code != Code.Ldtoken)
                        continue;
                    if (instr.Operand is not FieldDef field)
                        continue;
                    if (field.InitialValue == null)
                        continue;
                    switch (field.InitialValue.Length)
                    {
                        case 32:
                            newKey = field.InitialValue;
                            break;
                        case 16:
                            newIv = field.InitialValue;
                            break;
                    }
                }

                if (newKey == null || newIv == null)
                    continue;

                _stringDecrypterVersion = new LocalTypes(method).Exists("System.IntPtr")
                    ? StringDecrypterVersion.V38
                    : StringDecrypterVersion.V37;

                _key = newKey;
                _iv = newIv;
                return;
            }
        }

        private string Decrypt(int offset)
        {
            if (_key == null)
            {
                var length = BitConverter.ToInt32(_decryptedResource, offset);
                return Encoding.Unicode.GetString(_decryptedResource, offset + 4, length);
            }

            byte[] encryptedStringData;
            switch (_stringDecrypterVersion)
            {
                case StringDecrypterVersion.V37:
                    {
                        var fileOffset = BitConverter.ToInt32(_decryptedResource, offset);
                        var length = BitConverter.ToInt32(Context.ModuleBytes, fileOffset);
                        encryptedStringData = new byte[length];
                        Array.Copy(Context.ModuleBytes, fileOffset + 4, encryptedStringData, 0, length);
                        break;
                    }
                case StringDecrypterVersion.V38:
                    {
                        var rva = BitConverter.ToUInt32(_decryptedResource, offset);
                        var length = Context.PeImage.ReadInt32(rva);
                        encryptedStringData = Context.PeImage.ReadBytes(rva + 4, length);
                        break;
                    }
                default:
                    throw new ApplicationException("Unknown string decrypter version");
            }

            return Encoding.Unicode.GetString(DeobUtils.AesDecrypt(encryptedStringData, _key, _iv));
        }

        private long InlineStringsStatically()
        {
            bool IsDecrypterMethod(IMDTokenProvider method) => method != null &&
                                                               _decrypterMethods.Any(x =>
                                                                   x.Key.Equals(method) || x.Key.MDToken.ToInt32()
                                                                       .Equals(method.MDToken.ToInt32()));

            long count = 0;
            foreach (var method in Context.Module.GetTypes().SelectMany(type =>
                         (from x in type.Methods where x.HasBody && x.Body.HasInstructions select x)
                         .ToArray()))
            {
                SimpleDeobfuscator.DeobfuscateBlocks(method);
                for (var i = 0; i < method.Body.Instructions.Count; i++)
                    try
                    {
                        if (!method.Body.Instructions[i].IsLdcI4() ||
                            !method.Body.Instructions[i + 1].OpCode.Equals(OpCodes.Call))
                            continue;

                        var methodDef = ((IMethod)method.Body.Instructions[i + 1].Operand).ResolveMethodDef();
                        if (methodDef != null && methodDef.HasReturnType != true)
                            continue;

                        if (methodDef != null && (!methodDef.HasParams() || methodDef.Parameters.Count != 1 ||
                                                  methodDef.Parameters[0].Type.FullName != "System.Int32"))
                            continue;

                        if (!IsDecrypterMethod(methodDef))
                            continue;

                        var decrypt = Decrypt(method.Body.Instructions[i].GetLdcI4Value());
                        method.Body.Instructions[i].OpCode = OpCodes.Nop;
                        method.Body.Instructions[i + 1].OpCode = OpCodes.Ldstr;
                        method.Body.Instructions[i + 1].Operand = decrypt;
                        count++;
                    }
                    catch { }

                SimpleDeobfuscator.DeobfuscateBlocks(method);
            }

            return count;
        }
        // https://www.cnblogs.com/Fred1987/p/18603592
        //copy from,https://gist.github.com/6rube/34b561827f0805f73742541b8b8bb770
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);



        /// <summary>
        /// Dynamically decrypts string literals at runtime
        /// Decrypts strings encrypted by .NET Reactor and restores them to their original form during runtime.
        /// </summary>
        /// <returns>The number of successfully decrypted strings</returns>
        private long InlineStringsDynamically()
        {
            // Skip dynamic decryption under certain conditions
            // Skip if NativeStub + NecroBit combination is used, or if reflection is not used
            if ((Context.Info.NativeStub && Context.Info.NecroBit) ||
                !Context.Info.UsesReflection)
                return 0;

            try
            {
                // Execute the module constructor of the target assembly to perform static initialization
                // This process initializes the data required for string decryption
                RuntimeHelpers.RunModuleConstructor(Context.Assembly.ManifestModule.ModuleHandle);
            }
            catch (Exception exc)
            {
                Console.WriteLine($"Exception on ModuleConstructor: {exc}");
            }

            long count = 0;                          // Number of successfully decrypted strings
            MethodDef decrypterMethod = null;        // Decrypter method info (for later removal)
            EmbeddedResource encryptedResource = null; // Encrypted resource info (for later removal)
            bool FirstTime = true;                   // Flag to print only the first exception

            try
            {
                // Apply Harmony patch: Intercept StackFrame.GetMethod() calls
                // This allows bypassing .NET Reactor's caller verification
                StacktracePatcher.Patch();
            }
            catch (Exception exc)
            {
                Console.WriteLine($"Exception on StringDec - Harmony: {exc}");
            }

            // Iterate through all types and methods to find string decryption patterns
            foreach (var type in Context.Module.GetTypes())
                foreach (var method in (from x in type.Methods where x.HasBody && x.Body.HasInstructions select x).ToArray())
                    for (var i = 0; i < method.Body.Instructions.Count; i++)
                        try
                        {
                            // Detect string decryption pattern: ldc.i4 (load integer) + call (method call)
                            // Example: ldc.i4 838 (string index) + call GQb61Dp8v (decryption method)
                            if (!method.Body.Instructions[i].IsLdcI4() ||
                                !method.Body.Instructions[i + 1].OpCode.Equals(OpCodes.Call))
                                continue;

                            // Extract information about the called method
                            var methodDef = ((IMethod)method.Body.Instructions[i + 1].Operand).ResolveMethodDef();
                            if (methodDef == null) continue;

                            // Check if the method has a return type (string decryption methods must return string)
                            if (!methodDef.HasReturnType)
                                continue;

                            // Exclude method calls within the same class (prevent self-invocation)
                            if (TypeEqualityComparer.Instance.Equals(method.DeclaringType, methodDef.DeclaringType))
                                continue;

                            // Validate return type: Must be System.String or System.Object under specific conditions
                            if (methodDef.ReturnType.FullName != "System.String" &&
                                !(methodDef.DeclaringType != null &&
                                    methodDef.DeclaringType == type &&
                                    methodDef.ReturnType.FullName == "System.Object"))
                                continue;

                            // Parameter validation: Must accept exactly one int parameter
                            // String decryption methods typically have the signature: string DecryptString(int index)
                            if (!methodDef.HasParams() || methodDef.Parameters.Count != 1 ||
                                methodDef.Parameters[0].Type.FullName != "System.Int32")
                                continue;

                            // Check if the method internally calls GetManifestResourceStream
                            // .NET Reactor stores encrypted string data in resources
                            if (!methodDef.Body.Instructions.Any(x =>
                                    x.OpCode.Equals(OpCodes.Callvirt) && x.Operand.ToString()!
                                        .Contains("System.Reflection.Assembly::GetManifestResourceStream")))
                                continue;

                            // Extract the resource name used within the method
                            var resourceName = DotNetUtils.GetCodeStrings(methodDef)
                                .FirstOrDefault(name =>
                                    Context.Assembly.GetManifestResourceNames().Any(x => x == name));

                            if (resourceName == null)
                                continue;

                            // Perform actual string decryption
                            // 1. Resolve the decryption method using reflection
                            var resolvedMethod = Context.Assembly.ManifestModule.ResolveMethod(
                                (int)methodDef.ResolveMethodDef().MDToken.Raw) as MethodInfo;

                            // 2. Set MethodToReplace for Harmony patching
                            //    This ensures that when StackFrame.GetMethod() is called within the decryption method,
                            //    our specified method is returned, bypassing caller verification
                            StacktracePatcher.PatchStackTraceGetMethod.MethodToReplace = resolvedMethod;

                            // 3. Invoke the decryption method (pass string index as parameter)
                            var result = resolvedMethod.Invoke(null, new object[] { method.Body.Instructions[i].GetLdcI4Value() });

                            // 4. Verify that the result is a string
                            if (result is not string operand)
                                continue;

                            // 5. Store information for cleanup (for later removal of decryption method and resources)
                            decrypterMethod ??= methodDef;
                            if (encryptedResource == null &&
                                DotNetUtils.GetResource(Context.Module, resourceName) is EmbeddedResource resource)
                                encryptedResource = resource;

                            // 6. Replace IL code: Change complex decryption calls to simple string loads
                            //    Example: ldc.i4 838 + call GQb61Dp8v ¡æ nop + ldstr "decrypted string"
                            method.Body.Instructions[i].OpCode = OpCodes.Nop;           // Remove index load
                            method.Body.Instructions[i + 1].OpCode = OpCodes.Ldstr;     // Direct string load
                            method.Body.Instructions[i + 1].Operand = operand;          // Set decrypted string

                            count++; // Increment success counter
                        }
                        catch (Exception exc)
                        {
                            // Print only the first exception to prevent log spam
                            if (FirstTime)
                            {
                                Console.WriteLine($"Exception on StringDec: {exc}");
                                FirstTime = false;
                            }
                        }

            // Cleanup: Add decryption-related code that is no longer needed to removal targets
            if (decrypterMethod != null)
                Cleaner.AddMethodToBeRemoved(decrypterMethod);
            if (encryptedResource != null)
                Cleaner.AddResourceToBeRemoved(encryptedResource);

            return count; // Return the number of successfully decrypted strings
        }


        private IContext Context { get; set; }
        private byte[] _key, _iv, _decryptedResource;
        private EncryptedResource _encryptedResource;
        private readonly Dictionary<MethodDef, string> _decrypterMethods = new();
        private StringDecrypterVersion _stringDecrypterVersion;

        private enum StringDecrypterVersion { V37, V38 }

        #region Nested Types
        public class StacktracePatcher
        {
            public static void Patch()
            {
                try
                {
                    if (harmony != null)
                    {
                        Console.WriteLine("Harmony already patched");
                        return;
                    }
                    harmony = new Harmony(HarmonyId);
                    // Use PatchAll to apply annotation-based patches
                    harmony.PatchAll(Assembly.GetExecutingAssembly());
                    Console.WriteLine($"PatchAll completed. Patched methods: {harmony.GetPatchedMethods().Count()}");

                    // Verify patch application
                    var stackFrameGetMethod = typeof(StackFrame).GetMethod("GetMethod", Type.EmptyTypes);
                    var patchInfo = Harmony.GetPatchInfo(stackFrameGetMethod);
                    if (patchInfo != null)
                    {
                        Console.WriteLine($"StackFrame.GetMethod patches - Prefixes: {patchInfo.Prefixes.Count}, Postfixes: {patchInfo.Postfixes.Count}");
                    }
                    else
                    {
                        Console.WriteLine("ERROR: StackFrame.GetMethod is NOT patched!");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Patch Exception: {ex}");
                }
            }

            private const string HarmonyId = "netreactorslayer.stringdecrypter";
            private static Harmony harmony;

            /// <summary>
            /// Intercepts StackFrame.GetMethod() calls to bypass .NET Reactor's caller verification
            /// </summary>
            [HarmonyPatch(typeof(StackFrame), "GetMethod")]
            public class PatchStackTraceGetMethod
            {
                public static void Postfix(ref MethodBase __result)
                {
                    //Console.WriteLine($"[POSTFIX] Original result: {__result?.Name}");
                    //Console.WriteLine($"[POSTFIX] DeclaringType: {__result?.DeclaringType?.Name ?? "null"}");

                    // More comprehensive conditions (considering modern .NET environments)
                    bool shouldReplace =
                        __result?.DeclaringType == typeof(RuntimeMethodHandle) ||        // Original condition
                        __result?.Name?.StartsWith("InvokeStub_") == true ||            // .NET Core/5+ pattern
                        (__result?.DeclaringType == null && MethodToReplace != null);    // Handle null DeclaringType

                    if (!shouldReplace)
                    {
                        //Console.WriteLine($"[POSTFIX] No replacement needed");
                        return;
                    }

                    if (MethodToReplace != null)
                    {
                        //Console.WriteLine($"[POSTFIX] Replacing {__result?.Name} with {MethodToReplace.Name}");
                        __result = MethodToReplace;
                    }
                }

                /// <summary>
                /// The method that will replace the original method in StackFrame.GetMethod() calls
                /// This is used to trick .NET Reactor into thinking the call is coming from an authorized method
                /// </summary>
                public static MethodInfo MethodToReplace;
            }
        }

        #endregion
    }
}