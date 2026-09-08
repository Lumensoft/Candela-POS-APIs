using System;
using System.Runtime.InteropServices;

namespace CandelaPOS.Infrastructure
{
    /// <summary>
    /// Sends raw ESC/POS bytes to a Windows-managed printer via winspool.drv,
    /// bypassing the print driver. Works for local USB printers and UNC shared
    /// printers (\\server\PrinterName).
    /// </summary>
    internal static class RawPrinterHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private class DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPStr)] public string pDataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterA",    SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", EntryPoint = "ClosePrinter",    SetLastError = true, ExactSpelling = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFOA di);

        [DllImport("winspool.drv", EntryPoint = "EndDocPrinter",   SetLastError = true, ExactSpelling = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "EndPagePrinter",  SetLastError = true, ExactSpelling = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "WritePrinter",    SetLastError = true, ExactSpelling = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        public static void SendBytesToPrinter(string printerName, byte[] bytes)
        {
            IntPtr hPrinter;
            if (!OpenPrinter(printerName.TrimEnd('\0'), out hPrinter, IntPtr.Zero))
                throw new InvalidOperationException(
                    $"Cannot open printer '{printerName}'. Win32 error: {Marshal.GetLastWin32Error()}");

            try
            {
                var di = new DOCINFOA { pDocName = "POS Receipt", pDataType = "RAW" };
                if (!StartDocPrinter(hPrinter, 1, di))
                    throw new InvalidOperationException(
                        $"StartDocPrinter failed. Win32 error: {Marshal.GetLastWin32Error()}");
                try
                {
                    StartPagePrinter(hPrinter);
                    try
                    {
                        IntPtr pBytes = Marshal.AllocCoTaskMem(bytes.Length);
                        try
                        {
                            Marshal.Copy(bytes, 0, pBytes, bytes.Length);
                            int written;
                            if (!WritePrinter(hPrinter, pBytes, bytes.Length, out written))
                                throw new InvalidOperationException(
                                    $"WritePrinter failed. Win32 error: {Marshal.GetLastWin32Error()}");
                        }
                        finally { Marshal.FreeCoTaskMem(pBytes); }
                    }
                    finally { EndPagePrinter(hPrinter); }
                }
                finally { EndDocPrinter(hPrinter); }
            }
            finally { ClosePrinter(hPrinter); }
        }
    }
}
