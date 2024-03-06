/*
 * DLLFromMemory.Net
 *
 * Load a native DLL from memory without the need to allow unsafe code
 *
 * Copyright (C) 2018 - 2019 by Bernhard Schelling
 *
 * Based on Memory Module.net 0.2
 * Copyright (C) 2012 - 2018 by Andreas Kanzler (andi_kanzler(at)gmx.de)
 * https://github.com/Scavanger/MemoryModule.net
 *
 * Based on Memory DLL loading code Version 0.0.4
 * Copyright (C) 2004 - 2015 by Joachim Bauch (mail(at)joachim-bauch.de)
 * https://github.com/fancycode/MemoryModule
 *
 *
 * The contents of this file are subject to the Mozilla Public License Version
 * 2.0 (the "License"); you may not use this file except in compliance with
 * the License. You may obtain a copy of the License at
 * http://www.mozilla.org/MPL/
 *
 * Software distributed under the License is distributed on an "AS IS" basis,
 * WITHOUT WARRANTY OF ANY KIND, either express or implied. See the License
 * for the specific language governing rights and limitations under the
 * License.
 *
 * The Original Code is MemoryModule.c
 *
 * The Initial Developer of the Original Code is Joachim Bauch.
 *
 * Portions created by Joachim Bauch are Copyright (C) 2004 - 2015
 * Joachim Bauch. All Rights Reserved.
 *
 * Portions created by Andreas Kanzler are Copyright (C) 2012 - 2018
 * Andreas Kanzler. All Rights Reserved.
 *
 * Portions created by Bernhard Schelling are Copyright (C) 2018 - 2019
 * Bernhard Schelling. All Rights Reserved.
 *
 */

using System;
using System.Runtime.InteropServices;

namespace StealthModule
{
    public partial class MemoryModule : IDisposable
    {
        void MemoryLoadLibrary(byte[] data)
        {
            if (data.Length < Marshal.SizeOf(typeof(IMAGE_DOS_HEADER)))
                throw new BadImageFormatException("DOS header too small");
            var dosHeader = data.ReadStruct<IMAGE_DOS_HEADER>(0);
            if (dosHeader.e_magic != Magic.IMAGE_DOS_SIGNATURE)
                throw new BadImageFormatException("Invalid DOS header magic");

            if (data.Length < dosHeader.e_lfanew + Marshal.SizeOf(typeof(IMAGE_NT_HEADERS)))
                throw new BadImageFormatException("NT header too small");
            var originalNtHeader = data.ReadStruct<IMAGE_NT_HEADERS>(dosHeader.e_lfanew);

            if (originalNtHeader.Signature != Magic.IMAGE_NT_SIGNATURE)
                throw new BadImageFormatException("Invalid NT header signature");
            if (originalNtHeader.FileHeader.Machine != GetMachineType())
                throw new BadImageFormatException("Machine type doesn't fit (i386 vs. AMD64)");
            if ((originalNtHeader.OptionalHeader.SectionAlignment & 1) > 0)
                throw new BadImageFormatException("Unsupported section alignment: " + originalNtHeader.OptionalHeader.SectionAlignment); //Only support multiple of 2
            if (originalNtHeader.OptionalHeader.AddressOfEntryPoint == 0)
                throw new ModuleException("Module has no entry point");

            NativeMethods.GetNativeSystemInfo(out var systemInfo);
            uint lastSectionEnd = 0;
            var ofSection = NativeMethods.IMAGE_FIRST_SECTION(dosHeader.e_lfanew, originalNtHeader.FileHeader.SizeOfOptionalHeader);
            for (var i = 0; i != originalNtHeader.FileHeader.NumberOfSections; i++, ofSection += Sz.IMAGE_SECTION_HEADER)
            {
                var section = data.ReadStruct<IMAGE_SECTION_HEADER>(ofSection);
                var endOfSection = section.VirtualAddress + (section.SizeOfRawData > 0 ? section.SizeOfRawData : originalNtHeader.OptionalHeader.SectionAlignment);
                if (endOfSection > lastSectionEnd)
                    lastSectionEnd = endOfSection;
            }

            var alignedImageSize = AlignValueUp(originalNtHeader.OptionalHeader.SizeOfImage, systemInfo.dwPageSize);
            var alignedLastSection = AlignValueUp(lastSectionEnd, systemInfo.dwPageSize);
            if (alignedImageSize != alignedLastSection)
                throw new BadImageFormatException("Wrong section alignment: image=" + alignedImageSize + ", section=" + alignedLastSection);

            var preferredBaseAddress = (Pointer)(originalNtHeader.OptionalHeader.ImageBaseLong >> (Is64BitProcess ? 0 : 32));

            moduleBase = AllocateModuleMemory(originalNtHeader, alignedImageSize, preferredBaseAddress);

            ntHeader = AllocateAndCopyHeaders(data, originalNtHeader) + dosHeader.e_lfanew;

            var addressDelta = moduleBase - preferredBaseAddress;
            if (addressDelta != Pointer.Zero)
            {
                // update relocated position
                // fixme: is those OffsetOf calls necessary?
                Marshal.OffsetOf(typeof(IMAGE_NT_HEADERS), "OptionalHeader");
                Marshal.OffsetOf(typeof(IMAGE_OPTIONAL_HEADER), "ImageBaseLong");
                var pImageBase = ntHeader + Of.IMAGE_NT_HEADERS_OptionalHeader + (Is64BitProcess ? Of64.IMAGE_OPTIONAL_HEADER_ImageBase : Of32.IMAGE_OPTIONAL_HEADER_ImageBase);
                pImageBase.Write(moduleBase);
            }

            // copy sections from DLL file block to new memory location
            CopySections(moduleBase, originalNtHeader, ntHeader, data);

            // adjust base address of imported data
            isRelocated = addressDelta == Pointer.Zero || PerformBaseRelocation(ref originalNtHeader, moduleBase, addressDelta);

            // load required dlls and adjust function table of imports
            importedModuleHandles = BuildImportTable(ref originalNtHeader, moduleBase);

            // mark memory pages depending on section headers and release sections that are marked as "discardable"
            FinalizeSections(ref originalNtHeader, moduleBase, ntHeader, systemInfo.dwPageSize);

            // TLS callbacks are executed BEFORE the main loading
            ExecuteTLS(ref originalNtHeader, moduleBase, ntHeader);

            // get entry point of loaded library
            IsDll = (originalNtHeader.FileHeader.Characteristics & Magic.IMAGE_FILE_DLL) != 0;
            if (originalNtHeader.OptionalHeader.AddressOfEntryPoint != 0)
            {
                if (IsDll)
                {
                    // notify library about attaching to process
                    var dllEntryPtr = moduleBase + originalNtHeader.OptionalHeader.AddressOfEntryPoint;
                    dllEntry = (DllEntryDelegate)Marshal.GetDelegateForFunctionPointer(dllEntryPtr, typeof(DllEntryDelegate)); // DllMain

                    isInitialized = dllEntry != null && dllEntry(moduleBase, DllReason.DLL_PROCESS_ATTACH, Pointer.Zero);
                    if (!isInitialized)
                        throw new ModuleException("Can't attach DLL to process");
                }
                else
                {
                    var exeEntryPtr = moduleBase + originalNtHeader.OptionalHeader.AddressOfEntryPoint;
                    exeEntry = (ExeEntryDelegate)Marshal.GetDelegateForFunctionPointer(exeEntryPtr, typeof(ExeEntryDelegate)); // main
                }
            }
        }

        private Pointer AllocateAndCopyHeaders(byte[] data, IMAGE_NT_HEADERS ntHeader)
        {
            // commit memory for headers
            var headers = NativeMethods.VirtualAlloc(moduleBase, ntHeader.OptionalHeader.SizeOfHeaders, AllocationType.COMMIT, MemoryProtection.READWRITE);
            if (headers == Pointer.Zero)
                throw new OutOfMemoryException("Header memory");

            // copy PE header to code
            Marshal.Copy(data, 0, headers, (int)ntHeader.OptionalHeader.SizeOfHeaders);
            return headers;
        }

        private static Pointer AllocateModuleMemory(IMAGE_NT_HEADERS OrgNTHeaders, uint alignedImageSize, Pointer oldHeader_OptionalHeader_ImageBase)
        {
            // reserve memory for image of library
            var mem = NativeMethods.VirtualAlloc(oldHeader_OptionalHeader_ImageBase, OrgNTHeaders.OptionalHeader.SizeOfImage, AllocationType.RESERVE | AllocationType.COMMIT, MemoryProtection.READWRITE);
            //pCode = Pointer.Zero; //test relocation with this

            // try to allocate memory at arbitrary position
            if (mem == Pointer.Zero)
                mem = NativeMethods.VirtualAlloc(Pointer.Zero, OrgNTHeaders.OptionalHeader.SizeOfImage, AllocationType.RESERVE | AllocationType.COMMIT, MemoryProtection.READWRITE);

            if (mem == Pointer.Zero)
                throw new OutOfMemoryException("Module memory");

            if (Is64BitProcess && mem.SpanBoundary(alignedImageSize, 32))
            {
                // Memory block may not span 4 GB (32 bit) boundaries.
                var blockedMemory = new System.Collections.Generic.List<IntPtr>();
                while (mem.SpanBoundary(alignedImageSize, 32))
                {
                    blockedMemory.Add(mem);
                    mem = NativeMethods.VirtualAlloc(Pointer.Zero, alignedImageSize, AllocationType.RESERVE | AllocationType.COMMIT, MemoryProtection.READWRITE);
                    if (mem == Pointer.Zero)
                        break;
                }
                foreach (var ptr in blockedMemory)
                    NativeMethods.VirtualFree(ptr, Pointer.Zero, AllocationType.RELEASE);
                if (mem == Pointer.Zero)
                    throw new OutOfMemoryException("Module memory block");
            }

            return mem;
        }

        private static void CopySections(Pointer moduleBase, IMAGE_NT_HEADERS ntHeadersData, Pointer ntHeadersAddress, byte[] data)
        {
            var sectionBase = NativeMethods.IMAGE_FIRST_SECTION(ntHeadersAddress, ntHeadersData.FileHeader.SizeOfOptionalHeader);
            for (var i = 0; i < ntHeadersData.FileHeader.NumberOfSections; i++, sectionBase += Sz.IMAGE_SECTION_HEADER)
            {
                var sectionHeader = sectionBase.Read<IMAGE_SECTION_HEADER>();
                if (sectionHeader.SizeOfRawData == 0)
                {
                    // section doesn't contain data in the dll itself, but may define uninitialized data
                    var align = ntHeadersData.OptionalHeader.SectionAlignment;
                    if (align > 0)
                    {
                        var dest = NativeMethods.VirtualAlloc(moduleBase + sectionHeader.VirtualAddress, align, AllocationType.COMMIT, MemoryProtection.READWRITE);
                        if (dest == Pointer.Zero)
                            throw new ModuleException("Unable to allocate memory");

                        // Always use position from file to support alignments smaller than page size (allocation above will align to page size).
                        dest = moduleBase + sectionHeader.VirtualAddress;

                        // NOTE: On 64bit systems we truncate to 32bit here but expand again later when "PhysicalAddress" is used.
                        (sectionBase + Of.IMAGE_SECTION_HEADER_PhysicalAddress).Write(unchecked((uint)(ulong)(long)dest));

                        //NativeMethods.MemSet(dest, 0, (UIntPtr)size);
                        for (var j = 0; j < align; j++)
                            Marshal.WriteByte(dest, j, 0); // inefficient but at least it doesn't use any native function
                    }
                }
                else
                {
                    // commit memory block and copy data from dll
                    var dest = NativeMethods.VirtualAlloc(moduleBase + sectionHeader.VirtualAddress, sectionHeader.SizeOfRawData, AllocationType.COMMIT, MemoryProtection.READWRITE);
                    if (dest == Pointer.Zero)
                        throw new ModuleException("Out of memory");

                    // Always use position from file to support alignments smaller than page size (allocation above will align to page size).
                    dest = moduleBase + sectionHeader.VirtualAddress;
                    Marshal.Copy(data, checked((int)sectionHeader.PointerToRawData), dest, checked((int)sectionHeader.SizeOfRawData));

                    // NOTE: On 64bit systems we truncate to 32bit here but expand again later when "PhysicalAddress" is used.
                    (sectionBase + Of.IMAGE_SECTION_HEADER_PhysicalAddress).Write(unchecked((uint)(ulong)(long)dest));
                }
            }
        }

        static bool PerformBaseRelocation(ref IMAGE_NT_HEADERS OrgNTHeaders, Pointer pCode, Pointer delta)
        {
            if (OrgNTHeaders.OptionalHeader.BaseRelocationTable.Size == 0)
                return delta == Pointer.Zero;

            for (var pRelocation = pCode + OrgNTHeaders.OptionalHeader.BaseRelocationTable.VirtualAddress; ;)
            {
                var Relocation = pRelocation.Read<IMAGE_BASE_RELOCATION>();
                if (Relocation.VirtualAdress == 0)
                    break;

                var pDest = pCode + Relocation.VirtualAdress;
                var pRelInfo = pRelocation + Sz.IMAGE_BASE_RELOCATION;
                var RelCount = (Relocation.SizeOfBlock - Sz.IMAGE_BASE_RELOCATION) / 2;
                for (uint i = 0; i != RelCount; i++, pRelInfo += sizeof(ushort))
                {
                    var relInfo = (ushort)Marshal.PtrToStructure(pRelInfo, typeof(ushort));
                    var type = (BasedRelocationType)(relInfo >> 12); // the upper 4 bits define the type of relocation
                    var offset = relInfo & 0xfff; // the lower 12 bits define the offset
                    var pPatchAddr = pDest + offset;

                    switch (type)
                    {
                        case BasedRelocationType.IMAGE_REL_BASED_ABSOLUTE:
                            // skip relocation
                            break;
                        case BasedRelocationType.IMAGE_REL_BASED_HIGHLOW:
                            // change complete 32 bit address
                            var patchAddrHL = (int)Marshal.PtrToStructure(pPatchAddr, typeof(int));
                            patchAddrHL += (int)delta;
                            Marshal.StructureToPtr(patchAddrHL, pPatchAddr, false);
                            break;
                        case BasedRelocationType.IMAGE_REL_BASED_DIR64:
                            var patchAddr64 = (long)Marshal.PtrToStructure(pPatchAddr, typeof(long));
                            patchAddr64 += (long)delta;
                            Marshal.StructureToPtr(patchAddr64, pPatchAddr, false);
                            break;
                    }
                }

                // advance to next relocation block
                pRelocation += Relocation.SizeOfBlock;
            }
            return true;
        }

        static Pointer[] BuildImportTable(ref IMAGE_NT_HEADERS OrgNTHeaders, Pointer pCode)
        {
            var ImportModules = new System.Collections.Generic.List<Pointer>();
            var NumEntries = OrgNTHeaders.OptionalHeader.ImportTable.Size / Sz.IMAGE_IMPORT_DESCRIPTOR;
            var pImportDesc = pCode + OrgNTHeaders.OptionalHeader.ImportTable.VirtualAddress;
            for (uint i = 0; i != NumEntries; i++, pImportDesc += Sz.IMAGE_IMPORT_DESCRIPTOR)
            {
                var ImportDesc = pImportDesc.Read<IMAGE_IMPORT_DESCRIPTOR>();
                if (ImportDesc.Name == 0)
                    break;

                var handle = NativeMethods.LoadLibrary(pCode + ImportDesc.Name);
                if (handle.IsInvalidHandle())
                {
                    foreach (var m in ImportModules)
                        NativeMethods.FreeLibrary(m);
                    ImportModules.Clear();
                    throw new ModuleException("Can't load libary " + Marshal.PtrToStringAnsi(pCode + ImportDesc.Name));
                }
                ImportModules.Add(handle);

                Pointer pThunkRef, pFuncRef;
                if (ImportDesc.OriginalFirstThunk > 0)
                {
                    pThunkRef = pCode + ImportDesc.OriginalFirstThunk;
                    pFuncRef = pCode + ImportDesc.FirstThunk;
                }
                else
                {
                    // no hint table
                    pThunkRef = pCode + ImportDesc.FirstThunk;
                    pFuncRef = pCode + ImportDesc.FirstThunk;
                }

                for (var SzRef = IntPtr.Size; ; pThunkRef += SzRef, pFuncRef += SzRef)
                {
                    Pointer ReadThunkRef = pThunkRef.ReadPointer(), WriteFuncRef;
                    if (ReadThunkRef == Pointer.Zero)
                        break;

                    if (NativeMethods.IMAGE_SNAP_BY_ORDINAL(ReadThunkRef))
                        WriteFuncRef = NativeMethods.GetProcAddress(handle, NativeMethods.IMAGE_ORDINAL(ReadThunkRef));
                    else
                        WriteFuncRef = NativeMethods.GetProcAddress(handle, pCode + ReadThunkRef + Of.IMAGE_IMPORT_BY_NAME_Name);

                    if (WriteFuncRef == Pointer.Zero)
                        throw new ModuleException("Can't get address for imported function");

                    pFuncRef.Write(WriteFuncRef);
                }
            }
            return ImportModules.Count > 0 ? ImportModules.ToArray() : null;
        }

        static void FinalizeSections(ref IMAGE_NT_HEADERS OrgNTHeaders, IntPtr pCode, IntPtr pNTHeaders, uint PageSize)
        {
            var imageOffset = Is64BitProcess ? (unchecked((ulong)pCode.ToInt64()) & 0xffffffff00000000) : Pointer.Zero;
            var pSection = NativeMethods.IMAGE_FIRST_SECTION(pNTHeaders, OrgNTHeaders.FileHeader.SizeOfOptionalHeader);
            var Section = pSection.Read<IMAGE_SECTION_HEADER>();
            var sectionData = new SectionFinalizeData();
            sectionData.Address = Section.PhysicalAddress | imageOffset;
            sectionData.AlignedAddress = sectionData.Address.AlignDown((UIntPtr)PageSize);
            sectionData.Size = GetRealSectionSize(ref Section, ref OrgNTHeaders);
            sectionData.Characteristics = Section.Characteristics;
            sectionData.Last = false;
            pSection += Sz.IMAGE_SECTION_HEADER;

            // loop through all sections and change access flags
            for (var i = 1; i < OrgNTHeaders.FileHeader.NumberOfSections; i++, pSection += Sz.IMAGE_SECTION_HEADER)
            {
                Section = pSection.Read<IMAGE_SECTION_HEADER>();
                var sectionAddress = Section.PhysicalAddress | imageOffset;
                var alignedAddress = sectionAddress.AlignDown((UIntPtr)PageSize);
                var sectionSize = GetRealSectionSize(ref Section, ref OrgNTHeaders);

                // Combine access flags of all sections that share a page
                // TODO(fancycode): We currently share flags of a trailing large section with the page of a first small section. This should be optimized.
                var a = sectionData.Address + sectionData.Size;
                ulong b = (ulong)a, c = unchecked((ulong)alignedAddress);

                if (sectionData.AlignedAddress == alignedAddress || (ulong)(sectionData.Address + sectionData.Size) > unchecked((ulong)alignedAddress))
                {
                    // Section shares page with previous
                    if ((Section.Characteristics & Magic.IMAGE_SCN_MEM_DISCARDABLE) == 0 || (sectionData.Characteristics & Magic.IMAGE_SCN_MEM_DISCARDABLE) == 0)
                        sectionData.Characteristics = (sectionData.Characteristics | Section.Characteristics) & ~Magic.IMAGE_SCN_MEM_DISCARDABLE;
                    else
                        sectionData.Characteristics |= Section.Characteristics;

                    sectionData.Size = sectionAddress + sectionSize - sectionData.Address;
                    continue;
                }

                FinalizeSection(sectionData, PageSize, OrgNTHeaders.OptionalHeader.SectionAlignment);

                sectionData.Address = sectionAddress;
                sectionData.AlignedAddress = alignedAddress;
                sectionData.Size = sectionSize;
                sectionData.Characteristics = Section.Characteristics;
            }
            sectionData.Last = true;
            FinalizeSection(sectionData, PageSize, OrgNTHeaders.OptionalHeader.SectionAlignment);
        }

        static void FinalizeSection(SectionFinalizeData SectionData, uint PageSize, uint SectionAlignment)
        {
            if (SectionData.Size == Pointer.Zero)
                return;

            if ((SectionData.Characteristics & Magic.IMAGE_SCN_MEM_DISCARDABLE) > 0)
            {
                // section is not needed any more and can safely be freed
                if (SectionData.Address == SectionData.AlignedAddress && (SectionData.Last || SectionAlignment == PageSize || (ulong)SectionData.Size % PageSize == 0))
                {
                    // Only allowed to decommit whole pages
                    NativeMethods.VirtualFree(SectionData.Address, SectionData.Size, AllocationType.DECOMMIT);
                }
                return;
            }

            // determine protection flags based on characteristics
            var readable = (SectionData.Characteristics & (uint)ImageSectionFlags.IMAGE_SCN_MEM_READ) != 0 ? 1 : 0;
            var writeable = (SectionData.Characteristics & (uint)ImageSectionFlags.IMAGE_SCN_MEM_WRITE) != 0 ? 1 : 0;
            var executable = (SectionData.Characteristics & (uint)ImageSectionFlags.IMAGE_SCN_MEM_EXECUTE) != 0 ? 1 : 0;
            var protect = (uint)ProtectionFlags[executable, readable, writeable];
            if ((SectionData.Characteristics & Magic.IMAGE_SCN_MEM_NOT_CACHED) > 0)
                protect |= Magic.PAGE_NOCACHE;

            // change memory access flags
            if (!NativeMethods.VirtualProtect(SectionData.Address, SectionData.Size, protect, out var oldProtect))
                throw new ModuleException("Error protecting memory page");
        }

        static void ExecuteTLS(ref IMAGE_NT_HEADERS OrgNTHeaders, Pointer pCode, Pointer pNTHeaders)
        {
            if (OrgNTHeaders.OptionalHeader.TLSTable.VirtualAddress == 0)
                return;
            var tlsDir = (pCode + OrgNTHeaders.OptionalHeader.TLSTable.VirtualAddress).Read<IMAGE_TLS_DIRECTORY>();
            var pCallBack = (Pointer)tlsDir.AddressOfCallBacks;
            if (pCallBack != Pointer.Zero)
            {
                for (Pointer Callback; (Callback = pCallBack.ReadPointer()) != Pointer.Zero; pCallBack += Pointer.Size)
                {
                    var tls = (ImageTlsDelegate)Marshal.GetDelegateForFunctionPointer(Callback, typeof(ImageTlsDelegate));
                    tls(pCode, DllReason.DLL_PROCESS_ATTACH, Pointer.Zero);
                }
            }
        }

        static IntPtr GetRealSectionSize(ref IMAGE_SECTION_HEADER Section, ref IMAGE_NT_HEADERS NTHeaders)
        {
            var size = Section.SizeOfRawData;
            if (size == 0)
            {
                if ((Section.Characteristics & Magic.IMAGE_SCN_CNT_INITIALIZED_DATA) > 0)
                    size = NTHeaders.OptionalHeader.SizeOfInitializedData;
                else if ((Section.Characteristics & Magic.IMAGE_SCN_CNT_UNINITIALIZED_DATA) > 0)
                    size = NTHeaders.OptionalHeader.SizeOfUninitializedData;
            }
            return IntPtr.Size == 8 ? (IntPtr)unchecked((long)size) : (IntPtr)unchecked((int)size);
        }
    }
}
