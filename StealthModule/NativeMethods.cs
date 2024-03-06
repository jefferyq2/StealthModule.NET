﻿using System;
using System.Runtime.InteropServices;

namespace StealthModule
{
    internal partial class NativeMethods
    {
        private delegate IntPtr DLoadLibrary(IntPtr lpFileName);
        private delegate bool DFreeLibrary(IntPtr hModule);
        private delegate IntPtr DVirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, AllocationType flAllocationType, MemoryProtection flProtect);
        private delegate bool DVirtualFree(IntPtr lpAddress, IntPtr dwSize, AllocationType dwFreeType);
        private delegate bool DVirtualProtect(IntPtr lpAddress, IntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);
        private delegate void DGetNativeSystemInfo(out SYSTEM_INFO lpSystemInfo);
        private delegate IntPtr DGetProcAddress(IntPtr hModule, IntPtr procName);

        static bool nativeInitialized;
        private static DLoadLibrary loadLibrary;
        private static DFreeLibrary freeLibrary;
        private static DVirtualAlloc virtualAlloc;
        private static DVirtualFree virtualFree;
        private static DVirtualProtect virtualProtect;
        private static DGetNativeSystemInfo getNativeSystemInfo;
        private static DGetProcAddress getProcAddress;

        internal static Pointer LoadLibrary(Pointer lpFileName)
        {
            if (loadLibrary == null)
                InitNatives();

            return loadLibrary(lpFileName);
        }

        internal static bool FreeLibrary(Pointer hModule)
        {
            if (freeLibrary == null)
                InitNatives();

            return freeLibrary(hModule);
        }

        internal static Pointer VirtualAlloc(Pointer lpAddress, Pointer dwSize, AllocationType flAllocationType, MemoryProtection flProtect)
        {
            if (virtualAlloc == null)
                InitNatives();

            return virtualAlloc(lpAddress, dwSize, flAllocationType, flProtect);
        }

        internal static bool VirtualFree(Pointer lpAddress, Pointer dwSize, AllocationType dwFreeType)
        {
            if (virtualFree == null)
                InitNatives();

            return virtualFree(lpAddress, dwSize, dwFreeType);
        }

        internal static bool VirtualProtect(Pointer lpAddress, Pointer dwSize, uint flNewProtect, out uint lpflOldProtect)
        {
            if (virtualProtect == null)
                InitNatives();

            return virtualProtect(lpAddress, dwSize, flNewProtect, out lpflOldProtect);
        }

        internal static void GetNativeSystemInfo(out SYSTEM_INFO lpSystemInfo)
        {
            if (getNativeSystemInfo == null)
                InitNatives();

            getNativeSystemInfo(out lpSystemInfo);
        }

        internal static Pointer GetProcAddress(Pointer hModule, Pointer procName)
        {
            if (getProcAddress == null)
                InitNatives();

            return getProcAddress(hModule, procName);
        }

        internal static void InitNatives()
        {
            if (nativeInitialized)
                return;

            var exports = new string[] {
                 "LoadLibraryA",
                 "FreeLibrary",
                 "VirtualAlloc",
                 "VirtualFree",
                 "VirtualProtect",
                 "GetNativeSystemInfo",
                 "GetProcAddress",
            };

            var addresses = ExportResolver.ResolveExports("kernel32.dll", exports, true);
            loadLibrary = Marshal.GetDelegateForFunctionPointer<DLoadLibrary>(addresses[0]);
            freeLibrary = Marshal.GetDelegateForFunctionPointer<DFreeLibrary>(addresses[1]);
            virtualAlloc = Marshal.GetDelegateForFunctionPointer<DVirtualAlloc>(addresses[2]);
            virtualFree = Marshal.GetDelegateForFunctionPointer<DVirtualFree>(addresses[3]);
            virtualProtect = Marshal.GetDelegateForFunctionPointer<DVirtualProtect>(addresses[4]);
            getNativeSystemInfo = Marshal.GetDelegateForFunctionPointer<DGetNativeSystemInfo>(addresses[5]);
            getProcAddress = Marshal.GetDelegateForFunctionPointer<DGetProcAddress>(addresses[6]);
            nativeInitialized = true;
        }
    }
}
