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
using NETReactorSlayer.Core.Abstractions;
using NETReactorSlayer.Core.Helper;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NETReactorSlayer.Core.Stages
{
    internal class ResourceResolver : IStage
    {
        public void Run(IContext context)
        {
            Context = context;

            try
            {
                if (!Find())
                {
                    Context.Logger.Warn("Couldn't find any encrypted resource.");
                    return;
                }

                using (_encryptedResource)
                {
                    ProcessEncryptedResource();
                }
            }
            catch (Exception ex)
            {
                Context.Logger.Error($"An unexpected error occurred during decrypting resources. {ex.Message}.");
            }
        }

        /// <summary>
        /// Processes encrypted resources. Handles VM (Code Virtualization) environments safely.
        /// </summary>
        private void ProcessEncryptedResource()
        {
            try
            {
                // Decrypt and decompress the resource
                var rawDecrypted = _encryptedResource.Decrypt();
                if (rawDecrypted == null)
                {
                    Context.Logger.Warn("Resource decryption returned null data.");
                    return;
                }
                var decompressedData = TryDecompress(rawDecrypted) ?? rawDecrypted;

                // Verify and process if it's valid .NET resource data
                if (IsValidResourceData(decompressedData))
                {
                    DeobUtils.DecryptAndAddResources(Context.Module, () => decompressedData);
                    Context.Logger.Info("Assembly resources decrypted");
                }
                else
                {
                    Context.Logger.Info("[ResourceResolver] VM/Non-standard resource data detected - skipping normal processing");
                }
                CleanupObfuscatorArtifacts();
            }
            catch (Exception ex)
            {
                // Resource processing failure may be normal in VM environments
                if (IsVMRelatedError(ex))
                {
                    Context.Logger.Warn($"Resource processing failed (VM environment): {ex.Message}");
                    CleanupObfuscatorArtifacts();
                }
                else throw;
            }
        }

        /// <summary>
        /// Attempts to decompress data using various methods.
        /// </summary>
        private static byte[] TryDecompress(byte[] data)
        {
            // Try decompression in order: QuickLZ, Deflate (with/without header), then offset attempts
            try { return QuickLz.Decompress(data); } catch { }
            try { return DeobUtils.Inflate(data, false); } catch { }
            try { return DeobUtils.Inflate(data, true); } catch { }

            // Common pattern in VM: Try decompression from different starting points
            // Sometimes offset 9 works. Surely it's not because it's .NET 9, right?
            for (int offset = 1; offset < Math.Min(data.Length, 20); offset++)
            {
                try { return DeobUtils.Inflate(data.Skip(offset).ToArray(), true); } catch { }
            }
            return null;
        }

        /// <summary>
        /// Checks if the data is valid .NET assembly resource data.
        /// </summary>
        private static bool IsValidResourceData(byte[] data) =>
            data != null && data.Length >= 64 &&
            ((data.Length > 2 && data[0] == 0x4D && data[1] == 0x5A) || data.Length >= 1000);

        /// <summary>
        /// Checks if the exception is VM-related.
        /// </summary>
        private static bool IsVMRelatedError(Exception ex) =>
            ex.Message.Contains("Invalid DOS signature") ||
            ex.Message.Contains("decryptedResourceData is null") ||
            ex.Message.Contains("Not a valid PE file") ||
            ex is BadImageFormatException;

        /// <summary>
        /// Cleans up obfuscation-related artifacts.
        /// </summary>
        private void CleanupObfuscatorArtifacts()
        {
            try
            {
                _methodToRemove.ForEach(method => Cleaner.AddCallToBeRemoved(method.ResolveMethodDef()));
                Cleaner.AddCallToBeRemoved(_encryptedResource.DecrypterMethod);
                Cleaner.AddTypeToBeRemoved(_encryptedResource.DecrypterMethod.DeclaringType);
                Cleaner.AddResourceToBeRemoved(_encryptedResource.EmbeddedResource);
            }
            catch (Exception ex)
            {
                Context.Logger.Warn($"Cleanup failed: {ex.Message}");
            }
        }

        private bool Find()
        {
            foreach (var type in Context.Module.GetTypes())
            {
                if (type.BaseType?.FullName != "System.Object" || !CheckFields(type.Fields))
                    continue;

                foreach (var decrypterMethod in from method in type.Methods
                                                where method.IsStatic && method.HasBody && method.Body.ExceptionHandlers.Count == 0
                                                where DotNetUtils.IsMethod(method, "System.Reflection.Assembly", "(System.Object,System.ResolveEventArgs)") ||
                                                      DotNetUtils.IsMethod(method, "System.Reflection.Assembly", "(System.Object,System.Object)")
                                                select GetDecrypterMethod(method, Array.Empty<string>(), true) ??
                                                       GetDecrypterMethod(method, Array.Empty<string>(), false)
                         into decrypterMethod
                                                where decrypterMethod != null
                                                select decrypterMethod)
                {
                    _encryptedResource = new EncryptedResource(Context, decrypterMethod);
                    if (_encryptedResource.EmbeddedResource == null)
                    {
                        _encryptedResource.Dispose();
                        continue;
                    }

                    _methodToRemove.AddRange(type.Methods);
                    return true;
                }
            }
            return false;
        }

        private static bool CheckFields(ICollection<FieldDef> fields)
        {
            if (fields.Count != 3 && fields.Count != 4) return false;
            var fieldTypes = new FieldTypes(fields);
            var numBools = fields.Count == 3 ? 1 : 2;

            return fieldTypes.Count("System.Boolean") == numBools &&
                   (fieldTypes.Count("System.Object") == 2 ||
                    (fieldTypes.Count("System.String[]") == 1 &&
                     (fieldTypes.Count("System.Reflection.Assembly") == 1 || fieldTypes.Count("System.Object") == 1)));
        }

        private MethodDef GetDecrypterMethod(MethodDef method, IList<string> additionalTypes, bool checkResource) =>
            EncryptedResource.IsKnownDecrypter(method, additionalTypes, checkResource) ? method :
            DotNetUtils.GetCalledMethods(Context.Module, method)
                .Where(calledMethod => DotNetUtils.IsMethod(calledMethod, "System.Void", "()"))
                .FirstOrDefault(calledMethod => EncryptedResource.IsKnownDecrypter(calledMethod, additionalTypes, checkResource));

        private static byte[] Decompress(byte[] bytes)
        {
            try { return QuickLz.Decompress(bytes); }
            catch { try { return DeobUtils.Inflate(bytes, true); } catch { return null; } }
        }

        private IContext Context { get; set; }
        private EncryptedResource _encryptedResource;
        private readonly List<MethodDef> _methodToRemove = new();
    }
}